using System;
using System.Buffers.Binary;
using System.Numerics;

namespace XASlave.Services;

internal readonly record struct TeleportOwner(ulong Character, uint Territory, nint Network, nint Zone, nint Connection)
{
    public bool Valid => Character != 0 && Territory != 0 && Network != 0 && Zone != 0 && Connection != 0;
}

internal readonly record struct TeleportFrame(TeleportOwner Owner, long At, uint EntityId, Vector3 Position,
    float Rotation, bool Usable, bool CanStart, bool Solo, bool PlayersNearby, bool CanRestore)
{
    public byte? MovementVariant { get; init; }
}

internal readonly record struct TeleportCommand(int Id, int P1, int P2, int P3, int P4);
internal sealed record TeleportSample(TeleportOwner Owner, long At, byte[] Bytes, uint A3, uint A4);

internal static class TeleportProximity
{
    public static float ClampDistance(float value) => float.IsFinite(value) ? Math.Clamp(value, 1f, 300f) : 100f;

    public static bool WithinDistance(Vector3 player, Vector3 other, float distance)
    {
        // Horizontal center distance; unknown coordinates conservatively block the bypass.
        if (!TeleportPositionPacket.Finite(player) || !TeleportPositionPacket.Finite(other)) return true;
        var x = player.X - other.X;
        var z = player.Z - other.Z;
        var radius = ClampDistance(distance);
        return x * x + z * z < radius * radius;
    }
}

internal static class TeleportPositionPacket
{
    // Native dispatch copies Length - 16 bytes from offset 32: 40 - 16 + 32 = 56.
    public const int Size = 56;
    public const ulong Length = 40;

    // Send marker for explicitly prioritized packets.
    public const uint PreparedSendMarker = 159868227;

    public static byte[] Create(int opcode, Vector3 position, float rotation, byte movementVariant)
    {
        if (opcode is <= 0 or > ushort.MaxValue || !Finite(position) || !float.IsFinite(rotation) || movementVariant > 3)
            throw new ArgumentException("Invalid current position packet state.");
        var bytes = new byte[Size];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, opcode);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36), (uint)movementVariant << 16);
        return Rewrite(bytes, opcode, position, rotation);
    }

    public static bool Valid(ReadOnlySpan<byte> bytes, int opcode)
    {
        if (bytes.Length != Size || opcode is <= 0 or > ushort.MaxValue ||
            BinaryPrimitives.ReadInt32LittleEndian(bytes) != opcode ||
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]) != Length)
            return false;
        return float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(bytes[32..])) && Finite(Position(bytes));
    }

    public static Vector3 Position(ReadOnlySpan<byte> bytes) => new(
        BinaryPrimitives.ReadSingleLittleEndian(bytes[40..]),
        BinaryPrimitives.ReadSingleLittleEndian(bytes[44..]),
        BinaryPrimitives.ReadSingleLittleEndian(bytes[48..]));

    public static bool Grounded(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size) return false;
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes[36..]);
        return (flags & ~0x30000u) is 0 or 2; // Normal or walking, including the four movement variants.
    }

    public static bool Finite(Vector3 position) => float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z);

    public static byte[] Rewrite(ReadOnlySpan<byte> template, int opcode, Vector3 position, float rotation)
    {
        if (!Valid(template, opcode) || !Finite(position) || !float.IsFinite(rotation))
            throw new ArgumentException("Invalid position packet or coordinates.");
        var copy = template.ToArray();
        BinaryPrimitives.WriteSingleLittleEndian(copy.AsSpan(32), rotation);
        BinaryPrimitives.WriteSingleLittleEndian(copy.AsSpan(40), position.X);
        BinaryPrimitives.WriteSingleLittleEndian(copy.AsSpan(44), position.Y);
        BinaryPrimitives.WriteSingleLittleEndian(copy.AsSpan(48), position.Z);
        return copy;
    }
}

internal interface IInstantTeleportRuntime
{
    bool SendPosition(TeleportOwner owner, byte[] bytes, uint a3, uint a4);
    bool Execute(TeleportCommand command);
    void TriggerTeleport(uint entityId);
}

// Called under the service's single monitor. No tasks/timers retain native pointers.
internal sealed class InstantTeleportWorkflow(IInstantTeleportRuntime runtime, int opcode)
{
    public const int TimeoutMs = 6000;
    public const int SampleMaxAgeMs = 2000;
    public const int FrameMaxAgeMs = 250;
    private sealed class Transaction(TeleportFrame frame, TeleportCommand command, TeleportSample sample, long now)
    {
        public readonly TeleportFrame Start = frame;
        public readonly TeleportCommand Command = command;
        public readonly TeleportSample Sample = sample with { Bytes = (byte[])sample.Bytes.Clone() };
        public readonly long Due = now + (frame.PlayersNearby ? 750 : 0);
        public readonly long Deadline = now + TimeoutMs;
        public bool Dispatched;
        public string? Failure;
    }

    private Transaction? active;
    public bool SoloOnly { get; set; } = true;
    public bool Active => active != null;
    public string Status { get; private set; } = "Ready; waiting for a recent position update.";
    public int Requests { get; private set; }
    public int Dispatched { get; private set; }
    public int TransportsObserved { get; private set; }
    public int Failures { get; private set; }
    public int NativeCancelsSuppressed { get; private set; }
    public int PreparedPositions { get; private set; }

    public static bool IsTeleport(int command) => command is 202 or 211;
    public static bool IsFailureLog(uint id) => id is 4000 or >= 1661 and <= 1672;
    private static bool Fresh(long at, long now, int maxAge) => now >= at && now - at <= maxAge;

    public string? GetStartBlockReason(TeleportFrame frame, TeleportSample? sample, long now)
    {
        if (!frame.Owner.Valid || frame.EntityId is 0 or 0xE0000000) return "No current player/area connection.";
        if (!frame.Usable) return "Player state prevents bypass (mount, movement, combat, duty or Invulnerable Mode).";
        if (!frame.CanStart) return "Player is busy or already casting.";
        if (SoloOnly && !frame.Solo) return "Only when alone is enabled: party membership or a player inside the safe distance.";
        if (!Fresh(frame.At, now, FrameMaxAgeMs)) return "Waiting for a current game frame.";
        if (!TeleportPositionPacket.Finite(frame.Position) || !float.IsFinite(frame.Rotation)) return "Invalid current player coordinates.";
        if (frame.MovementVariant is <= 3) return null;
        return GetSampleBlockReason(frame, sample, now);
    }

    private string? GetSampleBlockReason(TeleportFrame frame, TeleportSample? sample, long now)
    {
        if (sample == null) return "Current movement state unavailable; move once for a position sample.";
        if (sample.Owner != frame.Owner) return "Position sample belongs to another area or connection.";
        if (!TeleportPositionPacket.Valid(sample.Bytes, opcode)) return "Position packet layout did not validate.";
        if (!TeleportPositionPacket.Grounded(sample.Bytes)) return "Last position update was not grounded movement.";
        if (frame.MovementVariant is <= 3 && (BinaryPrimitives.ReadUInt32LittleEndian(sample.Bytes.AsSpan(36)) >> 16) != frame.MovementVariant)
            return "Movement variant changed since the last position sample.";
        var distanceSquared = Vector3.DistanceSquared(TeleportPositionPacket.Position(sample.Bytes), frame.Position);
        // A stationary player does not emit periodic position updates. An unchanged position remains current.
        if (sample.At > now || (!Fresh(sample.At, now, SampleMaxAgeMs) && distanceSquared > 0.0025f))
            return "Player moved since the last position update; waiting for a new sample.";
        if (distanceSquared > 9) return "Position sample is too far from the current player.";
        return null;
    }

    public bool TryStart(TeleportCommand command, TeleportFrame frame, TeleportSample? sample, long now)
    {
        if (!IsTeleport(command.Id)) return false;
        if (active != null)
        {
            if (CanSuppress(frame, now)) return true; // Preserve the pending destination.
            Abort("Previous teleport stopped before a new request.", frame, now, true);
            return false;
        }
        if (GetStartBlockReason(frame, sample, now) is { } reason)
        {
            Status = $"Normal teleport: {reason}";
            return false;
        }

        if (GetSampleBlockReason(frame, sample, now) != null)
        {
            sample = new TeleportSample(frame.Owner, now,
                TeleportPositionPacket.Create(opcode, frame.Position, frame.Rotation, frame.MovementVariant!.Value),
                0, TeleportPositionPacket.PreparedSendMarker);
            PreparedPositions++;
        }
        var transaction = new Transaction(frame, command, sample!, now);
        active = transaction;
        Requests++;
        Status = "Preparing instant teleport.";
        try
        {
            var bytes = TeleportPositionPacket.Rewrite(sample!.Bytes, opcode, frame.Position - new Vector3(0, 100, 0), frame.Rotation);
            var sent = runtime.SendPosition(frame.Owner, bytes, sample.A3, sample.A4);
            // A synchronous cancellation/failure callback owns cleanup; never replay after it.
            if (active != transaction) return true;
            if (sent) return true;
        }
        catch { /* Fall through to bounded cleanup and ordinary teleport. */ }
        if (active != transaction) return true;
        Abort("Position send failed; using normal teleport.", frame, now, true);
        return false;
    }

    public bool CanSuppress(TeleportFrame frame, long now) => active is { } transaction &&
        frame.Owner == transaction.Start.Owner && frame.Owner.Valid && frame.Usable &&
        Fresh(frame.At, now, FrameMaxAgeMs) && now < transaction.Deadline && transaction.Failure == null && (!SoloOnly || frame.Solo);

    public bool SuppressAction(uint type, uint id, TeleportFrame frame, long now) =>
        type == 1 && id == 5 && CanSuppress(frame, now);

    public bool PreventNativeCancel(TeleportFrame frame, long now)
    {
        // The client emits command 204 itself when the intentionally suppressed Action 5 cannot start.
        // Explicit Testbench cancellation uses Abort and the original command function, bypassing this gate.
        if (!CanSuppress(frame, now))
        {
            if (active != null) Abort("Teleport cancelled after bypass state expired.", frame, now, true, cancelNative: false);
            return false;
        }
        NativeCancelsSuppressed++;
        return true;
    }

    public void Tick(TeleportFrame frame, long now)
    {
        var transaction = active;
        if (transaction == null) return;
        if (!CanSuppress(frame, now))
        {
            Abort(transaction.Failure ?? (now >= transaction.Deadline ? "Teleport timed out." : "Teleport stopped: player or area state changed."), frame, now, true);
            return;
        }
        if (transaction.Dispatched || now < transaction.Due) return;
        transaction.Dispatched = true;
        Dispatched++;
        Status = "Teleport requested; waiting for area transport.";
        try
        {
            var sent = runtime.Execute(transaction.Command);
            if (active != transaction) return;
            if (transaction.Failure != null)
            {
                Abort(transaction.Failure, frame, now, true);
                return;
            }
            if (!sent)
            {
                Abort("Teleport command failed.", frame, now, true);
                return;
            }
            runtime.TriggerTeleport(frame.EntityId);
            // GeneralAction 7 can report false when nested Action 5 is deliberately suppressed.
            // Arrival is established by the client, never by this return value.
        }
        catch
        {
            if (active == transaction) Abort("Teleport dispatch failed.", frame, now, true);
        }
    }

    public void Transport()
    {
        if (active == null) return;
        active = null;
        TransportsObserved++;
        Status = "Area transport observed; suppression released.";
    }

    public void NotifyFailure(uint logId)
    {
        if (active != null && IsFailureLog(logId)) active.Failure = $"Teleport failed (game message {logId}).";
    }

    public void Abort(string reason, TeleportFrame frame, long now, bool failure, bool cancelNative = true)
    {
        var transaction = active;
        active = null; // Invalidate deferred work before calling any reentrant native function.
        if (transaction == null) return;
        Status = reason;
        if (failure) Failures++;
        if (!frame.Owner.Valid || frame.Owner != transaction.Start.Owner || !frame.CanRestore ||
            !Fresh(frame.At, now, FrameMaxAgeMs)) return;
        if (cancelNative && transaction.Dispatched)
        {
            try { runtime.Execute(new TeleportCommand(204, 0, 0, 0, 0)); }
            catch { Status += " Native cancellation failed."; }
        }
        try
        {
            var sample = transaction.Sample;
            var bytes = TeleportPositionPacket.Rewrite(sample.Bytes, opcode, frame.Position, frame.Rotation);
            if (!runtime.SendPosition(frame.Owner, bytes, sample.A3, sample.A4)) Status += " Position restore was not accepted.";
        }
        catch { Status += " Position restore failed."; }
    }
}
