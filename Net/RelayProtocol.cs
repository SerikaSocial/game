using System;
using System.Text;

namespace Serika.Net;

/// The relay's UDP envelope, mirroring server/instanced/src/protocol.rs. Every datagram is
/// [type:u8][payload]. Kept pure C# and Godot-free so the transport is testable outside the
/// engine.
public enum MsgType : byte
{
    Hello = 0x01,
    Welcome = 0x02,
    PeerJoin = 0x03,
    PeerLeave = 0x04,
    Pose = 0x05,
    Voice = 0x06,
    Ping = 0x07,
    Reject = 0x08,
    Chat = 0x09,
    /// client→server: [obj_id:u16][x:f32][y:f32][z:f32][qx:f32][qy:f32][qz:f32][qw:f32][lvx:f32][lvy:f32][lvz:f32]
    /// server→client: [peer_id:u32][obj_id:u16][x:f32][y:f32][z:f32][qx:f32][qy:f32][qz:f32][qw:f32][lvx:f32][lvy:f32][lvz:f32]
    /// Syncs a physics prop's transform + linear velocity. Ownership is implicit: whoever last
    /// sent an ObjectSync for a given obj_id owns it. Server fans out to all peers in AOI range.
    ObjectSync = 0x0A,
    /// client→server: [grab_type:u8][target_peer:u32][bone_or_obj_id:u16][x:f32][y:f32][z:f32]
    /// server→client: [peer_id:u32][grab_type:u8][bone_or_obj_id:u16][x:f32][y:f32][z:f32]
    /// grab_type: 0=start grab, 1=update grab position, 2=release grab
    /// Used for hair/PhysBone grabbing and physics prop grabbing on other players.
    PhysGrab = 0x0B,
}

public static class RelayProtocol
{
    public static byte[] WriteHello(string ticket)
    {
        var t = Encoding.UTF8.GetBytes(ticket);
        var buf = new byte[3 + t.Length];
        buf[0] = (byte)MsgType.Hello;
        buf[1] = (byte)(t.Length & 0xFF);
        buf[2] = (byte)((t.Length >> 8) & 0xFF);
        Array.Copy(t, 0, buf, 3, t.Length);
        return buf;
    }

    public static readonly byte[] Ping = { (byte)MsgType.Ping };

    /// A Pose/Voice datagram to send: [type][payload]. The relay re-frames it with our peer
    /// id before fan-out, so we don't include our own id.
    public static byte[] WriteOutbound(MsgType ty, byte[] payload)
    {
        var buf = new byte[1 + payload.Length];
        buf[0] = (byte)ty;
        Array.Copy(payload, 0, buf, 1, payload.Length);
        return buf;
    }

    public static uint ReadU32(ReadOnlySpan<byte> b, int at) =>
        (uint)b[at] | ((uint)b[at + 1] << 8) | ((uint)b[at + 2] << 16) | ((uint)b[at + 3] << 24);

    public static ushort ReadU16(ReadOnlySpan<byte> b, int at) =>
        (ushort)(b[at] | (b[at + 1] << 8));
}

public readonly struct PeerInfo
{
    public readonly uint PeerId;
    public readonly string Name;
    public readonly string UserId;
    public PeerInfo(uint peerId, string name, string userId = "") { PeerId = peerId; Name = name; UserId = userId; }
}
