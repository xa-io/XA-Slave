using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

public sealed class NotifyWhenFriendIsNearService : IDisposable, INearbyPlayerNotificationOutput
{
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IToastGui toastGui;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;
    private readonly Func<string, bool>? speak;
    private readonly object sync = new();
    private readonly NearbyPlayerScanEngine scanner = new();
    private readonly NearbyPlayerActionDispatcher dispatcher;
    private NearbyPlayerRuleSnapshot rules = NearbyPlayerRuleSnapshot.Create(null, null, Array.Empty<string>(), 300, false);
    private bool enabled, subscribed, disposed;
    private long loginGeneration, nextScan;
    private uint scanTerritory;
    private string configurationError = string.Empty;
    private global::XASlave.Localization.UiMessage? configurationErrorMessage;

    public NotifyWhenFriendIsNearService(IFramework framework, IClientState clientState, IObjectTable objectTable,
        IToastGui toastGui, IChatGui chatGui, IPluginLog log, Func<string, bool>? speak = null)
    {
        this.framework = framework; this.clientState = clientState; this.objectTable = objectTable;
        this.toastGui = toastGui; this.chatGui = chatGui; this.log = log; this.speak = speak;
        dispatcher = new NearbyPlayerActionDispatcher(this);
    }

    public string StatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiStatusText, StatusText);
    internal global::XASlave.Localization.UiMessage StatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiStatusText, StatusText);
    public string LastActionText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S03F020CBB71754CC");
    private global::XASlave.Localization.UiMessage? uiLastActionText = global::XASlave.Localization.UiMessage.Key("S03F020CBB71754CC");
    internal string LocalizedLastActionText => global::XASlave.Localization.UiMessage.Resolve(ref uiLastActionText, LastActionText);
    internal global::XASlave.Localization.UiMessage LastActionTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiLastActionText, LastActionText);
    public string LastMatchedPlayer { get; private set; } = global::XASlave.Localization.UiMessage.Key("SDC937B59892604F5").Original;
    private global::XASlave.Localization.UiMessage? uiLastMatchedPlayer = global::XASlave.Localization.UiMessage.Key("SDC937B59892604F5");
    internal global::XASlave.Localization.UiMessage LastMatchedPlayerMessage => global::XASlave.Localization.UiMessage.Capture(ref uiLastMatchedPlayer, LastMatchedPlayer);
    internal string LocalizedLastMatchedPlayer => global::XASlave.Localization.UiMessage.Resolve(ref uiLastMatchedPlayer, LastMatchedPlayer);
    public string SpeechStatus { get { lock (sync) return dispatcher.SpeechStatus; } }
    internal global::XASlave.Localization.UiMessage SpeechStatusMessage { get { lock (sync) return dispatcher.SpeechStatusMessage; } }
    internal string LocalizedSpeechStatus => SpeechStatusMessage.Display;
    public int PatternCount { get { lock (sync) return rules.Rules.Count(rule => rule.LegacyGroup); } }
    public int RuleCount { get { lock (sync) return rules.Rules.Count; } }
    public static int NormalizeCooldownSeconds(int value) => Math.Clamp(value, 10, 3600);
    private static long Now() => (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency));

    public void ApplyConfiguration(IEnumerable<string> patterns, int cooldownSeconds)
        => ApplyConfiguration(NearbyPlayerRuleSnapshot.Create(null, null, patterns, cooldownSeconds, false));

    internal void ApplyConfiguration(NearbyPlayerRuleSnapshot validated, Guid? changedRule = null)
    {
        lock (sync)
        {
            if (disposed) return;
            // Validation occurs before publication. Cooldowns survive configuration
            // replacement; queued/partial work belongs to the prior generation.
            if (changedRule.HasValue) { dispatcher.CancelRule(changedRule.Value); scanner.ChangeRule(changedRule.Value); }
            else { dispatcher.Cancel(); scanner.CancelPass(); }
            rules = validated; nextScan = 0; configurationError = string.Empty;
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, (enabled ? global::XASlave.Localization.UiMessage.Format("S5C29F50D3E75F8B9", rules.Rules.Count) : global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E")));
        }
    }

    internal void ReportConfigurationError(string text) => ReportConfigurationError(global::XASlave.Localization.UiMessage.Raw(text));

    internal void ReportConfigurationError(global::XASlave.Localization.UiMessage text)
    {
        lock (sync) { configurationError = global::XASlave.Localization.UiMessage.Store(ref configurationErrorMessage, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("S915244494A4076B6"), global::XASlave.Localization.UiMessage.Truncate(text, 256))); StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Capture(ref configurationErrorMessage, configurationError)); }
    }

    public bool SetEnabled(bool value)
    {
        lock (sync)
        {
            if (disposed) return false;
            if (value == enabled) return enabled;
            enabled = value; ClearSession();
            if (value) Subscribe(); else Unsubscribe();
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, (value ? global::XASlave.Localization.UiMessage.Format("S5C29F50D3E75F8B9", rules.Rules.Count) : global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E")));
            return value;
        }
    }

    private void Subscribe()
    {
        if (subscribed) return;
        framework.Update += OnFrameworkUpdate;
        clientState.Login += OnLogin;
        clientState.Logout += OnLogout;
        clientState.TerritoryChanged += OnTerritoryChanged;
        subscribed = true;
    }
    private void Unsubscribe()
    {
        if (!subscribed) return;
        framework.Update -= OnFrameworkUpdate;
        clientState.Login -= OnLogin;
        clientState.Logout -= OnLogout;
        clientState.TerritoryChanged -= OnTerritoryChanged;
        subscribed = false;
    }
    private void ClearSession()
    {
        dispatcher.Cancel(); scanner.Reset(++loginGeneration); nextScan = 0;
        LastMatchedPlayer = global::XASlave.Localization.UiMessage.Store(ref uiLastMatchedPlayer, global::XASlave.Localization.UiMessage.Key("SDC937B59892604F5")); LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Key("S03F020CBB71754CC"));
    }
    private void OnLogin() { lock (sync) { if (!disposed) ClearSession(); } }
    private void OnLogout(int _, int __) { lock (sync) { if (!disposed) { ClearSession(); StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, (enabled ? global::XASlave.Localization.UiMessage.Key("SCD1C385A747ED625") : global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"))); } } }
    private void OnTerritoryChanged(uint _)
    {
        lock (sync)
        {
            if (disposed) return;
            dispatcher.Cancel(); scanner.CancelPass(); nextScan = 0;
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        lock (sync)
        {
            if (disposed || !enabled || !framework.IsInFrameworkUpdateThread) return;
            if (!clientState.IsLoggedIn || objectTable.LocalPlayer == null)
            {
                // A missing/aborted snapshot is not an unmatched scan.
                dispatcher.Cancel(); scanner.CancelPass(); StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S0513F63EE1E68EE4")); return;
            }
            try
            {
                var now = Now();
                if (!scanner.Pending && now >= nextScan)
                {
                    var observations = SnapshotPlayers();
                    scanTerritory = clientState.TerritoryType;
                    scanner.Begin(rules, observations, loginGeneration, scanTerritory, now, dispatcher.FreeJobs);
                    nextScan = now + 2000;
                }
                var completed = scanner.Advance(Now);
                if (completed != null)
                {
                    if (completed.Count != 0) LastMatchedPlayer = global::XASlave.Localization.UiMessage.Store(ref uiLastMatchedPlayer, global::XASlave.Localization.UiMessage.Raw(completed[^1].Player.Name));
                    dispatcher.Enqueue(completed, Now());
                }
                var admittedLogin = loginGeneration;
                dispatcher.Drain(Now());
                if (disposed || !enabled || loginGeneration != admittedLogin) return;
                var regexIssue = rules.Rules.FirstOrDefault(rule => rule.Diagnostic.Length != 0)?.DiagnosticMessage;
                StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, (scanner.Status.Length != 0 ? scanner.StatusMessage : (regexIssue is { } currentRegexIssue ? currentRegexIssue : (scanner.Pending ? global::XASlave.Localization.UiMessage.Key("S187AF308D5C142CE") : global::XASlave.Localization.UiMessage.Format("S5C29F50D3E75F8B9", rules.Rules.Count)))));
                if (configurationError.Length != 0) StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Capture(ref configurationErrorMessage, configurationError));
                if (dispatcher.Status.Length != 0) LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, dispatcher.StatusMessage);
            }
            catch (Exception error)
            {
                dispatcher.Cancel(); scanner.CancelPass(); nextScan = Now() + 2000;
                StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S6B7D2BD501FC6076"));
                log.Warning(error, "[XASlave] Player notification scan failed.");
            }
        }
    }

    private IReadOnlyList<NearbyPlayerObservation> SnapshotPlayers()
    {
        var local = objectTable.LocalPlayer ?? throw new InvalidOperationException("Local player unavailable.");
        var result = new List<NearbyPlayerObservation>();
        foreach (var obj in objectTable)
        {
            if (obj is not IPlayerCharacter player || player.GameObjectId == local.GameObjectId) continue;
            var world = player.HomeWorld;
            if (world.RowId == 0 || world.RowId == ushort.MaxValue || !world.IsValid) continue;
            var name = player.Name.TextValue;
            var worldName = world.Value.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(worldName)) continue;
            result.Add(new(name, world.RowId, worldName, player.OnlineStatus.RowId, player.GameObjectId));
        }
        return result;
    }

    bool INearbyPlayerNotificationOutput.IsCurrent(NearbyPlayerEmission emission, Guid ruleId)
        => !disposed && enabled && framework.IsInFrameworkUpdateThread && clientState.IsLoggedIn
            && clientState.TerritoryType == scanTerritory && emission.Key.Login == loginGeneration
            && scanner.StillMatches(emission, ruleId);
    void INearbyPlayerNotificationOutput.LocalChat(string text) => chatGui.Print("[XASlave] " + text);
    void INearbyPlayerNotificationOutput.Toast(string text) => toastGui.ShowNormal(text);
    void INearbyPlayerNotificationOutput.Notification(string text) => Plugin.NotificationManager.AddNotification(new Notification
        { Title = global::XASlave.Localization.UiLocale.Native("S67DA8BCD8F0CF5A6"), Content = text, Type = NotificationType.Info });
    bool INearbyPlayerNotificationOutput.Speech(string text) => speak?.Invoke(text) == true;
    bool INearbyPlayerNotificationOutput.SendEntry(string text) => ChatHelper.TrySend(text);

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true; enabled = false; ClearSession(); Unsubscribe(); StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
        }
    }
}
