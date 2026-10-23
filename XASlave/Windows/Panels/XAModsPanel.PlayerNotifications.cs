using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using XASlave.Data;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private NearbyPlayerNotificationRule? nearbyRuleDraft;
    private string nearbyRuleWorlds = string.Empty, nearbyRuleTerritories = string.Empty, nearbyRuleStatuses = string.Empty;
    private string nearbyRuleCommands = string.Empty, nearbyRuleError = string.Empty;
    private global::XASlave.Localization.UiMessage? nearbyRuleErrorUiMessage = global::XASlave.Localization.UiMessage.Raw(string.Empty);

    private void DrawNearbyPlayerAdvancedRules()
    {
        if (!global::XASlave.Localization.UiNative.TreeNode((global::XASlave.Localization.UiLocale.Native("S8E5401450F6AF343") + "##NearbyPlayerRules"))) return;
        try
        {
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S2B0F93072F299190"));
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S62942C266B79F701"));
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SA27CFF51A2E03E61") + "##NearbyPlayerAdd"))) BeginNearbyRuleEdit(new NearbyPlayerNotificationRule());
            foreach (var rule in (plugin.Configuration.NotifyWhenFriendIsNearRules ?? []).ToArray())
            {
                if (rule == null) { ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S8EBB77FB1F458B06")); continue; }
                ImGui.PushID(rule.RuleId.ToString());
                try
                {
                    if (global::XASlave.Localization.UiNative.SmallButton(global::XASlave.Localization.UiLocale.Native("S464C4FFD019E1E96"))) BeginNearbyRuleEdit(rule);
                    ImGui.SameLine();
                    if (global::XASlave.Localization.UiNative.SmallButton(global::XASlave.Localization.UiLocale.Native("SC3812FC4ACB861D5"))) SaveNearbyRule(rule.RuleId, null);
                    ImGui.SameLine();
                    ImGui.TextUnformatted(global::XASlave.Localization.UiLocale.FormatKey("S41207716DC24D33B", (rule.Enabled ? global::XASlave.Localization.UiLocale.Get("S130011756125313C") : global::XASlave.Localization.UiLocale.Get("SCA7981B46ECF2C17")), rule.Label, NearbyNameModeMessage(rule.NameMode).Display, (rule.LegacyGroup ? global::XASlave.Localization.UiLocale.Get("S0A036D739C3F0956") : NearbyTriggerModeMessage(rule.TriggerMode).Display)));
                    if (!string.IsNullOrWhiteSpace(rule.MigrationNote)) ImGui.TextWrapped(NearbyPlayerRuleSnapshot.MigrationNoteMessage(rule).Display);
                }
                finally { ImGui.PopID(); }
            }
            if (nearbyRuleDraft != null) DrawNearbyRuleDraft();
            if (nearbyRuleError.Length != 0) ImGui.TextWrapped(global::XASlave.Localization.UiMessage.Resolve(ref nearbyRuleErrorUiMessage, nearbyRuleError));
        }
        finally { ImGui.TreePop(); }
    }


    private static global::XASlave.Localization.UiMessage NearbyNameModeMessage(NearbyPlayerNameMode value) => value switch
    {
        NearbyPlayerNameMode.Any => global::XASlave.Localization.UiMessage.Key("S2B505597DAA736F1"),
        NearbyPlayerNameMode.Exact => global::XASlave.Localization.UiMessage.Key("SB5AEE780C3BF589D"),
        NearbyPlayerNameMode.Regex => global::XASlave.Localization.UiMessage.Key("S0F2712521D504431"),
        _ => global::XASlave.Localization.UiMessage.Value(value),
    };

    private static global::XASlave.Localization.UiMessage NearbyTriggerModeMessage(NearbyPlayerTriggerMode value) => value switch
    {
        NearbyPlayerTriggerMode.Appearance => global::XASlave.Localization.UiMessage.Key("S3907FA7F80722A6F"),
        NearbyPlayerTriggerMode.Periodic => global::XASlave.Localization.UiMessage.Key("SC7280FC72DDE5683"),
        _ => global::XASlave.Localization.UiMessage.Value(value),
    };

    private void BeginNearbyRuleEdit(NearbyPlayerNotificationRule rule)
    {
        nearbyRuleDraft = NearbyPlayerRuleSnapshot.Clone(rule);
        nearbyRuleWorlds = string.Join(",", nearbyRuleDraft.HomeWorldIds);
        nearbyRuleTerritories = string.Join(",", nearbyRuleDraft.TerritoryIds);
        nearbyRuleStatuses = string.Join(",", nearbyRuleDraft.OnlineStatusIds);
        nearbyRuleCommands = string.Join("\n", nearbyRuleDraft.Commands);
        nearbyRuleError = global::XASlave.Localization.UiMessage.Store(ref nearbyRuleErrorUiMessage, global::XASlave.Localization.UiMessage.Raw(string.Empty));
    }

    private void DrawNearbyRuleDraft()
    {
        var draft = nearbyRuleDraft!;
        ImGui.Separator();
        if (draft.LegacyGroup)
        {
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S660EBF881A9067CE"));
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SC513540DB4EC561E") + "##NearbyConvert"))) draft.LegacyGroup = false;
        }
        ImGui.BeginDisabled(draft.LegacyGroup);
        try
        {
            var enabled = draft.Enabled;
            if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S57993E10706C49EB") + "##NearbyRuleEnabled"), ref enabled)) draft.Enabled = enabled;
            var label = draft.Label;
            if (global::XASlave.Localization.UiNative.InputText((global::XASlave.Localization.UiLocale.Native("S0E66373F45DCF3DD") + "##NearbyRuleLabel"), ref label, 256)) draft.Label = label;
            var mode = (int)draft.NameMode;
            if (global::XASlave.Localization.UiNative.Combo((global::XASlave.Localization.UiLocale.Native("S9156FCDC239FC167") + "##NearbyNameMode"), ref mode, new[] { global::XASlave.Localization.UiLocale.Native("S2B505597DAA736F1"), global::XASlave.Localization.UiLocale.Native("SA678BE91B8A6A92D"), global::XASlave.Localization.UiLocale.Native("SF5C7C485F9ECB4F4") }, 3)) draft.NameMode = (NearbyPlayerNameMode)mode;
            var pattern = draft.Pattern;
            if (global::XASlave.Localization.UiNative.InputText((global::XASlave.Localization.UiLocale.Native("S4288ADE73FF9D328") + "##NearbyNamePattern"), ref pattern, 2048)) draft.Pattern = pattern;
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SE493000F57C614F9"));
            global::XASlave.Localization.UiNative.InputText((global::XASlave.Localization.UiLocale.Native("SCB1D9D50EA73DCD0") + "##NearbyWorlds"), ref nearbyRuleWorlds, NearbyFilterBuffer(nearbyRuleWorlds));
            global::XASlave.Localization.UiNative.InputText((global::XASlave.Localization.UiLocale.Native("SCE968F00E8AB81A6") + "##NearbyTerritories"), ref nearbyRuleTerritories, NearbyFilterBuffer(nearbyRuleTerritories));
            global::XASlave.Localization.UiNative.InputText((global::XASlave.Localization.UiLocale.Native("S18CB438BC5512B40") + "##NearbyStatuses"), ref nearbyRuleStatuses, NearbyFilterBuffer(nearbyRuleStatuses));
            DrawNearbyIds(global::XASlave.Localization.UiLocale.Get("S9FDC811854E96F1F"), nearbyRuleWorlds, 0);
            DrawNearbyIds(global::XASlave.Localization.UiLocale.Get("SCEADD2AF477E0FE1"), nearbyRuleTerritories, 1);
            DrawNearbyIds(global::XASlave.Localization.UiLocale.Get("S4BD42EDED8649262"), nearbyRuleStatuses, 2);
            var trigger = (int)draft.TriggerMode;
            if (global::XASlave.Localization.UiNative.Combo((global::XASlave.Localization.UiLocale.Native("S8B9C643731C94119") + "##NearbyTrigger"), ref trigger, new[] { global::XASlave.Localization.UiLocale.Native("S3907FA7F80722A6F"), global::XASlave.Localization.UiLocale.Native("SC7280FC72DDE5683") }, 2)) draft.TriggerMode = (NearbyPlayerTriggerMode)trigger;
            var cooldown = draft.CooldownSeconds;
            if (global::XASlave.Localization.UiNative.InputInt((global::XASlave.Localization.UiLocale.Native("S20DDE92B9CF6D44D") + "##NearbyCooldown"), ref cooldown)) draft.CooldownSeconds = cooldown;
            var chat = draft.LocalChat; if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S1D979518DA0E679C") + "##NearbyChat"), ref chat)) draft.LocalChat = chat;
            ImGui.SameLine(); var toast = draft.Toast; if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S34B86033459D0EA5") + "##NearbyToast"), ref toast)) draft.Toast = toast;
            var notification = draft.Notification; if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S523CE961CF64B38D") + "##NearbyNotification"), ref notification)) draft.Notification = notification;
            var speech = draft.Speech; if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S9D358BD617A66FE7") + "##NearbySpeech"), ref speech)) draft.Speech = speech;
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S305A9B3FB13761F2"));
            var commands = draft.CommandsEnabled == true;
            if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S3ED942C447286AA2") + "##NearbyCommandsEnabled"), ref commands)) draft.CommandsEnabled = commands;
            var entryMode = (int)draft.EntryMode;
            if (global::XASlave.Localization.UiNative.Combo((global::XASlave.Localization.UiLocale.Native("S4A17440263CAD8E5") + "##NearbyEntryMode"), ref entryMode, new[] { global::XASlave.Localization.UiLocale.Native("S85DEC3AEE05FE0D3"), global::XASlave.Localization.UiLocale.Native("SAF2D932D49141D4A") }, 2)) draft.EntryMode = (NearbyPlayerEntryMode)entryMode;
            if (commands && draft.NameMode == NearbyPlayerNameMode.Any)
                ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("SCF0DA18A33E0EAAE"));
            global::XASlave.Localization.UiNative.InputTextMultiline((global::XASlave.Localization.UiLocale.Native("S830EE8CE2DCA846E") + "##NearbyCommands"), ref nearbyRuleCommands, 32768, new Vector2(-1, 100));
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S36FFD687E9580AE2"));
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SF51DF26A2C44A360") + "##NearbyRulePreview"))) ValidateNearbyDraft();
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S6C07A3AD4FE9E356") + "##NearbyRuleApply")) && ValidateNearbyDraft())
                SaveNearbyRule(draft.RuleId, draft);
        }
        finally { ImGui.EndDisabled(); }
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SBD0B7BE215CE3FBF") + "##NearbyRuleClose"))) nearbyRuleDraft = null;
    }

    private bool ValidateNearbyDraft()
    {
        try
        {
            var draft = nearbyRuleDraft!;
            draft.HomeWorldIds = ParseNearbyIds(nearbyRuleWorlds);
            draft.TerritoryIds = ParseNearbyIds(nearbyRuleTerritories);
            draft.OnlineStatusIds = ParseNearbyIds(nearbyRuleStatuses);
            draft.Commands = NearbyPlayerCommandTemplate.SplitEntries(new[] { nearbyRuleCommands }).ToList();
            var snapshot = NearbyPlayerRuleSnapshot.Create(1, new[] { draft }, [], draft.CooldownSeconds, explicitApply: true);
            try
            {
                var preview = snapshot.Rules[0].Templates.Select(template => template.Expand("Example Player", "Example World"));
                nearbyRuleError = global::XASlave.Localization.UiMessage.Store(ref nearbyRuleErrorUiMessage, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("SAFBD62AD717CC835"), ((draft.Commands.Count == 0 ? global::XASlave.Localization.UiMessage.Key("S532E121A8FCD009F") : global::XASlave.Localization.UiMessage.Value(string.Join(" | ", preview))))));
            }
            catch (ArgumentException) { nearbyRuleError = global::XASlave.Localization.UiMessage.Store(ref nearbyRuleErrorUiMessage, global::XASlave.Localization.UiMessage.Key("S8D66837657B5BD7D")); }
            return true;
        }
        catch (ArgumentException error) { nearbyRuleError = global::XASlave.Localization.UiMessage.Store(ref nearbyRuleErrorUiMessage, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("S04B5A3FE09BBA015"), global::XASlave.Localization.UiMessage.FromException(error))); return false; }
    }

    private void SaveNearbyRule(Guid id, NearbyPlayerNotificationRule? replacement)
    {
        try
        {
            if (plugin.Configuration.NotifyWhenFriendIsNearSchemaVersion != NearbyPlayerRuleSnapshot.SchemaVersion)
                throw new ArgumentException("Unsupported or unmigrated player-notification schema; existing rules were not overwritten.");
            var rules = (plugin.Configuration.NotifyWhenFriendIsNearRules ?? []).Select(NearbyPlayerRuleSnapshot.Clone).ToList();
            var index = rules.FindIndex(rule => rule.RuleId == id);
            if (replacement == null) { if (index >= 0) rules.RemoveAt(index); }
            else if (index >= 0) rules[index] = NearbyPlayerRuleSnapshot.Clone(replacement);
            else rules.Add(NearbyPlayerRuleSnapshot.Clone(replacement));
            var validated = NearbyPlayerRuleSnapshot.Create(1, rules, [], plugin.Configuration.NotifyWhenFriendIsNearCooldownSeconds, explicitApply: true);
            plugin.PublishNearbyPlayerRules(validated, plugin.Configuration.NotifyWhenFriendIsNearCooldownSeconds, save: true, changedRule: id);
            nearbyRuleError = global::XASlave.Localization.UiMessage.Store(ref nearbyRuleErrorUiMessage, global::XASlave.Localization.UiMessage.Key("SAEDDA4923BF029EC"));
            nearbyRuleDraft = null;
        }
        catch (ArgumentException error) { nearbyRuleError = global::XASlave.Localization.UiMessage.Store(ref nearbyRuleErrorUiMessage, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("SA1E861292DA548AC"), global::XASlave.Localization.UiMessage.FromException(error))); }
    }

    private static int NearbyFilterBuffer(string text) => checked((int)Math.Max(4096L, Encoding.UTF8.GetByteCount(text) + 4096L));

    private static List<uint> ParseNearbyIds(string text)
    {
        var result = new List<uint>();
        foreach (var part in text.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) throw new ArgumentException("Filter IDs must be unsigned decimal numbers.");
            result.Add(id);
        }
        return result.Distinct().OrderBy(id => id).ToList();
    }

    private static void DrawNearbyIds(string label, string text, int kind)
    {
        try
        {
            var ids = ParseNearbyIds(text);
            if (ids.Count == 0) { ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("SE461F16CE5F2320E", label)); return; }
            foreach (var id in ids)
            {
                string? name = null;
                try
                {
                    if (kind == 0) name = Plugin.DataManager.GetExcelSheet<World>()?.GetRowOrDefault(id)?.Name.ExtractText();
                    else if (kind == 1) name = Plugin.DataManager.GetExcelSheet<TerritoryType>()?.GetRowOrDefault(id)?.PlaceName.Value.Name.ExtractText();
                    else name = Plugin.DataManager.GetExcelSheet<OnlineStatus>()?.GetRowOrDefault(id)?.Name.ExtractText();
                }
                catch { /* Preserve numeric filters when labels cannot be resolved. */ }
                ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("S31475DA27D6F95FC", label, id, (string.IsNullOrWhiteSpace(name) ? global::XASlave.Localization.UiLocale.Get("S17E30A52B4901E08") : name)));
            }
        }
        catch (ArgumentException error) { ImGui.TextWrapped(global::XASlave.Localization.UiMessage.FromException(error).Display); }
    }
}
