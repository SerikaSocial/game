using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Godot;
using Serika.Net.Codec;

namespace Serika.Net.WebRtc;

/// P2P implementation of ISerikaTransport (M6). Instead of a dedicated relay, peers connect
/// directly in a mesh: the gateway carries only signalling (offer/answer/ICE), then each peer's
/// data channel carries the SAME `[type:u8][payload]` frames the relay used — so LocalPlayer,
/// RemoteAvatar, chat and voice code are identical regardless of transport. This is the seam the
/// ISerikaTransport comment promised: "M6 adds a WebRtcTransport with the same surface."
///
/// The actual media stack is provided by an IRtcPeerFactory (backed by the webrtc-native
/// GDExtension). With the NullRtcPeerFactory the transport compiles and runs but refuses P2P.
///
/// Usage: `endpoint` = gateway WebSocket URL, `ticket` = the instanceId. Session token + ICE list
/// + peer factory are supplied to the constructor.
public sealed class WebRtcTransport : ISerikaTransport
{
    private readonly string _sessionToken;
    private readonly IceServer[] _ice;
    private readonly IRtcPeerFactory _factory;
    private readonly RtcSignalClient _signal = new();

    private sealed class Entry { public IRtcPeer Peer; public uint PeerId; public bool Announced; }
    private readonly Dictionary<string, Entry> _peers = new(); // remote userId -> entry
    private uint _nextPeerId = 1;
    private bool _ready;

    public event Action<uint, PeerInfo[]> Connected;
    public event Action<PeerInfo> PeerJoined;
    public event Action<uint> PeerLeft;
    public event Action<uint, PoseFrame> PoseReceived;
    public event Action<uint, VoiceFrame> VoiceReceived;
    public event Action<uint, string> ChatReceived;
    public event Action<string> Rejected;

    public bool Connected_ => _ready;
    public uint SelfId => 0;

    public WebRtcTransport(string sessionToken, IceServer[] iceServers, IRtcPeerFactory factory)
    {
        _sessionToken = sessionToken;
        _ice = iceServers ?? Array.Empty<IceServer>();
        _factory = factory ?? new NullRtcPeerFactory();
    }

    public void Connect(string gatewayWsUrl, string instanceId)
    {
        if (!_factory.Available)
        {
            Rejected?.Invoke("P2P is unavailable in this build (WebRTC extension not installed).");
            return;
        }
        _signal.Ready += OnSignalReady;
        _signal.PeersList += OnPeersList;
        _signal.PeerJoined += OnRemoteJoin;
        _signal.PeerLeft += OnRemoteLeave;
        _signal.SignalFrom += OnSignalFrom;
        _ = _signal.ConnectAsync(gatewayWsUrl, _sessionToken, instanceId);
    }

    private void OnSignalReady()
    {
        // We're registered; peers arrive via OnPeersList. Report ourselves connected so the game
        // starts broadcasting pose (peers get spawned as their channels come up).
        _ready = true;
        Connected?.Invoke(SelfId, Array.Empty<PeerInfo>());
    }

    // Existing peers when we join: we are the initiator toward each of them.
    private void OnPeersList(string[] peers)
    {
        foreach (var userId in peers) EnsurePeer(userId, initiator: true);
    }

    // Someone joined after us: they will initiate, so we create a non-initiator peer to answer.
    private void OnRemoteJoin(string userId) => EnsurePeer(userId, initiator: false);

    private void OnRemoteLeave(string userId)
    {
        if (!_peers.Remove(userId, out var e)) return;
        e.Peer.Close();
        if (e.Announced) PeerLeft?.Invoke(e.PeerId);
    }

    private Entry EnsurePeer(string userId, bool initiator)
    {
        if (_peers.TryGetValue(userId, out var existing)) return existing;

        var peer = _factory.Create(initiator, _ice);
        var entry = new Entry { Peer = peer, PeerId = _nextPeerId++ };
        _peers[userId] = entry;

        peer.LocalSignal += json => _signal.SendSignal(userId, json);
        peer.Connected += () =>
        {
            if (entry.Announced) return;
            entry.Announced = true;
            // We don't get usernames over signalling yet; use the id as the display name for now.
            PeerJoined?.Invoke(new PeerInfo(entry.PeerId, $"peer{entry.PeerId}"));
        };
        peer.DataReceived += bytes => OnData(entry.PeerId, bytes);
        return entry;
    }

    private void OnSignalFrom(string fromUserId, string dataJson)
    {
        // A signal from a peer we haven't created yet means they initiated toward us.
        var entry = EnsurePeer(fromUserId, initiator: false);
        entry.Peer.OnRemoteSignal(dataJson);
    }

    // Decode a data-channel frame using the shared relay envelope: [type][payload] (no peer-id
    // prefix — on a direct channel the sender is the channel).
    private void OnData(uint peerId, byte[] data)
    {
        if (data.Length < 1) return;
        var payload = data.AsSpan(1);
        switch ((MsgType)data[0])
        {
            case MsgType.Pose:
                try { PoseReceived?.Invoke(peerId, PoseFrame.Decode(payload)); } catch (CodecException) { }
                break;
            case MsgType.Voice:
                try { VoiceReceived?.Invoke(peerId, VoiceFrame.Decode(payload)); } catch (CodecException) { }
                break;
            case MsgType.Chat:
                ChatReceived?.Invoke(peerId, Encoding.UTF8.GetString(payload));
                break;
        }
    }

    public void SendPose(PoseFrame frame) => Broadcast(RelayProtocol.WriteOutbound(MsgType.Pose, frame.Encode()));
    public void SendVoice(VoiceFrame frame) => Broadcast(RelayProtocol.WriteOutbound(MsgType.Voice, frame.Encode()));

    public void SendChat(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > 400) Array.Resize(ref bytes, 400);
        Broadcast(RelayProtocol.WriteOutbound(MsgType.Chat, bytes));
    }

    private void Broadcast(byte[] frame)
    {
        foreach (var e in _peers.Values) e.Peer.Send(frame);
    }

    public void Poll(double dt)
    {
        _signal.Poll();
        foreach (var e in _peers.Values) e.Peer.Poll();
    }

    public void Disconnect()
    {
        _signal.Leave();
        foreach (var e in _peers.Values) e.Peer.Close();
        _peers.Clear();
        _signal.Close();
        _ready = false;
    }

    /// Parse the ICE server JSON from GET /v1/rtc/ice into the transport's IceServer[].
    public static IceServer[] ParseIce(JsonElement root)
    {
        if (!root.TryGetProperty("iceServers", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<IceServer>();
        var list = new List<IceServer>();
        foreach (var s in arr.EnumerateArray())
        {
            string[] urls;
            var u = s.GetProperty("urls");
            if (u.ValueKind == JsonValueKind.Array)
            {
                urls = new string[u.GetArrayLength()];
                int i = 0;
                foreach (var el in u.EnumerateArray()) urls[i++] = el.GetString();
            }
            else urls = new[] { u.GetString() };

            string user = s.TryGetProperty("username", out var un) ? un.GetString() : null;
            string cred = s.TryGetProperty("credential", out var cr) ? cr.GetString() : null;
            list.Add(new IceServer(urls, user, cred));
        }
        return list.ToArray();
    }
}
