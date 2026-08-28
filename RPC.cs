using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;

namespace SerikaSocial;

/// Rich presence funnel for the whole client. Two sinks, one call:
///  - Discord — handled by Discord.DiscordRichPresence on the official Social SDK (native
///    lib, join secrets, auto-reconnect). Forwarded from UpdateState below.
///  - Serika — REST push to POST /api/users/me/rich-presence on api.serika.chat,
///    authenticated with the serika-accounts JWT (not the game API session token).
///
/// Both are fire-and-forget: failures log a warning and the next heartbeat retries. The Serika
/// presence expires ~60s after the last push on Serika's side, so we re-push every 30s while
/// active; the Discord presence doesn't expire and is re-pushed only on state changes and
/// reconnects.
public static class RpcPresence
{
    // SerikaCord API is a separate service from the game API.
    private const string SerikaApiBaseUrl = "https://api.serika.chat";
    private static string _accountsToken;
    private static string _worldName = "Home";
    private static int _playerCount = 1;
    private static int _maxPlayers = 16;
    private static string _applicationId = "93fdd8eb-b425-4799-aaef-b745671d4153";
    private static double _lastPush;
    private static double _pushInterval = 30.0;
    private static bool _active;

    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// Initialise the Serika REST side with the serika-accounts token. (Discord presence
    /// starts at boot via DiscordRichPresence.Init and needs no credentials.)
    public static void Init(string apiBaseUrl, string sessionToken, string accountsToken = null)
    {
        _accountsToken = accountsToken;
        _active = true;
    }

    /// Update the current world/lobby state. Called by Main when joining/leaving worlds or
    /// when the peer count changes. `worldId` non-null marks a joinable multiplayer world —
    /// that's what turns on Discord's party block and Join button.
    public static void UpdateState(string worldName, int playerCount, int maxPlayers = 16,
        string worldId = null)
    {
        _worldName = worldName ?? "Unknown";
        _playerCount = Math.Max(1, playerCount);
        _maxPlayers = Math.Max(1, maxPlayers);

        Discord.DiscordRichPresence.UpdateState(worldName, playerCount, maxPlayers, worldId);
        PushNow();
    }

    /// Called every frame from Main._Process to re-push the Serika presence on an interval.
    /// (Discord pumping happens in the same tick — see the forwarding call below.)
    public static void Poll(double delta)
    {
        Discord.DiscordRichPresence.Poll(delta);
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
        Discord.DiscordRichPresence.Shutdown();
    }

    private static void PushNow()
    {
        if (string.IsNullOrEmpty(_accountsToken)) return;
        _ = PushSerikaAsync($"In {_worldName}", $"{_playerCount}/{_maxPlayers} players");
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
