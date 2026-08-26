using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Godot;

namespace SerikaSocial;

/// Rich presence manager: pushes activity to both Discord and SerikaCord so the user's status
/// shows what world they're in and how full the lobby is.
///
/// Discord RPC uses the official IPC pipe (named socket on Linux/macOS, \\.\pipe on Windows).
/// Serika RPC uses the REST endpoint PUT /api/v1/users/@me/rich-presence with a bearer token.
///
/// Both are fire-and-forget: failures log a warning and the next heartbeat retries. The presence
/// expires ~60s after the last push on Serika's side, so we re-push every 30s while active.
public static class RpcPresence
{
    private static string _serikaApiUrl;
    private static string _sessionToken;
    private static string _worldName = "Home";
    private static int _playerCount = 1;
    private static int _maxPlayers = 16;
    private static string _applicationId = "93fdd8eb-b425-4799-aaef-b745671d4153";
    private static double _lastPush;
    private static double _pushInterval = 30.0;
    private static bool _active;
    private static bool _connected;

    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Discord IPC
    private const string DiscordClientId = "1542129033683279955";
    private static DiscordIpc _discord;

    /// Initialise with the Serika API URL and session token for Serika RPC.
    public static void Init(string apiBaseUrl, string sessionToken)
    {
        _serikaApiUrl = apiBaseUrl?.TrimEnd('/');
        _sessionToken = sessionToken;
        _active = true;

        // Try Discord IPC connection (non-fatal if it fails — Discord may not be running).
        try
        {
            _discord = new DiscordIpc(DiscordClientId);
            _discord.Connect();
            _connected = _discord.IsConnected;
            if (_connected) GD.Print("RPC: Discord IPC connected");
            else GD.Print("RPC: Discord IPC not available (Discord not running?)");
        }
        catch (Exception e)
        {
            GD.Print($"RPC: Discord IPC init failed: {e.Message}");
            _connected = false;
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
        _connected = false;
    }

    private static void PushNow()
    {
        string details = $"In {_worldName}";
        string state = $"{_playerCount}/{_maxPlayers} players";

        // Discord
        if (_connected && _discord != null)
        {
            try
            {
                _discord.UpdatePresence(_worldName, details, state, _playerCount, _maxPlayers);
            }
            catch (Exception e)
            {
                GD.Print($"RPC: Discord push failed: {e.Message}");
                _connected = false;
            }
        }

        // Serika
        if (!string.IsNullOrEmpty(_serikaApiUrl) && !string.IsNullOrEmpty(_sessionToken))
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
                application_id = _applicationId,
                assets = new
                {
                    large_image = "serika_logo",
                    large_text = "Serika Social",
                },
                buttons = new[]
                {
                    new { label = "Join Serika Social", url = "https://social.serika.dev" },
                },
            };

            var json = JsonSerializer.Serialize(payload);
            var req = new HttpRequestMessage(HttpMethod.Put,
                $"{_serikaApiUrl}/api/v1/users/@me/rich-presence")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _sessionToken);

            var res = await Http.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                var body = await res.Content.ReadAsStringAsync();
                GD.Print($"RPC: Serika push failed ({res.StatusCode}): {body}");
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
    private System.IO.FileStream _pipeStream;
    private bool _connected;
    private int _nonce;

    public bool IsConnected => _connected;

    public DiscordIpc(string clientId)
    {
        _clientId = clientId;
    }

    public void Connect()
    {
        // Try pipe paths: discord-ipc-0 through discord-ipc-9
        for (int i = 0; i < 10; i++)
        {
            string path = GetPipePath(i);
            if (TryOpenPipe(path))
            {
                _connected = true;
                Handshake();
                return;
            }
        }
        _connected = false;
    }

    private static string GetPipePath(int i)
    {
        if (OS.GetName() == "Windows")
            return $"\\\\.\\pipe\\discord-ipc-{i}";
        // Linux / macOS: XDG runtime dir or /tmp
        string env = System.Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "/tmp";
        return $"{env}/discord-ipc-{i}";
    }

    private bool TryOpenPipe(string path)
    {
        try
        {
            // Godot's FileAccess can open named pipes on some platforms, but for cross-platform
            // reliability we use a raw socket approach. Since Godot doesn't expose AF_UNIX directly,
            // we fall back to checking if the file exists and using a .NET approach.
            if (!System.IO.File.Exists(path) && OS.GetName() != "Windows")
                return false;

            // Use System.IO for named pipe on Linux/macOS
            // On Windows, named pipes are \\.\pipe\ and can be opened with .NET
            _pipeStream = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void Handshake()
    {
        var payload = JsonSerializer.Serialize(new { v = 1, client_id = _clientId });
        Send(0, payload); // opcode 0 = HANDSHAKE
        ReadFrame(); // read the READY response (we don't parse it, just consume)
    }

    public void UpdatePresence(string worldName, string details, string state, int playerCount, int maxPlayers)
    {
        if (!_connected || _pipeStream == null) return;

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
        Send(1, payload); // opcode 1 = FRAME
        ReadFrame(); // consume response
    }

    private void Send(int opcode, string payload)
    {
        if (_pipeStream == null) return;
        var body = Encoding.UTF8.GetBytes(payload);
        var header = new byte[8];
        // Little-endian: opcode (4 bytes) + length (4 bytes)
        BitConverter.GetBytes(opcode).CopyTo(header, 0);
        BitConverter.GetBytes(body.Length).CopyTo(header, 4);
        _pipeStream.Write(header, 0, 8);
        _pipeStream.Write(body, 0, body.Length);
        _pipeStream.Flush();
    }

    private void ReadFrame()
    {
        if (_pipeStream == null) return;
        try
        {
            var header = new byte[8];
            int read = 0;
            while (read < 8)
            {
                int n = _pipeStream.Read(header, read, 8 - read);
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
                int n = _pipeStream.Read(body, read, len - read);
                if (n <= 0) break;
                read += n;
            }
        }
        catch
        {
            _connected = false;
        }
    }

    public void Disconnect()
    {
        try { _pipeStream?.Dispose(); } catch { }
        _pipeStream = null;
        _connected = false;
    }

    public void Dispose()
    {
        Disconnect();
    }
}
