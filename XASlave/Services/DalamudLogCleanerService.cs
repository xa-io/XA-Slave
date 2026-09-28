using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

/// <summary>Archives and resets this client's active Dalamud file sink without replacing any logger.</summary>
public sealed class DalamudLogCleanerService : IDisposable
{
    public const int DefaultCheckIntervalMinutes = 15;
    public const int MaximumCheckIntervalMinutes = 1440;
    public const long AutomaticThresholdBytes = 90L * 1024 * 1024;
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const int ArchiveLimit = 10 * 1024 * 1024;
    private int cleaning;
    private long nextAllowedTick;
    private string statusText = "Ready. Keeps up to 10 MiB of recent output in the old log.";
    private string sizeStatusText = "Size has not been checked yet.";
    private readonly object automaticGate = new();
    private Timer? automaticTimer;
    private bool automaticEnabled;
    private bool disposed;
    private int checkIntervalMinutes = DefaultCheckIntervalMinutes;
    private long nextAutomaticCheckTick;
    private string automaticStatusText = "Automatic cleanup is off.";

    public string StatusText => Volatile.Read(ref statusText);
    public string SizeStatusText => Volatile.Read(ref sizeStatusText);
    public string AutomaticStatusText => Volatile.Read(ref automaticStatusText);
    public bool IsCleaning => Volatile.Read(ref cleaning) != 0;

    public bool TryCheckSize(out string message)
    {
        try
        {
            if (Volatile.Read(ref disposed))
                throw new InvalidOperationException("Dalamud Log Cleaner has stopped.");

            // Metadata only: do not flush, archive, clear, or reschedule automatic cleanup.
            var file = new FileInfo(ResolveLogPath());
            var bytes = file.Length;
            message = $"Checked {DateTime.Now:HH:mm:ss}: {file.Name} - {bytes / (1024d * 1024d):F2} MiB ({bytes:N0} bytes).";
            Volatile.Write(ref sizeStatusText, message);
            return true;
        }
        catch (Exception ex)
        {
            message = $"Could not check the log size: {ex.GetBaseException().Message}";
            Volatile.Write(ref sizeStatusText, message);
            return false;
        }
    }

    public static int NormalizeCheckInterval(int minutes)
        => Math.Clamp(minutes, 1, MaximumCheckIntervalMinutes);

    public bool ConfigureAutomatic(bool enabled, int minutes)
    {
        lock (automaticGate)
        {
            if (disposed)
                return false;

            automaticEnabled = enabled;
            checkIntervalMinutes = NormalizeCheckInterval(minutes);
            if (!enabled)
            {
                automaticTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                Volatile.Write(ref automaticStatusText, "Automatic cleanup is off.");
                return false;
            }

            automaticTimer ??= new Timer(CheckAutomatic, null, Timeout.Infinite, Timeout.Infinite);
            ScheduleNextAutomaticCheck();
            Volatile.Write(ref automaticStatusText, $"Checks every {checkIntervalMinutes} minutes; clears at 90 MiB. First check in {checkIntervalMinutes} minutes.");
            return true;
        }
    }

    public void Dispose()
    {
        // Wait for an already-started automatic action; queued callbacks then see disposed and do nothing.
        lock (automaticGate)
        {
            Volatile.Write(ref disposed, true);
            automaticEnabled = false;
            automaticTimer?.Dispose();
            automaticTimer = null;
        }
    }

    private void ScheduleNextAutomaticCheck()
    {
        var delay = checkIntervalMinutes * 60_000L;
        nextAutomaticCheckTick = Environment.TickCount64 + delay;
        automaticTimer!.Change(delay, Timeout.Infinite);
    }

    private void CheckAutomatic(object? _)
    {
        // Only file/managed logging operations run here; no native game, chat or ImGui calls.
        lock (automaticGate)
        {
            if (disposed || !automaticEnabled)
                return;

            var remaining = nextAutomaticCheckTick - Environment.TickCount64;
            if (remaining > 0)
            {
                // A callback queued before a settings change must not run the newly scheduled check early.
                automaticTimer!.Change(remaining, Timeout.Infinite);
                return;
            }

            try
            {
                var length = new FileInfo(ResolveLogPath()).Length;
                var result = $"Last check {DateTime.Now:HH:mm:ss}: {length / (1024d * 1024d):F2} MiB; below 90 MiB.";
                if (length >= AutomaticThresholdBytes)
                {
                    var success = TryCleanCore(string.Empty, AutomaticThresholdBytes, out var message);
                    result = $"Last check {DateTime.Now:HH:mm:ss}: {length / (1024d * 1024d):F2} MiB. {(success ? message : "Cleanup deferred: " + message)}";
                }
                Volatile.Write(ref automaticStatusText, result);
            }
            catch (Exception ex)
            {
                Volatile.Write(ref automaticStatusText, $"Automatic log size check failed: {ex.GetBaseException().Message}");
            }
            finally
            {
                // One-shot scheduling prevents overlapping checks and retries errors at the normal interval.
                ScheduleNextAutomaticCheck();
            }
        }
    }

    public bool TryClean(string arguments, out string message)
        => TryCleanCore(arguments, 0, out message);

    private bool TryCleanCore(string arguments, long minimumBytes, out string message)
    {
        if (Volatile.Read(ref disposed))
        {
            message = "Dalamud Log Cleaner has stopped.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(arguments))
        {
            message = "Usage: /xa clearlog";
            return false;
        }

        if (Interlocked.CompareExchange(ref cleaning, 1, 0) != 0)
        {
            message = "Dalamud Log Cleaner is already running.";
            return false;
        }

        var resetStarted = false;
        try
        {
            if (Environment.TickCount64 < nextAllowedTick)
            {
                message = "Wait five seconds between log cleanups.";
                return false;
            }

            var expectedPath = ResolveLogPath();
            var logger = Serilog.Log.Logger;
            var sink = FindFileSink(logger);
            // This private contract is verified against the Serilog 7.0 sink shipped with Dalamud 15.0.3.5.
            // Reject other versions/layouts before flushing, archiving or changing the file.
            if (sink.GetType().Assembly.GetName().Version != new Version(7, 0, 0, 0))
                throw new NotSupportedException("This Serilog file-sink version has not been verified.");

            var syncRoot = ReadField(sink, "_syncRoot");
            var output = ReadField(sink, "_output") as StreamWriter;
            var stream = ReadField(sink, "_underlyingStream") as FileStream;
            var counted = ReadField(sink, "_countingStreamWrapper") as Stream;
            if (syncRoot == null || output == null || stream == null || counted == null
                || counted.GetType().FullName != "Serilog.Sinks.File.WriteCountingStream"
                || !ReferenceEquals(counted.GetType().Assembly, sink.GetType().Assembly)
                || !ReferenceEquals(output.BaseStream, counted)
                || !ReferenceEquals(ReadField(counted, "_stream"), stream)
                || !string.Equals(Path.GetFullPath(stream.Name), expectedPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new NotSupportedException("The active Dalamud log writer does not match the supported layout.");

            var countedLength = counted.GetType().GetProperty("CountedLength", InstanceFlags);
            if (countedLength == null || countedLength.PropertyType != typeof(long))
                throw new NotSupportedException("The log size counter is unavailable.");

            if (!Monitor.TryEnter(syncRoot, millisecondsTimeout: 250))
                throw new IOException("The log writer is busy; try again later.");

            long bytesBefore;
            try
            {
                if (!ReferenceEquals(logger, Serilog.Log.Logger) || !stream.CanWrite || !stream.CanSeek)
                    throw new IOException("The active logger changed or closed; no cleanup was performed.");

                output.Flush();
                bytesBefore = stream.Length;
                if (stream.Position != bytesBefore || countedLength.GetValue(counted) is not long count || count != bytesBefore)
                    throw new NotSupportedException("The log position and byte counter disagree; no cleanup was performed.");

                // A manual cleanup may have won the race after the timer read the size. Check again under the writer lock.
                if (bytesBefore < minimumBytes)
                {
                    message = "The active log is now below 90 MiB; automatic cleanup was skipped.";
                    return true;
                }

                if (bytesBefore != 0)
                {
                    // Archive publication must succeed before touching the live stream. Read-only sharing
                    // cooperates with the writer's existing FileShare.Read handle; never reopen for writing.
                    ArchiveRecentOutput(expectedPath, bytesBefore);
                    resetStarted = true;
                    // WriteCountingStream.SetLength also resets CountedLength, so logging can resume at the cap.
                    counted.SetLength(0);
                    stream.Position = 0;
                    stream.Flush();
                    if (stream.Length != 0 || countedLength.GetValue(counted) is not long remaining || remaining != 0)
                        throw new IOException("The log reset could not be verified.");
                }
            }
            finally
            {
                Monitor.Exit(syncRoot);
            }

            nextAllowedTick = Environment.TickCount64 + 5000;
            message = bytesBefore == 0
                ? "The active Dalamud log is already empty. The old log was preserved."
                : $"Cleared {bytesBefore:N0} bytes from {Path.GetFileName(expectedPath)}; kept up to 10 MiB in {Path.GetFileName(Path.ChangeExtension(expectedPath, "old.log"))}. New entries continue normally.";
            Volatile.Write(ref statusText, message);
            return true;
        }
        catch (Exception ex)
        {
            message = resetStarted
                ? $"Log cleanup could not finish; recent output was archived but the active file may already be cleared. {ex.GetBaseException().Message}"
                : $"Log cleanup did not clear the active file. {ex.GetBaseException().Message}";
            Volatile.Write(ref statusText, message);
            return false;
        }
        finally
        {
            Volatile.Write(ref cleaning, 0);
        }
    }

    private static string ResolveLogPath()
    {
        var assembly = typeof(IFramework).Assembly;
        var serviceType = assembly.GetType("Dalamud.Service`1", throwOnError: true)!;
        var dalamudType = assembly.GetType("Dalamud.Dalamud", throwOnError: true)!;
        var getNullable = serviceType.MakeGenericType(dalamudType).GetMethod("GetNullable", StaticFlags)
            ?? throw new NotSupportedException("Dalamud's current startup information is unavailable.");
        var parameters = getNullable.GetParameters();
        object?[] args = parameters.Length switch
        {
            0 => [],
            1 when parameters[0].ParameterType.IsEnum => [Enum.Parse(parameters[0].ParameterType, "None")],
            _ => throw new NotSupportedException("Dalamud's service lookup has changed."),
        };
        var dalamud = getNullable.Invoke(null, args)
            ?? throw new InvalidOperationException("Dalamud is starting or shutting down.");
        var info = dalamudType.GetProperty("StartInfo", InstanceFlags)?.GetValue(dalamud)
            ?? throw new NotSupportedException("Dalamud's startup information is unavailable.");
        var logPath = info.GetType().GetProperty("LogPath", InstanceFlags)?.GetValue(info) as string;
        var nameProperty = info.GetType().GetProperty("LogName", InstanceFlags)
            ?? throw new NotSupportedException("Dalamud's log name is unavailable.");
        if (nameProperty.PropertyType != typeof(string))
            throw new NotSupportedException("Dalamud's log name layout has changed.");
        var logName = nameProperty.GetValue(info) as string;
        if (string.IsNullOrWhiteSpace(logPath) || !Path.IsPathFullyQualified(logPath)
            || (!string.IsNullOrEmpty(logName) && logName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new NotSupportedException("Dalamud's active log path is invalid.");

        return Path.GetFullPath(Path.Combine(logPath, string.IsNullOrEmpty(logName) ? "dalamud.log" : $"dalamud-{logName}.log"));
    }

    private static object FindFileSink(object logger)
    {
        var pending = new Stack<object>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        object? found = null;
        pending.Push(logger);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current))
                continue;
            if (visited.Count > 32)
                throw new NotSupportedException("The logging pipeline is larger than the supported layout.");

            switch (current.GetType().FullName)
            {
                case "Serilog.Sinks.File.FileSink":
                    if (found != null)
                        throw new NotSupportedException("Multiple log files are active; cleanup cannot select one safely.");
                    found = current;
                    break;
                case "Serilog.Core.Logger":
                case "Serilog.Core.Sinks.OptionalInterfaceForwardingSink":
                case "Serilog.Sinks.File.PeriodicFlushToDiskSink":
                    pending.Push(ReadField(current, "_sink") ?? throw new NotSupportedException("The log sink layout has changed."));
                    break;
                case "Serilog.Sinks.Async.BackgroundWorkerSink":
                    pending.Push(ReadField(current, "_wrappedSink") ?? throw new NotSupportedException("The async log sink layout has changed."));
                    break;
                case "Serilog.Core.Sinks.SafeAggregateSink":
                case "Serilog.Core.Sinks.AggregateSink":
                    if (ReadField(current, "_sinks") is not Array sinks || sinks.Length > 32)
                        throw new NotSupportedException("The aggregate log sink layout has changed.");
                    foreach (var child in sinks)
                        if (child != null)
                            pending.Push(child);
                    break;
            }
        }

        return found ?? throw new NotSupportedException("No supported active Dalamud file writer was found.");
    }

    private static object? ReadField(object instance, string name)
        => instance.GetType().GetField(name, InstanceFlags)?.GetValue(instance);

    private static void ArchiveRecentOutput(string logPath, long logLength)
    {
        var oldPath = Path.ChangeExtension(logPath, "old.log");
        var tempPath = oldPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var archive = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var currentBytes = (int)Math.Min(logLength, ArchiveLimit);
                if (currentBytes < ArchiveLimit)
                {
                    try
                    {
                        using var old = new FileStream(oldPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        CopyTail(old, archive, ArchiveLimit - currentBytes);
                    }
                    catch (FileNotFoundException)
                    {
                        // First cleanup: there is no older archive to retain.
                    }
                }

                using (var current = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (current.Length != logLength)
                        throw new IOException("The active log changed while preparing its archive.");
                    CopyTail(current, archive, currentBytes);
                }
                archive.Flush(flushToDisk: true);
            }

            File.Move(tempPath, oldPath, overwrite: true);
        }
        finally
        {
            // Never remove either log on an archive failure. Only our own uncommitted temporary file is disposable.
            try { File.Delete(tempPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void CopyTail(Stream source, Stream destination, int maximumBytes)
    {
        var remaining = (int)Math.Min(source.Length, maximumBytes);
        source.Seek(-remaining, SeekOrigin.End);
        var buffer = new byte[64 * 1024];
        while (remaining > 0)
        {
            var read = source.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read == 0)
                throw new EndOfStreamException("A log changed while its archive was being prepared.");
            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }
}
