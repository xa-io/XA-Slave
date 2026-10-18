#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
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
        if (!ImGui.CollapsingHeader("Plugin Inspector##xaAbuse")) return;
        PollPluginInspector();
        ImGui.TextWrapped("Inspect IPC, commands and member metadata for possible integrations. Prefer IPC, then commands, then reviewed reflection. Scanning does not call discovered controls.");
        using (ImRaii.Disabled(inspectorTask != null))
        {
            if (ImGui.Button("Refresh plugins##inspector")) RefreshInspectorPlugins();
            using (var combo = ImRaii.Combo("Plugin##inspector", inspectorSelected.Length == 0 ? "Select plugin" : inspectorSelected))
            if (combo)
            {
                foreach (var name in inspectorPlugins)
                    if (ImGui.Selectable(name, name == inspectorSelected))
                    {
                        inspectorSelected = name;
                        inspectorSnapshot = null;
                        inspectorInstance = null;
                        inspectorExportPath = string.Empty;
                        inspectorStatus = "Ready to scan " + name + ".";
                    }
            }
            using (ImRaii.Disabled(inspectorSelected.Length == 0))
                if (ImGui.Button("Scan IPC and metadata##inspector")) StartPluginInspection();
        }
        if (inspectorTask != null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel scan##inspector")) inspectorCancellation?.Cancel();
        }
        ImGui.TextWrapped(inspectorStatus);
        if (inspectorSnapshot is { } snapshot)
        {
            ImGui.TextWrapped($"Captured {snapshot.Report.Identity.InternalName}; MVID {inspectorModuleId:D}.");
            ImGui.TextWrapped($"Types: {snapshot.Report.Types.Count}; commands: {snapshot.Report.Commands.Count}; metadata entries: {snapshot.MemberCount}; partial: {snapshot.Report.Partial}.");
            DrawInspectorIpc(snapshot.Report);
            if (ImGui.Button("Export JSON##inspector")) ExportPluginInspection();
            using (var details = ImRaii.TreeNode("Discovery limits##inspector"))
            if (details)
                foreach (var limitation in snapshot.Report.Limitations) ImGui.TextWrapped(limitation);
        }
        if (inspectorExportPath.Length > 0)
        {
            ImGui.TextWrapped("Export: " + inspectorExportPath);
            if (ImGui.Button("Copy export path##inspector")) ImGui.SetClipboardText(inspectorExportPath);
        }
    }

    private void DrawInspectorIpc(PluginInspectionReport report)
    {
        if (report.Ipc is not { } ipc) return;
        var attributed = ipc.Channels.Count(channel => channel.AttributedToSelectedPlugin);
        ImGui.TextWrapped($"IPC registry: {ipc.Status}; channels: {ipc.Channels.Count}; providers attributed to selected plugin: {attributed}; declarations: {report.IpcDeclarations.Count}.");
        using var details = ImRaii.TreeNode("IPC discovery##inspector");
        if (!details) return;
        ImGui.TextWrapped("Snapshot only. Registrations can change; signatures do not establish behavior or thread requirements. Event subscribers do not identify the publisher. Shared wrappers can hide ownership.");
        ImGui.Checkbox("Show all registry channels##inspector", ref inspectorShowAllIpc);
        ImGui.InputText("Filter channel##inspector", ref inspectorIpcFilter, 256);
        var matched = 0;
        foreach (var channel in ipc.Channels)
        {
            if (!inspectorShowAllIpc && !channel.AttributedToSelectedPlugin && !channel.SelectedPluginSubscribes) continue;
            if (!channel.Name.Contains(inspectorIpcFilter, StringComparison.OrdinalIgnoreCase)) continue;
            matched++;
            if (matched > 100) continue;
            ImGui.TextUnformatted(channel.Name);
            if (!channel.NameComplete) ImGui.TextWrapped("Channel name incomplete; use a fresh complete capture before mapping a control.");
            ImGui.TextWrapped($"{channel.RegistrationState}; provider ownership: {(channel.AttributedToSelectedPlugin ? "selected assembly evidence" : "unresolved for selected plugin")}; selected plugin subscribes: {channel.SelectedPluginSubscribes}.");
            if (channel.Function != null) ImGui.TextWrapped("Function" + (channel.Function.SignatureComplete ? ": " : " (incomplete): ") + channel.Function.InvokeSignature);
            if (channel.Action != null) ImGui.TextWrapped("Action" + (channel.Action.SignatureComplete ? ": " : " (incomplete): ") + channel.Action.InvokeSignature);
        }
        if (matched == 0) ImGui.TextWrapped("No matching registry channels. Declarations may still be present; check the export and discovery limits.");
        if (matched > 100) ImGui.TextWrapped($"Showing 100 of {matched} matches. Filter further or export JSON for the captured list.");
        foreach (var limitation in ipc.Limitations) ImGui.TextWrapped(limitation);
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
            inspectorStatus = $"{inspectorPlugins.Length} loaded plugins available.";
        }
        catch (Exception error) { inspectorStatus = "Plugin list unavailable: " + error.GetType().Name; }
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
                inspectorStatus = "The selected plugin's loaded instance is unavailable. Refresh and try again.";
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
                report.Limitations.AddRange(commandLimitations);
                report.CommandCaptureIncomplete = commandLimitations.Count > 1;
                report.HasMainUi = hasMainUi;
                report.HasConfigUi = hasConfigUi;
                report.AssemblyVersion = assemblyVersion;
                token.ThrowIfCancellationRequested();
                var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                });
                token.ThrowIfCancellationRequested();
                if (Encoding.UTF8.GetByteCount(json) > InspectorMaxJsonBytes)
                    throw new InvalidOperationException("Inspector export exceeded its size limit.");
                return new InspectorSnapshot(report, json, report.Types.Sum(type => type.Members.Count));
            }, token);
            // Observe faults even if the debug panel stays closed or XA Slave is disposed.
            _ = inspectorTask.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            inspectorStatus = "Scanning IPC registry and metadata...";
        }
        catch (Exception error) { inspectorStatus = "Inspection could not start: " + error.GetType().Name; }
    }

    private static IReadOnlyList<PluginInspectionCommand> CaptureInspectorCommands(Assembly assembly, out List<string> limitations)
    {
        const int maxRegistryEntries = 4096;
        const int maxCommands = 512;
        var commands = new List<PluginInspectionCommand>();
        limitations = ["Commands are attributed by handler declaration or target type assembly. Shared wrappers can hide ownership; this is not proof of complete command coverage."];
        var scanned = 0;
        try
        {
            foreach (var pair in Plugin.CommandManager.Commands)
            {
                if (scanned++ >= maxRegistryEntries || commands.Count >= maxCommands)
                {
                    limitations.Add("Command capture truncated at its registry/command limit.");
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
                        declaredHere ? "Handler declaring assembly matches selected loaded assembly; ownership candidate."
                            : "Handler target type assembly matches selected loaded assembly; ownership candidate."));
                }
                catch (Exception error)
                {
                    if (limitations.Count < 64)
                        limitations.Add("A command entry could not be inspected: " + error.GetType().Name);
                }
            }
        }
        catch (Exception error) { limitations.Add("Command registry capture incomplete: " + error.GetType().Name); }
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
        if (canceled || task.IsCanceled) { inspectorStatus = "Inspection canceled."; return; }
        if (task.IsFaulted)
        {
            inspectorStatus = "Inspection failed: " + (task.Exception?.GetBaseException().GetType().Name ?? "unknown error");
            return;
        }
        if (!InspectorInstanceIsCurrent())
        {
            inspectorInstance = null;
            inspectorStatus = "The plugin unloaded or reloaded during capture. Refresh and scan its current instance.";
            return;
        }
        inspectorSnapshot = task.Result;
        inspectorStatus = "IPC and metadata captured. Review discovery limits, then Export JSON.";
    }

    private void ExportPluginInspection()
    {
        if (isDisposed || inspectorSnapshot == null) return;
        if (!InspectorInstanceIsCurrent())
        {
            inspectorSnapshot = null;
            inspectorStatus = "This capture belongs to an unloaded or replaced instance. Scan again before exporting.";
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
            inspectorStatus = "JSON exported. Member candidates have not been invoked or verified as controls.";
        }
        catch (Exception error) { inspectorStatus = "Export failed: " + error.GetType().Name; }
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
#endif
