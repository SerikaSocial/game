namespace SerikaSocial.Avatar;

/// The canonical wire order for humanoid bone rotations in a `PoseFrame`.
///
/// `proto/pose_codec.md` states the order "matches the VRM 1.0 humanoid bone set" and that
/// indices 0-21 are the LOD1 body bones. Its printed table is garbled — the column layout
/// drops RightLowerLeg, LeftShoulder and LeftUpperArm while listing eyes/jaw — so this array
/// is the authority the client encodes and decodes against. Both ends of the wire are this
/// client (the relay forwards pose payloads opaquely), so sender and receiver agreeing here
/// is what matters; the Rust codec only ever sees "N quaternions".
///
/// The order is fixed forever — inserting a bone is a v2 codec change, not a patch.
public static class HumanoidBones
{
    /// Indices 0-21: the body bones sent at LOD1 (Lod.Body). Names are the humanoid role keys
    /// used by `AvatarInstance.BoneOf`, which come from the `.ska`'s VRM humanoid mapping.
    public static readonly string[] Lod1 =
    {
        "hips",           //  0
        "spine",          //  1
        "chest",          //  2
        "upperChest",     //  3
        "neck",           //  4
        "head",           //  5
        "leftUpperLeg",   //  6
        "leftLowerLeg",   //  7
        "leftFoot",       //  8
        "rightUpperLeg",  //  9
        "rightLowerLeg",  // 10
        "rightFoot",      // 11
        "leftShoulder",   // 12
        "leftUpperArm",   // 13
        "leftLowerArm",   // 14
        "leftHand",       // 15
        "rightShoulder",  // 16
        "rightUpperArm",  // 17
        "rightLowerArm",  // 18
        "rightHand",      // 19
        "leftToes",       // 20
        "rightToes",      // 21
    };
}
