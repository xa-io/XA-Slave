using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace XASlave.Services;

/// <summary>
/// Explicit manual one-shot elevation. No game reads occur on the worker: the framework
/// owner must poll with fresh loading-screen admission and observe the resulting menu.
/// Diagnostics are original-English developer evidence, not localized UI status.
/// </summary>
internal sealed class LoadingScreenEjectionAttempt : IDisposable
{
    internal enum AttemptState { AwaitingElevation, AwaitingAdmission, Resetting, Completed, Stopped, Cancelled, Failed, Uncertain }

    private const string HelperResource = "XASlave.Services.LoadingScreenEjectionHelper.ps1";
    private static int activeAttempt;
    private readonly NamedPipeServerStream pipe;
    private readonly CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(120));
    private readonly Guid attemptId = Guid.NewGuid();
    private readonly XagmanLoginTcpReset.Preparation preparation;
    private readonly string journalPath;
    private readonly string encodedCommand;
    private readonly long started = Stopwatch.GetTimestamp();
    private Admission? pendingAdmission;
    private Snapshot snapshot = new(AttemptState.AwaitingElevation, "ejection_waiting_for_elevation");
    private int mayHaveReset;
    private int disposed;
    private sealed record Snapshot(AttemptState State, string Diagnostic);

    internal AttemptState State => Volatile.Read(ref snapshot).State;
    internal bool MayHaveReset => Volatile.Read(ref mayHaveReset) != 0;
    internal bool IsFinished => State is AttemptState.Completed or AttemptState.Stopped or AttemptState.Cancelled or AttemptState.Failed or AttemptState.Uncertain;
    internal string Diagnostic => Volatile.Read(ref snapshot).Diagnostic;

    private sealed class Admission(int index)
    {
        internal int Index { get; } = index;
        internal TaskCompletionSource<long> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private LoadingScreenEjectionAttempt(string directory, XagmanLoginTcpReset.Preparation prepared, string helper)
    {
        preparation = prepared;
        journalPath = Path.Combine(directory, $"loading-screen-ejection-{attemptId:N}.jsonl");
        var pipeName = $"XASlave.LoadingScreenEjection.{attemptId:N}";
        var manifest = new
        {
            Pid = preparation.Pid,
            Created = preparation.CreationFileTime.ToString(CultureInfo.InvariantCulture),
            preparation.Image,
            Pipe = pipeName,
            Attempt = attemptId.ToString("N"),
            Expires = DateTime.UtcNow.AddSeconds(120).Ticks.ToString(CultureInfo.InvariantCulture),
            Rows = preparation.Connections.Select(row => new[]
                { row.State, row.LocalAddress, row.LocalPort, row.RemoteAddress, row.RemotePort, row.OwningPid }).ToArray(),
        };
        // Only fixed embedded source is executable. All variable data is JSON inside base64;
        // paths, process identity and tuples are never interpolated as PowerShell code.
        var data = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(manifest));
        var command = "$m=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + data + "'))|ConvertFrom-Json\n" + helper;
        encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        if (encodedCommand.Length > 29000)
            throw new InvalidOperationException("The embedded helper exceeds the bounded Windows command line");
        pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance, 4096, 4096);
    }

    internal static bool TryStart(string journalDirectory, out LoadingScreenEjectionAttempt? attempt, out string diagnostic)
    {
        attempt = null;
        diagnostic = "ejection_busy: another one-shot helper is still active";
        if (Interlocked.CompareExchange(ref activeAttempt, 1, 0) != 0)
            return false;
        try
        {
            if (string.IsNullOrWhiteSpace(journalDirectory) || !Path.IsPathFullyQualified(journalDirectory))
                throw new InvalidOperationException("A fully qualified local journal directory is required");
            var directory = Path.GetFullPath(journalDirectory);
            if (directory.StartsWith(@"\\", StringComparison.Ordinal))
                throw new InvalidOperationException("Network and device journal paths are not accepted");
            if (!XagmanLoginTcpReset.TryPrepareCurrentProcess(out var prepared, out diagnostic) || prepared == null)
            {
                Volatile.Write(ref activeAttempt, 0);
                return false;
            }
            using var resource = typeof(LoadingScreenEjectionAttempt).Assembly.GetManifestResourceStream(HelperResource)
                ?? throw new InvalidOperationException("The fixed embedded ejection helper is missing");
            using var reader = new StreamReader(resource, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var helper = reader.ReadToEnd();
            if (helper.Length is < 100 or > 10000)
                throw new InvalidOperationException("The embedded ejection helper has invalid bounds");
            Directory.CreateDirectory(directory);
            var created = new LoadingScreenEjectionAttempt(directory, prepared, helper);
            attempt = created;
            _ = Task.Run(created.RunAsync);
            diagnostic = created.Diagnostic;
            return true;
        }
        catch (Exception error)
        {
            Volatile.Write(ref activeAttempt, 0);
            diagnostic = $"ejection_not_started: {error.GetType().Name}: {error.Message}";
            return false;
        }
    }

    /// <summary>Call on the framework thread only, with fresh game-state admission.</summary>
    internal AttemptState Poll(bool admissionValid, Action beforeReset)
    {
        if (IsFinished)
            return State;
        if (Volatile.Read(ref disposed) != 0 || cancellation.IsCancellationRequested
            || Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(120))
        {
            Finish(MayHaveReset ? AttemptState.Uncertain : AttemptState.Cancelled, "ejection_expired_or_disposed");
            Cancel();
            return State;
        }
        if (!admissionValid)
        {
            Finish(AttemptState.Stopped, "ejection_context_changed: no further connection resets authorized");
            Cancel();
            return State;
        }
        var admission = Interlocked.Exchange(ref pendingAdmission, null);
        if (admission == null)
            return State;
        try
        {
            // The worker has already durably flushed this tuple's dispatch intent. It cannot
            // send GO until this fresh framework decision; no game object crosses threads.
            if (!MayHaveReset)
                beforeReset();
            if (IsFinished || cancellation.IsCancellationRequested)
            {
                admission.Decision.TrySetCanceled();
                return State;
            }
            Volatile.Write(ref mayHaveReset, 1); // Conservative: from GO onward the outcome can be uncertain.
            SetState(AttemptState.Resetting, "ejection_reset_authorized: awaiting the one-shot helper result");
            admission.Decision.TrySetResult(DateTime.UtcNow.AddSeconds(1).Ticks);
        }
        catch (Exception error)
        {
            admission.Decision.TrySetException(error);
            Finish(MayHaveReset ? AttemptState.Uncertain : AttemptState.Failed, $"ejection_admission_failed: {error.GetType().Name}: {error.Message}");
            Cancel();
        }
        return State;
    }

    private async Task RunAsync()
    {
        try
        {
            using var journal = new FileStream(journalPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                4096, FileOptions.WriteThrough);
            WriteRecord(journal, new { State = "prepared", AtUtc = DateTimeOffset.UtcNow, AttemptId = attemptId,
                Schema = "xaslave.manual-loading-ejection/v1", Process = preparation });
            cancellation.Token.ThrowIfCancellationRequested();
            // ShellExecute/UAC is deliberately off the framework thread. There is no script
            // file to substitute, generic command channel, persistent broker or target picker.
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            using var helper = Process.Start(new ProcessStartInfo
            {
                FileName = powershell,
                Arguments = "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + encodedCommand,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System),
            }) ?? throw new InvalidOperationException("Windows did not return the one-shot helper process");
            // Accessing Handle pins this exact child; it is retained until the pipe is closed.
            _ = helper.Handle;
            cancellation.Token.ThrowIfCancellationRequested();
            await pipe.WaitForConnectionAsync(cancellation.Token).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid)
                || clientPid != checked((uint)helper.Id) || helper.HasExited)
                throw new InvalidOperationException("The pipe peer is not the exact elevated helper");
            if (await ReadLineAsync() != "HELLO|" + attemptId.ToString("N"))
                throw new InvalidOperationException("The helper attempt identity did not match");
            await SendLineAsync("OK");
            for (var index = 0; index < preparation.Connections.Length; index++)
            {
                var line = await ReadLineAsync();
                if (line == "ABSENT|" + index.ToString(CultureInfo.InvariantCulture))
                {
                    WriteRecord(journal, new { State = "already_absent", AtUtc = DateTimeOffset.UtcNow, Index = index });
                    continue;
                }
                if (line != "READY|" + index.ToString(CultureInfo.InvariantCulture))
                    throw new InvalidOperationException("Unexpected helper readiness response: " + line);
                WriteRecord(journal, new { State = "dispatch_intent", AtUtc = DateTimeOffset.UtcNow, Index = index,
                    Connection = preparation.Connections[index] });
                cancellation.Token.ThrowIfCancellationRequested();
                var admission = new Admission(index);
                SetState(AttemptState.AwaitingAdmission, "ejection_waiting_for_fresh_framework_admission");
                Volatile.Write(ref pendingAdmission, admission);
                var expires = await admission.Decision.Task.WaitAsync(cancellation.Token).ConfigureAwait(false);
                cancellation.Token.ThrowIfCancellationRequested();
                if (DateTime.UtcNow.Ticks >= expires)
                    throw new TimeoutException("Framework authorization expired before GO was sent");
                await SendLineAsync("GO|" + index.ToString(CultureInfo.InvariantCulture) + "|" + expires.ToString(CultureInfo.InvariantCulture));
                line = await ReadLineAsync();
                WriteRecord(journal, new { State = "helper_result", AtUtc = DateTimeOffset.UtcNow, Index = index, Response = line });
                if (line == "ABSENT|" + index.ToString(CultureInfo.InvariantCulture))
                    continue;
                if (line != "RESULT|" + index.ToString(CultureInfo.InvariantCulture) + "|0")
                    throw new InvalidOperationException("The helper did not confirm a successful reset: " + line);
            }
            if (await ReadLineAsync() != "DONE")
                throw new InvalidOperationException("The helper did not finish its bounded attempt");
            WriteRecord(journal, new { State = "completed", AtUtc = DateTimeOffset.UtcNow,
                GameEffect = "unverified: independently observe a usable menu before resuming" });
            Finish(AttemptState.Completed, "ejection_transport_completed: game recovery remains unverified");
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223 && !MayHaveReset)
        {
            Finish(AttemptState.Cancelled, "ejection_uac_cancelled: no connection reset was authorized");
        }
        catch (OperationCanceledException)
        {
            Finish(MayHaveReset ? AttemptState.Uncertain : AttemptState.Cancelled, "ejection_cancelled_or_expired: no further resets will be authorized");
        }
        catch (Exception error)
        {
            Finish(MayHaveReset ? AttemptState.Uncertain : AttemptState.Failed, $"ejection_failed: {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref pendingAdmission, null)?.Decision.TrySetCanceled();
            // Closing the channel revokes the one-shot helper; its own hard lifetime also
            // handles a dead plugin/game and an unanswered UAC prompt. Never kill the game.
            pipe.Dispose();
            cancellation.Dispose();
            Volatile.Write(ref activeAttempt, 0);
        }
    }

    private async Task<string> ReadLineAsync()
    {
        var bytes = new byte[1];
        var line = new StringBuilder();
        while (line.Length < 160)
        {
            if (await pipe.ReadAsync(bytes.AsMemory(), cancellation.Token).ConfigureAwait(false) != 1)
                throw new EndOfStreamException("The helper pipe closed");
            if (bytes[0] == (byte)'\n')
                return line.ToString();
            if (bytes[0] < 32 || bytes[0] > 126)
                throw new InvalidDataException("The helper sent an invalid protocol byte");
            line.Append((char)bytes[0]);
        }
        throw new InvalidDataException("The helper response exceeded its bounds");
    }

    private async Task SendLineAsync(string line)
    {
        await pipe.WriteAsync(Encoding.ASCII.GetBytes(line + "\n").AsMemory(), cancellation.Token).ConfigureAwait(false);
        await pipe.FlushAsync(cancellation.Token).ConfigureAwait(false);
    }

    private static void WriteRecord(FileStream journal, object record)
    {
        journal.Write(JsonSerializer.SerializeToUtf8Bytes(record));
        journal.WriteByte((byte)'\n');
        journal.Flush(flushToDisk: true);
    }

    private void SetState(AttemptState value, string text)
    {
        while (true)
        {
            var current = Volatile.Read(ref snapshot);
            if (current.State is AttemptState.Completed or AttemptState.Stopped or AttemptState.Cancelled or AttemptState.Failed or AttemptState.Uncertain)
                return;
            var next = new Snapshot(value, text);
            if (ReferenceEquals(Interlocked.CompareExchange(ref snapshot, next, current), current))
                return;
        }
    }

    private void Finish(AttemptState value, string text) => SetState(value, text);

    private void Cancel()
    {
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { /* The worker already revoked and disposed its channel. */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        Finish(MayHaveReset ? AttemptState.Uncertain : AttemptState.Cancelled, "ejection_disposed: channel revoked");
        Cancel();
        Interlocked.Exchange(ref pendingAdmission, null)?.Decision.TrySetCanceled();
        pipe.Dispose();
        // Do not wait for UAC or child shutdown on the framework thread. The worker owns the
        // pinned child and clears activeAttempt only after its final channel cleanup.
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
