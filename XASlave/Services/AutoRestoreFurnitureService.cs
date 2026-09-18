using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

internal sealed class AutoRestoreFurnitureService : IDisposable
{
    private readonly TaskRunner runner;
    private readonly AutoRestoreFurnitureTask task;
    private readonly Action unavailable;
    private FurnitureRestoreContext? context;
    private FurnitureRestoreControls? controls;
    private volatile bool enabled, disposed;
    private long generation;
    private bool showResults;
    internal string StatusText { get; private set; } = "Disabled";
    internal bool IsRunning => task.IsRunning;

    internal AutoRestoreFurnitureService(TaskRunner runner, MessageLogService messages, Action unavailable)
    {
        this.runner = runner; this.unavailable = unavailable;
        task = new(runner, () => new FurnitureRestoreNativeAdapter(context ?? throw new InvalidOperationException("Furniture controls are disabled."),
            messages, Plugin.ToastGui, Plugin.DataManager));
    }

    internal bool SetEnabled(bool value)
    {
        if (disposed) return false;
        if (enabled == value) return enabled;
        if (!value)
        {
            enabled = false; generation++;
            task.Stop("Furniture restoration disabled.");
            Plugin.Framework.Update -= Update;
            Plugin.AddonLifecycle.UnregisterListener(Observe);
            try { RetireControls(); }
            finally { context?.Dispose(); context = null; StatusText = "Disabled"; }
            return false;
        }
        var run = ++generation;
        enabled = true;
        try
        {
            FurnitureRestoreNativeBinding.Prepare();
            // Context invalidation listener must precede controls' destruction.
            var created = new FurnitureRestoreContext(Plugin.AddonLifecycle, reason => task.Stop(reason));
            if (disposed || !enabled || generation != run) { created.Dispose(); return false; }
            context = created;
            Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "HousingGoods", Observe);
            if (disposed || !enabled || generation != run) { Plugin.AddonLifecycle.UnregisterListener(Observe); return false; }
            Plugin.Framework.Update += Update;
            StatusText = "Enabled: open HousingGoods to choose a full furniture batch.";
            return true;
        }
        catch (Exception error)
        {
            enabled = false; Plugin.Framework.Update -= Update; Plugin.AddonLifecycle.UnregisterListener(Observe);
            context?.Dispose(); context = null;
            StatusText = "Unavailable: " + error.Message; return false;
        }
    }

    private void Update(IFramework framework)
    {
        if (disposed || !enabled || context == null) return;
        try
        {
            var host = context.Fresh("HousingGoods");
            if (controls != null && controls.Host != host) RetireControls();
            if (host == null) { StatusText = "Enabled: open HousingGoods to choose a full furniture batch."; return; }
            if (controls == null)
            {
                if (disposed || !enabled || context.Fresh("HousingGoods") != host) return;
                var created = new FurnitureRestoreControls(host.Value, Start, Stop, () => showResults = true,
                    () => !disposed && enabled && (context?.Fresh("HousingGoods") == host));
                if (disposed || !enabled || (context?.Fresh("HousingGoods") != host)) { created.Dispose(); return; }
                controls = created;
            }
            var completed = task.Results.Count(item => item.Outcome != FurnitureRestoreOutcome.NotAttempted);
            var confirmed = task.Results.Count(item => item.Outcome is FurnitureRestoreOutcome.Moved or FurnitureRestoreOutcome.Converted);
            var text = task.Status == "Choose a furniture batch mode from the current HousingGoods session."
                ? "Choose a furniture action." : task.Status;
            if (task.Results.Count > 0) text = $"{completed}/{task.Results.Count} outcomes; {confirmed} confirmed.\n{text}";
            controls.Update(task.IsRunning, !runner.IsRunning, text);
            StatusText = task.Status;
        }
        catch (Exception error)
        {
            SetEnabled(false); unavailable();
            StatusText = "Unavailable: " + error.Message;
            Plugin.Log.Warning(error, "Furniture controls stopped at a native lifecycle boundary.");
        }
    }

    private void Start(FurnitureRestoreMode mode)
    {
        if (disposed || !enabled || context == null || controls == null || context.Fresh("HousingGoods") != controls.Host) return;
        var owner = controls;
        task.Start(mode);
        if (disposed || !enabled || controls != owner) return;
        StatusText = task.Status;
        owner.Update(task.IsRunning, !runner.IsRunning, task.Status);
    }

    internal void Stop() { task.Stop(); StatusText = task.Status; }

    private void Observe(AddonEvent kind, AddonArgs args)
    {
        if (kind != AddonEvent.PreFinalize || controls == null || controls.Host.Address != args.Addon.Address) return;
        task.Stop("HousingGoods finalized.");
        RetireControls();
    }

    internal void RetireControls()
    {
        var retiring = controls; controls = null;
        retiring?.Dispose();
    }

    internal void DrawOptions()
    {
        ImGui.TextWrapped(task.Status);
        ImGui.TextWrapped("Choose a mode on HousingGoods. Load the native storeroom list before a batch that needs storage pages. Each native removal warning requires your own decision.");
        ImGui.BeginDisabled(!task.IsRunning);
        if (ImGui.Button("Stop##FurnitureRestore")) Stop();
        ImGui.EndDisabled(); ImGui.SameLine();
        if (ImGui.Button("Per-item results##FurnitureRestore")) showResults = true;
    }

    internal void DrawResults()
    {
        if (disposed) return;
        if (enabled) controls?.Draw();
        if (!showResults) return;
        ImGui.SetNextWindowSize(new Vector2(720, 420), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Furniture restore results", ref showResults))
        {
            DrawOptions();
            if (ImGui.BeginTable("##FurnitureResults", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(0, 260)))
            {
                ImGui.TableSetupColumn("Initial source"); ImGui.TableSetupColumn("Item / quantity");
                ImGui.TableSetupColumn("Outcome"); ImGui.TableSetupColumn("Detail"); ImGui.TableHeadersRow();
                foreach (var result in task.Results)
                {
                    ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.TextUnformatted($"{result.Source.Container}:{result.Source.Slot}");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted($"{result.Source.State.FullId} x{result.Source.Quantity}");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(result.Outcome.ToString());
                    ImGui.TableNextColumn(); ImGui.TextWrapped(result.Detail);
                }
                ImGui.EndTable();
            }
        }
        ImGui.End();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; enabled = false; generation++; controls?.StopCallbacks();
        Plugin.Framework.Update -= Update; Plugin.AddonLifecycle.UnregisterListener(Observe);
        try { task.Dispose(); }
        finally
        {
            context?.Dispose(); context = null;
            // Plugin retirement also releases the managed toolbar owner.
        }
    }
}
