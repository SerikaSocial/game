using System;
using System.Collections.Generic;
using System.Linq;
using Serika.Net.Codec;
using SerikaSocial.Avatar;
using Xunit;

namespace Serika.Net.Codec.Tests;

/// The canonical bone order is as much a part of the wire contract as the byte layout.
///
/// The relay decodes a pose frame only to validate it and to pull the root position for AOI; it
/// forwards the payload opaquely and never learns what any bone *is*. Slot 30 means
/// `leftIndexDistal` purely because both ends of the wire are this client and both read
/// `HumanoidBones.Full`. So the golden corpus cannot catch a reordering here — every byte would
/// still match — and the symptom would be a remote avatar whose fingers are wired to its eyes.
///
/// Hence a frozen test rather than a derived one: the expected names are written out by hand so
/// that editing the table fails here loudly, instead of shipping and mangling every avatar in
/// the field.
public class BoneOrderTests
{
    /// Indices 0-21, frozen. These have shipped; a LOD1 frame is exactly this prefix.
    private static readonly string[] FrozenLod1 =
    {
        "hips", "spine", "chest", "upperChest", "neck", "head",
        "leftUpperLeg", "leftLowerLeg", "leftFoot",
        "rightUpperLeg", "rightLowerLeg", "rightFoot",
        "leftShoulder", "leftUpperArm", "leftLowerArm", "leftHand",
        "rightShoulder", "rightUpperArm", "rightLowerArm", "rightHand",
        "leftToes", "rightToes",
    };

    [Fact]
    public void Lod1PrefixIsFrozen()
    {
        Assert.Equal(FrozenLod1.Length, HumanoidBones.Lod1.Length);
        Assert.Equal(FrozenLod1, HumanoidBones.Lod1);
        // ...and the same names must occupy the same slots in the full table, or a LOD0 frame
        // would disagree with a LOD1 frame about what bone 13 is.
        Assert.Equal(FrozenLod1, HumanoidBones.Full.Take(FrozenLod1.Length).ToArray());
    }

    [Fact]
    public void TableSizesMatchTheCodec()
    {
        Assert.Equal(LodExt.HumanoidBoneCount, HumanoidBones.Full.Length);
        Assert.Equal(LodExt.Lod1BoneCount, HumanoidBones.Lod1.Length);
        Assert.Equal(55, HumanoidBones.Full.Length);
        Assert.Equal(22, HumanoidBones.Lod1.Length);
    }

    [Fact]
    public void NamesAreUnique()
    {
        // A duplicated name is not a compile error and not a decode error. It silently makes two
        // wire slots drive the same bone, so one of them is dead and whichever is written last
        // wins — a limb that mysteriously ignores the network.
        var dupes = HumanoidBones.Full.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key);
        Assert.Empty(dupes);
        Assert.All(HumanoidBones.Full, n => Assert.False(string.IsNullOrWhiteSpace(n)));
    }

    [Fact]
    public void FingerBlockIsWhereTheSpecSaysItIs()
    {
        Assert.Equal(25, HumanoidBones.FirstFingerIndex);
        // pose_codec.md pins the last slot by name.
        Assert.Equal("rightLittleDistal", HumanoidBones.Full[54]);

        // 2 hands × 5 fingers × 3 segments.
        int fingerCount = HumanoidBones.Full.Length - HumanoidBones.FirstFingerIndex;
        Assert.Equal(30, fingerCount);

        // Everything at or past the boundary is a finger, and nothing before it is.
        string[] digits = { "Thumb", "Index", "Middle", "Ring", "Little" };
        for (int i = 0; i < HumanoidBones.Full.Length; i++)
        {
            bool isFinger = digits.Any(d => HumanoidBones.Full[i].Contains(d, StringComparison.Ordinal));
            Assert.Equal(i >= HumanoidBones.FirstFingerIndex, isFinger);
        }
    }

    [Fact]
    public void FingerSegmentsAreOrderedProximalToDistal()
    {
        string[] order = { "Proximal", "Intermediate", "Distal" };
        for (int i = HumanoidBones.FirstFingerIndex; i < HumanoidBones.Full.Length; i++)
        {
            int seg = (i - HumanoidBones.FirstFingerIndex) % 3;
            Assert.EndsWith(order[seg], HumanoidBones.Full[i], StringComparison.Ordinal);
        }

        // Left hand first, then right — 15 slots each.
        for (int i = HumanoidBones.FirstFingerIndex; i < HumanoidBones.FirstFingerIndex + 15; i++)
            Assert.StartsWith("left", HumanoidBones.Full[i], StringComparison.Ordinal);
        for (int i = HumanoidBones.FirstFingerIndex + 15; i < HumanoidBones.Full.Length; i++)
            Assert.StartsWith("right", HumanoidBones.Full[i], StringComparison.Ordinal);
    }

    /// A LOD0 frame must carry a distinct rotation per bone all the way to slot 54 — the point of
    /// the whole exercise being that a finger rotation survives the trip.
    [Fact]
    public void Lod0RoundTripsEveryBoneIncludingFingers()
    {
        var bones = new List<Quat>();
        for (int i = 0; i < HumanoidBones.Full.Length; i++)
        {
            // A distinct, non-identity rotation per slot, so a transposition shows up as a
            // mismatch rather than being masked by every bone holding the same value.
            double a = 0.017 * (i + 1);
            bones.Add(new Quat(0f, (float)Math.Sin(a / 2), 0f, (float)Math.Cos(a / 2)));
        }

        var frame = new PoseFrame
        {
            Lod = Lod.Full,
            Sequence = 200,
            RootPos = new[] { 1.5f, 0.25f, -3f },
            RootRot = Quat.Identity,
            Bones = bones,
        };

        var encoded = frame.Encode();
        Assert.Equal(LodExt.FrameLen(Lod.Full), encoded.Length);
        Assert.Equal(232, encoded.Length);

        var back = PoseFrame.Decode(encoded);
        Assert.Equal(Lod.Full, back.Lod);
        Assert.Equal(HumanoidBones.Full.Length, back.Bones.Count);

        for (int i = 0; i < bones.Count; i++)
        {
            // Quantization is ~0.14° worst case; compare the rotations, not the raw components,
            // and do it via the error vector rather than acos(dot) — near-identical quaternions
            // sit where acos has no precision left (see the note in pose_codec.md).
            var e = bones[i];
            var g = back.Bones[i];
            double dot = e.X * g.X + e.Y * g.Y + e.Z * g.Z + e.W * g.W;
            if (dot < 0) { g = new Quat(-g.X, -g.Y, -g.Z, -g.W); }
            double dx = e.X - g.X, dy = e.Y - g.Y, dz = e.Z - g.Z, dw = e.W - g.W;
            double err = Math.Sqrt(dx * dx + dy * dy + dz * dz + dw * dw);
            Assert.True(err < 0.005, $"bone {i} ({HumanoidBones.Full[i]}) drifted {err:F5}");
        }
    }

    /// A body frame from an older peer must leave a receiver's fingers alone rather than being
    /// misread as a short LOD0 frame.
    [Fact]
    public void Lod1FrameCarriesOnlyTheBodyPrefix()
    {
        var frame = new PoseFrame
        {
            Lod = Lod.Body,
            Sequence = 7,
            RootPos = new[] { 0f, 0f, 0f },
            RootRot = Quat.Identity,
            Bones = Enumerable.Repeat(Quat.Identity, HumanoidBones.Full.Length).ToList(),
        };

        // Handed 55 bones but told LOD1: the encoder must truncate to the 22-bone prefix, not
        // overrun the frame.
        var encoded = frame.Encode();
        Assert.Equal(100, encoded.Length);

        var back = PoseFrame.Decode(encoded);
        Assert.Equal(HumanoidBones.Lod1.Length, back.Bones.Count);
    }
}
