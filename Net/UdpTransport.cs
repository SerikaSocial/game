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
    private volatile bool _welcomed;
    private readonly object _sendLock = new();
    private System.Threading.Thread _keepalive;
    private volatile bool _running;
    private const int KeepaliveMs = 1000;
    /// Cap on datagrams processed per frame. After a stall the socket holds a backlog that
    /// would otherwise all be decoded in the frame that finally runs — another hitch, caused by
    /// recovering from the last one.
    private const int MaxDatagramsPerPoll = 512;
    /// Past this many in one frame, poses and voice are drained but not decoded: they describe
    /// where peers were, not where they are. Joins, leaves and chat are NEVER discarded — doing
    /// so is how you end up with ghost avatars and missing messages.
    private const int TransientBacklogBudget = 96;
    private double _helloTimer;
    private double _pingTimer;
    private double _connectTimeout;
    private const double ConnectTimeoutSeconds = 8.0;

    // After WELCOME, how long without any datagram before we call the session dead.
    private double _sinceRecv;
    private const double SilenceTimeoutSeconds = 4.0;
    // A frame this long is not a network event — the app itself stalled (first-visit shader
    // compilation in a heavy world is minutes on some machines). Charging that time to the
    // link declares a perfectly healthy session dead the instant the frame finally lands.
    private const double HitchSeconds = 0.75;
    private long _lastPollMs;
    private bool _reportedSendError;
    private readonly byte[] _rx = new byte[2048];

    public event Action<uint, PeerInfo[]> Connected;
    public event Action<PeerInfo> PeerJoined;
    public event Action<uint> PeerLeft;
    public event Action<uint, PoseFrame> PoseReceived;
    public event Action<uint, VoiceFrame> VoiceReceived;
    public event Action<uint, string> ChatReceived;
    public event Action<uint, ushort, float, float, float, float, float, float, float, float, float, float> ObjectSyncReceived;
    public event Action<uint, byte, ushort, float, float, float> PhysGrabReceived;
    /// A peer swapped avatar. Carries only who — the new model is looked up from the API.
    public event Action<uint> AvatarChanged;
    /// A message handler threw. Reported rather than rethrown so one bad datagram cannot take
    /// down the frame loop, but never swallowed silently — the owner logs it.
    public event Action<Exception> OnHandlerFault;
    /// The socket refused to send. Raised once per connection: a link that never sends at all is
    /// a different problem from a dropped datagram, and used to look identical.
    public event Action<string> SendFailed;
    public event Action<string> Rejected;
    /// A welcomed session went quiet. Distinct from `Rejected`, which is the relay refusing us:
    /// this one is retryable against the SAME instance and must not drop the player to Home.
    public event Action<string> Lost;

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
        _sinceRecv = 0;
        _lastPollMs = 0;
        _reportedSendError = false;
        _connectTimeout = ConnectTimeoutSeconds;
        SendHello();

        _running = true;
        _keepalive = new System.Threading.Thread(KeepaliveLoop)
        {
            IsBackground = true,   // must never hold the process open at quit
            Name = "serika-keepalive",
        };
        _keepalive.Start();
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

    /// Announce that we changed avatar. No payload: peers re-read our avatar from the API.
    public void SendAvatarChanged()
    {
        if (!_welcomed) return;
        Send(new[] { (byte)MsgType.AvatarChanged });
    }

    public void SendChat(string text)
    {
        if (!_welcomed || string.IsNullOrEmpty(text)) return;
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        if (bytes.Length > 400) Array.Resize(ref bytes, 400); // relay caps at 400 bytes
        Send(RelayProtocol.WriteOutbound(MsgType.Chat, bytes));
    }

    public void SendObjectSync(ushort objId, float x, float y, float z, float qx, float qy, float qz, float qw, float lvx, float lvy, float lvz)
    {
        if (!_welcomed) return;
        Send(RelayProtocol.WriteOutbound(MsgType.ObjectSync,
            ObjectFrames.WriteObjectSync(objId, x, y, z, qx, qy, qz, qw, lvx, lvy, lvz)));
    }

    public void SendPhysGrab(byte grabType, uint targetPeer, ushort boneOrObjId, float x, float y, float z)
    {
        if (!_welcomed) return;
        Send(RelayProtocol.WriteOutbound(MsgType.PhysGrab,
            ObjectFrames.WritePhysGrab(grabType, targetPeer, boneOrObjId, x, y, z)));
    }

    /// Drive once per frame. `dt` is seconds since last call, used for retransmit/keepalive.
    public void Poll(double dt)
    {
        if (_sock == null) return;

        // Drain FIRST. The liveness check used to run ahead of the receive loop, so a session
        // with a full socket buffer waiting to be read was declared dead without a single
        // datagram being looked at — which is what happens after any long frame, since `dt`
        // then arrives already over the threshold.
        bool got = false;
        int drained = 0;
        while (drained < MaxDatagramsPerPoll && TryReceive(out int n))
        {
            got = true;
            drained++;
            // Shed a stale backlog rather than replaying it: past the budget, poses and voice
            // are read off the socket and dropped, because they say where a peer WAS. Every
            // other type still goes through — a discarded join or leave is a ghost avatar.
            if (drained > TransientBacklogBudget && n >= 1
                && ((MsgType)_rx[0] == MsgType.Pose || (MsgType)_rx[0] == MsgType.Voice)) continue;

            // A handler runs arbitrary game code — spawning a remote avatar, routing a reject,
            // starting a reconnect. Two things must not happen as a result: an exception from one
            // datagram must not escape into the engine's frame loop (on Android that is a hard
            // crash, and a single malformed or unlucky packet should never take the client down),
            // and a handler that disconnects us must stop the drain rather than have the next
            // iteration touch a socket that is gone.
            try { Handle(_rx, n); }
            catch (Exception e) { OnHandlerFault?.Invoke(e); }
            if (_sock == null) break;
        }
        if (got) _sinceRecv = 0; // any datagram (even another peer's pose) proves the link is up

        // `dt` is the physics step, not wall time, and it lags reality across a stall. Measure
        // the real gap so a frame the app spent compiling shaders is recognised as a hitch
        // rather than silently accumulated into the silence budget.
        long nowMs = Environment.TickCount64;
        double wall = _lastPollMs == 0 ? dt : (nowMs - _lastPollMs) / 1000.0;
        _lastPollMs = nowMs;
        bool hitched = wall >= HitchSeconds;

        // Retransmit HELLO until the relay welcomes us.
        if (!_welcomed)
        {
            _helloTimer -= dt;
            if (_helloTimer <= 0) { SendHello(); _helloTimer = 0.25; }

            if (!hitched) _connectTimeout -= dt;
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
            // The keepalive thread owns the ping. This is only a backstop for the case where
            // that thread never started, and it is deliberately the same cadence.
            _pingTimer -= dt;
            if (_pingTimer <= 0)
            {
                if (_keepalive is not { IsAlive: true }) Send(RelayProtocol.Ping);
                _pingTimer = 1.0;
            }

            // Liveness: the relay echoes our 2 s pings and streams peer poses, so a welcomed
            // session should never go quiet for long. If it does — relay crashed, tunnel
            // dropped, laptop slept — nothing here ever noticed before; the client sat in a
            // frozen world sending pings into the void. Now we surface it as a disconnect.
            //
            // A hitched frame restarts the budget instead of adding to it. The player was not
            // on a broken network, their machine was busy; give the link a full window to
            // answer the ping we are about to send. If the stall outlasted the relay's own
            // PEER_TIMEOUT it really has dropped us, and the very next quiet window says so.
            if (hitched) _sinceRecv = 0;
            else _sinceRecv += dt;
            if (_sinceRecv >= SilenceTimeoutSeconds)
            {
                Lost?.Invoke("the world server stopped responding");
                _welcomed = false;
                _sock?.Close();
                _sock = null;
                return;
            }
        }
    }

    private bool TryReceive(out int n)
    {
        n = 0;
        // Read the field ONCE into a local. Handlers dispatched from the drain loop can tear the
        // transport down mid-loop — a Reject or a Lost routes into Main, which calls Disconnect
        // and nulls `_sock` — and the next iteration then dereferenced a null field. Only
        // SocketException was caught, so that surfaced as a NullReferenceException thrown out of
        // Poll, out of _PhysicsProcess and into the engine's frame loop.
        var sock = _sock;
        if (sock == null) return false;
        try
        {
            if (sock.Client.Available <= 0) return false;
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            n = sock.Client.ReceiveFrom(_rx, ref any);
            return n > 0;
        }
        catch (SocketException) { return false; }
        catch (ObjectDisposedException) { return false; }   // closed under us during teardown
    }

    private void Handle(byte[] buf, int n)
    {
        if (n < 1) return;
        switch ((MsgType)buf[0])
        {
            // Length guards on every case: a truncated or malformed datagram must be dropped,
            // not thrown out of _PhysicsProcess. The old code only checked n < 1, so a short
            // Chat (n < 5 → negative GetString count) or an oversized Reject length byte
            // threw ArgumentOutOfRangeException into the frame loop for as long as the
            // packets kept coming.
            case MsgType.Welcome:
            {
                if (n < 7) break;
                SelfId = RelayProtocol.ReadU32(buf, 1);
                int count = RelayProtocol.ReadU16(buf, 5);
                var peers = new PeerInfo[count];
                int o = 7;
                bool ok = true;
                for (int i = 0; i < count; i++)
                {
                    if (o + 4 > n || o + 5 > n) { ok = false; break; }
                    uint id = RelayProtocol.ReadU32(buf, o); o += 4;
                    int uidLen = buf[o++];
                    if (o + uidLen > n || o + 1 > n) { ok = false; break; }
                    string userId = System.Text.Encoding.UTF8.GetString(buf, o, uidLen); o += uidLen;
                    int nameLen = buf[o++];
                    if (o + nameLen > n) { ok = false; break; }
                    string name = System.Text.Encoding.UTF8.GetString(buf, o, nameLen); o += nameLen;
                    peers[i] = new PeerInfo(id, name, userId);
                }
                if (ok && !_welcomed) { _welcomed = true; Connected?.Invoke(SelfId, peers); }
                break;
            }
            case MsgType.PeerJoin:
            {
                if (n < 6) break;
                uint id = RelayProtocol.ReadU32(buf, 1);
                int uidLen = buf[5];
                if (n < 7 + uidLen) break;
                string userId = System.Text.Encoding.UTF8.GetString(buf, 6, uidLen);
                int nameOff = 6 + uidLen;
                int nameLen = buf[nameOff];
                if (n < nameOff + 1 + nameLen) break;
                string name = System.Text.Encoding.UTF8.GetString(buf, nameOff + 1, nameLen);
                PeerJoined?.Invoke(new PeerInfo(id, name, userId));
                break;
            }
            case MsgType.PeerLeave:
                if (n >= 5) PeerLeft?.Invoke(RelayProtocol.ReadU32(buf, 1));
                break;
            case MsgType.Pose:
            {
                if (n < 5) break;
                uint sender = RelayProtocol.ReadU32(buf, 1);
                try { PoseReceived?.Invoke(sender, PoseFrame.Decode(buf.AsSpan(5, n - 5))); }
                catch (CodecException) { /* drop malformed */ }
                break;
            }
            case MsgType.Voice:
            {
                if (n < 5) break;
                uint sender = RelayProtocol.ReadU32(buf, 1);
                try { VoiceReceived?.Invoke(sender, VoiceFrame.Decode(buf.AsSpan(5, n - 5))); }
                catch (CodecException) { }
                break;
            }
            case MsgType.Chat:
            {
                if (n < 5) break;
                uint sender = RelayProtocol.ReadU32(buf, 1);
                string text = System.Text.Encoding.UTF8.GetString(buf, 5, n - 5);
                ChatReceived?.Invoke(sender, text);
                break;
            }
            case MsgType.AvatarChanged:
            {
                if (n < 5) break;
                AvatarChanged?.Invoke(RelayProtocol.ReadU32(buf, 1));
                break;
            }
            case MsgType.ObjectSync:
            {
                // [type][peer_id:u32][body…] — skip the 5-byte relay envelope to reach the body.
                if (n < 5) break;
                uint sender = RelayProtocol.ReadU32(buf, 1);
                if (ObjectFrames.ReadObjectSync(buf.AsSpan(5, System.Math.Max(0, n - 5)), out var objId,
                        out var x, out var y, out var z,
                        out var qx, out var qy, out var qz, out var qw,
                        out var lvx, out var lvy, out var lvz))
                    ObjectSyncReceived?.Invoke(sender, objId, x, y, z, qx, qy, qz, qw, lvx, lvy, lvz);
                break;
            }
            case MsgType.PhysGrab:
            {
                // hasTargetPeer: false — the relay strips that field and re-frames with the sender
                // id, unlike the direct P2P channel in WebRtcTransport.
                if (n < 5) break;
                uint sender = RelayProtocol.ReadU32(buf, 1);
                if (ObjectFrames.ReadPhysGrab(buf.AsSpan(5, System.Math.Max(0, n - 5)), hasTargetPeer: false,
                        out var grabType, out var boneId, out var gx, out var gy, out var gz))
                    PhysGrabReceived?.Invoke(sender, grabType, boneId, gx, gy, gz);
                break;
            }
            case MsgType.Reject:
            {
                if (n < 2) break;
                int len = System.Math.Min(buf[1], n - 2);
                Rejected?.Invoke(System.Text.Encoding.UTF8.GetString(buf, 2, len));
                break;
            }
        }
    }

    private static void WriteU16(byte[] b, ref int o, ushort v) { b[o++] = (byte)(v & 0xFF); b[o++] = (byte)(v >> 8); }
    private static void WriteU32(byte[] b, ref int o, uint v)
    { b[o++] = (byte)(v & 0xFF); b[o++] = (byte)((v >> 8) & 0xFF); b[o++] = (byte)((v >> 16) & 0xFF); b[o++] = (byte)((v >> 24) & 0xFF); }
    private static void WriteF32(byte[] b, ref int o, float v)
    { var bytes = BitConverter.GetBytes(v); b[o++] = bytes[0]; b[o++] = bytes[1]; b[o++] = bytes[2]; b[o++] = bytes[3]; }
    private static ushort ReadU16(byte[] b, ref int o) { ushort v = (ushort)(b[o] | (b[o + 1] << 8)); o += 2; return v; }
    private static float ReadF32(byte[] b, ref int o)
    { float v = BitConverter.ToSingle(b, o); o += 4; return v; }

    private void SendHello() => Send(RelayProtocol.WriteHello(_ticket));

    private void Send(byte[] data)
    {
        // Serialised because the keepalive thread writes to the same socket.
        lock (_sendLock)
        {
            try { _sock?.Send(data, data.Length); }
            catch (SocketException e)
            {
                // Transient loss is normal on UDP and the retransmit logic covers control
                // messages — but a socket that NEVER sends is not transient, and swallowing
                // this made the two indistinguishable. A client that could not send a single
                // datagram looked exactly like a relay that was not answering: an eight-second
                // wait and "connection timed out", with the real error discarded here. Report
                // the first failure of each connection, once, so the cause is on the record.
                if (!_reportedSendError)
                {
                    _reportedSendError = true;
                    SendFailed?.Invoke($"{e.SocketErrorCode}: {e.Message}");
                }
            }
            catch (ObjectDisposedException) { /* closed under us during teardown */ }
        }
    }

    /// Keepalive lives on its OWN THREAD, and that is the whole point.
    ///
    /// The relay drops a peer after PEER_TIMEOUT (10 s) of silence. Pings used to be sent from
    /// `Poll`, i.e. from the frame loop — so any stall longer than 10 s got the player dropped,
    /// and the first visit to a heavy world stalls for far longer than that while its shaders
    /// compile. The player was removed from the instance *every time*, and everyone else watched
    /// them leave and come back. Reconnecting afterwards patched over the symptom; not being
    /// dropped in the first place is the fix. A frozen renderer is not a dead connection.
    ///
    /// One byte a second, and it deliberately does no bookkeeping of its own: liveness is still
    /// judged on the main thread from what actually arrives.
    private void KeepaliveLoop()
    {
        while (_running)
        {
            System.Threading.Thread.Sleep(KeepaliveMs);
            if (!_running || !_welcomed) continue;
            Send(RelayProtocol.Ping);
        }
    }

    public void Disconnect()
    {
        _running = false;
        _welcomed = false;
        _keepalive = null;   // background thread; it observes _running and exits on its own
        lock (_sendLock)
        {
            _sock?.Close();
            _sock?.Dispose();
            _sock = null;
        }
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
