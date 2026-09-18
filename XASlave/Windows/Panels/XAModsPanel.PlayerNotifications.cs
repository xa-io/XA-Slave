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

    private void DrawNearbyPlayerAdvancedRules()
    {
        if (!ImGui.TreeNode("Advanced player rules##NearbyPlayerRules")) return;
        try
        {
            ImGui.TextWrapped("Rules match name, home world, territory and online status together. Empty ID lists mean Any. Appearance requires two complete unmatched scans to rearm; Periodic can notify again after its cooldown.");
            ImGui.TextWrapped("Legacy patterns share one periodic cooldown and chat/toast notification. Rich rules are included in current presets; older plugin versions understand only the legacy pattern projection.");
            if (ImGui.Button("Add rule##NearbyPlayerAdd")) BeginNearbyRuleEdit(new NearbyPlayerNotificationRule());
            foreach (var rule in (plugin.Configuration.NotifyWhenFriendIsNearRules ?? []).ToArray())
            {
                if (rule == null) { ImGui.TextWrapped("Invalid rule record: restore a valid player-notification preset."); continue; }
                ImGui.PushID(rule.RuleId.ToString());
                try
                {
                    if (ImGui.SmallButton("Edit")) BeginNearbyRuleEdit(rule);
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Remove")) SaveNearbyRule(rule.RuleId, null);
                    ImGui.SameLine();
                    ImGui.TextUnformatted($"{(rule.Enabled ? "On" : "Off")} | {rule.Label} | {rule.NameMode} | {(rule.LegacyGroup ? "Legacy group" : rule.TriggerMode.ToString())}");
                    if (!string.IsNullOrWhiteSpace(rule.MigrationNote)) ImGui.TextWrapped(rule.MigrationNote);
                }
                finally { ImGui.PopID(); }
            }
            if (nearbyRuleDraft != null) DrawNearbyRuleDraft();
            if (nearbyRuleError.Length != 0) ImGui.TextWrapped(nearbyRuleError);
        }
        finally { ImGui.TreePop(); }
    }

    private void BeginNearbyRuleEdit(NearbyPlayerNotificationRule rule)
    {
        nearbyRuleDraft = NearbyPlayerRuleSnapshot.Clone(rule);
        nearbyRuleWorlds = string.Join(",", nearbyRuleDraft.HomeWorldIds);
        nearbyRuleTerritories = string.Join(",", nearbyRuleDraft.TerritoryIds);
        nearbyRuleStatuses = string.Join(",", nearbyRuleDraft.OnlineStatusIds);
        nearbyRuleCommands = string.Join("\n", nearbyRuleDraft.Commands);
        nearbyRuleError = string.Empty;
    }

    private void DrawNearbyRuleDraft()
    {
        var draft = nearbyRuleDraft!;
        ImGui.Separator();
        if (draft.LegacyGroup)
        {
            ImGui.TextWrapped("This pattern belongs to the shared legacy group. Use the simple pattern controls, or explicitly convert it before changing advanced behavior.");
            if (ImGui.Button("Convert to independent rule##NearbyConvert")) draft.LegacyGroup = false;
        }
        ImGui.BeginDisabled(draft.LegacyGroup);
        try
        {
            var enabled = draft.Enabled;
            if (ImGui.Checkbox("Rule enabled##NearbyRuleEnabled", ref enabled)) draft.Enabled = enabled;
            var label = draft.Label;
            if (ImGui.InputText("Label##NearbyRuleLabel", ref label, 256)) draft.Label = label;
            var mode = (int)draft.NameMode;
            if (ImGui.Combo("Name mode##NearbyNameMode", ref mode, new[] { "Any", "Exact (ignore case)", "Regex (ignore case)" }, 3)) draft.NameMode = (NearbyPlayerNameMode)mode;
            var pattern = draft.Pattern;
            if (ImGui.InputText("Pattern##NearbyNamePattern", ref pattern, 2048)) draft.Pattern = pattern;
            ImGui.TextDisabled("Regex uses a 5ms timeout; patterns are limited to 512 characters.");
            ImGui.InputText("Home world IDs##NearbyWorlds", ref nearbyRuleWorlds, NearbyFilterBuffer(nearbyRuleWorlds));
            ImGui.InputText("Territory IDs##NearbyTerritories", ref nearbyRuleTerritories, NearbyFilterBuffer(nearbyRuleTerritories));
            ImGui.InputText("Online status IDs##NearbyStatuses", ref nearbyRuleStatuses, NearbyFilterBuffer(nearbyRuleStatuses));
            DrawNearbyIds("Home worlds", nearbyRuleWorlds, 0);
            DrawNearbyIds("Territories", nearbyRuleTerritories, 1);
            DrawNearbyIds("Online statuses", nearbyRuleStatuses, 2);
            var trigger = (int)draft.TriggerMode;
            if (ImGui.Combo("Trigger##NearbyTrigger", ref trigger, new[] { "Appearance", "Periodic" }, 2)) draft.TriggerMode = (NearbyPlayerTriggerMode)trigger;
            var cooldown = draft.CooldownSeconds;
            if (ImGui.InputInt("Cooldown seconds##NearbyCooldown", ref cooldown)) draft.CooldownSeconds = cooldown;
            var chat = draft.LocalChat; if (ImGui.Checkbox("Local chat##NearbyChat", ref chat)) draft.LocalChat = chat;
            ImGui.SameLine(); var toast = draft.Toast; if (ImGui.Checkbox("Toast##NearbyToast", ref toast)) draft.Toast = toast;
            var notification = draft.Notification; if (ImGui.Checkbox("Info notification##NearbyNotification", ref notification)) draft.Notification = notification;
            var speech = draft.Speech; if (ImGui.Checkbox("Speech through EdgeTTS##NearbySpeech", ref speech)) draft.Speech = speech;
            ImGui.TextWrapped("Speech requires the optional EdgeTTS provider. The provider controls synthesis and network behavior; a request does not confirm audible playback.");
            var commands = draft.CommandsEnabled == true;
            if (ImGui.Checkbox("Enable configured commands/text##NearbyCommandsEnabled", ref commands)) draft.CommandsEnabled = commands;
            var entryMode = (int)draft.EntryMode;
            if (ImGui.Combo("Entry mode##NearbyEntryMode", ref entryMode, new[] { "Slash command", "Chat entry (outbound text)" }, 2)) draft.EntryMode = (NearbyPlayerEntryMode)entryMode;
            if (commands && draft.NameMode == NearbyPlayerNameMode.Any)
                ImGui.TextWrapped("Commands are enabled for Any name within the selected filters. Empty filters match every eligible nearby player.");
            ImGui.InputTextMultiline("Command templates##NearbyCommands", ref nearbyRuleCommands, 32768, new Vector2(-1, 100));
            ImGui.TextWrapped("One entry per line, at most 15. Placeholders: {0} or {name}, and {world}; use {{ and }} for literal braces. Expanded entries must fit 500 UTF-8 bytes. Commands are submitted locally at most once per 500ms; they are never retried automatically.");
            if (ImGui.Button("Validate / preview##NearbyRulePreview")) ValidateNearbyDraft();
            ImGui.SameLine();
            if (ImGui.Button("Apply rule##NearbyRuleApply") && ValidateNearbyDraft())
                SaveNearbyRule(draft.RuleId, draft);
        }
        finally { ImGui.EndDisabled(); }
        if (ImGui.Button("Close editor##NearbyRuleClose")) nearbyRuleDraft = null;
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
                nearbyRuleError = "Validated. Action preview (example identity only): " + (draft.Commands.Count == 0 ? "No command entries." : string.Join(" | ", preview));
            }
            catch (ArgumentException) { nearbyRuleError = "Template syntax is valid. The example identity exceeds the expanded limit; each actual identity is checked before dispatch."; }
            return true;
        }
        catch (ArgumentException error) { nearbyRuleError = "Validation failed: " + error.Message; return false; }
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
            nearbyRuleError = "Rule saved. Unrelated rules keep their pending work and cooldowns.";
            nearbyRuleDraft = null;
        }
        catch (ArgumentException error) { nearbyRuleError = "Rule not saved: " + error.Message; }
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
            if (ids.Count == 0) { ImGui.TextDisabled(label + ": Any"); return; }
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
                ImGui.TextDisabled($"{label}: {id} - {(string.IsNullOrWhiteSpace(name) ? "Unknown ID (retained)" : name)}");
            }
        }
        catch (ArgumentException error) { ImGui.TextWrapped(error.Message); }
    }
}
