using System.Collections.Generic;
using System.Linq;
using Godot;
using SerikaSocial.World.Video;

namespace SerikaSocial.World;

/// Loads a `.serikaworld` and reports what the marker pass actually produced — optionally
/// rendering it and playing a clip, so video can be verified instead of guessed at.
///
///   # numbers only (headless is fine)
///   Godot --headless -- --serika-worldtest --world <bundle>
///
///   # render a frame from the spawn point, with a clip playing on the screen
///   Godot -- --serika-worldtest --world <bundle> --ogv <file.ogv> --shot /tmp/out.png
///
/// Marker bugs are invisible from the authoring side: a screen whose height collapsed still
/// exports, imports and plays — it is just the wrong shape on the wall. And a screen that
/// renders pure white looks identical whether the decoder is dead or the texture never bound.
/// Both need measuring, not eyeballing. **Headless cannot verify video** — the dummy rasterizer
/// produces no textures — so `--shot` must run against a real display (`DISPLAY=:1`, no
/// `--headless`).
public static partial class WorldDiagnostic
{
    public static void Run(Node host, Node3D worldRoot, string path,
                           string ogv = null, string shot = null, string wait = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            GD.PrintErr("WORLDTEST: --world <path to .serikaworld|.glb> is required");
            host.GetTree().Quit(2);
            return;
        }
        if (!System.IO.File.Exists(path))
        {
            GD.PrintErr($"WORLDTEST: no such file: {path}");
            host.GetTree().Quit(2);
            return;
        }

        // Detach *now*, not at end of frame. `QueueFree` alone left the login backdrop — and
        // crucially its WorldEnvironment — parented under the same root the bundle loads into,
        // so `SetupEnvironment` found an "authored" environment and skipped the manifest's
        // lighting mode entirely. The diagnostic then measured a room lit by the login screen,
        // which is not what the game does: `SwapWorld` builds into a *fresh* root.
        foreach (var child in worldRoot.GetChildren())
        {
            worldRoot.RemoveChild(child);
            child.QueueFree();
        }

        GD.Print($"WORLDTEST: loading {path}");
        var spawn = WorldLoader.LoadFromPath(path, "worldtest", worldRoot);
        GD.Print($"WORLDTEST: spawn = {Fmt(spawn ?? Vector3.Zero)}");

        var screenMeshes = Descendants(worldRoot).OfType<MeshInstance3D>()
                                                 .Where(m => m.Name.ToString() == "VideoScreen")
                                                 .ToList();
        GD.Print($"WORLDTEST: video screens = {screenMeshes.Count}");

        foreach (var mesh in screenMeshes)
        {
            var size = (mesh.Mesh as QuadMesh)?.Size ?? Vector2.Zero;
            // The mesh resource size is NOT what the player sees: the node inherits its
            // parent's scale, so a squashed ancestor renders a correct quad as a sliver.
            var gscale = mesh.GlobalTransform.Basis.Scale;
            float wsW = size.X * gscale.X, wsH = size.Y * gscale.Y;
            float aspect = wsH > 0.001f ? wsW / wsH : 0f;
            GD.Print($"WORLDTEST:   mesh size   = {size.X:0.00} x {size.Y:0.00} m");
            GD.Print($"WORLDTEST:   node scale  = {Fmt(gscale)}");
            GD.Print($"WORLDTEST:   WORLD size  = {wsW:0.00} x {wsH:0.00} m (aspect {aspect:0.00}) " +
                     $"at {Fmt(mesh.GlobalPosition)} yaw {Mathf.RadToDeg(mesh.GlobalRotation.Y):0}°");
            if (wsH < 2.0f)
                GD.PrintErr("WORLDTEST:   ^ SUSPECT — world-space height under 2 m. Either the " +
                            "marker lost its height axis (see marker_size in build_worlds.py) " +
                            "or an ancestor node is squashing it.");
        }

        var seats = worldRoot.GetTree().GetNodesInGroup(Interactable.Group)
                             .OfType<SeatNode>().ToList();
        GD.Print($"WORLDTEST: seats = {seats.Count}");
        if (seats.Count > 0)
            GD.Print($"WORLDTEST:   seat height range = {seats.Min(s => s.GlobalPosition.Y):0.00}" +
                     $" .. {seats.Max(s => s.GlobalPosition.Y):0.00} m");
        GD.Print($"WORLDTEST: mesh instances = {Descendants(worldRoot).OfType<MeshInstance3D>().Count()}");

        // Props are the only marker whose correctness is invisible in a screenshot: a crate at
        // the wrong scale still renders as a crate, and a duplicate net id still renders as two
        // crates. Both are only findable as numbers.
        var props = Descendants(worldRoot).OfType<PhysicsProp>().ToList();
        GD.Print($"WORLDTEST: physics props = {props.Count}");
        var seenIds = new HashSet<ushort>();
        foreach (var p in props)
        {
            var box = p.FindChildren("*", "CollisionShape3D", true, false)
                       .OfType<CollisionShape3D>().FirstOrDefault()?.Shape as BoxShape3D;
            string dup = seenIds.Add(p.NetId) ? "" : "  ← DUPLICATE NetId";
            GD.Print($"WORLDTEST:   {p.Name} netId {p.NetId}, networked {p.Networked}, " +
                     $"size {(box != null ? Fmt(box.Size) : "none")} at {Fmt(p.GlobalPosition)}{dup}");
        }

        // Lights, post-normalisation. Energy alone says nothing about how bright a room ends
        // up: an omni's *range* is what decides whether it lights its corner or the whole
        // building, and glTF carries no range, so a light that looks tame by energy can still
        // wash out every surface in the world. Print both, plus the environment's ambient.
        ReportLights(worldRoot, "at rest");
        foreach (var we in Descendants(worldRoot).OfType<WorldEnvironment>())
        {
            var e = we.Environment;
            if (e == null) continue;
            GD.Print($"WORLDTEST: env ambient {e.AmbientLightEnergy:0.00} {e.AmbientLightColor}, " +
                     $"glow {e.GlowEnabled} (intensity {e.GlowIntensity:0.00}, " +
                     $"threshold {e.GlowHdrThreshold:0.00}), tonemap {e.TonemapMode} " +
                     $"white {e.TonemapWhite:0.0}, exposure {e.TonemapExposure:0.00}");
        }

        if (string.IsNullOrEmpty(shot))
        {
            GD.Print("WORLDTEST: done");
            host.GetTree().Quit(0);
            return;
        }

        // Render mode: frame the screen, optionally start a clip, and capture after the
        // decoder has had time to produce frames.
        var target = screenMeshes.FirstOrDefault();
        var cam = new Camera3D { Name = "DiagCam", Fov = 70 };
        worldRoot.AddChild(cam);
        if (target != null)
        {
            // Stand in front of the screen along the direction it actually faces. A QuadMesh
            // points down its local +Z, so the basis Z column is the facing vector — using a
            // fixed world offset instead put the camera behind the screen, outside the room.
            var facing = target.GlobalTransform.Basis.Z.Normalized();
            cam.GlobalPosition = target.GlobalPosition + facing * 11.0f + new Vector3(0, -1.0f, 0);
            cam.LookAt(target.GlobalPosition, Vector3.Up);
        }
        else
        {
            // No screen to frame. Standing at the spawn facing default-forward photographs
            // whichever wall happens to be there, which is how the Items Lab's first shot came
            // back as a flat grey rectangle. Back off and look at the geometry's centre.
            // Stay *at* the spawn — most of these worlds are enclosed rooms, so backing off to
            // frame them just puts the camera outside photographing the roof. Only the aim
            // changes: look at the geometry's centre rather than default-forward, which is how
            // the Items Lab's first shot came back as a flat grey wall.
            var eye = (spawn ?? Vector3.Zero) + new Vector3(0, 1.6f, 0);
            var centre = Centre(worldRoot);
            cam.GlobalPosition = eye;
            if (eye.DistanceSquaredTo(centre) > 0.01f) cam.LookAt(centre, Vector3.Up);
        }
        cam.Current = true;

        if (!string.IsNullOrEmpty(ogv))
        {
            var screens = host.GetTree().GetNodesInGroup(VideoScreen.Group)
                              .OfType<VideoScreen>().ToList();
            GD.Print($"WORLDTEST: playing {ogv} on {screens.Count} screen(s)");
            foreach (var s in screens) s.Play("diag://local", ogv, "ogv", "theora");
        }

        double waitSeconds = 2.5;
        if (!string.IsNullOrEmpty(wait) && double.TryParse(wait, out var w)) waitSeconds = w;
        GD.Print($"WORLDTEST: capturing after {waitSeconds:0.0}s");
        host.AddChild(new ShotTaker(shot, screenMeshes.FirstOrDefault(), worldRoot, waitSeconds));
    }

    /// Waits for the decoder to produce frames, reports whether the screen texture is real,
    /// then writes a PNG of the viewport.
    private sealed partial class ShotTaker : Node
    {
        private readonly string _path;
        private readonly MeshInstance3D _screen;
        private readonly Node _worldRoot;
        private readonly double _wait;
        private double _elapsed;

        public ShotTaker(string path, MeshInstance3D screen, Node worldRoot, double wait)
        {
            _path = path;
            _screen = screen;
            _worldRoot = worldRoot;
            _wait = wait;
        }

        public override void _Process(double delta)
        {
            // Wall-clock, not a frame count. The old version waited 150 frames "≈2.5 s at 60
            // fps", which is exactly the assumption that does not hold in the software
            // rasteriser this diagnostic usually runs under — and the house-light fade it now
            // has to outlast is measured in seconds, not frames.
            _elapsed += delta;
            if (_elapsed < _wait) return;

            // Report what actually ended up on the screen material. A pure-white screen is the
            // signature of the emissive material being shown with no video texture bound.
            if (_screen?.MaterialOverride is StandardMaterial3D m)
            {
                var tex = m.AlbedoTexture;
                GD.Print($"WORLDTEST: screen albedo texture = " +
                         (tex == null ? "NULL (screen will render flat emissive white)"
                                      : $"{tex.GetWidth()}x{tex.GetHeight()} {tex.GetType().Name}"));
                GD.Print($"WORLDTEST: screen emission = {m.Emission}, " +
                         $"energy = {m.EmissionEnergyMultiplier}, op = {m.EmissionOperator}");
                // Dump the decoded frame on its own. This separates "the decoder produced a
                // blank/white image" from "the frame is fine but the material blows it out".
                if (tex != null)
                {
                    var frame = tex.GetImage();
                    if (frame != null)
                    {
                        string side = _path.Replace(".png", "_frame.png");
                        frame.SavePng(side);
                        var c = frame.GetPixel(frame.GetWidth() / 2, frame.GetHeight() / 2);
                        GD.Print($"WORLDTEST: decoded frame {frame.GetWidth()}x{frame.GetHeight()} " +
                                 $"centre pixel = ({c.R:0.00}, {c.G:0.00}, {c.B:0.00}) → {side}");
                    }
                }
            }

            ReportLights(_worldRoot, "at capture");

            var img = GetViewport().GetTexture().GetImage();
            img.SavePng(_path);
            GD.Print($"WORLDTEST: wrote {_path}");
            GD.Print("WORLDTEST: done");
            GetTree().Quit(0);
        }
    }

    /// Dump every light's energy and reach. Called once at load and again at capture time, so a
    /// run with `--ogv` shows the house lights before and after the film starts — the whole point
    /// of `HouseLights` is a change in these numbers over time, which one snapshot cannot show.
    private static void ReportLights(Node worldRoot, string when)
    {
        foreach (var l in Descendants(worldRoot).OfType<Light3D>())
        {
            string extent = l switch
            {
                OmniLight3D o => $"range {o.OmniRange:0.0} m, atten {o.OmniAttenuation:0.00}",
                SpotLight3D s => $"range {s.SpotRange:0.0} m, angle {s.SpotAngle:0}°",
                _ => "directional",
            };
            GD.Print($"WORLDTEST: light [{when}] {l.Name} energy {l.LightEnergy:0.00}, {extent}, " +
                     $"visible {l.Visible}, shadows {l.ShadowEnabled}");
        }
    }

    /// Centre of every visible mesh in the world, for framing a shot.
    private static Vector3 Centre(Node root)
    {
        Aabb? total = null;
        foreach (var mi in Descendants(root).OfType<MeshInstance3D>())
        {
            if (mi.Mesh == null) continue;
            var world = mi.GlobalTransform * mi.GetAabb();
            total = total.HasValue ? total.Value.Merge(world) : world;
        }
        return total?.GetCenter() ?? Vector3.Zero;
    }

    private static IEnumerable<Node> Descendants(Node n)
    {
        foreach (var c in n.GetChildren())
        {
            yield return c;
            foreach (var g in Descendants(c)) yield return g;
        }
    }

    private static string Fmt(Vector3 v) => $"({v.X:0.00}, {v.Y:0.00}, {v.Z:0.00})";
}
