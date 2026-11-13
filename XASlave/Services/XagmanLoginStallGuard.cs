using System;

namespace XASlave.Services;

// Monotonic, operation-local policy. It never reads game state or dispatches actions.
internal sealed class XagmanLoginStallGuard
{
    internal const long QuietMilliseconds = 180_000;
    internal const long MaximumObservationGapMilliseconds = 5_000;
    internal const long RecoveryBudgetMilliseconds = 1_200_000;
    internal const int MaximumAttempts = 2;

    internal long LastObservation = -1;
    internal long QuietSince = -1;
    internal long FirstRecovery = -1;
    internal int Attempts;
    internal string Activity = string.Empty;

    internal static bool IsCandidate(bool ownedLogin, bool loggedIn, bool hasPlayer,
        bool connectedToZone, bool hasTerritory, bool usableMenuOrDialog)
        => ownedLogin && !loggedIn && !hasPlayer && connectedToZone
            && !hasTerritory && !usableMenuOrDialog;

    internal bool Observe(long now, bool candidate, string activity)
    {
        // A pause, missing framework ticks or a backwards clock cannot supply evidence
        // for uninterrupted inactivity. A hidden NowLoading addon is not required.
        var gap = LastObservation < 0 || now < LastObservation
            || now - LastObservation > MaximumObservationGapMilliseconds;
        LastObservation = now;
        if (!candidate)
        {
            QuietSince = -1;
            Activity = activity;
            return false;
        }
        if (gap || QuietSince < 0 || Activity != activity)
        {
            QuietSince = now;
            Activity = activity;
        }
        return now - QuietSince >= QuietMilliseconds;
    }

    internal bool TryBeginRecovery(long now)
    {
        if (Attempts >= MaximumAttempts || BudgetExpired(now)) return false;
        if (FirstRecovery < 0) FirstRecovery = now;
        Attempts++;
        ResetObservation();
        return true;
    }

    internal bool BudgetExpired(long now)
        => FirstRecovery >= 0 && (now < FirstRecovery || now - FirstRecovery >= RecoveryBudgetMilliseconds);

    internal void ResetObservation()
    {
        LastObservation = -1;
        QuietSince = -1;
        Activity = string.Empty;
    }
}
