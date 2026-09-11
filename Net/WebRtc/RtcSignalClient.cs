using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SerikaSocial;
using System.Threading;
using System.Threading.Tasks;

namespace Serika.Net.WebRtc;

/// WebSocket client for the gateway's WebRTC signalling (`/gateway`). Carries the offer/answer/
/// ICE-candidate exchange between P2P peers; no game data flows here. Receives run on a background
/// task and are queued, then dispatched on the game thread from Poll() so handlers can touch the
/// scene tree safely — mirroring UdpTransport's threading model.
public sealed class RtcSignalClient
{
    private ClientWebSocket _ws;
    private readonly ConcurrentQueue<string> _inbox = new();
    private CancellationTokenSource _cts;
    private string _instanceId;

    public event Action Ready;
    public event Action<string[]> PeersList;        // existing peers when we join
    public event Action<string> PeerJoined;         // a peer joined after us
    public event Action<string> PeerLeft;
    public event Action<string, string> SignalFrom; // (fromUserId, dataJson)

    public bool IsOpen => _ws?.State == WebSocketState.Open;

    /// Connect to `wss://…/gateway?token=…` and start receiving. Fires Ready on the gateway's ack.
    public async Task ConnectAsync(string gatewayWsUrl, string sessionToken, string instanceId)
    {
        _instanceId = instanceId;
        _cts = new CancellationTokenSource();
        _ws = new ClientWebSocket();
        var uri = new Uri($"{gatewayWsUrl}?token={Uri.EscapeDataString(sessionToken)}");
        await _ws.ConnectAsync(uri, _cts.Token);
        _ = ReceiveLoop(_cts.Token);
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        var sb = new StringBuilder();
        try
        {
            while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, result.Count));
                if (result.EndOfMessage)
                {
                    _inbox.Enqueue(sb.ToString());
                    sb.Clear();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    /// Drain queued signalling messages and fire events. Call each frame.
    public void Poll()
    {
        while (_inbox.TryDequeue(out var raw))
        {
            try { Dispatch(raw); }
            catch (JsonException) { /* ignore malformed */ }
        }
    }

    private void Dispatch(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var typeEl)) return;
        switch (typeEl.GetString())
        {
            case "ready":
                Ready?.Invoke();
                Send(new JsonObject { ["type"] = "rtc:join", ["instanceId"] = _instanceId });
                break;
            case "rtc:peers":
            {
                var peers = root.GetProperty("peers");
                var list = new string[peers.GetArrayLength()];
                int i = 0;
                foreach (var p in peers.EnumerateArray()) list[i++] = p.GetString();
                PeersList?.Invoke(list);
                break;
            }
            case "rtc:peer-join":
                PeerJoined?.Invoke(root.GetProperty("peer").GetString());
                break;
            case "rtc:peer-leave":
                PeerLeft?.Invoke(root.GetProperty("peer").GetString());
                break;
            case "rtc:signal":
                // `data` is an opaque SDP/ICE object; forward its raw JSON to the peer.
                SignalFrom?.Invoke(root.GetProperty("from").GetString(), root.GetProperty("data").GetRawText());
                break;
        }
    }

    /// Relay a signal (offer/answer/candidate) to a specific peer. `dataJson` is raw JSON.
    public void SendSignal(string toUserId, string dataJson)
    {
        var payload = $"{{\"type\":\"rtc:signal\",\"instanceId\":{AotJson.JStr(_instanceId)}," +
                      $"\"to\":{AotJson.JStr(toUserId)},\"data\":{dataJson}}}";
        SendRaw(payload);
    }

    public void Leave() => Send(new JsonObject { ["type"] = "rtc:leave", ["instanceId"] = _instanceId });

    private void Send(JsonObject obj) => SendRaw(obj.ToJsonString());

    private void SendRaw(string json)
    {
        if (_ws?.State != WebSocketState.Open) return;
        // Fire-and-forget; signalling is idempotent enough for M6's needs.
        _ = _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, _cts.Token);
    }

    public void Close()
    {
        try { _cts?.Cancel(); } catch { }
        try { _ = _ws?.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
    }
}
