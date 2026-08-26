using System;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Godot;

namespace SerikaSocial;

/// Rich presence manager: pushes activity to both Discord and SerikaCord so the user's status
/// shows what world they're in and how full the lobby is.
///
/// Discord RPC uses the official IPC pipe (Unix domain socket on Linux/macOS, \\\.\pipe on Windows).
/// Serika RPC uses the REST endpoint POST /api/users/me/rich-presence on api.serika.chat,
/// authenticated with the serika-accounts JWT (not the game API session token).
///
/// Both are fire-and-forget: failures log a warning and the next heartbeat retries. The presence
/// expires ~60s after the last push on Serika's side, so we re-push every 30s while active.
public static class RpcPresence
{
    // SerikaCord API is a separate service from the game API.
    private const string SerikaApiBaseUrl = "https://api.serika.chat";
    private static string _sessionToken;
    private static string _accountsToken;
    private static string _worldName = "Home";
    private static int _playerCount = 1;
    private static int _maxPlayers = 16;
    private static string _applicationId = "93fdd8eb-b425-4799-aaef-b745671d4153";
    private static double _lastPush;
    private static double _pushInterval = 30.0;
    private static bool _active;
    private static bool _discordConnected;

    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Discord IPC
    private const string DiscordClientId = "1542129033683279955";
    private static DiscordIpc _discord;

    /// Initialise with the game API URL (unused for Serika RPC — that hits api.serika.chat
    /// directly) and session token for Serika RPC.
    public static void Init(string apiBaseUrl, string sessionToken, string accountsToken = null)
    {
        _sessionToken = sessionToken;
        _accountsToken = accountsToken;
        _active = true;

        // Try Discord IPC connection (non-fatal if it fails — Discord may not be running).
        try
        {
            _discord = new DiscordIpc(DiscordClientId);
            _discord.Connect();
            _discordConnected = _discord.IsConnected;
            if (_discordConnected) GD.Print("RPC: Discord IPC connected");
            else GD.Print("RPC: Discord IPC not available (Discord not running?)");
        }
        catch (Exception e)
        {
            GD.Print($"RPC: Discord IPC init failed: {e.Message}");
            _discordConnected = false;
        }
    }

    /// Update the current world/lobby state. Called by Main when joining/leaving worlds or
    /// when peer count changes.
    public static void UpdateState(string worldName, int playerCount, int maxPlayers = 16)
    {
        _worldName = worldName ?? "Unknown";
        _playerCount = Math.Max(1, playerCount);
        _maxPlayers = Math.Max(1, maxPlayers);
        PushNow();
    }

    /// Called every frame from Main._Process to re-push presence on an interval.
    public static void Poll(double delta)
    {
        if (!_active) return;
        _lastPush += delta;
        if (_lastPush >= _pushInterval)
        {
            _lastPush = 0;
            PushNow();
        }
    }

    /// Stop pushing presence (e.g. on disconnect/logout).
    public static void Shutdown()
    {
        _active = false;
        _discord?.Disconnect();
        _discord = null;
        _discordConnected = false;
    }

    private static void PushNow()
    {
        string details = $"In {_worldName}";
        string state = $"{_playerCount}/{_maxPlayers} players";

        // Discord
        if (_discordConnected && _discord != null)
        {
            try
            {
                _discord.UpdatePresence(_worldName, details, state, _playerCount, _maxPlayers);
            }
            catch (Exception e)
            {
                GD.Print($"RPC: Discord push failed: {e.Message}");
                _discordConnected = false;
            }
        }
        else if (_discord == null || !_discord.IsConnected)
        {
            // Retry connection periodically — Discord may have started after the game.
            try
            {
                _discord?.Disconnect();
                _discord = new DiscordIpc(DiscordClientId);
                _discord.Connect();
                _discordConnected = _discord.IsConnected;
                if (_discordConnected)
                {
                    GD.Print("RPC: Discord IPC reconnected");
                    _discord.UpdatePresence(_worldName, details, state, _playerCount, _maxPlayers);
                }
            }
            catch { }
        }

        // Serika — hits api.serika.chat, not the game API
        if (!string.IsNullOrEmpty(_accountsToken))
        {
            _ = PushSerikaAsync(details, state);
        }
    }

    private static async Task PushSerikaAsync(string details, string state)
    {
        try
        {
            var payload = new
            {
                type = "game",
                name = "Serika Social",
                details = details,
                state = state,
                applicationId = _applicationId,
                assets = new
                {
                    largeImage = "serika_logo",
                    largeText = "Serika Social",
                },
                buttons = new[]
                {
                    new { label = "Join Serika Social", url = "https://social.serika.dev" },
                },
            };

            var json = JsonSerializer.Serialize(payload);
            var req = new HttpRequestMessage(HttpMethod.Post,
                $"{SerikaApiBaseUrl}/api/users/me/rich-presence")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accountsToken);

            var res = await Http.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                var body = await res.Content.ReadAsStringAsync();
                GD.Print($"RPC: Serika push failed ({res.StatusCode}): {body}");
            }
            else
            {
                GD.Print($"RPC: Serika presence pushed ({_worldName}, {_playerCount}/{_maxPlayers})");
            }
        }
        catch (Exception e)
        {
            GD.Print($"RPC: Serika push error: {e.Message}");
        }
    }
}

/// Minimal Discord IPC client using the named pipe protocol (opcode 1 = FRAME, opcode 2 = HANDSHAKE).
/// Implements just enough of the Discord RPC protocol to push presence updates. If Discord isn't
/// running or the pipe doesn't exist, all operations silently no-op.
internal sealed class DiscordIpc : IDisposable
{
    private readonly string _clientId;
    private Socket _socket;
    private System.IO.Pipes.NamedPipeClientStream _winPipe;
    private bool _connected;
    private int _nonce;

    public bool IsConnected => _connected;

    public DiscordIpc(string clientId)
    {
        _clientId = clientId;
    }

    public void Connect()
    {
        for (int i = 0; i < 10; i++)
        {
            if (TryConnect(i))
            {
                _connected = true;
                Handshake();
                return;
            }
        }
        _connected = false;
    }

    private bool TryConnect(int i)
    {
        if (OS.GetName() == "Windows")
        {
            try
            {
                _winPipe = new System.IO.Pipes.NamedPipeClientStream(".",
                    $"discord-ipc-{i}", System.IO.Pipes.PipeDirection.InOut,
                    System.IO.Pipes.PipeOptions.None);
                _winPipe.Connect(2000);
                return true;
            }
            catch { _winPipe = null; return false; }
        }
        else
        {
            string env = System.Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "/tmp";
            string path = $"{env}/discord-ipc-{i}";
            // Resolve symlinks (flatpak/vencord proxies the socket)
            try { path = System.IO.Path.GetFullPath(path); } catch { }
            if (!System.IO.File.Exists(path)) return false;
            try
            {
                _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                _socket.ReceiveTimeout = 5000;
                _socket.SendTimeout = 5000;
                _socket.Connect(new UnixDomainSocketEndPoint(path));
                return true;
            }
            catch
            {
                try { _socket?.Dispose(); } catch { }
                _socket = null;
                return false;
            }
        }
    }

    private void Handshake()
    {
        var payload = JsonSerializer.Serialize(new { v = 1, client_id = _clientId });
        Send(0, payload);
        ReadFrame();
    }

    public void UpdatePresence(string worldName, string details, string state, int playerCount, int maxPlayers)
    {
        if (!_connected) return;
        _nonce++;
        var payload = JsonSerializer.Serialize(new
        {
            cmd = "SET_ACTIVITY",
            args = new
            {
                pid = System.Environment.ProcessId,
                activity = new
                {
                    state = state,
                    details = details,
                    assets = new
                    {
                        large_image = "serika_logo",
                        large_text = "Serika Social",
                        small_image = "world_icon",
                        small_text = worldName,
                    },
                    timestamps = new { start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                },
            },
            nonce = _nonce.ToString(),
        });
        Send(1, payload);
        ReadFrame();
    }

    private void Send(int opcode, string payload)
    {
        var body = Encoding.UTF8.GetBytes(payload);
        var header = new byte[8];
        BitConverter.TryWriteBytes(header.AsSpan(0, 4), opcode);
        BitConverter.TryWriteBytes(header.AsSpan(4, 4), body.Length);
        if (_socket != null)
        {
            _socket.Send(header);
            _socket.Send(body);
        }
        else if (_winPipe != null)
        {
            _winPipe.Write(header, 0, 8);
            _winPipe.Write(body, 0, body.Length);
            _winPipe.Flush();
        }
    }

    private void ReadFrame()
    {
        try
        {
            var header = new byte[8];
            int read = 0;
            while (read < 8)
            {
                int n;
                if (_socket != null) n = _socket.Receive(header, read, 8 - read, SocketFlags.None);
                else if (_winPipe != null) n = _winPipe.Read(header, read, 8 - read);
                else return;
                if (n <= 0) break;
                read += n;
            }
            if (read < 8) return;
            int len = BitConverter.ToInt32(header, 4);
            if (len <= 0 || len > 65536) return;
            var body = new byte[len];
            read = 0;
            while (read < len)
            {
                int n;
                if (_socket != null) n = _socket.Receive(body, read, len - read, SocketFlags.None);
                else if (_winPipe != null) n = _winPipe.Read(body, read, len - read);
                else return;
                if (n <= 0) break;
                read += n;
            }
        }
        catch { _connected = false; }
    }

    public void Disconnect()
    {
        try { _socket?.Dispose(); } catch { }
        try { _winPipe?.Dispose(); } catch { }
        _socket = null;
        _winPipe = null;
        _connected = false;
    }

    public void Dispose() => Disconnect();
}
