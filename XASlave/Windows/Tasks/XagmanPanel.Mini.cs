using System;
using System.Collections.Generic;
using System.Linq;
using XASlave.Data;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private static global::XASlave.Localization.UiMessage GetXagmanStatusMessage(XagmanStatus value)
        => value switch
        {
            XagmanStatus.Idle => global::XASlave.Localization.UiMessage.Key("SAB0171CA0494D441"),
            XagmanStatus.Preflight => global::XASlave.Localization.UiMessage.Key("S54614E8038990FCB"),
            XagmanStatus.Relogging => global::XASlave.Localization.UiMessage.Key("S53060BE221215E6D"),
            XagmanStatus.Traveling => global::XASlave.Localization.UiMessage.Key("SC5542C06F6FE2E8B"),
            XagmanStatus.AtMeetSpot => global::XASlave.Localization.UiMessage.Key("S0AACD65C5647F3C7"),
            XagmanStatus.ReadyForQueue => global::XASlave.Localization.UiMessage.Key("SF6BB8A8A75D71F0C"),
            XagmanStatus.WaitingRoom => global::XASlave.Localization.UiMessage.Key("S92343F9552FB9CCB"),
            XagmanStatus.Queued => global::XASlave.Localization.UiMessage.Key("S661FF40A07E037BB"),
            XagmanStatus.Called => global::XASlave.Localization.UiMessage.Key("S3BB8FAF30715B01C"),
            XagmanStatus.Trading => global::XASlave.Localization.UiMessage.Key("SFDE20A1B46218890"),
            XagmanStatus.Standby => global::XASlave.Localization.UiMessage.Key("SFCD589C1BC20CD2D"),
            XagmanStatus.ReturningHome => global::XASlave.Localization.UiMessage.Key("SF92BBC0FDA7E1F62"),
            XagmanStatus.Completed => global::XASlave.Localization.UiMessage.Key("S22A970D2E5B1CC23"),
            XagmanStatus.Paused => global::XASlave.Localization.UiMessage.Key("SE159B06187D369A0"),
            XagmanStatus.Error => global::XASlave.Localization.UiMessage.Key("S54A0E8C17EBB21A1"),
            _ => global::XASlave.Localization.UiMessage.Raw(value.ToString()),
        };

    private static global::XASlave.Localization.UiMessage GetXagmanPhaseMessage(XagmanRunPhase value)
        => value switch
        {
            XagmanRunPhase.Legacy => global::XASlave.Localization.UiMessage.Key("S1432897A6136DE54"),
            XagmanRunPhase.Collection => global::XASlave.Localization.UiMessage.Key("S7B790708FF1B1713"),
            XagmanRunPhase.Restock => global::XASlave.Localization.UiMessage.Key("S5A79B9596F4EDE06"),
            _ => global::XASlave.Localization.UiMessage.Raw(value.ToString()),
        };

    private XagmanMiniSnapshot xagmanMiniSnapshot = XagmanMiniSnapshot.Empty;
    private long xagmanMiniNextUpdate;

    internal XagmanMiniSnapshot GetXagmanMiniSnapshot()
    {
        // Apply the current privacy setting even to a retained result captured before it changed.
        var snapshot = xagmanMiniSnapshot;
        return snapshot with
        {
            Character = string.IsNullOrWhiteSpace(snapshot.Character) ? string.Empty
                : GetDisplayCharacterKey(snapshot.Character, IsCharacterListAnonymizationEnabled()),
        };
    }

    private void ClearXagmanMiniSnapshot()
    {
        xagmanMiniSnapshot = XagmanMiniSnapshot.Empty;
        xagmanPublishedEta = null;
        xagmanMiniNextUpdate = 0;
    }

    private void UpdateXagmanMiniSnapshot(bool force = false)
    {
        if (isDisposed || (!force && Environment.TickCount64 < xagmanMiniNextUpdate))
            return;
        xagmanMiniNextUpdate = Environment.TickCount64 + 250;
        if (!xagmanRunning && !force)
        {
            if (xagmanMiniSnapshot.HasRun)
            {
                var state = (xagmanStatus == XagmanStatus.Error ? global::XASlave.Localization.UiMessage.Key("S54A0E8C17EBB21A1") : (xagmanStatus == XagmanStatus.Completed ? global::XASlave.Localization.UiMessage.Key("S22A970D2E5B1CC23") : (xagmanMiniSnapshot.Running ? global::XASlave.Localization.UiMessage.Key("S1A4F630AC1B69FD0") : xagmanMiniSnapshot.State)));
                xagmanMiniSnapshot = xagmanMiniSnapshot with { Running = false, State = state, Character = string.Empty, Estimate = global::XASlave.Localization.UiMessage.Key("S65AD5D9043739BDA") };
            }
            return;
        }

        var onh = plugin.Configuration.XagmanOutsideNetworkHelper;
        var owner = xagmanActiveRole == XagmanRole.FranchiseOwner;
        IReadOnlyList<string> plan = onh ? xagmanOnhRunList
            : owner && IsXagmanCollectionFirstRunActive() && xagmanCollectionFirstOwnerFullPlan.Count > 0 ? xagmanCollectionFirstOwnerFullPlan
            : owner ? xagmanOwnerRunPlan : xagmanTonyRunPlan;
        var keys = new HashSet<string>(plan, StringComparer.OrdinalIgnoreCase);
        var failed = new HashSet<string>(plugin.TaskRunner.FailedCharacters, StringComparer.OrdinalIgnoreCase);
        failed.UnionWith(xagmanCollectionFirstFailedCharacters);
        var failedCount = keys.Count(key => failed.Contains(key) && !xagmanSkippedCharacters.Contains(key));
        var skippedCount = keys.Count(xagmanSkippedCharacters.Contains);
        var partialCount = keys.Count(xagmanPartialOwners.ContainsKey);
        var completed = owner && !onh
            ? keys.Count(key => xagmanOwnerCompletedKeys.Contains(key) && !failed.Contains(key)
                && !xagmanSkippedCharacters.Contains(key) && !xagmanPartialOwners.ContainsKey(key))
            : Math.Clamp(owner ? GetXagmanLocalOwnerCompletedCharacters() : GetXagmanLocalTonyCompletedCharacters(), 0, keys.Count);
        var estimate = global::XASlave.Localization.UiMessage.Key("SCA184496974204A0");
        if (owner && !onh && partialCount == 0 && xagmanRunning)
        {
            // Collection-first progress covers the whole run, but timing samples reset
            // for Restock and must estimate only the current phase's roster.
            var etaPlan = xagmanOwnerRunPlan;
            var etaKeys = new HashSet<string>(etaPlan, StringComparer.OrdinalIgnoreCase);
            var samples = xagmanCharDurationSeconds.Where(pair => etaKeys.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var label = GetXagmanOwnerEtaLabelMessage(etaPlan, samples);
            if (string.IsNullOrWhiteSpace(label.Original)) label = global::XASlave.Localization.UiMessage.Key("SCA184496974204A0");
            estimate = (IsXagmanCollectionFirstRunActive() ? global::XASlave.Localization.UiMessage.Format("S31DBC1ABDB977186", GetXagmanPhaseMessage(xagmanRunPhase), label) : label);
        }
        else if (partialCount > 0)
            estimate = global::XASlave.Localization.UiMessage.Key("SDA44543F3F31CCAC");

        var displayState = GetXagmanStatusMessage(xagmanStatus);
        if (!onh && xagmanRunning)
        {
            if (!owner) estimate = GetXagmanTonyEtaLabel();
            else if (TryGetXagmanOwnerRouteWait(out var waitingState, out var waitingEstimate))
            {
                displayState = waitingState;
                estimate = waitingEstimate;
            }
        }

        // Never include free-form task/status text: it can expose character and world names.
        xagmanMiniSnapshot = new XagmanMiniSnapshot(true, xagmanRunning, xagmanActiveRole, onh,
            displayState, xagmanActiveCharacter, completed, keys.Count,
            owner && !onh ? global::XASlave.Localization.UiMessage.Key("SEEBBF6457E46A7F6") : global::XASlave.Localization.UiMessage.Key("S58190FFABF981AA3"), failedCount, skippedCount, partialCount, estimate);
    }

    private void RetainXagmanMiniResultBeforeStop()
    {
        if (!xagmanRunning)
            return;
        UpdateXagmanMiniSnapshot(true);
        var outcome = (xagmanStatus == XagmanStatus.Completed ? global::XASlave.Localization.UiMessage.Key("S22A970D2E5B1CC23") : (xagmanStatus == XagmanStatus.Error ? global::XASlave.Localization.UiMessage.Key("S54A0E8C17EBB21A1") : global::XASlave.Localization.UiMessage.Key("S1A4F630AC1B69FD0")));
        xagmanMiniSnapshot = xagmanMiniSnapshot with { Running = false, State = outcome, Character = string.Empty, Estimate = global::XASlave.Localization.UiMessage.Key("S65AD5D9043739BDA") };
    }
}
