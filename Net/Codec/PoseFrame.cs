using System;
using System.Collections.Generic;

namespace Serika.Net.Codec;

/// Avatar pose encoding. The normative spec is proto/pose_codec.md; this must produce
/// byte-identical output to the Rust implementation in proto/rust/src/pose.rs. GoldenTests
/// asserts exactly that against proto/golden/vectors.json.
///
/// Pure C# — deliberately no Godot types — so it can be tested without the engine and reused
/// anywhere. The Godot avatar layer converts to/from Godot's Quaternion/Vector3 at the edges.
public enum Lod : byte
{
    /// Full body + fingers — the nearest handful of avatars.
    Full = 0,
    /// Body only, fingers dropped.
    Body = 1,
    /// Head rotation + hand positions; the receiver IKs the rest.
    Distant = 2,
}

public static class LodExt
{
    public const int HumanoidBoneCount = 55;
    public const int Lod1BoneCount = 22;

    public static int BoneCount(this Lod lod) => lod switch
    {
        Lod.Full => HumanoidBoneCount,
        Lod.Body => Lod1BoneCount,
        Lod.Distant => 1, // the single head rotation
        _ => throw new CodecException($"unknown LOD {(byte)lod}"),
    };

    public static int FrameLen(this Lod lod) => lod switch
    {
        Lod.Full => Header + HumanoidBoneCount * 4,
        Lod.Body => Header + Lod1BoneCount * 4,
        Lod.Distant => Header + 4 + 12, // head rot + two hand positions
        _ => throw new CodecException($"unknown LOD {(byte)lod}"),
    };

    internal const int Header = 12;
}

public struct Quat
{
    public float X, Y, Z, W;
    public Quat(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
    public static readonly Quat Identity = new(0, 0, 0, 1);
    public float this[int i] => i switch { 0 => X, 1 => Y, 2 => Z, 3 => W, _ => throw new IndexOutOfRangeException() };
}

public sealed class PoseFrame
{
    public Lod Lod;
    public byte Sequence;
    public float[] RootPos = new float[3];
    public Quat RootRot = Quat.Identity;
    /// LodExt.BoneCount(Lod) rotations in canonical order; at Distant this is the head.
    public List<Quat> Bones = new();
    /// Left/right hand positions; only meaningful at Distant.
    public float[][] Hands = { new float[3], new float[3] };

    // ── Quantization constants (must match pose.rs) ──────────────────────────────────
    private const double PosRange = 256.0;
    private const double PosSpan = PosRange * 2.0;
    private const double U16Max = 65535.0;
    private static readonly double QuatScale = Math.Sqrt(2.0);
    // 1022, not 1023 — an odd code count puts zero exactly on a code so identity round-trips.
    private const double QuatMax = 1022.0;

    public byte[] Encode()
    {
        var len = LodExt.FrameLen(Lod);
        var buf = new byte[len];
        int o = 0;

        buf[o++] = (byte)Lod;
        buf[o++] = Sequence;
        for (int i = 0; i < 3; i++) { WriteU16(buf, ref o, QuantizePos(RootPos[i])); }
        WriteU32(buf, ref o, PackQuat(RootRot));

        switch (Lod)
        {
            case Lod.Full:
            case Lod.Body:
                int n = LodExt.BoneCount(Lod);
                for (int i = 0; i < n; i++)
                {
                    var q = i < Bones.Count ? Bones[i] : Quat.Identity; // pad, never truncate
                    WriteU32(buf, ref o, PackQuat(q));
                }
                break;
            case Lod.Distant:
                var head = Bones.Count > 0 ? Bones[0] : Quat.Identity;
                WriteU32(buf, ref o, PackQuat(head));
                for (int h = 0; h < 2; h++)
                    for (int a = 0; a < 3; a++)
                        WriteU16(buf, ref o, QuantizePos(Hands[h][a]));
                break;
        }
        return buf;
    }

    public static PoseFrame Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < LodExt.Header)
            throw new CodecException($"frame too short: need {LodExt.Header}, got {buf.Length}");

        byte flags = buf[0];
        if ((flags & 0xFC) != 0)
            throw new CodecException($"reserved flag bits set (0x{flags & 0xFC:x2})");
        var lod = (flags & 0x03) switch
        {
            0 => Lod.Full,
            1 => Lod.Body,
            2 => Lod.Distant,
            _ => throw new CodecException($"unknown LOD {flags & 0x03}"),
        };

        int want = LodExt.FrameLen(lod);
        if (buf.Length < want) throw new CodecException($"frame too short: need {want}, got {buf.Length}");
        if (buf.Length > want) throw new CodecException($"trailing bytes: {buf.Length - want}");

        var f = new PoseFrame { Lod = lod, Sequence = buf[1] };
        int o = 2;
        for (int i = 0; i < 3; i++) f.RootPos[i] = DequantizePos(ReadU16(buf, ref o));
        f.RootRot = UnpackQuat(ReadU32(buf, ref o));

        f.Bones = new List<Quat>(lod.BoneCount());
        switch (lod)
        {
            case Lod.Full:
            case Lod.Body:
                for (int i = 0; i < lod.BoneCount(); i++) f.Bones.Add(UnpackQuat(ReadU32(buf, ref o)));
                break;
            case Lod.Distant:
                f.Bones.Add(UnpackQuat(ReadU32(buf, ref o)));
                for (int h = 0; h < 2; h++)
                    for (int a = 0; a < 3; a++)
                        f.Hands[h][a] = DequantizePos(ReadU16(buf, ref o));
                break;
        }
        return f;
    }

    // ── Scalar quantizers ────────────────────────────────────────────────────────────

    public static ushort QuantizePos(float v)
    {
        double clamped = Math.Clamp((double)v, -PosRange, PosRange);
        double norm = (clamped + PosRange) / PosSpan;
        // Round half away from zero to match Rust's f64::round exactly.
        return (ushort)Math.Round(norm * U16Max, MidpointRounding.AwayFromZero);
    }

    public static float DequantizePos(ushort q) => (float)((q / U16Max) * PosSpan - PosRange);

    public static uint PackQuat(Quat q)
    {
        double[] c = { q.X, q.Y, q.Z, q.W };
        double len = Math.Sqrt(c[0] * c[0] + c[1] * c[1] + c[2] * c[2] + c[3] * c[3]);
        if (len > 0) { for (int i = 0; i < 4; i++) c[i] /= len; }
        else { c[0] = c[1] = c[2] = 0; c[3] = 1; }

        int largest = 0;
        for (int i = 1; i < 4; i++) if (Math.Abs(c[i]) > Math.Abs(c[largest])) largest = i;

        // q and -q are the same rotation; force the dropped component positive.
        if (c[largest] < 0) { for (int i = 0; i < 4; i++) c[i] = -c[i]; }

        uint word = (uint)largest << 30;
        int shift = 20;
        for (int i = 0; i < 4; i++)
        {
            if (i == largest) continue;
            double norm = (c[i] * QuatScale + 1.0) / 2.0;
            uint packed = (uint)Math.Clamp(Math.Round(norm * QuatMax, MidpointRounding.AwayFromZero), 0.0, QuatMax);
            word |= packed << shift;
            shift -= 10;
        }
        return word;
    }

    public static Quat UnpackQuat(uint word)
    {
        int largest = (int)(word >> 30);
        double[] c = new double[4];
        int shift = 20;
        double sumSq = 0;
        for (int i = 0; i < 4; i++)
        {
            if (i == largest) continue;
            double packed = (word >> shift) & 0x3FF;
            double v = (packed / QuatMax * 2.0 - 1.0) / QuatScale;
            c[i] = v;
            sumSq += v * v;
            shift -= 10;
        }
        // max(0,...) guards against rounding pushing sumSq past 1.0 → NaN.
        c[largest] = Math.Sqrt(Math.Max(0.0, 1.0 - sumSq));
        double len = Math.Sqrt(c[0] * c[0] + c[1] * c[1] + c[2] * c[2] + c[3] * c[3]);
        if (len > 0) { for (int i = 0; i < 4; i++) c[i] /= len; }
        return new Quat((float)c[0], (float)c[1], (float)c[2], (float)c[3]);
    }

    // ── LE byte helpers ──────────────────────────────────────────────────────────────
    private static void WriteU16(byte[] b, ref int o, ushort v) { b[o++] = (byte)(v & 0xFF); b[o++] = (byte)(v >> 8); }
    private static void WriteU32(byte[] b, ref int o, uint v)
    { b[o++] = (byte)(v & 0xFF); b[o++] = (byte)((v >> 8) & 0xFF); b[o++] = (byte)((v >> 16) & 0xFF); b[o++] = (byte)((v >> 24) & 0xFF); }
    private static ushort ReadU16(ReadOnlySpan<byte> b, ref int o) { ushort v = (ushort)(b[o] | (b[o + 1] << 8)); o += 2; return v; }
    private static uint ReadU32(ReadOnlySpan<byte> b, ref int o)
    { uint v = (uint)b[o] | ((uint)b[o + 1] << 8) | ((uint)b[o + 2] << 16) | ((uint)b[o + 3] << 24); o += 4; return v; }
}
