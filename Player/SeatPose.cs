using Godot;
using SerikaSocial.Avatar;

namespace SerikaSocial.Player;

internal static class SeatPose
{
    internal static Vector3 PinHips(AvatarInstance avatar, Vector3 anchor)
    {
        int hips = avatar?.BoneOf("hips") ?? -1;
        if (hips < 0 || avatar.Skeleton == null) return Vector3.Zero;
        return anchor - avatar.Skeleton.GlobalTransform * avatar.Skeleton.GetBoneGlobalPose(hips).Origin;
    }

    /// Pose packets carry rotations, not the seated clip's pelvis translation. Move the
    /// transmitted root by that translation so peers place the hips on the same chair.
    /// This uses the existing pose format and leaves standing/crouch networking unchanged.
    internal static Vector3 NetworkOrigin(AvatarInstance avatar, Vector3 origin)
    {
        int hips = avatar?.BoneOf("hips") ?? -1;
        if (hips < 0 || avatar.Skeleton == null) return origin;
        var skeleton = avatar.Skeleton;
        var delta = skeleton.GetBoneGlobalPose(hips).Origin - skeleton.GetBoneGlobalRest(hips).Origin;
        return origin + skeleton.GlobalBasis * delta;
    }
}
