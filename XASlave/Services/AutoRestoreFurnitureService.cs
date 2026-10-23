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
    internal string StatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiStatusText, StatusText);
    internal global::XASlave.Localization.UiMessage StatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiStatusText, StatusText);
    internal bool IsRunning => task.IsRunning;

    internal AutoRestoreFurnitureService(TaskRunner runner, MessageLogService messages, Action unavailable)
    {
        this.runner = runner; this.unavailable = unavailable;
        task = new(runner, () => new FurnitureRestoreNativeAdapter(context ?? throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SC0C5DC6A93C89562"), originalMessage => new InvalidOperationException(originalMessage)),
            messages, Plugin.ToastGui, Plugin.DataManager));
    }

    internal bool SetEnabled(bool value)
    {
        if (disposed) return false;
        if (enabled == value) return enabled;
        if (!value)
        {
            enabled = false; generation++;
            task.Stop(global::XASlave.Localization.UiMessage.Key("SDA6B6607DE83C912"));
            Plugin.Framework.Update -= Update;
            Plugin.AddonLifecycle.UnregisterListener(Observe);
            try { RetireControls(); }
            finally { context?.Dispose(); context = null; StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E")); }
            return false;
        }
        var run = ++generation;
        enabled = true;
        try
        {
            FurnitureRestoreNativeBinding.Prepare();
            // Context invalidation listener must precede controls' destruction.
            var created = new FurnitureRestoreContext(Plugin.AddonLifecycle, reason => task.Stop(reason)) { MessageRevoked = reason => task.Stop(reason) };
            if (disposed || !enabled || generation != run) { created.Dispose(); return false; }
            context = created;
            Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "HousingGoods", Observe);
            if (disposed || !enabled || generation != run) { Plugin.AddonLifecycle.UnregisterListener(Observe); return false; }
            Plugin.Framework.Update += Update;
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S7081B426E2E7BAC2"));
            return true;
        }
        catch (Exception error)
        {
            enabled = false; Plugin.Framework.Update -= Update; Plugin.AddonLifecycle.UnregisterListener(Observe);
            context?.Dispose(); context = null;
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("SF5646BBF5EE343C7"), global::XASlave.Localization.UiMessage.FromException(error))); return false;
        }
    }

    private void Update(IFramework framework)
    {
        if (disposed || !enabled || context == null) return;
        try
        {
            var host = context.Fresh("HousingGoods");
            if (controls != null && controls.Host != host) RetireControls();
            if (host == null) { StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S7081B426E2E7BAC2")); return; }
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
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, task.StatusMessage);
        }
        catch (Exception error)
        {
            SetEnabled(false); unavailable();
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("SF5646BBF5EE343C7"), global::XASlave.Localization.UiMessage.FromException(error)));
            Plugin.Log.Warning(error, "Furniture controls stopped at a native lifecycle boundary.");
        }
    }

    private void Start(FurnitureRestoreMode mode)
    {
        if (disposed || !enabled || context == null || controls == null || context.Fresh("HousingGoods") != controls.Host) return;
        var owner = controls;
        task.Start(mode);
        if (disposed || !enabled || controls != owner) return;
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, task.StatusMessage);
        owner.Update(task.IsRunning, !runner.IsRunning, task.Status);
    }

    internal void Stop() { task.Stop(); StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, task.StatusMessage); }

    private void Observe(AddonEvent kind, AddonArgs args)
    {
        if (kind != AddonEvent.PreFinalize || controls == null || controls.Host.Address != args.Addon.Address) return;
        task.Stop(global::XASlave.Localization.UiMessage.Key("S5D9BA91658521EEE"));
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
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S6B18B21D1F28F168"));
        ImGui.BeginDisabled(!task.IsRunning);
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SCAE7D57BC067A514") + "##FurnitureRestore"))) Stop();
        ImGui.EndDisabled(); ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SD37890AACA8D8854") + "##FurnitureRestore"))) showResults = true;
    }

    internal void DrawResults()
    {
        if (disposed) return;
        if (enabled) controls?.Draw();
        if (!showResults) return;
        ImGui.SetNextWindowSize(new Vector2(720, 420), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(global::XASlave.Localization.UiLocale.Native("SEB7847D9D349D0F3"), ref showResults))
        {
            DrawOptions();
            if (ImGui.BeginTable("##FurnitureResults", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(0, 260)))
            {
                global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S3EB6F86354FDFCF0")); global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SFB5C069688BF5E05"));
                global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S4E80ABB5B14656D4")); global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SFB5F27D5457C4641")); global::XASlave.Localization.UiNative.TableHeadersRow();
                foreach (var result in task.Results)
                {
                    ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.TextUnformatted($"{result.Source.Container}:{result.Source.Slot}");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(global::XASlave.Localization.UiLocale.FormatKey("S01C87B980A517967", result.Source.State.FullId, result.Source.Quantity));
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(result.Outcome.ToString());
                    ImGui.TableNextColumn(); ImGui.TextWrapped(result.LocalizedDetail);
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
