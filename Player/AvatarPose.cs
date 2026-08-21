using Godot;
using Serika.Net.Codec;

namespace SerikaSocial.Player;

/// Bridges Godot's transform types and the wire PoseFrame. M1 avatars are a capsule, so we
/// only carry root position + rotation (LOD Body with identity bones). When the humanoid rig
/// lands in M2, this is where muscle-space bone rotations get filled in.
public static class AvatarPose
{
    public static PoseFrame FromTransform(Transform3D xf, byte sequence)
    {
        var q = xf.Basis.GetRotationQuaternion();
        return new PoseFrame
        {
            Lod = Lod.Body,
            Sequence = sequence,
            RootPos = new[] { xf.Origin.X, xf.Origin.Y, xf.Origin.Z },
            RootRot = new Quat(q.X, q.Y, q.Z, q.W),
            // Identity bones until there's a rig to drive them.
            Bones = new System.Collections.Generic.List<Quat>(new Quat[Lod.Body.BoneCount()]),
        };
    }

    public static (Vector3 pos, Quaternion rot) ToTransform(PoseFrame f)
    {
        var pos = new Vector3(f.RootPos[0], f.RootPos[1], f.RootPos[2]);
        var rot = new Quaternion(f.RootRot.X, f.RootRot.Y, f.RootRot.Z, f.RootRot.W).Normalized();
        return (pos, rot);
    }
}
