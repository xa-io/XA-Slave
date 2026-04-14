using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace XASlave.Windows;

/// <summary>
/// AR Processing panel — partial class split from SlaveWindow.
/// Configures and monitors both pre-processing (before AR retainers) and
/// post-processing (after AR retainers, before relog) for AR multi-mode.
/// </summary>
public partial class SlaveWindow
{
    // ───────────────────────────────────────────────
    //  Task: AR Processing (Pre + Post)
    // ───────────────────────────────────────────────
    private void DrawArPostProcessTask()
    {
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "AutoRetainer Tasks");
        ImGui.TextDisabled("Bailout, pre-processing, and post-processing steps around AutoRetainer multi-mode character cycles.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Plugin status ──
        var arAvail = plugin.IpcClient.IsAutoRetainerAvailable();
        var xaDbAvail = plugin.IpcClient.IsXaDatabaseAvailable();

        if (arAvail)
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "AutoRetainer: Available");
        else
            ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), "AutoRetainer: Not available");

        if (xaDbAvail)
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "XA Database: Available");
        else
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "XA Database: Not available (save step will be skipped)");

        // Registration status
        if (plugin.ArPostProcessor.IsRegistered)
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "Subscribed to AutoRetainer events.");

        ImGui.Spacing();
        var cadenceIndex = GetCheckEveryIndex(plugin.Configuration.ArPrePostCheckEveryHours);
        ImGui.SetNextItemWidth(180);
        if (ImGui.SliderInt("Check Every##arPrePostEvery", ref cadenceIndex, 0, CheckEveryHourOptions.Length - 1,
                FormatCheckEveryHours(CheckEveryHourOptions[cadenceIndex])))
        {
            plugin.Configuration.ArPrePostCheckEveryHours = CheckEveryHourOptions[cadenceIndex];
            plugin.Configuration.Save();
        }
        ImGui.TextDisabled("Controls how often XA Slave will run AR pre/post scans per character. Always = every checkpoint.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(1.0f, 0.6f, 0.6f, 1.0f), "Ship Exploration Result Bailout");
        ImGui.TextDisabled("Watches SelectString and ship result windows, then suppresses AutoRetainer and presses ESC if either stays open too long.");
        ImGui.Spacing();

        var bailoutEnabled = plugin.Configuration.ArShipExplorationBailoutEnabled;
        if (ImGui.Checkbox("Enable Ship Exploration Result Bailout", ref bailoutEnabled))
        {
            plugin.Configuration.ArShipExplorationBailoutEnabled = bailoutEnabled;
            plugin.Configuration.Save();
            UpdateRegistration();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Monitors both AirShipExplorationResult and SelectString while AutoRetainer multi-mode is enabled. \n If either stays open past the selected timer, XA Slave suppresses AutoRetainer, sends ESC until the watched \n menus are gone, runs CharacterSafeWait, sends /ays reset, and then resumes AutoRetainer.");

        if (bailoutEnabled)
        {
            var bailoutOptions = new[] { 30, 60, 90 };
            var bailoutIndex = Array.IndexOf(bailoutOptions, plugin.Configuration.ArShipExplorationBailoutSeconds);
            if (bailoutIndex < 0) bailoutIndex = 0;
            ImGui.SetNextItemWidth(180);
            if (ImGui.SliderInt("Bailout Timer##shipBailout", ref bailoutIndex, 0, bailoutOptions.Length - 1,
                    $"{bailoutOptions[bailoutIndex]} sec"))
            {
                plugin.Configuration.ArShipExplorationBailoutSeconds = bailoutOptions[bailoutIndex];
                plugin.Configuration.Save();
            }
            ImGui.TextDisabled("This will only work when Multi is enabled.");
        }

        // ═══════════════════════════════════════════════════
        //  PRE-PROCESSING SECTION
        // ═══════════════════════════════════════════════════
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), "Pre-Processing (Before Retainers)");
        ImGui.TextDisabled("Runs on login BEFORE AR starts retainer processing. Suppresses AR during steps.");
        ImGui.Spacing();

        var preEnabled = plugin.Configuration.ArPreProcessEnabled;
        if (ImGui.Checkbox("Enable Pre-Processing", ref preEnabled))
        {
            plugin.Configuration.ArPreProcessEnabled = preEnabled;
            plugin.Configuration.Save();
            UpdateRegistration();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("On login during AR multi-mode: suppress AR → run steps → un-suppress.\nAR waits for steps to finish before going to retainer bell.");

        if (preEnabled)
        {
            ImGui.Spacing();
            var preChanged = false;

            var preDelay = plugin.Configuration.ArPreProcessLoginDelay;
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputFloat("Login Delay (seconds)##pre", ref preDelay, 1f, 5f, "%.0f"))
            {
                if (preDelay < 3f) preDelay = 3f;
                if (preDelay > 30f) preDelay = 30f;
                plugin.Configuration.ArPreProcessLoginDelay = preDelay;
                preChanged = true;
            }
            ImGui.TextDisabled("Wait time after login before starting pre-processing steps.");

            ImGui.Spacing();
            var preInv = plugin.Configuration.ArPreProcessOpenInventory;
            if (ImGui.Checkbox("Open Inventory##pre", ref preInv)) { plugin.Configuration.ArPreProcessOpenInventory = preInv; preChanged = true; }
            var preArm = plugin.Configuration.ArPreProcessOpenArmouryChest;
            if (ImGui.Checkbox("Open Armoury Chest##pre", ref preArm)) { plugin.Configuration.ArPreProcessOpenArmouryChest = preArm; preChanged = true; }
            var preSad = plugin.Configuration.ArPreProcessOpenSaddlebags;
            if (ImGui.Checkbox("Open Saddlebags##pre", ref preSad)) { plugin.Configuration.ArPreProcessOpenSaddlebags = preSad; preChanged = true; }
            var preJournal = plugin.Configuration.ArPreProcessOpenJournal;
            if (ImGui.Checkbox("Open Journal##pre", ref preJournal)) { plugin.Configuration.ArPreProcessOpenJournal = preJournal; preChanged = true; }
            var prePlot = plugin.Configuration.ArPreProcessCollectPersonalPlotInfo;
            if (ImGui.Checkbox("Collect Personal Plot Info##pre", ref prePlot)) { plugin.Configuration.ArPreProcessCollectPersonalPlotInfo = prePlot; preChanged = true; }
            var preFc = plugin.Configuration.ArPreProcessFcWindow;
            if (ImGui.Checkbox("Full FC Window Processing##pre", ref preFc)) { plugin.Configuration.ArPreProcessFcWindow = preFc; preChanged = true; }
            var preSave = plugin.Configuration.ArPreProcessSaveToXaDatabase;
            if (ImGui.Checkbox("Save to XA Database##pre", ref preSave)) { plugin.Configuration.ArPreProcessSaveToXaDatabase = preSave; preChanged = true; }

            if (preChanged) plugin.Configuration.Save();
        }

        // ═══════════════════════════════════════════════════
        //  POST-PROCESSING SECTION
        // ═══════════════════════════════════════════════════
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.8f, 0.6f, 1.0f, 1.0f), "Post-Processing (After Retainers)");
        ImGui.TextDisabled("Runs after AR finishes all retainers/subs, before AR relogs to next character.");
        ImGui.Spacing();

        var postEnabled = plugin.Configuration.ArPostProcessEnabled;
        if (ImGui.Checkbox("Enable Post-Processing", ref postEnabled))
        {
            plugin.Configuration.ArPostProcessEnabled = postEnabled;
            plugin.Configuration.Save();
            UpdateRegistration();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Uses AR's character post-processing IPC hook.\nAR pauses before relog and waits for XA Slave to finish.");

        if (postEnabled)
        {
            ImGui.Spacing();
            var postChanged = false;

            var postInv = plugin.Configuration.ArPostProcessOpenInventory;
            if (ImGui.Checkbox("Open Inventory##post", ref postInv)) { plugin.Configuration.ArPostProcessOpenInventory = postInv; postChanged = true; }
            var postArm = plugin.Configuration.ArPostProcessOpenArmouryChest;
            if (ImGui.Checkbox("Open Armoury Chest##post", ref postArm)) { plugin.Configuration.ArPostProcessOpenArmouryChest = postArm; postChanged = true; }
            var postSad = plugin.Configuration.ArPostProcessOpenSaddlebags;
            if (ImGui.Checkbox("Open Saddlebags##post", ref postSad)) { plugin.Configuration.ArPostProcessOpenSaddlebags = postSad; postChanged = true; }
            var postJournal = plugin.Configuration.ArPostProcessOpenJournal;
            if (ImGui.Checkbox("Open Journal##post", ref postJournal)) { plugin.Configuration.ArPostProcessOpenJournal = postJournal; postChanged = true; }
            var postPlot = plugin.Configuration.ArPostProcessCollectPersonalPlotInfo;
            if (ImGui.Checkbox("Collect Personal Plot Info##post", ref postPlot)) { plugin.Configuration.ArPostProcessCollectPersonalPlotInfo = postPlot; postChanged = true; }
            var postFc = plugin.Configuration.ArPostProcessFcWindow;
            if (ImGui.Checkbox("Full FC Window Processing##post", ref postFc)) { plugin.Configuration.ArPostProcessFcWindow = postFc; postChanged = true; }
            var postFcChest = plugin.Configuration.ArPostProcessCheckFcChestForGil;
            if (ImGui.Checkbox("Check FC Chest For Gil##post", ref postFcChest)) { plugin.Configuration.ArPostProcessCheckFcChestForGil = postFcChest; postChanged = true; }
            ImGui.SameLine();
            ImGui.TextDisabled("(i)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("This will only work inside the company workshop.");
            var postSave = plugin.Configuration.ArPostProcessSaveToXaDatabase;
            if (ImGui.Checkbox("Save to XA Database##post", ref postSave)) { plugin.Configuration.ArPostProcessSaveToXaDatabase = postSave; postChanged = true; }

            if (postChanged) plugin.Configuration.Save();
        }

        // ═══════════════════════════════════════════════════
        //  STATUS + LOG
        // ═══════════════════════════════════════════════════
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Status");
        ImGui.Spacing();

        if (plugin.ArPostProcessor.IsRunning)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), $"Running: {plugin.ArPostProcessor.StatusText}");
            if (ImGui.Button("Cancel"))
                plugin.ArPostProcessor.Cancel();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Cancel current processing and release AR.");
        }
        else
        {
            ImGui.TextDisabled(plugin.ArPostProcessor.IsRegistered
                ? "Idle — waiting for AutoRetainer multi-mode events."
                : "Not registered (enable Ship Bailout, Pre-Processing, or Post-Processing).");
        }

        if (plugin.ArPostProcessor.CharactersPreProcessed > 0 || plugin.ArPostProcessor.CharactersProcessed > 0)
            ImGui.TextDisabled($"Pre-processed: {plugin.ArPostProcessor.CharactersPreProcessed}  |  Post-processed: {plugin.ArPostProcessor.CharactersProcessed}");

        // ── Log ──
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Log");
        ImGui.SameLine();

        var logOn = plugin.ArPostProcessor.LogEnabled;
        if (ImGui.Checkbox("Enabled##log", ref logOn))
        {
            plugin.ArPostProcessor.LogEnabled = logOn;
        }
        ImGui.SameLine();

        if (ImGui.SmallButton("Copy"))
        {
            var text = plugin.ArPostProcessor.GetLogText();
            if (!string.IsNullOrEmpty(text))
                ImGui.SetClipboardText(text);
        }
        ImGui.SameLine();

        if (ImGui.SmallButton("Clear"))
            plugin.ArPostProcessor.ClearLog();

        ImGui.Spacing();

        var logMessages = plugin.ArPostProcessor.LogMessages;
        if (logMessages.Count == 0)
        {
            ImGui.TextDisabled(logOn ? "No log entries yet." : "Logging disabled.");
        }
        else
        {
            using (var child = ImRaii.Child("ArProcessLog", new Vector2(0, 150), true))
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

        // ── How it works ──
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.CollapsingHeader("How It Works"))
        {
            ImGui.TextWrapped(
                "PRE-PROCESSING: On character login during AR multi-mode, XA Slave immediately " +
                "suppresses AR (SetSuppressed=true), runs configured collection steps, then " +
                "un-suppresses AR so it can proceed to retainer processing.\n\n" +
                "SHIP BAILOUT: XA Slave watches both AirShipExplorationResult and SelectString while AutoRetainer multi-mode is enabled. If either stays open past the configured timer, XA Slave suppresses AR, sends ESC until no watched menus remain, runs CharacterSafeWait, sends /ays reset, and then resumes AutoRetainer.\n\n" +
                "POST-PROCESSING: After AR finishes all retainers/subs for a character, AR fires " +
                "OnCharacterAdditionalTask → XA Slave registers → AR fires OnCharacterReadyForPostprocess " +
                "→ XA Slave runs steps → signals AR to continue relogging.");
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
