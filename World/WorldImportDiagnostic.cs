using Godot;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Collections.Generic;

namespace SerikaSocial.World;

/// Standalone regression fixture for manifest-selected PBR and authored static collision.
/// Run: Godot --headless --path game res://World/WorldImportDiagnostic.tscn
/// Uses the actual GLB exporter, bundle reader, world loader and physics server.
public partial class WorldImportDiagnostic : Node3D
{
    private int _failures;
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "serika-world-import-" + Guid.NewGuid().ToString("N"));

    public override async void _Ready()
    {
        bool previousWorldToon = SerikaSocial.Avatar.ToonShading.WorldsEnabled;
        SerikaSocial.Avatar.ToonShading.WorldsEnabled = true;
        try
        {
            Directory.CreateDirectory(_temporary);
            var fixture = BuildFixture();
            AddChild(fixture);
            var expectedSpawn = fixture.GetNode<Node3D>("SPAWN").GlobalPosition;
            var floorTransform = fixture.GlobalTransform;
            var glb = Path.Combine(_temporary, "fixture.glb");
            var document = new GltfDocument();
            var state = new GltfState();
            Check(document.AppendFromScene(fixture, state) == Error.Ok, "fixture glTF export");
            Check(document.WriteToFilesystem(state, glb) == Error.Ok, "fixture GLB written");
            RemoveChild(fixture);
            fixture.Free();

            var authored = LoadCase(glb, "authored", "pbr", "authored");
            var spawn = WorldLoader.LoadFromPath(authored.path, "authored", authored.root);
            Check(spawn.HasValue && spawn.Value.DistanceTo(expectedSpawn) < .005f, "SPAWN survives transformed GLB ancestry");
            var meshes = Descendants(authored.root).OfType<MeshInstance3D>().ToList();
            Check(meshes.Where(m => m.Name.ToString().StartsWith("COL_")).All(m => !m.Visible), "authored proxies hidden");
            Check(Descendants(authored.root).OfType<StaticBody3D>().Count() == 2, "only two authored collision bodies");
            Check(meshes.Where(m => !m.Name.ToString().StartsWith("COL_")).All(m => m.GetActiveMaterial(0) is StandardMaterial3D), "PBR material preserved");
            var pbr = (StandardMaterial3D)meshes.First(m => m.Name == "VisibleFloor").GetActiveMaterial(0);
            Check(Mathf.Abs(pbr.Roughness - .73f) < .001f && Mathf.Abs(pbr.Metallic - .12f) < .001f,
                  "PBR roughness and metallic factors preserved");
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            var space = GetWorld3D().DirectSpaceState;
            foreach (float x in new[] { -2f, 0f, 2f })
            foreach (float z in new[] { -1f, 0f, 1f })
            {
                var point = floorTransform * new Vector3(x, 0, z);
                var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(point + Vector3.Up * 6, point - Vector3.Up));
                Check(hit.Count > 0 && hit["position"].AsVector3().DistanceTo(point) < .015f,
                    $"floor ray x={x} z={z}: decorative box ignored, mirrored/nonuniform floor solid");
            }
            var wallOrigin = floorTransform * new Vector3(2.5f, 1, 0);
            var normal = (floorTransform.Basis * Vector3.Right).Normalized();
            var wallHit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(wallOrigin - normal, wallOrigin + normal));
            Check(wallHit.Count > 0, "transformed authored wall blocks lateral movement");
            RemoveChild(authored.root);
            authored.root.Free();

            var legacy = LoadCase(glb, null, null, "legacy");
            WorldLoader.LoadFromPath(legacy.path, "legacy", legacy.root);
            Check(Descendants(legacy.root).OfType<StaticBody3D>().Count() == 4, "absent policy retains collision on all meshes");
            Check(Descendants(legacy.root).OfType<MeshInstance3D>().All(m => m.GetActiveMaterial(0) is ShaderMaterial), "absent shading retains toon conversion");
            RemoveChild(legacy.root);
            legacy.root.Free();

            // A typo cannot silently disable all existing-world collision or switch its look.
            var unknown = LoadCase(glb, "typo", "typo", "unknown");
            WorldLoader.LoadFromPath(unknown.path, "unknown", unknown.root);
            Check(Descendants(unknown.root).OfType<StaticBody3D>().Count() == 4, "unknown collision retains geometry default");
            Check(Descendants(unknown.root).OfType<MeshInstance3D>().All(m => m.GetActiveMaterial(0) is ShaderMaterial), "unknown shading retains toon default");
            RemoveChild(unknown.root);
            unknown.root.Free();

            GD.Print($"WORLD_IMPORT_TEST: {(_failures == 0 ? "PASS" : "FAIL")} ({_failures} failures)");
            GetTree().Quit(_failures == 0 ? 0 : 1);
        }
        catch (Exception e)
        {
            GD.PrintErr($"WORLD_IMPORT_TEST: exception {e}");
            GetTree().Quit(1);
        }
        finally
        {
            SerikaSocial.Avatar.ToonShading.WorldsEnabled = previousWorldToon;
            try { Directory.Delete(_temporary, true); } catch { }
        }
    }

    private (string path, Node3D root) LoadCase(string glb, string collision, string shading, string name)
    {
        var manifest = new Dictionary<string, object> { ["format"] = "glb", ["version"] = 1, ["modelFile"] = "world.glb", ["lighting"] = "outdoor" };
        if (collision != null) manifest["collision"] = collision;
        if (shading != null) manifest["shading"] = shading;
        var path = Path.Combine(_temporary, name + ".serikaworld");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(glb, "world.glb", CompressionLevel.NoCompression);
            using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            writer.Write(System.Text.Json.JsonSerializer.Serialize(manifest));
        }
        var root = new Node3D { Name = name };
        AddChild(root);
        return (path, root);
    }

    private Node3D BuildFixture()
    {
        var root = new Node3D { Name = "TransformedWorld" };
        root.Transform = new Transform3D(new Basis(Vector3.Up, .4f).Scaled(new Vector3(-1.4f, 1.2f, .8f)), new Vector3(7, 1, -3));
        AddBox(root, "VisibleFloor", new Vector3(6, .2f, 4), new Vector3(0, -.1f, 0));
        AddBox(root, "DecorativeFloatingBox", new Vector3(1.5f, 1.5f, 1.5f), new Vector3(0, 3, 0));
        AddBox(root, "COL_Floor", new Vector3(6, .2f, 4), new Vector3(0, -.1f, 0));
        AddBox(root, "COL_Wall", new Vector3(.2f, 2, 4), new Vector3(2.5f, 1, 0));
        root.AddChild(new Marker3D { Name = "SPAWN", Position = new Vector3(-1, .1f, 1) });
        return root;
    }

    private static void AddBox(Node3D root, string name, Vector3 size, Vector3 position)
    {
        var material = new StandardMaterial3D { AlbedoColor = new Color(.32f, .2f, .14f), Roughness = .73f, Metallic = .12f };
        root.AddChild(new MeshInstance3D { Name = name, Mesh = new BoxMesh { Size = size, Material = material }, Position = position });
    }

    private void Check(bool ok, string description)
    {
        GD.Print($"WORLD_IMPORT_TEST: {(ok ? "PASS" : "FAIL")} {description}");
        if (!ok) _failures++;
    }

    private static IEnumerable<Node> Descendants(Node root)
    {
        foreach (var child in root.GetChildren())
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
