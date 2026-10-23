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
    private global::XASlave.Localization.UiMessage? statusMessage = global::XASlave.Localization.UiMessage.Key("S8473DC4901505577");
    private string statusText = global::XASlave.Localization.UiLocale.Native("S8473DC4901505577");
    private global::XASlave.Localization.UiMessage? sizeMessage = global::XASlave.Localization.UiMessage.Key("SA7B3F3A064EBA40D");
    private string sizeStatusText = global::XASlave.Localization.UiLocale.Native("SA7B3F3A064EBA40D");
    private readonly object automaticGate = new();
    private Timer? automaticTimer;
    private bool automaticEnabled;
    private bool disposed;
    private int checkIntervalMinutes = DefaultCheckIntervalMinutes;
    private long nextAutomaticCheckTick;
    private global::XASlave.Localization.UiMessage? automaticMessage = global::XASlave.Localization.UiMessage.Key("SD4875C62452086CF");
    private string automaticStatusText = global::XASlave.Localization.UiLocale.Native("SD4875C62452086CF");

    public string StatusText => Volatile.Read(ref statusText);
    internal global::XASlave.Localization.UiMessage StatusMessage => global::XASlave.Localization.UiMessage.Capture(Volatile.Read(ref statusMessage), StatusText);
    public string LocalizedStatusText => global::XASlave.Localization.UiMessage.Resolve(Volatile.Read(ref statusMessage), StatusText);
    public string SizeStatusText => Volatile.Read(ref sizeStatusText);
    internal global::XASlave.Localization.UiMessage SizeStatusMessage => global::XASlave.Localization.UiMessage.Capture(Volatile.Read(ref sizeMessage), SizeStatusText);
    public string LocalizedSizeStatusText => global::XASlave.Localization.UiMessage.Resolve(Volatile.Read(ref sizeMessage), SizeStatusText);
    public string AutomaticStatusText => Volatile.Read(ref automaticStatusText);
    internal global::XASlave.Localization.UiMessage AutomaticStatusMessage => global::XASlave.Localization.UiMessage.Capture(Volatile.Read(ref automaticMessage), AutomaticStatusText);
    public string LocalizedAutomaticStatusText => global::XASlave.Localization.UiMessage.Resolve(Volatile.Read(ref automaticMessage), AutomaticStatusText);
    public bool IsCleaning => Volatile.Read(ref cleaning) != 0;

    public bool TryCheckSize(out string message)
    {
        var success = TryCheckSizeMessage(out var presentation);
        message = presentation.Original;
        return success;
    }

    internal bool TryCheckSizeMessage(out global::XASlave.Localization.UiMessage message)
    {
        try
        {
            if (Volatile.Read(ref disposed))
                throw global::XASlave.Localization.UiMessage.Attach(new InvalidOperationException(global::XASlave.Localization.UiLocale.Native("S3A4AC4FE967FE1A5")), global::XASlave.Localization.UiMessage.Key("S3A4AC4FE967FE1A5"));

            // Metadata only: do not flush, archive, clear, or reschedule automatic cleanup.
            var file = new FileInfo(ResolveLogPath());
            var bytes = file.Length;
            message = global::XASlave.Localization.UiMessage.Format("S5970C488ED89EB61" , DateTime.Now, file.Name, bytes / (1024d * 1024d), bytes);
            Volatile.Write(ref sizeStatusText, global::XASlave.Localization.UiMessage.Store(ref sizeMessage, message));
            return true;
        }
        catch (Exception ex)
        {
            message = global::XASlave.Localization.UiMessage.Format("S004F4701B32A46E3" , global::XASlave.Localization.UiMessage.FromException(ex.GetBaseException()));
            Volatile.Write(ref sizeStatusText, global::XASlave.Localization.UiMessage.Store(ref sizeMessage, message));
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
                Volatile.Write(ref automaticStatusText, global::XASlave.Localization.UiMessage.Store(ref automaticMessage, global::XASlave.Localization.UiMessage.Key("SD4875C62452086CF")));
                return false;
            }

            automaticTimer ??= new Timer(CheckAutomatic, null, Timeout.Infinite, Timeout.Infinite);
            ScheduleNextAutomaticCheck();
            Volatile.Write(ref automaticStatusText, global::XASlave.Localization.UiMessage.Store(ref automaticMessage, global::XASlave.Localization.UiMessage.Format("S062B8458A8D2B9B2" , checkIntervalMinutes, checkIntervalMinutes)));
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
                var result = global::XASlave.Localization.UiMessage.Format("S9B67AF314D1D9AAC" , DateTime.Now, length / (1024d * 1024d));
                if (length >= AutomaticThresholdBytes)
                {
                    var success = TryCleanCore(string.Empty, AutomaticThresholdBytes, out var message);
                    result = global::XASlave.Localization.UiMessage.Format("S123BB8F31A07D428" , DateTime.Now, length / (1024d * 1024d), (success ? message : global::XASlave.Localization.UiMessage.Format("S60495CD51621FD02" , message)));
                }
                Volatile.Write(ref automaticStatusText, global::XASlave.Localization.UiMessage.Store(ref automaticMessage, result));
            }
            catch (Exception ex)
            {
                Volatile.Write(ref automaticStatusText, global::XASlave.Localization.UiMessage.Store(ref automaticMessage, global::XASlave.Localization.UiMessage.Format("S92178A74DFAD0002" , global::XASlave.Localization.UiMessage.FromException(ex.GetBaseException()))));
            }
            finally
            {
                // One-shot scheduling prevents overlapping checks and retries errors at the normal interval.
                ScheduleNextAutomaticCheck();
            }
        }
    }

    public bool TryClean(string arguments, out string message)
    {
        var success = TryCleanMessage(arguments, out var presentation);
        message = presentation.Original;
        return success;
    }

    internal bool TryCleanMessage(string arguments, out global::XASlave.Localization.UiMessage message)
        => TryCleanCore(arguments, 0, out message);

    private bool TryCleanCore(string arguments, long minimumBytes, out global::XASlave.Localization.UiMessage message)
    {
        if (Volatile.Read(ref disposed))
        {
            message = global::XASlave.Localization.UiMessage.Key("S3A4AC4FE967FE1A5");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(arguments))
        {
            message = global::XASlave.Localization.UiMessage.Key("SDAA65B877E0E4911");
            return false;
        }

        if (Interlocked.CompareExchange(ref cleaning, 1, 0) != 0)
        {
            message = global::XASlave.Localization.UiMessage.Key("S0FD13E9FF31D3942");
            return false;
        }

        var resetStarted = false;
        try
        {
            if (Environment.TickCount64 < nextAllowedTick)
            {
                message = global::XASlave.Localization.UiMessage.Key("S2903B310F5B72000");
                return false;
            }

            var expectedPath = ResolveLogPath();
            var logger = Serilog.Log.Logger;
            var sink = FindFileSink(logger);
            // This private contract is verified against the Serilog 7.0 sink shipped with Dalamud 15.0.3.5.
            // Reject other versions/layouts before flushing, archiving or changing the file.
            if (sink.GetType().Assembly.GetName().Version != new Version(7, 0, 0, 0))
                throw global::XASlave.Localization.UiMessage.Attach(new NotSupportedException(global::XASlave.Localization.UiLocale.Native("SCFB128D201B24A35")), global::XASlave.Localization.UiMessage.Key("SCFB128D201B24A35"));

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
                throw global::XASlave.Localization.UiMessage.Attach(new NotSupportedException(global::XASlave.Localization.UiLocale.Native("SEFF1215D8BF80AF3")), global::XASlave.Localization.UiMessage.Key("SEFF1215D8BF80AF3"));

            var countedLength = counted.GetType().GetProperty("CountedLength", InstanceFlags);
            if (countedLength == null || countedLength.PropertyType != typeof(long))
                throw global::XASlave.Localization.UiMessage.Attach(new NotSupportedException(global::XASlave.Localization.UiLocale.Native("SFD668BD50AAAC1FA")), global::XASlave.Localization.UiMessage.Key("SFD668BD50AAAC1FA"));

            if (!Monitor.TryEnter(syncRoot, millisecondsTimeout: 250))
                throw global::XASlave.Localization.UiMessage.Attach(new IOException(global::XASlave.Localization.UiLocale.Native("S28FA03030071F9CF")), global::XASlave.Localization.UiMessage.Key("S28FA03030071F9CF"));

            long bytesBefore;
            try
            {
                if (!ReferenceEquals(logger, Serilog.Log.Logger) || !stream.CanWrite || !stream.CanSeek)
                    throw global::XASlave.Localization.UiMessage.Attach(new IOException(global::XASlave.Localization.UiLocale.Native("S821AFFAB46B8A93D")), global::XASlave.Localization.UiMessage.Key("S821AFFAB46B8A93D"));

                output.Flush();
                bytesBefore = stream.Length;
                if (stream.Position != bytesBefore || countedLength.GetValue(counted) is not long count || count != bytesBefore)
                    throw global::XASlave.Localization.UiMessage.Attach(new NotSupportedException(global::XASlave.Localization.UiLocale.Native("S6823CB162659ACE8")), global::XASlave.Localization.UiMessage.Key("S6823CB162659ACE8"));

                // A manual cleanup may have won the race after the timer read the size. Check again under the writer lock.
                if (bytesBefore < minimumBytes)
                {
                    message = global::XASlave.Localization.UiMessage.Key("S62E185F666942F53");
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
                        throw global::XASlave.Localization.UiMessage.Attach(new IOException(global::XASlave.Localization.UiLocale.Native("SF6D8CFEAEB329C56")), global::XASlave.Localization.UiMessage.Key("SF6D8CFEAEB329C56"));
                }
            }
            finally
            {
                Monitor.Exit(syncRoot);
            }

            nextAllowedTick = Environment.TickCount64 + 5000;
            message = (bytesBefore == 0 ? global::XASlave.Localization.UiMessage.Key("SE730DD19D7571B9F") : global::XASlave.Localization.UiMessage.Format("SBCCCCDFA33A14365" , bytesBefore, Path.GetFileName(expectedPath), Path.GetFileName(Path.ChangeExtension(expectedPath, "old.log"))));
            Volatile.Write(ref statusText, global::XASlave.Localization.UiMessage.Store(ref statusMessage, message));
            return true;
        }
        catch (Exception ex)
        {
            message = (resetStarted ? global::XASlave.Localization.UiMessage.Format("S21B98CB13F1024F7" , global::XASlave.Localization.UiMessage.FromException(ex.GetBaseException())) : global::XASlave.Localization.UiMessage.Format("S901F85387F359442" , global::XASlave.Localization.UiMessage.FromException(ex.GetBaseException())));
            Volatile.Write(ref statusText, global::XASlave.Localization.UiMessage.Store(ref statusMessage, message));
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
            ?? throw global::XASlave.Localization.UiMessage.Attach(new NotSupportedException(global::XASlave.Localization.UiLocale.Native("S6FCD2FB92805C1EC")), global::XASlave.Localization.UiMessage.Key("S6FCD2FB92805C1EC"));
        var parameters = getNullable.GetParameters();
        object?[] args = parameters.Length switch
        {
            0 => [],
            1 when parameters[0].ParameterType.IsEnum => [Enum.Parse(parameters[0].ParameterType, "None")],
            _ => throw global::XASlave.Localization.UiMessage.Attach(new NotSupportedException(global::XASlave.Localization.UiLocale.Native("S4EB854E690509A1D")), global::XASlave.Localization.UiMessage.Key("S4EB854E690509A1D")),
        };
        var dalamud = getNullable.Invoke(null, args)
            ?? throw global::XASlave.Localization.UiMessage.Attach(new InvalidOperationException(global::XASlave.Localization.UiLocale.Native("S3740B925D1519816")), global::XASlave.Localization.UiMessage.Key("S3740B925D1519816"));
        var info = dalamudType.GetProperty("StartInfo", InstanceFlags)?.GetValue(dalamud)
            ?? throw global::XASlave.Localization.UiMessage.Attach(new NotSupportedException(global::XASlave.Localization.UiLocale.Native("S5C456F3F752713FD")), global::XASlave.Localization.UiMessage.Key("S5C456F3F752713FD"));
        var logPath = info.GetType().GetProperty("LogPath", InstanceFlags)?.GetValue(info) as string;
        var nameProperty = info.GetType().GetProperty("LogName", InstanceFlags)
            ?? throw global::XASlave.Localization.UiMessage.Attach(new NotSupportedException(global::XASlave.Localization.UiLocale.Native("SF539E52519A6DF85")), global::XASlave.Localization.UiMessage.Key("SF539E52519A6DF85"));
        if (nameProperty.PropertyType != typeof(string))
            throw global::XASlave.Localization.UiMessage.Attach(new NotSupportedException(global::XASlave.Localization.UiLocale.Native("S2645572ED5E1E174")), global::XASlave.Localization.UiMessage.Key("S2645572ED5E1E174"));
        var logName = nameProperty.GetValue(info) as string;
        if (string.IsNullOrWhiteSpace(logPath) || !Path.IsPathFullyQualified(logPath)
            || (!string.IsNullOrEmpty(logName) && logName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw global::XASlave.Localization.UiMessage.Attach(new NotSupportedException(global::XASlave.Localization.UiLocale.Native("SC9FEE97B2076ECA3")), global::XASlave.Localization.UiMessage.Key("SC9FEE97B2076ECA3"));

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
                throw global::XASlave.Localization.UiMessage.Attach(new NotSupportedException(global::XASlave.Localization.UiLocale.Native("SB0E0BC81C7CF9B55")), global::XASlave.Localization.UiMessage.Key("SB0E0BC81C7CF9B55"));

            switch (current.GetType().FullName)
            {
                case "Serilog.Sinks.File.FileSink":
                    if (found != null)
                        throw global::XASlave.Localization.UiMessage.Attach(new NotSupportedException(global::XASlave.Localization.UiLocale.Native("S4693D6143BC09D87")), global::XASlave.Localization.UiMessage.Key("S4693D6143BC09D87"));
                    found = current;
                    break;
                case "Serilog.Core.Logger":
                case "Serilog.Core.Sinks.OptionalInterfaceForwardingSink":
                case "Serilog.Sinks.File.PeriodicFlushToDiskSink":
                    pending.Push(ReadField(current, "_sink") ?? throw global::XASlave.Localization.UiMessage.Attach(new NotSupportedException(global::XASlave.Localization.UiLocale.Native("S27CA81CCA2126058")), global::XASlave.Localization.UiMessage.Key("S27CA81CCA2126058")));
                    break;
                case "Serilog.Sinks.Async.BackgroundWorkerSink":
                    pending.Push(ReadField(current, "_wrappedSink") ?? throw global::XASlave.Localization.UiMessage.Attach(new NotSupportedException(global::XASlave.Localization.UiLocale.Native("S01E9BF61EED37BEF")), global::XASlave.Localization.UiMessage.Key("S01E9BF61EED37BEF")));
                    break;
                case "Serilog.Core.Sinks.SafeAggregateSink":
                case "Serilog.Core.Sinks.AggregateSink":
                    if (ReadField(current, "_sinks") is not Array sinks || sinks.Length > 32)
                        throw global::XASlave.Localization.UiMessage.Attach(new NotSupportedException(global::XASlave.Localization.UiLocale.Native("SAC045B9A910A74DF")), global::XASlave.Localization.UiMessage.Key("SAC045B9A910A74DF"));
                    foreach (var child in sinks)
                        if (child != null)
                            pending.Push(child);
                    break;
            }
        }

        return found ?? throw global::XASlave.Localization.UiMessage.Attach(new NotSupportedException(global::XASlave.Localization.UiLocale.Native("SDA2EEF42C9A6E4F9")), global::XASlave.Localization.UiMessage.Key("SDA2EEF42C9A6E4F9"));
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
                        throw global::XASlave.Localization.UiMessage.Attach(new IOException(global::XASlave.Localization.UiLocale.Native("SE9B72AF210FFA56D")), global::XASlave.Localization.UiMessage.Key("SE9B72AF210FFA56D"));
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
                throw global::XASlave.Localization.UiMessage.Attach(new EndOfStreamException(global::XASlave.Localization.UiLocale.Native("S27866FAAC3CB2482")), global::XASlave.Localization.UiMessage.Key("S27866FAAC3CB2482"));
            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }
}
