using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.Havok.Animation.Animation;
using FFXIVClientStructs.Havok.Animation.Playback;
using FFXIVClientStructs.Havok.Animation.Playback.Control;
using FFXIVClientStructs.Havok.Animation.Playback.Control.Default;
using FFXIVClientStructs.Havok.Common.Base.Container.Array;
using CharacterBase = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace XASlave.Services;

internal sealed unsafe partial class ViewSingleItemScene
{
    // Read-only identities are used to retire a recording when its native clip
    // changes. The user's scrubber replays recorded images, including their VFX.
    private readonly record struct AnimationControlIdentity(nint Draw, nint Skeleton, nint Partial,
        nint AnimatedSkeleton, nint Array, int Index, nint Control, nint Binding, nint Animation,
        float Duration, float Start, float End);
    private readonly record struct AnimationControlSnapshot(AnimationControlIdentity Identity, int Part,
        int PartialIndex, int Layer, float Time, float Weight);
    private AnimationControlIdentity[] animationControls = [];
    private AnimationControlIdentity? primaryAnimationControl;
    private string animationReadFailure = "";
    private long animationReadWarningAt;

    private static void ValidateAnimationLayouts()
    {
        if (sizeof(Skeleton) != 0x100 || sizeof(PartialSkeleton) != 0x230
            || sizeof(hkaAnimatedSkeleton) != 0x40 || sizeof(hkaAnimationControl) != 0x58
            || sizeof(hkaDefaultAnimationControl) != 0xC0 || sizeof(hkaAnimationBinding) != 0x58
            || sizeof(hkaAnimation) != 0x38 || sizeof(hkArray<nint>) != 0x10
            || Marshal.OffsetOf<hkArray<nint>>(nameof(hkArray<nint>.Data)).ToInt32() != 0
            || Marshal.OffsetOf<hkArray<nint>>(nameof(hkArray<nint>.Length)).ToInt32() != 8
            || Marshal.OffsetOf<hkArray<nint>>(nameof(hkArray<nint>.CapacityAndFlags)).ToInt32() != 12)
            throw new InvalidOperationException("The standalone animation layout changed.");
        RequireOffset<CharacterBase>(nameof(CharacterBase.Skeleton), 0xA0);
        RequireOffset<Skeleton>(nameof(Skeleton.Owner), 0xB8);
        RequireOffset<Skeleton>(nameof(Skeleton.PartialSkeletonCount), 0x50);
        RequireOffset<Skeleton>(nameof(Skeleton.PartialSkeletons), 0x68);
        RequireOffset<PartialSkeleton>(nameof(PartialSkeleton.Skeleton), 0x168);
        RequireOffset<PartialSkeleton>("_havokAnimatedSkeletons", 0x138);
        RequireOffset<hkaAnimatedSkeleton>(nameof(hkaAnimatedSkeleton.AnimationControls), 0x20);
        RequireOffset<hkaAnimationControl>(nameof(hkaAnimationControl.LocalTime), 0x10);
        RequireOffset<hkaAnimationControl>(nameof(hkaAnimationControl.Weight), 0x14);
        RequireOffset<hkaAnimationControl>(nameof(hkaAnimationControl.Binding), 0x38);
        RequireOffset<hkaDefaultAnimationControl>(nameof(hkaDefaultAnimationControl.CropStartAmountLocalTime), 0x9C);
        RequireOffset<hkaDefaultAnimationControl>(nameof(hkaDefaultAnimationControl.CropEndAmountLocalTime), 0xA0);
        RequireOffset<hkaAnimationBinding>(nameof(hkaAnimationBinding.Animation), 0x18);
        RequireOffset<hkaAnimation>(nameof(hkaAnimation.Duration), 0x14);
    }

    private void AddAnimationSamplingProofs(PEReader disk)
    {
        // Native skeleton rendering builds sample jobs from these controls.
        // This read-only timeline supplies the duration of a recorded cycle;
        // neither LocalTime nor independently simulated VFX state is rewritten.
        AddProof(disk, 0x25D6F0, 0x2CD); // Owned skeleton sample-job preparation
        AddProof(disk, 0x207A90, 0x18); // Advance is distinct from sampling
        AddProof(disk, 0x1F08FD0, 0x17D); // Build both partial-skeleton sample jobs
        AddProof(disk, 0x1F08A90, 0xB1); // Read each control's LocalTime as sample time
    }

    private void ResetAnimationControls()
    {
        animationControls = [];
        primaryAnimationControl = null;
        animationReadFailure = "";
    }

    private bool TryReadAnimationControls(out List<AnimationControlSnapshot> result)
    {
        result = [];
        if (!animationReady || !ConfirmStillOwner() || !StillGeometryReady(out var geometry)
            || geometry != stillGeometry) return false;
        var root = (nint)((NativeCharacter*)character)->DrawObject;
        for (var part = -1; part < 3; part++)
        {
            var drawAddress = part < 0 ? root : Marshal.ReadIntPtr(character + 0x710 + part * 0x70);
            if (drawAddress == 0) continue;
            NearbyZoneNativeBinding.RequireReadable(drawAddress, sizeof(CharacterBase));
            var skeleton = ((CharacterBase*)drawAddress)->Skeleton;
            if (skeleton == null) continue;
            NearbyZoneNativeBinding.RequireReadable((nint)skeleton, sizeof(Skeleton));
            if ((nint)skeleton->Owner != drawAddress || skeleton->PartialSkeletonCount > 64) return false;
            if (skeleton->PartialSkeletonCount == 0) continue;
            if (skeleton->PartialSkeletons == null) return false;
            NearbyZoneNativeBinding.RequireReadable((nint)skeleton->PartialSkeletons,
                checked(skeleton->PartialSkeletonCount * sizeof(PartialSkeleton)));
            for (var partialIndex = 0; partialIndex < skeleton->PartialSkeletonCount; partialIndex++)
            {
                var partial = &skeleton->PartialSkeletons[partialIndex];
                if (partial->Skeleton != skeleton) return false;
                for (var layer = 0; layer < 2; layer++)
                {
                    var animated = partial->GetHavokAnimatedSkeleton(layer);
                    if (animated == null) continue;
                    NearbyZoneNativeBinding.RequireReadable((nint)animated, sizeof(hkaAnimatedSkeleton));
                    var controls = animated->AnimationControls;
                    if (controls.Length < 0 || controls.Length > 80 || controls.Capacity < controls.Length) return false;
                    if (controls.Length == 0) continue;
                    if (controls.Data == null) return false;
                    NearbyZoneNativeBinding.RequireReadable((nint)controls.Data, checked(controls.Length * sizeof(nint)));
                    for (var index = 0; index < controls.Length; index++)
                    {
                        var control = controls[index].Value;
                        if (control == null) continue;
                        NearbyZoneNativeBinding.RequireReadable((nint)control, sizeof(hkaDefaultAnimationControl));
                        var binding = control->hkaAnimationControl.Binding.ptr;
                        if (binding == null) continue;
                        NearbyZoneNativeBinding.RequireReadable((nint)binding, sizeof(hkaAnimationBinding));
                        var animation = binding->Animation.ptr;
                        if (animation == null) continue;
                        NearbyZoneNativeBinding.RequireReadable((nint)animation, sizeof(hkaAnimation));
                        var duration = animation->Duration;
                        var start = control->CropStartAmountLocalTime;
                        var end = duration - control->CropEndAmountLocalTime;
                        var time = control->hkaAnimationControl.LocalTime;
                        var weight = control->hkaAnimationControl.Weight;
                        if (!float.IsFinite(duration) || duration <= 0 || duration > 3600
                            || !float.IsFinite(start) || !float.IsFinite(end) || start < 0 || end > duration
                            || end <= start || !float.IsFinite(time) || !float.IsFinite(weight)) return false;
                        result.Add(new(new(drawAddress, (nint)skeleton, (nint)partial, (nint)animated,
                            (nint)controls.Data, index, (nint)control, (nint)binding, (nint)animation,
                            duration, start, end), part, partialIndex, layer, time, weight));
                    }
                }
            }
        }
        return ConfirmStillOwner();
    }

    private static AnimationControlSnapshot ChoosePrimaryAnimation(List<AnimationControlSnapshot> controls)
    {
        // Keep the body's base clip ahead of facial/attachment overlays. Among
        // simultaneous base clips use the strongest currently blended control.
        return controls.OrderByDescending(value => value.Part == -1 && value.PartialIndex == 0 && value.Layer == 0)
            .ThenByDescending(value => value.Weight).First();
    }

    private void UpdateAnimationTimeline()
    {
        try { ReadAnimationTimeline(); }
        catch (Exception error)
        {
            // Optional timing metadata must not take down an otherwise admitted
            // still preview. No unconfirmed timeline is exposed to the recorder.
            if (animationReadFailure.Length == 0)
            {
                animationControls = [];
                primaryAnimationControl = null;
                animationTimelineRevision++;
            }
            animationState = new(false, animationPlaying, 0, 0, animationTimelineRevision,
                "Animation timing is unavailable for this model.");
            if (animationReadFailure == error.Message) return;
            animationReadFailure = error.Message;
            if (Environment.TickCount64 - animationReadWarningAt < 5000) return;
            animationReadWarningAt = Environment.TickCount64;
            Plugin.Log.Warning(error, "[View Item] Animation timing unavailable.");
        }
    }

    private void ReadAnimationTimeline()
    {
        if (!AnimationReadyForDisplay() || !TryReadAnimationControls(out var current))
        {
            if (animationControls.Length != 0) { ResetAnimationControls(); animationTimelineRevision++; }
            animationState = new(false, animationPlaying, 0, 0, animationTimelineRevision, "Animation is loading.");
            return;
        }
        var identities = current.Select(value => value.Identity).ToArray();
        animationReadFailure = "";
        var primary = current.Count == 0 ? (AnimationControlIdentity?)null : ChoosePrimaryAnimation(current).Identity;
        if (!animationControls.AsSpan().SequenceEqual(identities) || primaryAnimationControl != primary)
        {
            animationControls = identities;
            primaryAnimationControl = primary;
            animationTimelineRevision++;
        }
        if (primary == null)
        {
            animationState = new(true, animationPlaying, 0, 0, animationTimelineRevision, "This model has no skeletal animation timeline.");
            return;
        }
        var selected = current.First(value => value.Identity == primary.Value);
        var length = selected.Identity.End - selected.Identity.Start;
        animationState = new(true, animationPlaying, Math.Clamp(selected.Time - selected.Identity.Start, 0, length),
            length, animationTimelineRevision, animationPlaying ? "Playing." : "Paused.");
    }

}
