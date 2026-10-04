using System;
using System.Numerics;
using Dalamud.Interface.Textures.TextureWraps;

namespace XASlave.Services;

internal sealed unsafe partial class ViewSingleItemPreview
{
    private readonly ViewSingleItemAnimationClip animationClip = new();
    private string animationClipKind = "";
    private long animationRenderRevision = -1;
    private long animationControlRevision = -1;
    private string animationRecordingNotice = "";

    internal ViewSingleItemAnimationState Animation
    {
        get
        {
            var live = scene.Animation;
            return new(live.Ready, animationClip.IsRecording || animationClip.Playing,
                animationClip.Position, animationClip.Complete ? animationClip.Duration : 0,
                animationClip.Revision, animationClip.IsRecording ? "Recording " + animationClipKind + ". " + animationClip.Status
                    : !string.IsNullOrEmpty(animationClip.Status) ? animationClip.Status
                    : !string.IsNullOrEmpty(animationRecordingNotice) ? animationRecordingNotice
                    : !live.Ready ? live.Status : "Play to record a loop with its glow.",
                animationClip.Complete ? animationClip.FrameCount : 0, animationClip.FrameIndex);
        }
    }

    internal void SetAnimationPlaying(bool playing)
    {
        Validate();
        if (source == null || selection == null || selection is 105 or 106) return;
        lock (scene.SyncRoot)
        {
            DrainSceneImageRetirement();
            ValidateRecordedAnimation();
            var live = scene.Animation;
            if (!live.Ready) return;
            if (!playing)
            {
                scene.SetAnimationPlaying(false);
                animationClip.StopRecording();
                animationClip.SetPlaying(false);
            }
            else if (animationClip.Complete && animationClip.FrameCount >= 2)
            {
                scene.SetAnimationPlaying(false);
                animationClip.SetPlaying(true);
            }
            else
            {
                // Capture actual rendered frames: LocalTime alone cannot rewind
                // particle randomness, material clocks or scheduler effects.
                animationClipKind = live.Duration > 0 ? "one animation cycle" : "a five-second effect clip";
                animationRenderRevision = -1;
                animationControlRevision = live.Revision;
                animationRecordingNotice = "";
                animationClip.Start(live.Duration > 0 ? live.Duration : 5);
                scene.SetAnimationPlaying(true);
            }
        }
    }

    internal void SeekAnimationFrame(int frameIndex, long timelineRevision)
    {
        Validate();
        if (source == null || selection == null || selection is 105 or 106) return;
        lock (scene.SyncRoot)
        {
            DrainSceneImageRetirement();
            ValidateRecordedAnimation();
            if (timelineRevision != animationClip.Revision || !animationClip.Complete) return;
            scene.SetAnimationPlaying(false);
            animationClip.SeekFrame(frameIndex);
        }
    }

    private void ResetRecordedAnimation()
    {
        animationClip.Reset();
        animationRenderRevision = -1;
        animationControlRevision = -1;
        animationRecordingNotice = "";
        scene.SetAnimationPlaying(false);
    }

    private void UpdateRecordedAnimation()
    {
        ValidateRecordedAnimation();
        var recording = animationClip.IsRecording;
        if (recording && !scene.Animation.Ready) animationClip.StopRecording();
        animationClip.Update();
        if (recording && !animationClip.IsRecording) scene.SetAnimationPlaying(false);
    }

    private void ValidateRecordedAnimation()
    {
        if (animationControlRevision < 0 || animationControlRevision == scene.Animation.Revision) return;
        ResetRecordedAnimation();
        animationRecordingNotice = "The animation changed. Play to record its new cycle.";
    }

    internal Vector2 DisplayPixelSize { get; private set; }

    internal bool TryGetDisplayImage(out IDalamudTextureWrap image, out Vector2 uv)
    {
        // Keep all existing live identity/filter/image admission checks before
        // returning either a live frame or a recorded frame from this selection.
        uv = Vector2.One;
        lock (scene.SyncRoot)
        {
            if (!TryGetImage(out image, out var pixelSize)) return false;
            DisplayPixelSize = pixelSize;
            ValidateRecordedAnimation();
            if (animationRenderRevision >= 0 && animationRenderRevision != revision)
                ResetRecordedAnimation();
            if (animationClip.IsRecording && animationRenderRevision < 0) animationRenderRevision = revision;
            var recording = animationClip.IsRecording;
            animationClip.Update();
            animationClip.Tick(image, ImageUvMax, revision, pixelSize);
            if (recording && !animationClip.IsRecording) scene.SetAnimationPlaying(false);
            if (animationClip.TryGetImage(out var recorded)) image = recorded;
            else uv = ImageUvMax;
            return true;
        }
    }
}
