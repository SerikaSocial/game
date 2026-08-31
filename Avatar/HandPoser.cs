using Godot;

namespace SerikaSocial.Avatar;

/// Curls and splays a humanoid avatar's fingers with anatomical kinematics, so a VR player's
/// hand shape reaches their own eyes, mirrors, and peer avatars.
///
/// Computes both the flexion axis (along the knuckle line) and the splay/abduction axis (normal
/// to the palm). Implements natural anatomical knuckle convergence (fingers wrap inward toward the
/// center of the palm during flexion rather than folding on rigid parallel rails) and full 3D
/// thumb opposition.
public sealed class HandPoser
{
    public enum Finger { Thumb = 0, Index = 1, Middle = 2, Ring = 3, Little = 4 }

    private static readonly string[] Stems = { "Thumb", "Index", "Middle", "Ring", "Little" };
    private static readonly string[] Segments = { "Proximal", "Intermediate", "Distal" };

    private static readonly float[] FingerSegmentDeg = { 68f, 82f, 55f };
    private static readonly float[] ThumbSegmentDeg = { 28f, 38f, 36f };

    // Natural inward convergence angle per finger at full curl (degrees)
    private static readonly float[] ConvergenceDeg = { -15f, 5.5f, 0f, -6.0f, -10.5f };

    private readonly Skeleton3D _skel;
    private readonly Joint[] _joints;
    private readonly bool _valid;
    private readonly bool _isLeft;

    private readonly struct Joint
    {
        public readonly int Bone;
        public readonly Quaternion RestLocal;
        public readonly Vector3 FlexAxis;    // parent-space flexion axis
        public readonly Vector3 SplayAxis;   // parent-space splay/abduction axis
        public readonly float FullAngle;     // radians at curl = 1
        public readonly float ConvergenceRad;// radians of inward convergence at curl = 1
        public readonly bool Ok;

        public Joint(int bone, Quaternion restLocal, Vector3 flexAxis, Vector3 splayAxis, float fullAngle, float convergenceRad)
        {
            Bone = bone;
            RestLocal = restLocal;
            FlexAxis = flexAxis;
            SplayAxis = splayAxis;
            FullAngle = fullAngle;
            ConvergenceRad = convergenceRad;
            Ok = bone >= 0 && flexAxis.IsFinite() && flexAxis.LengthSquared() > 0.5f;
        }
    }

    public bool Valid => _valid;

    /// Build a poser for one hand of `avatar`. `left` picks which side's humanoid roles to read.
    public HandPoser(AvatarInstance avatar, bool left)
    {
        _isLeft = left;
        _skel = avatar?.Skeleton;
        _joints = new Joint[Stems.Length * Segments.Length];
        if (_skel == null) { _valid = false; return; }

        string side = left ? "left" : "right";

        int indexKnuckle = avatar.BoneOf($"{side}IndexProximal");
        int littleKnuckle = avatar.BoneOf($"{side}LittleProximal");
        int middleKnuckle = avatar.BoneOf($"{side}MiddleProximal");
        int middleTip = avatar.BoneOf($"{side}MiddleDistal");

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

        float sign = Mathf.Sign(knuckleAxis.Cross(fingerDir).Dot(Vector3.Down));
        if (sign == 0f) sign = 1f;
        var flexSkeleton = knuckleAxis * sign;
        var palmNormalSkeleton = (flexSkeleton.Cross(fingerDir)).Normalized();

        for (int f = 0; f < Stems.Length; f++)
        {
            var degrees = f == (int)Finger.Thumb ? ThumbSegmentDeg : FingerSegmentDeg;
            float convRad = Mathf.DegToRad(ConvergenceDeg[f]) * (_isLeft ? -1f : 1f);

            for (int s = 0; s < Segments.Length; s++)
            {
                int bone = avatar.BoneOf($"{side}{Stems[f]}{Segments[s]}");
                if (bone < 0) { _joints[f * Segments.Length + s] = default; continue; }

                int parent = _skel.GetBoneParent(bone);
                var parentRest = parent >= 0
                    ? _skel.GetBoneGlobalRest(parent).Basis.GetRotationQuaternion()
                    : Quaternion.Identity;

                var flexParent = (parentRest.Inverse() * flexSkeleton).Normalized();
                var splayParent = (parentRest.Inverse() * palmNormalSkeleton).Normalized();

                _joints[f * Segments.Length + s] = new Joint(
                    bone,
                    _skel.GetBoneRest(bone).Basis.GetRotationQuaternion(),
                    flexParent,
                    splayParent,
                    Mathf.DegToRad(degrees[s]),
                    s == 0 ? convRad : 0f);
            }
        }

        _valid = System.Array.Exists(_joints, j => j.Ok);
    }

    /// Write `curl` (five floats, 0–1, indexed by `Finger`) onto the skeleton with natural convergence.
    public void Apply(float[] curl) => Apply(curl, null, 0f);

    /// Write `curl`, optional `splay` angles, and `thumbOpposition` onto the skeleton.
    public void Apply(float[] curl, float[] splay, float thumbOpposition = 0f)
    {
        if (!_valid || curl == null) return;

        for (int f = 0; f < Stems.Length && f < curl.Length; f++)
        {
            float amount = Mathf.Clamp(curl[f], 0f, 1f);
            float userSplay = (splay != null && f < splay.Length) ? splay[f] : 0f;

            for (int s = 0; s < Segments.Length; s++)
            {
                var j = _joints[f * Segments.Length + s];
                if (!j.Ok) continue;

                // Base flexion
                var bend = new Quaternion(j.FlexAxis, j.FullAngle * amount);

                // Anatomical knuckle convergence & splay on proximal joints
                if (s == 0)
                {
                    float totalSplay = userSplay + j.ConvergenceRad * amount;
                    if (f == (int)Finger.Thumb && thumbOpposition > 0.05f)
                    {
                        totalSplay += thumbOpposition * 0.35f * (_isLeft ? 1f : -1f);
                    }

                    if (Mathf.Abs(totalSplay) > 1e-4f)
                    {
                        var splayRot = new Quaternion(j.SplayAxis, totalSplay);
                        bend = splayRot * bend;
                    }
                }

                _skel.SetBonePoseRotation(j.Bone, (j.RestLocal * bend).Normalized());
            }
        }
    }
}
