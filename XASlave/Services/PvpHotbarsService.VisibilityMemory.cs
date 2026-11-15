using System;

namespace XASlave.Services;

public unsafe sealed partial class PvpHotbarsService
{
    private const uint RememberedHotbarMask = 0x3FF;
    private const int DisplayBankReadyFrames = 2;
    private bool displayMemoryInitialized;
    private nint displayMemoryDataSet;
    private int displayMemoryLayout;
    private bool displayMemoryGamepadMode;
    // The game exposes one current display mask; keep independent choices for each action bank.
    private readonly uint[] displayMemoryMasks = new uint[2];
    private bool displayBankObserved;
    private bool displayObservedInPvp;
    private bool? displayObservedBank;
    private bool displayMemoryTransitionPending;
    private uint displayMemoryPendingMask;
    private long? displayMemorySyncStartedAt;
    private int displayMemoryReadyFrames;
    private uint? displayMemoryDestinationMask;

    private void ResetDisplayMemory()
    {
        displayMemoryInitialized = false;
        displayMemoryDataSet = 0;
        displayMemoryLayout = 0;
        displayMemoryGamepadMode = false;
        displayMemoryMasks[0] = 0;
        displayMemoryMasks[1] = 0;
        displayBankObserved = false;
        displayObservedInPvp = false;
        displayObservedBank = null;
        displayMemoryTransitionPending = false;
        displayMemoryPendingMask = 0;
        displayMemorySyncStartedAt = null;
        displayMemoryReadyFrames = 0;
        displayMemoryDestinationMask = null;
    }

    private void ObserveDisplayBank(bool? activePvp)
    {
        var inPvp = Plugin.ClientState.IsPvP;
        var changedAreaKind = displayBankObserved && displayObservedInPvp != inPvp;
        var changedBank = displayBankObserved && displayObservedBank != activePvp;
        var aligned = activePvp.HasValue && activePvp.Value == inPvp;
        if (displayMemoryInitialized
            && (changedAreaKind || (!displayMemoryTransitionPending && (changedBank || !aligned))))
        {
            // Freeze both modes' choices until the destination bank is ready to restore its own mask.
            displayMemoryTransitionPending = true;
            displayMemoryPendingMask = RememberedHotbarMask;
            displayMemorySyncStartedAt = null;
            displayMemoryReadyFrames = 0;
            displayMemoryDestinationMask = null;
        }
        if (!aligned || IsSuspended())
        {
            displayMemoryReadyFrames = 0;
            displayMemoryDestinationMask = null;
        }
        displayBankObserved = true;
        displayObservedInPvp = inPvp;
        displayObservedBank = activePvp;
    }

    private bool MatchesDisplayMemoryContext(nint dataSet, int layout, bool gamepadMode) =>
        displayMemoryInitialized && displayMemoryDataSet == dataSet
        && displayMemoryLayout == layout && displayMemoryGamepadMode == gamepadMode;

    private void PrepareDisplayMemory(uint mask, nint dataSet, int layout, bool gamepadMode)
    {
        if (MatchesDisplayMemoryContext(dataSet, layout, gamepadMode))
            return;
        // A different character/settings context starts from its own live preferences.
        displayMemoryInitialized = true;
        displayMemoryDataSet = dataSet;
        displayMemoryLayout = layout;
        displayMemoryGamepadMode = gamepadMode;
        // Seed both modes from this context's initial HUD. Later edits affect only the active mode,
        // including before the other mode has been visited for the first time.
        displayMemoryMasks[0] = mask & RememberedHotbarMask;
        displayMemoryMasks[1] = mask & RememberedHotbarMask;
        displayMemoryTransitionPending = false;
        displayMemoryPendingMask = 0;
        displayMemorySyncStartedAt = null;
        displayMemoryReadyFrames = 0;
        displayMemoryDestinationMask = null;
        // Both callers have already required an aligned, ready hotbar bank.
        displayBankObserved = true;
        displayObservedInPvp = Plugin.ClientState.IsPvP;
        displayObservedBank = displayObservedInPvp;
    }

    private void RememberDisplayRequest(int index, bool visible)
    {
        if (!displayMemoryInitialized)
            return;
        var mode = Plugin.ClientState.IsPvP ? 1 : 0;
        var bit = 1u << index;
        displayMemoryMasks[mode] = visible ? displayMemoryMasks[mode] | bit : displayMemoryMasks[mode] & ~bit;
        // A newer explicit command takes precedence over a pending bank synchronization.
        displayMemoryPendingMask &= ~bit;
    }

    private void FinishDisplayMemoryTransition()
    {
        displayMemoryTransitionPending = false;
        displayMemoryPendingMask = 0;
        displayMemorySyncStartedAt = null;
        displayMemoryReadyFrames = 0;
        displayMemoryDestinationMask = null;
    }

    private void MonitorDisplayMemory(bool? activePvp)
    {
        if (!enabled || disposed || !Plugin.Framework.IsInFrameworkUpdateThread || !Plugin.ClientState.IsLoggedIn)
            return;
        if (displayMemoryInitialized && !IsSuspended()
            && TryReadDisplayConfiguration(out _, out var currentDataSet, out var currentLayout, out var currentMode)
            && !MatchesDisplayMemoryContext(currentDataSet, currentLayout, currentMode))
        {
            // Discard an old-context deadline as well as its choices; capture the new baseline later.
            ResetDisplayMemory();
            return;
        }
        if (displayMemoryTransitionPending && displayMemorySyncStartedAt.HasValue
            && Environment.TickCount64 - displayMemorySyncStartedAt.Value >= ReadinessTimeoutMs)
        {
            for (var index = 0; index < Names.Length; index++)
                if ((displayMemoryPendingMask & (1u << index)) != 0)
                {
                    diagnostics.Event(index + 1, "bank-sync-expired", "The destination hotbar did not become ready within five seconds of bank synchronization.");
                    Plugin.ChatGui.PrintError($"[XASlave] Hotbar {index + 1}: visibility synchronization expired. Use /xa hotbar {index + 1} on (or off) when the HUD is ready.");
                }
            FinishDisplayMemoryTransition();
            return;
        }
        if (!activePvp.HasValue || activePvp.Value != Plugin.ClientState.IsPvP || IsSuspended())
        {
            displayMemoryReadyFrames = 0;
            displayMemoryDestinationMask = null;
            return;
        }
        if (!TryGetReadyAddon(1, out _, out _, out _, pvpOnly: false)
            || !TryReadDisplayConfiguration(out var mask, out var dataSet, out var layout, out var gamepadMode))
        {
            displayMemoryReadyFrames = 0;
            displayMemoryDestinationMask = null;
            return;
        }
        PrepareDisplayMemory(mask, dataSet, layout, gamepadMode);
        var mode = Plugin.ClientState.IsPvP ? 1 : 0;
        if (!displayMemoryTransitionPending)
        {
            var controlledMask = 0u;
            for (var index = 0; index < Names.Length; index++)
                if (ownedConfigDataSet[index] != 0)
                    controlledMask |= 1u << index;
            // In a ready, steady bank, native commands and manual HUD edits update only that mode.
            // Do not mistake an unfinished XA request or its rollback for a new external preference.
            displayMemoryMasks[mode] = (displayMemoryMasks[mode] & controlledMask) | (mask & RememberedHotbarMask & ~controlledMask);
            return;
        }
        if (displayMemoryDestinationMask.HasValue)
        {
            var changedMask = (displayMemoryDestinationMask.Value ^ mask) & RememberedHotbarMask;
            for (var index = 0; index < Names.Length; index++)
            {
                var bit = 1u << index;
                if ((changedMask & bit) == 0 || ownedConfigDataSet[index] != 0)
                    continue;
                // A later ready-HUD edit wins while another bar is still waiting to synchronize.
                displayMemoryMasks[mode] = (mask & bit) != 0 ? displayMemoryMasks[mode] | bit : displayMemoryMasks[mode] & ~bit;
                displayMemoryPendingMask &= ~bit;
            }
        }
        displayMemoryDestinationMask = mask & RememberedHotbarMask;
        displayMemorySyncStartedAt ??= Environment.TickCount64;
        displayMemoryReadyFrames++;
        if (displayMemoryReadyFrames < DisplayBankReadyFrames)
            return;

        var queuedMask = 0u;
        for (var index = 0; index < pendingCount; index++)
            queuedMask |= 1u << (pendingNumbers[index] - 1);
        for (var index = 0; index < Names.Length; index++)
        {
            var bit = 1u << index;
            // Explicit FIFO commands keep priority even while another target holds up their queue.
            if ((displayMemoryPendingMask & bit) == 0 || ownedConfigDataSet[index] != 0 || (queuedMask & bit) != 0)
                continue;
            if (!TryGetReadyAddon(index + 1, out var addon, out var reason, out var retryable, pvpOnly: false))
            {
                if (retryable)
                    continue;
                displayMemoryPendingMask &= ~bit;
                diagnostics.Event(index + 1, "bank-sync-rejected", reason);
                Plugin.ChatGui.PrintError($"[XASlave] Hotbar {index + 1}: visibility synchronization rejected. {reason}");
                continue;
            }
            if (!TryReadDisplayConfiguration(out mask, out dataSet, out layout, out gamepadMode))
                continue;
            if (!MatchesDisplayMemoryContext(dataSet, layout, gamepadMode))
            {
                PrepareDisplayMemory(mask, dataSet, layout, gamepadMode);
                return;
            }
            var visible = (displayMemoryMasks[mode] & bit) != 0;
            if (((mask & bit) != 0) == visible && IsVisibilityApplied(addon, visible))
            {
                displayMemoryPendingMask &= ~bit;
                continue;
            }
            diagnostics.Request(index + 1, visible, "bank-sync");
            if (TryApplyDisplay(index + 1, visible, out var message, out retryable, pvpOnly: false))
                displayMemoryPendingMask &= ~bit;
            else if (!retryable)
            {
                displayMemoryPendingMask &= ~bit;
                diagnostics.Event(index + 1, "bank-sync-rejected", message);
                Plugin.ChatGui.PrintError($"[XASlave] Hotbar {index + 1}: visibility synchronization rejected. {message}");
            }
        }
        // Do not classify our own setting notifications as later manual changes on the next frame.
        if (TryReadDisplayConfiguration(out mask, out dataSet, out layout, out gamepadMode)
            && MatchesDisplayMemoryContext(dataSet, layout, gamepadMode))
            displayMemoryDestinationMask = mask & RememberedHotbarMask;
        if (displayMemoryPendingMask == 0)
            FinishDisplayMemoryTransition();
    }
}
