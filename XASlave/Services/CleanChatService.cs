using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using XASlave.Data;

namespace XASlave.Services;

/// <summary>
/// Suppresses selected display records without rewriting their SeString payloads.
/// Instantiate/enable after MessageLog so operational observers retain the original.
/// Built-in announcement phrases cover English; other locales require explicit rules.
/// </summary>
public sealed class CleanChatService : IDisposable
{
    internal const int MaxRules = 20;
    internal const int MaxPatternLength = 512;
    internal const int MaxInputLength = 4096;
    internal const int RuleTimeoutMilliseconds = 5;
    internal const int MessageBudgetMilliseconds = 15;
    private const int MaxDiagnostics = 32;
    private readonly IChatGui chatGui;
    private readonly object syncRoot = new();
    private readonly List<string> diagnostics = new();
    private List<CompiledRule> rules = new();
    private CleanChatSettings settings = new();
    private bool enabled;
    private bool subscribed;
    private bool disposed;

    public CleanChatService(IChatGui chatGui)
    {
        this.chatGui = chatGui ?? throw new ArgumentNullException(nameof(chatGui));
    }

    public string StatusText
    {
        get
        {
            lock (syncRoot)
                return disposed ? "Disposed" : !enabled ? "Disabled" :
                    $"Enabled - English announcement filters; {rules.Count(x => !x.Quarantined)} valid regex rules ({(settings.RegexEnabled ? "on" : "off")}).";
        }
    }

    public IReadOnlyList<string> Diagnostics
    {
        get { lock (syncRoot) return diagnostics.ToArray(); }
    }

    public void ApplyConfiguration(CleanChatSettings? configuration)
    {
        lock (syncRoot)
        {
            if (disposed) return;
            var source = configuration ?? new CleanChatSettings();
            settings = new CleanChatSettings
            {
                HideWelcome = source.HideWelcome,
                HideEvents = source.HideEvents,
                HidePhishing = source.HidePhishing,
                HideRmt = source.HideRmt,
                RegexEnabled = source.RegexEnabled,
                RegexRules = source.RegexRules ?? string.Empty,
                RegexChannels = new HashSet<ushort>((source.RegexChannels ?? new HashSet<ushort>())
                    .Where(x => IsEligibleChannel((XivChatType)x))),
            };
            diagnostics.Clear();
            rules = new List<CompiledRule>();
            if (settings.RegexRules.Length > MaxRules * (MaxPatternLength + 2))
            {
                AddDiagnostic("Regex text exceeds the bounded settings size; shorten it and apply again.");
                return;
            }
            var ruleCount = 0;
            var lines = settings.RegexRules.Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                var pattern = lines[index].TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(pattern)) continue;
                if (++ruleCount > MaxRules)
                {
                    AddDiagnostic($"Only the first {MaxRules} nonblank regex rules are accepted.");
                    break;
                }
                if (pattern.Length > MaxPatternLength)
                {
                    AddDiagnostic($"Rule on line {index + 1} exceeds {MaxPatternLength} characters; quarantined until reapplied.");
                    continue;
                }
                try
                {
                    // Regex construction happens only when settings are applied, never
                    // on a chat callback. Inline (?-i) can opt into case sensitivity.
                    rules.Add(new CompiledRule(index + 1,
                        new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(RuleTimeoutMilliseconds))));
                }
                catch (ArgumentException)
                {
                    AddDiagnostic($"Rule on line {index + 1} is invalid; quarantined until reapplied.");
                }
            }
        }
    }

    public bool SetEnabled(bool value)
    {
        lock (syncRoot)
        {
            if (disposed) return false;
            if (value == enabled) return enabled;
            if (value && !subscribed)
            {
                chatGui.CheckMessageHandled += OnChatMessageHandled;
                subscribed = true;
            }
            else if (!value && subscribed)
            {
                chatGui.CheckMessageHandled -= OnChatMessageHandled;
                subscribed = false;
            }
            return enabled = value;
        }
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed) return;
            disposed = true;
            enabled = false;
            if (subscribed) chatGui.CheckMessageHandled -= OnChatMessageHandled;
            subscribed = false;
            rules.Clear();
        }
    }

    private void OnChatMessageHandled(IHandleableChatMessage message)
    {
        if (message.IsHandled) return;
        try
        {
            if (ShouldHide(message.LogKind, message.Message.TextValue))
                message.PreventOriginal();
        }
        catch (Exception)
        {
            // Filtering must fail open. Do not expose message contents in diagnostics.
            lock (syncRoot) AddDiagnostic("A chat filter failed; the affected message was preserved.");
        }
    }

    internal bool ShouldHide(XivChatType channel, string? text)
    {
        lock (syncRoot)
        {
            if (!enabled || disposed || !IsEligibleChannel(channel) || string.IsNullOrWhiteSpace(text)
                || text.Length > MaxInputLength || text.TrimStart().StartsWith("/xa onh ", StringComparison.OrdinalIgnoreCase))
                return false;
            if (IsSystemChannel(channel) && MatchesSystemAnnouncements(text)) return true;
            if (IsPlayerChannel(channel) && settings.HideRmt && MatchesKnownRmtAdvertisement(text)) return true;
            if (!settings.RegexEnabled || !settings.RegexChannels.Contains((ushort)channel)) return false;

            var startedAt = Stopwatch.GetTimestamp();
            foreach (var rule in rules)
            {
                if (rule.Quarantined) continue;
                // Leave enough budget for one entire bounded match. Exhaustion preserves
                // the message instead of trying all remaining rules on the game thread.
                if (ElapsedMilliseconds(startedAt) >= MessageBudgetMilliseconds - RuleTimeoutMilliseconds)
                {
                    AddDiagnostic("Regex message budget reached; remaining rules were skipped and the message preserved.");
                    return false;
                }
                try
                {
                    var matched = rule.Regex.IsMatch(text);
                    if (ElapsedMilliseconds(startedAt) > MessageBudgetMilliseconds)
                    {
                        rule.Quarantined = true;
                        AddDiagnostic($"Rule on line {rule.LineNumber} exceeded the message budget; quarantined until reapplied.");
                        return false;
                    }
                    if (matched) return true;
                }
                catch (RegexMatchTimeoutException)
                {
                    rule.Quarantined = true;
                    AddDiagnostic($"Rule on line {rule.LineNumber} timed out; quarantined until reapplied.");
                }
            }
            return false;
        }
    }

    private bool MatchesSystemAnnouncements(string text)
    {
        // A multi-line record is hidden only when every nonblank line is a selected
        // known announcement. Never remove unrelated text bundled with a banner.
        var foundAnnouncement = false;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (!(settings.HideWelcome && MatchesWelcome(line))
                && !(settings.HideEvents && MatchesEvent(line))
                && !(settings.HidePhishing && MatchesPhishing(line))) return false;
            foundAnnouncement = true;
        }
        return foundAnnouncement;
    }

    private static bool MatchesWelcome(string text)
        => text == "Welcome to FINAL FANTASY XIV!"
            || WorldData.Worlds.Any(world => text == $"Welcome to {world.Name}!");

    private static bool MatchesEvent(string text)
        => (text.StartsWith("* The ", StringComparison.Ordinal)
                && text.Contains(" seasonal event is underway until ", StringComparison.Ordinal)
                && HasOfficialLinkEnding(text, " Details: https://sqex.to/"))
            || (text.StartsWith("* The Moogle Treasure Trove - ", StringComparison.Ordinal)
                && text.Contains(" event is now underway! (Through ", StringComparison.Ordinal)
                && HasOfficialLinkEnding(text, " For more details, visit https://sqex.to/"));

    private static bool MatchesPhishing(string text)
        => text == "** Be Wary of Phishing Attempts via Tell **"
            || (text.StartsWith("If you receive a Tell containing a URL to a website from a random player, please check the contents carefully since there is a high possibility that it is a phishing site.", StringComparison.Ordinal)
                && HasOfficialLinkEnding(text, " Details: https://sqex.to/"));

    private static bool HasOfficialLinkEnding(string text, string marker)
    {
        var index = text.LastIndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return false;
        var code = text[(index + marker.Length)..].TrimEnd('.');
        return code.Length is > 0 and <= 32 && code.All(char.IsAsciiLetterOrDigit);
    }

    internal static bool MatchesKnownRmtAdvertisement(string text)
    {
        // Deliberately narrow: the ticket's normalized host, advertising banner and
        // price/stock/delivery cues together. Ordinary URLs and discussion survive.
        if (text.Contains('\r') || text.Contains('\n')) return false;
        var normalized = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Replace('0', 'o').Replace('|', 'l');
        const string host = "okaygold.com";
        var hostIndex = normalized.IndexOf(host, StringComparison.Ordinal);
        if (hostIndex < 2 || hostIndex > 64) return false;
        var prefix = normalized[..hostIndex].Trim();
        if (prefix.Length < 2 || prefix.Any(c => c != '-')) return false;
        var tail = normalized[(hostIndex + host.Length)..].TrimStart();
        if (!tail.StartsWith("--", StringComparison.Ordinal)) return false;
        var commercial = new string(tail.Where(char.IsAsciiLetterOrDigit).ToArray());
        return commercial.Contains("usd", StringComparison.Ordinal)
            && commercial.Contains("stock", StringComparison.Ordinal)
            && commercial.Contains("min", StringComparison.Ordinal)
            && commercial.Any(char.IsAsciiDigit);
    }

    internal static bool IsEligibleChannel(XivChatType channel) => IsSystemChannel(channel) || IsPlayerChannel(channel);
    private static bool IsSystemChannel(XivChatType channel) => channel is XivChatType.SystemMessage or XivChatType.Notice;
    private static bool IsPlayerChannel(XivChatType channel) => channel is XivChatType.Say or XivChatType.Shout or XivChatType.Yell or XivChatType.TellIncoming;
    private static double ElapsedMilliseconds(long startedAt) => (Stopwatch.GetTimestamp() - startedAt) * 1000d / Stopwatch.Frequency;

    private void AddDiagnostic(string message)
    {
        if (diagnostics.Count < MaxDiagnostics && !diagnostics.Contains(message)) diagnostics.Add(message);
    }

    private sealed class CompiledRule
    {
        internal readonly int LineNumber;
        internal readonly Regex Regex;
        internal bool Quarantined;
        internal CompiledRule(int lineNumber, Regex regex) { LineNumber = lineNumber; Regex = regex; }
    }
}
