using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace XASlave.Services;

internal enum InventoryShuttleOutcome { Requested, ClientUpdated, Partial, Failed, Unconfirmed }
internal readonly record struct InventoryShuttleTarget(InventoryShuttleSource Before, int Capacity);

internal interface IInventoryShuttleAdapter
{
    void Validate();
    InventoryShuttleSource Read(InventoryType inventory, ushort slot);
    InventoryShuttleTarget? Find(InventoryShuttleSource source);
    int Submit(InventoryShuttleSource source, InventoryShuttleTarget target);
    void CloseOwnedMenu();
}

// Each accepted call consumes one distinct chunk of the original quota. Nothing here
// retries a native call, sweeps another stack, or labels local mutations server-confirmed.
internal sealed class InventoryShuttleSequence
{
    private readonly IInventoryShuttleAdapter adapter;
    private readonly InventoryShuttleSource original;
    private readonly long started;
    private readonly Dictionary<(InventoryType, ushort), InventoryShuttleSource> expectedDestinations = [];
    private InventoryShuttleSource expectedSource;
    private InventoryShuttleTarget? pending;
    private int expectedDelta;
    private long lastSubmission;
    private bool submitted, stopped, menuClosed;
    internal int Moved { get; private set; }
    internal int Remaining => original.Quantity - Moved;
    internal int? NativeCode { get; private set; }
    internal bool Terminal { get; private set; }
    internal InventoryShuttleOutcome Outcome { get; private set; } = InventoryShuttleOutcome.Requested;

    internal global::XASlave.Localization.UiMessage OutcomeMessage => Outcome switch
    {
        InventoryShuttleOutcome.Requested => global::XASlave.Localization.UiMessage.Key("S2D9E28289FACAB94"),
        InventoryShuttleOutcome.ClientUpdated => global::XASlave.Localization.UiMessage.Key("SFE7CD02BF86D6503"),
        InventoryShuttleOutcome.Partial => global::XASlave.Localization.UiMessage.Key("SA4D50FB854036548"),
        InventoryShuttleOutcome.Failed => global::XASlave.Localization.UiMessage.Key("S031A8F0F659DF890"),
        InventoryShuttleOutcome.Unconfirmed => global::XASlave.Localization.UiMessage.Key("S3229D16CA643CB86"),
        _ => global::XASlave.Localization.UiMessage.Value(Outcome),
    };
    internal string Detail { get; private set; } = global::XASlave.Localization.UiMessage.Key("SA0BC29B0748C1B4D").Original;
    private global::XASlave.Localization.UiMessage? uiDetail = global::XASlave.Localization.UiMessage.Key("SA0BC29B0748C1B4D");
    internal global::XASlave.Localization.UiMessage DetailMessage => global::XASlave.Localization.UiMessage.Capture(ref uiDetail, Detail);
    internal string LocalizedDetail => global::XASlave.Localization.UiMessage.Resolve(ref uiDetail, Detail);

    internal InventoryShuttleSequence(IInventoryShuttleAdapter adapter, InventoryShuttleSource original, long now)
    {
        if (original.Quantity <= 0) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S474E68B737127F26"), originalMessage => new ArgumentException(originalMessage));
        this.adapter = adapter; this.original = original; expectedSource = original; started = now;
    }

    internal void Cancel(string reason) => Cancel(global::XASlave.Localization.UiMessage.Raw(reason));

    internal void Cancel(global::XASlave.Localization.UiMessage reason)
    {
        if (stopped) return;
        stopped = true; Terminal = true; pending = null;
        Outcome = submitted ? InventoryShuttleOutcome.Unconfirmed : InventoryShuttleOutcome.Failed;
        Detail = global::XASlave.Localization.UiMessage.Store(ref uiDetail, reason);
    }

    internal void Tick(long now)
    {
        if (stopped) return;
        try
        {
            adapter.Validate();
            // After a terminal local result, continue detecting corrections until the
            // original bounded observation window closes or a new user intent replaces us.
            if (Terminal)
            {
                ValidateObserved();
                if (now - started >= 10000) stopped = true;
                return;
            }
            if (now - started >= 10000) { Cancel(global::XASlave.Localization.UiMessage.Key("S5B654E18FF938E91")); return; }
            if (pending is { } target)
            {
                var source = adapter.Read(original.Inventory, original.Slot);
                var destination = adapter.Read(target.Before.Inventory, target.Before.Slot);
                var sourceQuantity = expectedSource.Quantity - expectedDelta;
                if (source.Quantity != sourceQuantity || (sourceQuantity == 0 ? source.State.FullId != 0 : source.State != original.State)
                    || destination.Quantity != target.Before.Quantity + expectedDelta || destination.State != original.State)
                { Cancel(global::XASlave.Localization.UiMessage.Key("SA4EA888B9F5A22BC")); return; }
                expectedSource = source;
                expectedDestinations[(destination.Inventory, destination.Slot)] = destination;
                Moved += expectedDelta; pending = null;
                ValidateObserved();
                if (Remaining == 0)
                {
                    Terminal = true; Outcome = InventoryShuttleOutcome.ClientUpdated;
                    Detail = global::XASlave.Localization.UiMessage.Store(ref uiDetail, global::XASlave.Localization.UiMessage.Key("S43B79AA11B30E61F"));
                }
                else Detail = global::XASlave.Localization.UiMessage.Store(ref uiDetail, global::XASlave.Localization.UiMessage.Key("S7A7700AD99400BCC"));
                return;
            }
            ValidateObserved();
            if (submitted && now - lastSubmission < 250) return;
            var next = adapter.Find(expectedSource);
            if (next == null)
            {
                Terminal = true; Outcome = Moved > 0 ? InventoryShuttleOutcome.Partial : InventoryShuttleOutcome.Failed;
                Detail = global::XASlave.Localization.UiMessage.Store(ref uiDetail, global::XASlave.Localization.UiMessage.Key("SFD57E2C19FE3F363"));
                return;
            }
            var candidate = next.Value;
            if (candidate.Before.Inventory == original.Inventory && candidate.Before.Slot == original.Slot)
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SF8B423EAB2E33383"), originalMessage => new InvalidOperationException(originalMessage));
            expectedDelta = Math.Min(expectedSource.Quantity, candidate.Capacity - candidate.Before.Quantity);
            if (expectedDelta <= 0 || expectedDelta > Remaining) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S04BE5F3F8C219D2F"), originalMessage => new InvalidOperationException(originalMessage));
            adapter.Validate();
            ValidateObserved();
            if (adapter.Read(candidate.Before.Inventory, candidate.Before.Slot) != candidate.Before)
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S81C4621B7C31E86E"), originalMessage => new InvalidOperationException(originalMessage));
            if (stopped) return;
            pending = candidate; submitted = true; lastSubmission = now;
            NativeCode = adapter.Submit(expectedSource, candidate);
            if (stopped) return;
            if (NativeCode != 0)
            {
                pending = null; stopped = true; Terminal = true; Outcome = InventoryShuttleOutcome.Failed;
                Detail = global::XASlave.Localization.UiMessage.Store(ref uiDetail, global::XASlave.Localization.UiMessage.Format("S1F2A6F460BAA3A82", NativeCode));
                return;
            }
            adapter.Validate();
            if (stopped) return;
            if (!menuClosed) { menuClosed = true; adapter.CloseOwnedMenu(); }
            Detail = global::XASlave.Localization.UiMessage.Store(ref uiDetail, global::XASlave.Localization.UiMessage.Key("S12F0FC9917301EA6"));
        }
        catch (Exception error) { Cancel(global::XASlave.Localization.UiMessage.FromException(error)); }
    }

    private void ValidateObserved()
    {
        if (adapter.Read(original.Inventory, original.Slot) != expectedSource)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SD1C0CDB5AEBFB20E"), originalMessage => new InvalidOperationException(originalMessage));
        foreach (var entry in expectedDestinations.Values)
            if (adapter.Read(entry.Inventory, entry.Slot) != entry)
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SEEBA71F1266B4624"), originalMessage => new InvalidOperationException(originalMessage));
    }
}
