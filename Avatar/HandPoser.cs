using Godot;

namespace SerikaSocial.Avatar;

/// Curls a humanoid avatar's fingers, so a VR player's hand shape reaches their own eyes, every
/// mirror, and (through the pose frame) every peer.
///
/// **The curl axis is derived from the rig, not assumed.** Finger bones have no convention for
/// which local axis is "bend" — a VRoid export, a Blender rig and a PMX conversion each pick a
/// different one, and hardcoding `Vector3.Right` bends one rig's fingers and splays another's
/// sideways. The knuckle line (index knuckle → little knuckle) *is* the flexion axis of a hand as
/// a matter of anatomy, so it is measured from the rest pose per hand and per rig.
///
/// That leaves only the sign ambiguous, and the sign is settled against skeleton-space down: a
/// bind pose is a T-pose or A-pose with the palms facing roughly downward, so of the two possible
/// rotation directions the correct one is whichever moves the fingertip downward. Testing the
/// direction of motion rather than the palm normal is what makes this work on both hands without
/// a left/right special case — the knuckle line reverses between hands, and so does the required
/// sign, and the test picks up both flips at once.
///
/// Roll and splay are not recovered, the same trade-off `AnimRetargeter` and `VrAvatarIk`
/// document: this is a one-degree-of-freedom flexion per finger, which is what the input
/// (a trigger, a grip, or a curl estimate) actually carries.
public sealed class HandPoser
{
    public enum Finger { Thumb = 0, Index = 1, Middle = 2, Ring = 3, Little = 4 }

    /// Humanoid role name stems, indexed by `Finger`. The `.ska` humanoid map uses
    /// `{side}{Stem}{Segment}` — e.g. `leftIndexProximal` (see tools/ska/vrm2ska.py KNOWN_ROLES).
    private static readonly string[] Stems = { "Thumb", "Index", "Middle", "Ring", "Little" };
    private static readonly string[] Segments = { "Proximal", "Intermediate", "Distal" };

    /// How far each segment bends at full curl. The distribution is anatomical: the middle
    /// knuckle closes furthest, and the thumb barely flexes at all compared to the fingers, which
    /// is why it gets its own row. A uniform angle across all three segments produces the
    /// characteristic "claw" that reads as a broken rig.
    private static readonly float[] FingerSegmentDeg = { 68f, 82f, 55f };
    private static readonly float[] ThumbSegmentDeg = { 28f, 38f, 36f };

    private readonly Skeleton3D _skel;
    private readonly Joint[] _joints;
    private readonly bool _valid;

    /// One posable phalanx: its bone index, its rest rotation, and the pre-solved axis-angle that
    /// takes it to full curl. Resolved once at construction — this runs every frame per hand, and
    /// re-deriving the axis from the rest pose each time would put a dozen skeleton queries and a
    /// pile of `Variant` boxing in the frame-time path.
    private readonly struct Joint
    {
        public readonly int Bone;
        public readonly Quaternion RestLocal;
        public readonly Vector3 Axis;      // parent-space flexion axis, already signed
        public readonly float FullAngle;   // radians at curl = 1
        public readonly bool Ok;

        public Joint(int bone, Quaternion restLocal, Vector3 axis, float fullAngle)
        {
            Bone = bone; RestLocal = restLocal; Axis = axis; FullAngle = fullAngle;
            Ok = bone >= 0 && axis.IsFinite() && axis.LengthSquared() > 0.5f;
        }
    }

    public bool Valid => _valid;

    /// Build a poser for one hand of `avatar`. `left` picks which side's humanoid roles to read.
    public HandPoser(AvatarInstance avatar, bool left)
    {
        _skel = avatar?.Skeleton;
        _joints = new Joint[Stems.Length * Segments.Length];
        if (_skel == null) { _valid = false; return; }

        string side = left ? "left" : "right";

        // The flexion axis, in skeleton space, from the knuckle line.
        int indexKnuckle = avatar.BoneOf($"{side}IndexProximal");
        int littleKnuckle = avatar.BoneOf($"{side}LittleProximal");
        int middleKnuckle = avatar.BoneOf($"{side}MiddleProximal");
        int middleTip = avatar.BoneOf($"{side}MiddleDistal");

        // Without a knuckle line there is no rig-derived axis and nothing trustworthy to fall back
        // on, so the poser stands down rather than guessing and splaying the fingers sideways.
        if (indexKnuckle < 0 || littleKnuckle < 0 || middleKnuckle < 0 || middleTip < 0)
        {
            _valid = false;
            return;
        }

        var knuckleAxis = _skel.GetBoneGlobalRest(indexKnuckle).Origin
                        - _skel.GetBoneGlobalRest(littleKnuckle).Origin;
        var fingerDir = _skel.GetBoneGlobalRest(middleTip).Origin
                      - _skel.GetBoneGlobalRest(middleKnuckle).Origin;
        if (knuckleAxis.LengthSquared() < 1e-8f || fingerDir.LengthSquared() < 1e-8f)
        {
            _valid = false;
            return;
        }
        knuckleAxis = knuckleAxis.Normalized();
        fingerDir = fingerDir.Normalized();

        // Rotating `fingerDir` about `knuckleAxis` by a small +angle moves it along
        // (knuckleAxis × fingerDir). Curling must move the tip toward the palm, and in a bind pose
        // the palm faces down, so take the sign that moves the tip downward.
        float sign = Mathf.Sign(knuckleAxis.Cross(fingerDir).Dot(Vector3.Down));
        if (sign == 0f) sign = 1f;
        var flexSkeleton = knuckleAxis * sign;

        for (int f = 0; f < Stems.Length; f++)
        {
            var degrees = f == (int)Finger.Thumb ? ThumbSegmentDeg : FingerSegmentDeg;
            for (int s = 0; s < Segments.Length; s++)
            {
                int bone = avatar.BoneOf($"{side}{Stems[f]}{Segments[s]}");
                if (bone < 0) { _joints[f * Segments.Length + s] = default; continue; }

                // The pose rotation Godot wants is in the bone's *parent* space, so the
                // skeleton-space flexion axis has to be carried into it. Using the rest pose for
                // the conversion (not the live pose) keeps the axis stable while the arm animates
                // — otherwise a finger's bend direction would swing around with the wrist.
                int parent = _skel.GetBoneParent(bone);
                var parentRest = parent >= 0
                    ? _skel.GetBoneGlobalRest(parent).Basis.GetRotationQuaternion()
                    : Quaternion.Identity;
                var axisParent = (parentRest.Inverse() * flexSkeleton).Normalized();

                _joints[f * Segments.Length + s] = new Joint(
                    bone,
                    _skel.GetBoneRest(bone).Basis.GetRotationQuaternion(),
                    axisParent,
                    Mathf.DegToRad(degrees[s]));
            }
        }

        // A rig with knuckles but no posable phalanges is not worth running.
        _valid = System.Array.Exists(_joints, j => j.Ok);
    }

    /// Write `curl` (five floats, 0–1, indexed by `Finger`) onto the skeleton.
    ///
    /// Must be called *after* `AvatarInstance.Animate` and the arm IK, for the same reason
    /// `HeadAim` runs last: whatever writes a bone last wins, and the locomotion clips animate
    /// finger bones on rigs whose walk cycle happens to include them.
    public void Apply(float[] curl)
    {
        if (!_valid || curl == null) return;

        for (int f = 0; f < Stems.Length && f < curl.Length; f++)
        {
            float amount = Mathf.Clamp(curl[f], 0f, 1f);
            for (int s = 0; s < Segments.Length; s++)
            {
                var j = _joints[f * Segments.Length + s];
                if (!j.Ok) continue;
                // Rest pose first, then flex: the rotation is a delta *from* the bind pose, so a
                // curl of 0 restores the bone exactly rather than zeroing it into a T-pose finger.
                var bend = new Quaternion(j.Axis, j.FullAngle * amount);
                _skel.SetBonePoseRotation(j.Bone, (j.RestLocal * bend).Normalized());
            }
        }
    }
}
