using System;

namespace XASlave.Services;

// A batch binds both live characters and their exact coordination generation.
// CharacterId is the content ID for connected peers and the visible entity ID for ONH peers.
internal sealed record XagmanTradeCapacityIdentity(
    string InstanceId,
    string Character,
    ulong CharacterId,
    string PartnerInstanceId,
    string PartnerCharacter,
    string Scope);

internal static class XagmanTradeCapacityProtocol
{
    internal const int Revision = 1;
    internal const int MaxFreeSlots = 140;
    internal const int MaxSequence = 1_000_000;

    internal static bool IsRequestIdValid(string? requestId)
        => requestId is { Length: 32 } && Guid.TryParseExact(requestId, "N", out var id) && id != Guid.Empty;

    internal static bool IsCapacityValid(bool known, int freeSlots)
        => known && freeSlots is >= 0 and <= MaxFreeSlots;

    internal static bool IsFresh(DateTime now, DateTime observed, double maximumAgeSeconds)
        => observed > DateTime.MinValue && maximumAgeSeconds >= 0
            && (now - observed).TotalSeconds is var age && age >= 0 && age <= maximumAgeSeconds;

    internal static bool IsSequenceValid(int sequence) => sequence is > 0 and <= MaxSequence;

    internal static bool MatchesIdentity(XagmanTradeCapacityIdentity? expected, XagmanTradeCapacityIdentity? actual)
        => expected != null && actual != null && expected.CharacterId != 0
            && expected.CharacterId == actual.CharacterId
            && !string.IsNullOrWhiteSpace(expected.Character)
            && !string.IsNullOrWhiteSpace(expected.PartnerCharacter)
            && !string.IsNullOrWhiteSpace(expected.Scope)
            && string.Equals(expected.InstanceId, actual.InstanceId, StringComparison.Ordinal)
            && string.Equals(expected.Character, actual.Character, StringComparison.OrdinalIgnoreCase)
            && string.Equals(expected.PartnerInstanceId, actual.PartnerInstanceId, StringComparison.Ordinal)
            && string.Equals(expected.PartnerCharacter, actual.PartnerCharacter, StringComparison.OrdinalIgnoreCase)
            && string.Equals(expected.Scope, actual.Scope, StringComparison.Ordinal);
}
