using System;
using Godot;

namespace SerikaSocial.Discord;

/// <summary>
/// Game-facing Discord presence manager built on the native Discord Social SDK.
///
/// Owns one <see cref="DiscordClient"/> for the process lifetime: connects after boot,
/// re-pushes presence whenever the world/roster changes (and again after any reconnect, since
/// a fresh IPC session starts with no activity), and retries the connection with capped
/// backoff so a Discord that starts after the game is still picked up.
///
/// Everything runs on the main thread: the SDK queues its callbacks and delivers them inside
/// <see cref="Pump"/>, which Main._Process calls every frame. Nothing here ever blocks.
///
/// Joining: the activity's join secret IS the serikasocial://world/&lt;id&gt; deep link. When
/// the game is running, Discord delivers it to <see cref="JoinWorldRequested"/> in-process;
/// when it isn't, Discord launches the registered launch command and the same string arrives
/// as a command-line argument (see DeepLink.FromCommandLine).
/// </summary>
public static class DiscordRichPresence
{
    /// Serika's Discord application (the same client id the old hand-rolled IPC client used,
    /// so the portal-side config — name, serika_logo asset — already matches).
    public const ulong DefaultApplicationId = 1542129033683279955;

    private static DiscordClient _client;
    private static bool _enabled;
    private static bool _shuttingDown;
    private static ulong _appId;
    private static bool _tokenApplied;
    private static bool _reauthTried;

    // Reconnect: start fast, back off to once a minute while Discord stays away.
    private static double _reconnectDelay = 5.0;
    private static double _reconnectTimer;

    // Last presence state — what to push on (re)connect and on roster changes.
    private static string _worldName = "Browsing the menus";
    private static string _worldId;
    private static int _players = 1;
    private static int _maxPlayers = 16;
    private static ulong _sinceUnixMs;

    public static bool Ready { get; private set; }
    /// Why the SDK is unavailable (native lib missing, Android, …) — null when it's running.
    public static string UnavailableReason { get; private set; }

    /// Fires with a world id when the local player accepts a join from Discord. Main wires
    /// this to JoinWorldById on the game thread.
    public static event Action<string> JoinWorldRequested;

    /// Boot the SDK and start connecting. Non-fatal by design: Discord presence must never
    /// keep the game from running, so every failure path just disables the feature.
    public static void Init()
    {
        if (_enabled || _shuttingDown) return;

        if (OS.HasFeature("android"))
        {
            UnavailableReason = "Discord SDK is desktop-only (Android ships as an AAR)";
            return;
        }

        try
        {
            if (!DiscordNative.EnsureLoaded())
            {
                UnavailableReason = DiscordNative.LoadFailure ?? "native library not found";
                return;
            }

            ulong appId = ParseAppId(OS.GetEnvironment("SERIKA_DISCORD_APP_ID")) ?? DefaultApplicationId;
            var severity = OS.GetEnvironment("SERIKA_DISCORD_LOG") == "verbose"
                ? DiscordNative.LoggingSeverity.Info
                : DiscordNative.LoggingSeverity.Warning;

            _client = new DiscordClient(appId, severity);
            _client.StatusChanged += OnStatusChanged;
            _client.JoinSecretReceived += OnJoinSecret;
            _client.TokenExpired += OnTokenExpired;
            _client.LogMessage += (msg, sev) =>
            {
                if (sev >= DiscordNative.LoggingSeverity.Warning) GD.Print($"[Discord] {msg}");
            };

            _sinceUnixMs = NowUnixMs();

            // Register every boot — the registration is local to this machine and Discord
            // forgets it otherwise. Quoted so paths with spaces survive.
            try
            {
                string exe = OS.GetExecutablePath();
                _client.RegisterLaunchCommand($"\"{exe}\"");
            }
            catch (Exception e) { GD.Print($"[Discord] launch command not registered: {e.Message}"); }

            _enabled = true;
            _appId = appId;
            GD.Print($"[Discord] Social SDK {DiscordClient.VersionString()} (app {appId})");
            Boot();
        }
        catch (Exception e)
        {
            UnavailableReason = e.Message;
            GD.Print($"[Discord] init failed, presence disabled: {e.Message}");
            try { _client?.Dispose(); } catch { }
            _client = null;
        }
    }

    /// <summary>
    /// Unlike the old IPC protocol, the Social SDK will not authenticate a tokenless client:
    /// Connect before UpdateToken is a guaranteed gateway 4004. Boot therefore runs the
    /// OAuth flow — saved token → refresh → fresh Authorize (a one-time consent popup inside
    /// Discord) — and only connects once a token is applied.
    /// </summary>
    private static void Boot()
    {
        var saved = LoadToken();
        if (saved != null && saved.appId == _appId.ToString() && !string.IsNullOrEmpty(saved.refreshToken))
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (saved.expiresUnixMs > now + 60_000 && !string.IsNullOrEmpty(saved.accessToken))
                ApplyTokenThenConnect(saved.accessToken, (DiscordNative.AuthorizationTokenType)saved.tokenType);
            else
                RefreshAndConnect(saved.refreshToken);
            return;
        }
        BeginAuthorizeFlow();
    }

    private static void BeginAuthorizeFlow()
    {
        GD.Print("[Discord] no saved authorization — requesting consent (check your Discord client for a prompt)");
        _client.BeginAuthorize((ok, error, code, redirectUri) =>
        {
            if (!ok)
            {
                // Declined/popup-closed/Discord not logged in — retry next boot, not in a loop.
                UnavailableReason = $"authorization failed: {error}";
                GD.Print($"[Discord] authorize failed: {error}");
                return;
            }
            _client.ExchangeCodeForToken(code, redirectUri, token =>
            {
                if (!token.Ok)
                {
                    UnavailableReason = $"token exchange failed: {token.Error}";
                    GD.Print($"[Discord] {UnavailableReason}");
                    return;
                }
                SaveToken(token);
                ApplyTokenThenConnect(token.AccessToken, token.TokenType);
            });
        });
    }

    private static void RefreshAndConnect(string refreshToken)
    {
        _client.RefreshToken(refreshToken, token =>
        {
            if (!token.Ok || string.IsNullOrEmpty(token.AccessToken))
            {
                // Dead refresh token — clear it and do a fresh consent.
                GD.Print($"[Discord] token refresh failed ({token.Error}), re-authorizing");
                ClearToken();
                BeginAuthorizeFlow();
                return;
            }
            SaveToken(token);
            ApplyTokenThenConnect(token.AccessToken, token.TokenType);
        });
    }

    private static void ApplyTokenThenConnect(string accessToken, int tokenType) =>
        ApplyTokenThenConnect(accessToken, (DiscordNative.AuthorizationTokenType)tokenType);

    private static void ApplyTokenThenConnect(string accessToken, DiscordNative.AuthorizationTokenType type)
    {
        _client.ApplyToken(type, accessToken, (ok, error) =>
        {
            if (!ok)
            {
                GD.Print($"[Discord] saved token rejected ({error}), re-authorizing");
                ClearToken();
                BeginAuthorizeFlow();
                return;
            }
            _tokenApplied = true;
            _client.Connect();
        });
    }

    private static void OnTokenExpired()
    {
        if (!_enabled) return;
        GD.Print("[Discord] access token expired — refreshing");
        var saved = LoadToken();
        if (saved?.refreshToken == null) { ClearToken(); BeginAuthorizeFlow(); return; }
        RefreshAndConnect(saved.refreshToken);
    }

    /// Update presence content. `worldId` non-null means a joinable multiplayer world; null
    /// means Home/menus (no party block, no join offer). World changes restart the elapsed
    /// timer; peer-count changes don't.
    public static void UpdateState(string worldName, int playerCount, int maxPlayers, string worldId)
    {
        worldName ??= _worldName;
        bool worldChanged = _worldId != worldId || _worldName != worldName;
        _worldName = worldName;
        _worldId = worldId;
        _players = Math.Max(1, playerCount);
        _maxPlayers = Math.Max(1, maxPlayers);
        if (worldChanged) _sinceUnixMs = NowUnixMs();
        PushNow();
    }

    /// Call once per frame from Main._Process: pumps SDK callbacks and drives reconnects.
    public static void Poll(double delta)
    {
        if (!_enabled || _shuttingDown) return;
        try
        {
            _client.Pump();

            if (!Ready && _tokenApplied && _reconnectTimer > 0)
            {
                _reconnectTimer -= delta;
                if (_reconnectTimer <= 0)
                {
                    _reconnectTimer = 0;
                    _client.Connect();
                }
            }
        }
        catch (Exception e)
        {
            // A faulted pump must not take the frame loop down; disable and move on.
            GD.PrintErr($"[Discord] pump failed, disabling: {e.Message}");
            Disable();
        }
    }

    /// Clear the activity and tear the client down. Idempotent; called on app quit.
    public static void Shutdown()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        try { if (Ready) _client?.ClearRichPresence(); } catch { }
        try { _client?.Dispose(); } catch { }
        _client = null;
        _enabled = false;
        Ready = false;
    }

    private static void Disable()
    {
        _enabled = false;
        Ready = false;
        try { _client?.Dispose(); } catch { }
        _client = null;
        UnavailableReason = "disabled after runtime error";
    }

    private static void OnStatusChanged(DiscordNative.ClientStatus status, DiscordNative.ClientError error, int detail)
    {
        if (_shuttingDown) return;

        if (status == DiscordNative.ClientStatus.Ready)
        {
            if (!Ready) GD.Print("[Discord] connected");
            Ready = true;
            _reauthTried = false;
            _reconnectDelay = 5.0;
            _reconnectTimer = 0;
            PushNow(); // a fresh session has no activity — restore ours
        }
        else if (status == DiscordNative.ClientStatus.Disconnected)
        {
            if (Ready) GD.Print($"[Discord] disconnected ({error}, detail {detail}) — retrying");
            Ready = false;
            // 4004 after a token was applied means the token itself is dead — a plain
            // reconnect would just 4004 forever, so re-authenticate (once per session).
            if (detail == 4004 && _tokenApplied && !_reauthTried)
            {
                _reauthTried = true;
                GD.Print("[Discord] gateway rejected the token — re-authorizing");
                ClearToken();
                BeginAuthorizeFlow();
                return;
            }
            if (_tokenApplied)
            {
                _reconnectTimer = _reconnectDelay;
                _reconnectDelay = Math.Min(_reconnectDelay * 2.0, 60.0);
            }
        }
    }

    private static void OnJoinSecret(string secret)
    {
        if (_shuttingDown || string.IsNullOrEmpty(secret)) return;
        GD.Print($"[Discord] join secret received: {secret}");
        try
        {
            var intent = DeepLink.Parse(secret);
            if (intent.Kind == DeepLink.Kind.World)
            {
                JoinWorldRequested?.Invoke(intent.Arg);
                return;
            }
            // Defensive: a bare world id as the secret (if the launch path ever strips the
            // scheme) still joins.
            if (IsUuidish(secret)) JoinWorldRequested?.Invoke(secret);
        }
        catch (Exception e) { GD.PrintErr($"[Discord] join secret unparsable: {e.Message}"); }
    }

    private static void PushNow()
    {
        if (!Ready || _client == null) return;
        bool inWorld = _worldId != null;
        _client.UpdateRichPresence(new ActivitySpec
        {
            Details = inWorld ? $"In {_worldName}" : _worldName,
            State = inWorld ? $"{_players}/{_maxPlayers} players" : null,
            StartUnixMs = _sinceUnixMs,
            PartyId = _worldId,
            PartySize = _players,
            PartyMax = _maxPlayers,
            JoinSecret = inWorld ? $"{DeepLink.Scheme}://world/{_worldId}" : null,
            SmallImage = inWorld ? "world_icon" : null,
            SmallText = inWorld ? _worldName : null,
            ButtonLabel = "Join Serika Social",
            ButtonUrl = "https://social.serika.dev",
        });
    }

    private static ulong NowUnixMs() => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ── Token persistence ─────────────────────────────────────────────────────────────
    // user://discord_token.json, same trust level as the saved session JWT: local file in
    // the user data dir, never in source control, cleared on re-auth failures.

    private sealed class SavedToken
    {
        public string appId { get; set; }
        public int tokenType { get; set; }
        public string accessToken { get; set; }
        public string refreshToken { get; set; }
        public long expiresUnixMs { get; set; }
    }

    private const string TokenPath = "user://discord_token.json";

    private static SavedToken LoadToken()
    {
        try
        {
            string abs = ProjectSettings.GlobalizePath(TokenPath);
            if (!System.IO.File.Exists(abs)) return null;
            return System.Text.Json.JsonSerializer.Deserialize<SavedToken>(System.IO.File.ReadAllText(abs));
        }
        catch { return null; }
    }

    private static void SaveToken(TokenResult token)
    {
        try
        {
            var saved = new SavedToken
            {
                appId = _appId.ToString(),
                tokenType = (int)token.TokenType,
                accessToken = token.AccessToken,
                refreshToken = token.RefreshToken,
                expiresUnixMs = token.ExpiresUnixMs,
            };
            System.IO.File.WriteAllText(ProjectSettings.GlobalizePath(TokenPath),
                System.Text.Json.JsonSerializer.Serialize(saved));
        }
        catch (Exception e) { GD.Print($"[Discord] token not saved: {e.Message}"); }
    }

    private static void ClearToken()
    {
        try
        {
            string abs = ProjectSettings.GlobalizePath(TokenPath);
            if (System.IO.File.Exists(abs)) System.IO.File.Delete(abs);
        }
        catch { }
    }

    private static ulong? ParseAppId(string s) =>
        ulong.TryParse(s, out ulong id) && id != 0 ? id : null;

    private static bool IsUuidish(string s)
    {
        if (s.Length == 0 || s.Length > 64) return false;
        foreach (char c in s)
            if (!char.IsLetterOrDigit(c) && c != '-') return false;
        return true;
    }
}
