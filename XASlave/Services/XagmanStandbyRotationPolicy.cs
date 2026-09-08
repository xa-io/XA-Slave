using System;
using System.Globalization;

namespace XASlave.Services;

internal static class XagmanStandbyRotationPolicy
{
    public static bool CanPublishRotationReady(
        bool cancellationReady,
        string targetTonyCharacter,
        string targetTonyInstanceId,
        int targetTonyCoordinationProtocolRevision,
        int requiredCoordinationProtocolRevision)
    {
        return cancellationReady
            && HasCompleteTonyScope(targetTonyCharacter, targetTonyInstanceId)
            && targetTonyCoordinationProtocolRevision == requiredCoordinationProtocolRevision;
    }

    public static string CreateRequestKey(
        string ownerKey,
        DateTime queueRequestedAtUtc,
        string targetTonyCharacter,
        string targetTonyInstanceId,
        int coordinationProtocolRevision)
    {
        if (queueRequestedAtUtc <= DateTime.MinValue
            || string.IsNullOrWhiteSpace(ownerKey)
            || !HasCompleteTonyScope(targetTonyCharacter, targetTonyInstanceId)
            || coordinationProtocolRevision <= 0)
        {
            return string.Empty;
        }

        return string.Join(
            "|",
            ownerKey.Trim().ToUpperInvariant(),
            queueRequestedAtUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            targetTonyCharacter.Trim().ToUpperInvariant(),
            targetTonyInstanceId.Trim().ToUpperInvariant(),
            coordinationProtocolRevision.ToString(CultureInfo.InvariantCulture));
    }

    public static bool TryAcceptRotationRequest(string requestKey, bool rotationReady, string targetTonyCharacter, string targetTonyInstanceId, int requestCoordinationProtocolRevision, string activeTonyCharacter, string activeTonyInstanceId, int requiredCoordinationProtocolRevision, string lastConsumedRequestKey, out bool waitingForCancellation)
    {
        waitingForCancellation = false;
        if (string.IsNullOrWhiteSpace(requestKey)
            || requestCoordinationProtocolRevision != requiredCoordinationProtocolRevision
            || !HasCompleteTonyScope(targetTonyCharacter, targetTonyInstanceId)
            || string.IsNullOrWhiteSpace(activeTonyCharacter)
            || string.IsNullOrWhiteSpace(activeTonyInstanceId)
            || !targetTonyCharacter.Trim().Equals(activeTonyCharacter.Trim(), StringComparison.OrdinalIgnoreCase)
            || !targetTonyInstanceId.Trim().Equals(activeTonyInstanceId.Trim(), StringComparison.OrdinalIgnoreCase)
            || requestKey.Equals(lastConsumedRequestKey, StringComparison.Ordinal))
        {
            return false;
        }

        if (!rotationReady)
        {
            waitingForCancellation = true;
            return false;
        }

        return true;
    }

    private static bool HasCompleteTonyScope(string targetTonyCharacter, string targetTonyInstanceId)
    {
        return !string.IsNullOrWhiteSpace(targetTonyCharacter)
            && !string.IsNullOrWhiteSpace(targetTonyInstanceId);
    }
}
