using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using XASlave.Data;
using XASlave.Services;
using XASlave.Services.Tasks;

namespace XASlave.Windows;

/// <summary>
/// Multi-FC Permissions Updater - logs into multiple characters and updates
/// FC member role permissions to have all permissions enabled.
///
/// Flow per character:
///   1. Relog to character
///   2. CharacterSafeWait
///   3. Open FC window (/freecompanycmd)
///   4. Fire FC callbacks to navigate to rank editor
///   5. Apply all permissions via FreeCompanyMemberRankEdit callback
///   6. Next character
///
/// Callback sequence:
///   FreeCompany true 0 2           → navigate to ranks tab
///   FreeCompanyRank true 2 2 ...   → select rank
///   FreeCompanyRank true 4 3 1513 557 ... → edit rank
///   FreeCompanyRank true 3 2 ...   → confirm
///   ContextMenu true 0 0 0 ...     → open context
///   FreeCompanyMemberRankEdit true 0 ... 1 1 1 1 1 ... → apply all permissions
/// </summary>
public partial class SlaveWindow
{
    // -- FC Permissions state --
    private readonly HashSet<int> fcPermsSelectedIndices = new();
    private string fcPermsNewChar = "";
    private string fcPermsSearchFilter = "";
    private bool fcPermsShowLog;

    private void DrawMultiFcPermissionsTask()
    {
        var cfg = plugin.Configuration;
        var chars = cfg.FcPermsCharacters;

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("S5C55501A8E5A4274"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SD289936A6FCC000B"));
        ImGui.Spacing();

        // -- Import / Refresh buttons --
        var arConfigExists = plugin.ArConfigReader.ConfigFileExists();
        using (ImRaii.Disabled(!arConfigExists))
        {
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S74BC538828226264") + "##fcPermsImportAR")))
            {
                try
                {
                    var (added, total) = ImportCharactersFromArToList(chars, cfg.FcPermsHonorArExclusions);
                    cfg.Save();
                    arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, (added > 0 ? global::XASlave.Localization.UiMessage.Format("SAFF25205F56B2E82", added, total) : global::XASlave.Localization.UiMessage.Format("SECB3F6AE67AFB0B6", total)));
                    if (cfg.FcPermsHonorArExclusions)
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

        DrawHonorAutoRetainerExclusions("fcPerms", cfg.FcPermsHonorArExclusions,
            value => cfg.FcPermsHonorArExclusions = value,
            () => PruneAutoRetainerExcludedSelections(chars, fcPermsSelectedIndices, cfg.FcPermsHonorArExclusions));
        PruneAutoRetainerExcludedSelections(chars, fcPermsSelectedIndices, cfg.FcPermsHonorArExclusions);

        var xaDbAvailable = plugin.IpcClient.IsXaDatabaseAvailable();
        using (ImRaii.Disabled(!xaDbAvailable))
        {
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S658431DD798692FF") + "##fcPermsPullXA")))
                PullXaDatabaseInfo();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(xaDbAvailable
                ? global::XASlave.Localization.UiLocale.Get("S711EAB8630E15C72")
                : global::XASlave.Localization.UiLocale.Get("S2A0686AE2571326E"));

        if (!string.IsNullOrEmpty(arImportStatus) && DateTime.UtcNow < arImportStatusExpiry)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), global::XASlave.Localization.UiMessage.Resolve(ref arImportStatusUiMessage, arImportStatus));
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Plugin status
        DrawTaskPluginStatus(false);

        ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f),
            global::XASlave.Localization.UiLocale.Get("SFA2B21C2F60BF3CC"));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- Run controls --
        var isRunning = plugin.TaskRunner.IsRunning && plugin.TaskRunner.CurrentTaskName == "FC Permissions Updater";
        if (isRunning)
        {
            var progress = plugin.TaskRunner.TotalItems > 0 ? (float)plugin.TaskRunner.CompletedItems / plugin.TaskRunner.TotalItems : 0f;
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), global::XASlave.Localization.UiLocale.FormatKey("S8EB69248AA11B2FD", plugin.TaskRunner.LocalizedStatusText));
            ImGui.ProgressBar(progress, new Vector2(-1, 0), $"{plugin.TaskRunner.CompletedItems}/{plugin.TaskRunner.TotalItems}");
            if (!string.IsNullOrEmpty(plugin.TaskRunner.CurrentItemLabel))
                ImGui.TextDisabled(plugin.TaskRunner.CurrentItemLabelDisplay);
            DrawProcessingList(plugin.TaskRunner);
            ImGui.Spacing();
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S19766ED6CCB2F4A3") + "##fcPermsCancel")))
                plugin.TaskRunner.Cancel();
        }
        else
        {
            var selectedChars = GetSelectedFcPermsCharacters();
            var canStart = selectedChars.Count > 0 && !plugin.TaskRunner.IsRunning;
            var started = DrawPriorityTaskActionButton(
                SlaveTask.MultiFcPermissions,
                (string.Format(global::XASlave.Localization.UiLocale.Native("SC0F20E306C7E38D9"), selectedChars.Count) + $"##fcPermsStart"),
                canStart,
                StartFcPermissionsUpdater,
                global::XASlave.Localization.UiLocale.Get("S630BBC121AA0D071"), displayButtonLabel: global::XASlave.Localization.UiLocale.FormatKey("SC0F20E306C7E38D9", selectedChars.Count));
            if (started)
                AutoOpenTaskLogIfVerbose(ref fcPermsShowLog);

            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S8D36DE8ED9DBBB1B") + "##fcPermsAll")))
                SelectVisibleFcPermsCharacters();
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SDDCEB7ADFDB8816E") + "##fcPermsNone")))
                fcPermsSelectedIndices.Clear();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- Character List --
        DrawCharacterListHeader(global::XASlave.Localization.UiLocale.Get("S9127EB7A3D10F5D6"), global::XASlave.Localization.UiLocale.FormatKey("S261FA25B9E69A2B0", chars.Count), "fcPermsAnonymize");
        var anonymizeCharacters = IsCharacterListAnonymizationEnabled();
        ImGui.Spacing();

        var fcPermsRegionFilter = cfg.FcPermsRegionFilter;
        if (DrawRegionFilterCombo((global::XASlave.Localization.UiLocale.Native("SD3A008EF1335692D") + "##fcPermsRegion"), ref fcPermsRegionFilter))
        {
            cfg.FcPermsRegionFilter = fcPermsRegionFilter;
            cfg.Save();
        }

        ImGui.SameLine();
        // Search filter
        ImGui.SetNextItemWidth(Scale(200f));
        ImGui.InputTextWithHint("##fcPermsSearch", global::XASlave.Localization.UiLocale.Get("SF62AB4CE4380C1B9"), ref fcPermsSearchFilter, 128);
        ImGui.Spacing();

        // Character table - columns: checkbox, #, character, world, FC name, member rank, FC rank, in FC, remove
        var charInfo = cfg.ReloggerCharacterInfo;

        using (var imguiScope167 = ImRaii.Table("FcPermsCharTable", 9,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Sortable | ImGuiTableFlags.Resizable,
            ScaledVector(0f, 250f)))
        if (imguiScope167)
        {
            global::XASlave.Localization.UiNative.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, Scale(30f));
            global::XASlave.Localization.UiNative.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort, Scale(30f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S1405A63E8A78B85C"), ImGuiTableColumnFlags.WidthStretch);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S78AE647DC5544D22"), ImGuiTableColumnFlags.WidthFixed, Scale(90f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SF9130E467E662842"), ImGuiTableColumnFlags.WidthFixed, Scale(120f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S20B5F2B2DFB80E10"), ImGuiTableColumnFlags.WidthFixed, Scale(95f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S2095FD0D8B39DB4A"), ImGuiTableColumnFlags.WidthFixed, Scale(60f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SCE3AF698202265E0"), ImGuiTableColumnFlags.WidthFixed, Scale(45f));
            global::XASlave.Localization.UiNative.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, Scale(25f));
            ImGui.TableSetupScrollFreeze(0, 1);
            global::XASlave.Localization.UiNative.TableHeadersRow();

            // Build filtered list
            var filtered = new List<(int OrigIdx, string CharName, string World, ReloggerCharacterData? Info)>();
            for (int idx = 0; idx < chars.Count; idx++)
            {
                var charName = chars[idx];
                if (!IsAutoRetainerCharacterAllowed(charName, cfg.FcPermsHonorArExclusions))
                    continue;
                var nameParts = charName.Split('@');
                var world = nameParts.Length > 1 ? nameParts[1] : "";

                if (!MatchesRegionFilter(world, cfg.FcPermsRegionFilter))
                    continue;

                if (!string.IsNullOrEmpty(fcPermsSearchFilter) &&
                    !charName.Contains(fcPermsSearchFilter, StringComparison.OrdinalIgnoreCase) &&
                    !world.Contains(fcPermsSearchFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                charInfo.TryGetValue(charName, out var info);
                filtered.Add((idx, charName, world, info));
            }

            // Sort
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
                        int cmp = colIdx switch
                        {
                            1 => a.OrigIdx.CompareTo(b.OrigIdx),
                            2 => string.Compare(a.CharName, b.CharName, StringComparison.OrdinalIgnoreCase),
                            3 => string.Compare(a.World, b.World, StringComparison.OrdinalIgnoreCase),
                            4 => string.Compare(a.Info?.FcName ?? "", b.Info?.FcName ?? "", StringComparison.OrdinalIgnoreCase),
                            5 => (a.Info?.FcMemberRankSort ?? int.MaxValue).CompareTo(b.Info?.FcMemberRankSort ?? int.MaxValue),
                            6 => (a.Info?.FreeCompanyRank ?? 0).CompareTo(b.Info?.FreeCompanyRank ?? 0),
                            7 => (a.Info != null && a.Info.FCID != 0).CompareTo(b.Info != null && b.Info.FCID != 0),
                            _ => a.OrigIdx.CompareTo(b.OrigIdx),
                        };
                        return ascending ? cmp : -cmp;
                    });
                }
            }

            var displayIndex = 0;
            foreach (var (i, charName, world, info) in filtered)
            {
                displayIndex++;
                ImGui.TableNextRow();

                // Checkbox
                ImGui.TableNextColumn();
                var selected = fcPermsSelectedIndices.Contains(i);
                if (ImGui.Checkbox($"##fpSel{i}", ref selected))
                {
                    if (selected) fcPermsSelectedIndices.Add(i);
                    else fcPermsSelectedIndices.Remove(i);
                }

                // #
                ImGui.TableNextColumn();
                ImGui.Text(displayIndex.ToString());

                // Character
                ImGui.TableNextColumn();
                ImGui.Text(GetDisplayCharacterKey(charName, anonymizeCharacters));

                // World
                ImGui.TableNextColumn();
                ImGui.TextDisabled(GetDisplayWorldFromKey(charName, anonymizeCharacters));

                // FC Name
                ImGui.TableNextColumn();
                if (info != null && !string.IsNullOrEmpty(info.FcName))
                    ImGui.TextDisabled(info.FcName);
                else
                    ImGui.TextDisabled("-");

                // Member Rank
                ImGui.TableNextColumn();
                var memberRankLabel = GetFcMemberRankLabelMessage(info).Display;
                if (!string.IsNullOrEmpty(memberRankLabel))
                    ImGui.TextDisabled(memberRankLabel);
                else
                    ImGui.TextDisabled("-");

                // FC Rank
                ImGui.TableNextColumn();
                var fcRankLabel = GetFreeCompanyRankLabel(info);
                if (!string.IsNullOrEmpty(fcRankLabel))
                    ImGui.TextDisabled(fcRankLabel);
                else
                    ImGui.TextDisabled("-");

                // In FC
                ImGui.TableNextColumn();
                if (info != null && info.FCID != 0)
                    ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), global::XASlave.Localization.UiLocale.Get("S85A39AB345D672FF"));
                else
                    ImGui.TextDisabled("-");

                // Remove
                ImGui.TableNextColumn();
                using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(1.0f, 0.4f, 0.4f, 1.0f)))
                {
                    if (global::XASlave.Localization.UiNative.SmallButton((global::XASlave.Localization.UiLocale.Native("S4B68AB3847FEDA7D") + $"##fpRm{i}")))
                    {
                        chars.RemoveAt(i);
                        fcPermsSelectedIndices.Remove(i);
                        var newSet = new HashSet<int>();
                        foreach (var idx in fcPermsSelectedIndices)
                            newSet.Add(idx > i ? idx - 1 : idx);
                        fcPermsSelectedIndices.Clear();
                        foreach (var idx in newSet) fcPermsSelectedIndices.Add(idx);
                        cfg.Save();
                        break;
                    }
                }
            }


        }

        ImGui.Spacing();

        // Add character input
        ImGui.SetNextItemWidth(Scale(250f));
        var enterPressed = ImGui.InputTextWithHint("##fcPermsAdd", global::XASlave.Localization.UiLocale.Get("SF32C73F5DCC35D82"), ref fcPermsNewChar, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S9FD728C66C9A256B") + "##fcPermsAddBtn")) || enterPressed) && !string.IsNullOrWhiteSpace(fcPermsNewChar))
        {
            var trimmed = fcPermsNewChar.Trim();
            if (!chars.Contains(trimmed))
            {
                chars.Add(trimmed);
                cfg.Save();
            }
            fcPermsNewChar = "";
        }
        ImGui.SameLine();
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S1B7CB3CFDA6217B9"));

        var logoutOnComplete = cfg.FcPermsLogoutOnComplete;
        var killGameOnComplete = cfg.FcPermsKillGameOnComplete;
        var enableArMultiOnComplete = cfg.FcPermsEnableArMultiOnComplete;
        if (DrawSharedCompletionAndLogFooter("fcPerms", "fcPerms", ref logoutOnComplete, ref killGameOnComplete, ref enableArMultiOnComplete, ref fcPermsShowLog, plugin.TaskRunner))
        {
            cfg.FcPermsLogoutOnComplete = logoutOnComplete;
            cfg.FcPermsKillGameOnComplete = killGameOnComplete;
            cfg.FcPermsEnableArMultiOnComplete = enableArMultiOnComplete;
            cfg.Save();
        }
    }

    private List<string> GetSelectedFcPermsCharacters()
    {
        var chars = plugin.Configuration.FcPermsCharacters;
        return fcPermsSelectedIndices
            .Where(i => i >= 0 && i < chars.Count
                && IsAutoRetainerCharacterAllowed(chars[i], plugin.Configuration.FcPermsHonorArExclusions))
            .OrderBy(i => i)
            .Select(i => chars[i])
            .ToList();
    }

    private void SelectVisibleFcPermsCharacters()
    {
        var cfg = plugin.Configuration;
        var chars = cfg.FcPermsCharacters;
        for (var i = 0; i < chars.Count; i++)
        {
            var charName = chars[i];
            if (!IsAutoRetainerCharacterAllowed(charName, cfg.FcPermsHonorArExclusions))
                continue;
            var world = GetWorldFromKey(charName);
            if (!MatchesRegionFilter(world, cfg.FcPermsRegionFilter))
                continue;
            if (!string.IsNullOrEmpty(fcPermsSearchFilter)
                && !charName.Contains(fcPermsSearchFilter, StringComparison.OrdinalIgnoreCase)
                && !world.Contains(fcPermsSearchFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            fcPermsSelectedIndices.Add(i);
        }
    }

    private void StartFcPermissionsUpdater()
    {
        var selected = GetSelectedFcPermsCharacters();
        if (selected.Count == 0)
            return;

        var steps = BuildFcPermissionsSteps(selected, plugin.TaskRunner);

        if (!plugin.TaskRunner.Start(global::XASlave.Localization.UiMessage.Key("SA6711086459806CB"), steps, onLog: (msg) =>
        {
            Plugin.Log.Information($"[TaskLogs] {msg}");
        }, totalItems: selected.Count, suppressLogoutCancel: true))
            return;

        HaltAutoCollectionForPriorityTask("FC Permissions Updater");
        reloggerRunList = new List<string>(selected);
    }

    private List<TaskStep> BuildFcPermissionsSteps(List<string> characters, TaskRunner runner)
    {
        var cfg = plugin.Configuration;
        var steps = new List<TaskStep>();

        // Disable AR Multi Mode
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

        for (int i = 0; i < characters.Count; i++)
        {
            var charName = characters[i];
            var charIndex = i + 1;
            var charTotal = characters.Count;

            // Label
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S5FB6F2415218C668", charIndex, charTotal, charName),
                OnEnter = () =>
                {
                    runner.CurrentItemLabel = $"[{charIndex}/{charTotal}] {charName}";
                    runner.AddLog($"-- Processing {charName} ({charIndex}/{charTotal}) --");
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });

            // Relog
            var relogReady = false;
            var relogFailed = false;
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SB8BF19049E887413", charName),
                OnEnter = () =>
                {
                    var current = MonthlyReloggerTask.GetCurrentCharacterNameWorld();
                    if (current.Equals(charName, StringComparison.OrdinalIgnoreCase))
                    {
                        runner.AddLog($"Already logged in as {charName}");
                        relogReady = true;
                        return;
                    }
                    runner.AddLog($"Relogging to {charName}...");
                    ChatHelper.SendMessage($"/ays relog {charName}");
                },
                IsComplete = () => true,
                TimeoutSec = 3f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S27A359F38EA15525", charName), 2.0f));

            // Wait for relog
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S4B35E54CFF820E18", charName),
                IsComplete = () =>
                {
                    if (relogReady) return true;
                    try
                    {
                        if (!Plugin.PlayerState.IsLoaded) return false;
                        if (!MonthlyReloggerTask.IsNamePlateReady() || !MonthlyReloggerTask.IsPlayerAvailable())
                            return false;
                        // Verify the intended character is actually loaded before applying FC
                        // permission edits; a silently-failed /ays relog would otherwise apply the
                        // destructive rank changes to whichever character is still logged in.
                        return MonthlyReloggerTask.GetCurrentCharacterNameWorld()
                            .Equals(charName, StringComparison.OrdinalIgnoreCase);
                    }
                    catch { return false; }
                },
                TimeoutSec = 600f,
                OnTimeout = () =>
                {
                    relogFailed = true;
                    runner.RecordFailedCharacter(charName);
                    runner.AddLog($"FAILED: Could not relog to {charName}. Skipping FC permission edits for this character.");
                },
            });

            // Guard the whole downstream FC-permission sequence behind relog success.
            var downstreamStart = steps.Count;
            var permissionFlowFailed = false;
            var ranksNavigationDispatched = false;
            var rankSelectionDispatched = false;
            var rankEditDispatched = false;
            var rankConfirmationDispatched = false;
            var contextMenuDispatched = false;
            var permissionsDispatched = false;

            void FailPermissionFlow(string message)
            {
                if (permissionFlowFailed)
                    return;

                permissionFlowFailed = true;
                if (!runner.IncompleteCharacters.Contains(charName, StringComparer.OrdinalIgnoreCase))
                    runner.RecordIncompleteCharacter(charName);
                runner.AddLog($"FAILED: {message} The permission update for {charName} is unverified; remaining callbacks were stopped.");
            }

            // SafeWait 3-pass
            foreach (var sw in MonthlyReloggerTask.BuildCharacterSafeWait3Pass(global::XASlave.Localization.UiMessage.Format("SFA31CB70A88EB43A", charName)))
            {
                steps.Add(sw);
            }

            // Open FC window
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SD9C5BF91F8698B01", charName),
                OnEnter = () =>
                {
                    runner.AddLog("Opening FC window...");
                    ChatHelper.SendMessage("/freecompanycmd");
                },
                IsComplete = () => AddonHelper.IsAddonVisible("FreeCompany"),
                TimeoutSec = 5f,
                OnTimeout = () => FailPermissionFlow("The Free Company window did not open."),
            });
            steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("SB2D58B61830F7784", charName), 0.5f));

            // FC callback sequence ("FreeCompany true 0 2")
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SCFEC22492021C846", charName),
                OnEnter = () =>
                {
                    runner.AddLog("Navigating to FC ranks...");
                    ranksNavigationDispatched = AddonHelper.IsAddonReady("FreeCompany") &&
                                                AddonHelper.FireCallbackTrueInt("FreeCompany", 0);
                },
                IsComplete = () => ranksNavigationDispatched && AddonHelper.IsAddonReady("FreeCompanyRank"),
                TimeoutSec = 2f,
                MaxRetries = 1,
                OnTimeout = () => FailPermissionFlow("The ranks tab did not open after its callback."),
            });
            steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S813F0477D6AF4612", charName), 0.3f));

            // FreeCompanyRank callbacks
            // ("FreeCompanyRank true 2 2 Undefined Undefined Undefined")
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SDCF062457237F08A", charName),
                OnEnter = () =>
                {
                    runner.AddLog("Selecting rank for editing...");
                    if (AddonHelper.IsAddonReady("FreeCompanyRank"))
                        rankSelectionDispatched = AddonHelper.FireCallback("FreeCompanyRank", 2, 2);
                },
                IsComplete = () => rankSelectionDispatched && AddonHelper.IsAddonReady("FreeCompanyRank"),
                TimeoutSec = 2f,
                MaxRetries = 1,
                OnTimeout = () => FailPermissionFlow("The target rank selection callback was not accepted."),
            });
            steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("SF28AD6531CF142D3", charName), 0.3f));

            // ("FreeCompanyRank true 4 3 1513 557 Undefined")
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SAD6EDB741A3F9278", charName),
                OnEnter = () =>
                {
                    if (AddonHelper.IsAddonReady("FreeCompanyRank"))
                        rankEditDispatched = AddonHelper.FireCallback("FreeCompanyRank", 4, 3, 1513, 557);
                },
                IsComplete = () => rankEditDispatched && AddonHelper.IsAddonReady("FreeCompanyRank"),
                TimeoutSec = 2f,
                MaxRetries = 1,
                OnTimeout = () => FailPermissionFlow("The target rank edit callback was not accepted."),
            });
            steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("SBC78A2CD15C1CA81", charName), 0.1f));

            // ("FreeCompanyRank true 3 2 Undefined Undefined Undefined")
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SB7BDB1754C1166E1", charName),
                OnEnter = () =>
                {
                    if (AddonHelper.IsAddonReady("FreeCompanyRank"))
                        rankConfirmationDispatched = AddonHelper.FireCallback("FreeCompanyRank", 3, 2);
                },
                IsComplete = () => rankConfirmationDispatched && AddonHelper.IsAddonReady("ContextMenu"),
                TimeoutSec = 2f,
                MaxRetries = 1,
                OnTimeout = () => FailPermissionFlow("The selected rank did not expose its context menu."),
            });
            steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("SEC10F2F8423DFD75", charName), 0.1f));

            // ("ContextMenu true 0 0 0 Undefined Undefined")
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S51C32F0EEFB50424", charName),
                OnEnter = () =>
                {
                    if (AddonHelper.IsAddonReady("ContextMenu"))
                        contextMenuDispatched = AddonHelper.FireCallback("ContextMenu", 0, 0, 0);
                },
                IsComplete = () => contextMenuDispatched && AddonHelper.IsAddonReady("FreeCompanyMemberRankEdit"),
                TimeoutSec = 2f,
                MaxRetries = 1,
                OnTimeout = () => FailPermissionFlow("The member-rank permissions editor did not open."),
            });
            steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S50A2BDAD7936C19C", charName), 0.1f));

            // Apply all permissions
            // ("FreeCompanyMemberRankEdit true 0 Undefined 1 1 1 1 1 1 1 1 1 1 1 1 3 1 1 1 1 1 3 1 1 1 1 1 1 1 1 -1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 -1 1 1 1")
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S05911A8BCCB22731", charName),
                OnEnter = () =>
                {
                    if (!AddonHelper.IsAddonReady("FreeCompanyMemberRankEdit") ||
                        !AddonHelper.AddonHasText("FreeCompanyMemberRankEdit", "Member", contains: true))
                    {
                        FailPermissionFlow("The open permissions editor could not be positively identified as the Member rank.");
                        return;
                    }

                    runner.AddLog("Applying all FC permissions to the verified Member rank editor...");
                    permissionsDispatched = AddonHelper.FireCallback("FreeCompanyMemberRankEdit",
                        0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 3, 1, 1, 1, 1, 1,
                        3, 1, 1, 1, 1, 1, 1, 1, 1, -1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                        1, 1, 1, 1, 1, 1, 1, 1, 1, -1, 1, 1, 1);
                },
                IsComplete = () => permissionFlowFailed ||
                                   permissionsDispatched && !AddonHelper.IsAddonReady("FreeCompanyMemberRankEdit"),
                TimeoutSec = 3f,
                MaxRetries = 1,
                OnTimeout = () => FailPermissionFlow("The verified Member rank permission callback was not accepted."),
            });
            steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("SF57943594251FB26", charName), 0.5f));

            // Close any remaining FC windows
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S2143476ACDD6160F", charName),
                OnEnter = () =>
                {
                    runner.AddLog("Closing FC windows...");
                    KeyInputHelper.PressKey(KeyInputHelper.VK_ESCAPE);
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S8EDC86C1D7989100", charName), 0.5f));
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S432876B47B97C02C", charName),
                OnEnter = () => KeyInputHelper.PressKey(KeyInputHelper.VK_ESCAPE),
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S53ABD5F8B5ACDD80", charName), 0.5f));

            // If relogging or any expected UI transition fails, skip the remaining permission
            // sequence so a later callback can never land on a stale or unintended rank editor.
            for (var s = downstreamStart; s < steps.Count; s++)
                steps[s] = MonthlyReloggerTask.WithSkip(steps[s], () => relogFailed || permissionFlowFailed);

            // Mark complete
            var capturedIndex = charIndex;
            var capturedName = charName;
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SB5063DC812CCDD2B", capturedName),
                OnEnter = () =>
                {
                    runner.CompletedItems = capturedIndex;
                    // Read the live relogFailed (set by the Wait Relog OnTimeout at runtime), not a
                    // build-time snapshot, so a failed character is reported as skipped.
                    if (relogFailed)
                    {
                        runner.AddLog($"Skipped {capturedName} ({capturedIndex}/{charTotal}) - relog failed");
                        return;
                    }
                    if (permissionFlowFailed)
                    {
                        runner.AddLog($"Skipped {capturedName} ({capturedIndex}/{charTotal}) - permission UI verification failed");
                        return;
                    }
                    runner.AddLog($"Finished {capturedName} ({capturedIndex}/{charTotal})");
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
        }

        // Summary
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S065ED4E38ED2B325"),
            OnEnter = () =>
            {
                if (!MonthlyReloggerTask.ShouldKeepLogoutCancelSuppressed(cfg.FcPermsLogoutOnComplete, cfg.FcPermsKillGameOnComplete))
                    runner.SuppressLogoutCancel = false;
                runner.AddLog($"══ SUMMARY: processed {characters.Count} character(s); verify failed/incomplete rows above before treating permissions as updated ══");
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });

        MonthlyReloggerTask.AddSharedCompletionSteps(
            steps,
            runner,
            cfg.FcPermsLogoutOnComplete,
            cfg.FcPermsKillGameOnComplete,
            cfg.FcPermsEnableArMultiOnComplete);

        return steps;
    }
}
