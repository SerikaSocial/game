using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Serika.Net;

/// A notification as the client models it. Mirrors `serialize()` in `server/api/src/notify.ts`,
/// which is the single wire shape used by both the REST list and the gateway push.
public sealed class SerikaNotification
{
    public string Id = "";
    public string Kind = "";
    public string Title = "";
    public string Body = "";
    /// A `serikasocial://` deep link, or null.
    public string Link;
    public bool Read;
    public DateTimeOffset CreatedAt;
    public DateTimeOffset? ExpiresAt;

    public string ActorId;
    public string ActorName;
    public string ActorAvatarUrl;

    // Kind-specific payload, flattened from `data` for the kinds the client acts on.
    public string WorldId;
    public string WorldName;
    public string InstanceId;

    /// An invite whose window has closed. The row stays in the list as history, but the Join
    /// button must not be offered — the instance it points at has probably emptied.
    public bool IsExpired => ExpiresAt.HasValue && ExpiresAt.Value < DateTimeOffset.UtcNow;

    public static SerikaNotification FromJson(JsonElement e)
    {
        var n = new SerikaNotification
        {
            Id = Str(e, "id") ?? "",
            Kind = Str(e, "kind") ?? "",
            Title = Str(e, "title") ?? "",
            Body = Str(e, "body") ?? "",
            Link = Str(e, "link"),
            Read = e.TryGetProperty("read", out var r) && r.ValueKind == JsonValueKind.True,
        };

        if (e.TryGetProperty("createdAt", out var c) && c.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(c.GetString(), out var created))
            n.CreatedAt = created;

        if (e.TryGetProperty("expiresAt", out var x) && x.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(x.GetString(), out var exp))
            n.ExpiresAt = exp;

        if (e.TryGetProperty("actor", out var a) && a.ValueKind == JsonValueKind.Object)
        {
            n.ActorId = Str(a, "id");
            n.ActorName = Str(a, "displayName") ?? Str(a, "username");
            n.ActorAvatarUrl = Str(a, "avatarUrl");
        }

        if (e.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
        {
            n.WorldId = Str(d, "worldId");
            n.WorldName = Str(d, "worldName");
            n.InstanceId = Str(d, "instanceId");
        }
        return n;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// Persistent control-plane connection to the gateway (`/gateway`).
///
/// **This did not exist before, and its absence is why invites did nothing.** The gateway has
/// always had an `/internal/invite` endpoint and a `gwpush:{userId}` fan-out, but the only thing
/// in the client that ever opened a gateway socket was `RtcSignalClient`, which connects solely
/// for the duration of a P2P session. So for a normal dedicated-relay player there was no socket,
/// every push found `sockets.get(userId)` empty, and the server dutifully reported
/// `{delivered:false, reason:"offline"}` for a user who was staring at the game.
///
/// Distinct from `RtcSignalClient` on purpose: this one is up for the whole session, reconnects on
/// its own, and carries presence/notifications, while that one is per-instance and carries
/// signalling. Sharing a socket would tie notification delivery to whether the player happens to
/// be in a P2P world.
///
/// Threading mirrors `UdpTransport` and `RtcSignalClient`: receives run on a background task and
/// are queued; `Poll()` dispatches on the game thread so handlers may touch the scene tree.
public sealed class GatewayClient
{
    private ClientWebSocket _ws;
    private readonly ConcurrentQueue<string> _inbox = new();
    private CancellationTokenSource _cts;
    private string _url;
    private string _token;
    private volatile bool _closing;
    private int _reconnectAttempt;
    private double _reconnectIn;

    /// Fired on the gateway's `ready` ack, including after a reconnect — the cue to re-fetch the
    /// notification list, since pushes that happened while the socket was down were missed.
    public event Action Ready;
    public event Action<SerikaNotification, int> NotificationReceived; // (notification, unreadCount)
    public event Action<string[]> PresenceUpdate;                     // online friend ids
    public event Action<bool> ConnectionChanged;                      // isOpen

    public bool IsOpen => _ws?.State == WebSocketState.Open;
    /// Server-reported unread count from the most recent push.
    public int Unread { get; private set; }

    /// Base reconnect delay; backs off to `MaxReconnectDelay`.
    private const double BaseReconnectDelay = 2.0;
    private const double MaxReconnectDelay = 60.0;

    public void Connect(string gatewayWsUrl, string sessionToken)
    {
        if (string.IsNullOrEmpty(gatewayWsUrl) || string.IsNullOrEmpty(sessionToken)) return;
        _url = gatewayWsUrl;
        _token = sessionToken;
        _closing = false;
        _reconnectAttempt = 0;
        _ = ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        if (_closing) return;
        try
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _ws = new ClientWebSocket();
            var uri = new Uri($"{_url}?token={Uri.EscapeDataString(_token)}");
            await _ws.ConnectAsync(uri, _cts.Token);
            _reconnectAttempt = 0;
            _inbox.Enqueue("{\"type\":\"__open\"}");
            _ = ReceiveLoop(_cts.Token);
        }
        catch (Exception)
        {
            // Connect failures are expected (offline, gateway restarting) — schedule a retry
            // rather than surfacing an error the player can do nothing about.
            ScheduleReconnect();
        }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buf = new byte[32 * 1024];
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
        catch (Exception) { }

        if (!_closing)
        {
            _inbox.Enqueue("{\"type\":\"__closed\"}");
            ScheduleReconnect();
        }
    }

    private void ScheduleReconnect()
    {
        if (_closing) return;
        _reconnectAttempt++;
        // Exponential backoff, capped. A gateway that is down stays down for a while, and a
        // client per player hammering it on a 2 s timer is how a restart becomes an outage.
        _reconnectIn = Math.Min(BaseReconnectDelay * Math.Pow(2, _reconnectAttempt - 1), MaxReconnectDelay);
    }

    /// Drain queued messages and fire events, and drive the reconnect timer. Call each frame.
    public void Poll(double delta)
    {
        if (_reconnectIn > 0)
        {
            _reconnectIn -= delta;
            if (_reconnectIn <= 0)
            {
                _reconnectIn = 0;
                _ = ConnectAsync();
            }
        }

        while (_inbox.TryDequeue(out var raw))
        {
            try { Dispatch(raw); }
            catch (JsonException) { /* ignore malformed */ }
            catch (Exception) { }
        }
    }

    private void Dispatch(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var typeEl)) return;

        switch (typeEl.GetString())
        {
            case "__open":
                ConnectionChanged?.Invoke(true);
                break;
            case "__closed":
                ConnectionChanged?.Invoke(false);
                break;
            case "ready":
                Ready?.Invoke();
                break;
            case "notification":
            {
                if (!root.TryGetProperty("notification", out var n)) break;
                if (root.TryGetProperty("unread", out var u) && u.TryGetInt32(out int unread)) Unread = unread;
                NotificationReceived?.Invoke(SerikaNotification.FromJson(n), Unread);
                break;
            }
            case "presence":
            {
                if (!root.TryGetProperty("online", out var arr) || arr.ValueKind != JsonValueKind.Array) break;
                var ids = new string[arr.GetArrayLength()];
                int i = 0;
                foreach (var p in arr.EnumerateArray()) ids[i++] = p.GetString();
                PresenceUpdate?.Invoke(ids);
                break;
            }
        }
    }

    /// Ask which of `friendIds` are online.
    public void RequestPresence(string[] friendIds)
    {
        if (friendIds == null || friendIds.Length == 0) return;
        var ids = new JsonArray();
        foreach (var id in friendIds) ids.Add(id);
        Send(new JsonObject { ["type"] = "presence:friends", ["friends"] = ids });
    }

    /// Keepalive, so an idle socket is not reaped by an intermediary.
    public void Ping() => Send(new JsonObject { ["type"] = "ping" });

    private void Send(JsonObject obj)
    {
        if (_ws?.State != WebSocketState.Open) return;
        try
        {
            // JsonObject is the reflection-free DOM writer: this socket runs on iOS too,
            // where `JsonSerializer.Serialize(object)` has no reflection to fall back on.
            var bytes = Encoding.UTF8.GetBytes(obj.ToJsonString());
            _ = _ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token);
        }
        catch (Exception) { }
    }

    public void Close()
    {
        _closing = true;
        _reconnectIn = 0;
        try { _cts?.Cancel(); } catch { }
        try { _ = _ws?.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
    }
}
