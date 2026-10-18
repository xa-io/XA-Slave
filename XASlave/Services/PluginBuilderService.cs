using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

/// <summary>Owns explicit user-selected bindings. Draw only queues work; target execution belongs to Framework.Update.</summary>
internal sealed class PluginBuilderService : IDisposable
{
    private const int MaxSources = 4096;
    private const int MaxWindows = 32;
    private const int MaxPages = 32;
    private const int MaxControls = 64;
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private readonly Plugin plugin;
    private readonly Dictionary<string, LoadedCatalog> catalogs = new(StringComparer.Ordinal);
    private readonly Dictionary<(Guid Window, Guid Control), PluginBuilderSample> samples = new();
    private readonly Dictionary<(Guid Window, Guid Control), DateTime> nextReads = new();
    private readonly HashSet<(Guid Window, Guid Control)> suspended = new();
    private readonly Dictionary<(Guid Window, Guid Control), PluginBuilderControlDefinition> savedControls = new();
    private readonly HashSet<Guid> openWindows = new();
    private readonly Dictionary<Guid, Guid> selectedPages = new();
    private readonly Queue<Request> requests = new();
    private CancellationTokenSource? scanCancellation;
    private Task<LoadedCatalog>? scanTask;
    // Host-service lifetime only. Individual plugin events reconcile their captured instances without expiring other sources.
    private int generation;
    private int scanGeneration;
    private int invalidated;
    private bool disposed;
    private bool refreshRequested;
    private string? pendingScan;
    private ulong lastContentId;
    private DateTime nextIdentityCheck;
    public event Action? WindowsChanged;
    internal IReadOnlyList<string> PluginNames { get; private set; } = Array.Empty<string>();
    internal string Status { get; private set; } = "Select a loaded plugin and scan its available sources.";
    internal bool IsScanning => scanTask != null || pendingScan != null;
    internal PluginBuilderCatalog? Catalog { get; private set; }
    internal List<PluginBuilderWindowDefinition> Windows => plugin.Configuration.PluginBuilderWindows;

    private sealed record LoadedCatalog(PluginBuilderCatalog Public, WeakReference<object> Instance,
        Dictionary<string, PluginBuilderBinding> Bindings, int Generation, bool Partial);
    private sealed record Request(Guid WindowId, Guid ControlId, bool Action, PluginBuilderControlDefinition Definition,
        int Generation, ulong ContentId, bool Manual);

    internal PluginBuilderService(Plugin plugin)
    {
        this.plugin = plugin;
        plugin.Configuration.PluginBuilderWindows ??= new();
        NormalizeDefinitions();
        RememberDefinitions();
        Plugin.Framework.Update += OnFrameworkUpdate;
        Plugin.PluginInterface.ActivePluginsChanged += OnPluginsChanged;
        RefreshPlugins();
    }

    internal void RefreshPlugins()
    {
        if (disposed) return;
        refreshRequested = true;
    }

    private void RefreshPluginsNow()
    {
        refreshRequested = false;
        try
        {
            PluginNames = Plugin.PluginInterface.InstalledPlugins.Where(item => item.IsLoaded)
                .Select(item => item.InternalName).Append("XASlave").Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal).Take(1024).ToArray();
        }
        catch { PluginNames = Array.Empty<string>(); Status = "Loaded plugin list unavailable."; }
    }

    internal void Scan(string pluginName)
    {
        if (disposed || IsScanning || string.IsNullOrWhiteSpace(pluginName)) return;
        pendingScan = Bound(pluginName, 256);
        Status = "Scan queued.";
    }

    private void StartScanNow(string pluginName)
    {
        try
        {
            if (!PluginNames.Contains(pluginName, StringComparer.Ordinal)) throw new InvalidOperationException("Refresh the loaded plugin list.");
            var instance = PluginBuilderBindingRules.ResolveLoadedPlugin(plugin, pluginName)
                ?? throw new InvalidOperationException("The selected plugin is unavailable.");
            var weak = new WeakReference<object>(instance);
            var assembly = instance.GetType().Assembly;
            var rootType = instance.GetType();
            var moduleId = assembly.ManifestModule.ModuleVersionId.ToString("D");
            var commands = CaptureCommands(assembly, pluginName, moduleId);
            scanCancellation?.Dispose();
            scanCancellation = new();
            var token = scanCancellation.Token;
            scanGeneration = Volatile.Read(ref generation);
            var expectedGeneration = scanGeneration;
            scanTask = Task.Run(() => BuildCatalog(pluginName, moduleId, rootType, weak, commands, expectedGeneration, token), token);
            _ = scanTask.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            Status = "Scanning IPC, commands and reflection metadata...";
        }
        catch (Exception error) { Status = "Scan unavailable: " + error.GetType().Name; }
    }

    internal PluginBuilderWindowDefinition? CreateWindow(string title)
    {
        if (disposed || Windows.Count >= MaxWindows) return null;
        var definition = new PluginBuilderWindowDefinition
        {
            Title = Bound(string.IsNullOrWhiteSpace(title) ? "Plugin Window" : title, 128),
            PluginName = Catalog?.PluginName ?? string.Empty, ModuleVersionId = Catalog?.ModuleVersionId ?? string.Empty,
        };
        Windows.Add(definition);
        Save();
        return definition;
    }

    internal void Save()
    {
        if (disposed) return;
        NormalizeDefinitions();
        var live = Windows.Take(MaxWindows).SelectMany(window => window.Controls.Take(MaxControls).Select(control => (window.Id, Control: control)))
            .ToDictionary(item => (item.Id, item.Control.Id), item => item.Control);
        foreach (var pair in savedControls)
        {
            if (live.TryGetValue(pair.Key, out var current) && ControlMatches(current, pair.Value)) continue;
            samples.Remove(pair.Key); nextReads.Remove(pair.Key);
            if (current == null || !PluginBuilderBindingRules.Matches(current.Source, pair.Value.Source)) suspended.Remove(pair.Key);
        }
        var keep = requests.Where(request => live.TryGetValue((request.WindowId, request.ControlId), out var current)
            && ControlMatches(current, request.Definition)).ToArray();
        requests.Clear(); foreach (var request in keep) requests.Enqueue(request);
        RememberDefinitions();
        plugin.Configuration.SaveDeferred();
        WindowsChanged?.Invoke();
    }

    internal void SetWindowOpen(Guid windowId, bool open)
    {
        if (disposed) return;
        if (open) openWindows.Add(windowId);
        else
        {
            if (!openWindows.Remove(windowId)) return;
            var keep = requests.Where(request => request.WindowId != windowId || request.Manual).ToArray();
            requests.Clear(); foreach (var request in keep) requests.Enqueue(request);
        }
    }

    internal PluginBuilderCatalog? GetCatalog(string pluginName)
        => !disposed && Volatile.Read(ref invalidated) == 0 && !string.IsNullOrEmpty(pluginName)
            && catalogs.TryGetValue(pluginName, out var catalog) && catalog.Generation == Volatile.Read(ref generation)
                ? catalog.Public : null;

    internal bool IsAvailable(PluginBuilderSource source)
        => !disposed && Volatile.Read(ref invalidated) == 0 && !string.IsNullOrEmpty(source.PluginName)
            && !string.IsNullOrEmpty(source.Id) && catalogs.TryGetValue(source.PluginName, out var catalog)
            && catalog.Generation == Volatile.Read(ref generation) && catalog.Public.ModuleVersionId == source.ModuleVersionId
            && catalog.Bindings.TryGetValue(source.Id, out var binding) && PluginBuilderBindingRules.Matches(source, binding.Source);

    internal IReadOnlyList<string> GetWindowPluginNames(PluginBuilderWindowDefinition window)
        => (window.Controls.Count == 0 ? new[] { window.PluginName } : window.Controls.Select(control => control.Source.PluginName))
            .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();

    internal void SetWindowPage(Guid windowId, Guid pageId)
    {
        if (disposed) return;
        var window = Windows.Take(MaxWindows).FirstOrDefault(item => item.Id == windowId);
        if (window == null || !window.Pages.Take(MaxPages).Any(page => page.Id == pageId)) return;
        if (selectedPages.TryGetValue(windowId, out var currentPage) && currentPage == pageId) return;
        selectedPages[windowId] = pageId;
        var keep = requests.Where(request => request.WindowId != windowId || request.Manual || request.Definition.PageId == pageId).ToArray();
        requests.Clear(); foreach (var request in keep) requests.Enqueue(request);
    }

    private bool IsSelectedPage(PluginBuilderWindowDefinition window, PluginBuilderControlDefinition control)
        => selectedPages.TryGetValue(window.Id, out var selectedPage) && selectedPage == control.PageId;

    internal PluginBuilderSample GetSample(Guid windowId, Guid controlId)
        => samples.TryGetValue((windowId, controlId), out var sample) ? sample : new("Not read", string.Empty, null);
    internal void QueueRead(Guid windowId, Guid controlId) => Queue(windowId, controlId, action: false);
    internal void QueueAction(Guid windowId, Guid controlId) => Queue(windowId, controlId, action: true);

    private void Queue(Guid windowId, Guid controlId, bool action, bool manual = true)
    {
        if (disposed || requests.Count >= 64) return;
        var window = Windows.FirstOrDefault(item => item.Id == windowId);
        var control = window?.Controls.FirstOrDefault(item => item.Id == controlId);
        if (window == null || control == null) return;
        if (requests.Any(item => item.WindowId == windowId && item.ControlId == controlId)) return;
        requests.Enqueue(new(windowId, controlId, action, CopyControl(control), Volatile.Read(ref generation), lastContentId, manual));
        if (manual) samples[(windowId, controlId)] = new("Queued", string.Empty, null);
    }

    private void OnPluginsChanged(IActivePluginsChangedEventArgs _)
    {
        Interlocked.Exchange(ref invalidated, 1);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (disposed || framework.IsFrameworkUnloading) return;
        try
        {
            if (Interlocked.Exchange(ref invalidated, 0) != 0)
            {
                ReconcileCatalogs();
                RefreshPlugins();
            }
            if (refreshRequested) RefreshPluginsNow();
            if (pendingScan is { } queuedScan)
            {
                pendingScan = null;
                StartScanNow(queuedScan);
            }
            PublishScan();
            var contentId = Plugin.PlayerState.IsLoaded ? Plugin.PlayerState.ContentId : 0;
            if (contentId != lastContentId)
            {
                lastContentId = contentId; requests.Clear(); nextReads.Clear(); samples.Clear();
            }
            if (DateTime.UtcNow >= nextIdentityCheck)
            {
                nextIdentityCheck = DateTime.UtcNow.AddSeconds(2);
                ReconcileCatalogs();
            }
            QueueAutomaticReads();
            // One target call per frame. A synchronous target cannot be interrupted; slow/error controls stop polling.
            if (requests.Count > 0 && Volatile.Read(ref invalidated) == 0) Execute(requests.Dequeue());
        }
        catch (Exception error) { Status = "Builder update unavailable: " + error.GetType().Name; }
    }

    private void PublishScan()
    {
        if (scanTask is not { IsCompleted: true } task) return;
        scanTask = null;
        var canceled = scanCancellation?.IsCancellationRequested == true;
        scanCancellation?.Dispose(); scanCancellation = null;
        if (canceled || task.IsCanceled || scanGeneration != Volatile.Read(ref generation)) { Status = "Scan canceled by a lifecycle change."; return; }
        if (task.IsFaulted) { Status = "Scan failed: " + task.Exception?.GetBaseException().GetType().Name; return; }
        var result = task.Result;
        if (!IsCurrent(result)) { Status = "Selected plugin changed during scan. Scan again."; return; }
        // A rescan must not retarget an already queued action to newly captured providers.
        var keep = requests.Where(request => request.Definition.Source.PluginName != result.Public.PluginName).ToArray();
        requests.Clear(); foreach (var request in keep) requests.Enqueue(request);
        catalogs[result.Public.PluginName] = result;
        Catalog = result.Public;
        foreach (var window in Windows.Take(MaxWindows))
            foreach (var control in window.Controls.Take(MaxControls).Where(control => control.Source.PluginName == Catalog.PluginName))
            {
                var key = (window.Id, control.Id);
                samples.Remove(key); suspended.Remove(key); nextReads.Remove(key);
            }
        Status = $"Scanned {Catalog.PluginName}: {Catalog.Sources.Count} sources{(result.Partial ? " (partial scan)" : string.Empty)}. Select each control explicitly; functions are not assumed read-only.";
    }

    private bool IsCurrent(LoadedCatalog catalog)
    {
        if (catalog.Generation != Volatile.Read(ref generation) || !catalog.Instance.TryGetTarget(out var captured)) return false;
        try
        {
            var current = PluginBuilderBindingRules.ResolveLoadedPlugin(plugin, catalog.Public.PluginName);
            return ReferenceEquals(current, captured) && current?.GetType().Assembly.ManifestModule.ModuleVersionId.ToString("D") == catalog.Public.ModuleVersionId;
        }
        catch { return false; }
    }

    private void ReconcileCatalogs()
    {
        foreach (var pair in catalogs.ToArray())
            if (!IsCurrent(pair.Value))
                InvalidateCatalog(pair.Key);
    }

    private void InvalidateCatalog(string pluginName)
    {
        catalogs.Remove(pluginName);
        if (Catalog?.PluginName == pluginName) Catalog = null;
        var controlKeys = Windows.Take(MaxWindows).SelectMany(window => window.Controls.Take(MaxControls)
            .Where(control => control.Source.PluginName == pluginName).Select(control => (window.Id, control.Id))).ToHashSet();
        foreach (var key in samples.Keys.Where(key => controlKeys.Contains(key)).ToArray())
            samples[key] = new("Plugin unavailable", "Scan again to bind the current instance.", DateTime.UtcNow);
        foreach (var key in nextReads.Keys.Where(key => controlKeys.Contains(key)).ToArray()) nextReads.Remove(key);
        var keep = requests.Where(request => request.Definition.Source.PluginName != pluginName).ToArray();
        requests.Clear(); foreach (var request in keep) requests.Enqueue(request);
        Status = pluginName + " changed or unloaded. Scan it again before using saved controls.";
        RefreshPlugins();
    }

    private void QueueAutomaticReads()
    {
        foreach (var window in Windows.Take(MaxWindows))
        {
            if (!openWindows.Contains(window.Id)) continue;
            foreach (var control in window.Controls.Take(MaxControls))
            {
                if (control.Kind != PluginBuilderControlKind.Info || control.AutoRefreshSeconds <= 0) continue;
                if (!IsSelectedPage(window, control) || !IsAvailable(control.Source)) continue;
                var key = (window.Id, control.Id);
                if (suspended.Contains(key)) continue;
                if (nextReads.TryGetValue(key, out var next) && DateTime.UtcNow < next) continue;
                nextReads[key] = DateTime.UtcNow.AddSeconds(Math.Clamp(control.AutoRefreshSeconds, 1, 300));
                Queue(window.Id, control.Id, action: false, manual: false);
            }
        }
    }

    private void Execute(Request request)
    {
        if (!request.Manual && !openWindows.Contains(request.WindowId)) return;
        if (Volatile.Read(ref invalidated) != 0) { requests.Enqueue(request); return; }
        var key = (request.WindowId, request.ControlId);
        var clock = Stopwatch.StartNew();
        var deferred = false;
        try
        {
            if (request.Generation != Volatile.Read(ref generation)) throw new InvalidOperationException("Plugin lifecycle changed.");
            if (CurrentContentId() != request.ContentId) throw new InvalidOperationException("Character changed after this request was queued.");
            var window = Windows.FirstOrDefault(item => item.Id == request.WindowId) ?? throw new InvalidOperationException("Window was removed.");
            var current = window.Controls.FirstOrDefault(item => item.Id == request.ControlId) ?? throw new InvalidOperationException("Control was removed.");
            var control = request.Definition;
            if (!ControlMatches(current, control)) throw new InvalidOperationException("Control changed after it was queued.");
            if (!request.Manual && !IsSelectedPage(window, current)) return;
            if (!IsAvailable(control.Source) || !catalogs.TryGetValue(control.Source.PluginName, out var catalog) || !IsCurrent(catalog))
            {
                // A host event can arrive after dequeue. Retain unaffected work until its instance has been reconciled.
                if (Volatile.Read(ref invalidated) != 0) { deferred = true; requests.Enqueue(request); return; }
                throw new InvalidOperationException("Scan this plugin again before using saved controls.");
            }
            if (!catalog.Bindings.TryGetValue(control.Source.Id, out var binding) || !PluginBuilderBindingRules.Matches(control.Source, binding.Source))
                throw new InvalidOperationException("Source no longer matches its scanned contract.");
            if (request.Action ? !binding.Source.CanExecute : !binding.Source.CanRead)
                throw new InvalidOperationException(binding.Source.UnsupportedReason.Length > 0 ? binding.Source.UnsupportedReason : "Unsupported operation.");
            if (binding.Source.RequiresReflectionConfirmation && !control.ReflectionReadConfirmed)
                throw new InvalidOperationException("Explicit reflection confirmation is required for this selected source.");
            if (control.Arguments.Count != binding.Parameters.Length) throw new InvalidOperationException("Argument count does not match the source.");
            var arguments = control.Arguments.Select((argument, index) => PluginBuilderBindingRules.ParseArgument(argument, binding.Parameters[index])).ToArray();
            if (!catalog.Instance.TryGetTarget(out var instance)) throw new InvalidOperationException("Plugin instance unavailable.");
            object? value;
            switch (binding.Source.Kind)
            {
                case PluginBuilderSourceKind.IpcFunction:
                case PluginBuilderSourceKind.IpcAction:
                    value = InvokeIpc(binding, arguments, instance.GetType().Assembly);
                    break;
                case PluginBuilderSourceKind.Command:
                    if (!request.Action || arguments.Length != 1) throw new InvalidOperationException("Commands require an explicit action.");
                    if (!Plugin.CommandManager.Commands.TryGetValue(binding.Source.Channel, out var command)
                        || command.Handler.Method != binding.ProviderMethod || binding.ProviderIdentity == null
                        || !binding.ProviderIdentity.TryGetTarget(out var scannedCommand) || !ReferenceEquals(command.Handler, scannedCommand))
                        throw new InvalidOperationException("Command registration changed.");
                    var commandArguments = (string?)arguments[0] ?? string.Empty;
                    if (commandArguments.Any(character => character is '\r' or '\n' or '\0')) throw new InvalidOperationException("Command arguments must be one line.");
                    value = Plugin.CommandManager.ProcessCommand(binding.Source.Channel + (commandArguments.Length > 0 ? " " + commandArguments : string.Empty));
                    break;
                default:
                    object? target = instance;
                    foreach (var field in binding.OwnerPath)
                    {
                        target = field.GetValue(field.IsStatic ? null : target ?? throw new InvalidOperationException("A selected owner field is null."));
                    }
                    value = binding.Member switch
                    {
                        FieldInfo field => field.GetValue(field.IsStatic ? null : target),
                        PropertyInfo property => property.GetValue(property.GetMethod!.IsStatic ? null : target),
                        MethodInfo method => method.Invoke(method.IsStatic ? null : target, arguments),
                        _ => throw new InvalidOperationException("Unsupported reflection member."),
                    };
                    break;
            }
            if (request.Generation != Volatile.Read(ref generation) || !IsCurrent(catalog) || CurrentContentId() != request.ContentId)
                throw new InvalidOperationException("Plugin or character changed during the call; result discarded.");
            samples[key] = new(request.Action ? "Invoked" : "Observed",
                binding.ResultType == typeof(void) ? "Request sent." : PluginBuilderValueProjection.Format(value, control.MemberPath), DateTime.UtcNow);
            if (request.Manual && !request.Action)
            {
                suspended.Remove(key);
                nextReads[key] = DateTime.UtcNow.AddSeconds(Math.Clamp(control.AutoRefreshSeconds, 1, 300));
            }
        }
        catch (Exception error)
        {
            var reason = error is TargetInvocationException { InnerException: not null } wrapped ? wrapped.InnerException : error;
            samples[key] = new("Unavailable", Bound(reason?.Message ?? "Call failed.", 512), DateTime.UtcNow);
            nextReads[key] = DateTime.MaxValue;
            suspended.Add(key);
        }
        finally
        {
            if (!deferred && clock.ElapsedMilliseconds > 50)
            {
                nextReads[key] = DateTime.MaxValue;
                suspended.Add(key);
                Status = "A selected source took more than 50 ms. Its automatic polling is paused; manual reads remain available.";
                if (samples.TryGetValue(key, out var sampled))
                    samples[key] = sampled with { Status = "Automatic refresh paused: slow source" };
            }
        }
    }

    private static object? InvokeIpc(PluginBuilderBinding binding, object?[] arguments, Assembly assembly)
    {
        var action = binding.Source.Kind == PluginBuilderSourceKind.IpcAction;
        var current = PluginInspectionIpcRegistry.FindProvider(binding.Source.Channel, action, assembly, Plugin.PluginInterface);
        if (current == null || current.Method != binding.ProviderMethod || binding.ProviderIdentity == null
            || !binding.ProviderIdentity.TryGetTarget(out var scannedProvider) || !ReferenceEquals(current, scannedProvider))
            throw new InvalidOperationException("IPC provider registration changed.");
        var invokeShape = current.GetType().GetMethod("Invoke") ?? throw new InvalidOperationException("IPC signature unavailable.");
        if (invokeShape.ReturnType != binding.ResultType || !invokeShape.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(binding.Parameters))
            throw new InvalidOperationException("IPC signature changed.");
        var types = binding.Parameters.Append(action ? typeof(object) : binding.ResultType).ToArray();
        var factory = typeof(IDalamudPluginInterface).GetMethods().Single(method => method.Name == "GetIpcSubscriber"
            && method.IsGenericMethodDefinition && method.GetGenericArguments().Length == types.Length);
        var closedFactory = factory.MakeGenericMethod(types);
        var subscriber = closedFactory.Invoke(Plugin.PluginInterface, new object[] { binding.Source.Channel });
        if (subscriber is not ICallGateSubscriber gate || (action ? !gate.HasAction : !gate.HasFunction))
            throw new InvalidOperationException("IPC provider unavailable.");
        var call = closedFactory.ReturnType.GetMethod(action ? "InvokeAction" : "InvokeFunc", binding.Parameters)
            ?? throw new InvalidOperationException("Official subscriber signature unavailable.");
        return call.Invoke(subscriber, arguments);
    }

    private static LoadedCatalog BuildCatalog(string name, string moduleId, Type rootType, WeakReference<object> instance,
        List<PluginBuilderBinding> commands, int generation, CancellationToken token)
    {
        var assembly = rootType.Assembly;
        var bindings = new Dictionary<string, PluginBuilderBinding>(StringComparer.Ordinal);
        var registry = PluginInspectionIpcRegistry.Capture(assembly, Plugin.PluginInterface, token);
        var scanPartial = registry.Incomplete;
        var clock = Stopwatch.StartNew();
        foreach (var channel in registry.Channels)
        {
            token.ThrowIfCancellationRequested();
            if (bindings.Count >= MaxSources || clock.Elapsed > TimeSpan.FromSeconds(5)) { scanPartial = true; break; }
            if (!channel.NameComplete || !channel.AttributedToSelectedPlugin) continue;
            AddIpc(channel, action: false); AddIpc(channel, action: true);
        }
        foreach (var command in commands)
        {
            if (bindings.Count >= MaxSources) { scanPartial = true; break; }
            bindings.TryAdd(command.Source.Id, command);
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Visit(rootType, Array.Empty<FieldInfo>(), 0, staticOnly: false);
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException error) { scanPartial = true; types = error.Types.Where(type => type != null).Cast<Type>().ToArray(); }
        foreach (var type in types.Take(1000))
        {
            if (type != rootType) Visit(type, Array.Empty<FieldInfo>(), 0, staticOnly: true);
            if (bindings.Count >= MaxSources || clock.Elapsed > TimeSpan.FromSeconds(5)) break;
        }
        token.ThrowIfCancellationRequested();
        var sources = bindings.Values.Select(binding => CopySource(binding.Source)).OrderBy(source => source.Kind)
            .ThenBy(source => source.Label, StringComparer.Ordinal).ToArray();
        return new(new(name, moduleId, Array.AsReadOnly(sources)), instance, bindings, generation,
            scanPartial || bindings.Count >= MaxSources || clock.Elapsed > TimeSpan.FromSeconds(5) || types.Length > 1000);

        void AddIpc(PluginInspectionIpcChannel channel, bool action)
        {
            if (bindings.Count >= MaxSources) return;
            var metadata = action ? channel.Action : channel.Function;
            if (metadata == null || !metadata.SignatureComplete || !metadata.AttributedToSelectedPlugin) return;
            var provider = PluginInspectionIpcRegistry.FindProvider(channel.Name, action, assembly, Plugin.PluginInterface);
            var shape = provider?.GetType().GetMethod("Invoke");
            if (provider == null || shape == null) return;
            var parameters = shape.GetParameters();
            var supported = parameters.Length <= 8 && parameters.All(parameter => PluginBuilderBindingRules.SupportsArgument(parameter.ParameterType))
                && SupportedResult(shape.ReturnType);
            var source = new PluginBuilderSource
            {
                Id = (action ? "ipc-action:" : "ipc-function:") + channel.Name, Label = channel.Name,
                Kind = action ? PluginBuilderSourceKind.IpcAction : PluginBuilderSourceKind.IpcFunction,
                PluginName = name, ModuleVersionId = moduleId, Channel = channel.Name,
                ParameterTypes = parameters.Select(parameter => PluginBuilderBindingRules.TypeKey(parameter.ParameterType)).ToList(),
                ParameterNames = parameters.Select(parameter => parameter.Name ?? "argument").ToList(),
                ReturnType = PluginBuilderBindingRules.TypeKey(shape.ReturnType), CanRead = supported && !action,
                CanExecute = supported, UnsupportedReason = supported ? string.Empty : "Only up to eight scalar arguments and managed synchronous results are supported.",
            };
            bindings.TryAdd(source.Id, new(source, parameters.Select(parameter => parameter.ParameterType).ToArray(), shape.ReturnType, null, Array.Empty<FieldInfo>(), provider.Method, new(provider)));
        }

        void Visit(Type type, FieldInfo[] path, int depth, bool staticOnly)
        {
            token.ThrowIfCancellationRequested();
            if (bindings.Count >= MaxSources || clock.Elapsed > TimeSpan.FromSeconds(5) || depth > 3 || type.Assembly != assembly || type.ContainsGenericParameters) return;
            var visitKey = type.FullName + ":" + string.Join("/", path.Select(field => field.MetadataToken));
            if (!seen.Add(visitKey)) return;
            MemberInfo[] members;
            try { members = type.GetMembers(Members); } catch { return; }
            foreach (var member in members)
            {
                token.ThrowIfCancellationRequested();
                if (bindings.Count >= MaxSources || clock.Elapsed > TimeSpan.FromSeconds(5)) return;
                Type result;
                ParameterInfo[] parameters;
                bool isStatic;
                PluginBuilderSourceKind kind;
                switch (member)
                {
                    case FieldInfo field:
                        result = field.FieldType; parameters = Array.Empty<ParameterInfo>(); isStatic = field.IsStatic; kind = PluginBuilderSourceKind.ReflectionField; break;
                    case PropertyInfo property when property.GetMethod != null && property.GetIndexParameters().Length == 0:
                        result = property.PropertyType; parameters = Array.Empty<ParameterInfo>(); isStatic = property.GetMethod.IsStatic; kind = PluginBuilderSourceKind.ReflectionProperty; break;
                    case MethodInfo method when !method.IsSpecialName && !method.IsGenericMethodDefinition && !method.ContainsGenericParameters:
                        result = method.ReturnType; parameters = method.GetParameters(); isStatic = method.IsStatic; kind = PluginBuilderSourceKind.ReflectionMethod; break;
                    default: continue;
                }
                if (staticOnly && !isStatic) continue;
                var supported = parameters.Length <= 8 && parameters.All(parameter => PluginBuilderBindingRules.SupportsArgument(parameter.ParameterType))
                    && SupportedResult(result);
                var source = new PluginBuilderSource
                {
                    Id = PluginBuilderBindingRules.MemberId(member, path),
                    Label = (path.Length == 0 ? type.Name : string.Join(".", path.Select(field => field.Name))) + "." + member.Name,
                    Kind = kind, PluginName = name, ModuleVersionId = moduleId, DeclaringType = type.FullName ?? type.Name,
                    MetadataToken = member.MetadataToken, Static = isStatic, FieldPath = path.Select(field => field.Name).ToList(),
                    ParameterTypes = parameters.Select(parameter => PluginBuilderBindingRules.TypeKey(parameter.ParameterType)).ToList(),
                    ParameterNames = parameters.Select(parameter => parameter.Name ?? "argument").ToList(),
                    ReturnType = PluginBuilderBindingRules.TypeKey(result), CanRead = supported && result != typeof(void),
                    CanExecute = supported && kind == PluginBuilderSourceKind.ReflectionMethod,
                    UnsupportedReason = supported ? string.Empty : "Only scalar arguments and managed synchronous results are supported.",
                };
                bindings.TryAdd(source.Id, new(source, parameters.Select(parameter => parameter.ParameterType).ToArray(), result, member, path, null, null));
                if (member is FieldInfo child && depth < 3 && child.FieldType.Assembly == assembly
                    && child.FieldType != rootType && !path.Any(owner => owner.DeclaringType == child.FieldType)
                    && !child.FieldType.IsEnum && !child.FieldType.IsPrimitive && !typeof(Delegate).IsAssignableFrom(child.FieldType))
                    Visit(child.FieldType, path.Append(child).ToArray(), depth + 1, staticOnly: false);
            }
        }
    }

    private static List<PluginBuilderBinding> CaptureCommands(Assembly assembly, string name, string moduleId)
    {
        var result = new List<PluginBuilderBinding>();
        foreach (var entry in Plugin.CommandManager.Commands.Take(4096))
        {
            var handler = entry.Value.Handler;
            if (handler.Method.Module.Assembly != assembly && handler.Target?.GetType().Assembly != assembly) continue;
            var source = new PluginBuilderSource
            {
                Id = "command:" + entry.Key, Label = entry.Key + " " + Bound(entry.Value.HelpMessage, 180), Kind = PluginBuilderSourceKind.Command,
                PluginName = name, ModuleVersionId = moduleId, Channel = entry.Key, CanExecute = true,
                ParameterTypes = new() { PluginBuilderBindingRules.TypeKey(typeof(string)) }, ParameterNames = new() { "arguments" },
                ReturnType = PluginBuilderBindingRules.TypeKey(typeof(bool)),
            };
            result.Add(new(source, new[] { typeof(string) }, typeof(bool), null, Array.Empty<FieldInfo>(), handler.Method, new(handler)));
            if (result.Count >= 512) break;
        }
        return result;
    }

    private static bool SupportedResult(Type type) => !type.IsByRef && !type.IsByRefLike && !type.IsPointer && !type.IsFunctionPointer
        && !type.ContainsGenericParameters && !typeof(Task).IsAssignableFrom(type) && type != typeof(ValueTask)
        && !(type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>)) && !typeof(Delegate).IsAssignableFrom(type);

    private void NormalizeDefinitions()
    {
        Windows.RemoveAll(window => window == null);
        var windowIds = new HashSet<Guid>();
        foreach (var window in Windows)
        {
            if (window.Id == Guid.Empty || !windowIds.Add(window.Id)) { window.Id = Guid.NewGuid(); windowIds.Add(window.Id); }
            window.PluginName ??= string.Empty; window.ModuleVersionId ??= string.Empty;
            window.Title = Bound(window.Title, 128);
            window.CommandName ??= string.Empty;
            window.SidebarWidth = Math.Clamp(window.SidebarWidth, 100, 400);
            window.Pages ??= new();
            window.Pages.RemoveAll(page => page == null);
            if (window.Pages.Count > MaxPages) window.Pages.RemoveRange(MaxPages, window.Pages.Count - MaxPages);
            if (window.Pages.Count == 0) window.Pages.Add(new PluginBuilderPageDefinition());
            var pageIds = new HashSet<Guid>();
            foreach (var page in window.Pages)
            {
                if (page.Id == Guid.Empty || !pageIds.Add(page.Id)) { page.Id = Guid.NewGuid(); pageIds.Add(page.Id); }
                page.Title = Bound(string.IsNullOrWhiteSpace(page.Title) ? "Overview" : page.Title, 128);
            }
            if (selectedPages.TryGetValue(window.Id, out var selectedPage) && !pageIds.Contains(selectedPage))
                selectedPages.Remove(window.Id);
            window.Controls ??= new();
            window.Controls.RemoveAll(control => control == null);
            var controlIds = new HashSet<Guid>();
            foreach (var control in window.Controls)
            {
                if (control.Id == Guid.Empty || !controlIds.Add(control.Id)) { control.Id = Guid.NewGuid(); controlIds.Add(control.Id); }
                if (!pageIds.Contains(control.PageId)) control.PageId = window.Pages[0].Id;
                control.Label = Bound(control.Label, 128); control.MemberPath = Bound(control.MemberPath, 512);
                control.Arguments ??= new(); control.Source ??= new();
                control.Arguments.RemoveAll(argument => argument == null);
                control.AutoRefreshSeconds = Math.Clamp(control.AutoRefreshSeconds, 0, 300);
                control.Width = Math.Clamp(control.Width, 0, 1200);
            }
        }
        foreach (var windowId in selectedPages.Keys.Where(id => !windowIds.Contains(id)).ToArray()) selectedPages.Remove(windowId);
    }

    private static string Bound(string? value, int count) => value == null ? string.Empty : value.Length <= count ? value : value[..count];
    private void RememberDefinitions()
    {
        savedControls.Clear();
        foreach (var window in Windows.Take(MaxWindows))
            foreach (var control in window.Controls.Take(MaxControls))
                savedControls[(window.Id, control.Id)] = CopyControl(control);
    }
    private static ulong CurrentContentId() => Plugin.PlayerState.IsLoaded ? Plugin.PlayerState.ContentId : 0;
    private static bool ControlMatches(PluginBuilderControlDefinition current, PluginBuilderControlDefinition queued)
        => current.Kind == queued.Kind && current.PageId == queued.PageId && current.MemberPath == queued.MemberPath
            && current.ReflectionReadConfirmed == queued.ReflectionReadConfirmed && current.AutoRefreshSeconds == queued.AutoRefreshSeconds
            && PluginBuilderBindingRules.Matches(current.Source, queued.Source) && current.Arguments.Count == queued.Arguments.Count
            && current.Arguments.Zip(queued.Arguments).All(pair => pair.First.Value == pair.Second.Value
                && pair.First.UseCurrentCharacterId == pair.Second.UseCurrentCharacterId
                && pair.First.UseCurrentCharacterName == pair.Second.UseCurrentCharacterName);
    private static PluginBuilderSource CopySource(PluginBuilderSource source) => new()
    {
        Id = source.Id, Label = source.Label, Kind = source.Kind, PluginName = source.PluginName, ModuleVersionId = source.ModuleVersionId,
        Channel = source.Channel, DeclaringType = source.DeclaringType, MetadataToken = source.MetadataToken, Static = source.Static,
        FieldPath = new(source.FieldPath ?? new()), ParameterTypes = new(source.ParameterTypes ?? new()),
        ParameterNames = new(source.ParameterNames ?? new()), ReturnType = source.ReturnType, CanRead = source.CanRead,
        CanExecute = source.CanExecute, UnsupportedReason = source.UnsupportedReason,
    };
    private static PluginBuilderControlDefinition CopyControl(PluginBuilderControlDefinition value) => new()
    {
        Id = value.Id, Label = value.Label, Kind = value.Kind, Source = CopySource(value.Source), AutoRefreshSeconds = value.AutoRefreshSeconds,
        ReflectionReadConfirmed = value.ReflectionReadConfirmed, MemberPath = value.MemberPath,
        PageId = value.PageId, SameLine = value.SameLine, Width = value.Width,
        Arguments = value.Arguments.Select(argument => new PluginBuilderArgument
            { Value = argument.Value, UseCurrentCharacterId = argument.UseCurrentCharacterId, UseCurrentCharacterName = argument.UseCurrentCharacterName }).ToList(),
    };

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Interlocked.Increment(ref generation);
        Interlocked.Exchange(ref invalidated, 1);
        Plugin.Framework.Update -= OnFrameworkUpdate;
        Plugin.PluginInterface.ActivePluginsChanged -= OnPluginsChanged;
        scanCancellation?.Cancel(); scanCancellation?.Dispose(); scanCancellation = null;
        requests.Clear(); catalogs.Clear(); samples.Clear(); nextReads.Clear(); suspended.Clear(); savedControls.Clear(); openWindows.Clear(); selectedPages.Clear(); Catalog = null; pendingScan = null; WindowsChanged = null;
    }
}
