using System;
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

    /// Create an instance of a world and get a join ticket + relay endpoint back.
    public async Task<JsonElement> CreateInstanceAsync(string worldId)
    {
        var body = JsonSerializer.Serialize(new { worldId });
        return await PostAuthedAsync("/v1/instances", body);
    }

    /// Join an existing instance, getting a fresh single-use ticket.
    public async Task<JsonElement> JoinInstanceAsync(string instanceId) =>
        await PostAuthedAsync($"/v1/instances/{instanceId}/join", "{}");

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
