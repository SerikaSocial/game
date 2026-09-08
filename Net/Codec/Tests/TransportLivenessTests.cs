using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Serika.Net;
using Xunit;

namespace Codec.Tests;

/// The transport's liveness rules decide whether a player stays in a world, and getting them
/// wrong is not a subtle bug: it drops people out of live events. They were wrong twice, both
/// times in a way that reading the code did not reveal, so they are pinned here against a real
/// UDP socket standing in for the relay.
public sealed class TransportLivenessTests
{
    /// A minimal stand-in relay: answers HELLO with WELCOME and echoes pings, and records what
    /// it received and when.
    private sealed class FakeRelay : IDisposable
    {
        private readonly UdpClient _sock = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly Thread _thread;
        private volatile bool _running = true;
        private readonly object _gate = new();
        private readonly List<DateTime> _pings = new();
        private EndPoint _client;
        /// When false the relay stops answering, which is how a real drop looks to the client.
        public volatile bool Responding = true;

        public IPEndPoint Endpoint => (IPEndPoint)_sock.Client.LocalEndPoint;
        public int PingCount { get { lock (_gate) return _pings.Count; } }

        public FakeRelay()
        {
            _sock.Client.ReceiveTimeout = 200;
            _thread = new Thread(Loop) { IsBackground = true };
            _thread.Start();
        }

        private void Loop()
        {
            var buf = new byte[2048];
            while (_running)
            {
                EndPoint any = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try { n = _sock.Client.ReceiveFrom(buf, ref any); }
                catch (SocketException) { continue; }
                catch (ObjectDisposedException) { return; }
                if (n < 1) continue;
                _client = any;
                if (!Responding) continue;

                switch ((MsgType)buf[0])
                {
                    case MsgType.Hello:
                        // WELCOME: [type][selfId u32][peerCount u16] — nobody else present.
                        var welcome = new byte[7];
                        welcome[0] = (byte)MsgType.Welcome;
                        BitConverter.GetBytes(7u).CopyTo(welcome, 1);
                        _sock.Client.SendTo(welcome, any);
                        break;
                    case MsgType.Ping:
                        lock (_gate) _pings.Add(DateTime.UtcNow);
                        _sock.Client.SendTo(new[] { (byte)MsgType.Ping }, any);
                        break;
                }
            }
        }

        /// Push an unsolicited datagram at the client (it must have talked to us first, so we
        /// know where it lives).
        public void SendToClient(byte[] data)
        {
            if (_client != null) _sock.Client.SendTo(data, _client);
        }

        public void Dispose() { _running = false; _sock.Dispose(); }
    }

    private static UdpTransport Connected(FakeRelay relay)
    {
        var t = new UdpTransport();
        t.Connect($"127.0.0.1:{relay.Endpoint.Port}", "test-ticket");
        for (int i = 0; i < 100 && !t.Connected_; i++) { t.Poll(1.0 / 60.0); Thread.Sleep(10); }
        Assert.True(t.Connected_, "transport should reach WELCOME against the fake relay");
        return t;
    }

    /// THE ONE THAT MATTERS. The relay drops a peer after 10 s of silence. Pings used to be sent
    /// from the frame loop, so any stall longer than that — a first visit to a heavy venue
    /// compiling its shaders takes minutes — got the player removed from the instance every
    /// single time. Keepalive now runs on its own thread: a frozen renderer is not a dead
    /// connection.
    [Fact]
    public void KeepaliveSurvivesAFrozenMainThread()
    {
        using var relay = new FakeRelay();
        using var t = Connected(relay);

        int before = relay.PingCount;
        // Poll is not called at all: this is precisely a stalled main thread.
        Thread.Sleep(3200);
        int during = relay.PingCount - before;

        Assert.True(during >= 2,
            $"the relay must keep hearing from a stalled client (got {during} pings in ~3.2 s)");
    }

    /// The liveness check used to run BEFORE the receive loop, so a client whose socket was full
    /// of waiting datagrams declared itself dead without reading one — guaranteed after any long
    /// frame, because `dt` then arrives already over the threshold.
    [Fact]
    public void QueuedDatagramsAreReadBeforeTheLinkIsCalledDead()
    {
        using var relay = new FakeRelay();
        using var t = Connected(relay);

        string lost = null;
        t.Lost += reason => lost = reason;

        // Let the keepalive thread put ping echoes in our receive buffer, then deliver one
        // oversized frame — the shape of a stall ending.
        Thread.Sleep(1500);
        t.Poll(60.0);

        Assert.Null(lost);
        Assert.True(t.Connected_, "a session with data waiting is alive, whatever dt says");
    }

    /// A genuinely dead relay must still be detected — the hitch tolerance must not become a
    /// blanket excuse that leaves players frozen in a world nobody else is in.
    [Fact]
    public void SilenceFromAnUnresponsiveRelayIsStillReported()
    {
        using var relay = new FakeRelay();
        using var t = Connected(relay);

        string lost = null;
        t.Lost += reason => lost = reason;
        relay.Responding = false;

        // Normal-length frames, no hitch: the budget must run down and fire.
        for (int i = 0; i < 600 && lost == null; i++) { t.Poll(1.0 / 60.0); Thread.Sleep(2); }

        Assert.NotNull(lost);
        Assert.False(t.Connected_);
    }

    /// Loss is retryable against the same instance; a relay REJECT is not. Main routes them
    /// differently — reconnect in place versus drop to Home — so they must stay distinct.
    [Fact]
    public void SilenceRaisesLostNotRejected()
    {
        using var relay = new FakeRelay();
        using var t = Connected(relay);

        bool rejected = false, lost = false;
        t.Rejected += _ => rejected = true;
        t.Lost += _ => lost = true;
        relay.Responding = false;

        for (int i = 0; i < 600 && !lost; i++) { t.Poll(1.0 / 60.0); Thread.Sleep(2); }

        Assert.True(lost);
        Assert.False(rejected, "a quiet link is not a refusal, and must not send the player Home");
    }

    /// Handlers run real game code, and two of them (Rejected, Lost) route into Main and call
    /// Disconnect. The drain loop then went round again and dereferenced a `_sock` that was now
    /// null — and only SocketException was caught, so a NullReferenceException came out of Poll,
    /// out of _PhysicsProcess, and into the engine. On desktop that is a logged error; on Android
    /// it is a crash on joining a session.
    [Fact]
    public void AHandlerThatDisconnectsMidDrainDoesNotThrow()
    {
        using var relay = new FakeRelay();
        var t = Connected(relay);

        // Exactly what Main does on a relay REJECT: tear the transport down from inside the
        // handler. Dispatched from the drain loop, so the loop is about to go round again.
        t.Rejected += _ => t.Disconnect();

        // REJECT: [type][reason_len u8][reason]
        var reason = System.Text.Encoding.UTF8.GetBytes("instance full");
        var reject = new byte[2 + reason.Length];
        reject[0] = (byte)MsgType.Reject;
        reject[1] = (byte)reason.Length;
        reason.CopyTo(reject, 2);
        relay.SendToClient(reject);
        // Queue more behind it, so the loop MUST iterate again after the disconnect. Without
        // that the socket is never touched post-teardown and the bug hides.
        for (int i = 0; i < 8; i++) relay.SendToClient(new[] { (byte)MsgType.Ping });
        Thread.Sleep(250);   // let them all land in the receive buffer

        var ex = Record.Exception(() => t.Poll(1.0 / 60.0));

        Assert.Null(ex);
        Assert.False(t.Connected_);
    }

    /// A throwing handler must not escape into the frame loop either — one peer whose avatar
    /// blows up on import should not take the client down with it.
    [Fact]
    public void AThrowingHandlerIsReportedNotPropagated()
    {
        using var relay = new FakeRelay();
        using var t = Connected(relay);

        Exception reported = null;
        t.OnHandlerFault += e => reported = e;
        t.PeerLeft += _ => throw new InvalidOperationException("avatar import blew up");

        // PEER_LEAVE: [type][peer_id u32]
        var leave = new byte[5];
        leave[0] = (byte)MsgType.PeerLeave;
        BitConverter.GetBytes(42u).CopyTo(leave, 1);
        relay.SendToClient(leave);

        var ex = Record.Exception(() =>
        {
            for (int i = 0; i < 100 && reported == null; i++) { t.Poll(1.0 / 60.0); Thread.Sleep(5); }
        });

        Assert.Null(ex);
        Assert.NotNull(reported);
        Assert.True(t.Connected_, "one bad datagram must not end the session");
    }

    /// Disconnect has to stop the keepalive thread, or every world change leaks one that goes on
    /// pinging a relay the player has left.
    [Fact]
    public void DisconnectStopsTheKeepalive()
    {
        using var relay = new FakeRelay();
        var t = Connected(relay);

        t.Disconnect();
        Thread.Sleep(1200);
        int after = relay.PingCount;
        Thread.Sleep(1600);

        Assert.Equal(after, relay.PingCount);
    }
}
