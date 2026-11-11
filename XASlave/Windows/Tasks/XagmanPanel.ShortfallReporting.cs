using System;
using System.Collections.Generic;
using System.Linq;
using XASlave.Data;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    // Reporting projections must never alter the requests used by trading or supplier rotation.
    private bool IsXagmanIgnoredGilShortfall(XagmanTradeRequestEntry request)
        => request.SelectorKind == XagmanItemSelectorKind.ExactItem
            && (request.Mode is XagmanItemMode.Take or XagmanItemMode.Balance or XagmanItemMode.TopUp)
            && XagmanShortfallPolicy.IsIgnoredGilShortfall(plugin.Configuration.XagmanIgnoreGilShortfalls, request.ItemId);

    private bool IsXagmanIgnoredGilOnlyPartialOwner(XagmanPartialOwnerState state)
        => !HasXagmanUnfinishedCollectionReason(state)
            && state.PendingGiveItems.Count == 0 && state.RequestedItems.Count > 0
            && state.RequestedItems.All(IsXagmanIgnoredGilShortfall);

    private static bool HasXagmanUnfinishedCollectionReason(XagmanPartialOwnerState state)
        // Capacity cleanup can know collection is unfinished without having a live Give snapshot.
        => string.Equals(state.Reason, global::XASlave.Localization.UiLocale.Native("S6C9C9EB470B90DC4"), StringComparison.Ordinal);

    private static bool ContainsXagmanReportingCharacter(IReadOnlyCollection<string>? characters, string character)
        => characters?.Contains(character, StringComparer.OrdinalIgnoreCase) ?? false;

    private bool IsXagmanProtectedReportingOwner(string character)
        => ContainsXagmanReportingCharacter(plugin.TaskRunner.FailedCharacters, character)
            || ContainsXagmanReportingCharacter(xagmanCollectionFirstFailedCharacters, character)
            || ContainsXagmanReportingCharacter(xagmanSkippedCharacters, character);

    private int GetXagmanReportedPartialOwnerCount(
        IEnumerable<XagmanPartialOwnerState> states,
        IReadOnlyCollection<string>? failedCharacters = null,
        IReadOnlyCollection<string>? skippedCharacters = null)
        => states.Count(state => !IsXagmanIgnoredGilOnlyPartialOwner(state)
            || ContainsXagmanReportingCharacter(failedCharacters, state.CharacterNameWorld)
            || ContainsXagmanReportingCharacter(skippedCharacters, state.CharacterNameWorld));

    private XagmanPartialOwnerState GetXagmanReportedPartialOwner(
        XagmanPartialOwnerState state, bool omitOpenEnded = false, bool allowIgnoredGil = true)
    {
        var removedGil = allowIgnoredGil && state.RequestedItems.Any(IsXagmanIgnoredGilShortfall);
        return new XagmanPartialOwnerState
        {
            CharacterNameWorld = state.CharacterNameWorld,
            RequestedItems = CloneXagmanTradeRequests(state.RequestedItems
                .Where(request => (!omitOpenEnded || !IsXagmanOpenEndedSupplyRequest(request))
                    && (!allowIgnoredGil || !IsXagmanIgnoredGilShortfall(request))).ToList()),
            PendingGiveItems = CloneXagmanTradeRequests(state.PendingGiveItems),
            // A remote/free-text reason can contain the original unfiltered gil deficit.
            ReasonMessage = removedGil && !HasXagmanUnfinishedCollectionReason(state)
                ? global::XASlave.Localization.UiMessage.Raw(string.Empty) : state.ReasonMessage,
        };
    }

    private bool HasXagmanReportedFinalPartialOwnerWork(XagmanPartialOwnerState state, bool allowIgnoredGil = true)
        => HasXagmanUnfinishedCollectionReason(state) || state.PendingGiveItems.Count > 0
            // An empty partial record can represent unvisited or otherwise unknown work.
            || state.RequestedItems.Count == 0
            || state.RequestedItems.Any(request => !IsXagmanOpenEndedSupplyRequest(request)
                && (!allowIgnoredGil || !IsXagmanIgnoredGilShortfall(request)));

    private bool CanIgnoreXagmanPeerGilShortfalls(XagmanPeerPresence peer)
        => plugin.Configuration.XagmanIgnoreGilShortfalls
            && IsXagmanPeerFresh(peer)
            && peer.SupplyPassComplete
            && peer.QueueRequestedAtUtc == DateTime.MinValue
            && string.IsNullOrWhiteSpace(peer.ActiveTradePartner)
            && string.IsNullOrWhiteSpace(peer.ActiveTradePartnerInstanceId)
            && peer.Status is not (XagmanStatus.Error or XagmanStatus.Preflight or XagmanStatus.Relogging
                or XagmanStatus.Traveling or XagmanStatus.ReadyForQueue or XagmanStatus.WaitingRoom
                or XagmanStatus.Queued or XagmanStatus.Called or XagmanStatus.Trading);
}
