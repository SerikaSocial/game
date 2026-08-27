using System.Collections.Generic;
using Godot;

namespace SerikaSocial.Avatar;

/// Head aim and head gestures — the layer that makes a desktop player's head point where they
/// are actually looking, and lets them nod "yes" or shake "no".
///
/// **This needs no netcode.** `HumanoidBones.Lod1` already carries `neck` (index 4) and `head`
/// (index 5), `CaptureBonePose` reads the ANIMATED pose, and `AvatarPose` packs all 22 LOD1
/// bones into every 20 Hz frame. Anything written here is streamed to peers, mirrored, and
/// baked into the shadow silhouette for free.
///
/// It runs as the LAST stage of `Animate()`, after the retargeter, because the retargeter owns
/// `neck` (it is a key of `AnimRetargeter.ChildOf`, so the chest→neck→head direction drives it)
/// and would otherwise overwrite anything written earlier in the frame. `head` is not a
/// `ChildOf` key and no longer gets a procedural swing, so nothing else writes it at all.
///
/// Not to be confused with the head sway that was deliberately removed from `Animate()`: that
/// was a free-running sine that streamed a permanently nodding skull to every peer. Everything
/// here is driven by intent — look input, or a gesture the player asked for — and is exactly
/// zero when the player is neither looking around nor gesturing.
public sealed partial class AvatarInstance
{
    // ── Cervical range ───────────────────────────────────────────────────────────────
    // A real neck does not pitch 90°. These match `LocalPlayer`'s first-person pitch clamps
    // exactly, so in first person the avatar's head lines up with the camera through the whole
    // travel of the mouse instead of saturating partway; in third person, where the orbit is
    // free to 80°, the head stops at the neck's limit and the camera keeps going.
    private const float AimPitchDown = 1.05f; // 60° — cervical flexion
    private const float AimPitchUp = 0.96f;   // 55° — cervical extension
    private const float AimYawLimit = 1.31f;  // 75° — cervical rotation, one side

    // How the aim is split between the two bones. Anatomically the lower cervical spine carries
    // the smaller share of gaze rotation and the atlanto-occipital joint the larger, and visually
    // putting it all on `head` snaps the skull off a rigid neck. Rigs with no `neck` bone get the
    // whole thing on `head`.
    private const float NeckAimShare = 0.35f;

    // A nod pivots almost entirely at the skull; the neck barely participates. Keeping the neck
    // share low here is also what stops a nod from visibly bobbing the shoulders.
    private const float NeckGestureShare = 0.18f;

    // ── Gesture shape ────────────────────────────────────────────────────────────────
    private const float NodFrequency = 2.4f;    // Hz — about two nods over the envelope
    private const float NodAmplitude = 0.30f;   // 17°
    private const float ShakeFrequency = 2.8f;
    private const float ShakeAmplitude = 0.38f; // 22°
    private const float GestureDuration = 1.0f;
    private const float GestureDecay = 2.6f;    // e-folds per second

    /// Which head gesture is running, if any.
    public enum HeadGesture { None, Nod, Shake }

    private float _aimYaw, _aimPitch;
    private HeadGesture _gesture = HeadGesture.None;
    private float _gestureTime;

    /// True while a nod or shake is playing. `VrPlayer` reads this to stand the head IK down for
    /// the duration, or the headset pose would overwrite the gesture the moment it was written.
    public bool GestureActive => _gesture != HeadGesture.None;

    /// Point the head where the player is looking. Both angles are body-relative radians:
    /// `pitch` positive looks up, `yawOffset` positive turns left. Desktop passes its mouse pitch
    /// and a zero yaw (the body already turns with the mouse); the values are clamped to the
    /// cervical range here, so callers may pass raw look angles.
    ///
    /// Call once per tick before `Animate`. Not used by VR — there the headset drives the head
    /// bone directly through `VrAvatarIk.SolveHead`.
    public void SetLookAim(float yawOffset, float pitch)
    {
        _aimYaw = Mathf.Clamp(yawOffset, -AimYawLimit, AimYawLimit);
        _aimPitch = Mathf.Clamp(pitch, -AimPitchDown, AimPitchUp);
    }

    /// Start a nod ("yes") or a head shake ("no"). Restarts a gesture already in flight rather
    /// than queueing, so leaning on the button reads as continued nodding.
    ///
    /// Deliberately additive rather than a full-body clip: `locomotion.glb`'s authored `Yes` and
    /// `Reject` clips take over the whole skeleton, which means they cancel your locomotion, snap
    /// your head away from wherever you were looking, and are suppressed outright above 0.5 m/s
    /// (see the `speed < 0.5f` gate in `Animate`). A nod is a head gesture; layering it on the aim
    /// lets you nod at someone while walking beside them, still looking at them.
    public void PlayHeadGesture(HeadGesture g)
    {
        _gesture = g;
        _gestureTime = 0f;
    }

    /// Advance the gesture envelope and return its contribution as (yaw, pitch) radians.
    /// A decaying sine: it starts and ends at zero, so there is no pop at either end, and it
    /// stops dead once the envelope is spent — nothing here free-runs.
    private (float yaw, float pitch) StepGesture(float dt)
    {
        if (_gesture == HeadGesture.None) return (0f, 0f);

        _gestureTime += dt;
        if (_gestureTime >= GestureDuration)
        {
            _gesture = HeadGesture.None;
            return (0f, 0f);
        }

        float envelope = Mathf.Exp(-_gestureTime * GestureDecay);
        return _gesture == HeadGesture.Nod
            ? (0f, Mathf.Sin(_gestureTime * Mathf.Tau * NodFrequency) * NodAmplitude * envelope)
            : (Mathf.Sin(_gestureTime * Mathf.Tau * ShakeFrequency) * ShakeAmplitude * envelope, 0f);
    }

    // What we last wrote to each bone, and the local-space correction that produced it, so the
    // next frame can tell its own contribution apart from a fresh animation pose. See BasePose.
    private readonly Dictionary<int, (Quaternion Written, Quaternion Correction)> _aimApplied = new();

    /// The bone's local pose with our own previous contribution removed.
    ///
    /// Necessary because the two bones are written by different things on different frames.
    /// `neck` is normally rewritten from scratch by the retargeter — but only when the current
    /// state actually has a clip; on `Idle` the retargeter's fallback weight is 1, its slerp
    /// collapses to a no-op, and the pose it "writes" is simply whatever was already there.
    /// `head` is never written by anything else at all. In both of those cases a post-multiplied
    /// delta would compound every frame and wind the head around like a screw.
    ///
    /// Rather than guess, compare: if the bone still holds the exact quaternion we left there,
    /// nobody has touched it and our correction is peeled back off. If it holds anything else,
    /// an animation has legitimately replaced it and it is used as-is.
    private Quaternion BasePose(int bone)
    {
        var current = Skeleton.GetBonePoseRotation(bone);
        if (_aimApplied.TryGetValue(bone, out var prev)
            && Mathf.Abs(current.Dot(prev.Written)) > 0.99999f)
            return (current * prev.Correction.Inverse()).Normalized();
        return current;
    }

    /// Rewrite `bone` so that, in skeleton space, it gains `delta` on top of `basePose`.
    ///
    /// `delta` is expressed in skeleton space — the character-aligned, Y-up, -Z-forward frame
    /// that `GetBoneGlobalPose` reports in, the same convention `VrAvatarIk` and `AnimRetargeter`
    /// use. Conjugating it by the bone's own global rotation turns it into the equivalent
    /// rotation in that bone's local frame, which is what makes this independent of how the rig's
    /// author happened to orient the neck and head bones.
    ///
    /// Conjugation rather than a parent-relative rebuild is also what keeps the two writes
    /// independent: because `local' = local · (Q⁻¹ · delta · Q)` gives `global' = delta · global`,
    /// applying deltas to neck and head from globals both sampled BEFORE either write leaves the
    /// head with the product of the two — i.e. the angles simply add — with no need to re-read a
    /// parent transform in between.
    private void WriteAim(int bone, Quaternion basePose, Quaternion boneGlobal, Quaternion delta)
    {
        var correction = (boneGlobal.Inverse() * delta * boneGlobal).Normalized();
        var written = (basePose * correction).Normalized();
        Skeleton.SetBonePoseRotation(bone, written);
        _aimApplied[bone] = (written, correction);
    }

    /// Skeleton-space rotation for a (yaw, pitch) pair. Yaw about up, then pitch about the
    /// character's right axis — positive pitch looks up.
    private static Quaternion AimDelta(float yaw, float pitch) =>
        new Quaternion(Vector3.Up, yaw) * new Quaternion(Vector3.Right, pitch);

    /// Apply look aim and any running gesture to the neck and head. The last thing `Animate`
    /// does, so it composes on top of whatever locomotion clip is playing rather than fighting it.
    private void ApplyHeadAim(float dt)
    {
        if (Skeleton == null) return;

        var (gestureYaw, gesturePitch) = StepGesture(dt);

        int head = BoneOf("head");
        if (head < 0) return;
        int neck = BoneOf("neck");

        // Everything lands on the head when the rig has no neck, so a neckless rig still aims.
        float neckAim = neck >= 0 ? NeckAimShare : 0f;
        float neckGesture = neck >= 0 ? NeckGestureShare : 0f;

        var neckDelta = AimDelta(_aimYaw * neckAim + gestureYaw * neckGesture,
                                 _aimPitch * neckAim + gesturePitch * neckGesture);
        var headDelta = AimDelta(_aimYaw * (1f - neckAim) + gestureYaw * (1f - neckGesture),
                                 _aimPitch * (1f - neckAim) + gesturePitch * (1f - neckGesture));

        // Both bases and both globals are sampled before either write — see WriteAim for why
        // that is what makes the two contributions add instead of one clobbering the other.
        Quaternion neckBase = default, neckGlobal = default;
        if (neck >= 0)
        {
            neckBase = BasePose(neck);
            neckGlobal = Skeleton.GetBoneGlobalPose(neck).Basis.GetRotationQuaternion();
        }
        var headBase = BasePose(head);
        var headGlobal = Skeleton.GetBoneGlobalPose(head).Basis.GetRotationQuaternion();

        if (neck >= 0) WriteAim(neck, neckBase, neckGlobal, neckDelta);
        WriteAim(head, headBase, headGlobal, headDelta);
    }
}
