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
    private bool enabled;
    private bool disposed;
    private int lastAvailable = -1;

    public string StatusText { get; private set; } = "Disabled";

    public bool SetEnabled(bool value)
    {
        if (disposed || value == enabled)
            return enabled;
        if (!value)
        {
            enabled = false;
            Plugin.Framework.Update -= Update;
            Plugin.ChatGui.CheckMessageHandled -= OnChatMessage;
            Array.Clear(commandVisibility);
            try { RestoreAll(showOwnedBars: true); }
            finally { Plugin.AddonLifecycle.UnregisterListener(OnFinalize); }
            StatusText = "Disabled";
            return false;
        }

        try
        {
            foreach (var name in Names)
                Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, name, OnFinalize);
            Plugin.Framework.Update += Update;
            Plugin.ChatGui.CheckMessageHandled += OnChatMessage;
            enabled = true;
            lastAvailable = -1;
            StatusText = "Monitoring blocked /hotbar display commands in PvP";
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Framework.Update -= Update;
            Plugin.ChatGui.CheckMessageHandled -= OnChatMessage;
            Plugin.AddonLifecycle.UnregisterListener(OnFinalize);
            StatusText = "Unable to register hotbar listeners";
            Plugin.Log.Warning(ex, "[XASlave] PVP Hotbars could not start.");
            return false;
        }
    }

    private void OnFinalize(AddonEvent type, AddonArgs args)
    {
        var index = Array.IndexOf(Names, args.AddonName);
        if (index >= 0)
        {
            owned[index] = 0; // Never retain a pointer across an addon lifetime.
            commandVisibility[index] = null;
        }
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
            || (requireVisibleMainBar && !mainBar->IsVisible))
            return false;
        var index = number - 1;
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(Names[index]).Address;
        message = $"Hotbar {number} is not ready.";
        if (addon == null || !addon->IsReady)
            return false;
        var visible = requestedVisibility ?? !addon->IsVisible;
        ApplyVisibility(index, addon, visible);
        commandVisibility[index] = visible;
        message = $"Hotbar {number}: {(visible ? "shown" : "hidden")}.";
        return true;
    }

    private static bool IsSuspended() => Plugin.GameGui.GameUiHidden
        || Plugin.Condition[ConditionFlag.BetweenAreas]
        || Plugin.Condition[ConditionFlag.BetweenAreas51]
        || Plugin.Condition[ConditionFlag.WatchingCutscene]
        || Plugin.Condition[ConditionFlag.WatchingCutscene78]
        || Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent];

    private void ApplyVisibility(int index, AtkUnitBase* addon, bool visible)
    {
        if (owned[index] != 0 && owned[index] != (nint)addon)
            owned[index] = 0;
        if (owned[index] == 0)
        {
            owned[index] = (nint)addon;
            originalVisibility[index] = addon->IsVisible;
        }
        addon->IsVisible = visible;
    }

    private void Update(IFramework framework)
    {
        if (!enabled || disposed)
            return;
        try
        {
            if (!Plugin.ClientState.IsLoggedIn)
            {
                Array.Clear(owned);
                Array.Clear(commandVisibility);
                lastAvailable = -1;
                StatusText = "Waiting for login";
                return;
            }

            var hotbars = RaptureHotbarModule.Instance();
            var active = Plugin.ClientState.IsPvP && hotbars != null && hotbars->PvPHotbarsActive;
            var suspended = IsSuspended();
            if (!active || suspended)
            {
                if (!active)
                    Array.Clear(commandVisibility);
                RestoreAll();
                lastAvailable = -1;
                StatusText = active ? "Paused while the HUD is hidden or unavailable" : "Waiting for PvP hotbars";
                return;
            }

            var available = 0;
            for (var index = 0; index < Names.Length; index++)
            {
                var visible = commandVisibility[index];
                if (!visible.HasValue)
                {
                    Restore(index);
                    continue;
                }
                var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(Names[index]).Address;
                if (addon == null || !addon->IsReady)
                    continue;
                ApplyVisibility(index, addon, visible.Value);
                available++;
            }
            if (available != lastAvailable)
            {
                lastAvailable = available;
                StatusText = available == 0
                    ? "Monitoring blocked /hotbar display commands in PvP"
                    : $"Monitoring blocked commands; {available} native hotbar override(s) active";
            }
        }
        catch (Exception ex)
        {
            SetEnabled(false);
            StatusText = "Stopped after a hotbar access error; toggle to retry";
            Plugin.Log.Warning(ex, "[XASlave] PVP Hotbars stopped after an update failure.");
        }
    }

    private void Restore(int index, bool showOwnedBars = false)
    {
        var address = owned[index];
        owned[index] = 0;
        if (address == 0 || !Plugin.ClientState.IsLoggedIn)
            return;
        // A PvE bank switch owns its new visibility. Never replay a PvP snapshot over it.
        var hotbars = RaptureHotbarModule.Instance();
        if (!Plugin.ClientState.IsPvP || hotbars == null || !hotbars->PvPHotbarsActive)
            return;
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(Names[index]).Address;
        if (addon != null && (nint)addon == address && addon->IsReady)
            addon->IsVisible = showOwnedBars || originalVisibility[index];
    }

    private void RestoreAll(bool showOwnedBars = false)
    {
        for (var index = 0; index < Names.Length; index++)
        {
            try { Restore(index, showOwnedBars); }
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
        disposed = true;
    }
}
