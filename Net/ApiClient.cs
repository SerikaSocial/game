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
public sealed class ApiClient
{
    private readonly HttpClient _http = new();
    private readonly string _baseUrl;
    public string SessionToken { get; private set; }

    public ApiClient(string baseUrl) => _baseUrl = baseUrl.TrimEnd('/');

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
        return json.GetProperty("user");
    }

    public async Task<JsonElement> GetWorldsAsync() =>
        await GetAsync("/v1/worlds");

    /// Fetch full world detail including instances and review stats.
    public async Task<JsonElement> GetWorldDetailAsync(string worldId) =>
        await GetAsync($"/v1/worlds/{worldId}");
    /// Download a world file (.skw) to a local path under user://, returning the absolute path.
    /// Returns null on failure.
    public async Task<string> DownloadWorldAsync(string url, string worldId)
    {
        try
        {
            DirAccess.MakeDirRecursiveAbsolute("user://worlds");
            string abs = ProjectSettings.GlobalizePath($"user://worlds/{worldId}.skw");
            if (await DownloadToAsync(url, abs))
                return abs;
        }
        catch (Exception e) { GD.PrintErr($"world download failed: {e.Message}"); }
        return null;
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

    /// Fetch raw image bytes (avatar/world thumbnails). Returns null on error.
    public async Task<byte[]> GetImageBytesAsync(string url)
    {
        try { return await _http.GetByteArrayAsync(url); }
        catch { return null; }
    }

    /// Download a .ska to a local path (user://), returning true on success.
    public async Task<bool> DownloadToAsync(string url, string absPath)
    {
        try
        {
            var bytes = await _http.GetByteArrayAsync(url);
            await System.IO.File.WriteAllBytesAsync(absPath, bytes);
            return true;
        }
        catch { return false; }
    }

    /// Create a brand-new instance of a world (used when you explicitly want a private/fresh one).
    public async Task<JsonElement> CreateInstanceAsync(string worldId)
    {
        var body = JsonSerializer.Serialize(new { worldId });
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
}
