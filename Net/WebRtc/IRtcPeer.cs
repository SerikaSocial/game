using System;
using Godot;

namespace Serika.Net.WebRtc;

/// One ICE server entry (STUN or TURN) as returned by GET /v1/rtc/ice.
public readonly struct IceServer
{
    public readonly string[] Urls;
    public readonly string Username;
    public readonly string Credential;
    public IceServer(string[] urls, string username, string credential)
    {
        Urls = urls; Username = username; Credential = credential;
    }
}

/// A single WebRTC peer connection with one reliable-ordered data channel carrying the same
/// `[type:u8][payload]` envelope the relay uses. This is the seam behind which the actual
/// media stack lives: the concrete implementation is backed by the `webrtc-native` GDExtension
/// (see CLAUDE.md — native Godot has no built-in WebRTC), so the rest of the client never
/// references Godot's WebRTC types directly and the project compiles without the extension.
public interface IRtcPeer
{
    /// SDP/ICE we produced locally and must relay to the remote peer via the gateway (JSON).
    event Action<string> LocalSignal;
    /// The data channel opened and is ready to carry frames.
    event Action Connected;
    /// A datagram arrived on the data channel.
    event Action<byte[]> DataReceived;

    /// Feed a signal (offer/answer/candidate JSON) received from the remote peer.
    void OnRemoteSignal(string json);
    /// Send a datagram over the data channel (no-op until Connected).
    void Send(byte[] data);
    /// Pump the underlying connection; call each frame.
    void Poll();
    void Close();
}

/// Builds peers. `Available` is false when the WebRTC GDExtension isn't installed, letting the
/// transport degrade cleanly (log + refuse P2P) instead of crashing.
public interface IRtcPeerFactory
{
    bool Available { get; }
    IRtcPeer Create(bool initiator, IceServer[] iceServers);
}

/// Fallback used when no WebRTC backend is registered. Everything is a no-op; creating a peer
/// logs once so the failure mode is obvious in the console.
public sealed class NullRtcPeerFactory : IRtcPeerFactory
{
    public bool Available => false;

    public IRtcPeer Create(bool initiator, IceServer[] iceServers)
    {
        GD.PushWarning("WebRTC requested but no backend is registered (install the webrtc-native " +
                       "GDExtension and register GodotRtcPeerFactory). P2P is unavailable.");
        return new NullRtcPeer();
    }

    private sealed class NullRtcPeer : IRtcPeer
    {
        public event Action<string> LocalSignal { add { } remove { } }
        public event Action Connected { add { } remove { } }
        public event Action<byte[]> DataReceived { add { } remove { } }
        public void OnRemoteSignal(string json) { }
        public void Send(byte[] data) { }
        public void Poll() { }
        public void Close() { }
    }
}
