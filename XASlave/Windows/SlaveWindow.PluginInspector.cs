using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private const int InspectorMaxJsonBytes = 16 * 1024 * 1024;
    private string[] inspectorPlugins = [];
    private string inspectorSelected = string.Empty;
    private string inspectorStatus = "Refresh the loaded plugin list, select a plugin, then Scan.";
    private global::XASlave.Localization.UiMessage? inspectorStatusUiMessage = global::XASlave.Localization.UiMessage.Key("SE4E560F5DD0F88C3");
    private string inspectorExportPath = string.Empty;
    private string inspectorIpcFilter = string.Empty;
    private bool inspectorShowAllIpc;
    private CancellationTokenSource? inspectorCancellation;
    private Task<InspectorSnapshot>? inspectorTask;
    private WeakReference<object>? inspectorInstance;
    private InspectorSnapshot? inspectorSnapshot;
    private Guid inspectorModuleId;
    private sealed record InspectorSnapshot(PluginInspectionReport Report, string Json, int MemberCount);

    private void DrawPluginInspector()
    {
        if (!global::XASlave.Localization.UiNative.CollapsingHeader((global::XASlave.Localization.UiLocale.Native("SE9D93BD5231580C6") + "##xaAbuse"))) return;
        PollPluginInspector();
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S13AAA4C4C3E83E04"));
        using (ImRaii.Disabled(inspectorTask != null))
        {
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S0986F19F7A29E4C0") + "##inspector"))) RefreshInspectorPlugins();
            using (var combo = global::XASlave.Localization.UiNative.ComboScope((global::XASlave.Localization.UiLocale.Native("SAB1173EED1D477D9") + "##inspector"), inspectorSelected.Length == 0 ? global::XASlave.Localization.UiLocale.Get("S53827013EDD04E78") : inspectorSelected))
            if (combo)
            {
                foreach (var name in inspectorPlugins)
                    if (ImGui.Selectable(name, name == inspectorSelected))
                    {
                        inspectorSelected = name;
                        inspectorSnapshot = null;
                        inspectorInstance = null;
                        inspectorExportPath = string.Empty;
                        inspectorStatus = global::XASlave.Localization.UiMessage.Store(ref inspectorStatusUiMessage, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("S597F7BD04E1BC619"), global::XASlave.Localization.UiMessage.Value(name)), global::XASlave.Localization.UiMessage.Key("SCDB4EE2AEA69CC6A")));
                    }
            }
            using (ImRaii.Disabled(inspectorSelected.Length == 0))
                if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S6D9A2DE6103D1EBB") + "##inspector"))) StartPluginInspection();
        }
        if (inspectorTask != null)
        {
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S298360E0B166F75D") + "##inspector"))) inspectorCancellation?.Cancel();
        }
        ImGui.TextWrapped(global::XASlave.Localization.UiMessage.Resolve(ref inspectorStatusUiMessage, inspectorStatus));
        if (inspectorSnapshot is { } snapshot)
        {
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.FormatKey("SA72BC12DBCFF8C01", snapshot.Report.Identity.InternalName, inspectorModuleId));
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.FormatKey("S14161D2EE9B022DE", snapshot.Report.Types.Count, snapshot.Report.Commands.Count, snapshot.MemberCount, snapshot.Report.Partial));
            DrawInspectorIpc(snapshot.Report);
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SFF25653EE9E47F2A") + "##inspector"))) ExportPluginInspection();
            using (var details = global::XASlave.Localization.UiNative.TreeScope((global::XASlave.Localization.UiLocale.Native("SDBB2757E0137D24D") + "##inspector")))
            if (details)
                foreach (var limitation in PluginInspectionPresentation.Limitations(snapshot.Report, snapshot.Report.Limitations)) ImGui.TextWrapped(limitation);
        }
        if (inspectorExportPath.Length > 0)
        {
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.FormatKey("S10EE28D4515037B5", inspectorExportPath));
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S1E6060104EDEE8CC") + "##inspector"))) ImGui.SetClipboardText(inspectorExportPath);
        }
    }

    private void DrawInspectorIpc(PluginInspectionReport report)
    {
        if (report.Ipc is not { } ipc) return;
        var attributed = ipc.Channels.Count(channel => channel.AttributedToSelectedPlugin);
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.FormatKey("SCF9FC1C134F1886E", PluginInspectionPresentation.Status(ipc, ipc.Status), ipc.Channels.Count, attributed, report.IpcDeclarations.Count));
        using var details = global::XASlave.Localization.UiNative.TreeScope((global::XASlave.Localization.UiLocale.Native("S51DF77B6E304E40A") + "##inspector"));
        if (!details) return;
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("SF4655A9A477414CD"));
        global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("SDDC0570C0FDC9063") + "##inspector"), ref inspectorShowAllIpc);
        global::XASlave.Localization.UiNative.InputText((global::XASlave.Localization.UiLocale.Native("S1463741E8FBFE681") + "##inspector"), ref inspectorIpcFilter, 256);
        var matched = 0;
        foreach (var channel in ipc.Channels)
        {
            if (!inspectorShowAllIpc && !channel.AttributedToSelectedPlugin && !channel.SelectedPluginSubscribes) continue;
            if (!channel.Name.Contains(inspectorIpcFilter, StringComparison.OrdinalIgnoreCase)) continue;
            matched++;
            if (matched > 100) continue;
            ImGui.TextUnformatted(channel.Name);
            if (!channel.NameComplete) ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S08F957737A55BDB5"));
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.FormatKey("SFEDB043979A284C8", channel.RegistrationMessage.Display, (channel.AttributedToSelectedPlugin ? global::XASlave.Localization.UiLocale.Get("SF909B541B275B451") : global::XASlave.Localization.UiLocale.Get("S62A959A75BC09212")), channel.SelectedPluginSubscribes));
            if (channel.Function != null) ImGui.TextWrapped(global::XASlave.Localization.UiLocale.FormatKey("SAAF12F8C1085F648", channel.Function.SignatureComplete ? ": " : global::XASlave.Localization.UiLocale.Get("S597234AE46CFD890"), channel.Function.InvokeSignature));
            if (channel.Action != null) ImGui.TextWrapped(global::XASlave.Localization.UiLocale.FormatKey("SFF75D30FDA520964", channel.Action.SignatureComplete ? ": " : global::XASlave.Localization.UiLocale.Get("S597234AE46CFD890"), channel.Action.InvokeSignature));
        }
        if (matched == 0) ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("SF76B03243797CC40"));
        if (matched > 100) ImGui.TextWrapped(global::XASlave.Localization.UiLocale.FormatKey("SF7FAFC372F1A54BF", matched));
        foreach (var limitation in PluginInspectionPresentation.Limitations(ipc, ipc.Limitations)) ImGui.TextWrapped(limitation);
    }

    private void RefreshInspectorPlugins()
    {
        try
        {
            inspectorPlugins = Plugin.PluginInterface.InstalledPlugins.Where(p => p.IsLoaded)
                .Select(p => p.InternalName).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            if (!inspectorPlugins.Contains(inspectorSelected, StringComparer.Ordinal))
            {
                inspectorSelected = string.Empty;
                inspectorSnapshot = null;
                inspectorInstance = null;
            }
            inspectorStatus = global::XASlave.Localization.UiMessage.Store(ref inspectorStatusUiMessage, global::XASlave.Localization.UiMessage.Format("S28E8BB682BA2FDF6", inspectorPlugins.Length));
        }
        catch (Exception error) { inspectorStatus = global::XASlave.Localization.UiMessage.Store(ref inspectorStatusUiMessage, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("SC22825757777599F"), global::XASlave.Localization.UiMessage.Value(error.GetType().Name))); }
    }

    private void StartPluginInspection()
    {
        if (isDisposed || inspectorTask != null) return;
        inspectorSnapshot = null;
        inspectorExportPath = string.Empty;
        inspectorInstance = null;
        try
        {
            var exposed = Plugin.PluginInterface.InstalledPlugins.FirstOrDefault(p => p.IsLoaded && p.InternalName == inspectorSelected);
            var instance = AutoRetainerUiReflectionService.TryGetLoadedPluginInstance(inspectorSelected, string.Empty);
            if (exposed == null || instance == null)
            {
                inspectorStatus = global::XASlave.Localization.UiMessage.Store(ref inspectorStatusUiMessage, global::XASlave.Localization.UiMessage.Key("S5845D296E51D68C9"));
                return;
            }
            var assembly = instance.GetType().Assembly;
            inspectorModuleId = assembly.ManifestModule.ModuleVersionId;
            inspectorInstance = new WeakReference<object>(instance);
            var identity = new PluginInspectionIdentity(inspectorSelected, exposed.Name, exposed.Version.ToString(),
                assembly.GetName().Name ?? string.Empty,
                inspectorModuleId.ToString("D"), DateTime.UtcNow);
            var commands = CaptureInspectorCommands(assembly, out var commandLimitations);
            var hasMainUi = exposed.HasMainUi;
            var hasConfigUi = exposed.HasConfigUi;
            var assemblyVersion = assembly.GetName().Version?.ToString() ?? string.Empty;
            var hostInterface = Plugin.PluginInterface;
            inspectorCancellation?.Dispose();
            inspectorCancellation = CancellationTokenSource.CreateLinkedTokenSource(workerCts.Token);
            var token = inspectorCancellation.Token;
            // The worker holds selected-assembly metadata and the host interface, never the peer instance/window.
            inspectorTask = Task.Run(() =>
            {
                var ipc = PluginInspectionIpcRegistry.Capture(assembly, hostInterface, token);
                var report = PluginInspectionCatalog.Capture(assembly, identity, commands, token);
                report.Ipc = ipc;
                report.Limitations.AddRange(commandLimitations.Select(message => message.Original));
                PluginInspectionPresentation.Append(report, commandLimitations);
                report.CommandCaptureIncomplete = commandLimitations.Count > 1;
                report.HasMainUi = hasMainUi;
                report.HasConfigUi = hasConfigUi;
                report.AssemblyVersion = assemblyVersion;
                token.ThrowIfCancellationRequested();
                var json = PluginInspectionJson.Serialize(report);
                token.ThrowIfCancellationRequested();
                if (Encoding.UTF8.GetByteCount(json) > InspectorMaxJsonBytes)
                    throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S100D0FDC945D07C6"), originalMessage => new InvalidOperationException(originalMessage));
                return new InspectorSnapshot(report, json, report.Types.Sum(type => type.Members.Count));
            }, token);
            // Observe faults even if the debug panel stays closed or XA Slave is disposed.
            _ = inspectorTask.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            inspectorStatus = global::XASlave.Localization.UiMessage.Store(ref inspectorStatusUiMessage, global::XASlave.Localization.UiMessage.Key("SD187EDA6AFD8AE11"));
        }
        catch (Exception error) { inspectorStatus = global::XASlave.Localization.UiMessage.Store(ref inspectorStatusUiMessage, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("SB0331AFAA1246AC9"), global::XASlave.Localization.UiMessage.Value(error.GetType().Name))); }
    }

    private static IReadOnlyList<PluginInspectionCommand> CaptureInspectorCommands(Assembly assembly, out List<global::XASlave.Localization.UiMessage> limitations)
    {
        const int maxRegistryEntries = 4096;
        const int maxCommands = 512;
        var commands = new List<PluginInspectionCommand>();
        limitations = [global::XASlave.Localization.UiMessage.Key("S93A1C851E40EA695")];
        var scanned = 0;
        try
        {
            foreach (var pair in Plugin.CommandManager.Commands)
            {
                if (scanned++ >= maxRegistryEntries || commands.Count >= maxCommands)
                {
                    limitations.Add(global::XASlave.Localization.UiMessage.Key("S07E10BCC72D344F5"));
                    break;
                }
                try
                {
                    var handler = pair.Value.Handler;
                    var method = handler.Method;
                    var targetType = handler.Target?.GetType();
                    var declaredHere = method.DeclaringType?.Assembly == assembly;
                    var targetHere = targetType?.Assembly == assembly;
                    if (!declaredHere && !targetHere) continue;
                    commands.Add(new PluginInspectionCommand(InspectorText(pair.Key), InspectorText(pair.Value.HelpMessage),
                        InspectorText((method.DeclaringType?.FullName ?? "<dynamic>") + "." + method.Name),
                        (declaredHere ? global::XASlave.Localization.UiMessage.Key("SBE146A39EB0856EF") : global::XASlave.Localization.UiMessage.Key("S1C33941227393F93")).Original));
                }
                catch (Exception error)
                {
                    if (limitations.Count < 64)
                        limitations.Add(global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("S3D5DAB93B67BF18B"), global::XASlave.Localization.UiMessage.Raw(error.GetType().Name)));
                }
            }
        }
        catch (Exception error) { limitations.Add(global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("SA3AC8D3E912D043E"), global::XASlave.Localization.UiMessage.Raw(error.GetType().Name))); }
        return commands;
    }

    private static string InspectorText(string? value)
        => value == null ? string.Empty : value.Length <= 2048 ? value : value[..2048] + " [truncated]";

    private bool InspectorInstanceIsCurrent()
    {
        if (isDisposed || inspectorInstance == null || !inspectorInstance.TryGetTarget(out var captured)) return false;
        try
        {
            var current = AutoRetainerUiReflectionService.TryGetLoadedPluginInstance(inspectorSelected, string.Empty);
            return current != null && ReferenceEquals(captured, current)
                && current.GetType().Assembly.ManifestModule.ModuleVersionId == inspectorModuleId;
        }
        catch { return false; }
    }

    private void PollPluginInspector()
    {
        var task = inspectorTask;
        if (task == null || !task.IsCompleted) return;
        inspectorTask = null;
        var canceled = inspectorCancellation?.IsCancellationRequested == true;
        inspectorCancellation?.Dispose();
        inspectorCancellation = null;
        if (canceled || task.IsCanceled) { inspectorStatus = global::XASlave.Localization.UiMessage.Store(ref inspectorStatusUiMessage, global::XASlave.Localization.UiMessage.Key("SA61BB768273A9233")); return; }
        if (task.IsFaulted)
        {
            inspectorStatus = global::XASlave.Localization.UiMessage.Store(ref inspectorStatusUiMessage, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("SD1BD98B7F9A8A300"), ((task.Exception?.GetBaseException().GetType().Name is { } failureType ? global::XASlave.Localization.UiMessage.Raw(failureType) : global::XASlave.Localization.UiMessage.Key("S3E4443E52275A9A0")))));
            return;
        }
        if (!InspectorInstanceIsCurrent())
        {
            inspectorInstance = null;
            inspectorStatus = global::XASlave.Localization.UiMessage.Store(ref inspectorStatusUiMessage, global::XASlave.Localization.UiMessage.Key("S5A8C809317442C36"));
            return;
        }
        inspectorSnapshot = task.Result;
        inspectorStatus = global::XASlave.Localization.UiMessage.Store(ref inspectorStatusUiMessage, global::XASlave.Localization.UiMessage.Key("SA42BDD4E50A5DFF9"));
    }

    private void ExportPluginInspection()
    {
        if (isDisposed || inspectorSnapshot == null) return;
        if (!InspectorInstanceIsCurrent())
        {
            inspectorSnapshot = null;
            inspectorStatus = global::XASlave.Localization.UiMessage.Store(ref inspectorStatusUiMessage, global::XASlave.Localization.UiMessage.Key("SE605F148E3A494A2"));
            return;
        }
        string? temporaryPath = null;
        var ownsTemporary = false;
        try
        {
            var directory = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "plugin-inspections");
            Directory.CreateDirectory(directory);
            // Generated names cannot inherit path separators or reserved names from plugin metadata.
            var destination = Path.Combine(directory, $"plugin-inspection-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
            temporaryPath = destination + ".partial";
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemporary = true;
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    writer.Write(inspectorSnapshot.Json);
            }
            File.Move(temporaryPath, destination, overwrite: false);
            temporaryPath = null;
            inspectorExportPath = destination;
            inspectorStatus = global::XASlave.Localization.UiMessage.Store(ref inspectorStatusUiMessage, global::XASlave.Localization.UiMessage.Key("S2596AA0163D5270D"));
        }
        catch (Exception error) { inspectorStatus = global::XASlave.Localization.UiMessage.Store(ref inspectorStatusUiMessage, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("SE82FD5F506B4B84B"), global::XASlave.Localization.UiMessage.Value(error.GetType().Name))); }
        finally
        {
            if (ownsTemporary && temporaryPath != null)
                try { File.Delete(temporaryPath); } catch { /* An owned partial may remain after an I/O failure. */ }
        }
    }

    private void DisposePluginInspector()
    {
        inspectorCancellation?.Cancel();
        inspectorCancellation?.Dispose();
        inspectorCancellation = null;
        inspectorTask = null;
        inspectorSnapshot = null;
        inspectorInstance = null;
        inspectorPlugins = [];
    }
}
