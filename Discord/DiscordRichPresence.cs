using System;
using Godot;
using SerikaSocial.UI;

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
    // One Authorize overlay per process. Closing it (or a 4004) must not pop another.
    private static bool _promptedThisProcess;
    // Init only constructs the client; Boot waits for the first Poll so RunCallbacks is live.
    private static bool _bootPending;
    private static ulong _lastPumpMs;
    private static Timer _pumpTimer;

    // Reconnect: start fast, back off to once a minute while Discord stays away.
    private static double _reconnectDelay = 5.0;
    private static double _reconnectTimer;

    // Authorize retry: when Discord isn't running the authorize fails silently. We retry
    // periodically so the game picks up Discord if it starts after the game.
    //
    // BOUNDED, and that bound is load-bearing. The retry used to be infinite (backing off only
    // to 60 s), so any authorize that reached Discord and failed was re-attempted for the whole
    // session — and an authorize that reaches Discord opens a BROWSER TAB. A misconfigured
    // redirect URI therefore produced a new "invalid redirect" tab every minute, forever.
    private const int MaxAuthorizeAttempts = 3;
    private static int _authorizeAttempts;
    private static double _authorizeRetryDelay = 10.0;
    private static double _authorizeRetryTimer;
    // A portal-side misconfiguration cannot be fixed by trying again. Set once, checked
    // everywhere that would otherwise re-prompt, cleared only by the Settings toggle.
    private static bool _authorizeTerminal;

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

        // Desktop only. Android ships the SDK as an AAR with Java glue, and iOS has no
        // dlopen-able drop at all — gating on "android" alone meant an iOS build would try to
        // extract and load a desktop .so that is not in the bundle.
        if (OS.HasFeature("mobile") || OS.HasFeature("android") || OS.HasFeature("ios"))
        {
            UnavailableReason = "Discord SDK is desktop-only";
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
            _bootPending = true;
            GD.Print($"[Discord] Social SDK {DiscordClient.VersionString()} (app {appId})");
            // Boot from Poll, not here. Init runs in the middle of Main._Ready — Authorize
            // would open Discord's in-app browser, then _Ready keeps building UI for many
            // seconds with no RunCallbacks, and Discord reports "browser no longer active".
            InstallAlwaysPump();
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
    /// Connect before UpdateToken is a guaranteed gateway 4004. Boot therefore prefers a
    /// saved refresh token, and only opens Discord's Authorize overlay when there is no
    /// grant yet. Closing that overlay is remembered — it does not come back next launch.
    /// </summary>
    private static void Boot()
    {
        if (_client == null || !_enabled) return;
        if (!DeviceProfile.Settings.DiscordPresence)
        {
            GD.Print("[Discord] presence off in settings — skipping");
            return;
        }

        var saved = LoadToken();
        bool haveRefresh = saved != null && saved.appId == _appId.ToString()
                           && !string.IsNullOrEmpty(saved.refreshToken);

        if (haveRefresh)
        {
            // A stored grant is the long-lived option: never pop Authorize while it works.
            SetConsent(DeviceProfile.Settings.DiscordConsentKind.Granted);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (saved.expiresUnixMs > now + 60_000 && !string.IsNullOrEmpty(saved.accessToken))
                ApplyTokenThenConnect(saved.accessToken, (DiscordNative.AuthorizationTokenType)saved.tokenType);
            else
                RefreshAndConnect(saved.refreshToken);
            return;
        }

        if (DeviceProfile.Settings.DiscordConsent == DeviceProfile.Settings.DiscordConsentKind.Declined)
        {
            GD.Print("[Discord] Authorize was declined — not asking again (Settings → Interface → Discord Rich Presence)");
            return;
        }

        BeginAuthorizeFlow();
    }

    /// Settings toggle. Turning presence on after a decline is the only way to see the
    /// overlay again. Turning it off aborts any in-flight prompt and disconnects.
    public static void SetEnabled(bool on)
    {
        DeviceProfile.Settings.DiscordPresence = on;
        if (!on)
        {
            DeviceProfile.Settings.Save();
            try { _client?.AbortAuthorize(); } catch { }
            try { _client?.Disconnect(); } catch { }
            Ready = false;
            _tokenApplied = false;
            GD.Print("[Discord] presence disabled");
            return;
        }

        // The toggle is the one deliberate "try again" in the product, so it clears every
        // give-up latch — including a portal misconfiguration the user has since fixed.
        _promptedThisProcess = false;
        _authorizeTerminal = false;
        _authorizeAttempts = 0;
        _authorizeRetryTimer = 0;
        _authorizeRetryDelay = 10.0;
        if (DeviceProfile.Settings.DiscordConsent == DeviceProfile.Settings.DiscordConsentKind.Declined)
            SetConsent(DeviceProfile.Settings.DiscordConsentKind.Unknown);
        else
            DeviceProfile.Settings.Save();

        if (_client == null)
        {
            // Init never ran (Android, missing lib) — nothing to do.
            return;
        }
        Boot();
    }

    private static void BeginAuthorizeFlow()
    {
        if (_client == null || _promptedThisProcess || _authorizeTerminal) return;
        if (_authorizeAttempts >= MaxAuthorizeAttempts)
        {
            GD.Print($"[Discord] giving up on authorize after {_authorizeAttempts} attempts this launch "
                     + "— it will try again next launch (or Settings → Interface → Discord Rich Presence)");
            _authorizeTerminal = true;
            return;
        }
        _promptedThisProcess = true;
        _authorizeAttempts++;
        GD.Print("[Discord] requesting consent — check your Discord client for a one-time Authorize prompt");
        _client.BeginAuthorize((ok, error, code, redirectUri) =>
        {
            if (_shuttingDown) return;
            if (!ok)
            {
                UnavailableReason = $"authorization failed: {error}";
                if (LooksLikeOAuthMisconfiguration(error))
                {
                    // Terminal by construction: the redirect URI / client config lives in the
                    // Discord Developer Portal, so every retry produces the identical error —
                    // and each one opens another browser tab at Discord's error page. Say
                    // exactly what to fix, once, and stop.
                    _authorizeTerminal = true;
                    UnavailableReason =
                        $"Discord app is misconfigured ({error}). Add http://127.0.0.1/callback under "
                        + "Developer Portal → OAuth2 → Redirects for application " + _appId
                        + ", and enable Public Client.";
                    GD.PrintErr($"[Discord] {UnavailableReason}");
                    GD.PrintErr("[Discord] not retrying — retrying cannot fix a portal-side redirect URI.");
                }
                else if (LooksLikeUserCancel(error))
                {
                    // Closing/cancelling the overlay is the long-lived "no" — Settings is
                    // how they get it back. Discord itself also remembers an Authorize.
                    GD.Print($"[Discord] authorize cancelled — not asking again until Settings → Interface → Discord Rich Presence");
                    DeviceProfile.Settings.DiscordPresence = false;
                    SetConsent(DeviceProfile.Settings.DiscordConsentKind.Declined);
                }
                else if (_authorizeAttempts < MaxAuthorizeAttempts)
                {
                    // Discord not running, overlay failed to attach, etc. Reset the prompt
                    // guard and schedule a retry so we pick up Discord if it starts later.
                    _promptedThisProcess = false;
                    _authorizeRetryTimer = _authorizeRetryDelay;
                    _authorizeRetryDelay = Math.Min(_authorizeRetryDelay * 2.0, 60.0);
                    GD.Print($"[Discord] authorize failed ({error}) — retry {_authorizeAttempts + 1}"
                             + $"/{MaxAuthorizeAttempts} in {_authorizeRetryTimer:F0}s");
                }
                else
                {
                    _authorizeTerminal = true;
                    GD.Print($"[Discord] authorize failed ({error}) — out of attempts for this launch");
                }
                return;
            }
            _client.ExchangeCodeForToken(code, redirectUri, token =>
            {
                if (_shuttingDown) return;
                if (!token.Ok)
                {
                    UnavailableReason = $"token exchange failed: {token.Error}";
                    GD.Print($"[Discord] {UnavailableReason}");
                    return;
                }
                SetConsent(DeviceProfile.Settings.DiscordConsentKind.Granted);
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
                GD.Print($"[Discord] token refresh failed ({token.Error})");
                if (LooksLikeInvalidGrant(token.Error))
                {
                    ClearToken();
                    if (DeviceProfile.Settings.DiscordConsent == DeviceProfile.Settings.DiscordConsentKind.Granted)
                        BeginAuthorizeFlow();
                }
                return;
            }
            SaveToken(token, previousRefresh: refreshToken);
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
                // Don't burn the grant on a single rejected apply (wrong type, Discord still
                // starting). Retry as Bearer, then refresh — Authorize is last resort.
                GD.Print($"[Discord] token apply failed ({error})");
                if (type != DiscordNative.AuthorizationTokenType.Bearer)
                {
                    ApplyTokenThenConnect(accessToken, DiscordNative.AuthorizationTokenType.Bearer);
                    return;
                }
                var saved = LoadToken();
                if (saved?.refreshToken != null)
                {
                    RefreshAndConnect(saved.refreshToken);
                    return;
                }
                return;
            }
            _tokenApplied = true;
            _client.Connect();
        });
    }

    private static void OnTokenExpired()
    {
        if (!_enabled || !DeviceProfile.Settings.DiscordPresence) return;
        GD.Print("[Discord] access token expired — refreshing");
        var saved = LoadToken();
        if (saved?.refreshToken == null)
        {
            ClearToken();
            if (DeviceProfile.Settings.DiscordConsent == DeviceProfile.Settings.DiscordConsentKind.Granted)
                BeginAuthorizeFlow();
            return;
        }
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
            // Discord's in-app authorize browser dies if RunCallbacks pauses for ~10 s.
            // Cap at ~60 Hz so a ProcessAlways timer plus _Process cannot double-tick reconnects.
            ulong now = NowUnixMs();
            if (now - _lastPumpMs >= 15)
            {
                _lastPumpMs = now;
                _client.Pump();
            }

            if (_bootPending)
            {
                _bootPending = false;
                Boot();
            }

            if (!Ready && _tokenApplied && _reconnectTimer > 0)
            {
                _reconnectTimer -= delta;
                if (_reconnectTimer <= 0)
                {
                    _reconnectTimer = 0;
                    _client.Connect();
                }
            }

            // Retry authorize when Discord wasn't running at boot.
            if (!Ready && !_tokenApplied && _authorizeRetryTimer > 0)
            {
                _authorizeRetryTimer -= delta;
                if (_authorizeRetryTimer <= 0)
                {
                    _authorizeRetryTimer = 0;
                    GD.Print("[Discord] retrying authorize (Discord may have started)");
                    BeginAuthorizeFlow();
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
        if (_pumpTimer != null && GodotObject.IsInstanceValid(_pumpTimer))
        {
            try { _pumpTimer.Stop(); _pumpTimer.QueueFree(); } catch { }
            _pumpTimer = null;
        }
        try { if (Ready) _client?.ClearRichPresence(); } catch { }
        try { _client?.Dispose(); } catch { }
        _client = null;
        _enabled = false;
        Ready = false;
    }

    /// Discord's authorize browser needs RunCallbacks even if the game window is unfocused
    /// (the player is in Discord clicking Authorize). A ProcessAlways timer keeps pumping
    /// if `_Process` is throttled.
    private static void InstallAlwaysPump()
    {
        if (_pumpTimer != null && GodotObject.IsInstanceValid(_pumpTimer)) return;
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null) return;
        _pumpTimer = new Timer
        {
            Name = "DiscordSdkPump",
            WaitTime = 0.05,
            Autostart = true,
            OneShot = false,
            ProcessMode = Node.ProcessModeEnum.Always,
        };
        _pumpTimer.Timeout += () => { if (!_shuttingDown) Poll(0.05); };
        tree.Root.CallDeferred(Node.MethodName.AddChild, _pumpTimer);
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
            _authorizeRetryTimer = 0;
            _authorizeRetryDelay = 10.0;
            _authorizeAttempts = 0;
            _authorizeTerminal = false;
            PushNow(); // a fresh session has no activity — restore ours
        }
        else if (status == DiscordNative.ClientStatus.Disconnected)
        {
            if (Ready) GD.Print($"[Discord] disconnected ({error}, detail {detail}) — retrying");
            Ready = false;
            // 4004 is also what Vesktop/arRPC returns (they speak IPC but cannot do the
            // Social SDK's authenticated handshake). That is not a dead grant — popping
            // Authorize every launch is the bug. Refresh once; if the token is actually
            // dead, RefreshAndConnect will Authorize. Otherwise just reconnect.
            if (detail == 4004 && !_reauthTried)
            {
                _reauthTried = true;
                var saved = LoadToken();
                if (saved?.refreshToken != null)
                {
                    GD.Print("[Discord] gateway 4004 — refreshing the saved grant, not re-prompting");
                    RefreshAndConnect(saved.refreshToken);
                    return;
                }
                // No grant to refresh. Vesktop/arRPC also 4004 with no token — do not
                // pop Authorize in a loop; the next Boot (next launch, or Settings) can.
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

    private static readonly System.Text.Json.JsonSerializerOptions TokenJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static SavedToken LoadToken()
    {
        try
        {
            string abs = ProjectSettings.GlobalizePath(TokenPath);
            if (!System.IO.File.Exists(abs)) return null;
            return System.Text.Json.JsonSerializer.Deserialize<SavedToken>(
                System.IO.File.ReadAllText(abs), TokenJson);
        }
        catch { return null; }
    }

    private static void SaveToken(TokenResult token, string previousRefresh = null)
    {
        try
        {
            // Refresh responses sometimes omit the refresh token; keep the one that still works.
            string refresh = !string.IsNullOrEmpty(token.RefreshToken) ? token.RefreshToken : previousRefresh;
            int type = token.TokenType == 0 ? (int)DiscordNative.AuthorizationTokenType.Bearer : token.TokenType;
            var saved = new SavedToken
            {
                appId = _appId.ToString(),
                tokenType = type,
                accessToken = token.AccessToken,
                refreshToken = refresh,
                expiresUnixMs = token.ExpiresUnixMs,
            };
            string abs = ProjectSettings.GlobalizePath(TokenPath);
            string tmp = abs + ".tmp";
            System.IO.File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(saved, TokenJson));
            System.IO.File.Copy(tmp, abs, overwrite: true);
            System.IO.File.Delete(tmp);
        }
        catch (Exception e) { GD.Print($"[Discord] token not saved: {e.Message}"); }
    }

    private static void SetConsent(DeviceProfile.Settings.DiscordConsentKind kind)
    {
        if (DeviceProfile.Settings.DiscordConsent == kind) return;
        DeviceProfile.Settings.DiscordConsent = kind;
        DeviceProfile.Settings.Save();
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

    private static bool LooksLikeUserCancel(string error)
    {
        if (string.IsNullOrEmpty(error)) return false;
        string e = error.ToLowerInvariant();
        // "abort" is what Discord reports when ITS browser times out because we stopped
        // pumping — that is not a player choice. Only an explicit cancel/deny is sticky.
        return e.Contains("cancel") || e.Contains("denied")
            || e.Contains("declin") || e.Contains("reject");
    }

    /// A portal-side OAuth configuration error: the app's registered redirect URIs, client type
    /// or scopes are wrong. Distinguished from a transient failure because retrying is
    /// *guaranteed* to reproduce it, and each attempt that reaches Discord opens a browser tab.
    /// This is the difference between one error line and a tab every minute all session.
    private static bool LooksLikeOAuthMisconfiguration(string error)
    {
        if (string.IsNullOrEmpty(error)) return false;
        string e = error.ToLowerInvariant();
        return e.Contains("redirect")            // "Invalid OAuth2 redirect_uri", missing redirect_uri
            || e.Contains("invalid_request")
            || e.Contains("invalid_client")
            || e.Contains("invalid_scope")
            || e.Contains("unauthorized_client");
    }

    private static bool LooksLikeInvalidGrant(string error)
    {
        if (string.IsNullOrEmpty(error)) return false;
        string e = error.ToLowerInvariant();
        return e.Contains("invalid_grant") || e.Contains("invalid grant") || e.Contains("revoked");
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
