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
    private readonly List<(InspectOutfitHistoryEntry Entry, string Player, global::XASlave.Localization.UiMessage Details)> rows = new();
    private string filter = string.Empty;
    private int revision = -1;

    internal InspectOutfitHistoryWindow(InspectOutfitTryOnService service)
        : base(global::XASlave.Localization.UiLocale.Get("S4005BB983C75D162") + "###XAInspectOutfitHistory", ImGuiWindowFlags.None)
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
        WindowName = global::XASlave.Localization.UiLocale.Get("S4005BB983C75D162") + "###XAInspectOutfitHistory";
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
            rows.Add((entry, $"{entry.PlayerName}@{world}", DescribeMessage(entry)));
        }
        revision = service.History.Revision;
    }

    public override void Draw()
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##OutfitPlayer", global::XASlave.Localization.UiLocale.Get("SB3EACA0CAA8CD445"), ref filter, 100);
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("S18A4F572683F4AD2", rows.Count, InspectOutfitHistory.Capacity));
        if (!service.CanTryHistory && !service.IsRunning)
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S4B527378BDD27C2E"));
        if (service.IsRunning && global::XASlave.Localization.UiNative.SmallButton(global::XASlave.Localization.UiLocale.Native("S522407FAB3628BAB"))) service.Stop();
        ImGui.TextWrapped(service.LocalizedStatusText);
        if (rows.Count == 0)
        {
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S27EA986EA2AEA507"));
            return;
        }

        Guid? remove = null;
        using (var table = ImRaii.Table("##OutfitHistory", 3,
                   ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerH,
                   new Vector2(-1, -1)))
        {
            if (!table) return;
            ImGui.TableSetupColumn(global::XASlave.Localization.UiLocale.Get("S47BBD248A3600747"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(global::XASlave.Localization.UiLocale.Get("SA5156DF2D5D59297"), ImGuiTableColumnFlags.WidthFixed, 65 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn(global::XASlave.Localization.UiLocale.Get("SC3812FC4ACB861D5"), ImGuiTableColumnFlags.WidthFixed, 65 * ImGuiHelpers.GlobalScale);
            var matched = false;
            foreach (var row in rows)
            {
                if (!row.Player.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                matched = true;
                using var id = ImRaii.PushId(row.Entry.Id.ToString());
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Player);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(row.Details.Display);
                ImGui.TextDisabled(row.Entry.SavedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                ImGui.TableNextColumn();
                using (ImRaii.Disabled(!service.CanTryHistory))
                    if (global::XASlave.Localization.UiNative.SmallButton(global::XASlave.Localization.UiLocale.Native("S954B7D52F5891AAF"))) service.RequestHistoryTryOn(row.Entry.Id);
                ImGui.TableNextColumn();
                if (global::XASlave.Localization.UiNative.SmallButton(global::XASlave.Localization.UiLocale.Native("SC3812FC4ACB861D5"))) remove = row.Entry.Id;
            }
            if (!matched)
            {
                ImGui.TableNextRow(); ImGui.TableNextColumn();
                ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SCF7F0037E84FB32A"));
            }
        }
        if (remove is { } removed) service.RemoveHistory(removed);
    }

    private static string Describe(InspectOutfitHistoryEntry entry) => DescribeMessage(entry).Original;

    private static global::XASlave.Localization.UiMessage DescribeMessage(InspectOutfitHistoryEntry entry)
    {
        if (!InspectOutfitHistory.TryGetEquipment(entry, out var equipment))
            return global::XASlave.Localization.UiMessage.Key("SFFD9C7429D0A8AD4");
        var sheet = Plugin.DataManager.GetExcelSheet<Item>();
        var lines = new List<global::XASlave.Localization.UiMessage>();
        foreach (var item in equipment)
        {
            if (item.BaseId == 0) continue;
            var gameName = sheet.GetRowOrDefault(item.Appearance.Item)?.Name.ToString();
            var name = gameName != null ? global::XASlave.Localization.UiMessage.Raw(gameName) : global::XASlave.Localization.UiMessage.Format("SFAD8B09F2BDD2797" , item.Appearance.Item);
            lines.Add(global::XASlave.Localization.UiMessage.Format("S3902B93DDC1B7585" , SlotNameMessage(item.Slot), name, item.Stain0, item.Stain1));
        }
        if (entry.FacewearId != 0) lines.Add(global::XASlave.Localization.UiMessage.Format("S9E6847986F25A423" , entry.FacewearId));
        return global::XASlave.Localization.UiMessage.Join("\n", lines);
    }

    private static string SlotName(ushort slot) => SlotNameMessage(slot).Original;

    private static global::XASlave.Localization.UiMessage SlotNameMessage(ushort slot) => slot switch
    {
        0 => global::XASlave.Localization.UiMessage.Key("SBD73F463F89F59CD"), 1 => global::XASlave.Localization.UiMessage.Key("S5D154D9C57D0F19C"), 2 => global::XASlave.Localization.UiMessage.Key("S9BB0AACC6D51F140"), 3 => global::XASlave.Localization.UiMessage.Key("S6CCAA6415B5EE449"), 4 => global::XASlave.Localization.UiMessage.Key("S6B7204778686145F"),
        6 => global::XASlave.Localization.UiMessage.Key("S38557594D0FCE7FE"), 7 => global::XASlave.Localization.UiMessage.Key("SC59E96ABC48597B1"), 8 => global::XASlave.Localization.UiMessage.Key("S3B72FF6687702060"), 9 => global::XASlave.Localization.UiMessage.Key("S6B27A97151BBF20F"), 10 => global::XASlave.Localization.UiMessage.Key("S535856A3F59103AB"),
        11 => global::XASlave.Localization.UiMessage.Key("S44D9F0B94BCAB574"), 12 => global::XASlave.Localization.UiMessage.Key("S2B4543478B84CB8E"), _ => global::XASlave.Localization.UiMessage.Key("SD6EAEC65E74285CE"),
    };
}
