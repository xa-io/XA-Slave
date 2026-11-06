using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XASlave.Localization;

namespace XASlave.Services;

/// <summary>Copies a finite snapshot of this client's active log without changing its file or logger.</summary>
public sealed class DalamudLogExportService : IDisposable
{
    internal const int CopyBufferSize = 64 * 1024;
    private readonly string exportDirectory;
    private readonly object stateGate = new();
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;
    private int exporting;
    private int openingFolder;
    private UiMessage statusMessage = UiMessage.Authored("Ready to export this client's active Dalamud log.");

    public DalamudLogExportService(string exportDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportDirectory);
        this.exportDirectory = Path.GetFullPath(exportDirectory);
    }

    public bool IsExporting => Volatile.Read(ref exporting) != 0;
    internal UiMessage StatusMessage => Volatile.Read(ref statusMessage);

    internal bool TryExecute(string args, out UiMessage message)
    {
        var argument = args?.Trim() ?? string.Empty;
        var openFolder = argument.Equals("folder", StringComparison.OrdinalIgnoreCase);
        if (argument.Length != 0 && !openFolder)
        {
            message = UiMessage.Authored("Usage: /xa devlogs [folder]");
            return false;
        }

        lock (stateGate)
        {
            if (disposed)
            {
                message = UiMessage.Authored("Log export is unavailable while XA Slave is shutting down.");
                return false;
            }
            if (openFolder ? openingFolder != 0 : exporting != 0)
            {
                message = openFolder
                    ? UiMessage.Authored("The exports folder is already opening.")
                    : UiMessage.Authored("A Dalamud log export is already running.");
                return false;
            }

            var cancellationToken = lifetime.Token;
            message = openFolder
                ? UiMessage.Authored("Opening the Dalamud log exports folder.")
                : UiMessage.Authored("Exporting the current Dalamud log. Use /xa devlogs folder to open exports.");
            Volatile.Write(ref statusMessage, message);
            if (openFolder) Volatile.Write(ref openingFolder, 1);
            else Volatile.Write(ref exporting, 1);
            try
            {
                // Resolve, open, copy, flush and launch Explorer only on a worker, never on the framework thread.
                if (openFolder) _ = Task.Run(() => OpenFolderAsync(cancellationToken));
                else _ = Task.Run(() => ExportAsync(cancellationToken));
                return true;
            }
            catch (Exception ex)
            {
                if (openFolder) Volatile.Write(ref openingFolder, 0);
                else Volatile.Write(ref exporting, 0);
                message = FailureMessage(openFolder, ex);
                Volatile.Write(ref statusMessage, message);
                return false;
            }
        }
    }

    private async Task ExportAsync(CancellationToken cancellationToken)
    {
        string? partialPath = null;
        var ownsPartial = false;
        var published = false;
        try
        {
            ThrowIfStopped(cancellationToken);
            var sourcePath = DalamudLogCleanerService.ResolveLogPath();
            string finalPath;
            long copied;
            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete, CopyBufferSize, FileOptions.SequentialScan))
            {
                // Appended bytes belong to a later export. Keep the original handle across log rotation.
                var initialLength = source.Length;
                ThrowIfStopped(cancellationToken);
                Directory.CreateDirectory(exportDirectory);
                var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
                var fileName = $"dalamud-{stamp}Z-p{Environment.ProcessId}-{Guid.NewGuid():N}.log";
                finalPath = Path.Combine(exportDirectory, fileName);
                partialPath = finalPath + ".partial";
                using (var destination = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, CopyBufferSize, FileOptions.SequentialScan))
                {
                    ownsPartial = true;
                    copied = CopySnapshot(source, destination, initialLength, cancellationToken);
                    destination.Flush(flushToDisk: true);
                }
            }

            // Disposal never waits for filesystem I/O. A move already entering the OS may finish,
            // but cancellation prevents its queued notification and never removes a completed export.
            ThrowIfStopped(cancellationToken);
            File.Move(partialPath, finalPath, overwrite: false);
            ownsPartial = false;
            published = true;
            ThrowIfStopped(cancellationToken);
            Volatile.Write(ref statusMessage, UiMessage.Format(
                UiLocale.Key("Saved {0:N0} bytes to {1}. Use /xa devlogs folder to open exports."), copied, finalPath));
            await NotifyCompletionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || Volatile.Read(ref disposed))
        {
            // Only the unpublished partial is ours to remove. A completed file remains available.
        }
        catch (Exception ex)
        {
            if (published)
                LogNotificationFailure(ex);
            else
                await ReportFailureAsync(FailureMessage(false, ex), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (ownsPartial && partialPath != null)
            {
                try { File.Delete(partialPath); }
                catch (Exception ex) { LogNotificationFailure(ex); }
            }
            Volatile.Write(ref exporting, 0);
        }
    }

    /// <summary>Reads exactly the opened file's initial byte count with bounded memory; never follows appended data.</summary>
    internal static long CopySnapshot(Stream source, Stream destination, long initialLength, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(initialLength);
        var buffer = new byte[CopyBufferSize];
        var remaining = initialLength;
        long copied = 0;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = (int)Math.Min(buffer.Length, remaining);
            var read = source.Read(buffer, 0, requested);
            if (read == 0)
                throw UiMessage.CreateException(UiMessage.Authored("The active log ended before its initial byte count was copied."),
                    text => new EndOfStreamException(text));
            destination.Write(buffer, 0, read);
            copied += read;
            remaining -= read;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return copied;
    }

    private async Task OpenFolderAsync(CancellationToken cancellationToken)
    {
        try
        {
            ThrowIfStopped(cancellationToken);
            Directory.CreateDirectory(exportDirectory);
            ThrowIfStopped(cancellationToken);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exportDirectory,
                UseShellExecute = true,
            });
            ThrowIfStopped(cancellationToken);
            Volatile.Write(ref statusMessage, UiMessage.Authored("Opened the Dalamud log exports folder."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || Volatile.Read(ref disposed)) { }
        catch (Exception ex)
        {
            await ReportFailureAsync(FailureMessage(true, ex), cancellationToken).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref openingFolder, 0); }
    }

    private async Task NotifyCompletionAsync(CancellationToken cancellationToken)
    {
        try
        {
            ThrowIfStopped(cancellationToken);
            await Plugin.RunOnGameThread(() =>
            {
                if (!CanNotify(cancellationToken)) return;
                var completion = UiMessage.Authored("Exported Dalamud log. Use /xa devlogs folder to open exports.");
                var notificationFailed = false;
                // Keep native chat/toasts English; the plugin-window status uses the selected UI language.
                try { notificationFailed = !ChatHelper.TrySend("/e " + completion.Original); }
                catch (Exception ex) { notificationFailed = true; LogNotificationFailure(ex); }
                if (!CanNotify(cancellationToken)) return;
                try { Plugin.ToastGui.ShowNormal(completion.Original); }
                catch (Exception ex) { notificationFailed = true; LogNotificationFailure(ex); }
                if (notificationFailed && CanNotify(cancellationToken))
                    Plugin.ChatGui.PrintError(UiMessage.Authored("Dalamud log was saved, but a completion notification could not be shown. Use /xa devlogs folder.").Original);
            }).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || Volatile.Read(ref disposed)) { }
        catch (Exception ex) { LogNotificationFailure(ex); }
    }

    private async Task ReportFailureAsync(UiMessage failure, CancellationToken cancellationToken)
    {
        try
        {
            ThrowIfStopped(cancellationToken);
            Volatile.Write(ref statusMessage, failure);
            await Plugin.RunOnGameThread(() =>
            {
                if (CanNotify(cancellationToken)) Plugin.ChatGui.PrintError(failure.Original);
            }).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || Volatile.Read(ref disposed)) { }
        catch (Exception ex) { LogNotificationFailure(ex); }
    }

    private static UiMessage FailureMessage(bool opening, Exception exception)
        => UiMessage.Format(UiLocale.Key(opening
                ? "Could not open the log exports folder: {0}"
                : "Could not export the Dalamud log: {0}"),
            UiMessage.FromException(exception.GetBaseException()));

    private bool CanNotify(CancellationToken cancellationToken)
        => !Volatile.Read(ref disposed) && !cancellationToken.IsCancellationRequested;

    private void ThrowIfStopped(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref disposed)) throw new OperationCanceledException(cancellationToken);
    }

    private static void LogNotificationFailure(Exception exception)
    {
        try { Plugin.Log.Warning(exception, "[XASlave] Dalamud log export notification or partial cleanup failed."); }
        catch { /* Logging can be unavailable during plugin teardown; never retry game notifications. */ }
    }

    public void Dispose()
    {
        lock (stateGate)
        {
            if (disposed) return;
            Volatile.Write(ref disposed, true);
        }
        lifetime.Cancel();
        lifetime.Dispose();
    }
}
