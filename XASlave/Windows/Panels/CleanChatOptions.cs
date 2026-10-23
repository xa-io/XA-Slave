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
        (3, global::XASlave.Localization.UiLocale.Native("S65659145569B894B")), (10, global::XASlave.Localization.UiLocale.Native("S64F3CD066A1D864F")), (11, global::XASlave.Localization.UiLocale.Native("S196867CDD24A7560")),
        (13, global::XASlave.Localization.UiLocale.Native("SE2233C32A314D9E6")), (30, global::XASlave.Localization.UiLocale.Native("S938AF47B2794FA2A")), (57, global::XASlave.Localization.UiLocale.Native("S6725E7BBCD28F3A8")),
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
        var changed = global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S0DE052387C0E542E") + "##CleanChat"), ref welcome);
        changed |= global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("SE38E0CCF41D0A480") + "##CleanChat"), ref events);
        changed |= global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("SDE9CB229B2A556E1") + "##CleanChat"), ref phishing);
        changed |= global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("SD70D8F1C72F0EEBD") + "##CleanChat"), ref rmt);
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S12500999D7AA9ADB"));
        changed |= global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S707803931F5B7536") + "##CleanChat"), ref regex);
        if (regex)
        {
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S72BA5EBEF274F058"));
            foreach (var channel in CleanChatChannels)
            {
                var selected = settings.RegexChannels.Contains(channel.Id);
                if (global::XASlave.Localization.UiNative.Checkbox($"{channel.Label}##CleanChatChannel{channel.Id}", ref selected))
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
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S55870F770F8D413A"));
        global::XASlave.Localization.UiNative.InputTextMultiline((global::XASlave.Localization.UiLocale.Native("S4228AEB07C410FFC") + "##CleanChat"), ref cleanChatRegexDraft, 11000, new Vector2(-1, 125));
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S4220CBD4AD102E37") + "##CleanChat")))
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
        foreach (var diagnostic in plugin.CleanChat.LocalizedDiagnostics)
            ImGui.TextWrapped(diagnostic);
    }
}
