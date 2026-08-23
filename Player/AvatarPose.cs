using System.Collections.Generic;
using Godot;
using Serika.Net.Codec;
using SerikaSocial.Avatar;

namespace SerikaSocial.Player;

/// Bridges Godot's transform types and the wire PoseFrame: root position + rotation plus the
/// 22 LOD1 humanoid bone rotations in `HumanoidBones.Lod1` order.
///
/// Streaming the real bone pose (rather than the identity bones this used to send) is what
/// lets peers see each other's crouch, emotes and locomotion — the receiver replays the
/// sender's actual rig instead of guessing an animation from observed velocity.
public static class AvatarPose
{
    // Reused across frames — pose encoding runs at 20 Hz per avatar and the client's hot
    // paths must stay allocation-free (Variant boxing puts the GC in the frame-time path).
    private static readonly Quat[] Scratch = new Quat[LodExt.Lod1BoneCount];
    private static readonly List<Quat> ScratchList = new(LodExt.Lod1BoneCount);

    public static PoseFrame FromTransform(Transform3D xf, byte sequence, AvatarInstance avatar = null)
    {
        var q = xf.Basis.GetRotationQuaternion();

        for (int i = 0; i < Scratch.Length; i++) Scratch[i] = Quat.Identity;
        avatar?.CaptureBonePose(Scratch);

        ScratchList.Clear();
        for (int i = 0; i < Scratch.Length; i++) ScratchList.Add(Scratch[i]);

        return new PoseFrame
        {
            Lod = Lod.Body,
            Sequence = sequence,
            RootPos = new[] { xf.Origin.X, xf.Origin.Y, xf.Origin.Z },
            RootRot = new Quat(q.X, q.Y, q.Z, q.W),
            Bones = new List<Quat>(ScratchList),
        };
    }

    public static (Vector3 pos, Quaternion rot) ToTransform(PoseFrame f)
    {
        var pos = new Vector3(f.RootPos[0], f.RootPos[1], f.RootPos[2]);
        var rot = new Quaternion(f.RootRot.X, f.RootRot.Y, f.RootRot.Z, f.RootRot.W).Normalized();
        return (pos, rot);
    }
}
