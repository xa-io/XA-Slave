using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;
using XASlave.Data;
using XASlave.Services;

namespace XASlave.Windows;

internal sealed class InspectOutfitHistoryWindow : Window
{
    private readonly InspectOutfitTryOnService service;
    private readonly List<(InspectOutfitHistoryEntry Entry, string Player, string Details)> rows = new();
    private string filter = string.Empty;
    private int revision = -1;

    internal InspectOutfitHistoryWindow(InspectOutfitTryOnService service)
        : base("Outfit History###XAInspectOutfitHistory", ImGuiWindowFlags.None)
    {
        this.service = service;
        Size = ImGuiHelpers.ScaledVector2(540, 320);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    internal void Open(string player = "")
    {
        filter = player.Length > 100 ? player[..100] : player;
        IsOpen = true;
        BringToFront();
    }

    public override void PreDraw()
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = ImGuiHelpers.ScaledVector2(420, 220),
            MaximumSize = new Vector2(float.MaxValue),
        };
        if (revision == service.History.Revision) return;
        rows.Clear();
        foreach (var entry in service.History.Entries.Take(InspectOutfitHistory.Capacity))
        {
            if (entry == null) continue;
            var world = string.IsNullOrWhiteSpace(entry.WorldName) ? entry.WorldId.ToString() : entry.WorldName;
            rows.Add((entry, $"{entry.PlayerName}@{world}", Describe(entry)));
        }
        revision = service.History.Revision;
    }

    public override void Draw()
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##OutfitPlayer", "Filter player or world...", ref filter, 100);
        ImGui.TextDisabled($"{rows.Count}/{InspectOutfitHistory.Capacity} saved outfits - newest first");
        if (!service.CanTryHistory && !service.IsRunning)
            ImGui.TextWrapped("Enable Inspect Outfit Try-on and log in to try a saved outfit.");
        if (service.IsRunning && ImGui.SmallButton("Stop try-on")) service.Stop();
        ImGui.TextWrapped(service.StatusText);
        if (rows.Count == 0)
        {
            ImGui.TextWrapped("Inspect a player with Inspect Outfit Try-on enabled to save their outfit here.");
            return;
        }

        Guid? remove = null;
        using (var table = ImRaii.Table("##OutfitHistory", 3,
                   ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerH,
                   new Vector2(-1, -1)))
        {
            if (!table) return;
            ImGui.TableSetupColumn("Player / saved", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Try on", ImGuiTableColumnFlags.WidthFixed, 65 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Remove", ImGuiTableColumnFlags.WidthFixed, 65 * ImGuiHelpers.GlobalScale);
            var matched = false;
            foreach (var row in rows)
            {
                if (!row.Player.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                matched = true;
                using var id = ImRaii.PushId(row.Entry.Id.ToString());
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Player);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(row.Details);
                ImGui.TextDisabled(row.Entry.SavedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                ImGui.TableNextColumn();
                using (ImRaii.Disabled(!service.CanTryHistory))
                    if (ImGui.SmallButton("Try On")) service.RequestHistoryTryOn(row.Entry.Id);
                ImGui.TableNextColumn();
                if (ImGui.SmallButton("Remove")) remove = row.Entry.Id;
            }
            if (!matched)
            {
                ImGui.TableNextRow(); ImGui.TableNextColumn();
                ImGui.TextDisabled("No matching players.");
            }
        }
        if (remove is { } removed) service.RemoveHistory(removed);
    }

    private static string Describe(InspectOutfitHistoryEntry entry)
    {
        if (!InspectOutfitHistory.TryGetEquipment(entry, out var equipment))
            return "This saved outfit has incomplete equipment data and cannot be tried on.";
        var sheet = Plugin.DataManager.GetExcelSheet<Item>();
        var lines = new List<string>();
        foreach (var item in equipment)
        {
            if (item.BaseId == 0) continue;
            var name = sheet.GetRowOrDefault(item.Appearance.Item)?.Name.ToString() ?? $"Item {item.Appearance.Item}";
            lines.Add($"{SlotName(item.Slot)}: {name} (dyes {item.Stain0}/{item.Stain1})");
        }
        if (entry.FacewearId != 0) lines.Add($"Facewear: {entry.FacewearId} (if unlocked)");
        return string.Join("\n", lines);
    }

    private static string SlotName(ushort slot) => slot switch
    {
        0 => "Main hand", 1 => "Off hand", 2 => "Head", 3 => "Body", 4 => "Hands",
        6 => "Legs", 7 => "Feet", 8 => "Ears", 9 => "Neck", 10 => "Wrists",
        11 => "Right ring", 12 => "Left ring", _ => "Gear",
    };
}
