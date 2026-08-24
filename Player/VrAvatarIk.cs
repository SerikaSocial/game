using Godot;
using SerikaSocial.Avatar;

namespace SerikaSocial.Player;

/// Drives a humanoid `.ska` avatar from the three things a headset actually tracks: the head
/// and the two hands. Everything else — elbows, spine lean, body yaw — is inferred.
///
/// The solver follows the same principle as `AnimRetargeter`: it swings a bone's *rest
/// direction* onto a desired direction rather than transferring a rest-delta. A VRM bind pose
/// is a clean T-pose, so the rest direction of `leftUpperArm` genuinely points down the arm,
/// which makes the swing well-defined. Bone roll/twist is not recovered — the same trade-off
/// documented for retargeting — so wrists do not counter-rotate with the controller.
///
/// All maths runs in *skeleton space* (the space `GetBoneGlobalPose` reports in). World-space
/// targets are pulled into it once, up front, so no per-bone transform juggling is needed.
public sealed class VrAvatarIk
{
    private readonly AvatarInstance _avatar;
    private readonly Skeleton3D _skel;

    private readonly int _head, _hips, _chest;
    private readonly Arm _left, _right;

    private readonly bool _valid;

    /// One arm chain, resolved once. `RestDirUpper` is the T-pose direction from shoulder to
    /// elbow; `L1`/`L2` are the segment lengths the law-of-cosines solve needs.
    private readonly struct Arm
    {
        public readonly int Upper, Lower, Hand;
        public readonly Vector3 RestDirUpper, RestDirLower;
        public readonly Quaternion RestRotUpper, RestRotLower;
        public readonly float L1, L2;
        public readonly bool Ok;

        public Arm(Skeleton3D skel, int upper, int lower, int hand)
        {
            Upper = upper; Lower = lower; Hand = hand;
            if (upper < 0 || lower < 0 || hand < 0)
            {
                RestDirUpper = Vector3.Down; RestDirLower = Vector3.Down;
                RestRotUpper = Quaternion.Identity; RestRotLower = Quaternion.Identity;
                L1 = L2 = 0; Ok = false;
                return;
            }

            var ru = skel.GetBoneGlobalRest(upper);
            var rl = skel.GetBoneGlobalRest(lower);
            var rh = skel.GetBoneGlobalRest(hand);

            var du = rl.Origin - ru.Origin;
            var dl = rh.Origin - rl.Origin;
            L1 = du.Length();
            L2 = dl.Length();

            // A degenerate chain (zero-length segments) would divide by zero in the solve.
            Ok = L1 > 1e-4f && L2 > 1e-4f;
            RestDirUpper = Ok ? du / L1 : Vector3.Down;
            RestDirLower = Ok ? dl / L2 : Vector3.Down;
            RestRotUpper = ru.Basis.GetRotationQuaternion();
            RestRotLower = rl.Basis.GetRotationQuaternion();
        }
    }

    public VrAvatarIk(AvatarInstance avatar)
    {
        _avatar = avatar;
        _skel = avatar?.Skeleton;
        if (_skel == null) { _valid = false; return; }

        _head = avatar.BoneOf("head");
        _hips = avatar.BoneOf("hips");
        _chest = avatar.BoneOf("chest");

        _left = new Arm(_skel, avatar.BoneOf("leftUpperArm"), avatar.BoneOf("leftLowerArm"),
                        avatar.BoneOf("leftHand"));
        _right = new Arm(_skel, avatar.BoneOf("rightUpperArm"), avatar.BoneOf("rightLowerArm"),
                         avatar.BoneOf("rightHand"));

        // Arms are the point of the exercise; a rig without them is not worth solving.
        _valid = _left.Ok || _right.Ok;
    }

    public bool Valid => _valid;

    /// Solve one frame. All three targets are world-space. `headBasis` orients the head bone;
    /// hand positions drive the arm chains. Call this *after* `AvatarInstance.Animate` so the
    /// IK overrides the procedural walk cycle on the bones it owns.
    public void Solve(Transform3D headWorld, Vector3 leftHandWorld, Vector3 rightHandWorld)
    {
        if (!_valid) return;

        var toSkel = _skel.GlobalTransform.AffineInverse();
        var headLocal = toSkel * headWorld;

        SolveHead(headLocal);
        if (_left.Ok) SolveArm(_left, toSkel * leftHandWorld, poleSign: +1f);
        if (_right.Ok) SolveArm(_right, toSkel * rightHandWorld, poleSign: -1f);
    }

    /// Point the head bone the way the headset is pointing. The neck is left alone — bending
    /// it convincingly needs a spine chain, and a wrong neck reads worse than a stiff one.
    private void SolveHead(Transform3D headSkel)
    {
        if (_head < 0) return;
        var desired = headSkel.Basis.GetRotationQuaternion();
        SetGlobalRotation(_head, desired);
    }

    /// Analytic two-bone IK. Places the elbow using the law of cosines, then swings each
    /// segment's rest direction onto the solved direction.
    ///
    /// `poleSign` pushes the elbow outward and backward — +1 for the left arm, -1 for the
    /// right — so elbows bend like elbows instead of snapping through the torso.
    private void SolveArm(in Arm arm, Vector3 targetSkel, float poleSign)
    {
        var shoulder = _skel.GetBoneGlobalPose(arm.Upper).Origin;

        var toTarget = targetSkel - shoulder;
        float dist = toTarget.Length();
        if (dist < 1e-4f) return;

        float reach = arm.L1 + arm.L2;
        // Clamp inside full extension: at exactly `reach` the cosine solve degenerates, and
        // beyond it there is no solution at all — the arm just points at an unreachable target.
        float d = Mathf.Clamp(dist, Mathf.Abs(arm.L1 - arm.L2) + 1e-3f, reach - 1e-3f);
        var aim = toTarget / dist;

        // Shoulder-to-elbow angle off the straight line to the target.
        float cosA = (arm.L1 * arm.L1 + d * d - arm.L2 * arm.L2) / (2f * arm.L1 * d);
        float a = Mathf.Acos(Mathf.Clamp(cosA, -1f, 1f));

        // Pole: outward along the body's X and backward along -Z, projected off the aim axis
        // so it is a valid rotation plane. Falls back to world up if the arm points straight
        // at the pole (degenerate cross product).
        var pole = new Vector3(poleSign, 0f, -0.65f).Normalized();
        var axis = aim.Cross(pole);
        if (axis.LengthSquared() < 1e-6f) axis = aim.Cross(Vector3.Up);
        if (axis.LengthSquared() < 1e-6f) axis = aim.Cross(Vector3.Right);
        axis = axis.Normalized();

        var upperDir = aim.Rotated(axis, a).Normalized();
        var elbow = shoulder + upperDir * arm.L1;
        var lowerDir = (targetSkel - elbow);
        if (lowerDir.LengthSquared() < 1e-8f) return;
        lowerDir = lowerDir.Normalized();

        var upperRot = SwingTo(arm.RestDirUpper, upperDir) * arm.RestRotUpper;
        var lowerRot = SwingTo(arm.RestDirLower, lowerDir) * arm.RestRotLower;

        SetGlobalRotation(arm.Upper, upperRot);
        // The lower arm's parent is the upper arm, whose global rotation we just solved — use
        // it directly rather than re-querying a skeleton that may not have flushed yet.
        _skel.SetBonePoseRotation(arm.Lower, upperRot.Inverse() * lowerRot);
    }

    /// Shortest-arc rotation taking `from` onto `to`, guarding the antiparallel case where the
    /// cross product vanishes and the axis is ambiguous.
    private static Quaternion SwingTo(Vector3 from, Vector3 to)
    {
        float dot = from.Dot(to);
        if (dot > 0.9999f) return Quaternion.Identity;
        if (dot < -0.9999f)
        {
            var ortho = from.Cross(Vector3.Up);
            if (ortho.LengthSquared() < 1e-6f) ortho = from.Cross(Vector3.Right);
            return new Quaternion(ortho.Normalized(), Mathf.Pi);
        }
        return new Quaternion(from, to);
    }

    /// Write a skeleton-space rotation onto a bone as the local pose rotation Godot expects.
    private void SetGlobalRotation(int bone, Quaternion globalRot)
    {
        int parent = _skel.GetBoneParent(bone);
        var parentRot = parent >= 0
            ? _skel.GetBoneGlobalPose(parent).Basis.GetRotationQuaternion()
            : Quaternion.Identity;
        _skel.SetBonePoseRotation(bone, (parentRot.Inverse() * globalRot).Normalized());
    }
}
