using System;

namespace XASlave.Data;

public readonly record struct ZoneIdentityContext(long LoginGeneration, long EntryGeneration, ulong ContentId, uint Territory, uint World, uint DataCenter, bool Ready, bool Replay);
public sealed record ZoneInitObservation(long Sequence, long LoginGeneration, ulong ContentId, uint World, ushort Territory, ushort Server, ushort Instance, bool Replay);
public readonly record struct ZoneIdentityView(ZoneIdentityContext Context, ZoneInitObservation? Packet, long EntryFloor, bool CachedEnableAllowed, bool HookAvailable);
public sealed record FieldOperationsIdentity(long Generation, long Sequence, uint Territory, ulong ContentId, uint World, uint DataCenter, ushort Server, ushort Instance, bool Cached)
{
    public uint Composite => ((uint)Server << 16) | Instance;
}

// All mutations are serialized by ZoneInitObservationService. Packet staging is separate from published entry state.
public sealed class ZoneInitIdentityState
{
    public ZoneIdentityContext Context { get; private set; }
    public ZoneInitObservation? Latest { get; private set; }
    public long EntryFloor { get; private set; }
    public bool CachedEnableAllowed { get; private set; } = true;
    private long sequence, settledSequence;
    private bool initialized, loading;

    public void SetContext(ulong contentId, uint territory, uint world, uint dataCenter, bool ready, bool replay, bool isLoading)
    {
        if (!initialized)
        {
            initialized = true;
            Context = new(1, 1, contentId, territory, world, dataCenter, ready, replay);
            loading = isLoading;
            if (isLoading || replay) CachedEnableAllowed = false;
        }
        else
        {
            var loginChanged = contentId != Context.ContentId || (Context.World != 0 && world != Context.World);
            var transition = loginChanged || territory != Context.Territory || replay != Context.Replay || (isLoading && !loading) || (!ready && Context.Ready);
            if (transition) Invalidate(territory, replay != Context.Replay);
            // A not-yet-published packet may precede the framework's login/world update. Its copied identity must match exactly.
            if (loginChanged && Latest is { } staged && staged.Sequence > settledSequence && !staged.Replay &&
                staged.LoginGeneration == Context.LoginGeneration && contentId != 0 && world != 0 && staged.ContentId == contentId && staged.World == world)
                Latest = staged with { LoginGeneration = Context.LoginGeneration + 1 };
            Context = new(Context.LoginGeneration + (loginChanged ? 1 : 0), Context.EntryGeneration, contentId, territory, world, dataCenter, ready, replay);
            loading = isLoading;
        }
        // Only a packet belonging to this loaded entry becomes its consumed watermark.
        if (TryLive(out var live)) settledSequence = Math.Max(settledSequence, live.Sequence);
    }

    public void Invalidate(uint territory, bool discardStaged = false)
    {
        EntryFloor = Math.Max(EntryFloor, discardStaged ? sequence : settledSequence);
        CachedEnableAllowed = false;
        Context = Context with { EntryGeneration = Context.EntryGeneration + 1, Territory = territory, Ready = false };
    }

    public ZoneIdentityView Read(bool hookAvailable)
    {
        // Publication itself consumes the packet. An invalidation before the next framework tick must not reuse it.
        if (hookAvailable && TryLive(out var live)) settledSequence = Math.Max(settledSequence, live.Sequence);
        return new(Context, Latest, EntryFloor, CachedEnableAllowed, hookAvailable);
    }

    public void Stage(ushort server, ushort instance, ushort territory, bool replay, ulong contentId, uint world)
        => Latest = new(++sequence, Context.LoginGeneration, contentId, world, territory, server, instance, replay);

    public bool TryLive(out ZoneInitObservation packet)
    {
        packet = Latest!;
        return Context.Ready && !Context.Replay && Context.ContentId != 0 && Context.World != 0 && Latest is { } current &&
            current.Sequence > EntryFloor && !current.Replay && current.LoginGeneration == Context.LoginGeneration &&
            current.ContentId == Context.ContentId && current.World == Context.World && current.Territory == Context.Territory &&
            (current.Server != 0 || current.Instance != 0);
    }

    public static bool Supported(byte intendedUse) => intendedUse is 41 or 48 or 61;
    public static FieldOperationsIdentity? Select(ZoneIdentityView view, byte intendedUse, ushort cachedTerritory = 0, ushort cachedServer = 0, ushort cachedInstance = 0)
    {
        var context = view.Context;
        if (!view.HookAvailable || !Supported(intendedUse) || !context.Ready || context.Replay || context.ContentId == 0 || context.World == 0) return null;
        var packet = view.Packet;
        if (packet != null && packet.Sequence > view.EntryFloor && !packet.Replay && packet.LoginGeneration == context.LoginGeneration &&
            packet.ContentId == context.ContentId && packet.World == context.World && packet.Territory == context.Territory && (packet.Server != 0 || packet.Instance != 0))
            return new(context.EntryGeneration, packet.Sequence, context.Territory, context.ContentId, context.World, context.DataCenter, packet.Server, packet.Instance, false);
        if (view.CachedEnableAllowed && cachedTerritory == context.Territory && (cachedServer != 0 || cachedInstance != 0))
            return new(context.EntryGeneration, 0, context.Territory, context.ContentId, context.World, context.DataCenter, cachedServer, cachedInstance, true);
        return null;
    }
}
