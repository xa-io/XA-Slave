using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace XASlave.Windows;

/// <summary>
/// AR Processing panel - partial class split from SlaveWindow.
/// Configures and monitors both pre-processing (before AR retainers) and
/// post-processing (after AR retainers, before relog) for AR multi-mode.
/// </summary>
public partial class SlaveWindow
{
    // -----------------------------------------------
    //  Task: AR Processing (Pre + Post)
    // -----------------------------------------------
    private void DrawArPostProcessTask()
    {
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("SB5AD1B026973DB32"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S1FF56494CF4204F4"));
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- Plugin status --
        var arAvail = plugin.IpcClient.IsAutoRetainerAvailable();
        var xaDbAvail = plugin.IpcClient.IsXaDatabaseAvailable();

        if (arAvail)
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), global::XASlave.Localization.UiLocale.Get("S772C511FB84E2EA4"));
        else
            ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), global::XASlave.Localization.UiLocale.Get("S78C56C3A44E3E804"));

        if (xaDbAvail)
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), global::XASlave.Localization.UiLocale.Get("S521C0B27DA80740E"));
        else
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), global::XASlave.Localization.UiLocale.Get("S21FC4D00252B0101"));

        // Registration status
        if (plugin.ArPostProcessor.IsRegistered)
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), global::XASlave.Localization.UiLocale.Get("SEA421EB2DD5EB6B7"));

        ImGui.Spacing();
        var cadenceIndex = GetCheckEveryIndex(plugin.Configuration.ArPrePostCheckEveryHours);
        ImGui.SetNextItemWidth(Scale(180f));
        if (global::XASlave.Localization.UiNative.SliderInt((global::XASlave.Localization.UiLocale.Native("S4415B9AB79ABEE43") + "##arPrePostEvery"), ref cadenceIndex, 0, CheckEveryHourOptions.Length - 1,
                FormatCheckEveryHoursMessage(CheckEveryHourOptions[cadenceIndex]).Display, ImGuiSliderFlags.AlwaysClamp))
        {
            plugin.Configuration.ArPrePostCheckEveryHours = CheckEveryHourOptions[cadenceIndex];
            plugin.Configuration.Save();
        }
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SBBB10D40C9B1E46E"));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(1.0f, 0.6f, 0.6f, 1.0f), global::XASlave.Localization.UiLocale.Get("SD20B3CC96A605DF7"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S3A0CE9696B9EED3B"));
        ImGui.Spacing();

        var bailoutEnabled = plugin.Configuration.ArShipExplorationBailoutEnabled;
        if (global::XASlave.Localization.UiNative.Checkbox(global::XASlave.Localization.UiLocale.Native("S6564B363198A5008"), ref bailoutEnabled))
        {
            plugin.Configuration.ArShipExplorationBailoutEnabled = bailoutEnabled;
            plugin.Configuration.Save();
            UpdateRegistration();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S094B27FCEDB0740B"));

        if (bailoutEnabled)
        {
            var bailoutOptions = new[] { 30, 60, 90 };
            var bailoutIndex = Array.IndexOf(bailoutOptions, plugin.Configuration.ArShipExplorationBailoutSeconds);
            if (bailoutIndex < 0) bailoutIndex = 0;
            ImGui.SetNextItemWidth(Scale(180f));
            if (global::XASlave.Localization.UiNative.SliderInt((global::XASlave.Localization.UiLocale.Native("S1AAB7FCF966CD2B0") + "##shipBailout"), ref bailoutIndex, 0, bailoutOptions.Length - 1,
                    $"{bailoutOptions[bailoutIndex]} sec", ImGuiSliderFlags.AlwaysClamp))
            {
                plugin.Configuration.ArShipExplorationBailoutSeconds = bailoutOptions[bailoutIndex];
                plugin.Configuration.Save();
            }
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SD0A042DF6C5E713B"));
        }

        // ═══════════════════════════════════════════════════
        //  PRE-PROCESSING SECTION
        // ═══════════════════════════════════════════════════
        //  STARTUP RECOVERY SECTION
        // ═══════════════════════════════════════════════════
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), global::XASlave.Localization.UiLocale.Get("S20C7D53F291AADFD"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S5DECF22694E639A6"));
        ImGui.Spacing();

        var preEnabled = plugin.Configuration.ArPreProcessEnabled;
        if (global::XASlave.Localization.UiNative.Checkbox(global::XASlave.Localization.UiLocale.Native("S1ABCFE47D90337CA"), ref preEnabled))
        {
            plugin.Configuration.ArPreProcessEnabled = preEnabled;
            plugin.Configuration.Save();
            UpdateRegistration();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S0A4B7AAF92623016"));

        if (preEnabled)
        {
            ImGui.Spacing();
            var preChanged = false;

            var preDelay = plugin.Configuration.ArPreProcessLoginDelay;
            ImGui.SetNextItemWidth(Scale(100f));
            if (global::XASlave.Localization.UiNative.InputFloat((global::XASlave.Localization.UiLocale.Native("S2B9474067408FB11") + "##pre"), ref preDelay, 1f, 5f, "%.0f"))
            {
                if (preDelay < 3f) preDelay = 3f;
                if (preDelay > 30f) preDelay = 30f;
                plugin.Configuration.ArPreProcessLoginDelay = preDelay;
                preChanged = true;
            }
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S01C9358CFA56A326"));

            ImGui.Spacing();
            var preInv = plugin.Configuration.ArPreProcessOpenInventory;
            if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S166DBD737506990A") + "##pre"), ref preInv)) { plugin.Configuration.ArPreProcessOpenInventory = preInv; preChanged = true; }
            var preArm = plugin.Configuration.ArPreProcessOpenArmouryChest;
            if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S53649CACF2611F56") + "##pre"), ref preArm)) { plugin.Configuration.ArPreProcessOpenArmouryChest = preArm; preChanged = true; }
            var preSad = plugin.Configuration.ArPreProcessOpenSaddlebags;
            if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S04135E2D90F4F408") + "##pre"), ref preSad)) { plugin.Configuration.ArPreProcessOpenSaddlebags = preSad; preChanged = true; }
            var preJournal = plugin.Configuration.ArPreProcessOpenJournal;
            if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("SF7B51D61398CF038") + "##pre"), ref preJournal)) { plugin.Configuration.ArPreProcessOpenJournal = preJournal; preChanged = true; }
            var prePlot = plugin.Configuration.ArPreProcessCollectPersonalPlotInfo;
            if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("SFF3C7116AEE8239B") + "##pre"), ref prePlot)) { plugin.Configuration.ArPreProcessCollectPersonalPlotInfo = prePlot; preChanged = true; }
            var preFc = plugin.Configuration.ArPreProcessFcWindow;
            if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S2E7E509EFF9AA1F6") + "##pre"), ref preFc)) { plugin.Configuration.ArPreProcessFcWindow = preFc; preChanged = true; }
            var preSave = plugin.Configuration.ArPreProcessSaveToXaDatabase;
            if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S0AED21970EED8844") + "##pre"), ref preSave)) { plugin.Configuration.ArPreProcessSaveToXaDatabase = preSave; preChanged = true; }

            if (preChanged) plugin.Configuration.Save();
        }

        // ═══════════════════════════════════════════════════
        //  POST-PROCESSING SECTION
        // ═══════════════════════════════════════════════════
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.8f, 0.6f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("S802033E9B2AD21EA"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S3C73551607FD348E"));
        ImGui.Spacing();

        var postEnabled = plugin.Configuration.ArPostProcessEnabled;
        if (global::XASlave.Localization.UiNative.Checkbox(global::XASlave.Localization.UiLocale.Native("S51779D53AB6BBAE4"), ref postEnabled))
        {
            plugin.Configuration.ArPostProcessEnabled = postEnabled;
            plugin.Configuration.Save();
            UpdateRegistration();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S05A5BEE2F48AA150"));

        if (postEnabled)
        {
            ImGui.Spacing();
            var postChanged = false;

            var postInv = plugin.Configuration.ArPostProcessOpenInventory;
            if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S166DBD737506990A") + "##post"), ref postInv)) { plugin.Configuration.ArPostProcessOpenInventory = postInv; postChanged = true; }
            var postArm = plugin.Configuration.ArPostProcessOpenArmouryChest;
            if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S53649CACF2611F56") + "##post"), ref postArm)) { plugin.Configuration.ArPostProcessOpenArmouryChest = postArm; postChanged = true; }
            var postSad = plugin.Configuration.ArPostProcessOpenSaddlebags;
            if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S04135E2D90F4F408") + "##post"), ref postSad)) { plugin.Configuration.ArPostProcessOpenSaddlebags = postSad; postChanged = true; }
            var postJournal = plugin.Configuration.ArPostProcessOpenJournal;
            if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("SF7B51D61398CF038") + "##post"), ref postJournal)) { plugin.Configuration.ArPostProcessOpenJournal = postJournal; postChanged = true; }
            var postPlot = plugin.Configuration.ArPostProcessCollectPersonalPlotInfo;
            if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("SFF3C7116AEE8239B") + "##post"), ref postPlot)) { plugin.Configuration.ArPostProcessCollectPersonalPlotInfo = postPlot; postChanged = true; }
            var postFc = plugin.Configuration.ArPostProcessFcWindow;
            if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S2E7E509EFF9AA1F6") + "##post"), ref postFc)) { plugin.Configuration.ArPostProcessFcWindow = postFc; postChanged = true; }
            var postFcChest = plugin.Configuration.ArPostProcessCheckFcChestForGil;
            if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S35CB06BE1A43A96C") + "##post"), ref postFcChest)) { plugin.Configuration.ArPostProcessCheckFcChestForGil = postFcChest; postChanged = true; }
            ImGui.SameLine();
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S272AB01808C90DF4"));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SECDED8A1563EAA17"));
            var postSave = plugin.Configuration.ArPostProcessSaveToXaDatabase;
            if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S0AED21970EED8844") + "##post"), ref postSave)) { plugin.Configuration.ArPostProcessSaveToXaDatabase = postSave; postChanged = true; }

            if (postChanged) plugin.Configuration.Save();
        }

        // ═══════════════════════════════════════════════════
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.9f, 0.75f, 0.35f, 1.0f), global::XASlave.Localization.UiLocale.Get("SF17B34F01EFE4FC6"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SEC3DC6C5CA0F89F4"));
        ImGui.Spacing();

        var startupRecoveryEnabled = plugin.Configuration.ArStartupRecoveryEnabled;
        var requireCtrlShiftToDisable = startupRecoveryEnabled && !(ImGui.GetIO().KeyCtrl && ImGui.GetIO().KeyShift);
        var startupRecoveryToggled = false;

        if (requireCtrlShiftToDisable)
        {
            using (ImRaii.Disabled())
            {
                global::XASlave.Localization.UiNative.Checkbox(global::XASlave.Localization.UiLocale.Native("SC2599429A167C27F"), ref startupRecoveryEnabled);
            }
        }
        else
        {
            startupRecoveryToggled = global::XASlave.Localization.UiNative.Checkbox(global::XASlave.Localization.UiLocale.Native("SC2599429A167C27F"), ref startupRecoveryEnabled);
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(
                global::XASlave.Localization.UiLocale.Get("SAC54FE7614557D3C"));
        }

        if (startupRecoveryToggled)
        {
            plugin.Configuration.ArStartupRecoveryEnabled = startupRecoveryEnabled;
            plugin.Configuration.Save();
            plugin.ArPostProcessor.RefreshStartupRecoveryCheck();
        }

        // ═══════════════════════════════════════════════════
        //  STATUS + LOG
        // ═══════════════════════════════════════════════════
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("S920E413C7D411B61"));
        ImGui.Spacing();

        if (plugin.ArPostProcessor.IsRunning)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), global::XASlave.Localization.UiLocale.FormatKey("S8EB69248AA11B2FD", plugin.ArPostProcessor.LocalizedStatusText));
            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S19766ED6CCB2F4A3")))
                plugin.ArPostProcessor.Cancel();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S23A337FEEA126354"));
        }
        else
        {
            ImGui.TextDisabled(plugin.ArPostProcessor.IsRegistered
                ? global::XASlave.Localization.UiLocale.Get("S333C1DF12A36ED46")
                : global::XASlave.Localization.UiLocale.Get("S7942848F1C0FAF10"));
        }

        if (plugin.ArPostProcessor.CharactersPreProcessed > 0 || plugin.ArPostProcessor.CharactersProcessed > 0)
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("S970F16A654BD4C6D", plugin.ArPostProcessor.CharactersPreProcessed, plugin.ArPostProcessor.CharactersProcessed));

        // -- Log --
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("S21E49EB2B299C9F7"));
        ImGui.SameLine();

        var logOn = plugin.ArPostProcessor.LogEnabled;
        if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S92C1CDFDF4CB9CF6") + "##log"), ref logOn))
        {
            plugin.ArPostProcessor.LogEnabled = logOn;
        }
        ImGui.SameLine();

        if (global::XASlave.Localization.UiNative.SmallButton(global::XASlave.Localization.UiLocale.Native("SE21F935F11D7E966")))
        {
            var text = plugin.ArPostProcessor.GetLogText();
            if (!string.IsNullOrEmpty(text))
                ImGui.SetClipboardText(text);
        }
        ImGui.SameLine();

        if (global::XASlave.Localization.UiNative.SmallButton(global::XASlave.Localization.UiLocale.Native("S83B12C2216EFB4FD")))
            plugin.ArPostProcessor.ClearLog();

        ImGui.Spacing();

        var logMessages = plugin.ArPostProcessor.LogMessages;
        if (logMessages.Count == 0)
        {
            ImGui.TextDisabled(logOn ? global::XASlave.Localization.UiLocale.Get("S985ED9835264EBDF") : global::XASlave.Localization.UiLocale.Get("S8054ADE432A55775"));
        }
        else
        {
            using (var child = ImRaii.Child("ArProcessLog", ScaledVector(0f, 150f), true))
            {
                if (child.Success)
                {
                    foreach (var msg in logMessages)
                        ImGui.TextWrapped(msg);

                    if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 20)
                        ImGui.SetScrollHereY(1.0f);
                }
            }
        }

        // -- How it works --
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.CollapsingHeader(global::XASlave.Localization.UiLocale.Native("SC1879525C75CB5C2")))
        {
            ImGui.TextWrapped(
                global::XASlave.Localization.UiLocale.Get("S86FAC292EF6062B8"));
        }
    }

    /// <summary>Register or unregister based on whether either pre or post processing is enabled.</summary>
    private void UpdateRegistration()
    {
        var needsRegistration = plugin.Configuration.ArShipExplorationBailoutEnabled || plugin.Configuration.ArPreProcessEnabled || plugin.Configuration.ArPostProcessEnabled;
        if (needsRegistration)
            plugin.ArPostProcessor.Register();
        else
            plugin.ArPostProcessor.Unregister();
    }
}
