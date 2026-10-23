using System;

namespace XASlave.Services;

internal enum FurnitureRestoreMode { PlacedToStoreroom, PlacedToInventory, StoredToInventory }
internal enum FurnitureRestoreOutcome { NotAttempted, Moved, Converted, Declined, Rejected, DependencyBlocked, Unconfirmed }
internal enum FurnitureRestoreEffect { Ordinary, Permit, Destructive, Unknown }

internal readonly record struct FurnitureRestoreSession(long Generation, nint Host, uint HostId, long HostEpoch,
    nint Agent, nint Manager, nint Territory, bool Exterior, ulong Estate, byte NativeSize, byte NativeListMode,
    ulong Character, uint TerritoryType);
internal readonly record struct FurnitureRestoreSource(uint Container, ushort Slot, int Quantity, InventoryShuttleItemState State);
internal sealed record FurnitureRestoreItemResult(FurnitureRestoreSource Source, FurnitureRestoreOutcome Outcome, string Detail)
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FurnitureRestoreItemResult, global::XASlave.Localization.UiMessage> Messages = new();
    internal global::XASlave.Localization.UiMessage DetailMessage => Messages.TryGetValue(this, out var message) ? global::XASlave.Localization.UiMessage.Capture(message, Detail) : global::XASlave.Localization.UiMessage.Raw(Detail);
    internal string LocalizedDetail => DetailMessage.Display;
    internal static FurnitureRestoreItemResult Create(FurnitureRestoreSource source, FurnitureRestoreOutcome outcome, global::XASlave.Localization.UiMessage detail)
    {
        var result = new FurnitureRestoreItemResult(source, outcome, detail.Original);
        Messages.Add(result, detail);
        return result;
    }
}

// Physical pages establish coverage, not permission or effective estate capacity.
internal static class FurnitureRestoreCapacity
{
    internal static int Limit(bool exterior, byte nativeSize)
    {
        if (nativeSize == 0) return exterior ? 40 : 300;
        if (nativeSize == 1) return exterior ? 60 : 450;
        if (nativeSize == 2) return exterior ? 80 : 600;
        if (!exterior && (nativeSize == 3 || nativeSize == 5)) return 150;
        throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SE63E7CC63DBE33C1"), originalMessageText => new InvalidOperationException(originalMessageText));
    }

    internal static uint[] Pages(bool exterior, bool stored, byte nativeSize)
    {
        _ = Limit(exterior, nativeSize);
        // Match native placement coverage: a small estate uses one 40-slot page.
        // Storage keeps both pages, including holdings above the current estate limit.
        if (exterior)
        {
            if (stored) return new uint[] { 27000, 27200 };
            return nativeSize == 0 ? new uint[] { 25001 } : new uint[] { 25001, 25200 };
        }
        var count = stored ? 12 : nativeSize == 0 ? 6 : nativeSize == 1 ? 9 : nativeSize == 2 ? 12 : 3;
        var first = stored ? 27001u : 25003u;
        var pages = new uint[count];
        for (var index = 0; index < count; index++) pages[index] = first + (uint)index;
        return pages;
    }

    internal static int StoreroomUsableEmpty(bool exterior, byte nativeSize, int nominalSlots, int emptySlots,
        int snapshotOrdinaryOccupancy, int nativeOrdinaryCount, int nativeLimit, bool allPagesLoaded, bool exclusionsReconciled)
    {
        var limit = Limit(exterior, nativeSize);
        if (!allPagesLoaded || !exclusionsReconciled) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S48E08749244C5284"), originalMessageText => new InvalidOperationException(originalMessageText));
        if (nominalSlots != (exterior ? 80 : 600) || emptySlots < 0 || emptySlots > nominalSlots
            || snapshotOrdinaryOccupancy < 0 || snapshotOrdinaryOccupancy > nominalSlots - emptySlots || nativeOrdinaryCount != snapshotOrdinaryOccupancy || nativeLimit != limit)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SC1D88307477C015C"), originalMessageText => new InvalidOperationException(originalMessageText));
        var physicalUsable = Math.Max(0, emptySlots - Math.Max(0, nominalSlots - limit));
        var estateUsable = Math.Max(0, limit - nativeOrdinaryCount);
        return Math.Min(physicalUsable, estateUsable);
    }

    internal static void ValidateMode(FurnitureRestoreMode mode)
    {
        if (!Enum.IsDefined(mode)) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S7E682E28A9C2FE9F"), originalMessageText => new ArgumentException(originalMessageText));
    }
}
