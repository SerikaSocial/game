namespace SerikaSocial.Avatar;

/// The canonical wire order for humanoid bone rotations in a `PoseFrame`.
///
/// `proto/pose_codec.md` states the order "matches the VRM 1.0 humanoid bone set" and that
/// indices 0-21 are the LOD1 body bones. Its printed table was garbled — the column layout
/// dropped RightLowerLeg, LeftShoulder and LeftUpperArm while listing eyes/jaw among the first
/// 22 — so this file is the authority the client encodes and decodes against. Both ends of the
/// wire are this client (the relay forwards pose payloads opaquely and only ever sees "N
/// quaternions"), so sender and receiver agreeing here is what matters. The document has since
/// been corrected to match this file rather than the other way round.
///
/// The order is fixed forever — inserting a bone is a v2 codec change, not a patch.
public static class HumanoidBones
{
    /// All 55 bones, in wire order. This is the LOD0 payload.
    ///
    /// 25 body bones + 30 finger bones = 55, which is exactly the VRM 1.0 humanoid set, so a
    /// `godot-vrm` import maps onto it without a translation table.
    ///
    /// **Indices 0-21 are frozen as the LOD1 prefix.** Eyes and jaw sit at 22-24 rather than
    /// among the body bones for that reason alone: `Lod1` has shipped, and a LOD1 frame is
    /// simply the first 22 entries of this array. Moving anything below index 22 to make the
    /// grouping prettier would silently reinterpret every pose frame in the field.
    public static readonly string[] Full =
    {
        // ── 0-21: the LOD1 body prefix. Do not reorder. ──────────────────────────────
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

        // ── 22-24: the remaining body bones, LOD0 only. ──────────────────────────────
        "leftEye",        // 22
        "rightEye",       // 23
        "jaw",            // 24

        // ── 25-54: fingers, proximal → intermediate → distal, left hand then right. ──
        "leftThumbProximal",     // 25
        "leftThumbIntermediate", // 26
        "leftThumbDistal",       // 27
        "leftIndexProximal",     // 28
        "leftIndexIntermediate", // 29
        "leftIndexDistal",       // 30
        "leftMiddleProximal",     // 31
        "leftMiddleIntermediate", // 32
        "leftMiddleDistal",       // 33
        "leftRingProximal",      // 34
        "leftRingIntermediate",  // 35
        "leftRingDistal",        // 36
        "leftLittleProximal",    // 37
        "leftLittleIntermediate",// 38
        "leftLittleDistal",      // 39
        "rightThumbProximal",     // 40
        "rightThumbIntermediate", // 41
        "rightThumbDistal",       // 42
        "rightIndexProximal",     // 43
        "rightIndexIntermediate", // 44
        "rightIndexDistal",       // 45
        "rightMiddleProximal",     // 46
        "rightMiddleIntermediate", // 47
        "rightMiddleDistal",       // 48
        "rightRingProximal",      // 49
        "rightRingIntermediate",  // 50
        "rightRingDistal",        // 51
        "rightLittleProximal",    // 52
        "rightLittleIntermediate",// 53
        "rightLittleDistal",      // 54
    };

    /// The first index carrying a finger. Everything from here up is dropped at LOD1.
    public const int FirstFingerIndex = 25;

    /// Indices 0-21: the body bones sent at LOD1 (`Lod.Body`). Names are the humanoid role keys
    /// used by `AvatarInstance.BoneOf`, which come from the `.ska`'s VRM humanoid mapping.
    ///
    /// Sliced from `Full` rather than written out a second time. Two hand-maintained lists that
    /// must agree on their overlap is a standing invitation for them to stop agreeing, and the
    /// failure mode — a LOD1 frame whose bone 13 means something different to sender and
    /// receiver — is a silently mangled avatar, not an error.
    public static readonly string[] Lod1 = Full[..22];
}
