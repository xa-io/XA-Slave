namespace XASlave.Services;

internal static class InstantTeleportPeerPolicy
{
    internal static TeleportFrame Apply(TeleportFrame frame, bool? peerPermitted) =>
        peerPermitted == true ? frame : frame with { Usable = false, CanStart = false };
}
