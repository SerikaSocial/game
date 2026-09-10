using Serika.Net.Codec;

namespace Serika.Net;

/// The seam that lets P2P (WebRTC) later drop in behind the same game code that talks to a
/// dedicated relay today. Everything above this interface is written once. In M1 the only
/// implementation is UdpTransport; M6 adds a WebRtcTransport with the same surface.
///
/// Callbacks fire from Poll(), which the caller drives once per frame — so handlers run on
/// the game thread and can touch the scene tree directly.
public interface ISerikaTransport
{
    /// Fired once, after the relay accepts our ticket. `peers` are those already present.
    event System.Action<uint, PeerInfo[]> Connected;
    event System.Action<PeerInfo> PeerJoined;
    event System.Action<uint> PeerLeft;
    event System.Action<uint, PoseFrame> PoseReceived;
    event System.Action<uint, VoiceFrame> VoiceReceived;
    /// A world text-chat line from another peer: (senderPeerId, text).
    event System.Action<uint, string> ChatReceived;
    /// A peer swapped avatar; the receiver re-reads their model from the API.
    event System.Action<uint> AvatarChanged;

    /// (senderPeerId, channel, payload) — a peer's world script emitted a NET_EMIT.
    event System.Action<uint, int, double> ScriptEventReceived;
    /// A physics object sync from another peer: (senderPeerId, objId, x, y, z, qx, qy, qz, qw, lvx, lvy, lvz).
    event System.Action<uint, ushort, float, float, float, float, float, float, float, float, float, float> ObjectSyncReceived;
    /// A physics grab event from another peer: (senderPeerId, grabType, boneOrObjId, x, y, z).
    event System.Action<uint, byte, ushort, float, float, float> PhysGrabReceived;
    /// Fired if the relay rejects us (bad/used ticket, full) with the reason string.
    event System.Action<string> Rejected;

    bool Connected_ { get; }
    uint SelfId { get; }

    void Connect(string endpoint, string ticket);
    void SendPose(PoseFrame frame);
    void SendVoice(VoiceFrame frame);
    /// Send a world text-chat line to everyone in the instance.
    void SendChat(string text);
    void SendAvatarChanged();

    /// Emit a world-script event to the instance.
    void SendScriptEvent(int channel, double payload);
    /// Send a physics object sync update. Ownership is implicit: whoever last sent wins.
    void SendObjectSync(ushort objId, float x, float y, float z, float qx, float qy, float qz, float qw, float lvx, float lvy, float lvz);
    /// Send a physics grab event (grab_type: 0=start, 1=update, 2=release).
    void SendPhysGrab(byte grabType, uint targetPeer, ushort boneOrObjId, float x, float y, float z);
    /// Drain inbound datagrams and fire callbacks; also sends keepalives. Call every frame,
    /// passing seconds elapsed since the last call (drives retransmit + keepalive timers).
    void Poll(double dt);
    void Disconnect();
}
