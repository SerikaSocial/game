using System;
using System.Net;
using System.Net.Sockets;
using Serika.Net.Codec;

namespace Serika.Net;

/// UDP implementation of the relay transport for M1. Uses System.Net.Sockets directly
/// rather than Godot's PacketPeerUDP, which keeps it Godot-free and unit-testable — the
/// tradeoff is no web export (Godot 4.x C# has none anyway; see docs).
///
/// The client sends HELLO and retransmits it until WELCOME arrives, since a lone datagram
/// can be lost. Everything else is fire-and-forget: a dropped pose is stale within 50ms.
public sealed class UdpTransport : ISerikaTransport, IDisposable
{
    private UdpClient _sock;
    private string _ticket = "";
    private bool _welcomed;
    private double _helloTimer;
    private double _pingTimer;
    private double _connectTimeout;
    private const double ConnectTimeoutSeconds = 15.0;
    private readonly byte[] _rx = new byte[2048];

    public event Action<uint, PeerInfo[]> Connected;
    public event Action<PeerInfo> PeerJoined;
    public event Action<uint> PeerLeft;
    public event Action<uint, PoseFrame> PoseReceived;
    public event Action<uint, VoiceFrame> VoiceReceived;
    public event Action<uint, string> ChatReceived;
    public event Action<string> Rejected;

    public bool Connected_ => _welcomed;
    public uint SelfId { get; private set; }

    public void Connect(string endpoint, string ticket)
    {
        var (host, port) = ParseEndpoint(endpoint);
        _sock = new UdpClient();
        _sock.Connect(host, port);
        _sock.Client.Blocking = false;
        _ticket = ticket;
        _welcomed = false;
        _helloTimer = 0;
        _connectTimeout = ConnectTimeoutSeconds;
        SendHello();
    }

    public void SendPose(PoseFrame frame)
    {
        if (!_welcomed) return;
        Send(RelayProtocol.WriteOutbound(MsgType.Pose, frame.Encode()));
    }

    public void SendVoice(VoiceFrame frame)
    {
        if (!_welcomed) return;
        Send(RelayProtocol.WriteOutbound(MsgType.Voice, frame.Encode()));
    }

    public void SendChat(string text)
    {
        if (!_welcomed || string.IsNullOrEmpty(text)) return;
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        if (bytes.Length > 400) Array.Resize(ref bytes, 400); // relay caps at 400 bytes
        Send(RelayProtocol.WriteOutbound(MsgType.Chat, bytes));
    }

    /// Drive once per frame. `dt` is seconds since last call, used for retransmit/keepalive.
    public void Poll(double dt)
    {
        if (_sock == null) return;

        // Retransmit HELLO until the relay welcomes us.
        if (!_welcomed)
        {
            _helloTimer -= dt;
            if (_helloTimer <= 0) { SendHello(); _helloTimer = 0.25; }

            _connectTimeout -= dt;
            if (_connectTimeout <= 0)
            {
                Rejected?.Invoke("Connection timed out — the world server may not be running. Check that the relay (instanced) is started on the expected endpoint.");
                _welcomed = false;
                _sock?.Close();
                _sock = null;
                return;
            }
        }
        else
        {
            // Keepalive doubles as an RTT probe and keeps NAT mappings open.
            _pingTimer -= dt;
            if (_pingTimer <= 0) { Send(RelayProtocol.Ping); _pingTimer = 2.0; }
        }

        // Drain everything queued this frame.
        while (TryReceive(out int n))
            Handle(_rx, n);
    }

    private bool TryReceive(out int n)
    {
        n = 0;
        try
        {
            if (_sock.Client.Available <= 0) return false;
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            n = _sock.Client.ReceiveFrom(_rx, ref any);
            return n > 0;
        }
        catch (SocketException) { return false; }
    }

    private void Handle(byte[] buf, int n)
    {
        if (n < 1) return;
        switch ((MsgType)buf[0])
        {
            case MsgType.Welcome:
            {
                SelfId = RelayProtocol.ReadU32(buf, 1);
                int count = RelayProtocol.ReadU16(buf, 5);
                var peers = new PeerInfo[count];
                int o = 7;
                for (int i = 0; i < count; i++)
                {
                    uint id = RelayProtocol.ReadU32(buf, o); o += 4;
                    int uidLen = buf[o++];
                    string userId = System.Text.Encoding.UTF8.GetString(buf, o, uidLen); o += uidLen;
                    int nameLen = buf[o++];
                    string name = System.Text.Encoding.UTF8.GetString(buf, o, nameLen); o += nameLen;
                    peers[i] = new PeerInfo(id, name, userId);
                }
                if (!_welcomed) { _welcomed = true; Connected?.Invoke(SelfId, peers); }
                break;
            }
            case MsgType.PeerJoin:
            {
                uint id = RelayProtocol.ReadU32(buf, 1);
                int uidLen = buf[5];
                string userId = System.Text.Encoding.UTF8.GetString(buf, 6, uidLen);
                int nameOff = 6 + uidLen;
                int nameLen = buf[nameOff];
                string name = System.Text.Encoding.UTF8.GetString(buf, nameOff + 1, nameLen);
                PeerJoined?.Invoke(new PeerInfo(id, name, userId));
                break;
            }
            case MsgType.PeerLeave:
                PeerLeft?.Invoke(RelayProtocol.ReadU32(buf, 1));
                break;
            case MsgType.Pose:
            {
                uint sender = RelayProtocol.ReadU32(buf, 1);
                try { PoseReceived?.Invoke(sender, PoseFrame.Decode(buf.AsSpan(5, n - 5))); }
                catch (CodecException) { /* drop malformed */ }
                break;
            }
            case MsgType.Voice:
            {
                uint sender = RelayProtocol.ReadU32(buf, 1);
                try { VoiceReceived?.Invoke(sender, VoiceFrame.Decode(buf.AsSpan(5, n - 5))); }
                catch (CodecException) { }
                break;
            }
            case MsgType.Chat:
            {
                uint sender = RelayProtocol.ReadU32(buf, 1);
                string text = System.Text.Encoding.UTF8.GetString(buf, 5, n - 5);
                ChatReceived?.Invoke(sender, text);
                break;
            }
            case MsgType.Reject:
            {
                int len = buf[1];
                Rejected?.Invoke(System.Text.Encoding.UTF8.GetString(buf, 2, len));
                break;
            }
        }
    }

    private void SendHello() => Send(RelayProtocol.WriteHello(_ticket));

    private void Send(byte[] data)
    {
        try { _sock?.Send(data, data.Length); }
        catch (SocketException) { /* transient; retransmit logic covers control msgs */ }
    }

    public void Disconnect()
    {
        _sock?.Close();
        _sock?.Dispose();
        _sock = null;
        _welcomed = false;
    }

    public void Dispose() => Disconnect();

    private static (string, int) ParseEndpoint(string endpoint)
    {
        // Accept "host:port"; tolerate an accidental scheme prefix.
        var s = endpoint.Replace("udp://", "");
        int i = s.LastIndexOf(':');
        if (i < 0) throw new ArgumentException($"endpoint must be host:port, got '{endpoint}'");
        return (s[..i], int.Parse(s[(i + 1)..]));
    }
}
