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

    public NotifyWhenFriendIsNearService(IFramework framework, IClientState clientState, IObjectTable objectTable,
        IToastGui toastGui, IChatGui chatGui, IPluginLog log, Func<string, bool>? speak = null)
    {
        this.framework = framework; this.clientState = clientState; this.objectTable = objectTable;
        this.toastGui = toastGui; this.chatGui = chatGui; this.log = log; this.speak = speak;
        dispatcher = new NearbyPlayerActionDispatcher(this);
    }

    public string StatusText { get; private set; } = "Disabled";
    public string LastActionText { get; private set; } = "No actions yet.";
    public string LastMatchedPlayer { get; private set; } = "None";
    public string SpeechStatus { get { lock (sync) return dispatcher.SpeechStatus; } }
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
            StatusText = enabled ? $"Enabled: {rules.Rules.Count} player notification rule(s)." : "Disabled";
        }
    }

    internal void ReportConfigurationError(string text)
    {
        lock (sync) { configurationError = "Configuration rejected: " + (text.Length <= 256 ? text : text[..256]); StatusText = configurationError; }
    }

    public bool SetEnabled(bool value)
    {
        lock (sync)
        {
            if (disposed) return false;
            if (value == enabled) return enabled;
            enabled = value; ClearSession();
            if (value) Subscribe(); else Unsubscribe();
            StatusText = value ? $"Enabled: {rules.Rules.Count} player notification rule(s)." : "Disabled";
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
        LastMatchedPlayer = "None"; LastActionText = "No actions yet.";
    }
    private void OnLogin() { lock (sync) { if (!disposed) ClearSession(); } }
    private void OnLogout(int _, int __) { lock (sync) { if (!disposed) { ClearSession(); StatusText = enabled ? "Waiting for login" : "Disabled"; } } }
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
                dispatcher.Cancel(); scanner.CancelPass(); StatusText = "Waiting for a valid local player"; return;
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
                    if (completed.Count != 0) LastMatchedPlayer = completed[^1].Player.Name;
                    dispatcher.Enqueue(completed, Now());
                }
                var admittedLogin = loginGeneration;
                dispatcher.Drain(Now());
                if (disposed || !enabled || loginGeneration != admittedLogin) return;
                var regexIssue = rules.Rules.FirstOrDefault(rule => rule.Diagnostic.Length != 0)?.Diagnostic;
                StatusText = scanner.Status.Length != 0 ? scanner.Status : regexIssue ?? (scanner.Pending ? "Scanning nearby players..." : $"Enabled: {rules.Rules.Count} player notification rule(s).");
                if (configurationError.Length != 0) StatusText = configurationError;
                if (dispatcher.Status.Length != 0) LastActionText = dispatcher.Status;
            }
            catch (Exception error)
            {
                dispatcher.Cancel(); scanner.CancelPass(); nextScan = Now() + 2000;
                StatusText = "Scan failed; waiting for the next complete snapshot.";
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
        { Title = "Nearby players", Content = text, Type = NotificationType.Info });
    bool INearbyPlayerNotificationOutput.Speech(string text) => speak?.Invoke(text) == true;
    bool INearbyPlayerNotificationOutput.SendEntry(string text) => ChatHelper.TrySend(text);

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true; enabled = false; ClearSession(); Unsubscribe(); StatusText = "Disabled";
        }
    }
}
