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
    internal string StatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiStatusText, StatusText);
    internal global::XASlave.Localization.UiMessage StatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiStatusText, StatusText);
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
        Stop((value ? global::XASlave.Localization.UiMessage.Key("S1C5FDD0C0EEB7FE8") : global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E")));
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
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S92C1CDFDF4CB9CF6"));
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
        Stop(global::XASlave.Localization.UiMessage.Key("S65DD85BEB5F5051C"));
        var normalized = settings.Normalize();
        configuration.AutoSortItemsSettings = normalized;
        if (normalized != settings) Plugin.Log.Warning("Invalid imported item-sort directions or tab mode were reset to their defaults.");
        enableIntent = intent;
        if (intent) { notBefore = delay; deadline = expires; }
    }

    private void ZoneChanged(uint territory)
    {
        Stop(global::XASlave.Localization.UiMessage.Key("S3ADC8E939C4830F4"));
        if (enabled && !disposed && territory != 0 && Settings.SortAfterZoneChange) Schedule(2000);
    }

    private void Logout(int type, int code) => Stop(global::XASlave.Localization.UiMessage.Key("S7BB5CCB0DC84A8F4"));

    private void Schedule(long delay)
    {
        if (disposed || !enabled || IsRunning) return;
        generation++; pending = true; requested = null;
        var now = Environment.TickCount64; notBefore = now + delay; deadline = now + 15000;
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, (delay == 0 ? global::XASlave.Localization.UiMessage.Key("S64992D3848E22900") : global::XASlave.Localization.UiMessage.Key("S91ECFCCF9D7AA07B")));
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
            if (Environment.TickCount64 >= deadline) { Stop(global::XASlave.Localization.UiMessage.Key("S570195611196C6FF")); return; }
            if (running)
            {
                if (!runner.IsRunning || runner.CurrentRunId != runnerId) Stop(global::XASlave.Localization.UiMessage.Key("S25062DBBFB3F06EC"));
                return;
            }
            if (Environment.TickCount64 < notBefore) return;
            var gate = AutoSortAdmission.Capture(runner.IsRunning);
            if (gate == AutoSortAdmissionResult.Wait) { StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S8A0F039A6B7EC238")); return; }
            if (gate != AutoSortAdmissionResult.Ready) { Stop(global::XASlave.Localization.UiMessage.Key("S735390A88C9B2529")); return; }
            StartTask();
        }
        catch (Exception error)
        {
            Plugin.Log.Warning(error, "Auto Sort Items could not start.");
            Stop(global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("S5CE228D6D72DFD42"), global::XASlave.Localization.UiMessage.FromException(error)));
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
            var accepted = runner.Start(global::XASlave.Localization.UiMessage.Key("SA318747875972AD1"), [new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Key("S60DC306423680F75"), IsComplete = () => Tick(owner),
                TimeoutSec = 16, MaxRetries = 0, OnTimeout = () => Stop(global::XASlave.Localization.UiMessage.Key("S78EABA4C292143AB")),
            }], onFinished: () => Finished(owner), suppressCompletionReport: true, totalItems: 13,
                suppressLogoutCancel: false, onTerminal: () => Terminal(owner), haltOnStepError: true);
            if (!accepted)
            {
                if (owner == generation) Stop(global::XASlave.Localization.UiMessage.Key("SB31FEFCAE8445E49"));
                return;
            }
            var acceptedId = runner.CurrentRunId;
            if (!running || disposed || owner != generation)
            {
                if (runner.IsRunning && runner.CurrentRunId == acceptedId) runner.Cancel();
                return;
            }
            runnerId = acceptedId; StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("SEC6A4A3113E194D9"));
        }
        catch (Exception error)
        {
            Plugin.Log.Warning(error, "Auto Sort Items could not prepare its sorters.");
            if (owner == generation) Stop(global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("S5CE228D6D72DFD42"), global::XASlave.Localization.UiMessage.FromException(error)));
        }
    }

    private void RequireRequest(long owner)
    {
        if (disposed || !enabled || !running || generation != owner || Environment.TickCount64 >= deadline
            || runnerId == 0 || !runner.IsRunning || runner.CurrentRunId != runnerId)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S47E942782733C0B8"), originalMessage => new InvalidOperationException(originalMessage));
        if (AutoSortAdmission.Capture(false) != AutoSortAdmissionResult.Ready)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S06EA689DF56203AC"), originalMessage => new InvalidOperationException(originalMessage));
    }

    private bool Tick(long owner)
    {
        if (disposed || !running || owner != generation) return true;
        try
        {
            RequireRequest(owner);
            if (transaction == null)
            {
                var currentAdapter = adapter ?? throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SE61BA1FEF2D3609A"), originalMessage => new InvalidOperationException(originalMessage));
                foreach (var target in currentAdapter.Capture().Targets)
                {
                    if (target.PassIndex < 0) continue;
                    StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Format("S903A478DDEB7CDAD", target.Container, target.PassIndex, target.Percent, target.Rules.Length));
                    if (!waitingForSorters) Plugin.Log.Information(StatusText);
                    waitingForSorters = true;
                    return false;
                }
                transaction = new(adapter ?? throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SE61BA1FEF2D3609A"), originalMessage => new InvalidOperationException(originalMessage)), owner, () => RequireRequest(owner));
                transaction.Submit(requested ?? throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S265A697DBC8D2B2B"), originalMessage => new InvalidOperationException(originalMessage)));
                StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("SCE86049A1CF8ADE5"));
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
            Stop(global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Format("SAFF7295B827A51D5", observed, 13 - observed), global::XASlave.Localization.UiMessage.FromException(error)));
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
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S68B0BDE8EDA235EF"));
        if (output.SendChat) Plugin.ChatGui.Print(global::XASlave.Localization.UiMessage.Key("S63ED90CB90CEF231").Original);
        if (output.SendNotification && !disposed && enabled && generation == completedOwner) Plugin.NotificationManager.AddNotification(new Notification
        {
            Title = global::XASlave.Localization.UiMessage.Key("SA318747875972AD1").Original, Content = global::XASlave.Localization.UiMessage.Key("S68B0BDE8EDA235EF").Original, Type = NotificationType.Success,
        });
    }

    private void Terminal(long owner)
    {
        if (owner == generation && running) Stop(global::XASlave.Localization.UiMessage.Key("SF3829E822FE2710B"));
    }

    internal void Stop() => Stop(global::XASlave.Localization.UiMessage.Key("S2F8793B60B6AD941"));
    internal void Stop(string reason) => Stop(global::XASlave.Localization.UiMessage.Raw(reason));
    internal void Stop(global::XASlave.Localization.UiMessage reason)
    {
        var ownedRun = runnerId;
        generation++; pending = false; running = false; enableIntent = false; runnerId = 0;
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, reason);
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
        var changed = global::XASlave.Localization.UiNative.Checkbox(global::XASlave.Localization.UiLocale.Native("S354DB4FC34714E93"), ref onEnable);
        changed |= global::XASlave.Localization.UiNative.Checkbox(global::XASlave.Localization.UiLocale.Native("SA06CD4BB0DD9E26A"), ref onZone);
        changed |= global::XASlave.Localization.UiNative.Combo(global::XASlave.Localization.UiLocale.Native("S0AA88FD647E45C44"), ref aId, string.Join("\0", new[] { global::XASlave.Localization.UiLocale.Native("S79479A6C76D8416A"), global::XASlave.Localization.UiLocale.Native("S77184595BDE3BEFC"), string.Empty }));
        changed |= global::XASlave.Localization.UiNative.Combo(global::XASlave.Localization.UiLocale.Native("S2752961FF6286F15"), ref aLevel, string.Join("\0", new[] { global::XASlave.Localization.UiLocale.Native("S79479A6C76D8416A"), global::XASlave.Localization.UiLocale.Native("S77184595BDE3BEFC"), string.Empty }));
        changed |= global::XASlave.Localization.UiNative.Combo(global::XASlave.Localization.UiLocale.Native("S307B3CAEF8219C93"), ref aCategory, string.Join("\0", new[] { global::XASlave.Localization.UiLocale.Native("S79479A6C76D8416A"), global::XASlave.Localization.UiLocale.Native("S77184595BDE3BEFC"), string.Empty }));
        changed |= global::XASlave.Localization.UiNative.Combo(global::XASlave.Localization.UiLocale.Native("S6851C110E6A146C4"), ref iHq, string.Join("\0", new[] { global::XASlave.Localization.UiLocale.Native("S79479A6C76D8416A"), global::XASlave.Localization.UiLocale.Native("S77184595BDE3BEFC"), string.Empty }));
        changed |= global::XASlave.Localization.UiNative.Combo(global::XASlave.Localization.UiLocale.Native("S6C5B5FFF512563DB"), ref iId, string.Join("\0", new[] { global::XASlave.Localization.UiLocale.Native("S79479A6C76D8416A"), global::XASlave.Localization.UiLocale.Native("S77184595BDE3BEFC"), string.Empty }));
        changed |= global::XASlave.Localization.UiNative.Combo(global::XASlave.Localization.UiLocale.Native("SF7AD22B36E362384"), ref iLevel, string.Join("\0", new[] { global::XASlave.Localization.UiLocale.Native("S79479A6C76D8416A"), global::XASlave.Localization.UiLocale.Native("S77184595BDE3BEFC"), string.Empty }));
        changed |= global::XASlave.Localization.UiNative.Combo(global::XASlave.Localization.UiLocale.Native("S6925C8F44E9EAF22"), ref iCategory, string.Join("\0", new[] { global::XASlave.Localization.UiLocale.Native("S79479A6C76D8416A"), global::XASlave.Localization.UiLocale.Native("S77184595BDE3BEFC"), string.Empty }));
        changed |= global::XASlave.Localization.UiNative.Combo(global::XASlave.Localization.UiLocale.Native("S2DC8F25FBD6C01F1"), ref tabs, string.Join("\0", new[] { global::XASlave.Localization.UiLocale.Native("SB3779C6B44FD3771"), global::XASlave.Localization.UiLocale.Native("SBD0A06202C440A9E"), string.Empty }));
        changed |= global::XASlave.Localization.UiNative.Checkbox(global::XASlave.Localization.UiLocale.Native("S5C27510C8D2252DE"), ref chat);
        changed |= global::XASlave.Localization.UiNative.Checkbox(global::XASlave.Localization.UiLocale.Native("SE3780536A5052BC9"), ref notification);
        if (changed)
        {
            ReplaceSettings(new(onEnable, onZone, aId, aLevel, aCategory, iHq, iId, iLevel, iCategory, tabs, chat, notification));
            configuration.Save();
        }
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("SF852D438DED05F80"));
        ImGui.TextWrapped(StatusText);
        ImGui.BeginDisabled(!enabled || IsRunning);
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S3327AC974D14941B") + "##AutoSortItems"))) SortNow();
        ImGui.EndDisabled(); ImGui.SameLine(); ImGui.BeginDisabled(!IsRunning);
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SCAE7D57BC067A514") + "##AutoSortItems"))) Stop();
        ImGui.EndDisabled();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; enabled = false;
        Plugin.Framework.Update -= Update; Plugin.ClientState.TerritoryChanged -= ZoneChanged;
        Plugin.ClientState.Logout -= Logout;
        try { Stop(global::XASlave.Localization.UiMessage.Key("S9B6757F5FDDF2F45")); }
        finally
        {
            var retiring = cachedAdapter; cachedAdapter = null;
            try { retiring?.Dispose(); }
            catch (Exception error) { Plugin.Log.Warning(error, "Item-sort adapter could not fully dispose."); }
        }
    }
}
