using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

namespace XASlave.Services;

// Copies only a previously admitted preview image. No game objects, native
// animation pointers or world state are retained by a recorded clip.
internal sealed class ViewSingleItemAnimationClip : IDisposable
{
    private const long MaximumBytes = 96L * 1024 * 1024;
    private const int MaximumFrames = 240;
    private const float MaximumSeconds = 30;
    private const double TargetInterval = 1.0 / 30;
    private readonly object sync = new();
    private readonly List<Frame> frames = new();
    private readonly Stopwatch recordClock = new();
    private readonly Stopwatch playClock = new();
    private PendingCopy? pending;
    private long revision, sourceGeneration, retainedBytes;
    private bool disposed, recording, complete, playing, sourceAssigned, finishPlaying, durationCapped;
    private int width, height, frameBudget;
    private Vector2 sourceUv;
    private float duration, position, playOrigin;
    private double interval, nextSample;
    private string status = string.Empty, failure = string.Empty;

    private sealed record Frame(float Time, IDalamudTextureWrap Image, long Bytes);
    private sealed record PendingCopy(long Revision, long Generation, float Time, int Width, int Height, long Bytes,
        IDalamudTextureWrap Source, CancellationTokenSource Cancellation, Task<IDalamudTextureWrap> Task)
    {
        internal Task? Retirement { get; set; }
    }

    internal bool IsRecording { get { lock (sync) return recording; } }
    internal bool Recording => IsRecording;
    // Complete also signals the parent to pause its live clone while the final
    // asynchronous image copy, if any, finishes. TryGetImage waits for that copy.
    internal bool Complete { get { lock (sync) return complete; } }
    internal bool HasFrames { get { lock (sync) return frames.Count != 0; } }
    internal int FrameCount { get { lock (sync) return frames.Count; } }
    internal int FrameIndex { get { lock (sync) return frames.Count == 0 ? -1 : SelectedFrameCore(); } }
    internal long Revision { get { lock (sync) return revision; } }
    internal float Duration { get { lock (sync) return duration; } }
    internal string Status { get { lock (sync) return status; } }
    internal bool Playing { get { lock (sync) return recording || playing; } set => SetPlaying(value); }
    internal float Position { get { lock (sync) return position; } set => Seek(value); }

    internal void Start(float durationSeconds)
    {
        lock (sync)
        {
            if (disposed) return;
            ResetCore();
            durationCapped = float.IsFinite(durationSeconds) && durationSeconds > MaximumSeconds;
            duration = float.IsFinite(durationSeconds) && durationSeconds > 0
                ? Math.Clamp(durationSeconds, 0.1f, MaximumSeconds) : 5;
            recording = true;
            status = durationCapped ? "Recording the first 30 seconds of this longer animation..."
                : "Preparing to record one animation cycle...";
        }
    }

    internal void Tick(IDalamudTextureWrap source, Vector2 uv, long generation, Vector2 pixelSize)
    {
        lock (sync)
        {
            if (disposed) return;
            // Reject the old source before polling its successful completion.
            if (sourceAssigned && sourceGeneration != generation)
            {
                ResetCore();
                status = "Recording cleared because the preview changed.";
                return;
            }
            UpdateCore();
            if (!recording) return;
            try
            {
                if (!float.IsFinite(uv.X) || !float.IsFinite(uv.Y) || uv.X <= 0 || uv.Y <= 0 || uv.X > 1 || uv.Y > 1)
                    throw new InvalidOperationException("The preview image crop is invalid.");
                if (!float.IsFinite(pixelSize.X) || !float.IsFinite(pixelSize.Y))
                    throw new InvalidOperationException("The preview image dimensions are invalid.");
                // The borrowed wrap can describe ActualWidth/Height while its
                // SRV is allocated larger. UV crops that SRV; multiplying the
                // wrap's dimensions by UV again would shrink the image twice.
                var copyWidth = checked((int)MathF.Round(pixelSize.X));
                var copyHeight = checked((int)MathF.Round(pixelSize.Y));
                var bytes = ImageBytes(copyWidth, copyHeight);
                if (bytes <= 0 || bytes > MaximumBytes)
                    throw new InvalidOperationException("This preview image exceeds the recording memory limit.");
                if (sourceAssigned && (width != copyWidth || height != copyHeight || sourceUv != uv))
                {
                    ResetCore();
                    status = "Recording cleared because the preview size changed.";
                    return;
                }
                // A canceled copy keeps the only pending slot until it settles,
                // so rapid resets cannot accumulate unbounded GPU allocations.
                if (pending != null) return;
                if (!sourceAssigned)
                {
                    sourceAssigned = true;
                    sourceGeneration = generation;
                    width = copyWidth; height = copyHeight; sourceUv = uv;
                    frameBudget = FrameLimit(bytes);
                    interval = Math.Max(TargetInterval, duration / frameBudget);
                    recordClock.Restart();
                }
                var elapsed = recordClock.Elapsed.TotalSeconds;
                position = (float)Math.Min(elapsed, duration);
                if (elapsed >= duration) { FinishRecordingCore(true); return; }
                if (frames.Count >= frameBudget || elapsed < nextSample) return;
                if (retainedBytes + bytes > MaximumBytes)
                    throw new InvalidOperationException("The recording memory limit was reached.");

                var retainedSource = source.CreateWrapSharingLowLevelResource();
                var cancellation = new CancellationTokenSource();
                try
                {
                    var task = Plugin.TextureProvider.CreateFromExistingTextureAsync(retainedSource,
                        new TextureModificationArgs
                        {
                            DxgiFormat = 28, // R8G8B8A8_UNORM: four bytes per pixel.
                            NewWidth = copyWidth, NewHeight = copyHeight,
                            Uv0 = Vector2.Zero, Uv1 = uv,
                        }, leaveWrapOpen: true, debugName: "View Item recorded animation frame",
                        cancellationToken: cancellation.Token);
                    pending = new(revision, generation, (float)elapsed, copyWidth, copyHeight, bytes,
                        retainedSource, cancellation, task);
                    nextSample = elapsed + interval;
                    status = $"Recording: {elapsed:0.0} / {duration:0.0} seconds ({frames.Count} frames)."
                        + (durationCapped ? " The animation is longer than the 30-second recording limit." : string.Empty);
                }
                catch
                {
                    DisposeTexture(retainedSource);
                    cancellation.Dispose();
                    throw;
                }
            }
            catch (Exception error)
            {
                failure = "Recording stopped: " + error.Message;
                FinishRecordingCore(false);
                status = failure;
            }
        }
    }

    internal void Update()
    {
        lock (sync) { if (!disposed) UpdateCore(); }
    }

    private void UpdateCore()
    {
        if (pending?.Retirement is { } retirement)
        {
            if (!retirement.IsCompleted) return;
            pending = null;
        }
        if (pending is { } copy && copy.Task.IsCompleted)
        {
            pending = null;
            try
            {
                if (copy.Task.IsCompletedSuccessfully)
                {
                    // The completed-success guard makes Result nonblocking.
                    var image = copy.Task.Result;
                    if (copy.Revision != revision || copy.Generation != sourceGeneration || disposed)
                        DisposeTexture(image);
                    else if (image.Width != copy.Width || image.Height != copy.Height
                        || frames.Count >= MaximumFrames || retainedBytes + copy.Bytes > MaximumBytes)
                    {
                        DisposeTexture(image);
                        failure = "Recording stopped: the copied image exceeded its reserved size.";
                        finishPlaying = false;
                        FinishRecordingCore(false);
                    }
                    else
                    {
                        frames.Add(new(copy.Time, image, copy.Bytes));
                        retainedBytes += copy.Bytes;
                    }
                }
                else
                {
                    var error = copy.Task.Exception?.GetBaseException(); // Observe failures, including stale copies.
                    if (copy.Revision == revision && copy.Generation == sourceGeneration)
                    {
                        failure = error == null ? "Recording canceled." : "Recording stopped: " + error.Message;
                        finishPlaying = false;
                        FinishRecordingCore(false);
                    }
                }
            }
            finally
            {
                DisposeTexture(copy.Source);
                copy.Cancellation.Dispose();
            }
            if (complete) BeginFinishedPlaybackCore();
        }
        if (recording && sourceAssigned && recordClock.Elapsed.TotalSeconds >= duration)
            FinishRecordingCore(true);
        if (playing && frames.Count >= 2 && duration > 0)
            position = (float)((playOrigin + playClock.Elapsed.TotalSeconds) % duration);
    }

    internal void StopRecording()
    {
        lock (sync)
        {
            if (disposed) return;
            if (recording) FinishRecordingCore(false);
            else PausePlaybackCore();
        }
    }

    private void FinishRecordingCore(bool playAfter)
    {
        if (!recording) return;
        recording = false;
        complete = true;
        finishPlaying = playAfter;
        duration = sourceAssigned ? (float)Math.Min(duration, recordClock.Elapsed.TotalSeconds) : 0;
        recordClock.Stop();
        position = duration;
        if (pending == null) BeginFinishedPlaybackCore();
        else status = "Finishing the recorded frame...";
    }

    private void BeginFinishedPlaybackCore()
    {
        if (finishPlaying && frames.Count >= 2 && duration > 0)
        {
            playing = true;
            playOrigin = position = 0;
            playClock.Restart();
        }
        finishPlaying = false;
        if (!string.IsNullOrEmpty(failure)) status = failure + (frames.Count != 0 ? " Recorded frames remain available." : string.Empty);
        else if (frames.Count == 0) status = "No animation frames were recorded.";
        else if (frames.Count == 1) status = "One frame recorded. Play again to record an animation.";
        else status = durationCapped ? $"Recorded {duration:0.0} seconds of a longer animation ({frames.Count} frames)."
            : $"Recorded {duration:0.0} seconds ({frames.Count} frames).";
    }

    internal void SetPlaying(bool value)
    {
        lock (sync)
        {
            if (disposed) return;
            if (!value)
            {
                if (recording) FinishRecordingCore(false);
                PausePlaybackCore();
                return;
            }
            if (recording) return;
            UpdateCore();
            if (pending != null || frames.Count < 2 || duration <= 0) return;
            playOrigin = position >= duration ? 0 : position;
            playing = true;
            playClock.Restart();
        }
    }

    internal void Seek(float seconds)
    {
        lock (sync)
        {
            if (disposed || !float.IsFinite(seconds)) return;
            if (recording) FinishRecordingCore(false);
            PausePlaybackCore();
            position = Math.Clamp(seconds, 0, duration);
        }
    }

    internal void SeekFrame(int index)
    {
        lock (sync)
        {
            if (disposed) return;
            if (recording) FinishRecordingCore(false);
            PausePlaybackCore();
            if (frames.Count != 0) position = frames[Math.Clamp(index, 0, frames.Count - 1)].Time;
        }
    }

    private void PausePlaybackCore()
    {
        if (playing && duration > 0) position = (float)((playOrigin + playClock.Elapsed.TotalSeconds) % duration);
        playing = false; finishPlaying = false;
        playClock.Stop();
    }

    internal bool TryGetImage(out IDalamudTextureWrap image)
    {
        lock (sync)
        {
            image = null!;
            if (disposed) return false;
            UpdateCore();
            if (recording || pending != null || frames.Count == 0) return false;
            // Sample timestamps describe recording elapsed time, not an exact
            // native scheduler time. The selected frame's pixels stay intact.
            image = frames[SelectedFrameCore()].Image;
            return true;
        }
    }

    private int SelectedFrameCore()
    {
        var index = frames.Count - 1;
        while (index > 0 && frames[index].Time > position) index--;
        return index;
    }

    internal void Clear() => Reset();

    internal void Reset()
    {
        lock (sync) { if (!disposed) ResetCore(); }
    }

    private void ResetCore()
    {
        revision++;
        RetirePending(pending);
        foreach (var frame in frames) DisposeTexture(frame.Image);
        frames.Clear(); retainedBytes = 0;
        recordClock.Reset(); playClock.Reset();
        recording = complete = playing = sourceAssigned = finishPlaying = durationCapped = false;
        width = height = frameBudget = 0;
        sourceGeneration = 0; sourceUv = Vector2.Zero;
        duration = position = playOrigin = 0;
        interval = nextSample = 0;
        status = failure = string.Empty;
    }

    private static long ImageBytes(int imageWidth, int imageHeight)
        => imageWidth <= 0 || imageHeight <= 0 || imageWidth > 16384 || imageHeight > 16384
            ? 0 : (long)imageWidth * imageHeight * 4;

    private static int FrameLimit(long bytes)
        => bytes <= 0 || bytes > MaximumBytes ? 0 : (int)Math.Min(MaximumFrames, MaximumBytes / bytes);

    private static void RetirePending(PendingCopy? copy)
    {
        if (copy == null || copy.Retirement != null) return;
        try { copy.Cancellation.Cancel(); }
        catch (Exception) { /* Completion cleanup still owns the source and result. */ }
        // Cleanup runs even if the window is closed and Update is never called.
        // Keep this pending slot occupied until its entire retirement completes.
        copy.Retirement = DisposePendingAsync(copy);
    }

    private static void DisposeTexture(IDalamudTextureWrap? texture)
    {
        try { texture?.Dispose(); }
        catch (Exception) { /* Continue releasing the remaining owned frames. */ }
    }

    private static async Task DisposePendingAsync(PendingCopy copy)
    {
        try { DisposeTexture(await copy.Task.ConfigureAwait(false)); }
        catch (Exception) { /* Observe faults and cancellation from a retired copy. */ }
        finally
        {
            DisposeTexture(copy.Source);
            copy.Cancellation.Dispose();
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            ResetCore();
            disposed = true;
            pending = null;
        }
    }
}
