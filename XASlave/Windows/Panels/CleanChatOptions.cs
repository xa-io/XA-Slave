using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using XASlave.Data;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private string cleanChatRegexDraft = string.Empty;
    private string? cleanChatLastSavedRegex;
    private static readonly (ushort Id, string Label)[] CleanChatChannels =
    [
        (3, "Notice"), (10, "Say"), (11, "Shout"),
        (13, "Incoming Tell"), (30, "Yell"), (57, "System"),
    ];

    private void DrawCleanChatOptions()
    {
        var cfg = plugin.Configuration;
        var settings = cfg.CleanChatSettings ??= new CleanChatSettings();
        settings.RegexChannels ??= new HashSet<ushort>();
        var welcome = settings.HideWelcome;
        var events = settings.HideEvents;
        var phishing = settings.HidePhishing;
        var rmt = settings.HideRmt;
        var regex = settings.RegexEnabled;
        var changed = ImGui.Checkbox("Welcome messages##CleanChat", ref welcome);
        changed |= ImGui.Checkbox("Event announcements##CleanChat", ref events);
        changed |= ImGui.Checkbox("Phishing reminder announcements##CleanChat", ref phishing);
        changed |= ImGui.Checkbox("Recognized RMT advertising##CleanChat", ref rmt);
        ImGui.TextWrapped("Announcement defaults match English system notices. Other messages and standalone blank lines remain visible. Filtering affects future chat only.");
        changed |= ImGui.Checkbox("Enable custom regex##CleanChat", ref regex);
        if (regex)
        {
            ImGui.TextWrapped("Regex channels (error and GM channels are always excluded):");
            foreach (var channel in CleanChatChannels)
            {
                var selected = settings.RegexChannels.Contains(channel.Id);
                if (ImGui.Checkbox($"{channel.Label}##CleanChatChannel{channel.Id}", ref selected))
                {
                    if (selected) settings.RegexChannels.Add(channel.Id);
                    else settings.RegexChannels.Remove(channel.Id);
                    changed = true;
                }
            }
        }

        if (cleanChatLastSavedRegex != (settings.RegexRules ?? string.Empty))
        {
            cleanChatRegexDraft = settings.RegexRules ?? string.Empty;
            cleanChatLastSavedRegex = cleanChatRegexDraft;
        }
        ImGui.TextWrapped("One .NET regex per line, up to 20 rules of 512 characters each. Matching ignores case. Invalid or timed-out rules are disabled until reapplied; overly long messages are left visible.");
        ImGui.InputTextMultiline("Rules##CleanChat", ref cleanChatRegexDraft, 11000, new Vector2(-1, 125));
        if (ImGui.Button("Apply and Save Regex##CleanChat"))
        {
            settings.RegexRules = cleanChatRegexDraft;
            cleanChatLastSavedRegex = cleanChatRegexDraft;
            changed = true;
        }
        if (changed)
        {
            settings.HideWelcome = welcome;
            settings.HideEvents = events;
            settings.HidePhishing = phishing;
            settings.HideRmt = rmt;
            settings.RegexEnabled = regex;
            plugin.CleanChat.ApplyConfiguration(settings);
            cfg.Save();
        }
        foreach (var diagnostic in plugin.CleanChat.Diagnostics)
            ImGui.TextWrapped(diagnostic);
    }
}
