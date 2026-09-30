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
        if (ImGui.Checkbox("Custom meeting coordinates##xagmanCustomMeet", ref enabled))
        { cfg.XagmanCustomMeetingCoordinatesEnabled = enabled; cfg.Save(); }
        if (!enabled) return;
        var coordinates = cfg.XagmanCustomMeetingCoordinates ?? string.Empty;
        ImGui.SetNextItemWidth(Scale(280));
        if (ImGui.InputText("XYZ##xagmanCustomMeetXYZ", ref coordinates, 160))
        { cfg.XagmanCustomMeetingCoordinates = coordinates; cfg.Save(); }
        var territory = (int)Math.Min(cfg.XagmanCustomMeetingTerritoryId, ushort.MaxValue);
        ImGui.SetNextItemWidth(Scale(140));
        if (ImGui.InputInt("Territory ID##xagmanCustomMeetTerritory", ref territory))
        { cfg.XagmanCustomMeetingTerritoryId = (uint)Math.Clamp(territory, 0, ushort.MaxValue); cfg.Save(); }
        if (ImGui.Button("Use current position and territory##xagmanCustomMeetCurrent")
            && Plugin.ObjectTable.LocalPlayer is { } local && Plugin.ClientState.TerritoryType != 0)
        {
            var p = local.Position;
            cfg.XagmanCustomMeetingCoordinates = string.Create(CultureInfo.InvariantCulture, $"{p.X:R}, {p.Y:R}, {p.Z:R}");
            cfg.XagmanCustomMeetingTerritoryId = Plugin.ClientState.TerritoryType;
            cfg.Save();
        }
        ImGui.TextDisabled("XYZ accepts commas or spaces. Configure the same meeting point on participating clients.");
        ImGui.TextDisabled("Moves after Lifestream finishes; NPC-selling locations take priority. Settings are fixed for a run.");
        if (!XagmanMeetingCoordinates.TryParse(cfg.XagmanCustomMeetingCoordinates, out _)
            || cfg.XagmanCustomMeetingTerritoryId == 0)
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.3f, 1f), "Enter three finite coordinates and the meetup territory ID.");
    }

    private bool TryCaptureXagmanCustomMeetingSettings()
    {
        StopXagmanCustomMeetingMovement();
        var cfg = plugin.Configuration;
        xagmanCustomMeetingEnabled = cfg.XagmanCustomMeetingCoordinatesEnabled;
        xagmanCustomMeetingTerritory = cfg.XagmanCustomMeetingTerritoryId;
        xagmanCustomMeetingError = string.Empty;
        if (xagmanCustomMeetingEnabled && (!XagmanMeetingCoordinates.TryParse(cfg.XagmanCustomMeetingCoordinates, out xagmanCustomMeetingPosition)
            || xagmanCustomMeetingTerritory == 0))
            xagmanCustomMeetingError = "Custom meeting coordinates need three finite XYZ values and a nonzero territory ID.";
        if (string.IsNullOrEmpty(xagmanCustomMeetingError)) return true;
        xagmanStatus = XagmanStatus.Error;
        xagmanStatusText = xagmanCustomMeetingError;
        arImportStatus = xagmanCustomMeetingError;
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
        { FailXagmanCustomMeetingMovement("Custom meetup movement did not finish within 300 seconds."); return; }
        if (xagmanCustomMoveRole != xagmanActiveRole
            || (xagmanCustomMoveContentId != 0 && (!Plugin.PlayerState.IsLoaded || Plugin.PlayerState.ContentId != xagmanCustomMoveContentId))
            || (xagmanCustomMoveTaskOwned && (!IsXagmanTaskRunnerActive() || plugin.TaskRunner.CurrentRunId != xagmanCustomMoveRunId)))
            FailXagmanCustomMeetingMovement("Custom meetup movement lost its character or task ownership.");
    }

    private CustomMeetingMoveResult PollXagmanCustomMeetingMovement(string character, bool taskOwned, out string error)
    {
        error = string.Empty;
        if (!xagmanCustomMeetingEnabled || HasXagmanCustomMeetingVendorOverride())
        { StopXagmanCustomMeetingMovement(); return CustomMeetingMoveResult.Complete; }
        if (!string.IsNullOrEmpty(xagmanCustomMeetingError))
        { error = xagmanCustomMeetingError; return CustomMeetingMoveResult.Failed; }
        var expectedTerritory = AetheryteData.GetZoneIdForAetheryteWithFallback(GetXagmanActiveMeetAetheryte());
        if (!XagmanMeetingCoordinates.IsTerritoryMatch(xagmanCustomMeetingTerritory, expectedTerritory, Plugin.ClientState.TerritoryType)
            || !IsXagmanAtMeetDestination(GetXagmanActiveMeetWorld(), GetXagmanActiveMeetAetheryte()))
        { error = "Custom meeting territory does not match the active meetup; no coordinate path was sent."; return CustomMeetingMoveResult.Failed; }
        if (!MonthlyReloggerTask.GetCurrentCharacterNameWorld().Equals(character, StringComparison.OrdinalIgnoreCase))
        { error = "Character changed before custom meetup movement."; return CustomMeetingMoveResult.Failed; }
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
        { error = "Custom meetup movement did not reach its point within 300 seconds."; return CustomMeetingMoveResult.Failed; }
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
                { error = "vnavmesh did not accept stopping at the custom meetup point."; return CustomMeetingMoveResult.Failed; }
                return CustomMeetingMoveResult.Pending;
            }
            StopXagmanCustomMeetingMovement();
            return CustomMeetingMoveResult.Complete;
        }
        xagmanStatus = XagmanStatus.Traveling;
        xagmanStatusText = "Moving to the custom meetup point.";
        if (!xagmanCustomMoveOwned)
        {
            if (moving) return CustomMeetingMoveResult.Pending;
            if (!plugin.IpcClient.VnavPathfindAndMoveCloseTo(xagmanCustomMeetingPosition, false, XagmanCustomMeetingStopDistance))
            { error = "vnavmesh rejected the custom meetup path."; return CustomMeetingMoveResult.Failed; }
            xagmanCustomMoveOwned = true;
            xagmanCustomMoveDispatchedUtc = DateTime.UtcNow;
            plugin.TaskRunner.AddLog("Xagman: pathing to the configured custom meetup point after Lifestream finished.");
            return CustomMeetingMoveResult.Pending;
        }
        if (moving) xagmanCustomMoveSawActivity = true;
        else if (xagmanCustomMoveSawActivity || (DateTime.UtcNow - xagmanCustomMoveDispatchedUtc).TotalSeconds >= 15)
        { error = "Custom meetup path stopped or failed to start before reaching the point."; return CustomMeetingMoveResult.Failed; }
        return CustomMeetingMoveResult.Pending;
    }

    private void FailXagmanCustomMeetingMovement(string reason)
    {
        var character = xagmanActiveCharacter;
        if (!string.IsNullOrWhiteSpace(character))
        {
            plugin.TaskRunner.RecordFailedCharacter(character);
            xagmanCharacterFailureReasons[character] = reason;
        }
        StopXagmanTask();
        xagmanStatus = XagmanStatus.Error;
        xagmanStatusText = reason;
        plugin.TaskRunner.AddLog($"Xagman: {reason}");
        PublishXagmanPresence();
    }

    private void AddXagmanCustomMeetingStep(List<TaskStep> steps, string character, Func<bool>? skip = null)
    {
        steps.Add(new TaskStep
        {
            Name = $"Xagman Custom Meetup: {character}",
            ShouldSkip = () =>
            {
                var shouldSkip = !xagmanCustomMeetingEnabled || (skip?.Invoke() ?? false);
                if (shouldSkip) StopXagmanCustomMeetingMovement();
                return shouldSkip;
            },
            IsComplete = () =>
            {
                var result = PollXagmanCustomMeetingMovement(character, true, out var error);
                if (result == CustomMeetingMoveResult.Failed) FailXagmanCustomMeetingMovement(error);
                return result != CustomMeetingMoveResult.Pending;
            },
            TimeoutSec = 300,
            OnTimeout = () => FailXagmanCustomMeetingMovement("Custom meetup movement timed out."),
        });
    }

    private bool ReassertXagmanCustomMeetingPoint()
    {
        var result = PollXagmanCustomMeetingMovement(xagmanActiveCharacter, false, out var error);
        if (result == CustomMeetingMoveResult.Failed) FailXagmanCustomMeetingMovement(error);
        return result != CustomMeetingMoveResult.Complete;
    }
}
