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

/// <summary>Handles rejected PvP display commands without reissuing chat commands.</summary>
public unsafe sealed class PvpHotbarsService : IDisposable
{
    internal const string BlockedDisplayPattern = "\\AThe command [\"“]/hotbar[ \\t]+display[ \\t]+(?<bar>10|[1-9])(?:[ \\t]+(?<state>on|off))?[\"”] is unavailable at this time\\.\\z";
    private static readonly Regex BlockedDisplay = new(BlockedDisplayPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(10));
    private static readonly string[] Names = ["_ActionBar", "_ActionBar01", "_ActionBar02", "_ActionBar03", "_ActionBar04", "_ActionBar05", "_ActionBar06", "_ActionBar07", "_ActionBar08", "_ActionBar09"];
    private readonly nint[] owned = new nint[10];
    private readonly bool[] originalVisibility = new bool[10];
    private readonly bool?[] commandVisibility = new bool?[10];
    private readonly bool[] confirmationPending = new bool[10];
    private readonly long[] requestedAt = new long[10];
    private readonly bool?[] restoreVisibility = new bool?[10];
    private readonly bool?[] restoreExpected = new bool?[10];
    private readonly bool[] restoreIssued = new bool[10];
    private readonly bool[] restoreTimedOut = new bool[10];
    private const long ConfirmationTimeoutMs = 1000;
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
            Plugin.Log.Warning(ex, "[XASlave] PVP Hotbars could not start.");
            return false;
        }
    }

    private void OnFinalize(AddonEvent type, AddonArgs args)
    {
        var index = Array.IndexOf(Names, args.AddonName);
        if (index >= 0)
            ClearOwnership(index); // Never retain a pointer across an addon lifetime.
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (!enabled || disposed || message.IsHandled || message.LogKind != XivChatType.ErrorMessage
            || !string.IsNullOrEmpty(message.Sender.TextValue) || !Plugin.Framework.IsInFrameworkUpdateThread)
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
            if (!TryApplyDisplay(number, visible, requireVisibleMainBar: true, out _))
                return;
            // Do not swallow unrelated/rejected operations, or resend a command and recurse.
            message.PreventOriginal();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[XASlave] PVP Hotbar Disabler left a blocked command unhandled.");
        }
    }

    public bool TryExecuteDisplayCommand(string arguments, out string message)
    {
        const string usage = "Usage: /xa hotbar <1-10> [on|off|toggle], or /xa hotbar on|off to enable/disable the mod.";
        message = usage;
        if (arguments.Length > 128)
            return false;
        var parts = arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || number is < 1 or > 10)
            return false;
        bool? visible = null;
        if (parts.Length == 2)
        {
            if (parts[1].Equals("on", StringComparison.OrdinalIgnoreCase)) visible = true;
            else if (parts[1].Equals("off", StringComparison.OrdinalIgnoreCase)) visible = false;
            else if (!parts[1].Equals("toggle", StringComparison.OrdinalIgnoreCase)) return false;
        }
        try
        {
            // Explicit XA commands can restore bar 1 even after it was hidden.
            return TryApplyDisplay(number, visible, requireVisibleMainBar: false, out message);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[XASlave] PVP Hotbar Disabler could not apply a direct command.");
            message = "Unable to change that hotbar; see the plugin log.";
            return false;
        }
    }

    private bool TryApplyDisplay(int number, bool? requestedVisibility, bool requireVisibleMainBar, out string message)
    {
        message = "Enable PVP Hotbar Disabler first with /xa hotbar on.";
        if (!enabled || disposed)
            return false;
        message = "Hotbar display requires the game thread, login and an active PvP HUD.";
        if (number is < 1 or > 10 || !Plugin.Framework.IsInFrameworkUpdateThread
            || !Plugin.ClientState.IsLoggedIn || !Plugin.ClientState.IsPvP || IsSuspended())
            return false;
        var hotbars = RaptureHotbarModule.Instance();
        var mainBar = (AtkUnitBase*)Plugin.GameGui.GetAddonByName("_ActionBar").Address;
        if (hotbars == null || !hotbars->PvPHotbarsActive || mainBar == null || !mainBar->IsReady
            || (requireVisibleMainBar && !IsShown(mainBar)))
            return false;
        var index = number - 1;
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(Names[index]).Address;
        message = $"Hotbar {number} is not ready.";
        if (addon == null || !addon->IsReady || addon->RootNode == null)
            return false;
        // Consecutive commands in the same frame toggle the pending request; otherwise use applied state.
        var wasVisible = confirmationPending[index] && owned[index] == (nint)addon
            ? commandVisibility[index] ?? IsShown(addon)
            : IsShown(addon);
        var visible = requestedVisibility ?? !wasVisible;
        if (!ApplyVisibility(index, addon, visible))
        {
            message = $"Hotbar {number}: the game did not accept the visibility request.";
            return false;
        }
        commandVisibility[index] = visible;
        confirmationPending[index] = true;
        requestedAt[index] = Environment.TickCount64;
        message = $"Hotbar {number}: {(visible ? "show" : "hide")} requested; checking the HUD.";
        return true;
    }

    private static bool IsSuspended() => Plugin.GameGui.GameUiHidden
        || Plugin.Condition[ConditionFlag.BetweenAreas]
        || Plugin.Condition[ConditionFlag.BetweenAreas51]
        || Plugin.Condition[ConditionFlag.WatchingCutscene]
        || Plugin.Condition[ConditionFlag.WatchingCutscene78]
        || Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent];

    private static bool IsShown(AtkUnitBase* addon) =>
        (addon->AppliedVisibilityState & AtkUnitBaseVisibilityState.Show) != 0
        && (addon->AppliedVisibilityState & AtkUnitBaseVisibilityState.Hide) == 0
        && addon->RootNode != null && addon->RootNode->IsVisible();

    private static bool IsVisibilityApplied(AtkUnitBase* addon, bool visible)
    {
        var expected = visible ? AtkUnitBaseVisibilityState.Show : AtkUnitBaseVisibilityState.Hide;
        var mask = AtkUnitBaseVisibilityState.Show | AtkUnitBaseVisibilityState.Hide;
        return (addon->VisibilityState & mask) == expected
            && (addon->AppliedVisibilityState & mask) == expected
            && (addon->AppliedVisibilityState & AtkUnitBaseVisibilityState.TransitionComplete) != 0
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
        if (owned[index] != 0 && owned[index] != (nint)addon)
            ClearOwnership(index);
        if (owned[index] == 0)
        {
            owned[index] = (nint)addon;
            originalVisibility[index] = IsShown(addon);
        }
        restoreVisibility[index] = null;
        restoreExpected[index] = null;
        restoreIssued[index] = false;
        confirmationPending[index] = false;
        commandVisibility[index] = null;
        return RequestVisibility(addon, visible);
    }

    private void Update(IFramework framework)
    {
        if (disposed || !Plugin.Framework.IsInFrameworkUpdateThread)
            return;
        try
        {
            if (!Plugin.ClientState.IsLoggedIn)
            {
                for (var index = 0; index < Names.Length; index++)
                    ClearOwnership(index);
                StopListenersIfIdle();
                StatusText = enabled ? "Waiting for login" : "Disabled";
                return;
            }

            var hotbars = RaptureHotbarModule.Instance();
            var active = Plugin.ClientState.IsPvP && hotbars != null && hotbars->PvPHotbarsActive;
            var suspended = IsSuspended();
            if (suspended)
            {
                StatusText = enabled ? "Paused while the HUD is hidden or unavailable" : "Disabled; waiting to restore controlled hotbars";
                return;
            }

            for (var index = 0; index < Names.Length; index++)
            {
                if (owned[index] == 0 || restoreVisibility[index].HasValue)
                    continue;
                var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(Names[index]).Address;
                if (addon == null)
                    continue;
                if ((nint)addon != owned[index])
                {
                    ClearOwnership(index);
                    continue;
                }
                if (!addon->IsReady)
                    continue;
                // A later HUD/game request owns its new state. Never replay our older snapshot over it.
                var expected = commandVisibility[index];
                if (expected.HasValue && !MatchesRequestedVisibility(addon, expected.Value))
                {
                    ClearOwnership(index);
                    continue;
                }
                if (!active)
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
                    ClearOwnership(index);
                    continue;
                }
                if (addon == null || !addon->IsReady)
                    continue;
                if (IsVisibilityApplied(addon, visible.Value))
                {
                    confirmationPending[index] = false;
                    Plugin.ChatGui.Print($"[XASlave] Hotbar {index + 1}: {(visible.Value ? "shown" : "hidden")}.");
                }
                else if (Environment.TickCount64 - requestedAt[index] >= ConfirmationTimeoutMs)
                {
                    confirmationPending[index] = false;
                    Plugin.ChatGui.PrintError($"[XASlave] Hotbar {index + 1}: the HUD did not confirm the visibility change. Try an explicit on/off command.");
                }
            }
            StopListenersIfIdle();
            StatusText = !enabled
                ? (HasOwnedBars ? "Disabled; waiting to restore controlled hotbars" : "Disabled")
                : active ? "Monitoring blocked /hotbar display commands in PvP" : "Waiting for PvP hotbars";
        }
        catch (Exception ex)
        {
            SetEnabled(false);
            StatusText = "Stopped after a hotbar access error; toggle to retry";
            Plugin.Log.Warning(ex, "[XASlave] PVP Hotbars stopped after an update failure.");
        }
    }

    private bool HasOwnedBars => Array.Exists(owned, address => address != 0);

    private void ClearOwnership(int index)
    {
        owned[index] = 0;
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
            if (owned[index] != 0)
                QueueRestore(index, showOwnedBars || originalVisibility[index]);
    }

    private void Restore(int index)
    {
        var address = owned[index];
        var visible = restoreVisibility[index];
        if (address == 0 || !visible.HasValue)
            return;
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(Names[index]).Address;
        if (addon == null)
            return;
        if ((nint)addon != address)
        {
            ClearOwnership(index);
            return;
        }
        if (!addon->IsReady || addon->RootNode == null)
            return;
        if (!restoreIssued[index])
        {
            var expected = restoreExpected[index];
            if (expected.HasValue && !MatchesRequestedVisibility(addon, expected.Value))
            {
                ClearOwnership(index);
                return;
            }
            // A same-instance override must be released even after the PvP bank switches.
            // Issue once; let the game process its transition without fighting HUD Layout each frame.
            restoreIssued[index] = true;
            requestedAt[index] = Environment.TickCount64;
            RequestVisibility(addon, visible.Value);
        }
        if (IsVisibilityApplied(addon, visible.Value))
            ClearOwnership(index);
        else if (!restoreTimedOut[index] && Environment.TickCount64 - requestedAt[index] >= ConfirmationTimeoutMs)
        {
            restoreTimedOut[index] = true;
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
                Plugin.Log.Warning(ex, "[XASlave] PVP Hotbars could not restore a native bar.");
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        SetEnabled(false);
        // Unload cannot retain a framework listener for deferred recovery.
        for (var index = 0; index < Names.Length; index++)
        {
            if (owned[index] != 0 && (!restoreIssued[index] || restoreTimedOut[index]))
                Plugin.Log.Warning($"[XASlave] Hotbar {index + 1} could not be restored before unload; recover with /hotbar display outside PvP.");
            ClearOwnership(index);
        }
        StopListenersIfIdle();
        disposed = true;
    }
}
