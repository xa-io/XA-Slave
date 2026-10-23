using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace XASlave.Services;

internal readonly record struct FurnitureNativeObservation(FurnitureRestoreDecision Decision, FurnitureRestoreAdmission Admission,
    bool CorrelatedRejection, bool Ambiguous, string Detail)
{
    internal global::XASlave.Localization.UiMessage? DetailMetadata { get; init; }
    internal global::XASlave.Localization.UiMessage DetailMessage => global::XASlave.Localization.UiMessage.Capture(DetailMetadata, Detail);
    internal static FurnitureNativeObservation Create(FurnitureRestoreDecision decision, FurnitureRestoreAdmission admission,
        bool correlatedRejection, bool ambiguous, global::XASlave.Localization.UiMessage detail)
        => new(decision, admission, correlatedRejection, ambiguous, detail.Original) { DetailMetadata = detail };
    public bool Equals(FurnitureNativeObservation other) => Decision == other.Decision && Admission == other.Admission
        && CorrelatedRejection == other.CorrelatedRejection && Ambiguous == other.Ambiguous && Detail == other.Detail;
    public override int GetHashCode() => HashCode.Combine(Decision, Admission, CorrelatedRejection, Ambiguous, Detail);
}
internal sealed record FurnitureWarningOpening(nint Listener, ulong Kind, uint AddonId, long AddonFloor);

// Passive callbacks always forward exactly once. No callback answers/closes a dialog,
// alters native pending fields or turns native entry/closure into transfer success.
internal sealed unsafe class FurnitureRestoreObserver : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MoveCall(nint manager, InventoryItem* item);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate AtkValue* ReceiveCall(nint listener, AtkValue* returnValue, AtkValue* values, uint count, ulong kind);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint OpenCall(nint module, nint text, nint yes, nint no, nint listener, nint kind,
        nint a7, nint a8, nint a9, nint a10, nint a11, nint a12, nint a13, nint a14,
        nint a15, nint a16, nint a17, nint a18, nint a19, nint a20, nint a21, nint a22);

    private readonly FurnitureRestoreNativeBinding binding;
    private readonly FurnitureRestoreWorld world;
    private readonly FurnitureRestoreContext context;
    private readonly MessageLogService messages;
    private readonly IToastGui toasts;
    private readonly Dictionary<string, uint> errors = new(StringComparer.Ordinal);
    private readonly FurnitureObserverHook<MoveCall> inventoryHook, storageHook;
    private readonly FurnitureObserverHook<ReceiveCall> indoorHook, outdoorHook;
    private readonly FurnitureObserverHook<OpenCall> openHook;
    private FurnitureRestoreSession session;
    private FurnitureRestoreMode mode;
    private FurnitureRestoreSource source;
    private FurnitureWarningOpening? opening;
    private FurnitureAddonStamp? warningStamp;
    private FurnitureRestoreDecision decision;
    private long generation, operation;
    private nint enteringItem;
    private int entryBudget, inOwnedEntry;
    private uint refusal;
    private bool active, submitted, disposed, subscribed;
    private string? failure;
    private global::XASlave.Localization.UiMessage? failureMessage;
    private global::XASlave.Localization.UiMessage FailureMessage => global::XASlave.Localization.UiMessage.Capture(failureMessage, failure ?? string.Empty);

    internal FurnitureRestoreObserver(FurnitureRestoreNativeBinding binding, FurnitureRestoreWorld world, FurnitureRestoreContext context,
        MessageLogService messages, IToastGui toasts)
    {
        this.binding = binding; this.world = world; this.context = context; this.messages = messages; this.toasts = toasts;
        foreach (var id in new uint[] { 3308, 3309, 3310, 3327, 3328, 3331, 3332, 3338, 3391, 3404, 3480, 3486, 3489, 6111 })
        {
            if (!Plugin.DataManager.GetExcelSheet<LogMessage>().TryGetRow(id, out var row))
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S7B5F5CD0CD02E2BE"), originalMessageText => new InvalidOperationException(originalMessageText));
            var text = row.Text.ExtractText().Trim();
            if (text.Length == 0 || (errors.TryGetValue(text, out var existing) && existing != id))
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S361F1C6217F3FEA6"), originalMessageText => new InvalidOperationException(originalMessageText));
            errors[text] = id;
        }
        inventoryHook = new(binding, "InventoryRestore", 0, InventoryEntry);
        storageHook = new(binding, "StorageRestore", 0, StorageEntry);
        indoorHook = new(binding, "IndoorDecision", 1, IndoorDecision);
        outdoorHook = new(binding, "OutdoorDecision", 1, OutdoorDecision);
        openHook = new(binding, "WarningOpen", 18, Open);
    }

    internal void Begin(FurnitureRestoreSession owner, FurnitureRestoreMode requestedMode)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (active) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S43A8843C974B9331"), originalMessageText => new InvalidOperationException(originalMessageText));
        world.Require(owner);
        if (world.HasNativePending(owner) || context.Fresh("SelectYesno") != null)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S3410AD8F77AFA71D"), originalMessageText => new InvalidOperationException(originalMessageText));
        session = owner; mode = requestedMode; var run = ++generation; operation = 0;
        failure = null; failureMessage = null; opening = null; warningStamp = null; refusal = 0; submitted = false;
        try
        {
            subscribed = true; messages.MessageObserved += Message; toasts.ErrorToast += ErrorToast;
            inventoryHook.Enable(); storageHook.Enable(); indoorHook.Enable(); outdoorHook.Enable(); openHook.Enable();
            world.Require(owner);
            if (disposed || generation != run) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SD575D05CE1ECCF0A"), originalMessageText => new InvalidOperationException(originalMessageText));
            active = true;
        }
        catch { End(); throw; }
    }

    // Called when the verified sequence advances to its next initial item. Native
    // pending cleanup must finish first; this method never resets that cleanup itself.
    internal bool Prepare(FurnitureRestoreSource item, long nextOperation)
    {
        Require();
        var run = generation;
        if (nextOperation == operation)
        {
            if (source != item) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SB3C1481AD2A115AF"), originalMessageText => new InvalidOperationException(originalMessageText));
            return true;
        }
        if (nextOperation <= operation) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SD87F756755B5FE24"), originalMessageText => new InvalidOperationException(originalMessageText));
        if (world.HasNativePending(session) || context.Fresh("SelectYesno") != null) return false;
        if (!active || disposed || generation != run) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S254C23BD771B8C10"), originalMessageText => new InvalidOperationException(originalMessageText));
        source = item; operation = nextOperation; opening = null; warningStamp = null;
        decision = FurnitureRestoreDecision.None; refusal = 0; submitted = false;
        return true;
    }

    internal void Submit(FurnitureRestoreSource item, long ownedOperation)
    {
        Require();
        var run = generation;
        if (ownedOperation != operation || source != item || submitted || world.HasNativePending(session) || context.Fresh("SelectYesno") != null)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S75F5B9F668C3FDDE"), originalMessageText => new InvalidOperationException(originalMessageText));
        var pointer = world.ResolveImmediate(session, source);
        if (!active || disposed || generation != run) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SFB44A8D1B3BE9697"), originalMessageText => new InvalidOperationException(originalMessageText));
        submitted = true; enteringItem = (nint)pointer; entryBudget = 1;
        try
        {
            var name = mode == FurnitureRestoreMode.PlacedToStoreroom ? "StorageRestore" : "InventoryRestore";
            // Exactly the normal manager entry; its warning/gates/context encoding remain native.
            ((delegate* unmanaged<HousingManager*, InventoryItem*, void>)binding.Function(name))((HousingManager*)session.Manager, pointer);
            if (active && entryBudget != 0) failure = global::XASlave.Localization.UiMessage.Store(ref failureMessage, global::XASlave.Localization.UiMessage.Key("SA14946F80837357E"));
        }
        finally { enteringItem = 0; entryBudget = 0; }
    }

    private void InventoryEntry(nint manager, InventoryItem* item) => Entry(inventoryHook.Original, false, manager, item);
    private void StorageEntry(nint manager, InventoryItem* item) => Entry(storageHook.Original, true, manager, item);

    private void Entry(MoveCall original, bool storage, nint manager, InventoryItem* item)
    {
        var run = generation;
        var owned = active && submitted && entryBudget == 1 && inOwnedEntry == 0 && manager == session.Manager
            && (nint)item == enteringItem && storage == (mode == FurnitureRestoreMode.PlacedToStoreroom);
        if (active)
        {
            if (!owned) failure = global::XASlave.Localization.UiMessage.Store(ref failureMessage, global::XASlave.Localization.UiMessage.Key("S9698DE3C9BE77748"));
            else
            {
                entryBudget = 0;
                try { Require(); if (world.ResolveImmediate(session, source) != item) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SA791D847EE2B8750"), originalMessageText => new InvalidOperationException(originalMessageText)); }
                catch (Exception error) { failure = global::XASlave.Localization.UiMessage.Store(ref failureMessage, global::XASlave.Localization.UiMessage.FromException(error)); owned = false; }
            }
        }
        if (owned) inOwnedEntry++;
        try { original(manager, item); }
        finally { if (owned && run == generation) inOwnedEntry--; }
    }

    private nint Open(nint module, nint text, nint yes, nint no, nint listener, nint kind,
        nint a7, nint a8, nint a9, nint a10, nint a11, nint a12, nint a13, nint a14,
        nint a15, nint a16, nint a17, nint a18, nint a19, nint a20, nint a21, nint a22)
    {
        var run = generation; var op = operation; var floor = context.Epoch;
        var relevant = active && submitted;
        if (relevant)
        {
            try
            {
                Require();
                if (opening != null || decision != FurnitureRestoreDecision.None || refusal != 0
                    || mode == FurnitureRestoreMode.PlacedToStoreroom || !RecognizedKind(session.Exterior, unchecked((ulong)kind)))
                    throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S648B3AEACCAB3D1B"), originalMessageText => new InvalidOperationException(originalMessageText));
                RequireListener(listener, false);
            }
            catch (Exception error) { failure = global::XASlave.Localization.UiMessage.Store(ref failureMessage, global::XASlave.Localization.UiMessage.FromException(error)); relevant = false; }
        }
        else if (active) failure = global::XASlave.Localization.UiMessage.Store(ref failureMessage, global::XASlave.Localization.UiMessage.Key("S9A0524AB27626516"));
        var result = openHook.Original(module, text, yes, no, listener, kind, a7, a8, a9, a10, a11, a12, a13, a14, a15, a16, a17, a18, a19, a20, a21, a22);
        if (relevant && active && generation == run && operation == op)
        {
            if (result <= 0 || (ulong)result > uint.MaxValue) failure = global::XASlave.Localization.UiMessage.Store(ref failureMessage, global::XASlave.Localization.UiMessage.Key("S4C0D9D3BD0039617"));
            else opening = new(listener, unchecked((ulong)kind), (uint)result, floor);
        }
        return result;
    }

    internal static bool RecognizedKind(bool exterior, ulong kind)
        => exterior ? kind == 0x19137 : kind is 0x19137 or 0x19138 or 0x1913A or 0x1913B;

    private void RequireListener(nint listener, bool requireAddon)
    {
        var expected = session.Territory + (session.Exterior ? 0x12A88 : 0x12640);
        if (listener != expected || FurnitureRestoreNativeBinding.ReadPointer(listener) != (session.Exterior ? binding.OutdoorListenerTable : binding.IndoorListenerTable)
            || FurnitureRestoreNativeBinding.ReadPointer(listener + 16) != session.Territory
            || FurnitureRestoreNativeBinding.ReadPointer(listener + 24) != (nint)world.ResolveImmediate(session, source))
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S485C9E3AA31582C5"), originalMessageText => new InvalidOperationException(originalMessageText));
        if (!requireAddon) return;
        if (opening == null || FurnitureRestoreNativeBinding.ReadUInt(listener + 32) != opening.AddonId)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S2CD4577682B9D1B1"), originalMessageText => new InvalidOperationException(originalMessageText));
        var stamp = context.Fresh("SelectYesno");
        if (stamp is not { } current || current.Epoch <= opening.AddonFloor || current.Id != opening.AddonId
            || (warningStamp != null && warningStamp != current))
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SAD2DE7A64920F065"), originalMessageText => new InvalidOperationException(originalMessageText));
        warningStamp = current;
    }

    private AtkValue* IndoorDecision(nint listener, AtkValue* result, AtkValue* values, uint count, ulong kind)
        => Receive(indoorHook.Original, listener, result, values, count, kind);
    private AtkValue* OutdoorDecision(nint listener, AtkValue* result, AtkValue* values, uint count, ulong kind)
        => Receive(outdoorHook.Original, listener, result, values, count, kind);

    private AtkValue* Receive(ReceiveCall original, nint listener, AtkValue* result, AtkValue* values, uint count, ulong kind)
    {
        var run = generation; var op = operation;
        var relevant = active && submitted && RecognizedKind(session.Exterior, kind);
        var accepted = false;
        if (relevant)
        {
            try
            {
                Require();
                if (opening == null || opening.Listener != listener || opening.Kind != kind || decision != FurnitureRestoreDecision.None || refusal != 0)
                    throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S128FEF012FCFCC86"), originalMessageText => new InvalidOperationException(originalMessageText));
                RequireListener(listener, true);
                if (values == null || count == 0) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SEE14D882F2F67817"), originalMessageText => new InvalidOperationException(originalMessageText));
                NearbyZoneNativeBinding.RequireReadable((nint)values, sizeof(AtkValue));
                if (values[0].Type != FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int)
                    throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S66965D246105BF9A"), originalMessageText => new InvalidOperationException(originalMessageText));
                accepted = values[0].Int == 0;
            }
            catch (Exception error) { failure = global::XASlave.Localization.UiMessage.Store(ref failureMessage, global::XASlave.Localization.UiMessage.FromException(error)); relevant = false; }
        }
        var returned = original(listener, result, values, count, kind);
        if (relevant && active && run == generation && op == operation)
            decision = accepted ? FurnitureRestoreDecision.Accepted : FurnitureRestoreDecision.Declined;
        return returned;
    }

    internal FurnitureNativeObservation Read()
    {
        if (!active || disposed) return FurnitureNativeObservation.Create(FurnitureRestoreDecision.Invalid, FurnitureRestoreAdmission.Rejected, false, true, global::XASlave.Localization.UiMessage.Key("S15D116CAD7B84017"));
        try
        {
            Require();
            if (opening != null && decision == FurnitureRestoreDecision.None)
            {
                RequireListener(opening.Listener, false);
                if (FurnitureRestoreNativeBinding.ReadUInt(opening.Listener + 32) != opening.AddonId)
                    throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S2CD4577682B9D1B1"), originalMessageText => new InvalidOperationException(originalMessageText));
                if (warningStamp != null || !context.WarningInitializing(opening.AddonId, opening.AddonFloor))
                    RequireListener(opening.Listener, true);
            }
            else if (opening == null && context.Fresh("SelectYesno") != null) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S7F58A45030B97931"), originalMessageText => new InvalidOperationException(originalMessageText));
        }
        catch (Exception error) { failure = global::XASlave.Localization.UiMessage.Store(ref failureMessage, global::XASlave.Localization.UiMessage.FromException(error)); }
        return FurnitureNativeObservation.Create(failure != null ? FurnitureRestoreDecision.Invalid : decision != FurnitureRestoreDecision.None ? decision
                : opening != null ? FurnitureRestoreDecision.Waiting : FurnitureRestoreDecision.None, refusal == 3391 ? FurnitureRestoreAdmission.DependencyBlocked : refusal != 0 ? FurnitureRestoreAdmission.Rejected : FurnitureRestoreAdmission.Ready, refusal != 0, failure != null, (failure != null ? FailureMessage : refusal == 0 ? global::XASlave.Localization.UiMessage.Raw(string.Empty) : global::XASlave.Localization.UiMessage.Format("S6536C24CCE5062B0" , refusal)));
    }

    internal bool HasSubmitted(long ownedOperation) => active && operation == ownedOperation && submitted;

    private void Message(MessageLogEntry entry) => CaptureError(entry.Message);
    private void ErrorToast(ref SeString message, ref bool isHandled) => CaptureError(message.TextValue);

    private void CaptureError(string text)
    {
        if (!active || !submitted || !errors.TryGetValue(text.Trim(), out var id)) return;
        try
        {
            Require();
            // Only the exact synchronous owned manager call can provide a definitive
            // refusal. Delayed matching text cannot authorize a dependency replay.
            if (inOwnedEntry == 1 && opening == null && decision == FurnitureRestoreDecision.None && (refusal == 0 || refusal == id)) refusal = id;
            else failure = global::XASlave.Localization.UiMessage.Store(ref failureMessage, global::XASlave.Localization.UiMessage.Format("S1818DCBE205CCC09" , id));
        }
        catch (Exception error) { failure = global::XASlave.Localization.UiMessage.Store(ref failureMessage, global::XASlave.Localization.UiMessage.FromException(error)); }
    }

    private void Require()
    {
        if (!active || disposed || failure != null) throw global::XASlave.Localization.UiMessage.CreateException((failure != null ? FailureMessage : global::XASlave.Localization.UiMessage.Key("S48E3BDA7D77CFCE6")), originalMessageText => new InvalidOperationException(originalMessageText));
        var run = generation;
        world.Require(session);
        if (!active || disposed || generation != run) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S04EBDE5F114E099D"), originalMessageText => new InvalidOperationException(originalMessageText));
    }

    internal void End()
    {
        active = false; generation++; enteringItem = 0; entryBudget = 0; inOwnedEntry = 0;
        if (subscribed) { messages.MessageObserved -= Message; toasts.ErrorToast -= ErrorToast; subscribed = false; }
        try { openHook.Disable(); }
        finally { try { indoorHook.Disable(); } finally { try { outdoorHook.Disable(); } finally { try { inventoryHook.Disable(); } finally { storageHook.Disable(); } } } }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { End(); }
        finally
        {
            try { openHook.Dispose(); }
            finally { try { indoorHook.Dispose(); } finally { try { outdoorHook.Dispose(); } finally { try { inventoryHook.Dispose(); } finally { storageHook.Dispose(); } } } }
        }
    }
}
