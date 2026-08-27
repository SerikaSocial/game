using System.Collections.Generic;
using Godot;
using Serika.Net.Codec;
using SerikaSocial.Avatar;

namespace SerikaSocial.Player;

/// Bridges Godot's transform types and the wire PoseFrame: root position + rotation plus the
/// humanoid bone rotations, in `HumanoidBones.Full` order.
///
/// Streaming the real bone pose (rather than the identity bones this used to send) is what
/// lets peers see each other's crouch, emotes and locomotion — the receiver replays the
/// sender's actual rig instead of guessing an animation from observed velocity.
///
/// Two LODs are emitted. `Lod.Body` carries the 22 body bones and is what goes out almost all
/// the time. `Lod.Full` carries all 55 — eyes, jaw and both hands' fingers — and is what makes
/// VR hand gestures visible to other people. It costs 232 bytes a frame against 100, so the
/// caller only asks for it when somebody is close enough to actually read a hand.
public static class AvatarPose
{
    // Reused across frames — pose encoding runs at 20 Hz per avatar and the client's hot
    // paths must stay allocation-free (Variant boxing puts the GC in the frame-time path).
    //
    // One scratch buffer sized for the larger LOD, sliced down for the smaller. `CaptureBonePose`
    // takes the LOD from the array length it is handed, so the slice is what selects the payload.
    private static readonly Quat[] ScratchFull = new Quat[LodExt.HumanoidBoneCount];
    private static readonly Quat[] ScratchBody = new Quat[LodExt.Lod1BoneCount];
    private static readonly List<Quat> ScratchList = new(LodExt.HumanoidBoneCount);

    public static PoseFrame FromTransform(Transform3D xf, byte sequence,
                                          AvatarInstance avatar = null, Lod lod = Lod.Body)
    {
        var q = xf.Basis.GetRotationQuaternion();

        var scratch = lod == Lod.Full ? ScratchFull : ScratchBody;
        for (int i = 0; i < scratch.Length; i++) scratch[i] = Quat.Identity;
        avatar?.CaptureBonePose(scratch);

        // Reused rather than reallocated: `UdpTransport.SendPose` encodes the frame synchronously
        // and keeps nothing, so the list never outlives this call.
        ScratchList.Clear();
        for (int i = 0; i < scratch.Length; i++) ScratchList.Add(scratch[i]);

        return new PoseFrame
        {
            Lod = lod,
            Sequence = sequence,
            RootPos = new[] { xf.Origin.X, xf.Origin.Y, xf.Origin.Z },
            RootRot = new Quat(q.X, q.Y, q.Z, q.W),
            Bones = ScratchList,
        };
    }

    public static (Vector3 pos, Quaternion rot) ToTransform(PoseFrame f)
    {
        var pos = new Vector3(f.RootPos[0], f.RootPos[1], f.RootPos[2]);
        var rot = new Quaternion(f.RootRot.X, f.RootRot.Y, f.RootRot.Z, f.RootRot.W).Normalized();
        return (pos, rot);
    }
}
