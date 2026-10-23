using System;
using System.Buffers.Binary;
using System.Numerics;
using XASlave.Localization;

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
        public UiMessage? FailureMessage;
    }

    private Transaction? active;
    public bool SoloOnly { get; set; } = true;
    public bool Active => active != null;
    private UiMessage? statusMessage = UiMessage.Key("S48D5CE005BEC6EA8");
    public string Status { get; private set; } = UiLocale.Native("S48D5CE005BEC6EA8");
    internal UiMessage StatusMessage => statusMessage ?? UiMessage.Raw(Status);
    public string StatusDisplay => UiMessage.Resolve(statusMessage, Status);
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
        => GetStartBlockMessage(frame, sample, now)?.Original;

    internal UiMessage? GetStartBlockMessage(TeleportFrame frame, TeleportSample? sample, long now)
    {
        if (!frame.Owner.Valid || frame.EntityId is 0 or 0xE0000000) return UiMessage.Key("SFFBE4947BF60CA65");
        if (!frame.Usable) return UiMessage.Key("S3BBA2A350C8FA042");
        if (!frame.CanStart) return UiMessage.Key("S501EFD8CF6686632");
        if (SoloOnly && !frame.Solo) return UiMessage.Key("SA5C7F8499822934D");
        if (!Fresh(frame.At, now, FrameMaxAgeMs)) return UiMessage.Key("SFD32F0E8224F2D5F");
        if (!TeleportPositionPacket.Finite(frame.Position) || !float.IsFinite(frame.Rotation)) return UiMessage.Key("S4F9CB353002DB8A8");
        if (frame.MovementVariant is <= 3) return null;
        return GetSampleBlockMessage(frame, sample, now);
    }

    private string? GetSampleBlockReason(TeleportFrame frame, TeleportSample? sample, long now)
        => GetSampleBlockMessage(frame, sample, now)?.Original;

    private UiMessage? GetSampleBlockMessage(TeleportFrame frame, TeleportSample? sample, long now)
    {
        if (sample == null) return UiMessage.Key("S3DA79791A46D7068");
        if (sample.Owner != frame.Owner) return UiMessage.Key("S0E689F23D4B05298");
        if (!TeleportPositionPacket.Valid(sample.Bytes, opcode)) return UiMessage.Key("S4DFF89E59E094DDB");
        if (!TeleportPositionPacket.Grounded(sample.Bytes)) return UiMessage.Key("SC53EF8410E234B03");
        if (frame.MovementVariant is <= 3 && (BinaryPrimitives.ReadUInt32LittleEndian(sample.Bytes.AsSpan(36)) >> 16) != frame.MovementVariant)
            return UiMessage.Key("S9ED0A10162CCA8A0");
        var distanceSquared = Vector3.DistanceSquared(TeleportPositionPacket.Position(sample.Bytes), frame.Position);
        // A stationary player does not emit periodic position updates. An unchanged position remains current.
        if (sample.At > now || (!Fresh(sample.At, now, SampleMaxAgeMs) && distanceSquared > 0.0025f))
            return UiMessage.Key("S396FD9B8AB16B484");
        if (distanceSquared > 9) return UiMessage.Key("S68E0FDD6191E447B");
        return null;
    }

    public bool TryStart(TeleportCommand command, TeleportFrame frame, TeleportSample? sample, long now)
    {
        if (!IsTeleport(command.Id)) return false;
        if (active != null)
        {
            if (CanSuppress(frame, now)) return true; // Preserve the pending destination.
            Abort(UiMessage.Key("SF8F1CCBD08BA549A"), frame, now, true);
            return false;
        }
        if (GetStartBlockMessage(frame, sample, now) is { } reason)
        {
            Status = UiMessage.Store(ref statusMessage, UiMessage.Format("S4E46DFC0288E9630", reason));
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
        Status = UiMessage.Store(ref statusMessage, UiMessage.Key("S39738B7B1E79848D"));
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
        Abort(UiMessage.Key("SFE0D2019DF9E8F61"), frame, now, true);
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
            if (active != null) Abort(UiMessage.Key("SEEDF22045E7987E8"), frame, now, true, cancelNative: false);
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
            Abort(transaction.Failure != null ? transaction.FailureMessage ?? UiMessage.Raw(transaction.Failure) : (now >= transaction.Deadline ? UiMessage.Key("SB84172BD08C78478") : UiMessage.Key("S3A8DC620A94C4712")), frame, now, true);
            return;
        }
        if (transaction.Dispatched || now < transaction.Due) return;
        transaction.Dispatched = true;
        Dispatched++;
        Status = UiMessage.Store(ref statusMessage, UiMessage.Key("S11E32605FFD8D156"));
        try
        {
            var sent = runtime.Execute(transaction.Command);
            if (active != transaction) return;
            if (transaction.Failure != null)
            {
                Abort(transaction.FailureMessage ?? UiMessage.Raw(transaction.Failure), frame, now, true);
                return;
            }
            if (!sent)
            {
                Abort(UiMessage.Key("S0C168ED63AB1546F"), frame, now, true);
                return;
            }
            runtime.TriggerTeleport(frame.EntityId);
            // GeneralAction 7 can report false when nested Action 5 is deliberately suppressed.
            // Arrival is established by the client, never by this return value.
        }
        catch
        {
            if (active == transaction) Abort(UiMessage.Key("SFF19F009D1B512C4"), frame, now, true);
        }
    }

    public void Transport()
    {
        if (active == null) return;
        active = null;
        TransportsObserved++;
        Status = UiMessage.Store(ref statusMessage, UiMessage.Key("S53D120C68025B860"));
    }

    public void NotifyFailure(uint logId)
    {
        if (active != null && IsFailureLog(logId)) active.Failure = UiMessage.Store(ref active.FailureMessage, UiMessage.Format("SC593B470059B9D61", logId));
    }

    public void Abort(string reason, TeleportFrame frame, long now, bool failure, bool cancelNative = true)
        => Abort(UiMessage.Raw(reason), frame, now, failure, cancelNative);

    internal void Abort(UiMessage reason, TeleportFrame frame, long now, bool failure, bool cancelNative = true)
    {
        var transaction = active;
        active = null; // Invalidate deferred work before calling any reentrant native function.
        if (transaction == null) return;
        Status = UiMessage.Store(ref statusMessage, reason);
        if (failure) Failures++;
        if (!frame.Owner.Valid || frame.Owner != transaction.Start.Owner || !frame.CanRestore ||
            !Fresh(frame.At, now, FrameMaxAgeMs)) return;
        if (cancelNative && transaction.Dispatched)
        {
            try { runtime.Execute(new TeleportCommand(204, 0, 0, 0, 0)); }
            catch { Status = UiMessage.Store(ref statusMessage, UiMessage.Format("S167D3A38EEA08664", StatusMessage)); }
        }
        try
        {
            var sample = transaction.Sample;
            var bytes = TeleportPositionPacket.Rewrite(sample.Bytes, opcode, frame.Position, frame.Rotation);
            if (!runtime.SendPosition(frame.Owner, bytes, sample.A3, sample.A4)) Status = UiMessage.Store(ref statusMessage, UiMessage.Format("SB8D39BBD97628E01", StatusMessage));
        }
        catch { Status = UiMessage.Store(ref statusMessage, UiMessage.Format("S94B1695F35C40850", StatusMessage)); }
    }
}
