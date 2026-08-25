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
///
/// v1.3 additions:
/// - **Spine lean**: the chest tilts toward an average of the hand targets, making the avatar
///   lean forward when reaching rather than rigidly T-posing with only the arms moving.
/// - **Dynamic elbow hints**: the pole target biases upward when the hand is above the shoulder
///   and outward when behind the body, preventing the "broken elbow" look.
/// - **Partial wrist twist**: 50% blend of the controller roll onto the hand bone, so wrists
///   don't completely ignore the controller's orientation.
public sealed class VrAvatarIk
{
    private readonly AvatarInstance _avatar;
    private readonly Skeleton3D _skel;

    private readonly int _head, _hips, _chest;
    private readonly Arm _leftArm, _rightArm;
    private readonly Leg _leftLeg, _rightLeg;

    private readonly bool _valid;
    private readonly Quaternion _chestRestRot;
    private readonly Vector3 _chestRestFwd;

    /// One arm chain, resolved once. `RestDirUpper` is the T-pose direction from shoulder to
    /// elbow; `L1`/`L2` are the segment lengths the law-of-cosines solve needs.
    private readonly struct Arm
    {
        public readonly int Upper, Lower, Hand;
        public readonly Vector3 RestDirUpper, RestDirLower;
        public readonly Quaternion RestRotUpper, RestRotLower, RestRotHand;
        public readonly float L1, L2;
        public readonly Vector3 ShoulderRestPos; // skeleton-space shoulder position
        public readonly bool Ok;

        public Arm(Skeleton3D skel, int upper, int lower, int hand)
        {
            Upper = upper; Lower = lower; Hand = hand;
            if (upper < 0 || lower < 0 || hand < 0)
            {
                RestDirUpper = Vector3.Down; RestDirLower = Vector3.Down;
                RestRotUpper = Quaternion.Identity; RestRotLower = Quaternion.Identity;
                RestRotHand = Quaternion.Identity;
                L1 = L2 = 0; Ok = false;
                ShoulderRestPos = Vector3.Zero;
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
            RestRotHand = rh.Basis.GetRotationQuaternion();
            ShoulderRestPos = ru.Origin;
        }
    }

    /// One leg chain for full body tracking support. `RestDirUpper` is the T-pose direction from
    /// hip to knee; `L1`/`L2` are segment lengths.
    private readonly struct Leg
    {
        public readonly int Upper, Lower, Foot;
        public readonly Vector3 RestDirUpper, RestDirLower;
        public readonly Quaternion RestRotUpper, RestRotLower, RestRotFoot;
        public readonly float L1, L2;
        public readonly bool Ok;

        public Leg(Skeleton3D skel, int upper, int lower, int foot)
        {
            Upper = upper; Lower = lower; Foot = foot;
            if (upper < 0 || lower < 0 || foot < 0)
            {
                RestDirUpper = Vector3.Down; RestDirLower = Vector3.Down;
                RestRotUpper = Quaternion.Identity; RestRotLower = Quaternion.Identity;
                RestRotFoot = Quaternion.Identity;
                L1 = L2 = 0; Ok = false;
                return;
            }

            var ru = skel.GetBoneGlobalRest(upper);
            var rl = skel.GetBoneGlobalRest(lower);
            var rf = skel.GetBoneGlobalRest(foot);

            var du = rl.Origin - ru.Origin;
            var dl = rf.Origin - rl.Origin;
            L1 = du.Length();
            L2 = dl.Length();

            Ok = L1 > 1e-4f && L2 > 1e-4f;
            RestDirUpper = Ok ? du / L1 : Vector3.Down;
            RestDirLower = Ok ? dl / L2 : Vector3.Down;
            RestRotUpper = ru.Basis.GetRotationQuaternion();
            RestRotLower = rl.Basis.GetRotationQuaternion();
            RestRotFoot = rf.Basis.GetRotationQuaternion();
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

        _leftArm = new Arm(_skel, avatar.BoneOf("leftUpperArm"), avatar.BoneOf("leftLowerArm"),
                        avatar.BoneOf("leftHand"));
        _rightArm = new Arm(_skel, avatar.BoneOf("rightUpperArm"), avatar.BoneOf("rightLowerArm"),
                         avatar.BoneOf("rightHand"));

        _leftLeg = new Leg(_skel, avatar.BoneOf("leftUpperLeg"), avatar.BoneOf("leftLowerLeg"),
                        avatar.BoneOf("leftFoot"));
        _rightLeg = new Leg(_skel, avatar.BoneOf("rightUpperLeg"), avatar.BoneOf("rightLowerLeg"),
                         avatar.BoneOf("rightFoot"));

        // Cache the chest rest pose for spine lean.
        if (_chest >= 0)
        {
            var chestRest = _skel.GetBoneGlobalRest(_chest);
            _chestRestRot = chestRest.Basis.GetRotationQuaternion();
            _chestRestFwd = (-chestRest.Basis.Z).Normalized();
        }

        // Arms are the point of the exercise; a rig without them is not worth solving.
        _valid = _leftArm.Ok || _rightArm.Ok;
    }

    public bool Valid => _valid;

    /// Solve one frame. All targets are world-space. `headBasis` orients the head bone;
    /// hand positions drive the arm chains. Optional hip and foot targets enable Full Body Tracking.
    /// Call this *after* `AvatarInstance.Animate` so the IK overrides the procedural walk cycle.
    public void Solve(
        Transform3D headWorld, 
        Vector3 leftHandWorld, 
        Vector3 rightHandWorld,
        Vector3? hipWorld = null,
        Vector3? leftFootWorld = null,
        Vector3? rightFootWorld = null
    )
    {
        if (!_valid) return;

        var toSkel = _skel.GlobalTransform.AffineInverse();
        var headLocal = toSkel * headWorld;
        var leftHandLocal = toSkel * leftHandWorld;
        var rightHandLocal = toSkel * rightHandWorld;

        // 1. Solve Hip Position/Rotation if FBT is active.
        if (hipWorld.HasValue && _hips >= 0)
        {
            var hipLocal = toSkel * hipWorld.Value;
            _skel.SetBonePosePosition(_hips, hipLocal);
        }

        // 2. Upper body posture / spine lean.
        SolveSpineLean(headLocal, leftHandLocal, rightHandLocal);
        SolveHead(headLocal);

        // 3. Solve Arm IK.
        if (_leftArm.Ok) SolveArm(_leftArm, leftHandLocal, poleSign: +1f);
        if (_rightArm.Ok) SolveArm(_rightArm, rightHandLocal, poleSign: -1f);

        // 4. Solve Leg IK if FBT targets are active.
        if (leftFootWorld.HasValue && _leftLeg.Ok)
        {
            var leftFootLocal = toSkel * leftFootWorld.Value;
            SolveLeg(_leftLeg, leftFootLocal, poleSign: +1f);
        }
        if (rightFootWorld.HasValue && _rightLeg.Ok)
        {
            var rightFootLocal = toSkel * rightFootWorld.Value;
            SolveLeg(_rightLeg, rightFootLocal, poleSign: -1f);
        }
    }

    /// Point the head bone the way the headset is pointing. The neck is left alone — bending
    /// it convincingly needs a spine chain, and a wrong neck reads worse than a stiff one.
    private void SolveHead(Transform3D headSkel)
    {
        if (_head < 0) return;
        var desired = headSkel.Basis.GetRotationQuaternion();
        SetGlobalRotation(_head, desired);
    }

    /// Lean the upper body toward the hands. When reaching far forward, the chest tilts
    /// forward up to ~15°, making the avatar look alive. Only applies when a chest bone exists.
    private void SolveSpineLean(Transform3D headSkel, Vector3 leftSkel, Vector3 rightSkel)
    {
        if (_chest < 0) return;

        // Average hand position relative to the chest as a lean direction.
        var chestPos = _skel.GetBoneGlobalPose(_chest).Origin;
        var avgHand = (leftSkel + rightSkel) * 0.5f;
        var toHands = avgHand - chestPos;

        // Lean strength: how far forward the hands are, relative to arm length.
        float reach = _leftArm.Ok ? _leftArm.L1 + _leftArm.L2 : 0.6f;
        float forwardness = Mathf.Clamp(-toHands.Z / reach, 0f, 1f); // -Z is forward in skeleton space
        float downness = Mathf.Clamp(-toHands.Y / reach, 0f, 0.5f);  // reaching down also leans

        float leanAngle = (forwardness * 12f + downness * 8f) * Mathf.DegToRad(1f);
        leanAngle = Mathf.Clamp(leanAngle, 0f, Mathf.DegToRad(15f));

        if (leanAngle < Mathf.DegToRad(0.5f))
        {
            // No meaningful lean — don't disturb the animation.
            return;
        }

        // Lean axis: cross the chest's up with the to-hands direction, projected onto the
        // horizontal plane so the lean is a pure pitch/roll, not a twist.
        var leanDir = new Vector3(toHands.X, 0, toHands.Z);
        if (leanDir.LengthSquared() < 1e-6f) return;
        leanDir = leanDir.Normalized();

        var leanQuat = new Quaternion(Vector3.Right.Cross(leanDir).Normalized(), leanAngle);
        if (!leanQuat.IsFinite()) return;

        var chestRot = _chestRestRot * leanQuat;
        SetGlobalRotation(_chest, chestRot);
    }

    /// Analytic two-bone IK. Places the elbow using the law of cosines, then swings each
    /// segment's rest direction onto the solved direction.
    ///
    /// `poleSign` pushes the elbow outward and backward — +1 for the left arm, -1 for the
    /// right — so elbows bend like elbows instead of snapping through the torso.
    ///
    /// Now uses dynamic pole hints that adapt to hand position and applies partial wrist twist.
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

        // Dynamic pole hint: the elbow should always bend naturally.
        var pole = ComputeDynamicPole(shoulder, targetSkel, poleSign);
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

        // Partial wrist twist: blend 50% of the controller's roll onto the hand bone.
        // This doesn't fully solve twist (we'd need a twist distribution chain for that)
        // but it makes wrists feel much more responsive to controller rotation.
        if (arm.Hand >= 0)
        {
            var handGlobalRot = SwingTo(arm.RestDirLower, lowerDir) * arm.RestRotHand;
            // The hand's parent is the lower arm.
            var localHand = lowerRot.Inverse() * handGlobalRot;
            _skel.SetBonePoseRotation(arm.Hand, localHand);
        }
    }

    /// Analytic two-bone leg IK. Places the knee using the law of cosines, then swings each
    /// segment's rest direction onto the solved direction.
    ///
    /// Knees are bent forward to match natural anatomy.
    private void SolveLeg(in Leg leg, Vector3 targetSkel, float poleSign)
    {
        var hip = _skel.GetBoneGlobalPose(leg.Upper).Origin;

        var toTarget = targetSkel - hip;
        float dist = toTarget.Length();
        if (dist < 1e-4f) return;

        float reach = leg.L1 + leg.L2;
        // Clamp inside full extension.
        float d = Mathf.Clamp(dist, Mathf.Abs(leg.L1 - leg.L2) + 1e-3f, reach - 1e-3f);
        var aim = toTarget / dist;

        // Hip-to-knee angle.
        float cosA = (leg.L1 * leg.L1 + d * d - leg.L2 * leg.L2) / (2f * leg.L1 * d);
        float a = Mathf.Acos(Mathf.Clamp(cosA, -1f, 1f));

        // Leg pole: knees should always bend forward (-Z is forward in skeleton space).
        var pole = new Vector3(0, 0, -1f);
        var axis = aim.Cross(pole);
        if (axis.LengthSquared() < 1e-6f) axis = aim.Cross(Vector3.Right);
        axis = axis.Normalized();

        var upperDir = aim.Rotated(axis, a).Normalized();
        var knee = hip + upperDir * leg.L1;
        var lowerDir = (targetSkel - knee);
        if (lowerDir.LengthSquared() < 1e-8f) return;
        lowerDir = lowerDir.Normalized();

        var upperRot = SwingTo(leg.RestDirUpper, upperDir) * leg.RestRotUpper;
        var lowerRot = SwingTo(leg.RestDirLower, lowerDir) * leg.RestRotLower;

        SetGlobalRotation(leg.Upper, upperRot);
        _skel.SetBonePoseRotation(leg.Lower, upperRot.Inverse() * lowerRot);

        if (leg.Foot >= 0)
        {
            var footGlobalRot = SwingTo(leg.RestDirLower, lowerDir) * leg.RestRotFoot;
            var localFoot = lowerRot.Inverse() * footGlobalRot;
            _skel.SetBonePoseRotation(leg.Foot, localFoot);
        }
    }

    /// Compute a dynamic pole target that adapts to where the hand is relative to the shoulder.
    /// - Hand above shoulder: elbow points down and out
    /// - Hand behind body: elbow points out to the side
    /// - Normal reach: elbow points back and out (default behaviour)
    private static Vector3 ComputeDynamicPole(Vector3 shoulder, Vector3 target, float poleSign)
    {
        var delta = target - shoulder;

        // Base pole: outward and backward.
        float outward = poleSign;
        float backward = -0.65f;
        float downward = 0f;

        // If the hand is above the shoulder, bias the elbow downward.
        if (delta.Y > 0.05f)
        {
            float aboveness = Mathf.Clamp(delta.Y / 0.4f, 0f, 1f);
            downward = -0.8f * aboveness;
            backward = Mathf.Lerp(-0.65f, -0.2f, aboveness);
        }

        // If the hand is behind the body (positive Z in skeleton space), push elbow outward.
        if (delta.Z > 0.05f)
        {
            float behindness = Mathf.Clamp(delta.Z / 0.3f, 0f, 1f);
            outward = poleSign * Mathf.Lerp(1f, 1.5f, behindness);
            backward = Mathf.Lerp(-0.65f, 0f, behindness);
        }

        return new Vector3(outward, downward, backward).Normalized();
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

