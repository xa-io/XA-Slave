using System;
using System.Collections.Generic;
using System.Linq;
using XASlave.Data;

namespace XASlave.Windows;

public partial class SlaveWindow
{
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
                var state = xagmanStatus == XagmanStatus.Error ? "Error"
                    : xagmanStatus == XagmanStatus.Completed ? "Completed"
                    : xagmanMiniSnapshot.Running ? "Stopped" : xagmanMiniSnapshot.State;
                xagmanMiniSnapshot = xagmanMiniSnapshot with { Running = false, State = state, Character = string.Empty, Estimate = "Run ended" };
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
        var estimate = "Unavailable";
        if (owner && !onh && partialCount == 0 && xagmanRunning)
        {
            // Collection-first progress covers the whole run, but timing samples reset
            // for Restock and must estimate only the current phase's roster.
            var etaPlan = xagmanOwnerRunPlan;
            var etaKeys = new HashSet<string>(etaPlan, StringComparer.OrdinalIgnoreCase);
            var samples = xagmanCharDurationSeconds.Where(pair => etaKeys.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var label = GetXagmanOwnerEtaLabel(etaPlan, samples);
            if (string.IsNullOrWhiteSpace(label)) label = "Unavailable";
            estimate = IsXagmanCollectionFirstRunActive() ? $"{xagmanRunPhase} phase: {label}" : label;
        }
        else if (partialCount > 0)
            estimate = "Unavailable while trade work remains unfinished";

        // Never include free-form task/status text: it can expose character and world names.
        xagmanMiniSnapshot = new XagmanMiniSnapshot(true, xagmanRunning, xagmanActiveRole, onh,
            xagmanStatus.ToString(), xagmanActiveCharacter, completed, keys.Count,
            owner && !onh ? "complete" : "processed", failedCount, skippedCount, partialCount, estimate);
    }

    private void RetainXagmanMiniResultBeforeStop()
    {
        if (!xagmanRunning)
            return;
        UpdateXagmanMiniSnapshot(true);
        var outcome = xagmanStatus == XagmanStatus.Completed ? "Completed"
            : xagmanStatus == XagmanStatus.Error ? "Error" : "Stopped";
        xagmanMiniSnapshot = xagmanMiniSnapshot with { Running = false, State = outcome, Character = string.Empty, Estimate = "Run ended" };
    }
}
