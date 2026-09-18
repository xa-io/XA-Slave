using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using XASlave.Data;

namespace XASlave.Services;

internal readonly record struct NearbyPlayerObservation(string Name, uint HomeWorld, string WorldName, uint OnlineStatus, ulong ObjectId);

internal sealed class NearbyPlayerRuleSnapshot
{
    internal const int SchemaVersion = 1;
    internal static readonly Guid LegacyGroupId = new("139c3e53-94f6-4e3a-ae0e-de68cc67d45d");
    internal ReadOnlyCollection<NearbyPlayerCompiledRule> Rules { get; }
    internal bool Migrated { get; }
    private NearbyPlayerRuleSnapshot(List<NearbyPlayerCompiledRule> rules, bool migrated)
    { Rules = rules.AsReadOnly(); Migrated = migrated; }

    internal static NearbyPlayerRuleSnapshot Create(int? schema, IEnumerable<NearbyPlayerNotificationRule>? configured,
        IEnumerable<string> legacyPatterns, int legacyCooldown, bool explicitApply)
    {
        if (schema.HasValue && schema.Value != SchemaVersion) throw new ArgumentException("Unsupported player-notification schema; existing rules are retained.");
        var migrated = !schema.HasValue;
        if (!migrated && configured == null) throw new ArgumentException("Versioned player-notification rules are missing.");
        var source = migrated ? Migrate(legacyPatterns, legacyCooldown) : configured!;
        var result = new List<NearbyPlayerCompiledRule>();
        var ids = new HashSet<Guid>();
        var legacyKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int? legacyGroupCooldown = null;
        foreach (var input in source)
        {
            if (input == null) throw new ArgumentException("Player-notification rule cannot be null.");
            var rule = Clone(input);
            if (rule.RuleId == Guid.Empty) rule.RuleId = Guid.NewGuid();
            if (rule.RuleId == LegacyGroupId || !ids.Add(rule.RuleId)) throw new ArgumentException("Player-notification rule IDs must be unique.");
            if (!Enum.IsDefined(rule.NameMode) || !Enum.IsDefined(rule.TriggerMode) || !Enum.IsDefined(rule.EntryMode))
                throw new ArgumentException("Unknown player-notification rule mode.");
            if (rule.Pattern.Length > 512) throw new ArgumentException("Player pattern exceeds 512 characters.");
            if (rule.NameMode != NearbyPlayerNameMode.Any && string.IsNullOrWhiteSpace(rule.Pattern)) throw new ArgumentException("Exact/regex player patterns cannot be empty.");
            rule.CooldownSeconds = Math.Clamp(rule.CooldownSeconds, 10, 3600);
            rule.HomeWorldIds = rule.HomeWorldIds.Distinct().OrderBy(x => x).ToList();
            rule.TerritoryIds = rule.TerritoryIds.Distinct().OrderBy(x => x).ToList();
            rule.OnlineStatusIds = rule.OnlineStatusIds.Distinct().OrderBy(x => x).ToList();
            if (!explicitApply) rule.CommandsEnabled = false;
            rule.Commands = NearbyPlayerCommandTemplate.SplitEntries(rule.Commands).ToList();
            if (rule.LegacyGroup)
            {
                if (legacyGroupCooldown.HasValue && legacyGroupCooldown.Value != rule.CooldownSeconds)
                    throw new ArgumentException("Legacy notification patterns must share one cooldown.");
                legacyGroupCooldown = rule.CooldownSeconds;
                // Group membership is the compatibility contract. Independent filters/
                // actions require the explicit UI operation that clears LegacyGroup.
                if (rule.NameMode == NearbyPlayerNameMode.Any || rule.HomeWorldIds.Count != 0 || rule.TerritoryIds.Count != 0 || rule.OnlineStatusIds.Count != 0
                    || rule.TriggerMode != NearbyPlayerTriggerMode.Periodic || !rule.LocalChat || !rule.Toast || rule.Notification || rule.Speech || rule.CommandsEnabled == true || rule.Commands.Count != 0)
                    throw new ArgumentException("Convert a legacy pattern to an independent rule before adding advanced filters/actions.");
                var key = rule.NameMode + "\0" + rule.Pattern + "\0" + rule.Enabled + "\0" + rule.CooldownSeconds;
                if (!legacyKeys.Add(key)) continue;
            }
            result.Add(new NearbyPlayerCompiledRule(rule));
        }
        return new(result, migrated);
    }

    internal List<NearbyPlayerNotificationRule> Export() => Rules.Select(rule => rule.Export()).ToList();
    internal List<string> LegacyPatterns() => Rules.Where(rule => rule.LegacyGroup && rule.ConfiguredEnabled).Select(rule =>
        rule.NameMode == NearbyPlayerNameMode.Regex ? "/" + rule.Pattern + "/" : rule.Pattern).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static IEnumerable<NearbyPlayerNotificationRule> Migrate(IEnumerable<string> patterns, int cooldown)
    {
        foreach (var pattern in patterns.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var rule = new NearbyPlayerNotificationRule
            {
                Label = "Legacy friend pattern", Pattern = pattern, LegacyGroup = true,
                TriggerMode = NearbyPlayerTriggerMode.Periodic, CooldownSeconds = Math.Clamp(cooldown, 10, 3600),
                LocalChat = true, Toast = true, Notification = false, Speech = false, CommandsEnabled = false,
            };
            if (pattern.Length > 2 && pattern[0] == '/' && pattern[^1] == '/')
            {
                try
                {
                    _ = new Regex(pattern[1..^1], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(5));
                    rule.NameMode = NearbyPlayerNameMode.Regex; rule.Pattern = pattern[1..^1];
                }
                catch (ArgumentException) { rule.MigrationNote = "Invalid legacy regex preserved as an exact literal pattern."; }
            }
            yield return rule;
        }
    }

    internal static NearbyPlayerNotificationRule Clone(NearbyPlayerNotificationRule rule) => new()
    {
        RuleId = rule.RuleId, Enabled = rule.Enabled, Label = rule.Label ?? string.Empty, NameMode = rule.NameMode,
        Pattern = rule.Pattern ?? string.Empty, HomeWorldIds = (rule.HomeWorldIds ?? new()).ToList(),
        TerritoryIds = (rule.TerritoryIds ?? new()).ToList(), OnlineStatusIds = (rule.OnlineStatusIds ?? new()).ToList(),
        TriggerMode = rule.TriggerMode, CooldownSeconds = rule.CooldownSeconds, LocalChat = rule.LocalChat, Toast = rule.Toast,
        Notification = rule.Notification, Speech = rule.Speech, CommandsEnabled = rule.CommandsEnabled,
        EntryMode = rule.EntryMode, Commands = (rule.Commands ?? new()).ToList(), LegacyGroup = rule.LegacyGroup,
        MigrationNote = rule.MigrationNote ?? string.Empty,
    };
}

internal sealed class NearbyPlayerCompiledRule
{
    private readonly NearbyPlayerNotificationRule rule;
    private readonly Regex? regex;
    private readonly HashSet<uint> homeWorlds, territories, statuses;
    private bool suspended;
    internal ReadOnlyCollection<NearbyPlayerCommandTemplate> Templates { get; }
    internal Guid Id => rule.RuleId;
    internal Guid CooldownGroup => rule.LegacyGroup ? NearbyPlayerRuleSnapshot.LegacyGroupId : rule.RuleId;
    internal bool Enabled => rule.Enabled && !suspended;
    internal bool ConfiguredEnabled => rule.Enabled;
    internal bool LegacyGroup => rule.LegacyGroup;
    internal NearbyPlayerNameMode NameMode => rule.NameMode;
    internal string Pattern => rule.Pattern;
    internal NearbyPlayerTriggerMode Trigger => rule.TriggerMode;
    internal int CooldownSeconds => rule.CooldownSeconds;
    internal bool LocalChat => rule.LocalChat;
    internal bool Toast => rule.Toast;
    internal bool Notification => rule.Notification;
    internal bool Speech => rule.Speech;
    internal bool CommandsEnabled => rule.CommandsEnabled == true;
    internal string Diagnostic { get; private set; } = string.Empty;

    internal NearbyPlayerCompiledRule(NearbyPlayerNotificationRule rule)
    {
        this.rule = NearbyPlayerRuleSnapshot.Clone(rule);
        homeWorlds = new HashSet<uint>(rule.HomeWorldIds);
        territories = new HashSet<uint>(rule.TerritoryIds);
        statuses = new HashSet<uint>(rule.OnlineStatusIds);
        if (rule.NameMode == NearbyPlayerNameMode.Regex)
            regex = new Regex(rule.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(5));
        Templates = rule.Commands.Select(command => NearbyPlayerCommandTemplate.Parse(command, rule.EntryMode)).ToList().AsReadOnly();
        // Parsing validates syntax/literal size even when disabled. Actual actor
        // expansion validates the complete UTF-8 size before actions are queued.
    }

    internal bool Matches(NearbyPlayerObservation player, uint territory)
    {
        if (!Enabled || player.HomeWorld == 0 || player.HomeWorld == ushort.MaxValue || string.IsNullOrWhiteSpace(player.Name) || string.IsNullOrWhiteSpace(player.WorldName)) return false;
        if (homeWorlds.Count != 0 && !homeWorlds.Contains(player.HomeWorld)) return false;
        if (territories.Count != 0 && !territories.Contains(territory)) return false;
        if (statuses.Count != 0 && !statuses.Contains(player.OnlineStatus)) return false;
        if (rule.NameMode == NearbyPlayerNameMode.Any) return true;
        if (regex == null) return string.Equals(rule.Pattern, player.Name, StringComparison.OrdinalIgnoreCase);
        try { return regex.IsMatch(player.Name); }
        catch (RegexMatchTimeoutException)
        {
            suspended = true;
            Diagnostic = "Regex matching timed out; this rule is suspended until configuration is reapplied.";
            return false;
        }
    }

    internal NearbyPlayerNotificationRule Export() => NearbyPlayerRuleSnapshot.Clone(rule);
}
