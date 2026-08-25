using System;
using Godot;

namespace SerikaSocial.World;

/// Packs and unpacks stroke point data into the existing ObjectSync 42-byte layout.
///
/// No proto or Rust changes: the relay fans these out as opaque ObjectSync packets. The client
/// routes by <c>NetId</c> range — ids ≥ 0x8000 are stroke segments, not physics props.
///
/// Field mapping (reusing the ObjectSync floats):
/// <list type="bullet">
/// <item><c>pos</c> (x,y,z) — stroke point world position</item>
/// <item><c>qx</c> — stroke id (stored as float bits, recovered via BitConverter)</item>
/// <item><c>qy</c> — point index within the stroke (stored as float bits)</item>
/// <item><c>qz</c> — colour hue (0–1, direct float)</item>
/// <item><c>qw</c> — pen radius in metres (direct float)</item>
/// <item><c>lvx</c> — flags: 1 = stroke start, 0 = continuation, -1 = stroke end</item>
/// <item><c>lvy</c>, <c>lvz</c> — unused (0)</item>
/// </list>
public static class StrokeNetwork
{
    public const float FlagStart = 1f;
    public const float FlagContinue = 0f;
    public const float FlagEnd = -1f;

    /// Pack a stroke point into the ObjectSync field tuple.
    public static (float x, float y, float z,
        float qx, float qy, float qz, float qw,
        float lvx, float lvy, float lvz) Pack(
            ushort strokeId, uint pointIndex, Vector3 pos,
            float hue, float radius, float flag)
    {
        return (
            pos.X, pos.Y, pos.Z,
            BitConverter.UInt32BitsToSingle(strokeId),
            BitConverter.UInt32BitsToSingle(pointIndex),
            hue, radius,
            flag, 0f, 0f);
    }

    /// Unpack a stroke point from the ObjectSync field tuple.
    public static bool TryUnpack(
        float x, float y, float z,
        float qx, float qy, float qz, float qw,
        float lvx, float lvy, float lvz,
        out ushort strokeId, out uint pointIndex, out Vector3 pos,
        out float hue, out float radius, out float flag)
    {
        strokeId = (ushort)BitConverter.SingleToUInt32Bits(qx);
        pointIndex = BitConverter.SingleToUInt32Bits(qy);
        pos = new Vector3(x, y, z);
        hue = qz;
        radius = qw;
        flag = lvx;
        return true;
    }
}

/// A single stroke point as used by <see cref="StrokeCanvas"/>.
public readonly struct StrokePoint
{
    public readonly ushort StrokeId;
    public readonly uint Index;
    public readonly Vector3 Position;
    public readonly float Hue;
    public readonly float Radius;
    /// 1 = start, 0 = continuation, -1 = end.
    public readonly float Flag;

    public StrokePoint(ushort strokeId, uint index, Vector3 position, float hue, float radius, float flag)
    {
        StrokeId = strokeId;
        Index = index;
        Position = position;
        Hue = hue;
        Radius = radius;
        Flag = flag;
    }

    public bool IsStart => Flag == StrokeNetwork.FlagStart;
    public bool IsEnd => Flag == StrokeNetwork.FlagEnd;
}
