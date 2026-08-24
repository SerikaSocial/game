using Godot;
using System;
using System.IO;
using System.Text.Json;

namespace SerikaSocial.World;

/// Loads a world scene from disk or a CDN download. World files live under
/// `user://worlds/{worldId}.<ext>` (written by the API client) or in the developer
/// 3DWorlds folder for local testing.
///
/// The canonical format is `.serikaworld` — a ZIP containing:
///   - world.glb       a self-contained GLB with embedded textures
///   - manifest.json   { "name", "spawn": [x,y,z], "format": "glb", "modelFile": "world.glb" }
///
/// The spawn point comes from the manifest (or a `Spawn` node inside the GLB). When a
/// world does not specify one, the spawn is the world origin (0, 0, 0).
///
/// This is the CDN/data path: no world geometry is hardcoded in C#.
public static class WorldLoader
{
    private const string WorldsCacheDir = "user://worlds";
    private static readonly Vector3 DefaultSpawn = Vector3.Zero; // spawn at origin when unset

    /// <summary>
    /// Attempt to load the named world.
    /// </summary>
    /// <returns>The player spawn point, or <c>null</c> when no asset exists (caller falls back).</returns>
    public static Vector3? Load(string worldId, Node3D root)
    {
        if (string.IsNullOrWhiteSpace(worldId))
        {
            GD.PrintErr("WorldLoader: worldId is null or empty");
            return null;
        }

        string[] exts = { ".serikaworld", ".skw", ".tscn", ".pck", ".glb", ".gltf" };

        // 1. Local user:// cache (a CDN download already landed here).
        foreach (var ext in exts)
        {
            string local = ProjectSettings.GlobalizePath($"{WorldsCacheDir}/{worldId}{ext}");
            if (File.Exists(local))
            {
                GD.Print($"WorldLoader: loading cached world {worldId} from {local}");
                return LoadFromFile(local, worldId, root);
            }
        }

        // 2. Developer 3DWorlds folder (local testing without CDN).
        string devPath = $"/media/pikachubolk/63d7930c-4cfb-4c68-96a6-879048200e36/root-files/Documents/Models/3DWorlds/SerikaWorlds/built/{worldId}";
        foreach (var ext in exts)
        {
            string local = $"{devPath}{ext}";
            if (File.Exists(local))
            {
                GD.Print($"WorldLoader: loading dev world {worldId} from {local}");
                return LoadFromFile(local, worldId, root);
            }
        }

        GD.Print($"WorldLoader: no asset found for world {worldId} — will use C# fallback");
        return null;
    }

    /// Load a specific world file by absolute path, bypassing the cache and dev-folder search.
    /// Used by the world diagnostic to inspect one exact bundle.
    public static Vector3? LoadFromPath(string path, string worldId, Node3D root) =>
        LoadFromFile(path, worldId, root);

    private static Vector3? LoadFromFile(string path, string worldId, Node3D root)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();

        if (ext == ".serikaworld")
            return LoadSerikaWorld(path, worldId, root);

        if (ext == ".pck" || ext == ".skw")
        {
            if (!ProjectSettings.LoadResourcePack(path))
            {
                GD.PrintErr($"WorldLoader: failed to load resource pack {path}");
                return DefaultSpawn;
            }
            var scene = LoadPackedScene($"res://{worldId}.tscn") ?? LoadPackedScene($"res://{worldId}.scn") ?? LoadPackedScene("res://world.tscn");
            return Attach(scene, worldId, root, null);
        }

        if (ext == ".tscn" || ext == ".scn")
            return Attach(LoadPackedScene(path), worldId, root, null);

        if (ext == ".glb" || ext == ".gltf")
        {
            var node = LoadGltfFromFile(path);
            return Attach(node, worldId, root, null);
        }

        GD.PrintErr($"WorldLoader: unknown world extension {ext}");
        return DefaultSpawn;
    }

    /// Load a `.serikaworld` (ZIP): read manifest.json + the embedded GLB from the archive.
    private static Vector3? LoadSerikaWorld(string path, string worldId, Node3D root)
    {
        var zip = new ZipReader();
        if (zip.Open(path) != Error.Ok)
        {
            GD.PrintErr($"WorldLoader: cannot open .serikaworld {path}");
            return DefaultSpawn;
        }

        // Manifest (optional — the world still loads with a default spawn if absent).
        Vector3? manifestSpawn = null;
        string modelFile = "world.glb";
        string lighting = "outdoor";
        if (zip.FileExists("manifest.json"))
        {
            try
            {
                var json = System.Text.Encoding.UTF8.GetString(zip.ReadFile("manifest.json"));
                using var doc = JsonDocument.Parse(json);
                var rootEl = doc.RootElement;
                if (rootEl.TryGetProperty("modelFile", out var mf) && mf.ValueKind == JsonValueKind.String)
                    modelFile = mf.GetString() ?? "world.glb";
                if (rootEl.TryGetProperty("lighting", out var lt) && lt.ValueKind == JsonValueKind.String)
                    lighting = lt.GetString() ?? "outdoor";
                if (rootEl.TryGetProperty("spawn", out var sp) && sp.ValueKind == JsonValueKind.Array && sp.GetArrayLength() >= 3)
                    manifestSpawn = new Vector3((float)sp[0].GetDouble(), (float)sp[1].GetDouble(), (float)sp[2].GetDouble());
            }
            catch (Exception e)
            {
                GD.PrintErr($"WorldLoader: bad manifest.json in {path}: {e.Message}");
            }
        }

        if (!zip.FileExists(modelFile))
        {
            GD.PrintErr($"WorldLoader: .serikaworld {path} has no {modelFile}");
            zip.Close();
            return DefaultSpawn;
        }

        byte[] glb = zip.ReadFile(modelFile);
        zip.Close();

        var node = LoadGltfFromBuffer(glb, Path.GetDirectoryName(path) ?? "");
        return Attach(node, worldId, root, manifestSpawn, lighting);
    }

    private static Node3D LoadGltfFromFile(string path)
    {
        var doc = new GltfDocument();
        var state = new GltfState();
        if (doc.AppendFromFile(path, state) != Error.Ok)
        {
            GD.PrintErr($"WorldLoader: failed to load glTF {path}");
            return null;
        }
        return doc.GenerateScene(state) as Node3D;
    }

    private static Node3D LoadGltfFromBuffer(byte[] glb, string basePath)
    {
        var doc = new GltfDocument();
        var state = new GltfState();
        if (doc.AppendFromBuffer(glb, basePath, state) != Error.Ok)
        {
            GD.PrintErr("WorldLoader: failed to parse embedded GLB");
            return null;
        }
        return doc.GenerateScene(state) as Node3D;
    }

    /// Parent the instantiated world under root, generate collision, and resolve the spawn.
    private static Vector3? Attach(Node instance, string worldId, Node3D root, Vector3? manifestSpawn, string lighting = "outdoor")
    {
        if (instance == null)
        {
            GD.PrintErr($"WorldLoader: could not instantiate world {worldId}");
            return DefaultSpawn;
        }

        instance.Name = $"World[{worldId}]";
        root.AddChild(instance);

        // GLB/GLTF worlds ship no collision — generate concave collision from every mesh so
        // the player walks on the geometry instead of falling through. Runs BEFORE marker
        // resolution so runtime components (which manage their own collision) are untouched.
        GenerateCollision(instance);

        // glTF-imported lights arrive at ~54× the authored wattage (candela/lux conversion),
        // giving energies in the thousands that blow enclosed rooms to solid white. Rescale
        // the set to a sane range, preserving the author's relative intensities.
        NormalizeWorldLights(instance);

        // Swap authoring markers for the live nodes they stand for (mirrors, seats, video).
        var spawnMarker = ResolveMarkers(instance);

        // Baked model worlds ship no lights of their own, so avatars (lit at runtime, not
        // baked) would be black. Add a ceiling grid of fill lights sized to the geometry.
        if (lighting == "baked")
            AddFillLights(instance as Node3D);

        // Environment tuned to the world: a dark cinema, a bright studio, a baked interior, …
        SetupEnvironment(root, lighting);

        // Spawn precedence: manifest → SPAWN marker → an in-scene node named *spawn* →
        // the geometry's own floor-centre → the origin. The AABB fallback keeps model worlds
        // (which are rarely modelled around the origin) from spawning the player in a wall or
        // in mid-air far from the room.
        return manifestSpawn ?? spawnMarker ?? FindSpawnPoint(instance)
            ?? FloorCentre(instance as Node3D) ?? DefaultSpawn;
    }

    /// Rescale all imported world lights so the brightest sits at a sane Godot energy,
    /// preserving relative intensities. Directional lights (the sun) are kept a touch stronger
    /// than the omni fill so outdoor scenes still read as sunlit.
    private static void NormalizeWorldLights(Node root)
    {
        var lights = new System.Collections.Generic.List<Light3D>();
        CollectLights(root, lights);
        if (lights.Count == 0) return;

        float maxE = 0f;
        foreach (var l in lights) maxE = Mathf.Max(maxE, l.LightEnergy);
        const float TargetMax = 2.2f;   // brightest omni after rescale
        if (maxE <= TargetMax * 1.5f) return; // already sane (e.g. a C# fallback set these)

        float scale = TargetMax / maxE;
        foreach (var l in lights)
        {
            l.LightEnergy *= scale;
            // Sun/moon: clamp into a gentle daylight range so it isn't washed out or invisible.
            if (l is DirectionalLight3D)
                l.LightEnergy = Mathf.Clamp(l.LightEnergy, 0.6f, 1.4f);
            else
                l.LightEnergy = Mathf.Min(l.LightEnergy, TargetMax);
        }
        GD.Print($"WorldLoader: rescaled {lights.Count} world lights (max {maxE:0} → {TargetMax})");
    }

    private static void CollectLights(Node node, System.Collections.Generic.List<Light3D> into)
    {
        if (node is Light3D l) into.Add(l);
        foreach (Node child in node.GetChildren())
            CollectLights(child, into);
    }

    /// World-space AABB of every visible mesh under a node, or null if there is none.
    private static Aabb? WorldAabb(Node3D root)
    {
        if (root == null) return null;
        Aabb? total = null;
        foreach (Node n in root.FindChildren("*", "MeshInstance3D", owned: false))
        {
            if (n is MeshInstance3D mi && mi.Mesh != null)
            {
                var local = mi.GetAabb();
                var world = mi.GlobalTransform * local; // transforms all 8 corners
                total = total.HasValue ? total.Value.Merge(world) : world;
            }
        }
        return total;
    }

    /// Floor-centre of the geometry, 1 m up — a safe spawn for worlds not built around origin.
    private static Vector3? FloorCentre(Node3D root)
    {
        var aabb = WorldAabb(root);
        if (!aabb.HasValue) return null;
        var a = aabb.Value;
        var c = a.GetCenter();
        return new Vector3(c.X, a.Position.Y + 1.0f, c.Z);
    }

    /// Add a ceiling grid of soft omni lights sized to the geometry, so a baked, lightless
    /// world still shades dynamic content (avatars) instead of leaving it black.
    private static void AddFillLights(Node3D root)
    {
        var aabb = WorldAabb(root);
        if (!aabb.HasValue) return;
        var a = aabb.Value;
        float w = Mathf.Max(1f, a.Size.X), d = Mathf.Max(1f, a.Size.Z), h = Mathf.Max(1f, a.Size.Y);

        int nx = Mathf.Clamp(Mathf.RoundToInt(w / 12f), 1, 4);
        int nz = Mathf.Clamp(Mathf.RoundToInt(d / 12f), 1, 4);
        float y = a.Position.Y + h * 0.9f;                 // just under the ceiling
        float range = Mathf.Max(w / nx, d / nz);
        float energy = Mathf.Clamp(range / 10f, 0.8f, 2.2f); // gentle — baked textures already carry the look

        var holder = new Node3D { Name = "FillLights" };
        root.AddChild(holder);
        for (int iz = 0; iz < nz; iz++)
        for (int ix = 0; ix < nx; ix++)
        {
            holder.AddChild(new OmniLight3D
            {
                Position = new Vector3(a.Position.X + w * (ix + 0.5f) / nx, y, a.Position.Z + d * (iz + 0.5f) / nz),
                OmniRange = range * 1.6f,
                LightEnergy = energy,
                LightColor = new Color(1.0f, 0.96f, 0.9f),
                ShadowEnabled = false, // fill only — real shadows would fight the baked ones
            });
        }
        GD.Print($"WorldLoader: {nx * nz} fill lights ({nx}x{nz}, range {range:0.0}, energy {energy:0.0})");
    }

    // ── Authoring markers ────────────────────────────────────────────────────────────────
    //
    // Reflections, seats and video surfaces are runtime behaviour and cannot be baked into a
    // mesh, so worlds authored in Blender export empty marker nodes instead. The prefixes are
    // the contract between the world-authoring tools and the client:
    //
    //   SPAWN          player spawn point
    //   SERIKA_MIRROR  a real Mirror        (scale.X = width, scale.Z = height)
    //   SERIKA_SEAT    a sittable SeatNode
    //   SERIKA_VIDEO   the video screen     (scale.X = width, scale.Z = height)
    private const string MarkerMirror = "SERIKA_MIRROR";
    private const string MarkerSeat   = "SERIKA_SEAT";
    private const string MarkerVideo  = "SERIKA_VIDEO";

    /// Replace every marker node with the live node it stands for. Returns the SPAWN
    /// marker's position when the world declares one.
    private static Vector3? ResolveMarkers(Node worldRoot)
    {
        Vector3? spawn = null;
        int mirrors = 0, seats = 0, videos = 0;

        // Snapshot first: we mutate the tree while walking it.
        var markers = new System.Collections.Generic.List<Node3D>();
        Collect(worldRoot, markers);

        foreach (var m in markers)
        {
            string name = m.Name.ToString();
            var xform = m.GlobalTransform;
            var pos = xform.Origin;
            float yawDeg = Mathf.RadToDeg(xform.Basis.GetEuler().Y);
            var scale = xform.Basis.Scale;
            var parent = m.GetParent();

            if (name.StartsWith(MarkerMirror, StringComparison.Ordinal))
            {
                float w = Mathf.Max(0.2f, scale.X);
                float h = Mathf.Max(0.2f, scale.Z);
                parent.AddChild(Mirror.Create(Vector3.Zero, 0f, w, h));
                var mirror = parent.GetChild(parent.GetChildCount() - 1) as Node3D;
                mirror.GlobalPosition = pos;
                mirror.GlobalRotation = new Vector3(0, Mathf.DegToRad(yawDeg), 0);
                mirrors++;
            }
            else if (name.StartsWith(MarkerSeat, StringComparison.Ordinal))
            {
                // SitYaw is radians (it's applied straight to a node rotation), while yawDeg is
                // degrees like the mirror/video markers use. Feeding degrees in pointed seats
                // at an essentially arbitrary angle — latent until seats became sittable.
                var seat = new SeatNode
                {
                    Name = name.Replace(MarkerSeat, "Seat"),
                    SitYaw = Mathf.DegToRad(yawDeg),
                };
                parent.AddChild(seat);
                seat.GlobalPosition = pos;
                seats++;
            }
            else if (name.StartsWith(MarkerVideo, StringComparison.Ordinal))
            {
                float w = Mathf.Max(1f, scale.X);
                float h = Mathf.Max(1f, scale.Z);
                var screen = new MeshInstance3D
                {
                    Name = "VideoScreen",
                    Mesh = new QuadMesh { Size = new Vector2(w, h) },
                    MaterialOverride = new StandardMaterial3D
                    {
                        AlbedoColor = new Color(0.02f, 0.02f, 0.03f),
                        EmissionEnabled = true,
                        Emission = new Color(0.05f, 0.05f, 0.08f),
                    },
                };
                parent.AddChild(screen);
                screen.GlobalPosition = pos;
                screen.GlobalRotation = new Vector3(0, Mathf.DegToRad(yawDeg), 0);
                parent.AddChild(new SerikaSocial.World.Video.VideoScreen(screen));
                videos++;
            }
            else // SPAWN
            {
                spawn = pos;
            }

            m.QueueFree();
        }

        if (mirrors + seats + videos > 0)
            GD.Print($"WorldLoader: resolved markers — {mirrors} mirror(s), {seats} seat(s), {videos} video screen(s)");

        return spawn;
    }

    private static void Collect(Node node, System.Collections.Generic.List<Node3D> into)
    {
        if (node is Node3D n3d)
        {
            string name = node.Name.ToString();
            if (name.StartsWith("SERIKA_", StringComparison.Ordinal) ||
                name.Equals("SPAWN", StringComparison.OrdinalIgnoreCase))
                into.Add(n3d);
        }
        foreach (Node child in node.GetChildren())
            Collect(child, into);
    }

    /// Recursively add trimesh (concave) static collision to every MeshInstance3D.
    private static void GenerateCollision(Node node)
    {
        if (node is MeshInstance3D mi && mi.Mesh != null)
            mi.CreateTrimeshCollision();

        foreach (Node child in node.GetChildren())
            GenerateCollision(child);
    }

    /// Configure the world environment for a lighting mode. GLB worlds ship no
    /// WorldEnvironment, so one flat ambient used to be applied to everything — which blew out
    /// the dark cinema and left baked interiors murky. Each mode picks an ambient level, sky
    /// and tonemap suited to that kind of space.
    ///
    ///   outdoor  daylit open worlds (Commons, tests) — soft sky ambient + the world's own sun
    ///   studio   bright, even, shadow-light rooms (Mirror Gallery)
    ///   lit      warm interiors that carry their own lamps (Serika Home)
    ///   dark     theatres — near-black ambient so the screen and sconces dominate (Cinema)
    ///   baked    model worlds whose lighting is painted into the textures (Backrooms, Gryffindor)
    private static void SetupEnvironment(Node3D root, string mode)
    {
        // A pre-existing WorldEnvironment (e.g. a C# fallback builder set one up) wins.
        foreach (Node child in root.GetChildren())
            if (child is WorldEnvironment)
                return;

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
        };

        Color skyTop, skyHorizon, ambient;
        float ambientEnergy;
        var ambientSource = Godot.Environment.AmbientSource.Color;

        switch (mode)
        {
            case "dark": // Cinema: let the screen and sconces do the work.
                skyTop = new Color(0.01f, 0.01f, 0.02f);
                skyHorizon = new Color(0.02f, 0.02f, 0.04f);
                ambient = new Color(0.10f, 0.10f, 0.14f);
                ambientEnergy = 0.06f;
                break;
            case "baked": // Lighting is in the textures: flat, fairly bright, no double shadows.
                skyTop = new Color(0.05f, 0.05f, 0.06f);
                skyHorizon = new Color(0.08f, 0.08f, 0.10f);
                ambient = new Color(0.85f, 0.83f, 0.80f);
                ambientEnergy = 0.9f;
                break;
            case "lit": // Warm interior with its own lamps.
                skyTop = new Color(0.06f, 0.05f, 0.08f);
                skyHorizon = new Color(0.12f, 0.10f, 0.12f);
                ambient = new Color(0.42f, 0.38f, 0.40f);
                ambientEnergy = 0.5f;
                break;
            case "studio": // Bright, even, low-contrast.
                skyTop = new Color(0.18f, 0.16f, 0.22f);
                skyHorizon = new Color(0.30f, 0.26f, 0.34f);
                ambient = new Color(0.70f, 0.68f, 0.75f);
                ambientEnergy = 0.85f;
                break;
            default: // outdoor
                skyTop = new Color(0.08f, 0.05f, 0.12f);
                skyHorizon = new Color(0.18f, 0.12f, 0.25f);
                ambient = new Color(0.55f, 0.5f, 0.6f);
                ambientEnergy = 0.45f;
                break;
        }

        env.Sky = new Sky
        {
            SkyMaterial = new ProceduralSkyMaterial { SkyTopColor = skyTop, SkyHorizonColor = skyHorizon },
        };
        env.AmbientLightSource = ambientSource;
        env.AmbientLightColor = ambient;
        env.AmbientLightEnergy = ambientEnergy;

        root.AddChild(new WorldEnvironment { Environment = env });
        GD.Print($"WorldLoader: environment mode '{mode}' (ambient {ambientEnergy})");
    }

    private static Node LoadPackedScene(string path)
    {
        var packed = ResourceLoader.Load<PackedScene>(path);
        if (packed == null)
        {
            GD.PrintErr($"WorldLoader: packed scene not found at {path}");
            return null;
        }
        return packed.Instantiate();
    }

    /// Look for a node named Spawn/SpawnPoint/SerikaSpawn and return its world position.
    private static Vector3? FindSpawnPoint(Node root)
    {
        foreach (Node child in root.FindChildren("*", owned: false))
        {
            if (child is Node3D n3d)
            {
                string lower = child.Name.ToString().ToLowerInvariant();
                if (lower.Contains("spawn"))
                    return n3d.GlobalPosition;
            }
        }
        return null;
    }
}
