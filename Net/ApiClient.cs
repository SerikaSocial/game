using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using HttpClient = System.Net.Http.HttpClient;

namespace Serika.Net;

/// HTTP client for server/api. Holds the session token after login and attaches it to
/// authed calls. Godot-free so it can be exercised from tests and a headless harness.
public sealed partial class ApiClient
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(180) };
    private readonly string _baseUrl;
    public string BaseUrl => _baseUrl;
    public string SessionToken { get; private set; }
    public string AccountsToken { get; private set; }
    public string LastDownloadError { get; private set; }

    public ApiClient(string baseUrl) => _baseUrl = baseUrl.TrimEnd('/');

    /// Verify a saved session token by calling /v1/session/me. Returns the user object
    /// if the token is still valid, or null if expired/invalid. Used for login persistence.
    public async Task<JsonElement> VerifySessionAsync(string token)
    {
        try
        {
            SessionToken = token;
            var res = await GetAuthedAsync("/v1/session/me");
            if (res.TryGetProperty("id", out _) )
                return res;
        }
        catch { }
        SessionToken = null;
        return default;
    }

    /// Exchange a PKCE code for our session token. Returns the logged-in user's id, or
    /// throws with the server's error code (e.g. "banned").
    public async Task<JsonElement> ExchangeAsync(string code, string codeVerifier)
    {
        var body = JsonSerializer.Serialize(new { code, code_verifier = codeVerifier });
        var res = await _http.PostAsync($"{_baseUrl}/v1/session/exchange",
            new StringContent(body, Encoding.UTF8, "application/json"));
        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(json.TryGetProperty("error", out var e) ? e.GetString() : "exchange_failed");
        SessionToken = json.GetProperty("session_token").GetString();
        if (json.TryGetProperty("accounts_token", out var at))
            AccountsToken = at.GetString();
        return json.GetProperty("user");
    }

    /// Login with email+password (no browser required). Returns the user object, or throws.
    public async Task<JsonElement> LoginWithEmailAsync(string email, string password)
    {
        var body = JsonSerializer.Serialize(new { email, password });
        var res = await _http.PostAsync($"{_baseUrl}/v1/session/login",
            new StringContent(body, Encoding.UTF8, "application/json"));
        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(json.TryGetProperty("error", out var e) ? e.GetString() : "login_failed");
        SessionToken = json.GetProperty("session_token").GetString();
        if (json.TryGetProperty("accounts_token", out var at))
            AccountsToken = at.GetString();
        return json.GetProperty("user");
    }

    public async Task<JsonElement> GetWorldsAsync() =>
        await GetAsync("/v1/worlds");

    /// Public Home registry. A short lookup timeout keeps an API outage from delaying startup;
    /// HTTP failures are distinct from an explicit empty selection for the cached fallback.
    public async Task<JsonElement> GetDefaultHomeAsync()
    {
        using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var response = await _http.GetAsync($"{_baseUrl}/v1/worlds/default-home", timeout.Token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        return json.RootElement.Clone();
    }

    /// Account-specific Home, falling back to the administrator's default on the server.
    public async Task<JsonElement> GetPersonalHomeAsync()
    {
        using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/v1/users/me/home");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SessionToken);
        using var response = await _http.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        return json.RootElement.Clone();
    }

    public async Task<JsonElement> SetPersonalHomeAsync(string worldId)
    {
        using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(worldId == null ? HttpMethod.Delete : HttpMethod.Put,
            $"{_baseUrl}/v1/users/me/home");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SessionToken);
        if (worldId != null)
            request.Content = new StringContent(JsonSerializer.Serialize(new { worldId }), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, timeout.Token);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(json.RootElement.TryGetProperty("error", out var error) ? error.GetString() : "home_save_failed");
        return json.RootElement.Clone();
    }

    /// Fetch full world detail including instances and review stats.
    public async Task<JsonElement> GetWorldDetailAsync(string worldId) =>
        await GetAsync($"/v1/worlds/{worldId}");
    /// Download a world file (.skw) to a local path under user://, returning the absolute path.
    /// Returns null on failure.
    /// Download a world bundle, reusing the cached copy only when it came from the same URL.
    ///
    /// World download URLs are content-addressed (the sha256 is in the path), so the URL
    /// changing *is* the world changing. The cache used to be keyed on `worldId` alone with no
    /// version recorded, which meant a stale bundle could win forever: republishing a world
    /// never reached anyone who had already visited it. Recording the source URL alongside the
    /// file makes a rejoin cheap and an update actually land.
    public async Task<string> DownloadWorldAsync(string url, string worldId)
    {
        try
        {
            DirAccess.MakeDirRecursiveAbsolute("user://worlds");
            string abs = ProjectSettings.GlobalizePath($"user://worlds/{worldId}.serikaworld");
            string src = ProjectSettings.GlobalizePath($"user://worlds/{worldId}.src");

            PurgeLegacyWorldCache();

            if (System.IO.File.Exists(abs))
            {
                string cachedUrl = null;
                if (System.IO.File.Exists(src))
                    try { cachedUrl = (await System.IO.File.ReadAllTextAsync(src)).Trim(); } catch { }

                // A cache hit is only a hit if the file is actually a bundle. Interrupted
                // downloads used to leave a truncated file at this exact path with a matching
                // `.src` sidecar beside it, so every later join took this fast path, handed the
                // loader a broken zip, and failed — permanently, on that machine, for that
                // world, with nothing that would ever re-fetch it.
                if (cachedUrl == url && LooksLikeBundle(abs))
                {
                    GD.Print($"world {worldId}: cache hit (unchanged)");
                    return abs;
                }
                if (cachedUrl == url)
                    GD.PrintErr($"world {worldId}: cached bundle is corrupt, purging and re-downloading");

                // Delete rather than overwrite. A failed or partial download over the top of a
                // stale bundle leaves a file that looks valid and loads the wrong world; with
                // it gone, a failure is a visible failure.
                GD.Print($"world {worldId}: cached copy is stale, purging and re-downloading");
                try { System.IO.File.Delete(abs); } catch { }
                try { if (System.IO.File.Exists(src)) System.IO.File.Delete(src); } catch { }
            }

            if (await DownloadToAsync(url, abs))
            {
                try { await System.IO.File.WriteAllTextAsync(src, url); }
                catch { /* the bundle is what matters; a missing sidecar just re-downloads */ }
                return abs;
            }

            // Download failed. An existing cached bundle is better than nothing, but it may be
            // the wrong version, so say so rather than letting it look like a success. A corrupt
            // one is not better than nothing — it fails the join with a confusing error instead
            // of an honest download failure.
            if (System.IO.File.Exists(abs) && LooksLikeBundle(abs))
            {
                GD.PrintErr($"world {worldId}: download failed, falling back to cached copy " +
                            "(may be out of date)");
                return abs;
            }
        }
        catch (Exception e) { GD.PrintErr($"world download failed: {e.Message}"); }
        return null;
    }

    private static bool _legacyCachePurged;

    /// One-time sweep of world bundles cached before versions were tracked.
    ///
    /// Those entries have no `.src` sidecar, so their version is unknowable — and an unknown
    /// version is exactly the case that shipped stale worlds to players for weeks. Deleting
    /// them costs one re-download each and guarantees everyone converges on the current build.
    private static void PurgeLegacyWorldCache()
    {
        if (_legacyCachePurged) return;
        _legacyCachePurged = true;
        try
        {
            string dir = ProjectSettings.GlobalizePath("user://worlds");
            if (!System.IO.Directory.Exists(dir)) return;
            int purged = 0;
            foreach (var f in System.IO.Directory.GetFiles(dir, "*.serikaworld"))
            {
                string sidecar = System.IO.Path.ChangeExtension(f, ".src");
                if (System.IO.File.Exists(sidecar)) continue;
                try { System.IO.File.Delete(f); purged++; } catch { }
            }
            if (purged > 0)
                GD.Print($"world cache: purged {purged} unversioned bundle(s) from before " +
                         "version tracking; they will be re-downloaded");
        }
        catch (Exception e) { GD.PrintErr($"world cache purge failed: {e.Message}"); }
    }

    /// Fetch the WebRTC ICE server list (STUN + TURN) for a P2P instance. Authed, since TURN
    /// credentials are per-user and short-lived.
    public async Task<JsonElement> GetIceServersAsync() =>
        await GetAuthedAsync("/v1/rtc/ice");

    /// The avatar the game should equip for the logged-in user (their chosen one, else a default
    /// outfit). Returns the .ska download URL, or null if none / on error.
    public async Task<string> GetCurrentAvatarUrlAsync()
    {
        try
        {
            var res = await GetAuthedAsync("/v1/avatars/current");
            if (res.TryGetProperty("avatar", out var a) && a.ValueKind == JsonValueKind.Object
                && a.TryGetProperty("downloadUrl", out var d) && d.ValueKind == JsonValueKind.String)
                return d.GetString();
        }
        catch { /* fall back to bundled default */ }
        return null;
    }

    /// The `.ska` URL for the avatar a given user is wearing, so remote players render as
    /// themselves. The relay only carries a peer's user id, so this is how the client resolves
    /// it. Returns null on error — the caller then falls back to the default outfit.
    public async Task<string> GetAvatarUrlForUserAsync(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return null;
        try
        {
            var res = await GetAsync($"/v1/avatars/by-user/{userId}");
            if (res.TryGetProperty("avatar", out var a) && a.ValueKind == JsonValueKind.Object
                && a.TryGetProperty("downloadUrl", out var d) && d.ValueKind == JsonValueKind.String)
                return d.GetString();
        }
        catch (Exception e) { GD.PrintErr($"peer avatar lookup failed: {e.Message}"); }
        return null;
    }

    /// List avatars the player can equip. `mine=false` returns the public/default catalogue
    /// (no auth needed); `mine=true` returns the caller's own uploads. Returns the `avatars`
    /// array, or an empty array on error.
    public async Task<JsonElement> GetAvatarsAsync(bool mine)
    {
        try
        {
            var res = mine
                ? await GetAuthedAsync("/v1/avatars/mine/list")
                : await GetAsync("/v1/avatars/?limit=120");
            if (res.TryGetProperty("avatars", out var arr) && arr.ValueKind == JsonValueKind.Array)
                return arr;
        }
        catch (Exception e) { GD.PrintErr($"avatar list failed: {e.Message}"); }
        return JsonDocument.Parse("[]").RootElement;
    }

    /// Persist the caller's avatar choice server-side so it's worn on the next join too.
    /// Throws on failure (not permitted, not found).
    public async Task SelectAvatarAsync(string avatarId) =>
        await PostAuthedAsync($"/v1/avatars/{avatarId}/select", "{}");

    /// Resolve a page/media URL into concrete playable stream tracks via the server (which runs
    /// yt-dlp — the client can't, least of all on Quest). Returns the `Resolved` JSON, or throws
    /// with the server's error code ("no_playable_streams", "resolve_timeout", "private_host", …).
    /// See server/api/src/routes/video.ts for the shape.
    public async Task<JsonElement> ResolveVideoAsync(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get,
            $"{_baseUrl}/v1/video/resolve?url={Uri.EscapeDataString(url)}");
        if (SessionToken != null)
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SessionToken);
        var res = await _http.SendAsync(req);
        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(
                json.TryGetProperty("error", out var e) ? e.GetString() : $"http {(int)res.StatusCode}");
        return json;
    }

    /// Start or attach to the server's shared segmented transcode for `url`, and report its
    /// progress. See `/v1/video/session` — one encode is shared by every viewer, so calling this
    /// for a clip someone else already queued is a cache hit, not a second encode.
    ///
    /// Returns `{ id, segments, done, segmentSeconds, title, duration }`, where `segments` is how
    /// many are complete and therefore safe to fetch.
    public async Task<JsonElement> StartVideoSessionAsync(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get,
            $"{_baseUrl}/v1/video/session?url={Uri.EscapeDataString(url)}");
        if (SessionToken != null)
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SessionToken);
        var res = await _http.SendAsync(req);
        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(
                json.TryGetProperty("error", out var e) ? e.GetString() : $"http {(int)res.StatusCode}");
        return json;
    }

    /// URL of one segment of a shared transcode job.
    public string VideoSegmentUrl(string jobId, int index) =>
        $"{_baseUrl}/v1/video/segment/{Uri.EscapeDataString(jobId)}/{index}";

    /// Resolve a peer's profile-picture URL (and display name) from their account id — the only
    /// identity the relay carries. Returns the avatarUrl, or null if the user has none / on error.
    public async Task<string> GetUserAvatarUrlAsync(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return null;
        try
        {
            var res = await GetAsync($"/v1/users/by-id/{userId}/card");
            if (res.TryGetProperty("avatarUrl", out var a) && a.ValueKind == JsonValueKind.String)
                return a.GetString();
        }
        catch { /* best-effort */ }
        return null;
    }

    /// Fetch raw image bytes (avatar/world thumbnails). Returns null on error.
    public async Task<byte[]> GetImageBytesAsync(string url)
    {
        try { return await _http.GetByteArrayAsync(url); }
        catch { return null; }
    }

    /// Download a .ska or .ogv to a local path (user://), returning true on success.
    /// A `.serikaworld` is a ZIP, and a ZIP whose central directory is missing is not a bundle
    /// however plausible its first two bytes are. Checks the End Of Central Directory signature
    /// at the tail, which is precisely what a truncated download loses.
    private static bool LooksLikeBundle(string absPath)
    {
        try
        {
            var info = new System.IO.FileInfo(absPath);
            if (!info.Exists || info.Length < 22) return false;
            using var f = System.IO.File.OpenRead(absPath);
            Span<byte> head = stackalloc byte[2];
            if (f.Read(head) != 2 || head[0] != (byte)'P' || head[1] != (byte)'K') return false;
            // EOCD is at most 22 + 65535 bytes from the end (the comment field). Scan that tail.
            int tail = (int)Math.Min(info.Length, 22 + 65535);
            var buf = new byte[tail];
            f.Seek(-tail, System.IO.SeekOrigin.End);
            int read = f.Read(buf, 0, tail);
            for (int i = read - 22; i >= 0; i--)
                if (buf[i] == 0x50 && buf[i + 1] == 0x4B && buf[i + 2] == 0x05 && buf[i + 3] == 0x06)
                    return true;
            return false;
        }
        catch { return false; }
    }

    /// Is this URL our own API? Compared on scheme+host+port, never by string prefix: a prefix
    /// test says yes to `https://api-social.ado.ink.evil.example/…`.
    private bool IsApiOrigin(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target)) return false;
        if (!Uri.TryCreate(_baseUrl, UriKind.Absolute, out var api)) return false;
        return target.Scheme == api.Scheme
            && string.Equals(target.Host, api.Host, StringComparison.OrdinalIgnoreCase)
            && target.Port == api.Port;
    }

    public async Task<bool> DownloadToAsync(string url, string absPath)
    {
        LastDownloadError = null;
        try
        {
            var dir = System.IO.Path.GetDirectoryName(absPath);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // ONLY our own API gets the session token. This used to go out with every request,
            // including CDN ones — handing the session JWT to Cloudflare and the bucket origin on
            // every world and avatar fetch. Asset URLs are content-addressed and public; they
            // need no credential, and a credential sent to a third party is a credential leaked.
            if (SessionToken != null && IsApiOrigin(url))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SessionToken);

            using var res = await _http.SendAsync(req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            if (!res.IsSuccessStatusCode) {
                LastDownloadError = $"HTTP {(int)res.StatusCode} ({res.ReasonPhrase})";
                GD.PrintErr($"Asset download failed: HTTP {(int)res.StatusCode} from {new Uri(url).Host}{new Uri(url).AbsolutePath}");
                return false;
            }

            // Download to a temp file and MOVE it into place. Writing straight to `absPath` left
            // a truncated file there whenever the transfer was interrupted — a dropped
            // connection, a full disk, a Windows antivirus scanner holding the handle — and
            // every caller then takes the `File.Exists(abs)` fast path and never downloads it
            // again. The result is a permanently broken avatar or world that no amount of
            // retrying fixes, because nothing ever tries. `DownloadShowAssetAsync` already did
            // this correctly; this path was the outlier.
            string tmp = absPath + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                await using (var stream = await res.Content.ReadAsStreamAsync())
                await using (var fs = new System.IO.FileStream(tmp, System.IO.FileMode.Create,
                                 System.IO.FileAccess.Write, System.IO.FileShare.None))
                {
                    await stream.CopyToAsync(fs);
                }

                // A zero-byte body is a failed download that reported success. Caching it is
                // indistinguishable from caching a real asset, and just as permanent.
                var written = new System.IO.FileInfo(tmp);
                if (!written.Exists || written.Length == 0)
                {
                    LastDownloadError = "the server returned an empty file";
                    GD.PrintErr($"Asset download failed: empty body from {new Uri(url).Host}");
                    return false;
                }

                System.IO.File.Move(tmp, absPath, true);
                return true;
            }
            finally
            {
                // Never leave a .part behind, on any exit path.
                try { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); } catch { }
            }
        }
        catch (Exception error) {
            LastDownloadError = error.Message;
            GD.PrintErr($"Asset download failed: {error.GetType().Name}: {error.Message}");
            return false;
        }
    }

    /// Create a brand-new instance of a world (used when you explicitly want a private/fresh one).
    public async Task<JsonElement> CreateInstanceAsync(string worldId, int access = 0)
    {
        var body = JsonSerializer.Serialize(new { worldId, access });
        return await PostAuthedAsync("/v1/instances", body);
    }

    /// Join a world the VRChat way: land in an existing open public instance if one has room,
    /// otherwise a fresh one is created. This is what makes two players in the same world
    /// actually meet — `CreateInstanceAsync` always made a separate empty instance.
    public async Task<JsonElement> JoinWorldInstanceAsync(string worldId)
    {
        var body = JsonSerializer.Serialize(new { worldId });
        return await PostAuthedAsync("/v1/instances/join-world", body);
    }

    public async Task<JsonElement> GetInstanceDetailAsync(string instanceId) =>
        await GetAuthedAsync($"/v1/instances/{instanceId}");

    /// Join an existing instance, getting a fresh single-use ticket.
    public async Task<JsonElement> JoinInstanceAsync(string instanceId) =>
        await PostAuthedAsync($"/v1/instances/{instanceId}/join", "{}");

    /// Fetch the user's block list (account IDs of blocked users). Used to show beans for
    /// blocked users in-world. Returns an empty set on error.
    public async Task<HashSet<string>> GetBlockedUsersAsync()
    {
        var result = new HashSet<string>();
        try
        {
            var res = await GetAuthedAsync("/v1/social/blocks");
            if (res.TryGetProperty("blocks", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in arr.EnumerateArray())
                {
                    if (b.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        result.Add(id.GetString());
                }
            }
        }
        catch { /* best-effort */ }
        return result;
    }

    // ── Social graph: friends, requests, blocks ──────────────────────────────────

    /// One entry of the social graph: enough to render a row anywhere in the client.
    public sealed class SocialUser
    {
        public string Id;
        public string Username;
        public string DisplayName; // may be null server-side; Username then
        public string AvatarUrl;
        public string Name => string.IsNullOrEmpty(DisplayName) ? Username : DisplayName;
    }

    public sealed class SocialGraph
    {
        public List<SocialUser> Friends = new();
        public List<SocialUser> Incoming = new();
        public List<SocialUser> Outgoing = new();
    }

    private static SocialUser ParseUser(JsonElement j)
    {
        var u = new SocialUser
        {
            Id = j.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null,
            Username = j.TryGetProperty("username", out var un) && un.ValueKind == JsonValueKind.String ? un.GetString() : "?",
            AvatarUrl = j.TryGetProperty("avatarUrl", out var au) && au.ValueKind == JsonValueKind.String ? au.GetString() : null,
        };
        if (j.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String)
            u.DisplayName = dn.GetString();
        return u;
    }

    /// Friends + pending incoming/outgoing requests for the social panel.
    /// Returns an empty graph on error — the panel shows a retry.
    public async Task<SocialGraph> GetSocialGraphAsync()
    {
        var g = new SocialGraph();
        try
        {
            var res = await GetAuthedAsync("/v1/social/friends");
            if (res.TryGetProperty("friends", out var f) && f.ValueKind == JsonValueKind.Array)
                foreach (var u in f.EnumerateArray()) g.Friends.Add(ParseUser(u));
            if (res.TryGetProperty("incoming", out var i) && i.ValueKind == JsonValueKind.Array)
                foreach (var u in i.EnumerateArray()) g.Incoming.Add(ParseUser(u));
            if (res.TryGetProperty("outgoing", out var o) && o.ValueKind == JsonValueKind.Array)
                foreach (var u in o.EnumerateArray()) g.Outgoing.Add(ParseUser(u));
        }
        catch (Exception e) { GD.PrintErr($"social graph fetch failed: {e.Message}"); }
        return g;
    }

    /// Send a friend request. Returns "pending" (sent), "accepted" (they had already
    /// asked us), "already_friends" or "request_already_sent" (idempotent server states).
    public async Task<string> SendFriendRequestAsync(string userId)
    {
        var j = await PostAuthedAsync($"/v1/social/friends/{userId}", "{}");
        return j.TryGetProperty("status", out var s) ? s.GetString() : "pending";
    }

    public async Task AcceptFriendRequestAsync(string userId) =>
        await PostAuthedAsync($"/v1/social/friends/{userId}/accept", "{}");

    /// Remove a friend, or decline/cancel a request — same server endpoint.
    public async Task RemoveFriendAsync(string userId) =>
        await DeleteAuthedAsync($"/v1/social/friends/{userId}");

    public async Task BlockUserAsync(string userId) =>
        await PostAuthedAsync($"/v1/social/block/{userId}", "{}");

    public async Task UnblockUserAsync(string userId) =>
        await DeleteAuthedAsync($"/v1/social/block/{userId}");

    /// The blocked list with names (the same endpoint GetBlockedUsersAsync reads, parsed
    /// fully) — for the social panel's Blocked tab. Empty list on error.
    public async Task<List<SocialUser>> GetBlockedUsersDetailedAsync()
    {
        var result = new List<SocialUser>();
        try
        {
            var res = await GetAuthedAsync("/v1/social/blocks");
            if (res.TryGetProperty("blocks", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var b in arr.EnumerateArray()) result.Add(ParseUser(b));
        }
        catch (Exception e) { GD.PrintErr($"blocked list fetch failed: {e.Message}"); }
        return result;
    }

    /// Search users by username prefix (min 2 chars). Returns candidates with online
    /// flags; empty list on error or short query.
    public async Task<List<SocialUser>> SearchUsersAsync(string query)
    {
        var result = new List<SocialUser>();
        if (string.IsNullOrEmpty(query) || query.Trim().Length < 2) return result;
        try
        {
            var res = await GetAuthedAsync($"/v1/social/users/search?q={Uri.EscapeDataString(query.Trim())}");
            if (res.TryGetProperty("users", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var u in arr.EnumerateArray()) result.Add(ParseUser(u));
        }
        catch (Exception e) { GD.PrintErr($"user search failed: {e.Message}"); }
        return result;
    }

    // ── Abuse reports ────────────────────────────────────────────────────────────

    /// File an abuse report. Categories 0..8 — must match REPORT_CATEGORIES in
    /// server/api/src/routes/reports.ts. Returns null on success, or the server's
    /// error code ("already_reported", "report_rate_limited", …).
    public async Task<string> ReportAsync(bool world, string targetId, int category, string details)
    {
        var body = JsonSerializer.Serialize(new
        {
            targetType = world ? "world" : "user",
            targetId,
            category,
            details = details ?? "",
        });
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/reports")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (SessionToken != null)
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SessionToken);
        var res = await _http.SendAsync(req);
        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        if (res.IsSuccessStatusCode) return null;
        return json.TryGetProperty("error", out var e) ? e.GetString() : $"http {(int)res.StatusCode}";
    }

    // ── Notifications ─────────────────────────────────────────────────────────────────

    /// Fetch the inbox. This is the authoritative read — the gateway push is only a fast path,
    /// and anything that arrived while the socket was down is here and nowhere else.
    public async Task<(SerikaNotification[] Items, int Unread)> GetNotificationsAsync(int limit = 50)
    {
        var json = await GetAuthedAsync($"/v1/notifications?limit={limit}");
        if (!json.TryGetProperty("notifications", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return (System.Array.Empty<SerikaNotification>(), 0);

        var list = new List<SerikaNotification>();
        foreach (var e in arr.EnumerateArray()) list.Add(SerikaNotification.FromJson(e));
        int unread = json.TryGetProperty("unread", out var u) && u.TryGetInt32(out int n) ? n : 0;
        return (list.ToArray(), unread);
    }

    /// Just the badge number, for the poll fallback when the gateway socket is down.
    public async Task<int> GetUnreadCountAsync()
    {
        var json = await GetAuthedAsync("/v1/notifications/unread");
        return json.TryGetProperty("unread", out var u) && u.TryGetInt32(out int n) ? n : 0;
    }

    /// Mark one read; returns the new unread count.
    public async Task<int> MarkNotificationReadAsync(string id)
    {
        var json = await PostAuthedAsync($"/v1/notifications/{Uri.EscapeDataString(id)}/read", "{}");
        return json.TryGetProperty("unread", out var u) && u.TryGetInt32(out int n) ? n : 0;
    }

    public async Task MarkAllNotificationsReadAsync() =>
        await PostAuthedAsync("/v1/notifications/read-all", "{}");

    public async Task DeleteNotificationAsync(string id) =>
        await DeleteAuthedAsync($"/v1/notifications/{Uri.EscapeDataString(id)}");

    public async Task ClearNotificationsAsync() => await DeleteAuthedAsync("/v1/notifications");

    // ── Invites ───────────────────────────────────────────────────────────────────────

    /// Invite a user to the instance we're in. Throws with the server's error string on refusal
    /// (`rate_limited`, `blocked`, `not_permitted`, …) so the caller can show a real reason.
    public async Task InviteToInstanceAsync(string targetUserId, string instanceId)
    {
        var body = JsonSerializer.Serialize(new { targetUserId, instanceId });
        await PostAuthedAsync("/v1/social/invite", body);
    }

    /// Join one specific instance. An invite names an instance, and joining the world instead
    /// would re-run matchmaking and can land the invitee in a different copy of it.
    public async Task<JsonElement> JoinInstanceByIdAsync(string instanceId) =>
        await PostAuthedAsync($"/v1/instances/{Uri.EscapeDataString(instanceId)}/join", "{}");

    private async Task<JsonElement> GetAsync(string path)
    {
        var res = await _http.GetAsync($"{_baseUrl}{path}");
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
    }

    private async Task<JsonElement> GetAuthedAsync(string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}{path}");
        if (SessionToken != null)
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SessionToken);
        var res = await _http.SendAsync(req);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
    }

    private async Task<JsonElement> PostAuthedAsync(string path, string body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}{path}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (SessionToken != null)
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SessionToken);
        var res = await _http.SendAsync(req);
        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(json.TryGetProperty("error", out var e) ? e.GetString() : $"http {(int)res.StatusCode}");
        return json;
    }

    private async Task DeleteAuthedAsync(string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, $"{_baseUrl}{path}");
        if (SessionToken != null)
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SessionToken);
        var res = await _http.SendAsync(req);
        if (!res.IsSuccessStatusCode)
        {
            var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
            throw new InvalidOperationException(json.TryGetProperty("error", out var e) ? e.GetString() : $"http {(int)res.StatusCode}");
        }
    }
}
