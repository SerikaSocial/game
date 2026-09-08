using System;

namespace Serika.Net;

/// Byte layouts for the two object-related relay messages, ObjectSync (0x0A) and PhysGrab (0x0B).
///
/// These were previously hand-rolled separately inside `UdpTransport` and `WebRtcTransport`, which
/// is how the two drifted: the relay strips PhysGrab's `target_peer` and re-frames with a sender id,
/// but a P2P data channel has no relay, so the same bytes mean different things on each path. That
/// difference is real and deliberate — it is captured here in one place, next to the code that
/// depends on it, rather than being rediscovered from two divergent parsers.
///
/// Godot-free on purpose so `Net/Codec/Tests` can round-trip it without the engine, the same way
/// `PoseFrame` and `VoiceFrame` are tested.
public static class ObjectFrames
{
    /// `[obj_id:u16][pos:3×f32][rot:4×f32][vel:3×f32]` = 42 bytes of content.
    ///
    /// The buffer is 44 because that is what the relay length-checks against
    /// (`server/instanced/src/server.rs`, `const EXPECTED: usize = 44`) — the trailing two bytes are
    /// padding and are never read. Note the relay checks `<`, not `==`, and copies the body verbatim,
    /// so these bytes are opaque to it.
    public const int ObjectSyncSize = 44;

    /// `[grab_type:u8][target_peer:u32][bone_or_obj:u16][x,y,z:f32]`, what a client sends.
    public const int PhysGrabSendSize = 19;

    /// What the relay fans back out, having dropped `target_peer` and prefixed the sender id
    /// separately: `[grab_type:u8][bone_or_obj:u16][x,y,z:f32]`.
    public const int PhysGrabRelayedSize = 15;

    public static byte[] WriteObjectSync(
        ushort objId, float x, float y, float z,
        float qx, float qy, float qz, float qw,
        float lvx, float lvy, float lvz)
    {
        var buf = new byte[ObjectSyncSize];
        int o = 0;
        WriteU16(buf, ref o, objId);
        WriteF32(buf, ref o, x); WriteF32(buf, ref o, y); WriteF32(buf, ref o, z);
        WriteF32(buf, ref o, qx); WriteF32(buf, ref o, qy); WriteF32(buf, ref o, qz); WriteF32(buf, ref o, qw);
        WriteF32(buf, ref o, lvx); WriteF32(buf, ref o, lvy); WriteF32(buf, ref o, lvz);
        return buf;
    }

    /// Read an ObjectSync body. `body` must start at the payload, with any relay envelope already
    /// skipped by the caller — that offset differs per transport and is the caller's business.
    public static bool ReadObjectSync(
        ReadOnlySpan<byte> body, out ushort objId,
        out float x, out float y, out float z,
        out float qx, out float qy, out float qz, out float qw,
        out float lvx, out float lvy, out float lvz)
    {
        objId = 0; x = y = z = 0; qx = qy = qz = 0; qw = 1; lvx = lvy = lvz = 0;
        if (body.Length < ObjectSyncSize) return false;
        int o = 0;
        objId = ReadU16(body, ref o);
        x = ReadF32(body, ref o); y = ReadF32(body, ref o); z = ReadF32(body, ref o);
        qx = ReadF32(body, ref o); qy = ReadF32(body, ref o); qz = ReadF32(body, ref o); qw = ReadF32(body, ref o);
        lvx = ReadF32(body, ref o); lvy = ReadF32(body, ref o); lvz = ReadF32(body, ref o);
        return true;
    }

    public static byte[] WritePhysGrab(byte grabType, uint targetPeer, ushort boneOrObjId, float x, float y, float z)
    {
        // grabType convention: 0=take, 1=update, 2=release for PHYSICS PROPS (payload =
        // prop NetId). Values with the top bit set (0x80 | 0/1/2) are HAIR/physbone grabs
        // (payload = spring chain index). The two payloads share one 16-bit id space with no
        // other discriminator, so receivers route on this bit — see Main.OnPhysGrabReceived.
        var buf = new byte[PhysGrabSendSize];
        int o = 0;
        buf[o++] = grabType;
        WriteU32(buf, ref o, targetPeer);
        WriteU16(buf, ref o, boneOrObjId);
        WriteF32(buf, ref o, x); WriteF32(buf, ref o, y); WriteF32(buf, ref o, z);
        return buf;
    }

    /// Read a PhysGrab body.
    ///
    /// `hasTargetPeer` selects which of the two layouts this is, and it is not cosmetic: get it
    /// wrong and every field after the first byte is read from the wrong offset, silently. Pass
    /// false for relay traffic (the server already removed the field) and true for a direct P2P
    /// channel (nothing removed it).
    public static bool ReadPhysGrab(
        ReadOnlySpan<byte> body, bool hasTargetPeer,
        out byte grabType, out ushort boneOrObjId, out float x, out float y, out float z)
    {
        grabType = 0; boneOrObjId = 0; x = y = z = 0;
        int need = hasTargetPeer ? PhysGrabSendSize : PhysGrabRelayedSize;
        if (body.Length < need) return false;
        int o = 0;
        grabType = body[o++];
        if (hasTargetPeer) o += 4;
        boneOrObjId = ReadU16(body, ref o);
        x = ReadF32(body, ref o); y = ReadF32(body, ref o); z = ReadF32(body, ref o);
        return true;
    }

    private static void WriteU16(byte[] b, ref int o, ushort v)
    { b[o++] = (byte)(v & 0xFF); b[o++] = (byte)(v >> 8); }

    private static void WriteU32(byte[] b, ref int o, uint v)
    { b[o++] = (byte)v; b[o++] = (byte)(v >> 8); b[o++] = (byte)(v >> 16); b[o++] = (byte)(v >> 24); }

    private static void WriteF32(byte[] b, ref int o, float v)
    {
        var bits = BitConverter.SingleToUInt32Bits(v);
        b[o++] = (byte)bits; b[o++] = (byte)(bits >> 8); b[o++] = (byte)(bits >> 16); b[o++] = (byte)(bits >> 24);
    }

    private static ushort ReadU16(ReadOnlySpan<byte> b, ref int o)
    { ushort v = (ushort)(b[o] | (b[o + 1] << 8)); o += 2; return v; }

    private static float ReadF32(ReadOnlySpan<byte> b, ref int o)
    {
        uint bits = (uint)b[o] | ((uint)b[o + 1] << 8) | ((uint)b[o + 2] << 16) | ((uint)b[o + 3] << 24);
        o += 4;
        return BitConverter.UInt32BitsToSingle(bits);
    }
}
