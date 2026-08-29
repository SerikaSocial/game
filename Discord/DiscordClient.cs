using System;
using System.Text;
using static SerikaSocial.Discord.DiscordNative;

namespace SerikaSocial.Discord;

/// <summary>
/// Everything one rich-presence push needs, as plain managed data. The client translates this
/// into the SDK's opaque activity tree, ships it, and drops the tree — the native objects only
/// need to live for the duration of the UpdateRichPresence call.
/// </summary>
public sealed class ActivitySpec
{
    public string Name = "Serika Social";
    /// Second line, e.g. "In The Commons".
    public string Details;
    /// Third line, e.g. "3/16 players".
    public string State;
    /// Unix-ms "started playing this world" — kept stable per world so Discord's elapsed
    /// timer doesn't reset on every re-push.
    public ulong? StartUnixMs;
    /// Non-null → show a party block (Discord renders "current/max" from these).
    public string PartyId;
    public int PartySize = 1;
    public int PartyMax = 16;
    /// Non-null → Discord offers Join/Ask-to-Join on the player's profile. Serika uses the
    /// serikasocial://world/&lt;id&gt; deep link itself as the secret so every surface that
    /// receives it (in-process callback, cold-launch argv, URL handler) parses identically.
    public string JoinSecret;
    public string LargeImage = "serika_logo";
    public string LargeText = "Serika Social";
    public string SmallImage;
    public string SmallText;
    public string ButtonLabel;
    public string ButtonUrl;
}

/// One GetToken/RefreshToken outcome as plain managed data.
public sealed record TokenResult(bool Ok, string Error, string AccessToken, string RefreshToken,
    int TokenType, int ExpiresInSec)
{
    public long ExpiresUnixMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ExpiresInSec * 1000L;
}

/// <summary>
/// Managed veneer over one Discord_Client handle: owns its lifetime, keeps every callback
/// delegate rooted (the SDK stores raw function pointers — a collected delegate is a crash),
/// and exposes presence/join/status operations. Not thread-safe: the Discord SDK queues its
/// callbacks and delivers them on whichever thread calls RunCallbacks — call <see cref="Pump"/>
/// from the main frame loop and everything here stays on the game thread.
/// </summary>
internal sealed unsafe class DiscordClient : IDisposable
{
    private DiscordNative.Discord_Client _client;
    private bool _init;
    private bool _authorizePending;
    // PKCE secret for the authorize→token exchange; lives from BeginAuthorize to GetToken.
    private string _codeVerifierSecret;

    // Rooted for the lifetime of the client — see the class comment.
    private readonly OnStatusChanged _statusDelegate;
    private readonly OnActivityJoin _joinDelegate;
    private readonly LogCb _logDelegate;
    private readonly UpdateRichPresenceCb _presenceDelegate;
    private readonly DiscordNative.AuthorizationCb _authorizeDelegate;
    private readonly DiscordNative.TokenExchangeCb _tokenDelegate;
    private readonly DiscordNative.UpdateTokenCb _updateTokenDelegate;
    private readonly DiscordNative.NoArgCb _tokenExpiredDelegate;

    public event Action<ClientStatus, ClientError, int> StatusChanged;
    public event Action<string> JoinSecretReceived;
    public event Action<string, LoggingSeverity> LogMessage;
    public event Action TokenExpired;

    public ulong ApplicationId { get; }

    public DiscordClient(ulong applicationId, LoggingSeverity minSeverity = LoggingSeverity.Warning)
    {
        ApplicationId = applicationId;

        _statusDelegate = (status, error, detail, _) =>
        {
            try { StatusChanged?.Invoke(status, error, detail); }
            catch (Exception e) { Godot.GD.PushError($"[Discord] status handler threw: {e.Message}"); }
        };
        _joinDelegate = (joinSecret, _) =>
        {
            try
            {
                var secret = joinSecret;
                JoinSecretReceived?.Invoke(ReadAndFree(ref secret));
            }
            catch (Exception e) { Godot.GD.PushError($"[Discord] join handler threw: {e.Message}"); }
        };
        _logDelegate = (message, severity, _) =>
        {
            try
            {
                var msg = message;
                LogMessage?.Invoke(ReadAndFree(ref msg), severity);
            }
            catch { /* logging must never take the process down */ }
        };
        _presenceDelegate = (ref Discord_ClientResult result, IntPtr _) =>
        {
            try
            {
                if (result.opaque == IntPtr.Zero) return;
                if (!Discord_ClientResult_Successful(ref result))
                {
                    Discord_String s = default;
                    Discord_ClientResult_ToString(ref result, ref s);
                    Godot.GD.Print($"[Discord] presence push rejected: {ReadAndFree(ref s)}");
                }
            }
            catch { }
        };
        _authorizeDelegate = (ref Discord_ClientResult result, Discord_String code, Discord_String redirectUri, IntPtr _) =>
        {
            try
            {
                _authorizePending = false;
                bool ok = result.opaque != IntPtr.Zero && Discord_ClientResult_Successful(ref result);
                string error = ok ? null : Describe(ref result);
                var codeStr = code;
                var redirStr = redirectUri;
                _onAuthorize?.Invoke(ok, error, ReadAndFree(ref codeStr), ReadAndFree(ref redirStr));
            }
            catch (Exception e) { Godot.GD.PushError($"[Discord] authorize handler threw: {e.Message}"); }
        };
        _tokenDelegate = (ref Discord_ClientResult result, Discord_String accessToken, Discord_String refreshToken,
            DiscordNative.AuthorizationTokenType tokenType, int expiresIn, Discord_String scopes, IntPtr _) =>
        {
            try
            {
                bool ok = result.opaque != IntPtr.Zero && Discord_ClientResult_Successful(ref result);
                var at = accessToken;
                var rt = refreshToken;
                var sc = scopes;
                var tr = ok
                    ? new TokenResult(true, null, ReadAndFree(ref at), ReadAndFree(ref rt), (int)tokenType, expiresIn)
                    : new TokenResult(false, Describe(ref result), null, null, (int)tokenType, 0);
                ReadAndFree(ref sc);
                _onTokens?.Invoke(tr);
            }
            catch (Exception e) { Godot.GD.PushError($"[Discord] token handler threw: {e.Message}"); }
        };
        _updateTokenDelegate = (ref Discord_ClientResult result, IntPtr _) =>
        {
            try
            {
                bool ok = result.opaque != IntPtr.Zero && Discord_ClientResult_Successful(ref result);
                _onTokenApplied?.Invoke(ok, ok ? null : Describe(ref result));
            }
            catch (Exception e) { Godot.GD.PushError($"[Discord] update-token handler threw: {e.Message}"); }
        };
        _tokenExpiredDelegate = _ =>
        {
            try { TokenExpired?.Invoke(); }
            catch (Exception e) { Godot.GD.PushError($"[Discord] token-expiry handler threw: {e.Message}"); }
        };

        Discord_Client_Init(ref _client);
        _init = true;
        Discord_Client_SetApplicationId(ref _client, applicationId);
        Discord_Client_SetStatusChangedCallback(ref _client, _statusDelegate, IntPtr.Zero, IntPtr.Zero);
        Discord_Client_SetActivityJoinCallback(ref _client, _joinDelegate, IntPtr.Zero, IntPtr.Zero);
        Discord_Client_SetTokenExpirationCallback(ref _client, _tokenExpiredDelegate, IntPtr.Zero, IntPtr.Zero);
        Discord_Client_AddLogCallback(ref _client, _logDelegate, IntPtr.Zero, IntPtr.Zero, minSeverity);
        // Overlay is Windows-only (D3D/GL). Setting a PID on Linux/macOS makes Discord's
        // in-app authorize browser attach to a window it then cannot find, and after ~10 s
        // it shows "this browser is no longer active" while the game is still running.
        if (Godot.OS.GetName() == "Windows")
        {
            try { Discord_Client_SetGameWindowPid(ref _client, (int)Godot.OS.GetProcessId()); }
            catch { }
        }
    }

    public ClientStatus Status => _init ? Discord_Client_GetStatus(ref _client) : ClientStatus.Disconnected;

    /// Deliver queued SDK callbacks. Call once per frame from the main thread.
    public void Pump() => Discord_RunCallbacks();

    /// Start the asynchronous connect. Outcomes arrive via <see cref="StatusChanged"/>.
    public void Connect() => Discord_Client_Connect(ref _client);

    public void Disconnect() => Discord_Client_Disconnect(ref _client);

    public bool IsAuthenticated => _init && Discord_Client_IsAuthenticated(ref _client);

    public void AbortAuthorize()
    {
        if (!_init || !_authorizePending) return;
        try { Discord_Client_AbortAuthorize(ref _client); } catch { }
        _authorizePending = false;
    }

    // ── Auth bootstrap ────────────────────────────────────────────────────────────────

    // Continuation slots — assigned per call, invoked once from the SDK callback. One auth
    // flow runs at a time, so a single slot of each is enough.
    private Action<bool, string, string, string> _onAuthorize;   // ok, error, code, redirectUri
    private Action<TokenResult> _onTokens;
    private Action<bool, string> _onTokenApplied;                // ok, error

    /// The scopes rich presence needs — the SDK's own default, so portal-side scope config
    /// can never drift from what we request.
    public static string DefaultPresenceScopes()
    {
        if (!EnsureLoaded()) return "identify";
        Discord_String s = default;
        Discord_Client_GetDefaultPresenceScopes(ref s);
        return ReadAndFree(ref s) ?? "identify";
    }

    /// <summary>
    /// Start the OAuth consent flow. Discord shows an Authorize prompt inside the local
    /// client; the outcome lands in <paramref name="onDone"/> with a one-time code (plus the
    /// redirect URI to hand back). The user can sit on that prompt for minutes — nothing here
    /// times out on its own.
    /// </summary>
    public void BeginAuthorize(Action<bool, string, string, string> onDone)
    {
        if (!_init || _authorizePending) return;

        Discord_AuthorizationArgs args = default;
        Discord_AuthorizationCodeVerifier verifier = default;
        Discord_AuthorizationCodeChallenge challenge = default;
        Discord_AuthorizationArgs_Init(ref args);
        try
        {
            // PKCE: the SDK mints a verifier; the challenge goes into the request and the
            // verifier's secret is needed again at GetToken time.
            Discord_Client_CreateAuthorizationCodeVerifier(ref _client, ref verifier);
            Discord_String secret = default;
            Discord_AuthorizationCodeVerifier_Verifier(ref verifier, ref secret);
            _codeVerifierSecret = ReadAndFree(ref secret);
            Discord_AuthorizationCodeChallenge_Init(ref challenge);
            Discord_AuthorizationCodeVerifier_Challenge(ref verifier, ref challenge);

            Discord_AuthorizationArgs_SetClientId(ref args, ApplicationId);
            byte[] scopes = Encoding.UTF8.GetBytes(DefaultPresenceScopes());
            fixed (byte* sp = scopes)
            {
                var s = new Discord_String { ptr = (IntPtr)sp, size = (nuint)scopes.Length };
                Discord_AuthorizationArgs_SetScopes(ref args, s);
            }
            DiscordNative.IntegrationType integration = DiscordNative.IntegrationType.UserInstall;
            Discord_AuthorizationCodeChallenge* pc = &challenge;
            DiscordNative.IntegrationType* pi = &integration;
            Discord_AuthorizationArgs_SetCodeChallenge(ref args, pc);
            Discord_AuthorizationArgs_SetIntegrationType(ref args, pi);

            _onAuthorize = onDone;
            _authorizePending = true;
            Discord_Client_Authorize(ref _client, ref args, _authorizeDelegate, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            // Args first — they hold pointers into the challenge. The SDK copies during
            // Authorize; after that these are ours to free. Reverse order is a UAF.
            Discord_AuthorizationArgs_Drop(ref args);
            if (challenge.opaque != IntPtr.Zero) Discord_AuthorizationCodeChallenge_Drop(ref challenge);
            if (verifier.opaque != IntPtr.Zero) Discord_AuthorizationCodeVerifier_Drop(ref verifier);
        }
    }

    /// Exchange an authorize code for access/refresh tokens (the PKCE verifier from
    /// BeginAuthorize is applied automatically).
    public void ExchangeCodeForToken(string code, string redirectUri, Action<TokenResult> onDone)
    {
        if (!_init) return;
        _onTokens = onDone;
        byte[] bc = Encoding.UTF8.GetBytes(code ?? string.Empty);
        byte[] bv = Encoding.UTF8.GetBytes(_codeVerifierSecret ?? string.Empty);
        byte[] br = Encoding.UTF8.GetBytes(redirectUri ?? string.Empty);
        fixed (byte* pc = bc)
        fixed (byte* pv = bv)
        fixed (byte* pr = br)
        {
            Discord_Client_GetToken(ref _client, ApplicationId,
                new Discord_String { ptr = (IntPtr)pc, size = (nuint)bc.Length },
                new Discord_String { ptr = (IntPtr)pv, size = (nuint)bv.Length },
                new Discord_String { ptr = (IntPtr)pr, size = (nuint)br.Length },
                _tokenDelegate, IntPtr.Zero, IntPtr.Zero);
        }
        _codeVerifierSecret = null;
    }

    /// Hand a (possibly saved) access token to the SDK. Connect only after this succeeds —
    /// connecting tokenless is a guaranteed gateway 4004.
    public void ApplyToken(DiscordNative.AuthorizationTokenType type, string token, Action<bool, string> onDone)
    {
        if (!_init) return;
        _onTokenApplied = onDone;
        byte[] b = Encoding.UTF8.GetBytes(token ?? string.Empty);
        fixed (byte* p = b)
        {
            var s = new Discord_String { ptr = (IntPtr)p, size = (nuint)b.Length };
            Discord_Client_UpdateToken(ref _client, type, s, _updateTokenDelegate, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// Refresh an expired access token.
    public void RefreshToken(string refreshToken, Action<TokenResult> onDone)
    {
        if (!_init) return;
        _onTokens = onDone;
        byte[] b = Encoding.UTF8.GetBytes(refreshToken ?? string.Empty);
        fixed (byte* p = b)
        {
            var s = new Discord_String { ptr = (IntPtr)p, size = (nuint)b.Length };
            Discord_Client_RefreshToken(ref _client, ApplicationId, s, _tokenDelegate, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private static string Describe(ref Discord_ClientResult result)
    {
        if (result.opaque == IntPtr.Zero) return "unknown error";
        Discord_String s = default;
        Discord_ClientResult_ToString(ref result, ref s);
        return ReadAndFree(ref s) ?? "unknown error";
    }

    /// Tell Discord how to launch the game for a cold join (registration is local to this
    /// machine and must be refreshed every boot, per the SDK docs).
    public bool RegisterLaunchCommand(string command)
    {
        bool ok = false;
        WithStr(command, s => ok = Discord_Client_RegisterLaunchCommand(ref _client, ApplicationId, s));
        return ok;
    }

    public void UpdateRichPresence(ActivitySpec spec)
    {
        if (!_init || spec == null) return;

        Discord_Activity act = default;
        Discord_Activity_Init(ref act);
        try
        {
            SetName(ref act, spec.Name);
            Discord_Activity_SetType(ref act, ActivityTypes.Playing);
            SetLine(ref act, spec.Details, details: true);
            SetLine(ref act, spec.State, details: false);
            ApplyTimestamps(ref act, spec.StartUnixMs);
            if (spec.PartyId != null) ApplyParty(ref act, spec);
            if (spec.JoinSecret != null) ApplySecrets(ref act, spec.JoinSecret);
            if (spec.LargeImage != null || spec.SmallImage != null) ApplyAssets(ref act, spec);
            if (spec.ButtonLabel != null && spec.ButtonUrl != null) AddButton(ref act, spec);

            Discord_Client_UpdateRichPresence(ref _client, ref act, _presenceDelegate, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            // The SDK copies synchronously inside UpdateRichPresence, so every native object
            // can be torn down on the way out — exactly what the C++ RAII wrapper does.
            Discord_Activity_Drop(ref act);
        }
    }

    // Each Apply* helper owns its sub-struct end to end (init → set → attach → drop). The
    // pattern matters: a local whose address is taken (for the C API's optional struct
    // pointer params) must never also be captured by a lambda, so nothing here uses the
    // WithStr/WithOptStr lambda helpers — the string pins are hand-rolled per field.

    private static void SetName(ref Discord_Activity act, string value)
    {
        byte[] b = Encoding.UTF8.GetBytes(value ?? string.Empty);
        fixed (byte* p = b)
        {
            var s = new Discord_String { ptr = (IntPtr)p, size = (nuint)b.Length };
            Discord_Activity_SetName(ref act, s);
        }
    }

    /// details/state are the two optional text lines of the profile card.
    private static void SetLine(ref Discord_Activity act, string value, bool details)
    {
        if (value == null)
        {
            if (details) Discord_Activity_SetDetails(ref act, null);
            else Discord_Activity_SetState(ref act, null);
            return;
        }
        byte[] b = Encoding.UTF8.GetBytes(value);
        fixed (byte* p = b)
        {
            var s = new Discord_String { ptr = (IntPtr)p, size = (nuint)b.Length };
            Discord_String* ps = &s;
            if (details) Discord_Activity_SetDetails(ref act, ps);
            else Discord_Activity_SetState(ref act, ps);
        }
    }

    private static void ApplyTimestamps(ref Discord_Activity act, ulong? startUnixMs)
    {
        if (startUnixMs is not ulong start) return;
        Discord_ActivityTimestamps stamps = default;
        Discord_ActivityTimestamps_Init(ref stamps);
        try
        {
            Discord_ActivityTimestamps_SetStart(ref stamps, start);
            Discord_ActivityTimestamps* ps = &stamps;
            Discord_Activity_SetTimestamps(ref act, ps);
        }
        finally
        {
            if (stamps.opaque != IntPtr.Zero) Discord_ActivityTimestamps_Drop(ref stamps);
        }
    }

    private static void ApplyParty(ref Discord_Activity act, ActivitySpec spec)
    {
        Discord_ActivityParty party = default;
        Discord_ActivityParty_Init(ref party);
        try
        {
            byte[] b = Encoding.UTF8.GetBytes(spec.PartyId);
            fixed (byte* p = b)
            {
                var s = new Discord_String { ptr = (IntPtr)p, size = (nuint)b.Length };
                Discord_ActivityParty_SetId(ref party, s);
            }
            Discord_ActivityParty_SetCurrentSize(ref party, Math.Max(1, spec.PartySize));
            Discord_ActivityParty_SetMaxSize(ref party, Math.Max(1, spec.PartyMax));
            // Public = friends can hit Join directly; Private forces Ask-to-Join.
            Discord_ActivityParty_SetPrivacy(ref party, ActivityPartyPrivacy.Public);
            Discord_ActivityParty* pp = &party;
            Discord_Activity_SetParty(ref act, pp);
        }
        finally
        {
            if (party.opaque != IntPtr.Zero) Discord_ActivityParty_Drop(ref party);
        }
    }

    private static void ApplySecrets(ref Discord_Activity act, string joinSecret)
    {
        Discord_ActivitySecrets secrets = default;
        Discord_ActivitySecrets_Init(ref secrets);
        try
        {
            byte[] b = Encoding.UTF8.GetBytes(joinSecret);
            fixed (byte* p = b)
            {
                var s = new Discord_String { ptr = (IntPtr)p, size = (nuint)b.Length };
                Discord_ActivitySecrets_SetJoin(ref secrets, s);
            }
            Discord_ActivitySecrets* ps = &secrets;
            Discord_Activity_SetSecrets(ref act, ps);
        }
        finally
        {
            if (secrets.opaque != IntPtr.Zero) Discord_ActivitySecrets_Drop(ref secrets);
        }
    }

    private static void ApplyAssets(ref Discord_Activity act, ActivitySpec spec)
    {
        Discord_ActivityAssets assets = default;
        Discord_ActivityAssets_Init(ref assets);
        try
        {
            SetAsset(ref assets, spec.LargeImage, 0);
            SetAsset(ref assets, spec.LargeText, 1);
            SetAsset(ref assets, spec.SmallImage, 2);
            SetAsset(ref assets, spec.SmallText, 3);
            Discord_ActivityAssets* pa = &assets;
            Discord_Activity_SetAssets(ref act, pa);
        }
        finally
        {
            if (assets.opaque != IntPtr.Zero) Discord_ActivityAssets_Drop(ref assets);
        }
    }

    /// One of the four optional asset strings, selected by index to avoid four copies of the
    /// same pin-and-call dance.
    private static void SetAsset(ref Discord_ActivityAssets assets, string value, int which)
    {
        if (value == null)
        {
            switch (which)
            {
                case 0: Discord_ActivityAssets_SetLargeImage(ref assets, null); break;
                case 1: Discord_ActivityAssets_SetLargeText(ref assets, null); break;
                case 2: Discord_ActivityAssets_SetSmallImage(ref assets, null); break;
                default: Discord_ActivityAssets_SetSmallText(ref assets, null); break;
            }
            return;
        }
        byte[] b = Encoding.UTF8.GetBytes(value);
        fixed (byte* p = b)
        {
            var s = new Discord_String { ptr = (IntPtr)p, size = (nuint)b.Length };
            Discord_String* ps = &s;
            switch (which)
            {
                case 0: Discord_ActivityAssets_SetLargeImage(ref assets, ps); break;
                case 1: Discord_ActivityAssets_SetLargeText(ref assets, ps); break;
                case 2: Discord_ActivityAssets_SetSmallImage(ref assets, ps); break;
                default: Discord_ActivityAssets_SetSmallText(ref assets, ps); break;
            }
        }
    }

    private static void AddButton(ref Discord_Activity act, ActivitySpec spec)
    {
        Discord_ActivityButton button = default;
        Discord_ActivityButton_Init(ref button);
        try
        {
            byte[] lb = Encoding.UTF8.GetBytes(spec.ButtonLabel);
            byte[] ub = Encoding.UTF8.GetBytes(spec.ButtonUrl);
            fixed (byte* lp = lb)
            fixed (byte* up = ub)
            {
                var ls = new Discord_String { ptr = (IntPtr)lp, size = (nuint)lb.Length };
                var us = new Discord_String { ptr = (IntPtr)up, size = (nuint)ub.Length };
                Discord_ActivityButton_SetLabel(ref button, ls);
                Discord_ActivityButton_SetUrl(ref button, us);
            }
            Discord_Activity_AddButton(ref act, ref button);
        }
        finally
        {
            if (button.opaque != IntPtr.Zero) Discord_ActivityButton_Drop(ref button);
        }
    }

    public void ClearRichPresence() => Discord_Client_ClearRichPresence(ref _client);

    public void Dispose()
    {
        if (!_init) return;
        _init = false;
        if (_authorizePending) { try { Discord_Client_AbortAuthorize(ref _client); } catch { } }
        try { Discord_Client_Disconnect(ref _client); } catch { }
        try { Discord_Client_Drop(ref _client); } catch { }
        _client = default;
    }

    // ── Static helpers (usable before/without a client instance) ─────────────────────

    public static string VersionString()
    {
        if (!EnsureLoaded()) return "(native library not loaded)";
        Discord_String h = default;
        Discord_Client_GetVersionHash(ref h);
        string hash = ReadAndFree(ref h);
        int major = Discord_Client_GetVersionMajor();
        int minor = Discord_Client_GetVersionMinor();
        int patch = Discord_Client_GetVersionPatch();
        return $"{major}.{minor}.{patch}" + (hash != null ? $" ({hash})" : "");
    }

    public static string StatusName(ClientStatus status)
    {
        if (!EnsureLoaded()) return status.ToString();
        Discord_String s = default;
        Discord_Client_StatusToString(status, ref s);
        return ReadAndFree(ref s) ?? status.ToString();
    }
}
