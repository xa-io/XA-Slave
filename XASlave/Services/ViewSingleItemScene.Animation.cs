using System;
using System.Runtime.InteropServices;

namespace XASlave.Services;

internal readonly record struct ViewSingleItemAnimationState(bool Ready, bool Playing, float Position,
    float Duration, long Revision, string Status, int FrameCount = 0, int FrameIndex = 0)
{
    internal global::XASlave.Localization.UiMessage? StatusMessage { get; init; }
    internal global::XASlave.Localization.UiMessage StatusPresentation => global::XASlave.Localization.UiMessage.Capture(StatusMessage, Status);
    internal string LocalizedStatus => StatusPresentation.Display;
    public bool Equals(ViewSingleItemAnimationState other) => Ready == other.Ready && Playing == other.Playing
        && Position.Equals(other.Position) && Duration.Equals(other.Duration) && Revision == other.Revision
        && Status == other.Status && FrameCount == other.FrameCount && FrameIndex == other.FrameIndex;
    public override int GetHashCode() => HashCode.Combine(Ready, Playing, Position, Duration, Revision, Status, FrameCount, FrameIndex);
    internal static ViewSingleItemAnimationState Create(bool ready, bool playing, float position, float duration,
        long revision, global::XASlave.Localization.UiMessage status, int frameCount = 0, int frameIndex = 0)
        => new(ready, playing, position, duration, revision, status.Original, frameCount, frameIndex) { StatusMessage = status };
}

internal sealed unsafe partial class ViewSingleItemScene
{
    private bool animationReady, animationPlaying;
    private long animationTimelineRevision;
    private ViewSingleItemAnimationState animationState = ViewSingleItemAnimationState.Create(false, false, 0, 0, 0, global::XASlave.Localization.UiMessage.Key("S44036EB352ED01CD"));

    internal ViewSingleItemAnimationState Animation { get { lock (sync) return animationState; } }

    private bool AnimationReadyForDisplay()
    {
        if (!animationReady) return false;
        if (animationPlaying) return !ownsPauseReason && Marshal.ReadByte(character + 0x1BA4) == 0;
        return animationPaused && (Marshal.ReadByte(character + 0x1BA4) & 0x10) != 0 && ConfirmPausedDraws();
    }

    internal void SetAnimationPlaying(bool playing)
    {
        Validate();
        lock (sync)
        {
            if (!animationReady || GetCharacter() == null)
            {
                // Stop intent survives a temporarily loading/replaced draw. The
                // next admitted Tick applies pause to the current owned clone.
                if (!playing) { animationPlaying = false; SuspendAnimationTimeline(); }
                return;
            }
            animationPlaying = playing;
            PauseOwnedAnimation();
            UpdateAnimationTimeline();
        }
    }

    private void ResetAnimationTimeline()
    {
        animationReady = animationPlaying = false;
        animationTimelineRevision++;
        ResetAnimationControls();
        animationState = ViewSingleItemAnimationState.Create(false, false, 0, 0, animationTimelineRevision, global::XASlave.Localization.UiMessage.Key("S44036EB352ED01CD"));
    }

    private void SuspendAnimationTimeline()
    {
        animationReady = false;
        animationState = ViewSingleItemAnimationState.Create(false, false, 0, 0, animationTimelineRevision, global::XASlave.Localization.UiMessage.Key("S44036EB352ED01CD"));
    }
}
