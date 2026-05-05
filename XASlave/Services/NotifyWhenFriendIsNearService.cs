using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

public sealed class NotifyWhenFriendIsNearService : IDisposable
{
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IToastGui toastGui;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;
    private readonly List<PlayerPatternRule> rules = [];
    private readonly Dictionary<string, DateTime> cooldownsByName = new(StringComparer.OrdinalIgnoreCase);
    private bool enabled;
    private bool subscribed;
    private int cooldownSeconds = 300;
    private DateTime lastScanUtc = DateTime.MinValue;

    public NotifyWhenFriendIsNearService(
        IFramework framework,
        IClientState clientState,
        IObjectTable objectTable,
        IToastGui toastGui,
        IChatGui chatGui,
        IPluginLog log)
    {
        this.framework = framework;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.toastGui = toastGui;
        this.chatGui = chatGui;
        this.log = log;
    }

    public string StatusText { get; private set; } = "Disabled";
    public string LastActionText { get; private set; } = "No actions yet.";
    public string LastMatchedPlayer { get; private set; } = "None";
    public int PatternCount => rules.Count;

    public static int NormalizeCooldownSeconds(int value)
    {
        return Math.Clamp(value, 10, 3600);
    }

    public void ApplyConfiguration(IEnumerable<string> patterns, int cooldownSeconds)
    {
        rules.Clear();
        foreach (var pattern in patterns
                     .Select(x => x.Trim())
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            rules.Add(PlayerPatternRule.Create(pattern));
        }

        this.cooldownSeconds = NormalizeCooldownSeconds(cooldownSeconds);

        if (enabled)
            StatusText = BuildStatusText();
    }

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
            return enabled;

        if (!value)
        {
            enabled = false;
            Unsubscribe();
            cooldownsByName.Clear();
            StatusText = "Disabled";
            return false;
        }

        enabled = true;
        Subscribe();
        StatusText = BuildStatusText();
        return true;
    }

    public void Dispose()
    {
        enabled = false;
        Unsubscribe();
        cooldownsByName.Clear();
    }

    private string BuildStatusText()
    {
        return $"Enabled - scanning for {rules.Count} friend pattern(s) with a {cooldownSeconds}s cooldown.";
    }

    private void Subscribe()
    {
        if (subscribed)
            return;

        framework.Update += OnFrameworkUpdate;
        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!subscribed)
            return;

        framework.Update -= OnFrameworkUpdate;
        subscribed = false;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!enabled || !clientState.IsLoggedIn || rules.Count == 0)
            return;

        if ((DateTime.UtcNow - lastScanUtc).TotalMilliseconds < 2000)
            return;

        lastScanUtc = DateTime.UtcNow;

        try
        {
            var localPlayer = objectTable.LocalPlayer;
            if (localPlayer == null)
                return;

            foreach (var obj in objectTable)
            {
                if (obj is not IPlayerCharacter player)
                    continue;

                if (player.GameObjectId == localPlayer.GameObjectId)
                    continue;

                var playerName = player.Name.TextValue;
                if (string.IsNullOrWhiteSpace(playerName))
                    continue;

                if (!rules.Any(rule => rule.IsMatch(playerName)))
                    continue;

                if (cooldownsByName.TryGetValue(playerName, out var nextAllowedUtc) && DateTime.UtcNow < nextAllowedUtc)
                    continue;

                cooldownsByName[playerName] = DateTime.UtcNow.AddSeconds(cooldownSeconds);
                LastMatchedPlayer = playerName;
                ShowLocalSystemNotification(playerName);
                LastActionText = $"Last action: detected friend '{playerName}' nearby at {DateTime.Now:HH:mm:ss}.";
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Notify When Friend Is Near failed during object-table scanning.");
        }
    }

    private void ShowLocalSystemNotification(string playerName)
    {
        var message = $"Friend nearby: {playerName}";
        toastGui.ShowNormal(message);
        chatGui.Print($"[XASlave] {message}");
    }

    private sealed class PlayerPatternRule
    {
        private readonly string pattern;
        private readonly Regex? regex;

        private PlayerPatternRule(string pattern, Regex? regex)
        {
            this.pattern = pattern;
            this.regex = regex;
        }

        public static PlayerPatternRule Create(string pattern)
        {
            if (pattern.Length > 2 && pattern[0] == '/' && pattern[^1] == '/')
            {
                try
                {
                    return new PlayerPatternRule(pattern, new Regex(pattern[1..^1], RegexOptions.IgnoreCase | RegexOptions.Compiled));
                }
                catch
                {
                    return new PlayerPatternRule(pattern, null);
                }
            }

            return new PlayerPatternRule(pattern, null);
        }

        public bool IsMatch(string value)
        {
            if (regex != null)
                return regex.IsMatch(value);

            return value.Equals(pattern, StringComparison.OrdinalIgnoreCase);
        }
    }
}
