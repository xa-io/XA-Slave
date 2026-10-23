using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using XASlave.Data;
using XASlave.Services;
using XASlave.Services.Tasks;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    // Match the existing owner and ONH trade approach distance.
    private const float XagmanCustomMeetingStopDistance = 1.5f;
    private bool xagmanCustomMeetingEnabled;
    private Vector3 xagmanCustomMeetingPosition;
    private uint xagmanCustomMeetingTerritory;
    private string xagmanCustomMeetingError = string.Empty;
    private global::XASlave.Localization.UiMessage? xagmanCustomMeetingErrorUiMessage = global::XASlave.Localization.UiMessage.Raw(string.Empty);
    private bool xagmanCustomMoveOwned;
    private bool xagmanCustomMoveSawActivity;
    private bool xagmanCustomMoveTaskOwned;
    private long xagmanCustomMoveRunId;
    private ulong xagmanCustomMoveContentId;
    private XagmanRole xagmanCustomMoveRole;
    private DateTime xagmanCustomMoveStartedUtc = DateTime.MinValue;
    private DateTime xagmanCustomMoveDispatchedUtc = DateTime.MinValue;

    private enum CustomMeetingMoveResult { Pending, Complete, Failed }

    private void DrawXagmanCustomMeetingCoordinates(Configuration cfg)
    {
        using var disabled = ImRaii.Disabled(xagmanRunning);
        var enabled = cfg.XagmanCustomMeetingCoordinatesEnabled;
        if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S932D22C4574DE173") + "##xagmanCustomMeet"), ref enabled))
        { cfg.XagmanCustomMeetingCoordinatesEnabled = enabled; cfg.Save(); }
        if (!enabled) return;
        var coordinates = cfg.XagmanCustomMeetingCoordinates ?? string.Empty;
        ImGui.SetNextItemWidth(Scale(280));
        if (global::XASlave.Localization.UiNative.InputText((global::XASlave.Localization.UiLocale.Native("SADE099751D2EA9F3") + "##xagmanCustomMeetXYZ"), ref coordinates, 160))
        { cfg.XagmanCustomMeetingCoordinates = coordinates; cfg.Save(); }
        var territory = (int)Math.Min(cfg.XagmanCustomMeetingTerritoryId, ushort.MaxValue);
        ImGui.SetNextItemWidth(Scale(140));
        if (global::XASlave.Localization.UiNative.InputInt((global::XASlave.Localization.UiLocale.Native("S1DEE6516DB68A011") + "##xagmanCustomMeetTerritory"), ref territory))
        { cfg.XagmanCustomMeetingTerritoryId = (uint)Math.Clamp(territory, 0, ushort.MaxValue); cfg.Save(); }
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S08627A09BF6CFFD7") + "##xagmanCustomMeetCurrent"))
            && Plugin.ObjectTable.LocalPlayer is { } local && Plugin.ClientState.TerritoryType != 0)
        {
            var p = local.Position;
            cfg.XagmanCustomMeetingCoordinates = string.Create(CultureInfo.InvariantCulture, $"{p.X:R}, {p.Y:R}, {p.Z:R}");
            cfg.XagmanCustomMeetingTerritoryId = Plugin.ClientState.TerritoryType;
            cfg.Save();
        }
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S516C59390AFA3C27"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S7B2F70BEA4AED2E4"));
        if (!XagmanMeetingCoordinates.TryParse(cfg.XagmanCustomMeetingCoordinates, out _)
            || cfg.XagmanCustomMeetingTerritoryId == 0)
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.3f, 1f), global::XASlave.Localization.UiLocale.Get("SFAA01D932A53AD8B"));
    }

    private bool TryCaptureXagmanCustomMeetingSettings()
    {
        StopXagmanCustomMeetingMovement();
        var cfg = plugin.Configuration;
        xagmanCustomMeetingEnabled = cfg.XagmanCustomMeetingCoordinatesEnabled;
        xagmanCustomMeetingTerritory = cfg.XagmanCustomMeetingTerritoryId;
        xagmanCustomMeetingError = global::XASlave.Localization.UiMessage.Store(ref xagmanCustomMeetingErrorUiMessage, global::XASlave.Localization.UiMessage.Raw(string.Empty));
        if (xagmanCustomMeetingEnabled && (!XagmanMeetingCoordinates.TryParse(cfg.XagmanCustomMeetingCoordinates, out xagmanCustomMeetingPosition)
            || xagmanCustomMeetingTerritory == 0))
            xagmanCustomMeetingError = global::XASlave.Localization.UiMessage.Store(ref xagmanCustomMeetingErrorUiMessage, global::XASlave.Localization.UiMessage.Key("S8EB20E5E5BF2A6DE"));
        if (string.IsNullOrEmpty(xagmanCustomMeetingError)) return true;
        xagmanStatus = XagmanStatus.Error;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Capture(ref xagmanCustomMeetingErrorUiMessage, xagmanCustomMeetingError));
        arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Capture(ref xagmanCustomMeetingErrorUiMessage, xagmanCustomMeetingError));
        arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
        plugin.TaskRunner.AddLog($"Xagman: {xagmanCustomMeetingError}");
        return false;
    }

    private bool HasXagmanCustomMeetingVendorOverride()
        => !plugin.Configuration.XagmanOutsideNetworkHelper
            && (xagmanActiveRole == XagmanRole.Tony ? xagmanTonySellLocationActive : GetXagmanTonySellLocationPeerForOwner() != null);

    private void StopXagmanCustomMeetingMovement()
    {
        if (xagmanCustomMoveOwned && !plugin.IpcClient.VnavStop())
            plugin.TaskRunner.AddLog("Xagman: custom meetup movement stop was not confirmed; stop vnavmesh manually if movement continues.");
        xagmanCustomMoveOwned = false;
        xagmanCustomMoveSawActivity = false;
        xagmanCustomMoveStartedUtc = DateTime.MinValue;
        xagmanCustomMoveDispatchedUtc = DateTime.MinValue;
        xagmanCustomMoveContentId = 0;
    }

    private void UpdateXagmanCustomMovementOwnership()
    {
        if (xagmanCustomMoveStartedUtc == DateTime.MinValue) return;
        if (!xagmanRunning || isDisposed)
        { StopXagmanCustomMeetingMovement(); return; }
        // Runtime reassertion may be gated by Lifestream or character safety before
        // it polls again. Keep the deadline independent of those external waits.
        if ((DateTime.UtcNow - xagmanCustomMoveStartedUtc).TotalSeconds >= 300)
        { FailXagmanCustomMeetingMovement(global::XASlave.Localization.UiMessage.Key("S19080296F543E229")); return; }
        if (xagmanCustomMoveRole != xagmanActiveRole
            || (xagmanCustomMoveContentId != 0 && (!Plugin.PlayerState.IsLoaded || Plugin.PlayerState.ContentId != xagmanCustomMoveContentId))
            || (xagmanCustomMoveTaskOwned && (!IsXagmanTaskRunnerActive() || plugin.TaskRunner.CurrentRunId != xagmanCustomMoveRunId)))
            FailXagmanCustomMeetingMovement(global::XASlave.Localization.UiMessage.Key("SBA9A7FAD0CCD4FB4"));
    }

    private CustomMeetingMoveResult PollXagmanCustomMeetingMovement(string character, bool taskOwned, out global::XASlave.Localization.UiMessage error)
    {
        error = global::XASlave.Localization.UiMessage.Raw(string.Empty);
        if (!xagmanCustomMeetingEnabled || HasXagmanCustomMeetingVendorOverride())
        { StopXagmanCustomMeetingMovement(); return CustomMeetingMoveResult.Complete; }
        if (!string.IsNullOrEmpty(xagmanCustomMeetingError))
        { error = global::XASlave.Localization.UiMessage.Capture(ref xagmanCustomMeetingErrorUiMessage, xagmanCustomMeetingError); return CustomMeetingMoveResult.Failed; }
        var expectedTerritory = AetheryteData.GetZoneIdForAetheryteWithFallback(GetXagmanActiveMeetAetheryte());
        if (!XagmanMeetingCoordinates.IsTerritoryMatch(xagmanCustomMeetingTerritory, expectedTerritory, Plugin.ClientState.TerritoryType)
            || !IsXagmanAtMeetDestination(GetXagmanActiveMeetWorld(), GetXagmanActiveMeetAetheryte()))
        { error = global::XASlave.Localization.UiMessage.Key("SA3F8F4188AB554A9"); return CustomMeetingMoveResult.Failed; }
        if (!MonthlyReloggerTask.GetCurrentCharacterNameWorld().Equals(character, StringComparison.OrdinalIgnoreCase))
        { error = global::XASlave.Localization.UiMessage.Key("S7008B3CAE1723F3C"); return CustomMeetingMoveResult.Failed; }
        if (!Plugin.PlayerState.IsLoaded || Plugin.PlayerState.ContentId == 0)
            return CustomMeetingMoveResult.Pending;
        if (xagmanCustomMoveStartedUtc == DateTime.MinValue)
        {
            xagmanCustomMoveStartedUtc = DateTime.UtcNow;
            xagmanCustomMoveTaskOwned = taskOwned;
            xagmanCustomMoveRunId = plugin.TaskRunner.CurrentRunId;
            xagmanCustomMoveContentId = Plugin.PlayerState.ContentId;
            xagmanCustomMoveRole = xagmanActiveRole;
        }
        if ((DateTime.UtcNow - xagmanCustomMoveStartedUtc).TotalSeconds >= 300)
        { error = global::XASlave.Localization.UiMessage.Key("S8B205AB93448D7FA"); return CustomMeetingMoveResult.Failed; }
        if (!plugin.IpcClient.TryGetLifestreamBusy(out var travelBusy) || travelBusy
            || !CharacterSafetyHelper.IsCharacterSafeWaitReady() || !plugin.IpcClient.VnavIsReady())
            return CustomMeetingMoveResult.Pending;
        var local = Plugin.ObjectTable.LocalPlayer;
        if (local == null) return CustomMeetingMoveResult.Pending;
        var moving = IsXagmanMovementActive();
        if (Vector3.Distance(local.Position, xagmanCustomMeetingPosition) <= XagmanCustomMeetingStopDistance)
        {
            if (moving)
            {
                if (xagmanCustomMoveOwned && !plugin.IpcClient.VnavStop())
                { error = global::XASlave.Localization.UiMessage.Key("S5C00FC06F104C1E4"); return CustomMeetingMoveResult.Failed; }
                return CustomMeetingMoveResult.Pending;
            }
            StopXagmanCustomMeetingMovement();
            return CustomMeetingMoveResult.Complete;
        }
        xagmanStatus = XagmanStatus.Traveling;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Key("S9AEA6E9C55F6CEF4"));
        if (!xagmanCustomMoveOwned)
        {
            if (moving) return CustomMeetingMoveResult.Pending;
            if (!plugin.IpcClient.VnavPathfindAndMoveCloseTo(xagmanCustomMeetingPosition, false, XagmanCustomMeetingStopDistance))
            { error = global::XASlave.Localization.UiMessage.Key("S1399FA740BA27B28"); return CustomMeetingMoveResult.Failed; }
            xagmanCustomMoveOwned = true;
            xagmanCustomMoveDispatchedUtc = DateTime.UtcNow;
            plugin.TaskRunner.AddLog("Xagman: pathing to the configured custom meetup point after Lifestream finished.");
            return CustomMeetingMoveResult.Pending;
        }
        if (moving) xagmanCustomMoveSawActivity = true;
        else if (xagmanCustomMoveSawActivity || (DateTime.UtcNow - xagmanCustomMoveDispatchedUtc).TotalSeconds >= 15)
        { error = global::XASlave.Localization.UiMessage.Key("S584F89820726EF51"); return CustomMeetingMoveResult.Failed; }
        return CustomMeetingMoveResult.Pending;
    }

    private void FailXagmanCustomMeetingMovement(global::XASlave.Localization.UiMessage reasonPresentation)
    {
        var reason = reasonPresentation.Original;
        var character = xagmanActiveCharacter;
        if (!string.IsNullOrWhiteSpace(character))
        {
            plugin.TaskRunner.RecordFailedCharacter(character);
            xagmanCharacterFailureReasons[character] = reasonPresentation;
        }
        StopXagmanTask();
        xagmanStatus = XagmanStatus.Error;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, reasonPresentation);
        plugin.TaskRunner.AddLog($"Xagman: {reason}");
        PublishXagmanPresence();
    }

    private void AddXagmanCustomMeetingStep(List<TaskStep> steps, string character, Func<bool>? skip = null)
    {
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S1168E45BC296BC0C", character),
            ShouldSkip = () =>
            {
                var shouldSkip = !xagmanCustomMeetingEnabled || (skip?.Invoke() ?? false);
                if (shouldSkip) StopXagmanCustomMeetingMovement();
                return shouldSkip;
            },
            IsComplete = () =>
            {
                var result = PollXagmanCustomMeetingMovement(character, true, out var error);
                if (result == CustomMeetingMoveResult.Failed) FailXagmanCustomMeetingMovement(global::XASlave.Localization.UiMessage.Capture(error, error.Original));
                return result != CustomMeetingMoveResult.Pending;
            },
            TimeoutSec = 300,
            OnTimeout = () => FailXagmanCustomMeetingMovement(global::XASlave.Localization.UiMessage.Key("S801B8390E8DC0B4A")),
        });
    }

    private bool ReassertXagmanCustomMeetingPoint()
    {
        var result = PollXagmanCustomMeetingMovement(xagmanActiveCharacter, false, out var error);
        if (result == CustomMeetingMoveResult.Failed) FailXagmanCustomMeetingMovement(global::XASlave.Localization.UiMessage.Capture(error, error.Original));
        return result != CustomMeetingMoveResult.Complete;
    }
}
