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
    /// Fired if the relay rejects us (bad/used ticket, full) with the reason string.
    event System.Action<string> Rejected;

    bool Connected_ { get; }
    uint SelfId { get; }

    void Connect(string endpoint, string ticket);
    void SendPose(PoseFrame frame);
    void SendVoice(VoiceFrame frame);
    /// Drain inbound datagrams and fire callbacks; also sends keepalives. Call every frame,
    /// passing seconds elapsed since the last call (drives retransmit + keepalive timers).
    void Poll(double dt);
    void Disconnect();
}
