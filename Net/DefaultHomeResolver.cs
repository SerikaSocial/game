using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using SerikaSocial;

namespace Serika.Net;

/// Resolves the administrator's published Home without making startup depend on the API.
/// Cache entries are scoped to the API and tied to the bundle's content-addressed source URL.
public static class DefaultHomeResolver
{
    public sealed record World(string Id, string Name, string VersionId, string DownloadUrl);
    public sealed record Resolved(World World, string Path);
    internal sealed record Cached(string ApiBaseUrl, World World);

    public static async Task<Resolved> ResolveAsync(string apiBaseUrl, string cacheDirectory,
        Func<Task<JsonElement>> fetch, Func<World, Task<string>> download, string accountId = null)
    {
        string registryPath = RegistryPath(cacheDirectory, accountId);
        Resolved cached = ReadCache(registryPath, apiBaseUrl, cacheDirectory);
        try
        {
            var response = await fetch();
            if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("world", out var selected))
                throw new InvalidDataException("Home registry response is missing its selection");
            if (selected.ValueKind == JsonValueKind.Null)
            {
                // An explicit removal/private publication is different from an outage: do
                // not keep serving the previous selection after the server withdraws it.
                try { File.Delete(registryPath); } catch { }
                return null;
            }
            var world = ParseWorld(selected);
            if (cached?.World == world) return cached;
            string path = await download(world);
            var resolved = VerifiedBundle(world, cacheDirectory);
            if (path == null || resolved == null) return ReadCache(registryPath, apiBaseUrl, cacheDirectory);
            try
            {
                Directory.CreateDirectory(cacheDirectory);
                await File.WriteAllTextAsync(registryPath + ".tmp", JsonSerializer.Serialize(new Cached(apiBaseUrl, world), AotJson.Options));
                File.Move(registryPath + ".tmp", registryPath, true);
            }
            catch { /* A read-only cache must not prevent entering a downloaded world. */ }
            return resolved;
        }
        catch
        {
            // Re-check files: an unsuccessful replacement may have removed an old bundle.
            return ReadCache(registryPath, apiBaseUrl, cacheDirectory);
        }
    }

    // Never share a saved personal selection between two accounts on the same computer.
    private static string RegistryPath(string directory, string accountId) => System.IO.Path.Combine(directory,
        string.IsNullOrEmpty(accountId) ? "default-home.json" : "home-" +
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(accountId))).ToLowerInvariant() + ".json");

    public static void ForgetSelection(string directory, string accountId)
    {
        try { File.Delete(RegistryPath(directory, accountId)); } catch { }
    }

    private static World ParseWorld(JsonElement json)
    {
        var world = new World(json.GetProperty("id").GetString(), json.GetProperty("name").GetString(),
            json.GetProperty("versionId").GetString(), json.GetProperty("downloadUrl").GetString());
        if (!Valid(world)) throw new InvalidDataException("Invalid Home registry entry");
        return world;
    }

    private static bool Valid(World world) => world != null
        && Guid.TryParseExact(world.Id, "D", out var id) && id != Guid.Empty
        && Guid.TryParseExact(world.VersionId, "D", out var version) && version != Guid.Empty
        && !string.IsNullOrWhiteSpace(world.Name)
        && Uri.TryCreate(world.DownloadUrl, UriKind.Absolute, out var url)
        && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp);

    private static Resolved ReadCache(string registryPath, string apiBaseUrl, string cacheDirectory)
    {
        try
        {
            var cache = JsonSerializer.Deserialize<Cached>(File.ReadAllText(registryPath), AotJson.Options);
            return cache?.ApiBaseUrl == apiBaseUrl && Valid(cache.World)
                ? VerifiedBundle(cache.World, cacheDirectory) : null;
        }
        catch { return null; }
    }

    private static Resolved VerifiedBundle(World world, string cacheDirectory)
    {
        try
        {
            string path = System.IO.Path.Combine(cacheDirectory, world.Id + ".serikaworld");
            string source = System.IO.Path.Combine(cacheDirectory, world.Id + ".src");
            return File.Exists(path) && new FileInfo(path).Length > 4
                && File.ReadAllText(source).Trim() == world.DownloadUrl ? new Resolved(world, path) : null;
        }
        catch { return null; }
    }
}
