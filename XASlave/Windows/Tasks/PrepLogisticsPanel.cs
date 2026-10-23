using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using XASlave.Data;
using XASlave.Services;
using XASlave.Services.Tasks;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private readonly HashSet<int> prepLogisticsSelectedIndices = new();
    private readonly ConcurrentQueue<string> prepLogisticsCompletedCharacterQueue = new();
    private string prepLogisticsNewChar = string.Empty;
    private string prepLogisticsSearchFilter = string.Empty;
    private string prepLogisticsWorldFilter = string.Empty;
    private string prepLogisticsAetheryteFilter = string.Empty;
    private bool prepLogisticsShowLog;
    private List<string>? prepLogisticsAetheryteNames;

    private void DrawPrepLogisticsTask()
    {
        var cfg = plugin.Configuration;
        var runner = plugin.TaskRunner;
        var chars = cfg.PrepLogisticsCharacters;
        while (prepLogisticsCompletedCharacterQueue.TryDequeue(out var completedCharacter))
        {
            if (SelectionCompletion.RemoveCompletedCharacter(chars, prepLogisticsSelectedIndices, completedCharacter))
                runner.AddLog($"Prep Logistics: unchecked {completedCharacter} from the character list.");
        }
        var arOk = plugin.IpcClient.IsAutoRetainerAvailable();
        var lsOk = plugin.IpcClient.IsLifestreamAvailable();
        var allRequired = arOk && lsOk;

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("S2AE13753A8D90982"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S16FB80FFB6E6BB36"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S0D2EC15385610356"));
        ImGui.Spacing();

        var arConfigExists = plugin.ArConfigReader.ConfigFileExists();
        using (ImRaii.Disabled(!arConfigExists))
        {
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S74BC538828226264") + "##prepLogisticsImportAR")))
            {
                try
                {
                    var (added, total) = ImportCharactersFromArToList(chars, cfg.PrepLogisticsHonorArExclusions);
                    cfg.Save();
                    arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, (added > 0 ? global::XASlave.Localization.UiMessage.Format("SAFF25205F56B2E82", added, total) : global::XASlave.Localization.UiMessage.Format("SECB3F6AE67AFB0B6", total)));
                    if (cfg.PrepLogisticsHonorArExclusions)
                        arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Capture(ref arImportStatusUiMessage, arImportStatus), plugin.ArConfigReader.ExclusionStatusMessage));
                    arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
                }
                catch (Exception ex)
                {
                    arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Format("S002C95411A310E05", ex.Message));
                    arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
                }
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(arConfigExists
                ? global::XASlave.Localization.UiLocale.FormatKey("S4F45236F63C59987", plugin.ArConfigReader.GetAutoRetainerConfigPath())
                : global::XASlave.Localization.UiLocale.FormatKey("S7F848B59CF50E7D8", plugin.ArConfigReader.GetAutoRetainerConfigPath()));

        DrawHonorAutoRetainerExclusions("prepLogistics", cfg.PrepLogisticsHonorArExclusions,
            value => cfg.PrepLogisticsHonorArExclusions = value,
            () => PruneAutoRetainerExcludedSelections(chars, prepLogisticsSelectedIndices, cfg.PrepLogisticsHonorArExclusions));
        PruneAutoRetainerExcludedSelections(chars, prepLogisticsSelectedIndices, cfg.PrepLogisticsHonorArExclusions);

        var xaDbAvailable = plugin.IpcClient.IsXaDatabaseAvailable();
        using (ImRaii.Disabled(!xaDbAvailable))
        {
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S658431DD798692FF") + "##prepLogisticsPullXA")))
                PullXaDatabaseInfo();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(xaDbAvailable
                ? global::XASlave.Localization.UiLocale.Get("S53D36FCA96FA2578")
                : global::XASlave.Localization.UiLocale.Get("S2A0686AE2571326E"));

        if (!string.IsNullOrEmpty(arImportStatus) && DateTime.UtcNow < arImportStatusExpiry)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), global::XASlave.Localization.UiMessage.Resolve(ref arImportStatusUiMessage, arImportStatus));
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (allRequired)
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), global::XASlave.Localization.UiLocale.Get("SCB98E3ED3C5CB275"));
        else
            ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), global::XASlave.Localization.UiLocale.Get("SEBA5BEF6412F53DD"));

        ImGui.Text(global::XASlave.Localization.UiLocale.Get("S4588EB2A8F8AB239"));
        ImGui.SameLine(); ImGui.TextColored(arOk ? new Vector4(0.4f, 1.0f, 0.4f, 1.0f) : new Vector4(1.0f, 0.4f, 0.4f, 1.0f), arOk ? global::XASlave.Localization.UiLocale.Get("S4BD2C435DD24A0CE") : global::XASlave.Localization.UiLocale.Get("S90E684E28B687709"));
        ImGui.SameLine(); ImGui.TextColored(lsOk ? new Vector4(0.4f, 1.0f, 0.4f, 1.0f) : new Vector4(1.0f, 0.4f, 0.4f, 1.0f), lsOk ? global::XASlave.Localization.UiLocale.Get("S3D7411ED3FFD4E68") : global::XASlave.Localization.UiLocale.Get("S595BF8BFAA2A6143"));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var isRunning = runner.IsRunning && runner.CurrentTaskName == "Prep Logistics";
        if (isRunning)
        {
            var progress = runner.TotalItems > 0 ? (float)runner.CompletedItems / runner.TotalItems : 0f;
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), global::XASlave.Localization.UiLocale.FormatKey("S8EB69248AA11B2FD", runner.LocalizedStatusText));
            ImGui.ProgressBar(progress, new Vector2(-1, 0), $"{runner.CompletedItems}/{runner.TotalItems}");
            if (!string.IsNullOrEmpty(runner.CurrentItemLabel))
                ImGui.TextDisabled(runner.CurrentItemLabelDisplay);
            DrawProcessingList(runner);
            ImGui.Spacing();
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S19766ED6CCB2F4A3") + "##prepLogisticsCancel")))
                runner.Cancel();
        }
        else
        {
            var selectedChars = GetSelectedPrepLogisticsCharacters();
            var hasWorld = !string.IsNullOrWhiteSpace(cfg.PrepLogisticsTargetWorld);
            var canStart = selectedChars.Count > 0 && hasWorld && !runner.IsRunning && allRequired;

            var started = DrawPriorityTaskActionButton(
                SlaveTask.PrepLogistics,
                (string.Format(global::XASlave.Localization.UiLocale.Native("SC0F20E306C7E38D9"), selectedChars.Count) + $"##prepLogisticsStart"),
                canStart,
                StartPrepLogistics,
                !allRequired
                    ? global::XASlave.Localization.UiLocale.Get("S0A4C6C0B785E7FB1")
                    : !hasWorld
                        ? global::XASlave.Localization.UiLocale.Get("S089681C25D8227CA")
                        : global::XASlave.Localization.UiLocale.Get("S630BBC121AA0D071"), displayButtonLabel: global::XASlave.Localization.UiLocale.FormatKey("SC0F20E306C7E38D9", selectedChars.Count));
            if (started)
                AutoOpenTaskLogIfVerbose(ref prepLogisticsShowLog);

            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S8D36DE8ED9DBBB1B") + "##prepLogisticsAll")))
                SelectVisiblePrepLogisticsCharacters();

            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SDDCEB7ADFDB8816E") + "##prepLogisticsNone")))
                prepLogisticsSelectedIndices.Clear();

            ImGui.SameLine();
            DrawPrepLogisticsWorldSelector(cfg);

            ImGui.SameLine();
            DrawPrepLogisticsAetheryteSelector(cfg);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawCharacterListHeader(global::XASlave.Localization.UiLocale.Get("S9127EB7A3D10F5D6"), global::XASlave.Localization.UiLocale.FormatKey("S261FA25B9E69A2B0", chars.Count), "prepLogisticsAnonymize");
        var anonymizeCharacters = IsCharacterListAnonymizationEnabled();
        ImGui.Spacing();

        var prepRegionFilter = cfg.PrepLogisticsRegionFilter;
        if (DrawRegionFilterCombo((global::XASlave.Localization.UiLocale.Native("SD3A008EF1335692D") + "##prepLogisticsRegion"), ref prepRegionFilter))
        {
            cfg.PrepLogisticsRegionFilter = prepRegionFilter;
            cfg.Save();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(Scale(200f));
        ImGui.InputTextWithHint("##prepLogisticsSearch", global::XASlave.Localization.UiLocale.Get("SF62AB4CE4380C1B9"), ref prepLogisticsSearchFilter, 128);
        ImGui.Spacing();

        using (var imguiScope171 = ImRaii.Table("PrepLogisticsCharTable", 7,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Sortable | ImGuiTableFlags.Resizable,
            ScaledVector(0f, 250f)))
        if (imguiScope171)
        {
            global::XASlave.Localization.UiNative.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, Scale(30f));
            global::XASlave.Localization.UiNative.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort, Scale(30f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S1405A63E8A78B85C"), ImGuiTableColumnFlags.WidthStretch);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S78AE647DC5544D22"), ImGuiTableColumnFlags.WidthFixed, Scale(90f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SFE1780A6B87DF813"), ImGuiTableColumnFlags.WidthFixed, Scale(120f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S18BE9C4A127B0581"), ImGuiTableColumnFlags.WidthFixed, Scale(70f));
            global::XASlave.Localization.UiNative.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, Scale(25f));
            ImGui.TableSetupScrollFreeze(0, 1);
            global::XASlave.Localization.UiNative.TableHeadersRow();

            var charInfo = cfg.ReloggerCharacterInfo;
            var filtered = new List<(int OrigIdx, string CharName, string World, string RegionDc, ReloggerCharacterData? Info)>();
            for (var idx = 0; idx < chars.Count; idx++)
            {
                var charName = chars[idx];
                if (!IsAutoRetainerCharacterAllowed(charName, cfg.PrepLogisticsHonorArExclusions))
                    continue;
                var world = GetWorldFromKey(charName);
                var regionDc = WorldData.GetRegionDcLabel(world);

                if (!MatchesRegionFilter(world, cfg.PrepLogisticsRegionFilter))
                    continue;

                if (!string.IsNullOrEmpty(prepLogisticsSearchFilter)
                    && !charName.Contains(prepLogisticsSearchFilter, StringComparison.OrdinalIgnoreCase)
                    && !world.Contains(prepLogisticsSearchFilter, StringComparison.OrdinalIgnoreCase)
                    && !regionDc.Contains(prepLogisticsSearchFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                charInfo.TryGetValue(charName, out var info);
                filtered.Add((idx, charName, world, regionDc, info));
            }

            var sortSpecs = ImGui.TableGetSortSpecs();
            if (sortSpecs.SpecsDirty) sortSpecs.SpecsDirty = false;
            if (sortSpecs.SpecsCount > 0)
            {
                unsafe
                {
                    var spec = sortSpecs.Specs;
                    var colIdx = spec.ColumnIndex;
                    var ascending = spec.SortDirection == ImGuiSortDirection.Ascending;
                    filtered.Sort((a, b) =>
                    {
                        var cmp = colIdx switch
                        {
                            1 => a.OrigIdx.CompareTo(b.OrigIdx),
                            2 => string.Compare(a.CharName, b.CharName, StringComparison.OrdinalIgnoreCase),
                            3 => string.Compare(a.World, b.World, StringComparison.OrdinalIgnoreCase),
                            4 => string.Compare(a.RegionDc, b.RegionDc, StringComparison.OrdinalIgnoreCase),
                            5 => (a.Info?.MainInventoryFreeSlots ?? 0).CompareTo(b.Info?.MainInventoryFreeSlots ?? 0),
                            _ => a.OrigIdx.CompareTo(b.OrigIdx),
                        };
                        return ascending ? cmp : -cmp;
                    });
                }
            }

            var displayIndex = 0;
            foreach (var (i, charName, world, regionDc, info) in filtered)
            {
                displayIndex++;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var selected = prepLogisticsSelectedIndices.Contains(i);
                if (ImGui.Checkbox($"##prepLogisticsSel{i}", ref selected))
                {
                    if (selected) prepLogisticsSelectedIndices.Add(i);
                    else prepLogisticsSelectedIndices.Remove(i);
                }

                ImGui.TableNextColumn();
                ImGui.Text(displayIndex.ToString());

                ImGui.TableNextColumn();
                ImGui.Text(GetDisplayCharacterKey(charName, anonymizeCharacters));

                ImGui.TableNextColumn();
                ImGui.TextDisabled(GetDisplayWorldFromKey(charName, anonymizeCharacters));

                ImGui.TableNextColumn();
                ImGui.TextDisabled(regionDc);

                ImGui.TableNextColumn();
                var inventoryLabel = GetInventoryFreeSlotsLabel(info);
                if (!string.IsNullOrEmpty(inventoryLabel))
                    ImGui.TextDisabled(inventoryLabel);
                else
                    ImGui.TextDisabled("-");

                ImGui.TableNextColumn();
                using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(1.0f, 0.4f, 0.4f, 1.0f)))
                {
                    if (global::XASlave.Localization.UiNative.SmallButton((global::XASlave.Localization.UiLocale.Native("S4B68AB3847FEDA7D") + $"##prepLogisticsRm{i}")))
                    {
                        chars.RemoveAt(i);
                        prepLogisticsSelectedIndices.Remove(i);
                        var newSet = new HashSet<int>();
                        foreach (var idx in prepLogisticsSelectedIndices)
                            newSet.Add(idx > i ? idx - 1 : idx);
                        prepLogisticsSelectedIndices.Clear();
                        foreach (var idx in newSet)
                            prepLogisticsSelectedIndices.Add(idx);
                        cfg.Save();
                        break;
                    }
                }
            }


        }

        ImGui.Spacing();
        ImGui.SetNextItemWidth(Scale(250f));
        var enterPressed = ImGui.InputTextWithHint("##prepLogisticsAdd", global::XASlave.Localization.UiLocale.Get("SF32C73F5DCC35D82"), ref prepLogisticsNewChar, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S9FD728C66C9A256B") + "##prepLogisticsAddBtn")) || enterPressed) && !string.IsNullOrWhiteSpace(prepLogisticsNewChar))
        {
            var trimmed = prepLogisticsNewChar.Trim();
            if (!chars.Contains(trimmed))
            {
                chars.Add(trimmed);
                cfg.Save();
            }
            prepLogisticsNewChar = string.Empty;
        }
        ImGui.SameLine();
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S1B7CB3CFDA6217B9"));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var logoutOnComplete = cfg.PrepLogisticsLogoutOnComplete;
        var killGameOnComplete = cfg.PrepLogisticsKillGameOnComplete;
        var enableArMultiOnComplete = cfg.PrepLogisticsEnableArMultiOnComplete;

        var targetDestination = GetPrepLogisticsDestinationLabel(cfg.PrepLogisticsTargetWorld, cfg.PrepLogisticsTargetAetheryte);
        if (!string.IsNullOrWhiteSpace(cfg.PrepLogisticsTargetWorld))
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("SBF80D5858F4868BD", targetDestination));
        else
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S25F48FA6405F34C2"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SCC545F030F72D2E0"));
        if (DrawSharedCompletionAndLogFooter("prepLogistics", "prepLogistics", ref logoutOnComplete, ref killGameOnComplete, ref enableArMultiOnComplete, ref prepLogisticsShowLog, runner))
        {
            cfg.PrepLogisticsLogoutOnComplete = logoutOnComplete;
            cfg.PrepLogisticsKillGameOnComplete = killGameOnComplete;
            cfg.PrepLogisticsEnableArMultiOnComplete = enableArMultiOnComplete;
            cfg.Save();
        }
    }

    private void DrawPrepLogisticsWorldSelector(XASlave.Configuration cfg)
    {
        var label = string.IsNullOrWhiteSpace(cfg.PrepLogisticsTargetWorld)
            ? "Select World##prepLogisticsWorldButton"
            : $"{cfg.PrepLogisticsTargetWorld}##prepLogisticsWorldButton";

        if (ImGui.Button(label))
            ImGui.OpenPopup("PrepLogisticsWorldPopup");

        using var popup = ImRaii.Popup("PrepLogisticsWorldPopup");
        if (!popup)
            return;

        ImGui.SetNextItemWidth(Scale(240f));
        ImGui.InputTextWithHint("##prepLogisticsWorldFilter", global::XASlave.Localization.UiLocale.Get("S2F41446BE02C17D6"), ref prepLogisticsWorldFilter, 128);
        ImGui.Separator();

        foreach (var region in WorldData.RegionOrder)
        {
            var regionWorlds = WorldData.Worlds.Where(w => w.Region == region).ToList();
            if (!regionWorlds.Any(w => string.IsNullOrWhiteSpace(prepLogisticsWorldFilter) || w.Name.Contains(prepLogisticsWorldFilter, StringComparison.OrdinalIgnoreCase)))
                continue;

            using var regionNode = ImRaii.TreeNode(region);
            if (!regionNode)
                continue;

            if (!WorldData.DataCenterOrder.TryGetValue(region, out var dcs))
                continue;

            foreach (var dc in dcs)
            {
                var dcWorlds = regionWorlds
                    .Where(w => w.DataCenter == dc)
                    .Where(w => string.IsNullOrWhiteSpace(prepLogisticsWorldFilter) || w.Name.Contains(prepLogisticsWorldFilter, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(w => w.Name)
                    .ToList();
                if (dcWorlds.Count == 0)
                    continue;

                using var dataCenterNode = ImRaii.TreeNode(dc);
                if (!dataCenterNode)
                    continue;

                foreach (var world in dcWorlds)
                {
                    if (!ImGui.Selectable(world.Name, string.Equals(cfg.PrepLogisticsTargetWorld, world.Name, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    cfg.PrepLogisticsTargetWorld = world.Name;
                    cfg.Save();
                    prepLogisticsWorldFilter = string.Empty;
                    ImGui.CloseCurrentPopup();
                    break;
                }
            }
        }
    }

    private void DrawPrepLogisticsAetheryteSelector(XASlave.Configuration cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.PrepLogisticsTargetWorld))
        {
            using (ImRaii.Disabled())
            {
                global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S3C82033F8EADE216") + "##prepLogisticsAetheryteButton"));
            }
            return;
        }

        var label = string.IsNullOrWhiteSpace(cfg.PrepLogisticsTargetAetheryte)
            ? "Select Location##prepLogisticsAetheryteButton"
            : $"{cfg.PrepLogisticsTargetAetheryte}##prepLogisticsAetheryteButton";

        if (ImGui.Button(label))
            ImGui.OpenPopup("PrepLogisticsAetherytePopup");

        using var popup = ImRaii.Popup("PrepLogisticsAetherytePopup");
        if (!popup)
            return;

        ImGui.SetNextItemWidth(Scale(240f));
        ImGui.InputTextWithHint("##prepLogisticsAetheryteFilter", global::XASlave.Localization.UiLocale.Get("S93A486F4363AC018"), ref prepLogisticsAetheryteFilter, 128);
        ImGui.Separator();

        if (global::XASlave.Localization.UiNative.Selectable(global::XASlave.Localization.UiLocale.Native("SDF0057F3116E70A3"), string.IsNullOrWhiteSpace(cfg.PrepLogisticsTargetAetheryte)))
        {
            cfg.PrepLogisticsTargetAetheryte = string.Empty;
            cfg.Save();
            prepLogisticsAetheryteFilter = string.Empty;
            ImGui.CloseCurrentPopup();
        }

        foreach (var aetheryte in GetPrepLogisticsAetheryteNames())
        {
            if (!string.IsNullOrWhiteSpace(prepLogisticsAetheryteFilter)
                && !aetheryte.Contains(prepLogisticsAetheryteFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!ImGui.Selectable(aetheryte, string.Equals(cfg.PrepLogisticsTargetAetheryte, aetheryte, StringComparison.OrdinalIgnoreCase)))
                continue;

            cfg.PrepLogisticsTargetAetheryte = aetheryte;
            cfg.Save();
            prepLogisticsAetheryteFilter = string.Empty;
            ImGui.CloseCurrentPopup();
            break;
        }
    }

    private List<string> GetPrepLogisticsAetheryteNames()
    {
        if (prepLogisticsAetheryteNames != null)
            return prepLogisticsAetheryteNames;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Aetheryte>();
            foreach (var row in sheet)
            {
                if (!row.IsAetheryte)
                    continue;

                var name = row.PlaceName.Value.Name.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }
        }
        catch
        {
        }

        prepLogisticsAetheryteNames = names
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return prepLogisticsAetheryteNames;
    }

    private static string GetPrepLogisticsAetheryteZoneName(string aetheryteName)
    {
        if (string.IsNullOrWhiteSpace(aetheryteName))
            return string.Empty;

        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Aetheryte>();
            foreach (var row in sheet)
            {
                if (!row.IsAetheryte)
                    continue;

                var placeName = row.PlaceName.ValueNullable?.Name.ToString() ?? string.Empty;
                if (!placeName.Equals(aetheryteName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var zoneName = row.Territory.ValueNullable?.PlaceName.ValueNullable?.Name.ToString() ?? string.Empty;
                return zoneName;
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private List<string> GetSelectedPrepLogisticsCharacters()
    {
        var chars = plugin.Configuration.PrepLogisticsCharacters;
        return prepLogisticsSelectedIndices
            .Where(i => i >= 0 && i < chars.Count
                && IsAutoRetainerCharacterAllowed(chars[i], plugin.Configuration.PrepLogisticsHonorArExclusions))
            .OrderBy(i => i)
            .Select(i => chars[i])
            .ToList();
    }

    private void SelectVisiblePrepLogisticsCharacters()
    {
        var cfg = plugin.Configuration;
        var chars = cfg.PrepLogisticsCharacters;
        for (var i = 0; i < chars.Count; i++)
        {
            var charName = chars[i];
            if (!IsAutoRetainerCharacterAllowed(charName, cfg.PrepLogisticsHonorArExclusions))
                continue;
            var world = GetWorldFromKey(charName);
            var regionDc = WorldData.GetRegionDcLabel(world);
            if (!MatchesRegionFilter(world, cfg.PrepLogisticsRegionFilter))
                continue;
            if (!string.IsNullOrEmpty(prepLogisticsSearchFilter)
                && !charName.Contains(prepLogisticsSearchFilter, StringComparison.OrdinalIgnoreCase)
                && !world.Contains(prepLogisticsSearchFilter, StringComparison.OrdinalIgnoreCase)
                && !regionDc.Contains(prepLogisticsSearchFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            prepLogisticsSelectedIndices.Add(i);
        }
    }

    private void StartPrepLogistics()
    {
        var cfg = plugin.Configuration;
        var selected = GetSelectedPrepLogisticsCharacters();
        var targetWorld = cfg.PrepLogisticsTargetWorld;
        var targetAetheryte = cfg.PrepLogisticsTargetAetheryte;
        if (selected.Count == 0 || string.IsNullOrWhiteSpace(targetWorld))
            return;

        var steps = BuildPrepLogisticsSteps(
            selected,
            targetWorld,
            targetAetheryte,
            cfg.PrepLogisticsEnableArMultiOnComplete,
            cfg.PrepLogisticsLogoutOnComplete,
            cfg.PrepLogisticsKillGameOnComplete,
            plugin.TaskRunner);
        if (!plugin.TaskRunner.Start(global::XASlave.Localization.UiMessage.Key("S2AE13753A8D90982"), steps, onLog: msg =>
        {
            Plugin.Log.Information($"[TaskLogs] {msg}");
        }, totalItems: selected.Count, suppressLogoutCancel: true))
            return;

        HaltAutoCollectionForPriorityTask("Prep Logistics");
        reloggerRunList = new List<string>(selected);
        AutoOpenTaskLogIfVerbose(ref prepLogisticsShowLog);
    }

    private List<TaskStep> BuildPrepLogisticsSteps(List<string> characters, string targetWorld, string targetAetheryte, bool enableArMultiOnComplete, bool logoutOnComplete, bool killGameOnComplete, TaskRunner runner)
    {
        var steps = new List<TaskStep>();
        var targetDestination = GetPrepLogisticsDestinationLabel(targetWorld, targetAetheryte);
        var lifestreamDestination = BuildPrepLogisticsDestinationCommand(targetWorld, targetAetheryte);
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S22C41FFCBA42741B"),
            OnEnter = () =>
            {
                runner.AddLog("Disabling AutoRetainer Multi Mode...");
                plugin.IpcClient.AutoRetainerSetMultiModeEnabled(false);
            },
            IsComplete = () => true,
            TimeoutSec = 3f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Key("SC4B6369ECA28F664"), 1.0f));

        for (var i = 0; i < characters.Count; i++)
        {
            var charName = characters[i];
            var charIndex = i + 1;
            var charTotal = characters.Count;
            var relogFailed = false;
            var skipTravel = false;

            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S5FB6F2415218C668", charIndex, charTotal, charName),
                OnEnter = () =>
                {
                    runner.CurrentItemLabel = $"[{charIndex}/{charTotal}] {charName} -> {targetDestination}";
                    runner.AddLog($"-- Prep Logistics: {charName} ({charIndex}/{charTotal}) -> {targetDestination} --");
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });

            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S57020E1FC3DE71E4", charName),
                OnEnter = () =>
                {
                    var current = MonthlyReloggerTask.GetCurrentCharacterNameWorld();
                    if (current.Equals(charName, StringComparison.OrdinalIgnoreCase))
                    {
                        runner.AddLog($"Already logged in as {charName}.");
                        return;
                    }

                    runner.AddLog($"Relogging to {charName}...");
                    ChatHelper.SendMessage($"/ays relog {charName}");
                },
                IsComplete = () => MonthlyReloggerTask.GetCurrentCharacterNameWorld().Equals(charName, StringComparison.OrdinalIgnoreCase)
                    && CharacterSafetyHelper.IsCharacterSafeWaitReady(),
                TimeoutSec = 600f,
                MaxRetries = 2,
                OnTimeout = () =>
                {
                    relogFailed = true;
                    runner.RecordFailedCharacter(charName);
                    runner.AddLog($"FAILED: Could not relog to {charName} after 3 attempt(s). Skipping travel.");
                },
            });

            foreach (var sw in MonthlyReloggerTask.BuildCharacterSafeWait3Pass(global::XASlave.Localization.UiMessage.Format("S9758F6F2B7D1B780", charName), 30f))
            {
                var origComplete = sw.IsComplete;
                steps.Add(new TaskStep
                {
                    NameMessage = sw.NameMessage,
                    OnEnter = sw.OnEnter,
                    IsComplete = () => relogFailed || origComplete(),
                    TimeoutSec = sw.TimeoutSec,
                });
            }

            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S877680ABD5DA350E", targetDestination, charName),
                OnEnter = () =>
                {
                    if (relogFailed)
                        return;

                    var currentWorld = GetCurrentWorldName();
                    var currentLocation = GetCurrentLocationName();
                    var alreadyOnTargetWorld = currentWorld.Equals(targetWorld, StringComparison.OrdinalIgnoreCase);
                    var hasTargetAetheryte = !string.IsNullOrWhiteSpace(targetAetheryte);

                    if (alreadyOnTargetWorld && (!hasTargetAetheryte || currentLocation.Equals(targetAetheryte, StringComparison.OrdinalIgnoreCase)))
                    {
                        skipTravel = true;
                        runner.AddLog(hasTargetAetheryte
                            ? $"Already at {targetDestination}; skipping travel."
                            : $"Already on {targetWorld}; skipping world travel.");
                        return;
                    }

                    if (alreadyOnTargetWorld && hasTargetAetheryte)
                    {
                        var targetZone = GetPrepLogisticsAetheryteZoneName(targetAetheryte);
                        if (!string.IsNullOrWhiteSpace(targetZone)
                            && currentLocation.Equals(targetZone, StringComparison.OrdinalIgnoreCase))
                        {
                            runner.AddLog($"Already on {targetWorld} in zone '{targetZone}'; treating destination as reached.");
                            skipTravel = true;
                            return;
                        }
                    }

                    runner.AddLog($"Traveling to {targetDestination} via Lifestream...");
                    plugin.IpcClient.LifestreamExecuteCommand(lifestreamDestination);
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });

            steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S565D32CD1789D48F", charName), 1.0f));

            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SEB48E1B135B01FD3", charName),
                IsComplete = () =>
                {
                    if (relogFailed || skipTravel)
                        return true;

                    try { return plugin.IpcClient.LifestreamIsBusy(); }
                    catch { return true; }
                },
                TimeoutSec = 10f,
            });

            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S242514319CDF7151", charName),
                IsComplete = () =>
                {
                    if (relogFailed || skipTravel)
                        return true;

                    try { return !plugin.IpcClient.LifestreamIsBusy(); }
                    catch { return true; }
                },
                TimeoutSec = 120f,
            });

            foreach (var sw in MonthlyReloggerTask.BuildCharacterSafeWait3Pass(global::XASlave.Localization.UiMessage.Format("S0282E5398BFB6417", charName), 30f))
            {
                var origComplete = sw.IsComplete;
                steps.Add(new TaskStep
                {
                    NameMessage = sw.NameMessage,
                    OnEnter = sw.OnEnter,
                    IsComplete = () => relogFailed || origComplete(),
                    TimeoutSec = sw.TimeoutSec,
                });
            }

            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SF4773A51334A546E", charName),
                OnEnter = () =>
                {
                    if (relogFailed)
                        return;

                    var currentWorld = GetCurrentWorldName();
                    if (currentWorld.Equals(targetWorld, StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrWhiteSpace(targetAetheryte))
                        {
                            runner.AddLog($"Confirmed {charName} is now on {targetWorld}.");
                        }
                        else
                        {
                            var targetZone = GetPrepLogisticsAetheryteZoneName(targetAetheryte);
                            if (!string.IsNullOrWhiteSpace(targetZone))
                                runner.AddLog($"Confirmed {charName} is now on {targetWorld}. Target location '{targetAetheryte}' maps to zone '{targetZone}'.");
                            else
                                runner.AddLog($"Confirmed {charName} is now on {targetWorld}. Target location was '{targetAetheryte}'.");
                        }
                    }
                    else
                    {
                        runner.AddLog($"WARNING: Expected world '{targetWorld}', but current location is '{GetCurrentPrepLogisticsLocationLabel()}'.");
                    }
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });

            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SB5063DC812CCDD2B", charName),
                OnEnter = () =>
                {
                    runner.CompletedItems = Math.Min(runner.CompletedItems + 1, runner.TotalItems);

                    if (relogFailed)
                    {
                        runner.AddLog($"Prep Logistics: leaving {charName} checked because the relog failed.");
                        return;
                    }

                    UncheckPrepLogisticsCharacter(charName, runner);
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
        }

        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S0790D751B397858D"),
            OnEnter = () =>
            {
                if (!MonthlyReloggerTask.ShouldKeepLogoutCancelSuppressed(logoutOnComplete, killGameOnComplete))
                    runner.SuppressLogoutCancel = false;
                if (runner.FailedCharacters.Count > 0)
                    runner.AddLog($"══ SUMMARY: {characters.Count - runner.FailedCharacters.Count}/{characters.Count} character(s) processed for {targetDestination}, {runner.FailedCharacters.Count} failed ══");
                else
                    runner.AddLog($"══ SUMMARY: All {characters.Count} character(s) processed for {targetDestination} ══");
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });

        MonthlyReloggerTask.AddSharedCompletionSteps(steps, runner, logoutOnComplete, killGameOnComplete, enableArMultiOnComplete);

        return steps;
    }

    private static string GetPrepLogisticsDestinationLabel(string targetWorld, string targetAetheryte)
    {
        if (string.IsNullOrWhiteSpace(targetWorld))
            return string.Empty;

        return string.IsNullOrWhiteSpace(targetAetheryte)
            ? targetWorld
            : $"{targetWorld}, {targetAetheryte}";
    }

    private static string BuildPrepLogisticsDestinationCommand(string targetWorld, string targetAetheryte)
    {
        return GetPrepLogisticsDestinationLabel(targetWorld, targetAetheryte);
    }

    private static string GetCurrentWorldName()
    {
        try
        {
            var local = Plugin.ObjectTable.LocalPlayer;
            if (local == null)
                return string.Empty;

            return WorldData.GetById(local.CurrentWorld.RowId)?.Name ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetCurrentLocationName()
    {
        try
        {
            var territoryId = Plugin.ClientState.TerritoryType;
            var sheet = Plugin.DataManager.GetExcelSheet<TerritoryType>();
            if (sheet.TryGetRow(territoryId, out var row))
                return row.PlaceName.Value.Name.ToString();
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string GetCurrentPrepLogisticsLocationLabel()
    {
        var currentWorld = GetCurrentWorldName();
        var currentLocation = GetCurrentLocationName();
        if (string.IsNullOrWhiteSpace(currentWorld))
            return "Unknown";

        return string.IsNullOrWhiteSpace(currentLocation)
            ? currentWorld
            : $"{currentWorld}, {currentLocation}";
    }

    private void UncheckPrepLogisticsCharacter(string characterName, TaskRunner runner)
    {
        prepLogisticsCompletedCharacterQueue.Enqueue(characterName);
    }
}
