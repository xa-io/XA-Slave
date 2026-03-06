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
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "AR Processing");
        ImGui.TextDisabled("Pre/post-processing steps around AutoRetainer multi-mode character cycles.");
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
            var postFc = plugin.Configuration.ArPostProcessFcWindow;
            if (ImGui.Checkbox("Full FC Window Processing##post", ref postFc)) { plugin.Configuration.ArPostProcessFcWindow = postFc; postChanged = true; }
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
                : "Not registered (enable Pre or Post processing).");
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
            plugin.ArPostProcessor.LogEnabled = logOn;
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
                "POST-PROCESSING: After AR finishes all retainers/subs for a character, AR fires " +
                "OnCharacterAdditionalTask → XA Slave registers → AR fires OnCharacterReadyForPostprocess " +
                "→ XA Slave runs steps → signals AR to continue relogging.");
        }
    }

    /// <summary>Register or unregister based on whether either pre or post processing is enabled.</summary>
    private void UpdateRegistration()
    {
        var needsRegistration = plugin.Configuration.ArPreProcessEnabled || plugin.Configuration.ArPostProcessEnabled;
        if (needsRegistration && !plugin.ArPostProcessor.IsRegistered)
            plugin.ArPostProcessor.Register();
        else if (!needsRegistration && plugin.ArPostProcessor.IsRegistered)
            plugin.ArPostProcessor.Unregister();
    }
}
