using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private const string EurekaLogogramCreatorFavoriteDragDropType = "EurekaLogogramCreatorFavoritePlateIndex";
    private string eurekaLogogramCreatorActionFilter = string.Empty;
    private string eurekaLogogramCreatorRecipeFilter = string.Empty;
    private string eurekaLogogramCreatorFavoritePlateName = string.Empty;
    private readonly Dictionary<int, string> eurekaLogogramCreatorFavoritePlateRenameInputs = [];
    private bool eurekaLogogramCreatorHowToUseExpanded = true;

    private void DrawEurekaLogogramCreatorTask()
    {
        var logogramCreator = plugin.EurekaLogogramCreator;
        var fontScaling = ImGui.GetFontSize() / 17f;
        var isInManipulator = logogramCreator.IsManipulatorVisible();

        ImGui.TextColored(new Vector4(0.82f, 1.0f, 0.55f, 1.0f), "Logogram Creator");
        ImGui.TextDisabled("Field Operations task for Eureka Logos plate setup, queueing, and extraction.");

        if (ImGui.Button(
                eurekaLogogramCreatorHowToUseExpanded
                    ? "How to use [-]##EurekaLogogramCreatorHowToUse"
                    : "How to use [+]##EurekaLogogramCreatorHowToUse"))
        {
            eurekaLogogramCreatorHowToUseExpanded = !eurekaLogogramCreatorHowToUseExpanded;
        }

        if (eurekaLogogramCreatorHowToUseExpanded)
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextDisabled("Go to a Logos Manipulator in Eureka to use the live synthesis and extraction workflow.");
            ImGui.TextDisabled("Favorites, recipe locks, and source-logogram prices can be managed here even before you walk up to the manipulator.");
            ImGui.TextDisabled("Once the manipulator is open, XA Slave unlocks the live action list, stock scan, queue processing, and extraction automation tabs.");
            ImGui.PopTextWrapPos();
        }

        ImGui.Separator();

        if (!isInManipulator)
        {
            ImGui.TextColored(
                new Vector4(1.0f, 0.65f, 0.25f, 1.0f),
                "Open the Logos Manipulator to unlock the live crafting tabs.");
        }

        DrawEurekaLogogramCreatorToolbar(fontScaling, isInManipulator);

        if (!ImGui.BeginTabBar("##EurekaLogogramCreatorTabs"))
            return;

        if (ImGui.BeginTabItem("Favorites"))
        {
            DrawEurekaLogogramCreatorFavoritesTab(fontScaling);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Recipes"))
        {
            DrawEurekaLogogramCreatorRecipePreferencesTab(fontScaling);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Costs"))
        {
            DrawEurekaLogogramCreatorCostsTab(fontScaling);
            ImGui.EndTabItem();
        }

        if (isInManipulator && ImGui.BeginTabItem("Logos Actions"))
        {
            DrawEurekaLogogramCreatorLogosActionsTab(fontScaling);
            ImGui.EndTabItem();
        }

        if (isInManipulator && ImGui.BeginTabItem("Logogram Inventory"))
        {
            DrawEurekaLogogramCreatorInventoryTab(fontScaling);
            ImGui.EndTabItem();
        }

        if (isInManipulator && ImGui.BeginTabItem("Queue"))
        {
            DrawEurekaLogogramCreatorQueueTab(fontScaling);
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawEurekaLogogramCreatorToolbar(float fontScaling, bool isInManipulator)
    {
        var logogramCreator = plugin.EurekaLogogramCreator;

        if (isInManipulator)
        {
            if (ImGui.Button("Refresh All Pages"))
            {
                logogramCreator.RefreshKnownDataNow();
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Scans the currently open manipulator pages for stock and logos-action usage.");
            }
        }

        var autoRefreshAllPagesOnOpen = logogramCreator.Configuration.AutoRefreshAllPagesOnOpen;
        if (ImGui.Checkbox("Auto Refresh On Open", ref autoRefreshAllPagesOnOpen))
        {
            logogramCreator.SetAutoRefreshAllPagesOnOpen(autoRefreshAllPagesOnOpen);
        }

        ImGui.SameLine();
        var autoDestroyWhenFull = logogramCreator.Configuration.AutoDestroyWhenMagiaBoardFull;
        if (ImGui.Checkbox("Auto Destroy When Full", ref autoDestroyWhenFull))
        {
            logogramCreator.SetAutoDestroyWhenMagiaBoardFull(autoDestroyWhenFull);
        }

        var autoRetryFailedExtraction = logogramCreator.Configuration.AutoRetryFailedExtraction;
        if (ImGui.Checkbox("Retry Failed Extraction", ref autoRetryFailedExtraction))
        {
            logogramCreator.SetAutoRetryFailedExtraction(autoRetryFailedExtraction);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("After a failed extraction, acknowledge it and rebuild the same plate automatically if the cache still says mats remain.");
        }

        ImGui.SameLine();
        var showFavoritesOverlay = logogramCreator.Configuration.ShowFavoritesOverlay;
        if (ImGui.Checkbox("Show Favorites Overlay", ref showFavoritesOverlay))
        {
            logogramCreator.SetShowFavoritesOverlay(showFavoritesOverlay);
        }

        ImGui.SetNextItemWidth(210 * fontScaling);
        var queueStepFrameDelay = logogramCreator.QueueStepFrameDelayFrames;
        if (ImGui.SliderInt("Step Delay (Less is faster)", ref queueStepFrameDelay, 1, 120))
        {
            logogramCreator.SetQueueStepFrameDelay(queueStepFrameDelay);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Minimum framework frames to wait between queue actions and shard-page scan clicks.");
        }

        if (logogramCreator.HasActiveOrQueuedAutoLogoAction)
        {
            if (ImGui.Button("Cancel Active Run", new Vector2(150 * fontScaling, 0)))
            {
                logogramCreator.CancelAutoLogoAction();
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Stops the current extraction run and clears all queued plates.");
            }
        }

        var logosActionsColor = logogramCreator.IsMagiaBoardFull
            ? new Vector4(1.0f, 0.55f, 0.2f, 1.0f)
            : new Vector4(0.7f, 0.7f, 0.7f, 1.0f);
        ImGui.TextColored(logosActionsColor, $"Logos Actions: {logogramCreator.LogosActionSlotsUsed}/{logogramCreator.LogosActionSlotCapacity}");

        var cacheColor = logogramCreator.HasLogogramStockCache
            ? new Vector4(0.2f, 0.9f, 0.4f, 1.0f)
            : new Vector4(1.0f, 0.6f, 0.2f, 1.0f);
        ImGui.SameLine();
        ImGui.TextColored(cacheColor, logogramCreator.HasLogogramStockCache ? "Stock Cache: Ready" : "Stock Cache: Missing");

        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), logogramCreator.LastStatus);
        ImGui.Separator();
    }

    private void DrawEurekaLogogramCreatorPlateBuilder(float fontScaling)
    {
        var logogramCreator = plugin.EurekaLogogramCreator;
        var astralLabel = logogramCreator.PendingAstralActionId.HasValue
            ? logogramCreator.GetActionName(logogramCreator.PendingAstralActionId.Value)
            : "Empty";
        var umbralLabel = logogramCreator.PendingUmbralActionId.HasValue
            ? logogramCreator.GetActionName(logogramCreator.PendingUmbralActionId.Value)
            : "Empty";
        var craftableCount = logogramCreator.GetPendingPlateCraftableCount();
        var canStartPlate = logogramCreator.CanQueuePendingPlate(out var pendingPlateError);
        var hasPendingSelection = logogramCreator.PendingAstralActionId.HasValue || logogramCreator.PendingUmbralActionId.HasValue;
        var craftableColor = craftableCount > 0
            ? new Vector4(0.0f, 1.0f, 0.0f, 1.0f)
            : new Vector4(1.0f, 0.6f, 0.2f, 1.0f);
        var pendingPlateBareMinimumLabel = hasPendingSelection && logogramCreator.TryGetPendingPlateBareMinimumGilCost(out var pendingPlateBareMinimumCost)
            ? logogramCreator.FormatGilCost(pendingPlateBareMinimumCost)
            : "0 gil";
        var pendingPlateRecipeCostLabel = hasPendingSelection && logogramCreator.TryGetPendingPlateRecipeGilCost(out var pendingPlateRecipeCost)
            ? logogramCreator.FormatGilCost(pendingPlateRecipeCost)
            : "0 gil";

        ImGui.Text($"Astral: {astralLabel}");
        ImGui.Text($"Umbral: {umbralLabel}");
        ImGui.TextColored(craftableColor, $"Current Plate: {logogramCreator.GetPendingPlateDescription()} | Craftable: {craftableCount}");
        ImGui.TextColored(new Vector4(0.9f, 0.8f, 0.45f, 1.0f), $"Current Plate Costs: Bare Minimum {pendingPlateBareMinimumLabel} | Recipe Cost {pendingPlateRecipeCostLabel}");

        ImGui.BeginDisabled(!canStartPlate);
        if (ImGui.Button("Start", new Vector2(90 * fontScaling, 0)))
        {
            logogramCreator.QueuePendingPlate();
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Clear Selection", new Vector2(130 * fontScaling, 0)))
        {
            logogramCreator.ClearPendingPlateSelection();
        }

        ImGui.SameLine();
        if (ImGui.Button("Add Selection To Fav Bar", new Vector2(170 * fontScaling, 0)))
        {
            var plateName = string.IsNullOrWhiteSpace(eurekaLogogramCreatorFavoritePlateName)
                ? logogramCreator.GetPendingPlateDescription()
                : eurekaLogogramCreatorFavoritePlateName;
            logogramCreator.SaveCurrentQueueAsFavoritePlate(plateName);
        }

        if (!canStartPlate && hasPendingSelection && !string.IsNullOrWhiteSpace(pendingPlateError))
        {
            ImGui.TextColored(new Vector4(1.0f, 0.35f, 0.35f, 1.0f), pendingPlateError);
        }

        ImGui.Separator();
    }

    private void DrawEurekaLogogramCreatorFavoritesTab(float fontScaling)
    {
        var logogramCreator = plugin.EurekaLogogramCreator;

        ImGui.TextDisabled("Favorite plates save one Astral / Umbral build so they can be queued directly from the manipulator.");
        ImGui.TextDisabled("Recipe Cost matches the higher gil basis shown in the Recipes tab. Bare Minimum assumes the fastest successful appraisals.");

        ImGui.PushItemWidth(300 * fontScaling);
        ImGui.InputTextWithHint("##EurekaLogogramCreatorFavoritePlateName", "Favorite plate name...", ref eurekaLogogramCreatorFavoritePlateName, 100);
        ImGui.PopItemWidth();

        ImGui.SameLine();
        if (ImGui.Button("Add Current Selection"))
        {
            var plateName = string.IsNullOrWhiteSpace(eurekaLogogramCreatorFavoritePlateName)
                ? logogramCreator.GetPendingPlateDescription()
                : eurekaLogogramCreatorFavoritePlateName;
            logogramCreator.SaveCurrentQueueAsFavoritePlate(plateName);
            if (!string.IsNullOrWhiteSpace(eurekaLogogramCreatorFavoritePlateName))
            {
                eurekaLogogramCreatorFavoritePlateName = string.Empty;
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Saves the currently selected Astral / Umbral plate.");
        }

        ImGui.Separator();
        DrawEurekaLogogramCreatorPlateBuilder(fontScaling);

        if (logogramCreator.Configuration.FavoritePlates.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "No favorite plates saved yet.");
            return;
        }

        ImGui.BeginChild("##EurekaLogogramCreatorFavoritePlates", new Vector2(0, 0), true);

        for (var i = 0; i < logogramCreator.Configuration.FavoritePlates.Count; i++)
        {
            var plate = logogramCreator.Configuration.FavoritePlates[i];
            var (astralActionId, umbralActionId) = GetEurekaLogogramCreatorFavoriteActionIds(plate);
            var plateLabel = logogramCreator.DescribeFavoritePlate(plate);
            var astralSummary = astralActionId.HasValue ? logogramCreator.GetActionName(astralActionId.Value) : "Empty";
            var umbralSummary = umbralActionId.HasValue ? logogramCreator.GetActionName(umbralActionId.Value) : "Empty";
            var astralBareMinimumLabel = !astralActionId.HasValue
                ? "0 gil"
                : logogramCreator.TryGetActionBareMinimumGilCost(astralActionId.Value, out var astralBareMinimumCost)
                    ? logogramCreator.FormatGilCost(astralBareMinimumCost)
                    : "Cost N/A";
            var astralRecipeCostLabel = !astralActionId.HasValue
                ? "0 gil"
                : logogramCreator.TryGetActionRecipeGilCost(astralActionId.Value, out var astralRecipeCost)
                    ? logogramCreator.FormatGilCost(astralRecipeCost)
                    : "Cost N/A";
            var umbralBareMinimumLabel = !umbralActionId.HasValue
                ? "0 gil"
                : logogramCreator.TryGetActionBareMinimumGilCost(umbralActionId.Value, out var umbralBareMinimumCost)
                    ? logogramCreator.FormatGilCost(umbralBareMinimumCost)
                    : "Cost N/A";
            var umbralRecipeCostLabel = !umbralActionId.HasValue
                ? "0 gil"
                : logogramCreator.TryGetActionRecipeGilCost(umbralActionId.Value, out var umbralRecipeCost)
                    ? logogramCreator.FormatGilCost(umbralRecipeCost)
                    : "Cost N/A";
            var plateBareMinimumLabel = logogramCreator.TryGetFavoritePlateBareMinimumGilCost(plate, out var plateBareMinimumCost)
                ? logogramCreator.FormatGilCost(plateBareMinimumCost)
                : "Cost N/A";
            var plateRecipeCostLabel = logogramCreator.TryGetFavoritePlateRecipeGilCost(plate, out var plateRecipeCost)
                ? logogramCreator.FormatGilCost(plateRecipeCost)
                : "Cost N/A";
            if (!eurekaLogogramCreatorFavoritePlateRenameInputs.TryGetValue(i, out var renameInput))
            {
                renameInput = plate.Name;
            }

            ImGui.PushID(i);
            ImGui.Text(plateLabel);
            if (ImGui.BeginDragDropSource())
            {
                ImGui.SetDragDropPayload(EurekaLogogramCreatorFavoriteDragDropType, BitConverter.GetBytes(i));

                ImGui.TextUnformatted($"Move {plateLabel}");
                ImGui.EndDragDropSource();
            }

            if (ImGui.BeginDragDropTarget())
            {
                unsafe
                {
                    var payload = ImGui.AcceptDragDropPayload(EurekaLogogramCreatorFavoriteDragDropType);
                    if (payload.Data != null && payload.DataSize == sizeof(int))
                    {
                        var sourceIndex = Marshal.ReadInt32((IntPtr)payload.Data);
                        var rowMidpointY = (ImGui.GetItemRectMin().Y + ImGui.GetItemRectMax().Y) * 0.5f;
                        var insertIndex = ImGui.GetMousePos().Y >= rowMidpointY ? i + 1 : i;
                        if (logogramCreator.InsertFavoritePlateAt(sourceIndex, insertIndex))
                        {
                            ResetEurekaLogogramCreatorFavoriteRenameInputs();
                            ImGui.EndDragDropTarget();
                            ImGui.PopID();
                            break;
                        }
                    }
                }

                ImGui.EndDragDropTarget();
            }

            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), $"Umbral: {umbralSummary} | Bare Minimum: {umbralBareMinimumLabel} | Recipe Cost: {umbralRecipeCostLabel}");
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), $"Astral: {astralSummary} | Bare Minimum: {astralBareMinimumLabel} | Recipe Cost: {astralRecipeCostLabel}");
            ImGui.TextColored(new Vector4(0.9f, 0.8f, 0.45f, 1.0f), $"Plate Costs: Bare Minimum {plateBareMinimumLabel} | Recipe Cost {plateRecipeCostLabel}");
            ImGui.TextDisabled("Drag this row to reorder it, or use Up / Down.");

            ImGui.PushItemWidth(220 * fontScaling);
            if (ImGui.InputTextWithHint("##FavoriteRename", "Custom button name...", ref renameInput, 100, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                logogramCreator.RenameFavoritePlate(i, renameInput);
            }
            eurekaLogogramCreatorFavoritePlateRenameInputs[i] = renameInput;
            ImGui.PopItemWidth();

            ImGui.SameLine();
            if (ImGui.Button("Rename"))
            {
                logogramCreator.RenameFavoritePlate(i, renameInput);
            }

            ImGui.SameLine();
            ImGui.BeginDisabled(i == 0);
            if (ImGui.Button("Up"))
            {
                if (logogramCreator.MoveFavoritePlate(i, i - 1))
                {
                    ResetEurekaLogogramCreatorFavoriteRenameInputs();
                    ImGui.EndDisabled();
                    ImGui.PopID();
                    break;
                }
            }
            ImGui.EndDisabled();

            ImGui.SameLine();
            ImGui.BeginDisabled(i >= logogramCreator.Configuration.FavoritePlates.Count - 1);
            if (ImGui.Button("Down"))
            {
                if (logogramCreator.MoveFavoritePlate(i, i + 1))
                {
                    ResetEurekaLogogramCreatorFavoriteRenameInputs();
                    ImGui.EndDisabled();
                    ImGui.PopID();
                    break;
                }
            }
            ImGui.EndDisabled();

            if (ImGui.Button("Queue Plate"))
            {
                logogramCreator.QueueFavoritePlate(plate);
            }

            ImGui.SameLine();
            if (ImGui.Button("Update From Selection"))
            {
                logogramCreator.UpsertFavoritePlate(plate.Name, logogramCreator.PendingAstralActionId, logogramCreator.PendingUmbralActionId);
            }

            ImGui.SameLine();
            if (ImGui.Button("Delete"))
            {
                logogramCreator.DeleteFavoritePlate(i);
                ResetEurekaLogogramCreatorFavoriteRenameInputs();
                ImGui.PopID();
                break;
            }

            ImGui.Separator();
            ImGui.PopID();
        }

        var validPlateCount = logogramCreator.Configuration.FavoritePlates.Count;
        foreach (var staleKey in eurekaLogogramCreatorFavoritePlateRenameInputs.Keys.Where(x => x >= validPlateCount).ToList())
        {
            eurekaLogogramCreatorFavoritePlateRenameInputs.Remove(staleKey);
        }

        ImGui.EndChild();
    }

    private void ResetEurekaLogogramCreatorFavoriteRenameInputs()
    {
        eurekaLogogramCreatorFavoritePlateRenameInputs.Clear();
    }

    private void DrawEurekaLogogramCreatorLogosActionsTab(float fontScaling)
    {
        var logogramCreator = plugin.EurekaLogogramCreator;

        DrawEurekaLogogramCreatorPlateBuilder(fontScaling);

        ImGui.PushItemWidth(320 * fontScaling);
        ImGui.InputTextWithHint("##EurekaLogogramCreatorActionFilter", "Filter actions...", ref eurekaLogogramCreatorActionFilter, 64, ImGuiInputTextFlags.AutoSelectAll);
        ImGui.PopItemWidth();

        ImGui.Separator();

        var actionSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        ImGui.BeginChild("##EurekaLogogramCreatorActionsList", new Vector2(0, -30 * fontScaling), true);

        foreach (var action in logogramCreator.LogosActions)
        {
            var actionName = actionSheet != null && actionSheet.TryGetRow(action.Id, out var actionRow)
                ? actionRow.Name.ExtractText()
                : $"Action {action.Id}";

            if (!string.IsNullOrEmpty(eurekaLogogramCreatorActionFilter) &&
                !actionName.Contains(eurekaLogogramCreatorActionFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            DrawEurekaLogogramCreatorActionRow(action, actionName, fontScaling);
        }

        ImGui.EndChild();

        if (logogramCreator.IsProcessingQueue)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.0f, 1.0f), "Processing plate queue...");
        }
        else if (logogramCreator.SynthesisQueue.Count > 0)
        {
            ImGui.Text($"Queue: {logogramCreator.SynthesisQueue.Count} plates pending");
            ImGui.SameLine();
            if (ImGui.Button("Process Queue"))
            {
                logogramCreator.ProcessSynthesisQueue();
            }
        }
    }

    private void DrawEurekaLogogramCreatorActionRow(LogosAction action, string actionName, float fontScaling)
    {
        var logogramCreator = plugin.EurekaLogogramCreator;
        var actionButtonHeight = MathF.Max(18 * fontScaling, ImGui.GetTextLineHeight() + (4 * fontScaling));
        var currentStock = logogramCreator.LogosActionStock.TryGetValue(action.Id, out var ownedCount) ? ownedCount : 0;
        var resolvedRecipe = logogramCreator.GetResolvedRecipe(action, out var recipeIndex);
        var maxCraftable = logogramCreator.GetCraftableCount(resolvedRecipe);
        var canCraft = maxCraftable > 0;
        var isLockedRecipe = logogramCreator.GetPreferredRecipeIndex(action.Id) >= 0;
        var recipeMode = isLockedRecipe
            ? $"Locked Recipe {recipeIndex + 1}"
            : EurekaLogogramCreatorService.AutomaticRecipeModeLabel;

        var texture = Plugin.TextureProvider.GetFromGameIcon(action.IconID).GetWrapOrEmpty();
        ImGui.Image(texture.Handle, new Vector2(32, 32) * fontScaling);

        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.Text(actionName);
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), $"(Owned: {currentStock})");

        if (resolvedRecipe != null)
        {
            var recipeText = logogramCreator.FormatRecipe(resolvedRecipe);
            var bareMinimumCost = logogramCreator.TryGetRecipeBareMinimumGilCost(resolvedRecipe, out var bareMinimumGilCost)
                ? logogramCreator.FormatGilCost(bareMinimumGilCost)
                : "Cost N/A";
            var recipeColor = canCraft
                ? new Vector4(0.0f, 1.0f, 0.0f, 1.0f)
                : new Vector4(1.0f, 0.2f, 0.2f, 1.0f);
            ImGui.TextColored(recipeColor, $"{recipeMode}: {maxCraftable} | Bare Minimum {bareMinimumCost} | {recipeText}");
        }
        else
        {
            ImGui.TextColored(new Vector4(1.0f, 0.2f, 0.2f, 1.0f), "No recipe data");
        }

        ImGui.EndGroup();

        var buttonWidth = 70 * fontScaling;
        var soloWidth = 60 * fontScaling;
        var startX = MathF.Max(ImGui.GetCursorPosX(), ImGui.GetWindowWidth() - (buttonWidth * 2) - soloWidth - 54 * fontScaling);
        ImGui.SameLine(startX);

        DrawEurekaLogogramCreatorAssignButton(action.Id, PlateSide.Umbral, "Umbral", buttonWidth, actionButtonHeight, canCraft);
        ImGui.SameLine();
        DrawEurekaLogogramCreatorAssignButton(action.Id, PlateSide.Astral, "Astral", buttonWidth, actionButtonHeight, canCraft);
        ImGui.SameLine();
        DrawEurekaLogogramCreatorSoloButton(action.Id, soloWidth, actionButtonHeight, canCraft);

        ImGui.Separator();
    }

    private void DrawEurekaLogogramCreatorAssignButton(uint actionId, PlateSide side, string label, float buttonWidth, float buttonHeight, bool canCraft)
    {
        var logogramCreator = plugin.EurekaLogogramCreator;

        if (canCraft)
        {
            if (ImGui.Button($"{label}##{side}_{actionId}", new Vector2(buttonWidth, buttonHeight)))
            {
                logogramCreator.SetPendingPlateSelection(side, actionId);
            }

            return;
        }

        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.5f);
        ImGui.Button($"Need Mats##{side}_{actionId}", new Vector2(buttonWidth, buttonHeight));
        ImGui.PopStyleVar();
    }

    private void DrawEurekaLogogramCreatorSoloButton(uint actionId, float buttonWidth, float buttonHeight, bool canCraft)
    {
        var logogramCreator = plugin.EurekaLogogramCreator;

        ImGui.BeginDisabled(!canCraft);
        if (ImGui.Button($"Solo##{actionId}", new Vector2(buttonWidth, buttonHeight)))
        {
            logogramCreator.QueueSynthesis(actionId);
        }
        ImGui.EndDisabled();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Queues this action by itself into the Astral Array and starts immediately when possible.");
        }
    }

    private void DrawEurekaLogogramCreatorRecipePreferencesTab(float fontScaling)
    {
        var logogramCreator = plugin.EurekaLogogramCreator;
        var automaticRecipeModeLabel = EurekaLogogramCreatorService.AutomaticRecipeModeLabel;

        ImGui.TextDisabled("Lock a specific synthesis recipe per action. Automatic mode uses Cheapest By Gil and the source-logogram prices from the Costs tab.");
        ImGui.PushItemWidth(320 * fontScaling);
        ImGui.InputTextWithHint("##EurekaLogogramCreatorRecipeFilter", "Filter actions...", ref eurekaLogogramCreatorRecipeFilter, 64, ImGuiInputTextFlags.AutoSelectAll);
        ImGui.PopItemWidth();
        ImGui.Separator();

        var actionSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        ImGui.BeginChild("##EurekaLogogramCreatorRecipePrefs", new Vector2(0, 0), true);

        foreach (var action in logogramCreator.LogosActions)
        {
            var actionName = actionSheet != null && actionSheet.TryGetRow(action.Id, out var actionRow)
                ? actionRow.Name.ExtractText()
                : $"Action {action.Id}";
            if (!string.IsNullOrEmpty(eurekaLogogramCreatorRecipeFilter) &&
                !actionName.Contains(eurekaLogogramCreatorRecipeFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ImGui.PushID((int)action.Id);
            ImGui.Text(actionName);

            var selectedRecipeIndex = logogramCreator.GetPreferredRecipeIndex(action.Id);
            var automaticRecipePreview = GetEurekaLogogramCreatorAutomaticRecipePreviewLabel(action, automaticRecipeModeLabel);
            var preview = selectedRecipeIndex >= 0 && selectedRecipeIndex < action.Recipes.Count
                ? GetEurekaLogogramCreatorRecipeChoiceLabel(selectedRecipeIndex, action.Recipes[selectedRecipeIndex], logogramCreator.GetCraftableCount(action.Recipes[selectedRecipeIndex]))
                : automaticRecipePreview;

            if (ImGui.BeginCombo("##RecipeChoice", preview))
            {
                var automaticSelected = selectedRecipeIndex < 0;
                if (ImGui.Selectable(automaticRecipePreview, automaticSelected))
                {
                    logogramCreator.SetPreferredRecipeIndex(action.Id, -1);
                }

                for (var i = 0; i < action.Recipes.Count; i++)
                {
                    var recipe = action.Recipes[i];
                    var craftableCount = logogramCreator.GetCraftableCount(recipe);
                    var itemLabel = GetEurekaLogogramCreatorRecipeChoiceLabel(i, recipe, craftableCount);
                    if (ImGui.Selectable(itemLabel, selectedRecipeIndex == i))
                    {
                        logogramCreator.SetPreferredRecipeIndex(action.Id, i);
                    }
                }

                ImGui.EndCombo();
            }

            var resolvedRecipe = logogramCreator.GetResolvedRecipe(action, out var resolvedRecipeIndex);
            if (resolvedRecipe != null)
            {
                var resolvedRecipeCostLabel = logogramCreator.TryGetRecipeGilCost(resolvedRecipe, out var resolvedRecipeCost)
                    ? logogramCreator.FormatGilCost(resolvedRecipeCost)
                    : "Cost N/A";
                var recipeColor = logogramCreator.IsRecipeCraftable(resolvedRecipe)
                    ? new Vector4(0.0f, 1.0f, 0.0f, 1.0f)
                    : new Vector4(1.0f, 0.2f, 0.2f, 1.0f);
                ImGui.TextColored(
                    recipeColor,
                    $"{(logogramCreator.GetPreferredRecipeIndex(action.Id) >= 0 ? $"Locked Recipe {resolvedRecipeIndex + 1}" : automaticRecipeModeLabel)}: {resolvedRecipeCostLabel} | {logogramCreator.FormatRecipe(resolvedRecipe)}");
            }

            ImGui.Separator();
            ImGui.PopID();
        }

        ImGui.EndChild();
    }

    private void DrawEurekaLogogramCreatorCostsTab(float fontScaling)
    {
        var logogramCreator = plugin.EurekaLogogramCreator;

        ImGui.TextDisabled("Automatic recipe selection uses these source-logogram prices as its gil basis.");

        if (ImGui.Button("Reset Costs To Defaults", new Vector2(180 * fontScaling, 0)))
        {
            logogramCreator.ResetLogogramSourceGilCosts();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Restores the default source-logogram prices used to seed the configuration.");
        }

        ImGui.Separator();
        ImGui.TextDisabled("Expected specific mneme cost = source logogram cost x source pool size.");

        ImGui.BeginChild("##EurekaLogogramCreatorCostsTab", new Vector2(0, 0), true);

        if (ImGui.BeginTable("##EurekaLogogramCreatorLogogramCosts", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Source");
            ImGui.TableSetupColumn("Pool");
            ImGui.TableSetupColumn("Cost Each");
            ImGui.TableSetupColumn("Specific Mneme Cost");
            ImGui.TableHeadersRow();

            foreach (var source in logogramCreator.GetLogogramSourceDefinitions())
            {
                var poolSize = logogramCreator.GetLogogramSourcePoolSize(source.ItemId);
                var configuredCost = logogramCreator.GetConfiguredLogogramSourceGilCost(source.ItemId);
                var specificMnemeCost = logogramCreator.GetConfiguredSpecificMnemeGilCost(source.ItemId);

                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.Text(source.Name);

                ImGui.TableNextColumn();
                ImGui.Text(poolSize > 0 ? poolSize.ToString() : "?");

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-float.Epsilon);
                if (ImGui.InputInt($"##SourceCost_{source.ItemId}", ref configuredCost, 100, 1000))
                {
                    logogramCreator.SetLogogramSourceGilCost(source.ItemId, configuredCost);
                }

                ImGui.TableNextColumn();
                ImGui.Text(poolSize > 0 ? logogramCreator.FormatGilCost(specificMnemeCost) : "N/A");
            }

            ImGui.EndTable();
        }

        ImGui.EndChild();
    }

    private void DrawEurekaLogogramCreatorInventoryTab(float fontScaling)
    {
        var logogramCreator = plugin.EurekaLogogramCreator;

        ImGui.Text($"Known Logogram Stock ({logogramCreator.LogogramStock.Count}/28):");
        if (!logogramCreator.HasLogogramStockCache)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.6f, 0.2f, 1.0f), "Run Refresh All Pages once to build the cache.");
        }

        ImGui.Separator();
        ImGui.BeginChild("##EurekaLogogramCreatorLogogramList", new Vector2(0, 0), true);
        var itemSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();

        foreach (var kvp in logogramCreator.LogogramStock.OrderBy(x =>
                     logogramCreator.Logograms.TryGetValue(x.Key, out var knownLogogram) ? knownLogogram.Name : x.Key.ToString()))
        {
            if (!logogramCreator.Logograms.TryGetValue(kvp.Key, out var logogram))
            {
                continue;
            }

            var iconId = 65000u;
            if (itemSheet != null && itemSheet.TryGetRow((uint)logogram.Id, out var itemRow) && itemRow.Icon > 0)
            {
                iconId = itemRow.Icon;
            }

            var texture = Plugin.TextureProvider.GetFromGameIcon(iconId).GetWrapOrEmpty();
            ImGui.Image(texture.Handle, new Vector2(24, 24) * fontScaling);
            ImGui.SameLine();
            ImGui.Text($"{logogram.Name}: {kvp.Value}");
        }

        ImGui.EndChild();
    }

    private void DrawEurekaLogogramCreatorQueueTab(float fontScaling)
    {
        var logogramCreator = plugin.EurekaLogogramCreator;

        DrawEurekaLogogramCreatorPlateBuilder(fontScaling);

        ImGui.Text("Queued Plates:");
        ImGui.Separator();

        if (logogramCreator.SynthesisQueue.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "Queue is empty");
            return;
        }

        ImGui.BeginChild("##EurekaLogogramCreatorQueueList", new Vector2(0, -40 * fontScaling), true);

        var queueList = logogramCreator.SynthesisQueue.ToList();
        for (var i = 0; i < queueList.Count; i++)
        {
            var request = queueList[i];
            ImGui.Text($"{i + 1}. {request.Label}");
            if (request.Astral != null)
            {
                ImGui.TextColored(
                    new Vector4(0.7f, 0.7f, 0.7f, 1.0f),
                    $"Astral: {request.Astral.ActionName} (Recipe {request.Astral.RecipeIndex + 1}: {logogramCreator.FormatRecipe(request.Astral.Recipe)})");
            }

            if (request.Umbral != null)
            {
                ImGui.TextColored(
                    new Vector4(0.7f, 0.7f, 0.7f, 1.0f),
                    $"Umbral: {request.Umbral.ActionName} (Recipe {request.Umbral.RecipeIndex + 1}: {logogramCreator.FormatRecipe(request.Umbral.Recipe)})");
            }

            ImGui.Separator();
        }

        ImGui.EndChild();

        if (ImGui.Button("Clear Queue", new Vector2(110 * fontScaling, 0)))
        {
            logogramCreator.SynthesisQueue.Clear();
        }

        ImGui.SameLine();
        if (ImGui.Button("Process Queue", new Vector2(120 * fontScaling, 0)))
        {
            logogramCreator.ProcessSynthesisQueue();
        }
    }

    private (uint? AstralActionId, uint? UmbralActionId) GetEurekaLogogramCreatorFavoriteActionIds(FavoritePlate plate)
    {
        uint? astralActionId = plate.AstralActionId;
        uint? umbralActionId = plate.UmbralActionId;

        if (!astralActionId.HasValue && plate.ActionIds.Count > 0)
        {
            astralActionId = plate.ActionIds[0];
        }

        if (!umbralActionId.HasValue && plate.ActionIds.Count > 1)
        {
            umbralActionId = plate.ActionIds[1];
        }

        return (astralActionId, umbralActionId);
    }

    private string GetEurekaLogogramCreatorAutomaticRecipePreviewLabel(LogosAction action, string automaticRecipeModeLabel)
    {
        var logogramCreator = plugin.EurekaLogogramCreator;
        var resolvedRecipe = logogramCreator.GetResolvedRecipe(action, out _);
        if (resolvedRecipe == null || !logogramCreator.TryGetRecipeGilCost(resolvedRecipe, out var gilCost))
        {
            return $"Automatic ({automaticRecipeModeLabel})";
        }

        return $"Automatic ({automaticRecipeModeLabel}) - {logogramCreator.FormatGilCost(gilCost)}";
    }

    private string GetEurekaLogogramCreatorRecipeChoiceLabel(int recipeIndex, IReadOnlyList<Recipe> recipe, int craftableCount)
    {
        var logogramCreator = plugin.EurekaLogogramCreator;
        var gilCostLabel = logogramCreator.TryGetRecipeGilCost(recipe, out var gilCost)
            ? logogramCreator.FormatGilCost(gilCost)
            : "Cost N/A";

        return $"Recipe {recipeIndex + 1} [{craftableCount} | {gilCostLabel}] - {logogramCreator.FormatRecipe(recipe)}";
    }
}
