using System;
using System.Runtime.InteropServices;

namespace XASlave.Services;

internal readonly record struct ViewSingleItemAnimationState(bool Ready, bool Playing, float Position,
    float Duration, long Revision, string Status, int FrameCount = 0, int FrameIndex = 0);

internal sealed unsafe partial class ViewSingleItemScene
{
    private bool animationReady, animationPlaying;
    private long animationTimelineRevision;
    private ViewSingleItemAnimationState animationState = new(false, false, 0, 0, 0, "Animation is loading.");

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
        animationState = new(false, false, 0, 0, animationTimelineRevision, "Animation is loading.");
    }

    private void SuspendAnimationTimeline()
    {
        animationReady = false;
        animationState = new(false, false, 0, 0, animationTimelineRevision, "Animation is loading.");
    }
}
