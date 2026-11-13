using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace XASlave.Services;

/// <summary>
/// One bounded attempt to reset this game's direct IPv4 zone connections.
/// The caller owns stall detection, run/incident identity and subsequent menu verification.
/// This transport never elevates, selects another process, or retries a native reset.
/// </summary>
internal static class XagmanLoginTcpReset
{
    private const uint Established = 5;
    private const uint DeleteTcb = 12;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint WaitTimeout = 258;
    private const uint QueryLimitedInformationAndSynchronize = 0x00101000;
    private const int MaximumTableBytes = 16 * 1024 * 1024;
    private const int MaximumConnections = 4;
    private static readonly object AttemptLock = new();
    private static readonly TimeSpan AttemptDeadline = TimeSpan.FromSeconds(10);

    // Diagnostics are original-English developer evidence, never localized UI/status text.
    // Attempted also covers an existing journal whose prior dispatch cannot be excluded.
    internal sealed record Result(bool Attempted, bool Succeeded, string Diagnostic, bool RequiresElevation = false);

    // Raw native DWORDs deliberately remain unchanged, including network-order ports.
    internal readonly record struct Connection(
        uint State, uint LocalAddress, uint LocalPort,
        uint RemoteAddress, uint RemotePort, uint OwningPid);

    internal sealed record Preparation(uint Pid, ulong CreationFileTime, string Image, Connection[] Connections);

    /// <summary>Read-only preparation for the explicit one-shot manual elevated helper.</summary>
    internal static bool TryPrepareCurrentProcess(out Preparation? preparation, out string diagnostic)
    {
        preparation = null;
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new InvalidOperationException("Windows is required");
            var pid = checked((uint)Environment.ProcessId);
            using var process = OpenProcess(QueryLimitedInformationAndSynchronize, false, pid);
            if (process.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot pin the current game process");
            var identity = ReadIdentity(process, pid);
            if (!string.Equals(Path.GetFileName(identity.Image), "ffxiv_dx11.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The current process is not ffxiv_dx11.exe");
            if (Marshal.SizeOf<TcpOwnerRow>() != 24 || Marshal.SizeOf<TcpRow>() != 20)
                throw new InvalidOperationException("TCP native layout mismatch");
            if (!TrySelectConnections(ReadConnections(), pid, out var selected, out diagnostic))
                return false;
            RequireSameProcess(process, identity);
            preparation = new(pid, identity.CreationFileTime, identity.Image, selected);
            diagnostic = "tcp_reset_prepared: read-only process and connection snapshot";
            return true;
        }
        catch (Exception error)
        {
            diagnostic = $"tcp_reset_preparation_failed: {error.GetType().Name}: {error.Message}";
            return false;
        }
    }

    internal static Result TryResetCurrentProcess(string journalDirectory, Guid attemptId, Func<bool> stillStalled)
    {
        if (!System.Threading.Monitor.TryEnter(AttemptLock))
            return new(false, false, "tcp_reset_busy: another attempt is still active");

        var attempted = false;
        try
        {
            if (!OperatingSystem.IsWindows() || attemptId == Guid.Empty)
                return new(false, false, "tcp_reset_invalid_context: Windows and a nonempty incident attempt ID are required");
            if (stillStalled == null || !stillStalled())
                return new(false, false, "tcp_reset_no_longer_admitted: login context changed before preparation");
            if (string.IsNullOrWhiteSpace(journalDirectory) || !Path.IsPathFullyQualified(journalDirectory))
                return new(false, false, "tcp_reset_invalid_journal: a fully qualified local directory is required");
            var directory = Path.GetFullPath(journalDirectory);
            if (directory.StartsWith(@"\\", StringComparison.Ordinal))
                return new(false, false, "tcp_reset_invalid_journal: network and device paths are not accepted");
            var journalPath = Path.Combine(directory, $"xagman-login-tcp-reset-{attemptId:N}.jsonl");
            if (File.Exists(journalPath))
                return new(true, false, "tcp_reset_prior_attempt: existing journal fences this incident; reconcile its result before continuing");

            var pid = checked((uint)Environment.ProcessId);
            using var process = OpenProcess(QueryLimitedInformationAndSynchronize, false, pid);
            if (process.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot pin the current game process");
            var identity = ReadIdentity(process, pid);
            if (!string.Equals(Path.GetFileName(identity.Image), "ffxiv_dx11.exe", StringComparison.OrdinalIgnoreCase))
                return new(false, false, "tcp_reset_wrong_host: the current process is not ffxiv_dx11.exe");
            if (!IsUserAnAdmin())
                return new(false, false, "tcp_reset_elevation_required: no connection reset was attempted", RequiresElevation: true);
            if (Marshal.SizeOf<TcpOwnerRow>() != 24 || Marshal.SizeOf<TcpRow>() != 20)
                return new(false, false, "tcp_reset_native_layout_mismatch: expected 24-byte owner rows and 20-byte reset rows");

            var started = Stopwatch.GetTimestamp();
            if (!TrySelectConnections(ReadConnections(), pid, out var selected, out var selectionDiagnostic))
                return new(false, false, selectionDiagnostic);
            RequireSameProcess(process, identity);

            Directory.CreateDirectory(directory);
            FileStream journal;
            try
            {
                journal = new FileStream(journalPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                    4096, FileOptions.WriteThrough);
            }
            catch (IOException) when (File.Exists(journalPath))
            {
                return new(true, false, "tcp_reset_prior_attempt: existing journal fences this incident; reconcile its result before continuing");
            }

            using (journal)
            {
                WriteRecord(journal, new
                {
                    State = "prepared", AtUtc = DateTimeOffset.UtcNow, Schema = "xaslave.login-tcp-reset/v1",
                    AttemptId = attemptId, Process = identity, Connections = selected,
                });

                foreach (var connection in selected)
                {
                    if (Stopwatch.GetElapsedTime(started) >= AttemptDeadline)
                        throw new InvalidOperationException("Attempt deadline elapsed; no further reset was submitted");
                    RequireSameProcess(process, identity);
                    var current = ReadConnections();
                    var matches = current.Where(row => SameTuple(row, connection)).ToArray();
                    if (matches.Length == 0)
                    {
                        WriteRecord(journal, new { State = "already_absent", AtUtc = DateTimeOffset.UtcNow, Connection = connection });
                        continue;
                    }
                    if (matches.Length != 1 || matches[0] != connection || !IsEligibleConnection(matches[0], pid))
                        throw new InvalidOperationException("The selected tuple changed owner, state or identity; no further reset was submitted");
                    // A newly observed zone peer/candidate invalidates the prepared set.
                    if (!TrySelectConnections(current, pid, out var remaining, out var diagnostic)
                        || remaining.Any(row => !selected.Contains(row)))
                        throw new InvalidOperationException("The prepared connection set changed: " + diagnostic);
                    RequireSameProcess(process, identity);
                    var reset = new TcpRow
                    {
                        State = DeleteTcb, LocalAddress = connection.LocalAddress, LocalPort = connection.LocalPort,
                        RemoteAddress = connection.RemoteAddress, RemotePort = connection.RemotePort,
                    };
                    WriteRecord(journal, new { State = "dispatching", AtUtc = DateTimeOffset.UtcNow, Connection = connection });
                    // Durable disk flush can take time. Recheck after it, immediately before
                    // the sole mutating call, while retaining the original raw tuple bytes.
                    if (Stopwatch.GetElapsedTime(started) >= AttemptDeadline)
                        throw new InvalidOperationException("Attempt deadline elapsed after journaling; no further reset was submitted");
                    RequireSameProcess(process, identity);
                    var beforeCall = ReadConnections().Where(row => SameTuple(row, connection)).ToArray();
                    if (beforeCall.Length != 1 || beforeCall[0] != connection)
                        throw new InvalidOperationException("Tuple ownership or state changed after journaling; no further reset was submitted");
                    if (Stopwatch.GetElapsedTime(started) >= AttemptDeadline)
                        throw new InvalidOperationException("Attempt deadline elapsed during the final recheck; no further reset was submitted");
                    if (!stillStalled())
                        throw new InvalidOperationException("Login context progressed before dispatch; no further reset was submitted");
                    if (Stopwatch.GetElapsedTime(started) >= AttemptDeadline)
                        throw new InvalidOperationException("Attempt deadline elapsed during admission recheck; no further reset was submitted");
                    attempted = true;
                    // SetTcpEntry has no PID parameter. Rechecks minimize, but cannot remove,
                    // the native enumeration-to-call tuple-reuse race.
                    var code = SetTcpEntry(ref reset);
                    WriteRecord(journal, new { State = "returned", AtUtc = DateTimeOffset.UtcNow, Connection = connection, Win32Result = code });
                    if (code != 0)
                        return new(true, false, $"tcp_reset_native_failure: SetTcpEntry returned {code}; no further reset was submitted");
                }

                RequireSameProcess(process, identity);
                var stillPresent = ReadConnections().Where(row => selected.Any(saved => SameTuple(row, saved))).ToArray();
                WriteRecord(journal, new
                {
                    State = "completed", AtUtc = DateTimeOffset.UtcNow, RemainingSelectedConnections = stillPresent,
                    GameEffect = "unverified: caller must independently observe a usable menu before resuming",
                });
                return new(attempted, stillPresent.Length == 0,
                    stillPresent.Length == 0
                        ? "tcp_reset_connections_absent: game recovery remains unverified"
                        : "tcp_reset_connections_remain: no automatic retry is permitted");
            }
        }
        catch (Exception error)
        {
            // A failure after the dispatch record may include a completed native call.
            // The immutable journal must remain in place even when result persistence fails.
            return new(attempted, false,
                $"tcp_reset_{(attempted ? "uncertain" : "not_dispatched")}: {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            System.Threading.Monitor.Exit(AttemptLock);
        }
    }

    /// <summary>Pure selection policy, suitable for fixture tests without opening a process or socket.</summary>
    internal static bool TrySelectConnections(IReadOnlyList<Connection> rows, uint pid,
        out Connection[] selected, out string diagnostic)
    {
        selected = [];
        if (pid == 0)
        {
            diagnostic = "tcp_reset_invalid_pid";
            return false;
        }

        var candidates = new List<Connection>();
        foreach (var row in rows)
        {
            if (row.OwningPid != pid || row.State != Established || !IsZonePort(row.RemotePort))
                continue;
            if (!IsEligibleConnection(row, pid))
            {
                diagnostic = "tcp_reset_unsupported_endpoint: zone-port connection is private, proxied or malformed";
                return false;
            }
            candidates.Add(row);
            if (candidates.Count > MaximumConnections)
            {
                diagnostic = "tcp_reset_ambiguous_connections: more than four zone connections";
                return false;
            }
        }

        if (candidates.Count == 0)
        {
            diagnostic = "tcp_reset_no_supported_connections: no direct established IPv4 zone connection";
            return false;
        }
        if (candidates.Select(row => row.RemoteAddress).Distinct().Count() != 1
            || candidates.Where((row, index) => candidates.Take(index).Any(other => SameTuple(row, other))).Any())
        {
            diagnostic = "tcp_reset_ambiguous_connections: multiple zone peers or duplicate tuples";
            return false;
        }
        selected = candidates.OrderBy(row => row.LocalAddress).ThenBy(row => row.LocalPort)
            .ThenBy(row => row.RemotePort).ToArray();
        diagnostic = "tcp_reset_selection_ready";
        return true;
    }

    private static bool IsEligibleConnection(Connection row, uint pid)
        => row.OwningPid == pid && row.State == Established && IsZonePort(row.RemotePort)
            && HostPort(row.LocalPort) != 0 && IsLocalUnicast(row.LocalAddress) && IsPublicIpv4(row.RemoteAddress);

    private static ushort HostPort(uint rawPort) => BinaryPrimitives.ReverseEndianness((ushort)rawPort);

    // 55007 is the observed recovered connection; 55006-55007 is the existing
    // zone-port pair recognized by AutoDisplayNetworkLatencyService. Lobby and
    // broader heuristic port ranges deliberately have no reset fallback.
    private static bool IsZonePort(uint rawPort) => HostPort(rawPort) is 55006 or 55007;

    private static bool SameTuple(Connection left, Connection right)
        => left.LocalAddress == right.LocalAddress && left.LocalPort == right.LocalPort
            && left.RemoteAddress == right.RemoteAddress && left.RemotePort == right.RemotePort;

    private static bool IsLocalUnicast(uint address)
    {
        var first = (byte)address;
        return first is > 0 and < 224 and not 127;
    }

    private static bool IsPublicIpv4(uint address)
    {
        var first = (byte)address;
        var second = (byte)(address >> 8);
        var third = (byte)(address >> 16);
        return IsLocalUnicast(address)
            && first != 10
            && !(first == 100 && second is >= 64 and <= 127)
            && !(first == 169 && second == 254)
            && !(first == 172 && second is >= 16 and <= 31)
            && !(first == 192 && ((second is 0 or 168) || second == 88 && third == 99))
            && !(first == 198 && ((second is 18 or 19) || second == 51 && third == 100))
            && !(first == 203 && second == 0 && third == 113);
    }

    private static Connection[] ReadConnections()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint size = 0;
            var code = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0);
            if (code is not 0 and not ErrorInsufficientBuffer)
                throw new Win32Exception(checked((int)code), "TCP table sizing failed");
            if (size < sizeof(uint) || size > MaximumTableBytes)
                throw new InvalidOperationException("TCP table size exceeded the bounded reader");
            var capacity = checked((int)size);
            var buffer = Marshal.AllocHGlobal(capacity);
            try
            {
                code = GetExtendedTcpTable(buffer, ref size, false, 2, 5, 0);
                if (code == ErrorInsufficientBuffer)
                    continue; // Read-only sizing retry; native reset calls are never retried.
                if (code != 0)
                    throw new Win32Exception(checked((int)code), "TCP table read failed");
                if (size < sizeof(uint) || size > capacity)
                    throw new InvalidOperationException("TCP table returned invalid bounds");
                var count = unchecked((uint)Marshal.ReadInt32(buffer));
                var rowSize = Marshal.SizeOf<TcpOwnerRow>();
                if (count > (size - sizeof(uint)) / rowSize)
                    throw new InvalidOperationException("TCP row count exceeded the returned table");
                var rows = new Connection[checked((int)count)];
                for (var index = 0; index < rows.Length; index++)
                {
                    var row = Marshal.PtrToStructure<TcpOwnerRow>(IntPtr.Add(buffer, sizeof(uint) + index * rowSize));
                    rows[index] = new(row.State, row.LocalAddress, row.LocalPort, row.RemoteAddress, row.RemotePort, row.OwningPid);
                }
                return rows;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        throw new InvalidOperationException("TCP table changed repeatedly; no further reset was submitted");
    }

    private sealed record ProcessIdentity(uint Pid, ulong CreationFileTime, string Image);

    private static ProcessIdentity ReadIdentity(SafeProcessHandle process, uint pid)
    {
        if (process.IsInvalid || process.IsClosed || WaitForSingleObject(process, 0) != WaitTimeout)
            throw new InvalidOperationException("The pinned process is no longer live");
        var image = new StringBuilder(32768);
        uint length = 32768;
        if (!QueryFullProcessImageName(process, 0, image, ref length)
            || !GetProcessTimes(process, out var created, out _, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read the pinned process identity");
        return new(pid, ((ulong)created.High << 32) | created.Low, image.ToString());
    }

    private static void RequireSameProcess(SafeProcessHandle process, ProcessIdentity expected)
    {
        if (checked((uint)Environment.ProcessId) != expected.Pid || ReadIdentity(process, expected.Pid) != expected)
            throw new InvalidOperationException("The pinned current-process identity changed");
    }

    private static void WriteRecord(FileStream journal, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        journal.Write(bytes);
        journal.WriteByte((byte)'\n');
        journal.Flush(flushToDisk: true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime { public uint Low; public uint High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpOwnerRow
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint pid);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder image, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out NativeFileTime created,
        out NativeFileTime exited, out NativeFileTime kernel, out NativeFileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsUserAnAdmin();

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool order, uint addressFamily, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll")]
    private static extern uint SetTcpEntry(ref TcpRow row);
}
