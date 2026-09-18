using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

internal sealed class AutoSortItemsService : IDisposable
{
    private readonly Configuration configuration;
    private readonly TaskRunner runner;
    private AutoSortNativeAdapter? adapter;
    private AutoSortNativeAdapter? cachedAdapter;
    private AutoSortTransaction? transaction;
    private AutoSortItemsSettings? requested;
    private long generation, frameworkSequence, runnerId, notBefore, deadline;
    private bool enabled, disposed, pending, running, enableIntent;
    private bool waitingForSorters;
    internal string StatusText { get; private set; } = "Disabled";
    internal bool IsRunning => pending || running || enableIntent;
    internal AutoSortItemsSettings Settings => (configuration.AutoSortItemsSettings ?? new()).Normalize();

    internal AutoSortItemsService(Configuration configuration, TaskRunner runner)
    {
        this.configuration = configuration; this.runner = runner;
        var saved = configuration.AutoSortItemsSettings ?? new();
        var normalized = saved.Normalize();
        configuration.AutoSortItemsSettings = normalized;
        if (saved != normalized) Plugin.Log.Warning("Invalid item-sort directions or tab mode were reset to their defaults.");
    }

    internal bool SetEnabled(bool value)
    {
        if (disposed) return false;
        if (enabled == value) return enabled;
        Stop(value ? "Item sorting enabled." : "Disabled");
        enabled = value;
        if (value)
        {
            Plugin.Framework.Update += Update;
            Plugin.ClientState.TerritoryChanged += ZoneChanged;
            Plugin.ClientState.Logout += Logout;
            // Presets enable definitions before importing options. Retain intent,
            // not a snapshot of the old options, until the next framework turn.
            enableIntent = Plugin.ClientState.IsLoggedIn && Plugin.ClientState.TerritoryType != 0;
            if (enableIntent) { notBefore = Environment.TickCount64 + 2000; deadline = Environment.TickCount64 + 15000; }
            StatusText = "Enabled";
        }
        else
        {
            Plugin.Framework.Update -= Update;
            Plugin.ClientState.TerritoryChanged -= ZoneChanged;
            Plugin.ClientState.Logout -= Logout;
        }
        return enabled;
    }

    internal void ReplaceSettings(AutoSortItemsSettings settings, bool importingPreset = false)
    {
        var intent = importingPreset && enableIntent;
        var delay = notBefore; var expires = deadline;
        Stop("Settings changed; pending item sorting cancelled.");
        var normalized = settings.Normalize();
        configuration.AutoSortItemsSettings = normalized;
        if (normalized != settings) Plugin.Log.Warning("Invalid imported item-sort directions or tab mode were reset to their defaults.");
        enableIntent = intent;
        if (intent) { notBefore = delay; deadline = expires; }
    }

    private void ZoneChanged(uint territory)
    {
        Stop("Zone changed.");
        if (enabled && !disposed && territory != 0 && Settings.SortAfterZoneChange) Schedule(2000);
    }

    private void Logout(int type, int code) => Stop("Logged out; item sorting cancelled.");

    private void Schedule(long delay)
    {
        if (disposed || !enabled || IsRunning) return;
        generation++; pending = true; requested = null;
        var now = Environment.TickCount64; notBefore = now + delay; deadline = now + 15000;
        StatusText = delay == 0 ? "Waiting for sorting admission." : "Sorting after the two-second delay.";
    }

    internal void SortNow()
    {
        if (!enabled || disposed || IsRunning) return;
        Schedule(0);
    }

    private void Update(IFramework framework)
    {
        frameworkSequence++;
        if (disposed || !enabled) return;
        try
        {
            if (enableIntent)
            {
                enableIntent = false;
                if (Settings.SortOnEnable) { generation++; pending = true; }
            }
            if (!pending && !running) return;
            if (Environment.TickCount64 >= deadline) { Stop("Item sorting timed out; any native work remains unconfirmed."); return; }
            if (running)
            {
                if (!runner.IsRunning || runner.CurrentRunId != runnerId) Stop("The owned item-sort task ended.");
                return;
            }
            if (Environment.TickCount64 < notBefore) return;
            var gate = AutoSortAdmission.Capture(runner.IsRunning);
            if (gate == AutoSortAdmissionResult.Wait) { StatusText = "Waiting for login, screen readiness or event completion."; return; }
            if (gate != AutoSortAdmissionResult.Ready) { Stop("Item sorting rejected: the character, zone or task runner is busy."); return; }
            StartTask();
        }
        catch (Exception error)
        {
            Plugin.Log.Warning(error, "Auto Sort Items could not start.");
            Stop("Item sorting unavailable: " + error.Message);
        }
    }

    private void StartTask()
    {
        var owner = generation;
        requested = Settings; pending = false; running = true; runnerId = 0; waitingForSorters = false;
        try
        {
            // Reuse disk layout validation and the read-back observer across
            // requests. No native hook is installed or retained.
            var created = cachedAdapter ?? new AutoSortNativeAdapter(() => frameworkSequence);
            if (!running || disposed || owner != generation)
            {
                if (created != cachedAdapter) created.Dispose();
                return;
            }
            cachedAdapter = created;
            adapter = created;
            var accepted = runner.Start("Auto Sort Items", [new TaskStep
            {
                Name = "Sort normal inventory and armoury", IsComplete = () => Tick(owner),
                TimeoutSec = 16, MaxRetries = 0, OnTimeout = () => Stop("Item sorting timed out; native work is unconfirmed."),
            }], onFinished: () => Finished(owner), suppressCompletionReport: true, totalItems: 13,
                suppressLogoutCancel: false, onTerminal: () => Terminal(owner), haltOnStepError: true);
            if (!accepted)
            {
                if (owner == generation) Stop("The shared task runner rejected item sorting.");
                return;
            }
            var acceptedId = runner.CurrentRunId;
            if (!running || disposed || owner != generation)
            {
                if (runner.IsRunning && runner.CurrentRunId == acceptedId) runner.Cancel();
                return;
            }
            runnerId = acceptedId; StatusText = "Preparing owned item-sort conditions.";
        }
        catch (Exception error)
        {
            Plugin.Log.Warning(error, "Auto Sort Items could not prepare its sorters.");
            if (owner == generation) Stop("Item sorting unavailable: " + error.Message);
        }
    }

    private void RequireRequest(long owner)
    {
        if (disposed || !enabled || !running || generation != owner || Environment.TickCount64 >= deadline
            || runnerId == 0 || !runner.IsRunning || runner.CurrentRunId != runnerId)
            throw new InvalidOperationException("The item-sort request is no longer current.");
        if (AutoSortAdmission.Capture(false) != AutoSortAdmissionResult.Ready)
            throw new InvalidOperationException("The character or zone no longer permits item sorting.");
    }

    private bool Tick(long owner)
    {
        if (disposed || !running || owner != generation) return true;
        try
        {
            RequireRequest(owner);
            if (transaction == null)
            {
                var currentAdapter = adapter ?? throw new InvalidOperationException("Item-sort adapter is unavailable.");
                foreach (var target in currentAdapter.Capture().Targets)
                {
                    if (target.PassIndex < 0) continue;
                    StatusText = $"Waiting for active item sorter: container {target.Container}, pass {target.PassIndex}, {target.Percent}% complete, {target.Rules.Length} rules.";
                    if (!waitingForSorters) Plugin.Log.Information(StatusText);
                    waitingForSorters = true;
                    return false;
                }
                transaction = new(adapter ?? throw new InvalidOperationException("Item-sort adapter is unavailable."), owner, () => RequireRequest(owner));
                transaction.Submit(requested ?? throw new InvalidOperationException("Item-sort settings are unavailable."));
                StatusText = "Native item sorting started; waiting for verified completion.";
            }
            var complete = transaction.Poll();
            if (complete) runner.CompletedItems = 13;
            return complete;
        }
        catch (Exception error)
        {
            Plugin.Log.Warning(error, "Auto Sort Items stopped before verified completion.");
            var observed = transaction?.ObservedStartCount ?? 0;
            transaction?.TryCleanup();
            Stop($"Item sorting stopped with an unconfirmed outcome ({observed}/13 targets observed starting; {13 - observed} not observed): " + error.Message);
            return true;
        }
    }

    private void Finished(long owner)
    {
        if (owner != generation || !running || (transaction?.Complete) != true || requested == null) return;
        var output = requested;
        // Completion is already proved by two distinct consecutive native snapshots.
        // Retire ownership before local output can invoke reentrant plugin handlers.
        running = false; runnerId = 0; generation++;
        var completedOwner = generation;
        Release();
        if (disposed || !enabled || generation != completedOwner) return;
        StatusText = "Normal inventory and armoury sorting completed.";
        if (output.SendChat) Plugin.ChatGui.Print("[XA] Normal inventory and armoury sorting completed.");
        if (output.SendNotification && !disposed && enabled && generation == completedOwner) Plugin.NotificationManager.AddNotification(new Notification
        {
            Title = "Auto Sort Items", Content = "Normal inventory and armoury sorting completed.", Type = NotificationType.Success,
        });
    }

    private void Terminal(long owner)
    {
        if (owner == generation && running) Stop("The item-sort task ended without verified completion.");
    }

    internal void Stop(string reason = "Item sorting stopped; any started native sorts are left to finish.")
    {
        var ownedRun = runnerId;
        generation++; pending = false; running = false; enableIntent = false; runnerId = 0;
        StatusText = reason;
        try { if (ownedRun != 0 && runner.IsRunning && runner.CurrentRunId == ownedRun) runner.Cancel(); }
        finally { Release(); }
    }

    private void Release()
    {
        var retiring = adapter; adapter = null; transaction = null; requested = null;
        try { retiring?.End(); }
        catch (Exception error) { Plugin.Log.Warning(error, "Item-sort observation could not fully retire."); }
    }

    internal void DrawOptions()
    {
        var settings = Settings;
        var onEnable = settings.SortOnEnable; var onZone = settings.SortAfterZoneChange;
        var chat = settings.SendChat; var notification = settings.SendNotification;
        var aId = settings.ArmouryId; var aLevel = settings.ArmouryItemLevel; var aCategory = settings.ArmouryCategory;
        var iHq = settings.InventoryHq; var iId = settings.InventoryId; var iLevel = settings.InventoryItemLevel;
        var iCategory = settings.InventoryCategory; var tabs = settings.InventoryTabs;
        var changed = ImGui.Checkbox("Sort on enable (two-second delay)", ref onEnable);
        changed |= ImGui.Checkbox("Sort after zone changes", ref onZone);
        changed |= ImGui.Combo("Armoury ID", ref aId, "Descending\0Ascending\0");
        changed |= ImGui.Combo("Armoury item level", ref aLevel, "Descending\0Ascending\0");
        changed |= ImGui.Combo("Armoury category", ref aCategory, "Descending\0Ascending\0");
        changed |= ImGui.Combo("Inventory HQ", ref iHq, "Descending\0Ascending\0");
        changed |= ImGui.Combo("Inventory ID", ref iId, "Descending\0Ascending\0");
        changed |= ImGui.Combo("Inventory item level", ref iLevel, "Descending\0Ascending\0");
        changed |= ImGui.Combo("Inventory category", ref iCategory, "Descending\0Ascending\0");
        changed |= ImGui.Combo("Inventory tabs", ref tabs, "Split tabs\0Merged\0");
        changed |= ImGui.Checkbox("Local completion chat", ref chat);
        changed |= ImGui.Checkbox("Completion notification", ref notification);
        if (changed)
        {
            ReplaceSettings(new(onEnable, onZone, aId, aLevel, aCategory, iHq, iId, iLevel, iCategory, tabs, chat, notification));
            configuration.Save();
        }
        ImGui.TextWrapped("Enabling can sort after two seconds. Pending game sort commands cause a busy result. Later sorting passes take priority; earlier passes break ties.");
        ImGui.TextWrapped(StatusText);
        ImGui.BeginDisabled(!enabled || IsRunning);
        if (ImGui.Button("Sort now##AutoSortItems")) SortNow();
        ImGui.EndDisabled(); ImGui.SameLine(); ImGui.BeginDisabled(!IsRunning);
        if (ImGui.Button("Stop##AutoSortItems")) Stop();
        ImGui.EndDisabled();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; enabled = false;
        Plugin.Framework.Update -= Update; Plugin.ClientState.TerritoryChanged -= ZoneChanged;
        Plugin.ClientState.Logout -= Logout;
        try { Stop("Item sorting disposed."); }
        finally
        {
            var retiring = cachedAdapter; cachedAdapter = null;
            try { retiring?.Dispose(); }
            catch (Exception error) { Plugin.Log.Warning(error, "Item-sort adapter could not fully dispose."); }
        }
    }
}
