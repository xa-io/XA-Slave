using System;
using System.Numerics;
using Dalamud.Interface.Textures.TextureWraps;

namespace XASlave.Services;

internal sealed unsafe partial class ViewSingleItemPreview
{
    private readonly ViewSingleItemAnimationClip animationClip = new();
    private global::XASlave.Localization.UiMessage? animationClipKindMessage;
    private string animationClipKind = "";
    private long animationRenderRevision = -1;
    private long animationControlRevision = -1;
    private global::XASlave.Localization.UiMessage? animationRecordingNoticeMessage;
    private string animationRecordingNotice = "";

    internal ViewSingleItemAnimationState Animation
    {
        get
        {
            var live = scene.Animation;
            return ViewSingleItemAnimationState.Create(live.Ready, animationClip.IsRecording || animationClip.Playing, animationClip.Position, animationClip.Complete ? animationClip.Duration : 0, animationClip.Revision, (animationClip.IsRecording ? global::XASlave.Localization.UiMessage.Format("S9ACAF433203D5340" , global::XASlave.Localization.UiMessage.Capture(animationClipKindMessage, animationClipKind), animationClip.StatusMessage) : (!string.IsNullOrEmpty(animationClip.Status) ? animationClip.StatusMessage : (!string.IsNullOrEmpty(animationRecordingNotice) ? global::XASlave.Localization.UiMessage.Capture(animationRecordingNoticeMessage, animationRecordingNotice) : (!live.Ready ? live.StatusPresentation : global::XASlave.Localization.UiMessage.Key("S1B5755C340A4BB94"))))), animationClip.Complete ? animationClip.FrameCount : 0, animationClip.FrameIndex);
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
                animationClipKind = global::XASlave.Localization.UiMessage.Store(ref animationClipKindMessage, (live.Duration > 0 ? global::XASlave.Localization.UiMessage.Key("S2FB7F070FCF653E2") : global::XASlave.Localization.UiMessage.Key("SBB97F64CCE50C5C1")));
                animationRenderRevision = -1;
                animationControlRevision = live.Revision;
                animationRecordingNotice = global::XASlave.Localization.UiMessage.Store(ref animationRecordingNoticeMessage, global::XASlave.Localization.UiMessage.Raw(string.Empty));
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
        animationRecordingNotice = global::XASlave.Localization.UiMessage.Store(ref animationRecordingNoticeMessage, global::XASlave.Localization.UiMessage.Raw(string.Empty));
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
        animationRecordingNotice = global::XASlave.Localization.UiMessage.Store(ref animationRecordingNoticeMessage, global::XASlave.Localization.UiMessage.Key("S17B03676265E9821"));
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
