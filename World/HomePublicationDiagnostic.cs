using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serika.Net;

namespace SerikaSocial.World;

/// Exercises the public registry, real API download, and Main's actual selected-Home builder.
/// Run with XDG_DATA_HOME and SERIKA_DIAGNOSTIC_CACHE_ROOT set to a scratch directory,
/// SERIKA_EXPECTED_HOME_ID set to the published UUID, and SERIKA_HOME_REPORT to a JSON path.
public partial class HomePublicationDiagnostic : Node3D
{
    private readonly List<object> _checks = new();
    private int _failures;

    public override async void _Ready()
    {
        string expected = System.Environment.GetEnvironmentVariable("SERIKA_EXPECTED_HOME_ID");
        string scope = System.Environment.GetEnvironmentVariable("SERIKA_DIAGNOSTIC_CACHE_ROOT");
        string report = System.Environment.GetEnvironmentVariable("SERIKA_HOME_REPORT");
        string worldId = null, versionId = null, downloadUrl = null, digest = null, cache = null;
        try
        {
            if (!Guid.TryParse(expected, out _) || string.IsNullOrWhiteSpace(scope))
                throw new InvalidOperationException("An expected published UUID and isolated cache directory are required");
            cache = ProjectSettings.GlobalizePath("user://worlds");
            string boundary = Path.GetFullPath(scope).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(cache).StartsWith(boundary, StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to touch the user's normal Home cache; set XDG_DATA_HOME to the scratch directory");
            var api = new ApiClient(System.Environment.GetEnvironmentVariable("SERIKA_API_URL") ?? "https://api-social.ado.ink");
            var resolved = await DefaultHomeResolver.ResolveAsync(api.BaseUrl, cache, api.GetDefaultHomeAsync,
                world => api.DownloadWorldAsync(world.DownloadUrl, world.Id));
            Check(resolved != null, "public registry resolves and real API client downloads the selected Home");
            if (resolved == null) throw new InvalidDataException("Home resolver returned its built-in fallback");
            worldId = resolved.World.Id; versionId = resolved.World.VersionId; downloadUrl = resolved.World.DownloadUrl;
            Check(worldId == expected, "resolved Home is the exact administrator-selected published UUID");
            var fresh = (await api.GetDefaultHomeAsync()).GetProperty("world");
            Check(fresh.GetProperty("id").GetString() == worldId && fresh.GetProperty("versionId").GetString() == versionId
                && fresh.GetProperty("downloadUrl").GetString() == downloadUrl, "downloaded publication matches a fresh public registry response");
            digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(resolved.Path))).ToLowerInvariant();
            string addressedHash = new Uri(downloadUrl).Segments.Select(s => s.Trim('/'))
                .FirstOrDefault(s => Regex.IsMatch(s, "^[0-9a-f]{64}$"));
            Check(addressedHash == digest, "downloaded bytes match the CDN content-addressed SHA256");

            var root = new Node3D { Name = "SelectedHome" }; AddChild(root);
            var main = new Main();
            try
            {
                typeof(Main).GetField("_defaultHome", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, resolved);
                var home = (Worlds.Home)typeof(Main).GetMethod("BuildSelectedHome", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, new object[] { root });
                string name = (string)typeof(Main).GetField("_homeName", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main);
                Check(name == resolved.World.Name, "Main builds the published Home instead of its built-in fallback");
                Check(float.IsFinite(home.Spawn.X) && float.IsFinite(home.Spawn.Y) && float.IsFinite(home.Spawn.Z), "published Home supplies a finite spawn");
            }
            finally { main.Free(); }
            for (int i = 0; i < 3; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            var nodes = Descendants(root).ToList();
            Check(nodes.OfType<Portal>().Count() == 5 && nodes.OfType<Portal>().All(p => p.Mode == PortalMode.DirectWorld), "published Home contains five direct travel portals");
            Check(nodes.OfType<SeatNode>().Count() == 24, "published Home contains 24 usable seats");
            Check(nodes.OfType<Mirror>().Count() == 2, "published Home contains two reflection mirrors");
            var fish = nodes.OfType<Node3D>().Where(n => n.Name.ToString().StartsWith("FISH_ROOT_")).ToList();
            Check(fish.Count == 14, "published Home contains 14 authored fish");
            var swimmers = nodes.OfType<AnimationPlayer>().Where(p => p.IsPlaying() && p.CurrentAnimation.ToString().Contains("AquariumSwim")).ToList();
            Check(swimmers.Count == 1, "published aquarium swim animation starts automatically");
            var before = fish.Select(f => f.GlobalPosition).ToArray();
            swimmers.FirstOrDefault()?.Advance(1);
            Check(fish.Count == 14 && fish.Where((f, i) => f.GlobalPosition.DistanceTo(before[i]) > .01f).Count() == 14, "all 14 fish move on imported animation tracks");
            Check(nodes.OfType<StaticBody3D>().Any(), "published Home has solid authored geometry");
        }
        catch (Exception error)
        {
            Check(false, error.GetBaseException().Message);
        }
        finally
        {
            var result = new { passed = _failures == 0, worldId, versionId, downloadUrl, sha256 = digest, cacheDirectory = cache, checks = _checks };
            if (!string.IsNullOrWhiteSpace(report)) File.WriteAllText(report, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            GD.Print($"HOME_PUBLICATION_TEST: {(_failures == 0 ? "PASS" : "FAIL")} ({_checks.Count} checks, {_failures} failures)");
            GetTree().Quit(_failures == 0 ? 0 : 1);
        }
    }

    private void Check(bool passed, string check)
    {
        if (!passed) _failures++;
        _checks.Add(new { check, passed });
        GD.Print($"HOME_PUBLICATION_TEST: {(passed ? "PASS" : "FAIL")} {check}");
    }
    private static IEnumerable<Node> Descendants(Node root)
    { foreach (var child in root.GetChildren()) { yield return child; foreach (var node in Descendants(child)) yield return node; } }
}
