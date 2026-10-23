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
    private readonly Dictionary<FavoritePlate, string> eurekaLogogramCreatorFavoritePlateRenameInputs = [];
    private bool eurekaLogogramCreatorHowToUseExpanded = true;

    private void DrawEurekaLogogramCreatorTask()
    {
        var logogramCreator = plugin.EurekaLogogramCreator;
        logogramCreator.EnsureDataLoaded();
        var fontScaling = ImGui.GetFontSize() / 17f;
        var isInManipulator = logogramCreator.IsManipulatorVisible();

        ImGui.TextColored(new Vector4(0.82f, 1.0f, 0.55f, 1.0f), global::XASlave.Localization.UiLocale.Get("S4D24F60E78E1A812"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SD1406075A2223BE2"));

        if (global::XASlave.Localization.UiNative.Button(
                eurekaLogogramCreatorHowToUseExpanded
                    ? (global::XASlave.Localization.UiLocale.Native("S6A4DC3ABD4C219BA") + "##EurekaLogogramCreatorHowToUse")
                    : (global::XASlave.Localization.UiLocale.Native("S91F1A47CACCC47B8") + "##EurekaLogogramCreatorHowToUse")))
        {
            eurekaLogogramCreatorHowToUseExpanded = !eurekaLogogramCreatorHowToUseExpanded;
        }

        if (eurekaLogogramCreatorHowToUseExpanded)
        {
            using (ImRaii.TextWrapPos(0f))
            {
                ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S12D805CD994853DC"));
                ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S1DA812AEB76E3B8D"));
                ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SF239DE9D252CE458"));
            }
        }

        ImGui.Separator();

        if (!isInManipulator)
        {
            ImGui.TextColored(
                new Vector4(1.0f, 0.65f, 0.25f, 1.0f),
                global::XASlave.Localization.UiLocale.Get("SFD8417C81A5EA640"));
        }

        DrawEurekaLogogramCreatorToolbar(fontScaling, isInManipulator);

        using var tabBar = ImRaii.TabBar("##EurekaLogogramCreatorTabs");
        if (!tabBar)
            return;

        using (var favoritesTab = global::XASlave.Localization.UiNative.TabScope(global::XASlave.Localization.UiLocale.Native("S7A1F2A83ACA9A081")))
        {
            if (favoritesTab)
                DrawEurekaLogogramCreatorFavoritesTab(fontScaling);
        }

        using (var recipesTab = global::XASlave.Localization.UiNative.TabScope(global::XASlave.Localization.UiLocale.Native("SD664282E62E41A19")))
        {
            if (recipesTab)
                DrawEurekaLogogramCreatorRecipePreferencesTab(fontScaling);
        }

        using (var costsTab = global::XASlave.Localization.UiNative.TabScope(global::XASlave.Localization.UiLocale.Native("SB88FC5FC4765148E")))
        {
            if (costsTab)
                DrawEurekaLogogramCreatorCostsTab(fontScaling);
        }

        if (isInManipulator)
        {
            using (var actionsTab = global::XASlave.Localization.UiNative.TabScope(global::XASlave.Localization.UiLocale.Native("S3658F9EA003D76DC")))
            {
                if (actionsTab)
                    DrawEurekaLogogramCreatorLogosActionsTab(fontScaling);
            }

            using (var inventoryTab = global::XASlave.Localization.UiNative.TabScope(global::XASlave.Localization.UiLocale.Native("SDBC4C4CDA3BDC92C")))
            {
                if (inventoryTab)
                    DrawEurekaLogogramCreatorInventoryTab(fontScaling);
            }

            using (var queueTab = global::XASlave.Localization.UiNative.TabScope(global::XASlave.Localization.UiLocale.Native("S3B2FE03E36893916")))
            {
                if (queueTab)
                    DrawEurekaLogogramCreatorQueueTab(fontScaling);
            }
        }
    }

    private void DrawEurekaLogogramCreatorToolbar(float fontScaling, bool isInManipulator)
    {
        var logogramCreator = plugin.EurekaLogogramCreator;

        if (isInManipulator)
        {
            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SA462B071745135CC")))
            {
                logogramCreator.RefreshKnownDataNow();
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S97DD4FECF0548A0E"));
            }
        }

        if (logogramCreator.StaticCatalogLoadFailed)
        {
            if (isInManipulator)
            {
                ImGui.SameLine();
            }

            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S91419D8B5BD12059")))
            {
                logogramCreator.RetryStaticCatalogLoad();
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SA2B58F28581442C5"));
            }
        }

        var autoRefreshAllPagesOnOpen = logogramCreator.Configuration.AutoRefreshAllPagesOnOpen;
        if (global::XASlave.Localization.UiNative.Checkbox(global::XASlave.Localization.UiLocale.Native("S1658E5DEA1877B4D"), ref autoRefreshAllPagesOnOpen))
        {
            logogramCreator.SetAutoRefreshAllPagesOnOpen(autoRefreshAllPagesOnOpen);
        }

        ImGui.SameLine();
        var autoDestroyWhenFull = logogramCreator.Configuration.AutoDestroyWhenMagiaBoardFull;
        if (global::XASlave.Localization.UiNative.Checkbox(global::XASlave.Localization.UiLocale.Native("SCDDDC1544A1FE266"), ref autoDestroyWhenFull))
        {
            logogramCreator.SetAutoDestroyWhenMagiaBoardFull(autoDestroyWhenFull);
        }

        var autoRetryFailedExtraction = logogramCreator.Configuration.AutoRetryFailedExtraction;
        if (global::XASlave.Localization.UiNative.Checkbox(global::XASlave.Localization.UiLocale.Native("S72C89EADECBF6CAB"), ref autoRetryFailedExtraction))
        {
            logogramCreator.SetAutoRetryFailedExtraction(autoRetryFailedExtraction);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S5D6BA2356F0B7950"));
        }

        ImGui.SameLine();
        var showFavoritesOverlay = logogramCreator.Configuration.ShowFavoritesOverlay;
        if (global::XASlave.Localization.UiNative.Checkbox(global::XASlave.Localization.UiLocale.Native("S8729C3F40AB71CBD"), ref showFavoritesOverlay))
        {
            logogramCreator.SetShowFavoritesOverlay(showFavoritesOverlay);
        }

        ImGui.SetNextItemWidth(210 * fontScaling);
        var queueStepFrameDelay = logogramCreator.QueueStepFrameDelayFrames;
        if (global::XASlave.Localization.UiNative.SliderInt(global::XASlave.Localization.UiLocale.Native("S30B061793178B17F"), ref queueStepFrameDelay, 1, 120, "%d", ImGuiSliderFlags.AlwaysClamp))
        {
            logogramCreator.SetQueueStepFrameDelay(queueStepFrameDelay);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S0E5CB7E05B5F0A53"));
        }

        if (logogramCreator.HasActiveOrQueuedAutoLogoAction)
        {
            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S8623BD5CBF768502"), new Vector2(150 * fontScaling, 0)))
            {
                logogramCreator.CancelAutoLogoAction();
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S395E68AC41D23215"));
            }
        }

        var staticCatalogColor = logogramCreator.IsStaticCatalogReady
            ? new Vector4(0.2f, 0.9f, 0.4f, 1.0f)
            : logogramCreator.StaticCatalogLoadFailed
                ? new Vector4(1.0f, 0.35f, 0.25f, 1.0f)
                : new Vector4(1.0f, 0.6f, 0.2f, 1.0f);
        ImGui.TextColored(staticCatalogColor, global::XASlave.Localization.UiLocale.FormatKey("S57FAA3CEA4DF3D4A", logogramCreator.LocalizedStaticCatalogState));
        if (logogramCreator.StaticCatalogLoadFailed && !string.IsNullOrWhiteSpace(logogramCreator.StaticCatalogError))
        {
            ImGui.TextColored(staticCatalogColor, logogramCreator.LocalizedStaticCatalogError);
        }

        var logosActionsColor = logogramCreator.IsMagiaBoardFull
            ? new Vector4(1.0f, 0.55f, 0.2f, 1.0f)
            : new Vector4(0.7f, 0.7f, 0.7f, 1.0f);
        ImGui.TextColored(logosActionsColor, global::XASlave.Localization.UiLocale.FormatKey("S296EE6C8FD4AAA76", logogramCreator.LogosActionSlotsUsed, logogramCreator.LogosActionSlotCapacity));

        var cacheColor = logogramCreator.HasLogogramStockCache
            ? new Vector4(0.2f, 0.9f, 0.4f, 1.0f)
            : new Vector4(1.0f, 0.6f, 0.2f, 1.0f);
        ImGui.SameLine();
        ImGui.TextColored(cacheColor, logogramCreator.HasLogogramStockCache ? global::XASlave.Localization.UiLocale.Get("S6D60DD97B288C034") : global::XASlave.Localization.UiLocale.Get("S4F509AD0B1CDDE71"));

        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), logogramCreator.LocalizedLastStatus);
        ImGui.Separator();
    }

    private static void DrawEurekaLogogramCreatorCatalogNotReady(EurekaLogogramCreatorService logogramCreator)
    {
        var color = logogramCreator.StaticCatalogLoadFailed
            ? new Vector4(1.0f, 0.35f, 0.25f, 1.0f)
            : new Vector4(1.0f, 0.6f, 0.2f, 1.0f);
        ImGui.TextColored(color, global::XASlave.Localization.UiLocale.FormatKey("S57FAA3CEA4DF3D4A", logogramCreator.LocalizedStaticCatalogState));
        if (logogramCreator.StaticCatalogLoadFailed && !string.IsNullOrWhiteSpace(logogramCreator.StaticCatalogError))
        {
            ImGui.TextColored(color, logogramCreator.LocalizedStaticCatalogError);
        }

        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SE9CF78E301BBC4C0"));
    }

    private void DrawEurekaLogogramCreatorPlateBuilder(float fontScaling)
    {
        var logogramCreator = plugin.EurekaLogogramCreator;
        var astralLabel = logogramCreator.PendingAstralActionId.HasValue
            ? logogramCreator.GetActionNameMessage(logogramCreator.PendingAstralActionId.Value).Display
            : global::XASlave.Localization.UiLocale.Get("SC6C094BC0054F9CB");
        var umbralLabel = logogramCreator.PendingUmbralActionId.HasValue
            ? logogramCreator.GetActionNameMessage(logogramCreator.PendingUmbralActionId.Value).Display
            : global::XASlave.Localization.UiLocale.Get("SC6C094BC0054F9CB");
        var craftableCount = logogramCreator.GetPendingPlateCraftableCount();
        var canStartPlate = logogramCreator.CanQueuePendingPlateMessage(out var pendingPlateError);
        var hasPendingSelection = logogramCreator.PendingAstralActionId.HasValue || logogramCreator.PendingUmbralActionId.HasValue;
        var craftableColor = craftableCount > 0
            ? new Vector4(0.0f, 1.0f, 0.0f, 1.0f)
            : new Vector4(1.0f, 0.6f, 0.2f, 1.0f);
        var pendingPlateBareMinimumLabel = hasPendingSelection && logogramCreator.TryGetPendingPlateBareMinimumGilCost(out var pendingPlateBareMinimumCost)
            ? logogramCreator.FormatGilCostMessage(pendingPlateBareMinimumCost).Display
            : global::XASlave.Localization.UiLocale.Get("SDC73E2ECC2FB8601");
        var pendingPlateRecipeCostLabel = hasPendingSelection && logogramCreator.TryGetPendingPlateRecipeGilCost(out var pendingPlateRecipeCost)
            ? logogramCreator.FormatGilCostMessage(pendingPlateRecipeCost).Display
            : global::XASlave.Localization.UiLocale.Get("SDC73E2ECC2FB8601");

        ImGui.Text(global::XASlave.Localization.UiLocale.FormatKey("S4221E2B5824676A3", astralLabel));
        ImGui.Text(global::XASlave.Localization.UiLocale.FormatKey("SB96BE368012A30BB", umbralLabel));
        ImGui.TextColored(craftableColor, global::XASlave.Localization.UiLocale.FormatKey("S329F870BCF04ACCF", logogramCreator.GetPendingPlateDescriptionMessage().Display, craftableCount));
        ImGui.TextColored(new Vector4(0.9f, 0.8f, 0.45f, 1.0f), global::XASlave.Localization.UiLocale.FormatKey("S2270A582A32B7F0B", pendingPlateBareMinimumLabel, pendingPlateRecipeCostLabel));

        using (ImRaii.Disabled(!canStartPlate))
        {
            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SE4BB9F1ECE9AF926"), new Vector2(90 * fontScaling, 0)))
            {
                logogramCreator.QueuePendingPlate();
            }
        }

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SC52FF5EA803D5775"), new Vector2(130 * fontScaling, 0)))
        {
            logogramCreator.ClearPendingPlateSelection();
        }

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S8C37EDD405BBEF75"), new Vector2(170 * fontScaling, 0)))
        {
            var plateName = string.IsNullOrWhiteSpace(eurekaLogogramCreatorFavoritePlateName)
                ? logogramCreator.GetPendingPlateDescription()
                : eurekaLogogramCreatorFavoritePlateName;
            logogramCreator.SaveCurrentQueueAsFavoritePlate(plateName);
        }

        if (!canStartPlate && hasPendingSelection && !string.IsNullOrWhiteSpace(pendingPlateError.Original))
        {
            ImGui.TextColored(new Vector4(1.0f, 0.35f, 0.35f, 1.0f), pendingPlateError.Display);
        }

        ImGui.Separator();
    }

    private void DrawEurekaLogogramCreatorFavoritesTab(float fontScaling)
    {
        var logogramCreator = plugin.EurekaLogogramCreator;

        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S4A44316D969469A5"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S43E40BC1C006AE53"));

        using (ImRaii.ItemWidth(300 * fontScaling))
        {
            ImGui.InputTextWithHint("##EurekaLogogramCreatorFavoritePlateName", global::XASlave.Localization.UiLocale.Get("S1C72B0FF9D03B59A"), ref eurekaLogogramCreatorFavoritePlateName, 100);
        }

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S544359EA913DFB34")))
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
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SACFA8C5011FB2519"));
        }

        ImGui.Separator();
        DrawEurekaLogogramCreatorPlateBuilder(fontScaling);

        if (logogramCreator.Configuration.FavoritePlates.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), global::XASlave.Localization.UiLocale.Get("S08C996E454DA4EFC"));
            return;
        }

        using (ImRaii.Child("##EurekaLogogramCreatorFavoritePlates", new Vector2(0, 0), true))
        {

            for (var i = 0; i < logogramCreator.Configuration.FavoritePlates.Count; i++)
            {
                var plate = logogramCreator.Configuration.FavoritePlates[i];
                var (astralActionId, umbralActionId) = GetEurekaLogogramCreatorFavoriteActionIds(plate);
                var plateLabel = logogramCreator.DescribeFavoritePlateMessage(plate).Display;
                var astralSummary = astralActionId.HasValue ? logogramCreator.GetActionNameMessage(astralActionId.Value).Display : global::XASlave.Localization.UiLocale.Get("SC6C094BC0054F9CB");
                var umbralSummary = umbralActionId.HasValue ? logogramCreator.GetActionNameMessage(umbralActionId.Value).Display : global::XASlave.Localization.UiLocale.Get("SC6C094BC0054F9CB");
                var astralBareMinimumLabel = !astralActionId.HasValue
                    ? global::XASlave.Localization.UiLocale.Get("SDC73E2ECC2FB8601")
                    : logogramCreator.TryGetActionBareMinimumGilCost(astralActionId.Value, out var astralBareMinimumCost)
                        ? logogramCreator.FormatGilCostMessage(astralBareMinimumCost).Display
                        : global::XASlave.Localization.UiLocale.Get("SF3F276560015EA0D");
                var astralRecipeCostLabel = !astralActionId.HasValue
                    ? global::XASlave.Localization.UiLocale.Get("SDC73E2ECC2FB8601")
                    : logogramCreator.TryGetActionRecipeGilCost(astralActionId.Value, out var astralRecipeCost)
                        ? logogramCreator.FormatGilCostMessage(astralRecipeCost).Display
                        : global::XASlave.Localization.UiLocale.Get("SF3F276560015EA0D");
                var umbralBareMinimumLabel = !umbralActionId.HasValue
                    ? global::XASlave.Localization.UiLocale.Get("SDC73E2ECC2FB8601")
                    : logogramCreator.TryGetActionBareMinimumGilCost(umbralActionId.Value, out var umbralBareMinimumCost)
                        ? logogramCreator.FormatGilCostMessage(umbralBareMinimumCost).Display
                        : global::XASlave.Localization.UiLocale.Get("SF3F276560015EA0D");
                var umbralRecipeCostLabel = !umbralActionId.HasValue
                    ? global::XASlave.Localization.UiLocale.Get("SDC73E2ECC2FB8601")
                    : logogramCreator.TryGetActionRecipeGilCost(umbralActionId.Value, out var umbralRecipeCost)
                        ? logogramCreator.FormatGilCostMessage(umbralRecipeCost).Display
                        : global::XASlave.Localization.UiLocale.Get("SF3F276560015EA0D");
                var plateBareMinimumLabel = logogramCreator.TryGetFavoritePlateBareMinimumGilCost(plate, out var plateBareMinimumCost)
                    ? logogramCreator.FormatGilCostMessage(plateBareMinimumCost).Display
                    : global::XASlave.Localization.UiLocale.Get("SF3F276560015EA0D");
                var plateRecipeCostLabel = logogramCreator.TryGetFavoritePlateRecipeGilCost(plate, out var plateRecipeCost)
                    ? logogramCreator.FormatGilCostMessage(plateRecipeCost).Display
                    : global::XASlave.Localization.UiLocale.Get("SF3F276560015EA0D");
                if (!eurekaLogogramCreatorFavoritePlateRenameInputs.TryGetValue(plate, out var renameInput))
                {
                    renameInput = plate.Name;
                }

                using (ImRaii.PushId(i))
                {
                    ImGui.Text(plateLabel);
                    using (var imguiScope369 = ImRaii.DragDropSource())
                    if (imguiScope369)
                    {
                        ImGui.SetDragDropPayload(EurekaLogogramCreatorFavoriteDragDropType, BitConverter.GetBytes(i));

                        ImGui.TextUnformatted(global::XASlave.Localization.UiLocale.FormatKey("S9353099143D0E13C", plateLabel));

                    }

                    using (var imguiScope377 = ImRaii.DragDropTarget())
                    if (imguiScope377)
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
                                    break;
                                }
                            }
                        }


                    }

                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), global::XASlave.Localization.UiLocale.FormatKey("S92185F8AC481524C", umbralSummary, umbralBareMinimumLabel, umbralRecipeCostLabel));
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), global::XASlave.Localization.UiLocale.FormatKey("S01B9599587AF0BBC", astralSummary, astralBareMinimumLabel, astralRecipeCostLabel));
                    ImGui.TextColored(new Vector4(0.9f, 0.8f, 0.45f, 1.0f), global::XASlave.Localization.UiLocale.FormatKey("S1EBF1E7F12498FC1", plateBareMinimumLabel, plateRecipeCostLabel));
                    ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S36BDFFF5E01C8F7B"));

                    using (ImRaii.ItemWidth(220 * fontScaling))
                    {
                        if (ImGui.InputTextWithHint("##FavoriteRename", global::XASlave.Localization.UiLocale.Get("S6082B0C0B2DDA0B0"), ref renameInput, 100, ImGuiInputTextFlags.EnterReturnsTrue))
                        {
                            logogramCreator.RenameFavoritePlate(i, renameInput);
                        }
                        eurekaLogogramCreatorFavoritePlateRenameInputs[plate] = renameInput;
                    }

                    ImGui.SameLine();
                    if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S3064D79A295CEFE4")))
                    {
                        logogramCreator.RenameFavoritePlate(i, renameInput);
                    }

                    ImGui.SameLine();
                    using (ImRaii.Disabled(i == 0))
                    {
                        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S55490A4BF3A8E8CB")))
                        {
                            if (logogramCreator.MoveFavoritePlate(i, i - 1))
                            {
                                ResetEurekaLogogramCreatorFavoriteRenameInputs();
                                break;
                            }
                        }
                    }

                    ImGui.SameLine();
                    using (ImRaii.Disabled(i >= logogramCreator.Configuration.FavoritePlates.Count - 1))
                    {
                        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SB86D11AF79188E58")))
                        {
                            if (logogramCreator.MoveFavoritePlate(i, i + 1))
                            {
                                ResetEurekaLogogramCreatorFavoriteRenameInputs();
                                break;
                            }
                        }
                    }

                    if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S55CCDE2C285549DB")))
                    {
                        logogramCreator.QueueFavoritePlate(plate);
                    }

                    ImGui.SameLine();
                    if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SB659EA7E03225B38")))
                    {
                        logogramCreator.UpsertFavoritePlate(plate.Name, logogramCreator.PendingAstralActionId, logogramCreator.PendingUmbralActionId);
                    }

                    ImGui.SameLine();
                    if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SE2D0A54968EAD24E")))
                    {
                        logogramCreator.DeleteFavoritePlate(i);
                        ResetEurekaLogogramCreatorFavoriteRenameInputs();
                        break;
                    }

                    ImGui.Separator();
                }
            }

            var validPlates = logogramCreator.Configuration.FavoritePlates;
            foreach (var staleKey in eurekaLogogramCreatorFavoritePlateRenameInputs.Keys.Where(x => !validPlates.Contains(x)).ToList())
            {
                eurekaLogogramCreatorFavoritePlateRenameInputs.Remove(staleKey);
            }

        }
    }

    private void ResetEurekaLogogramCreatorFavoriteRenameInputs()
    {
        eurekaLogogramCreatorFavoritePlateRenameInputs.Clear();
    }

    private void DrawEurekaLogogramCreatorLogosActionsTab(float fontScaling)
    {
        var logogramCreator = plugin.EurekaLogogramCreator;

        if (!logogramCreator.IsStaticCatalogReady)
        {
            DrawEurekaLogogramCreatorCatalogNotReady(logogramCreator);
            return;
        }

        DrawEurekaLogogramCreatorPlateBuilder(fontScaling);

        using (ImRaii.ItemWidth(320 * fontScaling))
        {
            ImGui.InputTextWithHint("##EurekaLogogramCreatorActionFilter", global::XASlave.Localization.UiLocale.Get("SD27A9BA1268EE067"), ref eurekaLogogramCreatorActionFilter, 64, ImGuiInputTextFlags.AutoSelectAll);
        }

        ImGui.Separator();

        var actionSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        using (ImRaii.Child("##EurekaLogogramCreatorActionsList", new Vector2(0, -30 * fontScaling), true))
        {

            foreach (var action in logogramCreator.LogosActions)
            {
                var actionNameMessage = actionSheet != null && actionSheet.TryGetRow(action.Id, out var actionRow)
                    ? global::XASlave.Localization.UiMessage.Raw(actionRow.Name.ExtractText())
                    : global::XASlave.Localization.UiMessage.Format("S48D7DB9B2B65D27D" , action.Id);
                var actionName = actionNameMessage.Original;

                if (!string.IsNullOrEmpty(eurekaLogogramCreatorActionFilter) &&
                    !actionName.Contains(eurekaLogogramCreatorActionFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                DrawEurekaLogogramCreatorActionRow(action, actionNameMessage.Display, fontScaling);
            }

        }

        if (logogramCreator.IsProcessingQueue)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("SC197A18603DB850F"));
        }
        else if (logogramCreator.SynthesisQueue.Count > 0)
        {
            ImGui.Text(global::XASlave.Localization.UiLocale.FormatKey("SB597383981989597", logogramCreator.SynthesisQueue.Count));
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S094D76E4C77C0B71")))
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
            ? global::XASlave.Localization.UiLocale.FormatKey("S4C5408ED0EDB783E", recipeIndex + 1)
            : global::XASlave.Localization.UiLocale.T(EurekaLogogramCreatorService.AutomaticRecipeModeLabel);

        var texture = Plugin.TextureProvider.GetFromGameIcon(action.IconID).GetWrapOrEmpty();
        ImGui.Image(texture.Handle, new Vector2(32, 32) * fontScaling);

        ImGui.SameLine();
        using (ImRaii.Group())
        {
            ImGui.Text(actionName);
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), global::XASlave.Localization.UiLocale.FormatKey("S1DC5403737E6AC2E", currentStock));

            if (resolvedRecipe != null)
            {
                var recipeText = logogramCreator.FormatRecipeMessage(resolvedRecipe).Display;
                var bareMinimumCost = logogramCreator.TryGetRecipeBareMinimumGilCost(resolvedRecipe, out var bareMinimumGilCost)
                    ? logogramCreator.FormatGilCostMessage(bareMinimumGilCost).Display
                    : global::XASlave.Localization.UiLocale.Get("SF3F276560015EA0D");
                var recipeColor = canCraft
                    ? new Vector4(0.0f, 1.0f, 0.0f, 1.0f)
                    : new Vector4(1.0f, 0.2f, 0.2f, 1.0f);
                ImGui.TextColored(recipeColor, global::XASlave.Localization.UiLocale.FormatKey("S1F90A2843E3CD9BD", recipeMode, maxCraftable, bareMinimumCost, recipeText));
            }
            else
            {
                ImGui.TextColored(new Vector4(1.0f, 0.2f, 0.2f, 1.0f), global::XASlave.Localization.UiLocale.Get("S0513CA2BCB1E4E26"));
            }

        }

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
            if (global::XASlave.Localization.UiNative.Button($"{label}##{side}_{actionId}", new Vector2(buttonWidth, buttonHeight), display: EurekaLogogramCreatorService.GetPlateSideMessage(side).Display))
            {
                logogramCreator.SetPendingPlateSelection(side, actionId);
            }

            return;
        }

        using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, 0.5f))
        {
            global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S723C494DF58EF30B") + $"##{side}_{actionId}"), new Vector2(buttonWidth, buttonHeight));
        }
    }

    private void DrawEurekaLogogramCreatorSoloButton(uint actionId, float buttonWidth, float buttonHeight, bool canCraft)
    {
        var logogramCreator = plugin.EurekaLogogramCreator;

        using (ImRaii.Disabled(!canCraft))
        {
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SA6DB480094950DB2") + $"##{actionId}"), new Vector2(buttonWidth, buttonHeight)))
            {
                logogramCreator.QueueSynthesis(actionId);
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S63253AFB3B0DCC44"));
        }
    }

    private void DrawEurekaLogogramCreatorRecipePreferencesTab(float fontScaling)
    {
        var logogramCreator = plugin.EurekaLogogramCreator;
        var automaticRecipeModeLabel = EurekaLogogramCreatorService.AutomaticRecipeModeLabel;

        if (!logogramCreator.IsStaticCatalogReady)
        {
            DrawEurekaLogogramCreatorCatalogNotReady(logogramCreator);
            return;
        }

        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S6EFBB9905D933B79"));
        using (ImRaii.ItemWidth(320 * fontScaling))
        {
            ImGui.InputTextWithHint("##EurekaLogogramCreatorRecipeFilter", global::XASlave.Localization.UiLocale.Get("SD27A9BA1268EE067"), ref eurekaLogogramCreatorRecipeFilter, 64, ImGuiInputTextFlags.AutoSelectAll);
        }
        ImGui.Separator();

        var actionSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        using (ImRaii.Child("##EurekaLogogramCreatorRecipePrefs", new Vector2(0, 0), true))
        {

            foreach (var action in logogramCreator.LogosActions)
            {
                var actionNameMessage = actionSheet != null && actionSheet.TryGetRow(action.Id, out var actionRow)
                    ? global::XASlave.Localization.UiMessage.Raw(actionRow.Name.ExtractText())
                    : global::XASlave.Localization.UiMessage.Format("S48D7DB9B2B65D27D" , action.Id);
                var actionName = actionNameMessage.Original;
                if (!string.IsNullOrEmpty(eurekaLogogramCreatorRecipeFilter) &&
                    !actionName.Contains(eurekaLogogramCreatorRecipeFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using (ImRaii.PushId((int)action.Id))
                {
                    ImGui.Text(actionNameMessage.Display);

                    var selectedRecipeIndex = logogramCreator.GetPreferredRecipeIndex(action.Id);
                    var automaticRecipePreview = GetEurekaLogogramCreatorAutomaticRecipePreviewLabelMessage(action, automaticRecipeModeLabel);
                    var preview = selectedRecipeIndex >= 0 && selectedRecipeIndex < action.Recipes.Count
                        ? GetEurekaLogogramCreatorRecipeChoiceLabelMessage(selectedRecipeIndex, action.Recipes[selectedRecipeIndex], logogramCreator.GetCraftableCount(action.Recipes[selectedRecipeIndex]))
                        : automaticRecipePreview;

                    using (var imguiScope668 = global::XASlave.Localization.UiNative.ComboScope("##RecipeChoice", preview.Display))
                    if (imguiScope668)
                    {
                        var automaticSelected = selectedRecipeIndex < 0;
                        if (global::XASlave.Localization.UiNative.Selectable(automaticRecipePreview.Original, automaticSelected, display: automaticRecipePreview.Display))
                        {
                            logogramCreator.SetPreferredRecipeIndex(action.Id, -1);
                        }

                        for (var i = 0; i < action.Recipes.Count; i++)
                        {
                            var recipe = action.Recipes[i];
                            var craftableCount = logogramCreator.GetCraftableCount(recipe);
                            var itemLabel = GetEurekaLogogramCreatorRecipeChoiceLabelMessage(i, recipe, craftableCount);
                            if (global::XASlave.Localization.UiNative.Selectable(itemLabel.Original, selectedRecipeIndex == i, display: itemLabel.Display))
                            {
                                logogramCreator.SetPreferredRecipeIndex(action.Id, i);
                            }
                        }


                    }

                    var resolvedRecipe = logogramCreator.GetResolvedRecipe(action, out var resolvedRecipeIndex);
                    if (resolvedRecipe != null)
                    {
                        var resolvedRecipeCostLabel = logogramCreator.TryGetRecipeGilCost(resolvedRecipe, out var resolvedRecipeCost)
                            ? logogramCreator.FormatGilCostMessage(resolvedRecipeCost).Display
                            : global::XASlave.Localization.UiLocale.Get("SF3F276560015EA0D");
                        var recipeColor = logogramCreator.IsRecipeCraftable(resolvedRecipe)
                            ? new Vector4(0.0f, 1.0f, 0.0f, 1.0f)
                            : new Vector4(1.0f, 0.2f, 0.2f, 1.0f);
                        ImGui.TextColored(
                            recipeColor,
                            global::XASlave.Localization.UiLocale.FormatKey("S322763E0264E3098", (logogramCreator.GetPreferredRecipeIndex(action.Id) >= 0 ? global::XASlave.Localization.UiLocale.FormatKey("S4C5408ED0EDB783E", resolvedRecipeIndex + 1) : global::XASlave.Localization.UiMessage.Authored(automaticRecipeModeLabel).Display), resolvedRecipeCostLabel, logogramCreator.FormatRecipeMessage(resolvedRecipe).Display));
                    }

                    ImGui.Separator();
                }
            }

        }
    }

    private void DrawEurekaLogogramCreatorCostsTab(float fontScaling)
    {
        var logogramCreator = plugin.EurekaLogogramCreator;

        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S0B4E36D6C917EBE2"));

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SC8EEB0EDB956AFB1"), new Vector2(180 * fontScaling, 0)))
        {
            logogramCreator.ResetLogogramSourceGilCosts();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SDA72E1DE93971C96"));
        }

        ImGui.Separator();
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SDDD4788B8D795800"));

        using (ImRaii.Child("##EurekaLogogramCreatorCostsTab", new Vector2(0, 0), true))
        {

            using (var imguiScope732 = ImRaii.Table("##EurekaLogogramCreatorLogogramCosts", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
            if (imguiScope732)
            {
                global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S0E570CA6FABE24F9"));
                global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SF13024272C385F73"));
                global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SAFC4B9BB152423C8"));
                global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S4018BB445F30CCCF"));
                global::XASlave.Localization.UiNative.TableHeadersRow();

                foreach (var source in logogramCreator.GetLogogramSourceDefinitions())
                {
                    var poolSize = logogramCreator.GetLogogramSourcePoolSize(source.ItemId);
                    var configuredCost = logogramCreator.GetConfiguredLogogramSourceGilCost(source.ItemId);
                    var specificMnemeCost = logogramCreator.GetConfiguredSpecificMnemeGilCost(source.ItemId);

                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.Text(global::XASlave.Localization.UiMessage.Authored(source.Name).Display);

                    ImGui.TableNextColumn();
                    ImGui.Text(poolSize > 0 ? poolSize.ToString() : "?");

                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(-float.Epsilon);
                    if (ImGui.InputInt($"##SourceCost_{source.ItemId}", ref configuredCost, 100, 1000))
                    {
                        logogramCreator.SetLogogramSourceGilCost(source.ItemId, configuredCost);
                    }

                    ImGui.TableNextColumn();
                    ImGui.Text(poolSize > 0 ? logogramCreator.FormatGilCostMessage(specificMnemeCost).Display : global::XASlave.Localization.UiLocale.Get("SE2F79E5B60330BBA"));
                }


            }

        }
    }

    private void DrawEurekaLogogramCreatorInventoryTab(float fontScaling)
    {
        var logogramCreator = plugin.EurekaLogogramCreator;

        ImGui.Text(global::XASlave.Localization.UiLocale.FormatKey("S85BABA1C069F622E", logogramCreator.LogogramStock.Count));
        if (!logogramCreator.HasLogogramStockCache)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.6f, 0.2f, 1.0f), global::XASlave.Localization.UiLocale.Get("SB59DA6061DDD4E87"));
        }

        ImGui.Separator();
        using (ImRaii.Child("##EurekaLogogramCreatorLogogramList", new Vector2(0, 0), true))
        {
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

        }
    }

    private void DrawEurekaLogogramCreatorQueueTab(float fontScaling)
    {
        var logogramCreator = plugin.EurekaLogogramCreator;

        DrawEurekaLogogramCreatorPlateBuilder(fontScaling);

        ImGui.Text(global::XASlave.Localization.UiLocale.Get("S4D349B4A69C4CC67"));
        ImGui.Separator();

        if (logogramCreator.SynthesisQueue.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), global::XASlave.Localization.UiLocale.Get("SC501C695157F1E2C"));
            return;
        }

        using (ImRaii.Child("##EurekaLogogramCreatorQueueList", new Vector2(0, -40 * fontScaling), true))
        {

            var queueList = logogramCreator.SynthesisQueue.ToList();
            for (var i = 0; i < queueList.Count; i++)
            {
                var request = queueList[i];
                ImGui.Text($"{i + 1}. {logogramCreator.GetPlateRequestLabelMessage(request).Display}");
                if (request.Astral != null)
                {
                    ImGui.TextColored(
                        new Vector4(0.7f, 0.7f, 0.7f, 1.0f),
                        global::XASlave.Localization.UiLocale.FormatKey("S94A6770AB4C113F3", logogramCreator.GetPlateSelectionNameMessage(request.Astral).Display, request.Astral.RecipeIndex + 1, logogramCreator.FormatRecipeMessage(request.Astral.Recipe).Display));
                }

                if (request.Umbral != null)
                {
                    ImGui.TextColored(
                        new Vector4(0.7f, 0.7f, 0.7f, 1.0f),
                        global::XASlave.Localization.UiLocale.FormatKey("SA90409E060221D6E", logogramCreator.GetPlateSelectionNameMessage(request.Umbral).Display, request.Umbral.RecipeIndex + 1, logogramCreator.FormatRecipeMessage(request.Umbral.Recipe).Display));
                }

                ImGui.Separator();
            }

        }

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S3FDA72E584598F73"), new Vector2(110 * fontScaling, 0)))
        {
            logogramCreator.SynthesisQueue.Clear();
        }

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S094D76E4C77C0B71"), new Vector2(120 * fontScaling, 0)))
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

    private string GetEurekaLogogramCreatorAutomaticRecipePreviewLabel(LogosAction action, string automaticRecipeModeLabel) => GetEurekaLogogramCreatorAutomaticRecipePreviewLabelMessage(action, automaticRecipeModeLabel).Original;

    private global::XASlave.Localization.UiMessage GetEurekaLogogramCreatorAutomaticRecipePreviewLabelMessage(LogosAction action, string automaticRecipeModeLabel)
    {
        var logogramCreator = plugin.EurekaLogogramCreator;
        var resolvedRecipe = logogramCreator.GetResolvedRecipe(action, out _);
        if (resolvedRecipe == null || !logogramCreator.TryGetRecipeGilCost(resolvedRecipe, out var gilCost))
        {
            return global::XASlave.Localization.UiMessage.Format("S3E9C7F031E63DEF8" , global::XASlave.Localization.UiMessage.Authored(automaticRecipeModeLabel));
        }

        return global::XASlave.Localization.UiMessage.Format("S59F0EFBE8AE4EC48" , global::XASlave.Localization.UiMessage.Authored(automaticRecipeModeLabel), logogramCreator.FormatGilCostMessage(gilCost));
    }

    private string GetEurekaLogogramCreatorRecipeChoiceLabel(int recipeIndex, IReadOnlyList<Recipe> recipe, int craftableCount) => GetEurekaLogogramCreatorRecipeChoiceLabelMessage(recipeIndex, recipe, craftableCount).Original;

    private global::XASlave.Localization.UiMessage GetEurekaLogogramCreatorRecipeChoiceLabelMessage(int recipeIndex, IReadOnlyList<Recipe> recipe, int craftableCount)
    {
        var logogramCreator = plugin.EurekaLogogramCreator;
        var gilCostLabel = logogramCreator.TryGetRecipeGilCost(recipe, out var gilCost)
            ? logogramCreator.FormatGilCostMessage(gilCost)
            : global::XASlave.Localization.UiMessage.Key("SF3F276560015EA0D");

        return global::XASlave.Localization.UiMessage.Format("SE8FCAE4004CC1D17" , recipeIndex + 1, craftableCount, gilCostLabel, logogramCreator.FormatRecipeMessage(recipe));
    }
}
