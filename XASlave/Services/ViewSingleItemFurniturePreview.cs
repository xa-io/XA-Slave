using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

namespace XASlave.Services;

// One bounded worker owns the detached asset read/raster/upload operation. All
// publication and retirement goes through sync, including late cancellation.
internal sealed class ViewSingleItemFurniturePreview : IDisposable
{
    private readonly object sync = new();
    private ViewSingleItemFurnitureAsset? asset;
    private ViewSingleItemFurnitureModel? model;
    private IDalamudTextureWrap? image;
    private Pending? pending;
    private bool disposed, dirty;
    private long generation, cameraRevision;
    private int width = 640, height = 480;
    private float yaw = 0.45f, pitch = -0.25f, roll, zoom = 1;
    private Vector2 pan;
    private string status = global::XASlave.Localization.UiLocale.Native("S79A1A6EE49441B20");

    private sealed record Rendered(ViewSingleItemFurnitureModel Model, IDalamudTextureWrap Image);
    private sealed record Pending(long Generation, long CameraRevision, CancellationTokenSource Cancellation, Task<Rendered> Task);

    private global::XASlave.Localization.UiMessage? statusMessage = global::XASlave.Localization.UiMessage.Key("S79A1A6EE49441B20");
    internal global::XASlave.Localization.UiMessage StatusMessage { get { lock (sync) return global::XASlave.Localization.UiMessage.Capture(statusMessage, status); } }
    internal string LocalizedStatus => StatusMessage.Display;
    internal string Status { get { lock (sync) return status; } }

    internal void Load(ViewSingleItemFurnitureAsset value)
    {
        lock (sync)
        {
            if (disposed) return;
            ClearCore();
            asset = value;
            ResetCameraCore();
            status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Key("SA42719B747241482"));
        }
    }

    internal void Tick()
    {
        lock (sync)
        {
            if (disposed) return;
            if (pending is { } completed && completed.Task.IsCompleted)
            {
                pending = null;
                completed.Cancellation.Dispose();
                if (completed.Task.IsCompletedSuccessfully)
                {
                    var result = completed.Task.Result;
                    if (completed.Generation != generation || asset == null) result.Image.Dispose();
                    else
                    {
                        model = result.Model;
                        image?.Dispose();
                        image = result.Image;
                        dirty = completed.CameraRevision != cameraRevision;
                        status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, (string.IsNullOrEmpty(model.Notes) ? global::XASlave.Localization.UiMessage.Key("S2A23CDBB9FA97CCE") : model.NotesPresentation));
                    }
                }
                else
                {
                    // Observe faults even when a previous selection was canceled.
                    var error = completed.Task.Exception?.GetBaseException();
                    if (completed.Generation == generation && asset != null && error != null)
                    {
                        // Preserve an input made while an older view failed;
                        // unchanged failed views remain stopped until new input.
                        dirty = completed.CameraRevision != cameraRevision;
                        status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Format("SC23F340A1A7167D4" , global::XASlave.Localization.UiMessage.FromException(error)));
                        Plugin.Log.Warning(error, "[View Item] Furniture preview failed for item {ItemId}.", asset.ItemId);
                    }
                }
            }
            if (asset == null || pending != null || !dirty) return;
            var selected = asset;
            var loaded = model;
            var renderYaw = yaw;
            var renderPitch = pitch;
            var renderRoll = roll;
            var renderZoom = zoom;
            var renderPan = pan;
            var renderWidth = width;
            var renderHeight = height;
            var textures = Plugin.TextureProvider;
            var cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            var job = Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                var geometry = loaded ?? ViewSingleItemFurnitureAssets.Load(selected, token);
                var pixels = ViewSingleItemFurnitureRasterizer.Render(geometry, renderWidth, renderHeight,
                    renderYaw, renderPitch, renderRoll, renderZoom, renderPan, token);
                token.ThrowIfCancellationRequested();
                var texture = await textures.CreateFromRawAsync(RawImageSpecification.Bgra32(renderWidth, renderHeight),
                    pixels, debugName: "View Item furnishing", cancellationToken: token).ConfigureAwait(false);
                // Cancellation can win just after the provider created a wrap.
                if (token.IsCancellationRequested)
                {
                    texture.Dispose();
                    token.ThrowIfCancellationRequested();
                }
                return new Rendered(geometry, texture);
            }, token);
            pending = new(generation, cameraRevision, cancellation, job);
            dirty = false;
        }
    }

    internal bool TryGetImage(out IDalamudTextureWrap value)
    {
        lock (sync)
        {
            value = image!;
            return !disposed && image != null;
        }
    }

    internal void SetViewport(Vector2 size)
    {
        if (!float.IsFinite(size.X) || !float.IsFinite(size.Y) || size.X < 1 || size.Y < 1) return;
        // Keep the canvas aspect while bounding each generated image to 768 px.
        var scale = Math.Min(1, 768 / Math.Max(size.X, size.Y));
        var nextWidth = Math.Clamp((int)MathF.Round(size.X * scale), 1, 768);
        var nextHeight = Math.Clamp((int)MathF.Round(size.Y * scale), 1, 768);
        lock (sync)
        {
            if (disposed || (width == nextWidth && height == nextHeight)) return;
            width = nextWidth; height = nextHeight;
            cameraRevision++; dirty = true;
        }
    }

    internal void Camera(float yawDelta, float pitchDelta, float zoomDelta, float panX, float panY, float rollDelta = 0)
    {
        if (!float.IsFinite(yawDelta) || !float.IsFinite(pitchDelta) || !float.IsFinite(zoomDelta)
            || !float.IsFinite(panX) || !float.IsFinite(panY) || !float.IsFinite(rollDelta)
            || (yawDelta == 0 && pitchDelta == 0 && zoomDelta == 0 && panX == 0 && panY == 0 && rollDelta == 0)) return;
        lock (sync)
        {
            if (disposed || asset == null) return;
            yaw = MathF.IEEERemainder(yaw + yawDelta, MathF.Tau);
            pitch = MathF.IEEERemainder(pitch + pitchDelta, MathF.Tau);
            roll = MathF.IEEERemainder(roll + rollDelta, MathF.Tau);
            zoom = Math.Clamp(zoom * MathF.Exp(-Math.Clamp(zoomDelta, -20, 20) * 0.15f), 0.1f, 16);
            pan = Vector2.Clamp(pan + new Vector2(panX, panY), new Vector2(-4), new Vector2(4));
            cameraRevision++; dirty = true;
        }
    }

    internal void ResetCamera() { lock (sync) { if (!disposed) ResetCameraCore(); } }
    private void ResetCameraCore()
    {
        yaw = 0.45f; pitch = -0.25f; roll = 0; zoom = 1; pan = Vector2.Zero;
        cameraRevision++; dirty = true;
    }

    internal void Clear() { lock (sync) { ClearCore(); } }
    private void ClearCore()
    {
        generation++;
        pending?.Cancellation.Cancel();
        if (pending is { } canceled) _ = RetireCanceledAsync(canceled);
        // Keep the worker slot until it settles; repeated item clicks cannot
        // accumulate independent loaders, pixel buffers or texture uploads.
        asset = null; model = null; dirty = false;
        image?.Dispose(); image = null;
        status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Key("S79A1A6EE49441B20"));
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            ClearCore();
        }
    }

    private async Task RetireCanceledAsync(Pending abandoned)
    {
        try { await abandoned.Task.ConfigureAwait(false); }
        catch (Exception) { /* A canceled/failed worker owns no image. */ }
        lock (sync)
        {
            if (!ReferenceEquals(pending, abandoned)) return;
            pending = null;
            if (abandoned.Task.IsCompletedSuccessfully) abandoned.Task.Result.Image.Dispose();
            abandoned.Cancellation.Dispose();
        }
    }
}
