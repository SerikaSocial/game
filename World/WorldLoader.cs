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
        string collision = "geometry";
        string shading = "default";
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
                if (rootEl.TryGetProperty("collision", out var co) && co.ValueKind == JsonValueKind.String)
                    collision = co.GetString() ?? "geometry";
                if (rootEl.TryGetProperty("shading", out var sh) && sh.ValueKind == JsonValueKind.String)
                    shading = sh.GetString() ?? "default";
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
        return Attach(node, worldId, root, manifestSpawn, lighting, collision, shading);
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
    private static Vector3? Attach(Node instance, string worldId, Node3D root, Vector3? manifestSpawn, string lighting = "outdoor",
                                   string collision = "geometry", string shading = "default")
    {
        if (instance == null)
        {
            GD.PrintErr($"WorldLoader: could not instantiate world {worldId}");
            return DefaultSpawn;
        }

        instance.Name = $"World[{worldId}]";
        root.AddChild(instance);

        // Existing worlds generate collision from their visual geometry. Authored worlds
        // opt in to simple COL_* proxies so leaves, water, tiles and joints cannot snag the
        // player. Run before marker resolution; live components own their own collision.
        if (collision == "authored")
        {
            if (GenerateAuthoredCollision(instance) == 0)
            {
                GD.PrintErr($"WorldLoader: {worldId} requests authored collision but has no COL_* mesh proxies");
                root.RemoveChild(instance);
                instance.QueueFree();
                return null;
            }
        }
        else
            GenerateCollision(instance);

        // glTF-imported lights arrive at ~54× the authored wattage (candela/lux conversion),
        // giving energies in the thousands that blow enclosed rooms to solid white. Rescale
        // the set to a sane range, preserving the author's relative intensities.
        NormalizeWorldLights(instance);

        // An explicit PBR world preserves its exported material response even if the global
        // world-toon toggle is enabled. Other worlds retain the current client policy. Run
        // before markers so live mirrors, video screens and seats keep their own materials.
        if (shading != "pbr")
            SerikaSocial.Avatar.ToonShading.ApplyToWorld(instance);

        // Swap authoring markers for the live nodes they stand for (mirrors, seats, video).
        var spawnMarker = ResolveMarkers(instance, out int videoScreens);
        StartAmbientAnimations(instance);

        // A theatre with a screen in it gets house lights: the room drops when a clip starts and
        // the picture becomes the only thing lighting it. Gated on the `dark` mode, so a video
        // screen in a daylit world does not put out the sun when someone queues a clip.
        if (lighting == "dark" && videoScreens > 0)
            (instance as Node3D)?.AddChild(new HouseLights { Name = "HouseLights" });

        // Baked model worlds ship no lights of their own, so avatars (lit at runtime, not
        // baked) would be black. Add a ceiling grid of fill lights sized to the geometry.
        if (lighting == "baked")
            AddFillLights(instance as Node3D);

        // Environment tuned to the world: a dark cinema, a bright studio, a baked interior, …
        SetupEnvironment(root, lighting);

        // …and a sun, if the GLB brought none. Must run after the fill-light pass above so a
        // baked world's grid is already in place, and before `ApplyToScene` so the new light
        // gets the device profile's shadow settings stamped onto it like any other.
        EnsureKeyLight(instance as Node3D ?? root, lighting);

        if (lighting == "dark" && UI.DeviceProfile.IsStandaloneXr)
            ApplyStandaloneDarkVisibility(root, instance);

        // Cloud worlds bring their own lights and environment, all authored at full quality.
        // Stamp the device profile over them, or a Quest ends up rendering a desktop-tier world.
        UI.DeviceProfile.ApplyToScene(root);

        // Distance-based LOD: on Quest, tag meshes with visibility ranges so far-off detail
        // fades out instead of drowning the GPU. This is a load-time pass — zero per-frame cost.
        WorldLod.Apply(instance);

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

        // Shadows and falloff are wrong on import regardless of how sane the energies are, so
        // this pass runs unconditionally — it used to sit inside the rescale loop below, which
        // meant any world whose lights were already correctly scaled silently kept its
        // shadowless, flat-falloff lights.
        // Reach first: it reads the *imported* energy, which is still in glTF candela at this
        // point. Rescaling below destroys that.
        foreach (var l in lights) ClampLightReach(l);
        foreach (var l in lights) ConfigureLightQuality(l);
        CapPositionalLightsForMobile(lights);

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

    /// Give an imported point/spot light a finite reach.
    ///
    /// This is the single biggest reason cloud worlds came out washed out. glTF has no required
    /// `range` on a punctual light and Blender's exporter writes none, so Godot imports every
    /// one of them at its default **4096 m**. Godot's falloff is
    /// `pow(1 - distance / range, attenuation)` — at a range of 4096 that term is 0.998 across a
    /// 16 x 24 m room, i.e. no falloff at all. The Cinema's nine lamps were therefore each
    /// lighting every surface in the building at essentially full strength: a flat ~10x
    /// over-exposure with no pools of light, no gradients, and everything pushed past the glow
    /// threshold so the whole room hazed over. It read as "the world is too bright" but the
    /// energies were fine — only the distances were wrong.
    ///
    /// glTF point/spot intensity is in candela, which falls off as inverse-square, so the
    /// distance at which a lamp stops mattering is `sqrt(intensity / cutoff)`. That puts a 5163 cd
    /// sconce at ~10 m and the 7609 cd screen bounce at ~12 m — believable for a room this size,
    /// and derived from what the author actually wrote rather than a magic number per world.
    ///
    /// Only applied when the range is still the importer's default: an author who set a range
    /// meant it, and every C# fallback builder sets its own.
    private static void ClampLightReach(Light3D l)
    {
        const float ImporterDefaultRange = 4096f; // Godot's default when glTF omits `range`
        const float CutoffCandela = 50f;          // below this a lamp contributes nothing visible
        const float MinRange = 4f, MaxRange = 40f;

        float energy = l.LightEnergy;
        float reach = Mathf.Clamp(Mathf.Sqrt(Mathf.Max(energy, 1f) / CutoffCandela), MinRange, MaxRange);

        if (l is OmniLight3D omni)
        {
            if (omni.OmniRange < ImporterDefaultRange - 1f) return;
            omni.OmniRange = reach;
        }
        else if (l is SpotLight3D spot)
        {
            if (spot.SpotRange < ImporterDefaultRange - 1f) return;
            spot.SpotRange = reach;
        }
    }

    /// Make a world's own lights behave like lights rather than decals.
    ///
    /// A GLB's punctual lights import with shadows off and a hard-edged default falloff, so a
    /// wall sconce lit a perfect circle *through* its own fixture, through pillars and through
    /// the seat backs in front of it. Shadows are what make the pool of light take the shape of
    /// the room; the soft-shadow size is what stops the edge looking stencilled.
    private static void ConfigureLightQuality(Light3D l)
    {
        if (l is DirectionalLight3D) return; // handled by DeviceProfile / EnsureKeyLight
        if (!UI.DeviceProfile.Shadows) return;

        l.ShadowEnabled = true;

        // These bias values are large, and they have to be.
        //
        // A room this size lit by point lights self-shadows badly at the defaults (0.02 /
        // 1.0): the acne shows up not as speckle but as smooth *concentric rings* centred on
        // each lamp, because the surfaces are big untextured flats and the depth comparison
        // fails in even bands radiating from the light. It looked so unlike normal shadow
        // acne that it was mistaken in turn for SSAO, SSR, SDFGI, glow, an 8-bit banding
        // problem and a bad normal map — all of which were ruled out one at a time before the
        // shadow pass turned out to be the cause. Raising the bias clears it completely and
        // costs nothing here, because nothing in these worlds is thin enough to peter-pan.
        l.ShadowBias = 0.20f;
        l.ShadowNormalBias = 3.0f;

        // The falloff *curve* is deliberately left exactly as imported.
        //
        // Steepening it (an `OmniAttenuation` of 1.6, chosen to look "more physical") made the
        // same ringing worse independently of the shadow bias, by compressing each light's
        // gradient into fewer of the available levels. Whatever the exporter wrote is spread
        // across enough range to stay smooth — leave it alone.
        if (l is OmniLight3D omni)
        {
            // A real bulb has area, so its shadow edge softens with distance from the occluder.
            // Verified safe once the bias above is in place: soft shadows were suspected of
            // causing the ringing and are not.
            omni.LightSize = 0.12f;
            omni.OmniShadowMode = OmniLight3D.ShadowMode.Cube;
        }
        else if (l is SpotLight3D spot)
        {
            spot.LightSize = 0.1f;
        }
    }

    /// Compatibility (the Android / Quest renderer) only shades each mesh with a handful of
    /// omni/spot lights. A cinema ships nine sconces plus a screen bounce — over the limit —
    /// so most of the room is unlit and reads as black. Keep the brightest few and hide the
    /// rest; the remaining lamps plus the directional fill cover the space.
    private static void CapPositionalLightsForMobile(System.Collections.Generic.List<Light3D> lights)
    {
        if (!UI.DeviceProfile.IsStandaloneXr) return;
        var positional = new System.Collections.Generic.List<Light3D>();
        foreach (var l in lights)
            if (l is OmniLight3D or SpotLight3D) positional.Add(l);
        // Compatibility's per-mesh omni budget is 8. Hiding down to 6 still left the
        // Cinema's seats and far walls in the dark; keep the renderer's full set.
        const int Max = 8;
        if (positional.Count <= Max) return;
        positional.Sort((a, b) => b.LightEnergy.CompareTo(a.LightEnergy));
        for (int i = Max; i < positional.Count; i++)
            positional[i].Visible = false;
        GD.Print($"WorldLoader: hid {positional.Count - Max} extra lights for mobile Compatibility (kept {Max})");
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
    //   SERIKA_PROP    a grabbable PhysicsProp (scale = box extents)
    private const string MarkerMirror = "SERIKA_MIRROR";
    private const string MarkerSeat   = "SERIKA_SEAT";
    private const string MarkerVideo  = "SERIKA_VIDEO";
    private const string MarkerProp   = "SERIKA_PROP";
    private const string MarkerPortal = "SERIKA_PORTAL_";

    /// First network id handed to a marker-spawned prop.
    ///
    /// The prop id range is shared with the marker pens `Main` spawns at 100-104, so authored
    /// props start at 200 to stay clear of them. Ids are assigned in tree-walk order, which is
    /// stable for a given bundle — every client loading the same `.serikaworld` therefore
    /// numbers the props identically, which is the whole requirement for sync to work.
    private const ushort PropNetIdBase = 200;

    /// Replace every marker node with the live node it stands for. Returns the SPAWN
    /// marker's position when the world declares one, and how many video screens it built —
    /// the screens are not in the scene tree yet at this point, so the caller cannot count
    /// them by group.
    private static Vector3? ResolveMarkers(Node worldRoot, out int videoScreens)
    {
        Vector3? spawn = null;
        int mirrors = 0, seats = 0, videos = 0, props = 0, portals = 0;

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

            if (name.StartsWith(MarkerPortal, StringComparison.Ordinal))
            {
                if (!TryParsePortalMarker(name, out var targetId, out var label))
                {
                    GD.PrintErr($"WorldLoader: ignored invalid portal marker '{name}'");
                    continue;
                }
                // Unlike the legacy mirror marker, portal dimensions use Godot X/Y/Z (width,
                // height, depth). Blender empties therefore use scale (width, depth, height).
                // Bake ancestor scale into dimensions once; physics never inherits a scaled body.
                var portal = Portal.Create(label, new Color(.23f, .48f, .52f), Vector3.Zero, 0,
                    PortalMode.DirectWorld, targetId, width: xform.Basis.X.Length(),
                    height: xform.Basis.Y.Length(), depth: xform.Basis.Z.Length());
                portal.TopLevel = true;
                parent.AddChild(portal);
                portal.GlobalPosition = pos;
                portal.GlobalRotation = new Vector3(0, Mathf.DegToRad(yawDeg), 0);
                portals++;
            }
            else if (name.StartsWith(MarkerMirror, StringComparison.Ordinal))
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
            else if (name.StartsWith(MarkerProp, StringComparison.Ordinal))
            {
                // Marker scale is the box's full extents, floored so a marker left at the
                // default scale of 1 does not become a 1 m crate nobody can lift.
                var size = new Vector3(Mathf.Max(0.1f, scale.X), Mathf.Max(0.1f, scale.Y),
                                       Mathf.Max(0.1f, scale.Z));
                var prop = new PhysicsProp
                {
                    Name = name.Replace(MarkerProp, "Prop"),
                    NetId = (ushort)(PropNetIdBase + props),
                    Networked = true,
                    PropName = "Pick up",
                };
                prop.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
                prop.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = size },
                    MaterialOverride = new StandardMaterial3D
                    {
                        AlbedoColor = new Color(0.50f, 0.45f, 0.55f),
                        Roughness = 0.6f,
                        Metallic = 0.2f,
                    },
                });
                parent.AddChild(prop);
                prop.GlobalPosition = pos;
                prop.GlobalRotation = new Vector3(0, Mathf.DegToRad(yawDeg), 0);
                props++;
            }
            else // SPAWN
            {
                spawn = pos;
            }

            m.QueueFree();
        }

        if (mirrors + seats + videos + props + portals > 0)
            GD.Print($"WorldLoader: resolved markers — {mirrors} mirror(s), {seats} seat(s), " +
                     $"{videos} video screen(s), {props} prop(s), {portals} portal(s)");

        videoScreens = videos;
        return spawn;
    }

    private static void Collect(Node node, System.Collections.Generic.List<Node3D> into)
    {
        if (node is Node3D n3d)
        {
            string name = node.Name.ToString();
            if (name.StartsWith(MarkerMirror, StringComparison.Ordinal) ||
                name.StartsWith(MarkerSeat, StringComparison.Ordinal) ||
                name.StartsWith(MarkerVideo, StringComparison.Ordinal) ||
                name.StartsWith(MarkerProp, StringComparison.Ordinal) ||
                name.StartsWith(MarkerPortal, StringComparison.Ordinal) ||
                name.Equals("SPAWN", StringComparison.OrdinalIgnoreCase))
                into.Add(n3d);
        }
        foreach (Node child in node.GetChildren())
            Collect(child, into);
    }

    /// Portal targets are data, never URLs or executable script. A UUID in the node name
    /// survives both Blender and Godot GLB import without depending on optional extras support.
    internal static bool TryParsePortalMarker(string name, out string worldId, out string label)
    {
        worldId = label = "";
        if (!name.StartsWith(MarkerPortal, StringComparison.Ordinal)) return false;
        var fields = name.Substring(MarkerPortal.Length).Split(new[] { "__" }, 2, StringSplitOptions.None);
        if (!Guid.TryParseExact(fields[0], "D", out var id) || id == Guid.Empty) return false;
        worldId = id.ToString("D");
        label = fields.Length > 1 ? fields[1].Replace('_', ' ').Trim() : "Enter world";
        if (label.Length == 0) label = "Enter world";
        if (label.Length > 64) label = label.Substring(0, 64);
        return true;
    }

    /// The aquarium's shared Blender NLA track is an explicit ambient-animation opt-in.
    /// Leave RESET and unrelated authored clips stopped; animated scenery stays script-free.
    private static void StartAmbientAnimations(Node node)
    {
        if (node is AnimationPlayer player)
            foreach (var name in player.GetAnimationList())
                if (name.Equals("AquariumSwim", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith("/AquariumSwim", StringComparison.OrdinalIgnoreCase))
                {
                    var clip = player.GetAnimation(name);
                    clip.LoopMode = Animation.LoopModeEnum.Linear;
                    player.Play(name);
                    player.Advance(0);
                    GD.Print($"WorldLoader: looping ambient animation '{name}' ({clip.Length:0.00}s)");
                    break;
                }
        foreach (Node child in node.GetChildren()) StartAmbientAnimations(child);
    }

    /// Recursively add trimesh (concave) static collision to every MeshInstance3D.
    private static void GenerateCollision(Node node)
    {
        if (node is MeshInstance3D mi && mi.Mesh != null)
            mi.CreateTrimeshCollision();

        foreach (Node child in node.GetChildren())
            GenerateCollision(child);
    }

    /// Build collision only from COL_* meshes in a manifest-opted-in static world.
    /// Bake full world transforms into the faces: concave physics shapes cannot reliably
    /// inherit negative or nonuniform scale. TopLevel keeps the generated body's transform
    /// at identity even when the imported GLB has transformed ancestors. A reflected basis
    /// also reverses winding, so swap each triangle back to retain its outward collision face.
    private static int GenerateAuthoredCollision(Node instance)
    {
        var proxies = new System.Collections.Generic.List<MeshInstance3D>();
        CollectCollisionProxies(instance, proxies);
        int generated = 0;
        foreach (var proxy in proxies)
        {
            var faces = proxy.Mesh.GetFaces();
            if (faces.Length == 0) continue;
            var transform = proxy.GlobalTransform;
            for (int i = 0; i < faces.Length; i++) faces[i] = transform * faces[i];
            if (transform.Basis.Determinant() < 0)
                for (int i = 0; i + 2 < faces.Length; i += 3)
                    (faces[i + 1], faces[i + 2]) = (faces[i + 2], faces[i + 1]);

            var shape = new ConcavePolygonShape3D();
            shape.SetFaces(faces);
            var body = new StaticBody3D { Name = $"AuthoredCollision_{generated}", TopLevel = true };
            instance.AddChild(body);
            body.GlobalTransform = Transform3D.Identity;
            body.AddChild(new CollisionShape3D { Shape = shape });
            proxy.Visible = false;
            generated++;
        }
        GD.Print($"WorldLoader: authored collision — {generated} hidden proxy mesh(es)");
        return generated;
    }

    private static void CollectCollisionProxies(Node node, System.Collections.Generic.List<MeshInstance3D> into)
    {
        if (node is MeshInstance3D mi && mi.Mesh != null &&
            mi.Name.ToString().StartsWith("COL_", StringComparison.Ordinal))
            into.Add(mi);
        foreach (Node child in node.GetChildren()) CollectCollisionProxies(child, into);
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
        // A pre-existing WorldEnvironment (e.g. a C# fallback builder set one up) keeps its
        // authored sky, ambient and fog — but it still gets the post-processing pass below.
        //
        // This used to return outright, which meant every world that shipped an environment —
        // which is most of them — silently opted out of ambient occlusion and tonemapping. The
        // Cinema was the giveaway: 26 meshes, 112 seats, and not one contact shadow between
        // them, so the whole room read as flat purple paper.
        foreach (Node child in root.GetChildren())
        {
            if (child is WorldEnvironment existing && existing.Environment != null)
            {
                ApplyPostProcessing(existing.Environment, mode);
                if (mode == "dark" && UI.DeviceProfile.IsStandaloneXr)
                    ForceStandaloneDarkEnv(existing.Environment);
                GD.Print($"WorldLoader: kept authored environment, added post-processing " +
                         $"(mode '{mode}')");
                return;
            }
        }

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
                // Quest / Android uses the Compatibility renderer, which only shades a
                // mesh with a handful of omnis. Near-zero ambient then reads as a black
                // room with a couple of glowing patches. Lift it a little on standalone so
                // the aisles stay walkable; desktop keeps the theatrical dark.
                //
                // Only a *little*: ambient is directionless, so it is the one knob that can
                // make a room brighter and flatter at the same time. The readability fix is
                // the albedo lift in ApplyStandaloneDarkVisibility, not this.
                if (UI.DeviceProfile.IsStandaloneXr)
                {
                    ambient = new Color(0.62f, 0.58f, 0.68f);
                    ambientEnergy = StandaloneDarkAmbient;
                }
                else
                {
                    ambient = new Color(0.10f, 0.10f, 0.14f);
                    ambientEnergy = 0.06f;
                }
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
            case "daylit": // Neutral daylight for atria and skylit natural interiors.
                skyTop = new Color(.20f, .36f, .52f);
                skyHorizon = new Color(.65f, .72f, .77f);
                ambient = new Color(.75f, .82f, .90f);
                ambientEnergy = .45f;
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

        ApplyPostProcessing(env, mode);

        root.AddChild(new WorldEnvironment { Environment = env });
        GD.Print($"WorldLoader: environment mode '{mode}' (ambient {ambientEnergy}, " +
                 $"ssao {env.SsaoEnabled}, glow {env.GlowEnabled})");
    }

    /// Screen-space effects applied to every world, authored environment or not.
    ///
    /// Deliberately limited to things that add depth cues rather than style: an author's sky,
    /// ambient colour and fog are their decisions, but "objects should look like they are
    /// touching the floor" is not a stylistic choice.
    private static void ApplyPostProcessing(Godot.Environment env, string mode)
    {
        // Contact shadows. Ambient light alone leaves every corner, doorway and
        // object-on-floor junction at exactly the same brightness as the open floor, which is
        // what makes an ambient-lit GLB read as flat papercraft. Skipped on the mobile tier,
        // where SSAO is a full-resolution depth pass.
        if (UI.DeviceProfile.Current != UI.DeviceProfile.Tier.Low)
        {
            env.SsaoEnabled = true;
            env.SsaoRadius = 1.4f;
            env.SsaoIntensity = 1.6f;
            // Occlusion should darken *ambient* only. At 1.0 it eats the sun's contribution
            // too and objects look sooty on their lit side.
            env.SsaoLightAffect = 0.15f;
        }

        // Real-time global illumination (SDFGI) is deliberately NOT enabled here.
        //
        // It is the obvious way to get light to bounce, and on paper it is what these worlds
        // want: they are user-uploaded GLBs that nobody is going to light-bake. But it could
        // not be shown to improve any world available to test, it is by far the most expensive
        // effect on the list, and it needs thick, closed geometry to avoid leaking — which a
        // hollow GLB room built from single-sided boxes is not. Turning it on blind, on every
        // world a user uploads, is a worse default than leaving it off.
        //
        // If it comes back, it needs a per-world manifest opt-in, not a global switch.

        // Screen-space reflections: the specular half of the same problem. A floor that reflects
        // nothing reads as chalk no matter how well it is lit.
        if (UI.DeviceProfile.Current == UI.DeviceProfile.Tier.High)
        {
            env.SsrEnabled = true;
            env.SsrMaxSteps = 32;
            env.SsrFadeIn = 0.15f;
            env.SsrFadeOut = 2.0f;
        }

        // Glow. The mode matters: a cinema screen and neon want it; a baked interior does not,
        // because its highlights are painted into the texture and would smear.
        if (UI.DeviceProfile.BloomEnabled && mode != "baked" && !env.GlowEnabled)
        {
            env.GlowEnabled = true;
            env.GlowIntensity = mode == "dark" ? 0.7f : 0.45f;
            env.GlowStrength = 1.1f;
            // `GlowBloom` is a *threshold-independent* lift: it blooms the entire image by that
            // fraction, dark pixels included. The threshold below is the knob that says "only
            // bright things glow"; adding bloom on top quietly undoes it, and a dark room with a
            // few very hot emissive strips is where that shows worst — the Cinema's aisle lights
            // and cove strips smeared a haze over every seat in the house. Keep it at zero for
            // dark interiors and barely on elsewhere.
            env.GlowBloom = mode == "dark" ? 0.0f : 0.05f;
            // Only genuinely bright things bloom. Below ~1.0 the whole image hazes over.
            env.GlowHdrThreshold = 1.0f;
            env.GlowHdrScale = 2.0f;
            env.GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Additive;
            // Wide, soft falloff around a light source rather than a tight halo.
            env.SetGlowLevel(3, 1.0f);
            env.SetGlowLevel(4, 1.0f);
            env.SetGlowLevel(5, 0.6f);
        }

        // Filmic tonemapping with the default white point of 1.0 clips everything brighter than
        // white, so lamps and sky flatten into featureless patches. Headroom lets the
        // highlights roll off instead. Only raised, never lowered — an author who set their own
        // white point meant it.
        if (env.TonemapMode == Godot.Environment.ToneMapper.Linear)
            env.TonemapMode = Godot.Environment.ToneMapper.Filmic;
        if (env.TonemapWhite < 4.0f) env.TonemapWhite = 4.0f;
    }

    /// Sun/key light for worlds that ship none.
    ///
    /// A `.serikaworld` is a GLB, and most GLB exports carry no lights at all — so these worlds
    /// were lit purely by the environment's flat ambient. That was survivable while avatars
    /// imported as `KHR_materials_unlit` and ignored lighting entirely, but the toon shader made
    /// them respond to it: an avatar in an unlit world now gets a uniform ambient wash with no
    /// band, no shadow side and no form. The world itself has the same problem — nothing casts a
    /// shadow, so nothing looks like it is standing on the floor.
    ///
    /// Only added when the world truly has none of its own; an authored sun always wins.
    private static void EnsureKeyLight(Node3D root, string mode)
    {
        // A baked world's shadows are already painted into its textures. A theatre on
        // desktop is supposed to be dark. On Quest the Compatibility renderer cannot
        // light a whole auditorium with nine omnis, so a dim directional fill is what
        // actually lets you see the seats.
        if (mode == "baked") return;
        if (mode == "dark" && !UI.DeviceProfile.IsStandaloneXr) return;

        var lights = new System.Collections.Generic.List<Light3D>();
        CollectLights(root, lights);
        foreach (var l in lights)
        {
            if (l is not DirectionalLight3D) continue;
            // A directional that exists but contributes nothing still blocks the fill.
            if (l.Visible && l.LightEnergy >= 0.25f) return;
        }

        var (energy, colour, pitch) = mode switch
        {
            // Even, low-contrast fill from high up — a gallery wants form, not drama.
            "studio" => (0.9f, new Color(1.0f, 0.98f, 1.0f), -62f),
            // Warm, low, lamp-like to match the interior's own fixtures.
            "lit" => (0.7f, new Color(1.0f, 0.92f, 0.80f), -38f),
            // Quest cinema: enough to read the room, not enough to kill the sconces.
            "dark" => (1.1f, new Color(1.0f, 0.94f, 0.88f), -50f),
            // Outdoor: a proper sun, slightly warm, at a mid-afternoon angle.
            _ => (1.15f, new Color(1.0f, 0.96f, 0.90f), -45f),
        };

        var sun = new DirectionalLight3D
        {
            Name = "SerikaKeyLight",
            LightEnergy = energy,
            LightColor = colour,
            ShadowEnabled = UI.DeviceProfile.Shadows,
            // Softens the shadow edge with distance, so a character's contact shadow stays
            // tight while distant geometry doesn't shimmer.
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits,
            LightAngularDistance = 1.0f,
        };
        root.AddChild(sun);
        // Yaw is offset from the spawn's facing so the key is three-quarter, not head-on: a
        // light down the view axis flattens the toon ramp into a single band.
        sun.RotationDegrees = new Vector3(pitch, 35f, 0f);

        GD.Print($"WorldLoader: added key light for '{mode}' " +
                 $"(energy {energy}, shadows {sun.ShadowEnabled})");
    }

    /// Quest / Android cinema: Compatibility only shades a mesh with a handful of omnis, and the
    /// software occluder can hide a closed room's whole interior — including the emissive
    /// sconces, which is how "0 light" survived an earlier ambient bump. Both of those are
    /// handled (`CapPositionalLightsForMobile`, `DisableLiveOcclusionCulling`). This pass is now
    /// only the small remainder: kill backface culling on single-sided room geometry, and hang
    /// one fill light so the far rows are not lit solely by the six nearest sconces.
    ///
    /// **Never brighten a room with EMISSION or AMBIENT.** Both are direction-independent: they
    /// add the same value to a surface no matter which way it faces, so each one dilutes the
    /// only terms that carry shape. The previous version stacked four flat terms — ambient 1.35,
    /// emission = albedo × 0.55 on *every* surface, and a 2.8-energy fill at attenuation 0.4
    /// (which across a 16 × 24 m room is not a falloff at all) — against a single directional
    /// key. Around 80% of the final pixel value was constant, which is the numeric definition of
    /// the "too bright and not shaded" the Cinema shipped as.
    ///
    /// **The dark albedos that pass was written to compensate for do not exist.** glTF's
    /// `baseColorFactor` is LINEAR and Godot's `AlbedoColor` is sRGB-ENCODED, so the Cinema wall
    /// authored at 0.13 arrives here as 0.396 — a perfectly ordinary mid-grey. Comparing a
    /// runtime `AlbedoColor` against a threshold read off the glTF file is an apples-to-oranges
    /// test, and it is the one that justified self-lighting the entire room. Measured, not
    /// assumed: `--serika-worldtest --world <bundle> --standalone` dumps every albedo.
    ///
    /// So the room is lit the ordinary way — ambient just off black, the world's own eight
    /// surviving omnis, one fill, and the directional key from `EnsureKeyLight` doing the
    /// shading.
    private static void ApplyStandaloneDarkVisibility(Node root, Node instance)
    {
        WorldLod.DisableLiveOcclusionCulling();

        foreach (var node in root.FindChildren("*", "WorldEnvironment", true, false))
        {
            if (node is not WorldEnvironment we || we.Environment == null) continue;
            ForceStandaloneDarkEnv(we.Environment);
        }

        if (instance != null)
        {
            int surfaces = 0;
            foreach (var node in instance.FindChildren("*", "MeshInstance3D", true, false))
            {
                if (node is not MeshInstance3D mi || mi.Mesh == null) continue;
                // Don't touch the picture — HouseLights and VideoScreen own that surface.
                if (mi.Name.ToString().Contains("VideoScreen", StringComparison.Ordinal)) continue;
                for (int i = 0; i < mi.Mesh.GetSurfaceCount(); i++)
                {
                    if (mi.GetActiveMaterial(i) is not BaseMaterial3D src) continue;
                    var dup = (BaseMaterial3D)src.Duplicate();
                    // A GLB room is a shell of single-sided boxes; from inside it, every wall is
                    // a backface. Desktop gets away with more of the room's own lights, so this
                    // shows up first on standalone.
                    dup.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
                    if (dup.ShadingMode == BaseMaterial3D.ShadingModeEnum.Unshaded)
                        dup.ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel;
                    dup.Metallic = 0f;
                    // Emission is deliberately untouched. The authored emissive fittings (step
                    // lights, sconce glow, exit signs) keep the values their author set, which is
                    // what lets `HouseLights.CaptureEmissive` treat those seven surfaces — and
                    // only those — as the fittings to dim when a film starts. The old
                    // self-lighting pass made all 33 surfaces emissive and silently hijacked it.
                    mi.SetSurfaceOverrideMaterial(i, dup);
                    surfaces++;
                }
            }

            AddStandaloneRoomFill(instance as Node3D);
            GD.Print($"WorldLoader: standalone dark — {surfaces} surface(s) double-sided");
        }

        GD.Print($"WorldLoader: standalone dark visibility — ambient {StandaloneDarkAmbient:0.00}, " +
                 "occluder off, no self-lighting");
    }

    /// Flat ambient fill for a `dark` world on standalone. Keeps unlit sides off pure black and
    /// nothing more — every 0.1 added here is contrast subtracted from the whole room.
    private const float StandaloneDarkAmbient = 0.30f;

    private static void ForceStandaloneDarkEnv(Godot.Environment env)
    {
        env.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        env.AmbientLightColor = new Color(0.62f, 0.58f, 0.68f);
        // Ambient is a flat term — see ApplyStandaloneDarkVisibility. It exists to keep the
        // unlit side of things off pure black, not to light the room; the key light and the
        // fill omni do that. At the old 1.35 it *was* the room and nothing had a shaded side.
        env.AmbientLightEnergy = Mathf.Max(env.AmbientLightEnergy, StandaloneDarkAmbient);
        env.BackgroundMode = Godot.Environment.BGMode.Color;
        env.BackgroundColor = new Color(0.10f, 0.08f, 0.14f);
        env.SsaoEnabled = false;
        env.SsrEnabled = false;
        env.VolumetricFogEnabled = false;
    }

    /// One large unshadowed omni high in the room so Compatibility has a light that actually
    /// reaches the seats, not just the six nearest sconces.
    ///
    /// The attenuation matters more than the energy. At the old 0.4 the falloff term is nearly
    /// constant across the whole span of a room this size, which makes this a second ambient
    /// wearing a light's clothing — brightness everywhere, gradient nowhere. A roughly
    /// inverse-square 1.8 gives the far rows visibly less than the front, which is what makes
    /// the space read as having depth, and it is hung above head height so surfaces are lit
    /// from above rather than head-on.
    private static void AddStandaloneRoomFill(Node3D instance)
    {
        if (instance == null) return;
        var aabb = WorldAabb(instance);
        if (!aabb.HasValue) return;
        var a = aabb.Value;
        float range = Mathf.Max(8f, a.Size.Length() * 0.7f);
        var fill = new OmniLight3D
        {
            Name = "SerikaStandaloneFill",
            LightEnergy = 1.1f,
            LightColor = new Color(1.0f, 0.94f, 0.88f),
            OmniRange = range,
            OmniAttenuation = 1.8f,
            ShadowEnabled = false,
        };
        instance.AddChild(fill);
        fill.GlobalPosition = a.GetCenter() + new Vector3(0f, a.Size.Y * 0.12f, 0f);
        GD.Print($"WorldLoader: standalone room fill at {fill.GlobalPosition} range {range:0.0}");
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
