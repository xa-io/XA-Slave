using System;

namespace XASlave.Services;

// Service-owned observation around the retained workflow. It never sends or
// retries anything and is accessed under the service's existing monitor.
internal sealed class InstantTeleportRecoveryState
{
    private TeleportOwner pendingOwner;
    internal bool Pending => pendingOwner.Valid;

    internal bool Send(TeleportOwner owner, bool preparing, Func<bool> send)
    {
        // A thrown native send may already have affected the connection.
        if (preparing) pendingOwner = owner;
        var accepted = send();
        if (!preparing && accepted && pendingOwner == owner) pendingOwner = default;
        return accepted;
    }

    internal void ObserveOwner(TeleportOwner owner)
    {
        // Missing/transient state is not evidence that recovery completed.
        if (owner.Valid && Pending && owner != pendingOwner) pendingOwner = default;
    }

    internal void ObserveOrdinaryPosition(TeleportOwner owner, bool accepted)
    {
        if (accepted && owner == pendingOwner) pendingOwner = default;
    }
}
