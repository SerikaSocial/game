using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using SerikaSocial.Events;
using Godot;
namespace Serika.Net;

public sealed partial class ApiClient
{
    public async Task<JsonElement> EventRequestAsync(string path, object body = null)
    {
        using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, _baseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SessionToken);
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body, LiveEvent.Json), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(json.RootElement.TryGetProperty("error", out var error) ? error.GetString() : "Event request failed.");
        return json.RootElement.Clone();
    }
    public async Task<LiveEvent[]> GetEventsAsync(bool admin = false) => JsonSerializer.Deserialize<LiveEvent[]>((await EventRequestAsync(admin ? "/v1/admin/events/" : "/v1/events/")).GetRawText(), LiveEvent.Json);
    public async Task<LiveEvent> GetEventAsync(string id) => JsonSerializer.Deserialize<LiveEvent>((await EventRequestAsync("/v1/events/" + Uri.EscapeDataString(id))).GetRawText(), LiveEvent.Json);
    public async Task<JsonElement> UploadEventFileAsync(string kind, string path, string venueName = null)
    {
        using var multipart = new MultipartFormDataContent();
        var stream = new StreamContent(File.OpenRead(path));
        stream.Headers.ContentType = new MediaTypeHeaderValue(kind == "banner" ? "image/png" : "application/octet-stream");
        multipart.Add(stream, "file", Path.GetFileName(path));
        if (venueName != null) multipart.Add(new StringContent(venueName), "name");
        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + (kind == "venue" ? "/v1/admin/events/venues" : "/v1/admin/events/assets/" + kind)) { Content = multipart };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SessionToken);
        using var response = await _http.SendAsync(request);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(json.RootElement.TryGetProperty("error", out var error) ? error.GetString() : "Upload failed.");
        return json.RootElement.Clone();
    }
    public string EventAssetUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https" ? url : _baseUrl + "/" + url.TrimStart('/');
    public async Task<string> DownloadShowAssetAsync(string url)
    {
        url = EventAssetUrl(url);
        var uri = new Uri(url);
        if (uri.Scheme is not ("http" or "https")) throw new InvalidOperationException("Invalid event asset URL.");
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        string path = ProjectSettings.GlobalizePath("user://events/" + hash + Path.GetExtension(uri.AbsolutePath));
        if (File.Exists(path)) return path;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 134217728) throw new InvalidOperationException("Event asset exceeds 128 MB.");
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try {
            await using var input = await response.Content.ReadAsStreamAsync();
            await using (var output = File.Create(tmp)) {
                byte[] buffer = new byte[65536]; long total = 0; int n;
                while ((n = await input.ReadAsync(buffer)) > 0) { total += n; if (total > 134217728) throw new InvalidOperationException("Event asset exceeds 128 MB."); await output.WriteAsync(buffer.AsMemory(0, n)); }
            }
            File.Move(tmp, path, true); return path;
        } finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}
