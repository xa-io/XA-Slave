using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using XASlave.Localization;

namespace XASlave.Services;

/// <summary>Handles direct hotbar display commands in all zones and rejected display commands in PvP.</summary>
public unsafe sealed partial class PvpHotbarsService : IDisposable
{
    internal const string BlockedDisplayPattern = "\\AThe command [\"“]/hotbar[ \\t]+display[ \\t]+(?<bar>10|[1-9])(?:[ \\t]+(?<state>on|off))?[\"”] is unavailable at this time\\.\\z";
    private static readonly Regex BlockedDisplay = new(BlockedDisplayPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(10));
    private static readonly string[] Names = ["_ActionBar", "_ActionBar01", "_ActionBar02", "_ActionBar03", "_ActionBar04", "_ActionBar05", "_ActionBar06", "_ActionBar07", "_ActionBar08", "_ActionBar09"];
    private readonly PvpHotbarDiagnostics diagnostics = new();
    private readonly nint[] owned = new nint[10];
    private readonly nint[] ownedConfigDataSet = new nint[10];
    private readonly int[] ownedHudLayout = new int[10];
    private readonly bool[] ownedGamepadMode = new bool[10];
    private readonly bool[] ownedInPvp = new bool[10];
    private readonly bool?[] displayExpected = new bool?[10];
    private readonly bool[] originalVisibility = new bool[10];
    private readonly bool?[] commandVisibility = new bool?[10];
    private readonly bool[] confirmationPending = new bool[10];
    private readonly long[] requestedAt = new long[10];
    private readonly bool?[] restoreVisibility = new bool?[10];
    private readonly bool?[] restoreExpected = new bool?[10];
    private readonly bool[] restoreIssued = new bool[10];
    private readonly bool[] restoreTimedOut = new bool[10];
    private const long ConfirmationTimeoutMs = 1000;
    private const long ReadinessTimeoutMs = 5000;
    private const int MaxPendingCommands = 32;
    private readonly int[] pendingNumbers = new int[MaxPendingCommands];
    private readonly bool?[] pendingStates = new bool?[MaxPendingCommands];
    private readonly bool[] pendingInPvp = new bool[MaxPendingCommands];
    private readonly long[] pendingDeadlines = new long[MaxPendingCommands];
    private int pendingCount;
    private uint pendingTerritory;
    private bool enabled;
    private bool listening;
    private bool disposed;

    public string StatusText { get; private set; } = "Disabled";
    internal UiMessage StatusTextMessage => UiMessage.Authored(StatusText);
    internal string LocalizedStatusText => StatusTextMessage.Display;

    public bool SetEnabled(bool value)
    {
        if (disposed || (value && enabled))
            return enabled;
        if (!value)
        {
            enabled = false;
            ResetDisplayMemory();
            diagnostics.Stop("the mod was disabled");
            CancelPending("the mod was disabled");
            Plugin.ChatGui.CheckMessageHandled -= OnChatMessage;
            QueueRestoreAll(showOwnedBars: true);
            RestoreAll();
            StopListenersIfIdle();
            StatusText = HasOwnedBars ? "Disabled; waiting to restore controlled hotbars" : "Disabled";
            return false;
        }

        try
        {
            if (!listening)
            {
                foreach (var name in Names)
                    Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, name, OnFinalize);
                Plugin.Framework.Update += Update;
                listening = true;
            }
            Plugin.ChatGui.CheckMessageHandled += OnChatMessage;
            enabled = true;
            StatusText = "Monitoring blocked /hotbar display commands in PvP";
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Framework.Update -= Update;
            Plugin.ChatGui.CheckMessageHandled -= OnChatMessage;
            Plugin.AddonLifecycle.UnregisterListener(OnFinalize);
            listening = false;
            StatusText = "Unable to register hotbar listeners";
            Plugin.Log.Warning(ex, "[XASlave] Hotbar Maintainer could not start.");
            return false;
        }
    }

    private void OnFinalize(AddonEvent type, AddonArgs args)
    {
        diagnostics.AddonFinalizing(args.AddonName);
        var index = Array.IndexOf(Names, args.AddonName);
        if (index >= 0)
            ReleaseAddon(index); // Config restoration does not dereference the finalized addon.
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (!enabled || disposed || !Plugin.ClientState.IsPvP || message.IsHandled || message.LogKind != XivChatType.ErrorMessage
            || !string.IsNullOrEmpty(message.Sender.TextValue))
            return;
        try
        {
            var text = message.Message.TextValue;
            if (text.Length > 256)
                return;
            var match = BlockedDisplay.Match(text);
            if (!match.Success)
                return;
            var number = int.Parse(match.Groups["bar"].Value, CultureInfo.InvariantCulture);
            var state = match.Groups["state"].Value;
            bool? visible = state.Length == 0 ? null : state.Equals("on", StringComparison.OrdinalIgnoreCase);
            diagnostics.Request(number, visible, "fallback");
            if (!TryRequestDisplay(number, visible, out var result))
            {
                Plugin.Log.Warning($"[XASlave] Hotbar Maintainer rejected blocked command: {result}");
                return;
            }
            // An accepted deferred request owns its eventual success/failure notification.
            // Never resend the game's rejected command or suppress unrelated errors.
            message.PreventOriginal();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[XASlave] Hotbar Maintainer left a blocked command unhandled.");
        }
    }

    public bool TryExecuteDisplayCommand(string arguments, out string message)
    {
        const string usage = "Usage: /xa hotbar <1-10> [on|off|toggle], or /xa hotbar on|off to enable/disable the mod.";
        message = usage;
        if (arguments.Length > 128)
        {
            diagnostics.Event(0, "rejected", "Command exceeds 128 characters.");
            return false;
        }
        var parts = arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || number is < 1 or > 10)
        {
            diagnostics.Event(0, "rejected", usage);
            return false;
        }
        bool? visible = null;
        if (parts.Length == 2)
        {
            if (parts[1].Equals("on", StringComparison.OrdinalIgnoreCase)) visible = true;
            else if (parts[1].Equals("off", StringComparison.OrdinalIgnoreCase)) visible = false;
            else if (!parts[1].Equals("toggle", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Event(number, "rejected", usage);
                return false;
            }
        }
        try
        {
            diagnostics.Request(number, visible, "direct");
            return TryRequestDisplay(number, visible, out message, pvpOnly: false);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[XASlave] Hotbar Maintainer could not apply a direct command.");
            message = "Unable to change that hotbar; see the plugin log.";
            diagnostics.Event(number, "exception", message);
            return false;
        }
    }

    private bool TryGetReadyAddon(int number, out AtkUnitBase* addon, out string message, out bool retryable, bool pvpOnly = true)
    {
        addon = null;
        retryable = false;
        message = "Enable Hotbar Maintainer first with /xa hotbar on.";
        if (!enabled || disposed)
            return false;
        message = "Hotbar number must be between 1 and 10.";
        if (number is < 1 or > 10)
            return false;
        message = "Hotbar display must run on the game thread.";
        if (!Plugin.Framework.IsInFrameworkUpdateThread)
            return false;
        message = "Hotbar display requires login.";
        if (!Plugin.ClientState.IsLoggedIn)
            return false;
        message = "Hotbar display requires a PvP area.";
        if (pvpOnly && !Plugin.ClientState.IsPvP)
            return false;
        message = "Hotbar display is unavailable during a cutscene.";
        if (Plugin.Condition[ConditionFlag.WatchingCutscene]
            || Plugin.Condition[ConditionFlag.WatchingCutscene78]
            || Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent])
            return false;
        // Loading often hides the HUD too. Defer that case, but not an intentionally hidden HUD.
        message = "Hotbar display is waiting for area loading to finish.";
        if (Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51])
        {
            retryable = true;
            return false;
        }
        message = "Hotbar display is unavailable while the HUD is hidden.";
        if (Plugin.GameGui.GameUiHidden)
            return false;
        retryable = true;
        var hotbars = RaptureHotbarModule.Instance();
        message = "Hotbar display is waiting for the hotbar module.";
        if (hotbars == null)
            return false;
        message = "Hotbar display is waiting for the active hotbar bank.";
        if (hotbars->PvPHotbarsActive != Plugin.ClientState.IsPvP)
            return false;
        var mainBar = (AtkUnitBase*)Plugin.GameGui.GetAddonByName("_ActionBar").Address;
        message = "Hotbar display is waiting for the main hotbar addon.";
        if (mainBar == null || !mainBar->IsReady || mainBar->RootNode == null)
            return false;
        var index = number - 1;
        addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(Names[index]).Address;
        message = $"Hotbar {number} is not ready.";
        if (addon == null || !addon->IsReady || addon->RootNode == null)
            return false;
        message = "Hotbar display is waiting for the HUD display settings.";
        if (!TryReadDisplayConfiguration(out _, out _, out _, out _))
            return false;
        retryable = false;
        message = string.Empty;
        return true;
    }

    private bool TryRequestDisplay(int number, bool? requestedVisibility, out string message, bool pvpOnly = true)
    {
        var ready = TryGetReadyAddon(number, out _, out message, out var retryable, pvpOnly);
        if (!ready && !retryable)
        {
            diagnostics.Event(number, "rejected", message);
            return false;
        }
        if (pendingCount > 0 && pendingTerritory != Plugin.ClientState.TerritoryType)
            CancelPending("the territory changed");
        if (ready && pendingCount == 0)
            return TryApplyDisplay(number, requestedVisibility, out message, out _, pvpOnly);
        if (pendingCount >= MaxPendingCommands)
        {
            message = "Hotbar command queue is full; wait before trying again.";
            diagnostics.Event(number, "rejected", message);
            return false;
        }
        if (pendingCount == 0)
            pendingTerritory = Plugin.ClientState.TerritoryType;
        pendingNumbers[pendingCount] = number;
        pendingStates[pendingCount] = requestedVisibility;
        pendingInPvp[pendingCount] = Plugin.ClientState.IsPvP;
        pendingDeadlines[pendingCount] = Environment.TickCount64 + ReadinessTimeoutMs;
        pendingCount++;
        diagnostics.Event(number, "queued", message.Length == 0 ? "Waiting for earlier queued commands." : message);
        message = $"Hotbar {number}: queued for up to 5 seconds while the HUD becomes ready.";
        return true;
    }

    private void RemovePending()
    {
        pendingCount--;
        for (var index = 0; index < pendingCount; index++)
        {
            pendingNumbers[index] = pendingNumbers[index + 1];
            pendingStates[index] = pendingStates[index + 1];
            pendingInPvp[index] = pendingInPvp[index + 1];
            pendingDeadlines[index] = pendingDeadlines[index + 1];
        }
    }

    private void CancelPending(string reason)
    {
        if (pendingCount == 0)
            return;
        for (var index = 0; index < pendingCount; index++)
            diagnostics.Event(pendingNumbers[index], "cancelled", reason);
        Plugin.ChatGui.PrintError($"[XASlave] Cancelled {pendingCount} queued hotbar command(s): {reason}.");
        pendingCount = 0;
    }

    private void ProcessPending()
    {
        if (pendingCount == 0)
            return;
        if (pendingTerritory != Plugin.ClientState.TerritoryType)
        {
            CancelPending("the territory changed");
            return;
        }
        while (pendingCount > 0)
        {
            var number = pendingNumbers[0];
            // A queued command belongs to the bank/context where it was requested.
            if (pendingInPvp[0] != Plugin.ClientState.IsPvP)
            {
                diagnostics.Event(number, "cancelled", "The PvP context changed before the command was applied.");
                Plugin.ChatGui.PrintError($"[XASlave] Hotbar {number}: queued command cancelled because the PvP context changed.");
                RemovePending();
                continue;
            }
            // Check the deadline before native access, even if the HUD became ready this frame.
            if (Environment.TickCount64 >= pendingDeadlines[0])
            {
                TryGetReadyAddon(number, out _, out var reason, out _, pvpOnly: pendingInPvp[0]);
                if (reason.Length == 0)
                    reason = "the readiness deadline elapsed";
                diagnostics.Event(number, "expired", reason);
                Plugin.ChatGui.PrintError($"[XASlave] Hotbar {number}: queued command expired. {reason}");
                RemovePending();
                continue;
            }
            if (TryApplyDisplay(number, pendingStates[0], out var message, out var retryable, pvpOnly: pendingInPvp[0]))
            {
                RemovePending();
                continue;
            }
            if (retryable)
                return;
            diagnostics.Event(number, "rejected", message);
            Plugin.ChatGui.PrintError($"[XASlave] Hotbar {number}: queued command rejected. {message}");
            RemovePending();
        }
    }

    private bool TryApplyDisplay(int number, bool? requestedVisibility, out string message, out bool retryable, bool pvpOnly = true)
    {
        if (!TryGetReadyAddon(number, out var addon, out message, out retryable, pvpOnly))
            return false;
        var index = number - 1;
        if (!TryReadDisplayConfiguration(out var displayMask, out var dataSet, out var layout, out var gamepadMode))
        {
            retryable = true;
            message = "Hotbar display is waiting for the HUD display settings.";
            return false;
        }
        var configured = (displayMask & (1u << index)) != 0;
        PrepareDisplayMemory(displayMask, dataSet, layout, gamepadMode);
        if (ownedConfigDataSet[index] != 0
            && (ownedInPvp[index] != Plugin.ClientState.IsPvP
                || !MatchesDisplayContext(index, dataSet, layout, gamepadMode)
                || (displayExpected[index].HasValue && displayExpected[index] != configured)
                || (commandVisibility[index].HasValue && !MatchesRequestedVisibility(addon, commandVisibility[index]!.Value))))
            ClearOwnership(index);
        // The HUD setting owns display intent; addon/root flags can remain shown while it is off.
        var wasVisible = confirmationPending[index] && owned[index] == (nint)addon
            ? commandVisibility[index] ?? configured
            : configured;
        var visible = requestedVisibility ?? !wasVisible;
        diagnostics.Begin(number, visible, addon);
        if (!ApplyVisibility(index, addon, visible))
        {
            message = $"Hotbar {number}: the game did not accept the visibility request.";
            diagnostics.Event(number, "rejected", message);
            return false;
        }
        RememberDisplayRequest(index, visible);
        commandVisibility[index] = visible;
        confirmationPending[index] = true;
        requestedAt[index] = Environment.TickCount64;
        message = $"Hotbar {number}: {(visible ? "show" : "hide")} requested; checking the HUD.";
        diagnostics.Event(number, "accepted", message);
        return true;
    }

    private static bool TryReadDisplayConfiguration(out uint mask, out nint dataSet, out int layout, out bool gamepadMode)
    {
        mask = 0;
        dataSet = 0;
        layout = -1;
        gamepadMode = false;
        if (!Plugin.Framework.IsInFrameworkUpdateThread || !Plugin.ClientState.IsLoggedIn)
            return false;
        var config = AddonConfig.Instance();
        if (config == null || !config->IsLoaded || config->ActiveDataSet == null)
            return false;
        dataSet = (nint)config->ActiveDataSet;
        layout = config->ActiveDataSet->CurrentHudLayout;
        return layout is >= 0 and < 4
            && Plugin.GameConfig.UiConfig.TryGetBool("PadMode", out gamepadMode)
            && Plugin.GameConfig.UiControl.TryGetUInt("HotbarDisp", out mask);
    }

    private bool MatchesDisplayContext(int index, nint dataSet, int layout, bool gamepadMode) =>
        ownedConfigDataSet[index] != 0 && ownedConfigDataSet[index] == dataSet
        && ownedHudLayout[index] == layout && ownedGamepadMode[index] == gamepadMode;

    private bool IsDisplayConfigurationApplied(int index, bool visible) =>
        TryReadDisplayConfiguration(out var mask, out var dataSet, out var layout, out var gamepadMode)
        && MatchesDisplayContext(index, dataSet, layout, gamepadMode)
        && ((mask & (1u << index)) != 0) == visible;

    private bool RequestDisplayConfiguration(int index, bool visible)
    {
        if (!TryReadDisplayConfiguration(out var mask, out var dataSet, out var layout, out var gamepadMode)
            || !MatchesDisplayContext(index, dataSet, layout, gamepadMode))
            return false;
        var bit = 1u << index;
        if (displayExpected[index].HasValue && ((mask & bit) != 0) != displayExpected[index]!.Value)
            return false; // A later HUD edit owns this bar; do not overwrite it.
        var nextMask = visible ? mask | bit : mask & ~bit;
        displayExpected[index] = visible;
        if (nextMask != mask)
        {
            // Named supported setter dispatches the game's config notification, unlike raw entry writes.
            Plugin.GameConfig.UiControl.Set("HotbarDisp", nextMask);
            diagnostics.Event(index + 1, "hud-setting-requested", $"HotbarDisp=0x{mask:X8}->0x{nextMask:X8}; layout={layout}; gamepad={gamepadMode}");
        }
        // Set returns void. Verify the actual current value rather than treating submission as success.
        if (IsDisplayConfigurationApplied(index, visible))
            return true;
        // A refused setter left the previous value intact; retain that expectation for recovery.
        if (TryReadDisplayConfiguration(out var currentMask, out var currentData, out var currentLayout, out var currentMode)
            && MatchesDisplayContext(index, currentData, currentLayout, currentMode)
            && (currentMask & bit) == (mask & bit))
            displayExpected[index] = (mask & bit) != 0;
        diagnostics.Event(index + 1, "hud-setting-unconfirmed", "The HUD display setting did not read back as requested.");
        return false;
    }

    private void ReleaseAddon(int index)
    {
        owned[index] = 0;
        if (ownedConfigDataSet[index] != 0)
        {
            if (!restoreVisibility[index].HasValue)
                QueueRestore(index, originalVisibility[index], null);
            else
                restoreExpected[index] = null; // Keep an already queued disable/recovery target.
        }
        else
            ClearOwnership(index);
    }

    private static bool IsSuspended() => Plugin.GameGui.GameUiHidden
        || Plugin.Condition[ConditionFlag.BetweenAreas]
        || Plugin.Condition[ConditionFlag.BetweenAreas51]
        || Plugin.Condition[ConditionFlag.WatchingCutscene]
        || Plugin.Condition[ConditionFlag.WatchingCutscene78]
        || Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent];

    private static bool IsVisibilityApplied(AtkUnitBase* addon, bool visible)
    {
        var expected = visible ? AtkUnitBaseVisibilityState.Show : AtkUnitBaseVisibilityState.Hide;
        var mask = AtkUnitBaseVisibilityState.Show | AtkUnitBaseVisibilityState.Hide;
        // HotbarDisp controls bit 0; the native Draw dispatcher honors every low-12 suppression flag.
        return (addon->VisibilityState & mask) == expected
            && (addon->AppliedVisibilityState & mask) == expected
            && (addon->AppliedVisibilityState & AtkUnitBaseVisibilityState.TransitionComplete) != 0
            && (visible ? (addon->VisibilityFlags & 0xFFF) == 0 : (addon->VisibilityFlags & 1) != 0)
            && addon->RootNode != null && addon->RootNode->IsVisible() == visible;
    }

    private static bool RequestVisibility(AtkUnitBase* addon, bool visible)
    {
        // Match AtkUnitManager.SetAddonVisibility. IsVisible's bit setter leaves invalid transition states.
        // Always call the native method, including apparent no-ops, to repair those old states.
        if (visible)
            addon->Show(false, 0xF);
        else
            addon->Hide(false, false, 1);
        return MatchesRequestedVisibility(addon, visible);
    }

    private static bool MatchesRequestedVisibility(AtkUnitBase* addon, bool visible)
    {
        var expected = visible ? AtkUnitBaseVisibilityState.Show : AtkUnitBaseVisibilityState.Hide;
        return (addon->VisibilityState & (AtkUnitBaseVisibilityState.Show | AtkUnitBaseVisibilityState.Hide)) == expected;
    }

    private bool ApplyVisibility(int index, AtkUnitBase* addon, bool visible)
    {
        if (!TryReadDisplayConfiguration(out var mask, out var dataSet, out var layout, out var gamepadMode))
            return false;
        if (ownedConfigDataSet[index] != 0 && !MatchesDisplayContext(index, dataSet, layout, gamepadMode))
            ClearOwnership(index);
        if (ownedConfigDataSet[index] == 0)
        {
            ownedConfigDataSet[index] = dataSet;
            ownedHudLayout[index] = layout;
            ownedGamepadMode[index] = gamepadMode;
            ownedInPvp[index] = Plugin.ClientState.IsPvP;
            originalVisibility[index] = (mask & (1u << index)) != 0;
        }
        owned[index] = (nint)addon;
        displayExpected[index] = (mask & (1u << index)) != 0;
        restoreVisibility[index] = null;
        restoreExpected[index] = null;
        restoreIssued[index] = false;
        confirmationPending[index] = false;
        commandVisibility[index] = null;
        var nativeAttempted = false;
        try
        {
            if (RequestDisplayConfiguration(index, visible))
            {
                nativeAttempted = true;
                if (RequestVisibility(addon, visible))
                    return true;
            }
            diagnostics.Event(index + 1, "rejected", "HUD display-setting readback or native visibility request did not match; restoring the owned state.");
        }
        catch (Exception ex)
        {
            diagnostics.Event(index + 1, "exception", $"HUD/native request failed: {ex.GetType().Name}; restoring the owned state.");
            Plugin.Log.Warning(ex, "[XASlave] Hotbar Maintainer could not update HUD display settings.");
        }
        // A setter may throw before changing the value. Keep recovery bound to what we still own.
        if (TryReadDisplayConfiguration(out var currentMask, out var currentData, out var currentLayout, out var currentMode)
            && MatchesDisplayContext(index, currentData, currentLayout, currentMode)
            && (currentMask & (1u << index)) == (mask & (1u << index)))
            displayExpected[index] = (mask & (1u << index)) != 0;
        if (!nativeAttempted || !MatchesRequestedVisibility(addon, visible))
            owned[index] = 0;
        QueueRestore(index, originalVisibility[index], visible);
        Restore(index);
        return false;
    }

    private void Update(IFramework framework)
    {
        if (disposed || !Plugin.Framework.IsInFrameworkUpdateThread)
            return;
        try
        {
            // Diagnostics outlive command ownership briefly and must observe suspension/overrides too.
            diagnostics.Observe();
            if (!Plugin.ClientState.IsLoggedIn)
            {
                diagnostics.Stop("the character logged out");
                CancelPending("the character logged out");
                ResetDisplayMemory();
                for (var index = 0; index < Names.Length; index++)
                    ClearOwnership(index);
                StopListenersIfIdle();
                StatusText = enabled ? "Waiting for login" : "Disabled";
                return;
            }

            var hotbars = RaptureHotbarModule.Instance();
            bool? activePvpBank = hotbars == null ? null : hotbars->PvPHotbarsActive;
            // Freeze the previous bank's choices before queued commands or the new bank can change them.
            ObserveDisplayBank(activePvpBank);
            ProcessPending();
            var active = Plugin.ClientState.IsPvP && hotbars != null && hotbars->PvPHotbarsActive;
            var suspended = IsSuspended();
            if (suspended)
            {
                StatusText = enabled ? "Paused while the HUD is hidden or unavailable" : "Disabled; waiting to restore controlled hotbars";
                return;
            }

            for (var index = 0; index < Names.Length; index++)
            {
                if (ownedConfigDataSet[index] == 0 || restoreVisibility[index].HasValue)
                    continue;
                if (!TryReadDisplayConfiguration(out var displayMask, out var dataSet, out var layout, out var gamepadMode))
                    continue;
                if (!MatchesDisplayContext(index, dataSet, layout, gamepadMode))
                {
                    diagnostics.Event(index + 1, "cancelled", "The HUD layout, character settings or control mode changed; releasing the previous context.");
                    ClearOwnership(index);
                    continue;
                }
                if (displayExpected[index].HasValue && ((displayMask & (1u << index)) != 0) != displayExpected[index]!.Value)
                {
                    diagnostics.Event(index + 1, "overridden", "A later HUD display-setting change owns this bar; releasing ownership.");
                    ClearOwnership(index);
                    continue;
                }
                var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(Names[index]).Address;
                if (addon == null)
                    continue;
                if ((nint)addon != owned[index])
                {
                    diagnostics.Event(index + 1, "cancelled", "The addon instance changed; restoring only the owned HUD setting.");
                    ReleaseAddon(index);
                    continue;
                }
                if (!addon->IsReady)
                    continue;
                // A later HUD/game request owns its new state. Never replay our older snapshot over it.
                var expected = commandVisibility[index];
                if (expected.HasValue && !MatchesRequestedVisibility(addon, expected.Value))
                {
                    diagnostics.Event(index + 1, "overridden", "A later HUD/game visibility request differs; releasing ownership.");
                    ClearOwnership(index);
                    continue;
                }
                // Only unfinished requests still own a recovery snapshot; confirmed choices persist.
                if (ownedInPvp[index] != Plugin.ClientState.IsPvP || (ownedInPvp[index] && !active))
                    QueueRestore(index, originalVisibility[index], expected);
            }
            RestoreAll();
            for (var index = 0; index < Names.Length; index++)
            {
                var visible = commandVisibility[index];
                if (!visible.HasValue || !confirmationPending[index])
                    continue;
                var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(Names[index]).Address;
                if (addon != null && (nint)addon != owned[index])
                {
                    diagnostics.Event(index + 1, "cancelled", "The addon instance changed before confirmation; restoring only the owned HUD setting.");
                    ReleaseAddon(index);
                    continue;
                }
                if (addon == null || !addon->IsReady)
                    continue;
                if (IsVisibilityApplied(addon, visible.Value) && IsDisplayConfigurationApplied(index, visible.Value))
                {
                    confirmationPending[index] = false;
                    diagnostics.Event(index + 1, "confirmed", "HUD display setting, native draw gate and requested/applied/root state matched. Follow-up trace records draw activity; pixels remain unverified.");
                    Plugin.ChatGui.Print($"[XASlave] Hotbar {index + 1}: {(visible.Value ? "shown" : "hidden")}.");
                    // A confirmed choice belongs to the user in both banks; keep only the remembered intent.
                    ClearOwnership(index);
                }
                else if (Environment.TickCount64 - requestedAt[index] >= ConfirmationTimeoutMs)
                {
                    confirmationPending[index] = false;
                    diagnostics.Event(index + 1, "unconfirmed", "HUD display setting, native draw gate or addon state did not confirm within one second.");
                    Plugin.ChatGui.PrintError($"[XASlave] Hotbar {index + 1}: the HUD did not confirm the visibility change. Try an explicit on/off command.");
                }
            }
            MonitorDisplayMemory(activePvpBank);
            StopListenersIfIdle();
            StatusText = !enabled
                ? (HasOwnedBars ? "Disabled; waiting to restore controlled hotbars" : "Disabled")
                : active ? "Monitoring blocked /hotbar display commands in PvP"
                : Plugin.ClientState.IsPvP ? "Waiting for PvP hotbars"
                : hotbars != null && !hotbars->PvPHotbarsActive ? "Hotbar commands ready" : "Waiting for hotbars";
        }
        catch (Exception ex)
        {
            SetEnabled(false);
            StatusText = "Stopped after a hotbar access error; toggle to retry";
            Plugin.Log.Warning(ex, "[XASlave] Hotbar Maintainer stopped after an update failure.");
        }
    }

    private bool HasOwnedBars => Array.Exists(ownedConfigDataSet, address => address != 0);

    private void ClearOwnership(int index)
    {
        owned[index] = 0;
        ownedConfigDataSet[index] = 0;
        ownedHudLayout[index] = 0;
        ownedGamepadMode[index] = false;
        ownedInPvp[index] = false;
        displayExpected[index] = null;
        commandVisibility[index] = null;
        confirmationPending[index] = false;
        restoreVisibility[index] = null;
        restoreExpected[index] = null;
        restoreIssued[index] = false;
        restoreTimedOut[index] = false;
    }

    private void StopListenersIfIdle()
    {
        if (enabled || HasOwnedBars || !listening)
            return;
        Plugin.Framework.Update -= Update;
        Plugin.AddonLifecycle.UnregisterListener(OnFinalize);
        listening = false;
    }

    private void QueueRestore(int index, bool visible, bool? expected = null)
    {
        commandVisibility[index] = null;
        confirmationPending[index] = false;
        restoreVisibility[index] = visible;
        restoreExpected[index] = expected;
        restoreIssued[index] = false;
        restoreTimedOut[index] = false;
    }

    private void QueueRestoreAll(bool showOwnedBars)
    {
        for (var index = 0; index < Names.Length; index++)
            if (ownedConfigDataSet[index] != 0)
                QueueRestore(index, showOwnedBars || originalVisibility[index]);
    }

    private void Restore(int index)
    {
        var address = owned[index];
        var visible = restoreVisibility[index];
        if (ownedConfigDataSet[index] == 0 || !visible.HasValue)
            return;
        if (!TryReadDisplayConfiguration(out var displayMask, out var dataSet, out var layout, out var gamepadMode))
            return;
        if (!MatchesDisplayContext(index, dataSet, layout, gamepadMode)
            || (displayExpected[index].HasValue && ((displayMask & (1u << index)) != 0) != displayExpected[index]!.Value))
        {
            diagnostics.Event(index + 1, "cancelled", "Restoration yielded to a later HUD setting or changed layout/character/control mode.");
            ClearOwnership(index);
            return;
        }
        AtkUnitBase* addon = null;
        if (address != 0)
        {
            addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(Names[index]).Address;
            if (addon == null)
                return;
            if ((nint)addon != address)
            {
                // Restore the config value without issuing Show/Hide to the replacement addon.
                owned[index] = 0;
                addon = null;
            }
            else if (!addon->IsReady || addon->RootNode == null)
                return;
        }
        if (!restoreIssued[index])
        {
            var expected = restoreExpected[index];
            if (addon != null && expected.HasValue && !MatchesRequestedVisibility(addon, expected.Value))
            {
                ClearOwnership(index);
                return;
            }
            // A same-instance override must be released even after the PvP bank switches.
            // Issue once; let the game process its transition without fighting HUD Layout each frame.
            restoreIssued[index] = true;
            requestedAt[index] = Environment.TickCount64;
            diagnostics.Event(index + 1, "restore-requested", visible.Value ? "Show controlled bar." : "Hide controlled bar.");
            if (RequestDisplayConfiguration(index, visible.Value) && addon != null)
                RequestVisibility(addon, visible.Value);
        }
        if (IsDisplayConfigurationApplied(index, visible.Value) && (addon == null || IsVisibilityApplied(addon, visible.Value)))
        {
            diagnostics.Event(index + 1, "restore-confirmed", addon == null ? "Owned HUD setting restored; no direct addon visibility call was made to a replacement addon." : "HUD display setting, native draw gate and addon restoration matched.");
            ClearOwnership(index);
        }
        else if (!restoreTimedOut[index] && Environment.TickCount64 - requestedAt[index] >= ConfirmationTimeoutMs)
        {
            restoreTimedOut[index] = true;
            diagnostics.Event(index + 1, "restore-unconfirmed", "The HUD did not confirm restoration within one second.");
            Plugin.Log.Warning($"[XASlave] Hotbar {index + 1} restoration was not confirmed; /xa hotbar off retries recovery.");
            Plugin.ChatGui.PrintError($"[XASlave] Hotbar {index + 1}: restoration was not confirmed. Use /xa hotbar off to retry when the HUD is available.");
        }
    }

    private void RestoreAll()
    {
        if (!Plugin.Framework.IsInFrameworkUpdateThread || !Plugin.ClientState.IsLoggedIn || IsSuspended())
            return;
        for (var index = 0; index < Names.Length; index++)
        {
            try { Restore(index); }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "[XASlave] Hotbar Maintainer could not restore a native bar.");
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        diagnostics.Stop("the service was disposed");
        SetEnabled(false);
        // Unload cannot retain a framework listener for deferred recovery.
        for (var index = 0; index < Names.Length; index++)
        {
            if (ownedConfigDataSet[index] != 0 && (!restoreIssued[index] || restoreTimedOut[index]))
                Plugin.Log.Warning($"[XASlave] Hotbar {index + 1} could not be restored before unload; recover with /hotbar display outside PvP.");
            ClearOwnership(index);
        }
        StopListenersIfIdle();
        disposed = true;
    }
}
