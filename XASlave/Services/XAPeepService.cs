using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using XASlave.Data;

namespace XASlave.Services;

public sealed class XAPeepService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SoundCooldown = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan CenterNotificationDuration = TimeSpan.FromSeconds(3);
    private static float UiScale => ImGuiHelpers.GlobalScale;
    private static readonly Vector4 HeaderPurple = new(0.82f, 0.49f, 0.96f, 1.0f);
    private static readonly Vector4 BoxPurple = new(0.19f, 0.06f, 0.24f, 0.88f);

    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly SlaveDatabaseService slaveDatabase;
    private readonly Configuration configuration;

    private readonly Stopwatch pollTimer = Stopwatch.StartNew();
    private readonly Dictionary<ulong, ActiveTargeterState> activeTargeters = new();
    private readonly Dictionary<string, XAPeepPlayerSummary> playerSummaryCache = new(StringComparer.OrdinalIgnoreCase);

    private List<XAPeepPlayerSummary> recentPlayers = new();
    private List<XAPeepSessionRecord> recentSessions = new();

    private bool enabled;
    private bool subscribed;
    private DateTime lastSoundAtUtc = DateTime.MinValue;
    private DateTime centerNotificationUntilUtc = DateTime.MinValue;
    private string centerNotificationText = string.Empty;

    public XAPeepService(
        IFramework framework,
        IClientState clientState,
        ICondition condition,
        IObjectTable objectTable,
        IGameGui gameGui,
        IPluginLog log,
        SlaveDatabaseService slaveDatabase,
        Configuration configuration)
    {
        this.framework = framework;
        this.clientState = clientState;
        this.condition = condition;
        this.objectTable = objectTable;
        this.gameGui = gameGui;
        this.log = log;
        this.slaveDatabase = slaveDatabase;
        this.configuration = configuration;

        RefreshHistoryCache();
        UpdateStatusText();
    }

    public bool IsEnabled => enabled;

    public int ActiveCount => activeTargeters.Count;

    public string StatusText { get; private set; } = "Disabled";

    public bool SetEnabled(bool value)
    {
        if (enabled == value)
        {
            UpdateStatusText();
            return enabled;
        }

        if (!value)
            FinalizeAllActiveTargeters(DateTime.UtcNow);

        enabled = value;
        UpdateSubscriptions();

        if (enabled)
        {
            pollTimer.Restart();
            RefreshHistoryCache();
            StatusText = recentPlayers.Count > 0
                ? $"Enabled - starting. {recentPlayers.Count} tracked players cached in history."
                : "Enabled - starting.";
        }
        else
        {
            UpdateStatusText();
        }

        return enabled;
    }

    public List<XAPeepLiveTargeterView> GetLiveTargeters()
    {
        var nowUtc = DateTime.UtcNow;
        return activeTargeters.Values
            .Select(state => state.ToView(nowUtc))
            .OrderByDescending(view => view.StartedUtc)
            .ThenBy(view => view.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public List<XAPeepTrackedPlayerView> GetTrackedPlayers(int limit = 200)
    {
        limit = Math.Clamp(limit, 1, 500);

        var nowUtc = DateTime.UtcNow;
        var liveViews = activeTargeters.Values
            .Select(state => state.ToTrackedPlayerView(nowUtc))
            .OrderByDescending(view => view.CurrentStartedUtc)
            .ThenBy(view => view.CompactName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var liveKeys = new HashSet<string>(liveViews.Select(view => view.PlayerKey), StringComparer.OrdinalIgnoreCase);
        var historyViews = recentPlayers
            .Where(summary => !liveKeys.Contains(summary.PlayerKey))
            .Select(summary => new XAPeepTrackedPlayerView(
                summary.PlayerKey,
                summary.DisplayName,
                XAPeepData.FormatCompactName(summary.DisplayName),
                summary.HomeWorldId,
                summary.JobId,
                false,
                0,
                summary.FirstTargetedUtc,
                summary.LastTargetedUtc,
                DateTime.MinValue,
                summary.TotalTargetCount,
                summary.TotalTargetDurationSeconds))
            .OrderByDescending(view => view.LastSeenUtc)
            .ThenBy(view => view.CompactName, StringComparer.OrdinalIgnoreCase);

        return liveViews
            .Concat(historyViews)
            .Take(limit)
            .ToList();
    }

    public List<XAPeepPlayerSummary> GetRecentPlayers(int limit = 200)
    {
        limit = Math.Clamp(limit, 1, 500);
        return recentPlayers
            .Take(limit)
            .ToList();
    }

    public List<XAPeepSessionRecord> GetRecentSessions(int limit = 200)
    {
        limit = Math.Clamp(limit, 1, 500);
        return recentSessions
            .Take(limit)
            .ToList();
    }

    public void ClearHistory()
    {
        slaveDatabase.ClearXAPeepHistory();
        recentPlayers = new();
        recentSessions = new();
        playerSummaryCache.Clear();
        centerNotificationText = string.Empty;
        centerNotificationUntilUtc = DateTime.MinValue;

        foreach (var state in activeTargeters.Values)
            state.ResetPersistedHistory();

        UpdateStatusText();
    }

    public void PlayConfiguredSoundPreview()
    {
        TryPlayConfiguredSound();
    }

    public void DrawOverlay()
    {
        var nowUtc = DateTime.UtcNow;
        var showCenterNotification = configuration.XAPeepShowCenterNotification
            && !string.IsNullOrWhiteSpace(centerNotificationText)
            && centerNotificationUntilUtc > nowUtc;

        if (!enabled && !showCenterNotification)
            return;

        var viewport = ImGui.GetMainViewport();
        var drawList = ImGui.GetForegroundDrawList();
        var headerColor = ImGui.GetColorU32(HeaderPurple);
        var boxColor = ImGui.GetColorU32(BoxPurple);
        var outlineColor = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.65f));

        if (showCenterNotification)
            DrawCenteredOverlayTag(drawList, viewport.Pos + (viewport.Size * 0.5f), centerNotificationText, headerColor, boxColor, outlineColor);

        if (!enabled || activeTargeters.Count == 0 || !clientState.IsLoggedIn)
            return;

        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null)
            return;

        var showLocalCard = configuration.XAPeepDisplayLineWhenTargetingMe;
        var showTargeterLine = configuration.XAPeepShowTargeterLine;
        var showTargeterDot = configuration.XAPeepShowTargeterDot;
        var showTargetersCard = configuration.XAPeepShowTargetersCard;
        if (!showLocalCard && !showTargeterLine && !showTargeterDot && !showTargetersCard)
            return;

        var liveTargeters = GetLiveTargeters();
        if (liveTargeters.Count == 0)
            return;

        var hasLocalPosition = gameGui.WorldToScreen(localPlayer.Position, out var localScreenPos);
        if (!hasLocalPosition)
            localScreenPos = viewport.Pos + (viewport.Size * 0.5f);

        var lineColor = ImGui.GetColorU32(configuration.XAPeepTargeterLineColor);
        var dotColor = ImGui.GetColorU32(configuration.XAPeepTargeterDotColor);
        var dotSize = Scale(Math.Clamp(configuration.XAPeepTargeterDotSize, 1f, 15f));

        if (showLocalCard)
        {
            var summaryText = liveTargeters.Count == 1
                ? "1 player is targeting you"
                : $"{liveTargeters.Count} players are targeting you";
            DrawOverlayTag(drawList, localScreenPos + ScaledVector(18f, -42f), summaryText, headerColor, boxColor, outlineColor);
        }

        var actorsById = new Dictionary<ulong, IPlayerCharacter>();
        foreach (var obj in objectTable)
        {
            if (obj is IPlayerCharacter player && !actorsById.ContainsKey(player.GameObjectId))
                actorsById[player.GameObjectId] = player;
        }

        foreach (var view in liveTargeters)
        {
            if (!actorsById.TryGetValue(view.GameObjectId, out var actor))
                continue;

            if (!gameGui.WorldToScreen(actor.Position, out var targetScreenPos))
                continue;

            if (showTargeterLine)
                drawList.AddLine(localScreenPos, targetScreenPos, lineColor, Scale(3f));

            if (showTargeterDot)
                drawList.AddCircleFilled(targetScreenPos, dotSize, dotColor, 18);

            if (showTargetersCard)
            {
                var label = $"{view.CompactName} x{view.TotalTargetCount}";
                DrawOverlayTag(drawList, targetScreenPos + ScaledVector(12f, -10f), label, headerColor, boxColor, outlineColor);
            }
        }
    }

    public void Dispose()
    {
        FinalizeAllActiveTargeters(DateTime.UtcNow);
        enabled = false;
        UpdateSubscriptions();
        UpdateStatusText();
    }

    private void UpdateSubscriptions()
    {
        if (enabled == subscribed)
            return;

        if (enabled)
        {
            framework.Update += OnFrameworkUpdate;
            clientState.Login += OnLogin;
            clientState.Logout += OnLogout;
            pollTimer.Restart();
        }
        else
        {
            framework.Update -= OnFrameworkUpdate;
            clientState.Login -= OnLogin;
            clientState.Logout -= OnLogout;
        }

        subscribed = enabled;
    }

    private void OnLogin()
    {
        pollTimer.Restart();
        UpdateStatusText();
    }

    private void OnLogout(int type, int code)
    {
        FinalizeAllActiveTargeters(DateTime.UtcNow);
        centerNotificationText = string.Empty;
        centerNotificationUntilUtc = DateTime.MinValue;
        UpdateStatusText();
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (pollTimer.Elapsed < PollInterval)
            return;

        pollTimer.Restart();
        PollTargeters(DateTime.UtcNow);
    }

    private void PollTargeters(DateTime nowUtc)
    {
        if (!enabled)
        {
            UpdateStatusText();
            return;
        }

        IPlayerCharacter? localPlayer;
        try
        {
            localPlayer = objectTable.LocalPlayer;
        }
        catch (InvalidOperationException ex) when (IsNotOnMainThreadException(ex))
        {
            return;
        }

        if (!clientState.IsLoggedIn || localPlayer == null)
        {
            UpdateStatusText();
            return;
        }

        if (IsDutyLoggingPaused())
        {
            FinalizeAllActiveTargeters(nowUtc);
            UpdateStatusText();
            return;
        }

        var visibleTargeterIds = new HashSet<ulong>();

        foreach (var player in EnumerateTrackedPlayers(localPlayer.GameObjectId))
        {
            visibleTargeterIds.Add(player.GameObjectId);

            if (!activeTargeters.TryGetValue(player.GameObjectId, out var state))
            {
                state = CreateState(player, nowUtc);
                activeTargeters[player.GameObjectId] = state;
                ShowCenterNotification(state, nowUtc);
                ShowChatNotification(state);
                TryPlayTargetSound(nowUtc);
            }

            state.Refresh(player, nowUtc);
        }

        foreach (var staleId in activeTargeters.Keys.Where(id => !visibleTargeterIds.Contains(id)).ToArray())
            FinalizeTargeter(staleId, nowUtc);

        UpdateStatusText();
    }

    private IEnumerable<IPlayerCharacter> EnumerateTrackedPlayers(ulong localPlayerGameObjectId)
    {
        foreach (var obj in objectTable)
        {
            if (obj is not IPlayerCharacter player)
                continue;

            if (ShouldTrackPlayer(player, localPlayerGameObjectId))
                yield return player;
        }
    }

    private bool ShouldTrackPlayer(IPlayerCharacter player, ulong localPlayerGameObjectId)
    {
        if (player.GameObjectId == 0 || player.GameObjectId == localPlayerGameObjectId)
            return false;

        if (player.TargetObjectId != localPlayerGameObjectId)
            return false;

        if (IsDutyLoggingPaused())
            return false;

        if (!configuration.XAPeepLogParty && player.StatusFlags.HasFlag(StatusFlags.PartyMember))
            return false;

        if (!configuration.XAPeepLogAlliance && player.StatusFlags.HasFlag(StatusFlags.AllianceMember))
            return false;

        if (!configuration.XAPeepLogInCombat && player.StatusFlags.HasFlag(StatusFlags.InCombat))
            return false;

        return true;
    }

    private bool IsDutyLoggingPaused()
    {
        return !configuration.XAPeepLogInDuty && condition[ConditionFlag.BoundByDuty];
    }

    private ActiveTargeterState CreateState(IPlayerCharacter player, DateTime nowUtc)
    {
        var name = player.Name.TextValue;
        var homeWorldId = player.HomeWorld.RowId;
        var playerKey = XAPeepData.NormalizePlayerKey(name, homeWorldId);

        if (!playerSummaryCache.TryGetValue(playerKey, out var summary))
        {
            if (slaveDatabase.TryGetXAPeepPlayerSummary(playerKey, out var loadedSummary) && loadedSummary != null)
            {
                summary = loadedSummary;
                playerSummaryCache[playerKey] = loadedSummary;
            }
        }

        return new ActiveTargeterState(
            playerKey,
            name,
            homeWorldId,
            player.ClassJob.RowId,
            player.GameObjectId,
            nowUtc,
            summary?.FirstTargetedUtc ?? DateTime.MinValue,
            summary?.TotalTargetCount ?? 0,
            summary?.TotalTargetDurationSeconds ?? 0d);
    }

    private void FinalizeAllActiveTargeters(DateTime nowUtc)
    {
        foreach (var gameObjectId in activeTargeters.Keys.ToArray())
            FinalizeTargeter(gameObjectId, nowUtc);
    }

    private void FinalizeTargeter(ulong gameObjectId, DateTime nowUtc)
    {
        if (!activeTargeters.Remove(gameObjectId, out var state))
            return;

        var duration = state.GetDuration(nowUtc);
        var territoryId = clientState.TerritoryType;
        slaveDatabase.RecordXAPeepSession(state.Name, state.HomeWorldId, state.JobId, territoryId, state.StartedUtc, nowUtc);

        var firstTargetedUtc = state.FirstTargetedUtc == DateTime.MinValue
            ? state.StartedUtc
            : state.FirstTargetedUtc;
        var updatedSummary = new XAPeepPlayerSummary(
            state.PlayerKey,
            XAPeepData.FormatDisplayName(state.Name, state.HomeWorldId),
            state.HomeWorldId,
            state.JobId,
            state.PriorTargetCount + 1,
            state.PriorTargetDurationSeconds + duration.TotalSeconds,
            firstTargetedUtc,
            nowUtc,
            territoryId);

        playerSummaryCache[state.PlayerKey] = updatedSummary;
        RefreshHistoryCache();
    }

    private void RefreshHistoryCache()
    {
        recentPlayers = slaveDatabase.GetRecentXAPeepPlayers(200);
        recentSessions = slaveDatabase.GetRecentXAPeepSessions(200);

        foreach (var summary in recentPlayers)
            playerSummaryCache[summary.PlayerKey] = summary;
    }

    private void UpdateStatusText()
    {
        if (!enabled)
        {
            StatusText = "Disabled";
            return;
        }

        if (!clientState.IsLoggedIn)
        {
            StatusText = recentPlayers.Count > 0
                ? $"Enabled - logged out. {recentPlayers.Count} tracked players cached in history."
                : "Enabled - waiting for a character.";
            return;
        }

        try
        {
            if (objectTable.LocalPlayer == null)
            {
                StatusText = recentPlayers.Count > 0
                    ? $"Enabled - waiting for a character. {recentPlayers.Count} tracked players cached in history."
                    : "Enabled - waiting for a character.";
                return;
            }
        }
        catch (InvalidOperationException ex) when (IsNotOnMainThreadException(ex))
        {
            StatusText = recentPlayers.Count > 0
                ? $"Enabled - starting. {recentPlayers.Count} tracked players cached in history."
                : "Enabled - starting.";
            return;
        }

        if (IsDutyLoggingPaused())
        {
            StatusText = recentPlayers.Count > 0
                ? $"Enabled - duty logging paused. {recentPlayers.Count} tracked players in history."
                : "Enabled - duty logging paused.";
            return;
        }

        if (activeTargeters.Count == 0)
        {
            StatusText = recentPlayers.Count > 0
                ? $"Enabled - watching for targeters. {recentPlayers.Count} tracked players in history."
                : "Enabled - watching for targeters.";
            return;
        }

        StatusText = activeTargeters.Count == 1
            ? "Enabled - 1 player is targeting you."
            : $"Enabled - {activeTargeters.Count} players are targeting you.";
    }

    private void ShowCenterNotification(ActiveTargeterState state, DateTime nowUtc)
    {
        if (!configuration.XAPeepShowCenterNotification)
            return;

        var compactName = XAPeepData.FormatCompactName(state.Name);
        var totalTargetCount = state.PriorTargetCount + 1;
        centerNotificationText = totalTargetCount <= 1
            ? $"{compactName} is targeting you"
            : $"{compactName} is targeting you x{totalTargetCount}";
        centerNotificationUntilUtc = nowUtc + CenterNotificationDuration;
    }

    private void ShowChatNotification(ActiveTargeterState state)
    {
        if (!configuration.XAPeepShowChatNotification)
            return;

        var compactName = XAPeepData.FormatCompactName(state.Name);
        var totalTargetCount = state.PriorTargetCount + 1;
        var message = totalTargetCount <= 1
            ? $"{compactName} is targeting you."
            : $"{compactName} is targeting you x{totalTargetCount}.";
        Plugin.ChatGui.Print($"[XASlave] {message}");
    }

    private void TryPlayTargetSound(DateTime nowUtc)
    {
        if (lastSoundAtUtc != DateTime.MinValue && nowUtc - lastSoundAtUtc < SoundCooldown)
            return;

        if (TryPlayConfiguredSound())
            lastSoundAtUtc = nowUtc;
    }

    private unsafe bool TryPlayConfiguredSound()
    {
        var soundEffectValue = XAPeepData.GetSoundEffectValue(configuration.XAPeepSoundEffectId);
        if (soundEffectValue == 0)
            return false;

        if (XAPeepSoundPlayer.TryPlayAlert(configuration.XAPeepSoundEffectId, configuration.XAPeepSoundVolume, log))
            return true;

        try
        {
            UIGlobals.PlaySoundEffect(soundEffectValue);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] XA Peep could not play the configured in-game sound effect.");
            return false;
        }
    }

    private static bool IsNotOnMainThreadException(InvalidOperationException ex)
    {
        return ex.Message.Contains("Not on main thread!", StringComparison.OrdinalIgnoreCase);
    }

    private static void DrawOverlayTag(ImDrawListPtr drawList, Vector2 position, string text, uint textColor, uint backgroundColor, uint outlineColor)
    {
        var textSize = ImGui.CalcTextSize(text);
        var padding = ScaledVector(6f, 4f);
        var min = position;
        var max = position + textSize + (padding * 2f);
        drawList.AddRectFilled(min, max, backgroundColor, Scale(6f));
        drawList.AddRect(min, max, outlineColor, Scale(6f), ImDrawFlags.None, Scale(1f));
        drawList.AddText(position + padding, textColor, text);
    }

    private static void DrawCenteredOverlayTag(ImDrawListPtr drawList, Vector2 center, string text, uint textColor, uint backgroundColor, uint outlineColor)
    {
        var textSize = ImGui.CalcTextSize(text);
        var padding = ScaledVector(12f, 8f);
        var min = center - (textSize * 0.5f) - padding;
        var max = center + (textSize * 0.5f) + padding;
        drawList.AddRectFilled(min, max, backgroundColor, Scale(8f));
        drawList.AddRect(min, max, outlineColor, Scale(8f), ImDrawFlags.None, Scale(1f));
        drawList.AddText(center - (textSize * 0.5f), textColor, text);
    }

    private static float Scale(float value)
        => value * UiScale;

    private static Vector2 ScaledVector(float x, float y)
        => ImGuiHelpers.ScaledVector2(x, y);

    private sealed class ActiveTargeterState
    {
        public ActiveTargeterState(
            string playerKey,
            string name,
            uint homeWorldId,
            uint jobId,
            ulong gameObjectId,
            DateTime startedUtc,
            DateTime firstTargetedUtc,
            int priorTargetCount,
            double priorTargetDurationSeconds)
        {
            PlayerKey = playerKey;
            Name = name;
            HomeWorldId = homeWorldId;
            JobId = jobId;
            GameObjectId = gameObjectId;
            StartedUtc = startedUtc;
            LastSeenUtc = startedUtc;
            FirstTargetedUtc = firstTargetedUtc;
            PriorTargetCount = priorTargetCount;
            PriorTargetDurationSeconds = priorTargetDurationSeconds;
        }

        public string PlayerKey { get; }

        public string Name { get; private set; }

        public uint HomeWorldId { get; private set; }

        public uint JobId { get; private set; }

        public ulong GameObjectId { get; private set; }

        public DateTime StartedUtc { get; }

        public DateTime LastSeenUtc { get; private set; }

        public DateTime FirstTargetedUtc { get; private set; }

        public int PriorTargetCount { get; private set; }

        public double PriorTargetDurationSeconds { get; private set; }

        public void Refresh(IPlayerCharacter player, DateTime nowUtc)
        {
            Name = player.Name.TextValue;
            HomeWorldId = player.HomeWorld.RowId;
            JobId = player.ClassJob.RowId;
            GameObjectId = player.GameObjectId;
            LastSeenUtc = nowUtc;
            if (FirstTargetedUtc == DateTime.MinValue)
                FirstTargetedUtc = StartedUtc;
        }

        public TimeSpan GetDuration(DateTime nowUtc)
        {
            var effectiveEndUtc = nowUtc < StartedUtc ? StartedUtc : nowUtc;
            return effectiveEndUtc - StartedUtc;
        }

        public XAPeepLiveTargeterView ToView(DateTime nowUtc)
        {
            var duration = GetDuration(nowUtc);
            return new XAPeepLiveTargeterView(
                PlayerKey,
                XAPeepData.FormatDisplayName(Name, HomeWorldId),
                XAPeepData.FormatCompactName(Name),
                HomeWorldId,
                JobId,
                GameObjectId,
                StartedUtc,
                LastSeenUtc,
                duration,
                PriorTargetCount + 1,
                PriorTargetDurationSeconds + duration.TotalSeconds);
        }

        public XAPeepTrackedPlayerView ToTrackedPlayerView(DateTime nowUtc)
        {
            var duration = GetDuration(nowUtc);
            var firstTargetedUtc = FirstTargetedUtc == DateTime.MinValue ? StartedUtc : FirstTargetedUtc;
            return new XAPeepTrackedPlayerView(
                PlayerKey,
                XAPeepData.FormatDisplayName(Name, HomeWorldId),
                XAPeepData.FormatCompactName(Name),
                HomeWorldId,
                JobId,
                true,
                GameObjectId,
                firstTargetedUtc,
                LastSeenUtc,
                StartedUtc,
                PriorTargetCount + 1,
                PriorTargetDurationSeconds + duration.TotalSeconds);
        }

        public void ResetPersistedHistory()
        {
            FirstTargetedUtc = DateTime.MinValue;
            PriorTargetCount = 0;
            PriorTargetDurationSeconds = 0d;
        }
    }
}
