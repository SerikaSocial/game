using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SerikaSocial.World;

/// Mirror verification inside a REAL world bundle, as opposed to the synthetic studio room that
/// `MirrorDiagnostic` builds.
///
///   Godot --path game -- --serika-mirrorworld --world &lt;bundle.serikaworld&gt; [--ska x.ska]
///                        [--out /tmp/mw] [--index 0]
///
/// The studio harness proves the optics; this proves the things only a shipped world can show:
///
///   • that `WorldLoader`'s `SERIKA_MIRROR*` marker pass produces mirrors that actually reflect,
///     at the size and orientation the author baked into the GLB;
///   • that the RENDER BUDGET holds. The Mirror Gallery hangs eight mirrors in one room, all
///     within `MirrorRange`, and every live one is a second full render of the scene. Uncapped
///     that is eight extra renders a frame, which is exactly the sort of thing that is invisible
///     in a screenshot and fatal on a Quest.
///
/// Needs a real display (`DISPLAY=:1`); headless renders nothing.
public static partial class MirrorWorldDiagnostic
{
    private const int WarmupFrames = 24;
    private const int SettleFrames = 10;

    public static void Run(Node host, Node3D worldRoot, string worldPath, string skaPath,
                           string outPrefix, string indexArg)
    {
        outPrefix ??= "/tmp/mirrorworld";

        if (DisplayServer.GetName() == "headless")
        {
            GD.Print("MIRRORWORLD FAIL: headless cannot render. Use a real display or xvfb-run.");
            host.GetTree().Quit(1);
            return;
        }
        if (string.IsNullOrWhiteSpace(worldPath) || !System.IO.File.Exists(worldPath))
        {
            GD.Print($"MIRRORWORLD FAIL: --world <bundle> required (got '{worldPath}')");
            host.GetTree().Quit(2);
            return;
        }

        // Same reason WorldDiagnostic does this: the login backdrop's WorldEnvironment would
        // otherwise still be parented here and the bundle's own lighting would be skipped.
        foreach (var child in worldRoot.GetChildren())
        {
            worldRoot.RemoveChild(child);
            child.QueueFree();
        }

        GD.Print($"MIRRORWORLD loading {worldPath}");
        var spawn = WorldLoader.LoadFromPath(worldPath, "mirrorworld", worldRoot) ?? Vector3.Zero;

        var mirrors = Descendants(worldRoot).OfType<Mirror>().ToList();
        GD.Print($"MIRRORWORLD tier={UI.DeviceProfile.Current} budget={UI.DeviceProfile.MirrorBudget} " +
                 $"range={UI.DeviceProfile.MirrorRange} mirrors={mirrors.Count} spawn={spawn}");
        if (mirrors.Count == 0)
        {
            GD.Print("MIRRORWORLD FAIL: the bundle resolved no mirrors");
            host.GetTree().Quit(1);
            return;
        }

        // Pick the mirror a PLAYER would actually walk up to: nearest to the world's spawn among
        // those whose reflective face is turned toward it. Plain index-0 is not good enough —
        // the Mirror Gallery's first mirror faces out of the building, so targeting it put the
        // camera outside the level photographing the back of a wall.
        int index;
        if (int.TryParse(indexArg, out int explicitIndex))
        {
            index = Mathf.Clamp(explicitIndex, 0, mirrors.Count - 1);
        }
        else
        {
            index = 0;
            float best = float.NegativeInfinity;
            for (int m = 0; m < mirrors.Count; m++)
            {
                var ms = SurfaceOf(mirrors[m]);
                if (ms == null) continue;
                Vector3 c = ms.GlobalPosition;
                Vector3 n = ms.GlobalBasis.Z.Normalized();
                Vector3 toSpawn = spawn - c;
                float d = toSpawn.Length();
                if (d < 0.01f) continue;
                float facing = n.Dot(toSpawn / d);
                if (facing < 0.2f) continue;          // turned away from where players arrive
                float score = facing - d * 0.05f;     // prefer facing, then near
                if (score > best) { best = score; index = m; }
            }
        }
        var target = mirrors[index];

        // The reflective quad is the mirror's last child (Mirror._Ready adds the viewport first,
        // then the surface); its transform carries the authored size and orientation.
        var surf = SurfaceOf(target);
        if (surf == null)
        {
            GD.Print("MIRRORWORLD FAIL: mirror has no surface mesh");
            host.GetTree().Quit(1);
            return;
        }

        var quad = (surf.Mesh as QuadMesh)?.Size ?? Vector2.Zero;
        Vector3 centre = surf.GlobalPosition;
        Vector3 normal = surf.GlobalBasis.Z.Normalized();
        GD.Print($"MIRRORWORLD target[{index}] centre={centre} normal={normal} " +
                 $"quad={quad.X:0.00}x{quad.Y:0.00} m");

        // Stand an avatar where a player would stand: in front of the glass, facing it.
        var avatar = SerikaSocial.Avatar.AvatarLibrary.InstantiateOrDefault(skaPath);
        worldRoot.AddChild(avatar);
        // Off to one side of the glass centre, so she does not stand between the camera and the
        // very reflection this is meant to photograph.
        var side = normal.Cross(Vector3.Up).Normalized();
        Vector3 avPos = centre + normal * 1.3f + side * (quad.X * 0.26f);
        // Feet on the FLOOR, taken from the world's own spawn height. Using the glass's bottom
        // edge instead left her hanging in mid-air in Serika Home, whose mirror is mounted 1.65 m
        // up the wall — a floating avatar is not a photograph of anything a player would see.
        avPos.Y = spawn.Y;
        avatar.GlobalPosition = avPos;
        // Face the GLASS. For yaw θ a Godot rig's forward is (−sin θ, 0, −cos θ), so facing
        // −normal (into the mirror) is exactly atan2(n.x, n.z) — no negation, no extra π. Both
        // of those were tried and both spun her round to face the camera, which put her BACK in
        // the reflection: the one thing a mirror photograph must not show.
        avatar.GlobalRotation = new Vector3(0, Mathf.Atan2(normal.X, normal.Z), 0);

        var cam = new Camera3D { Name = "MirrorWorldCam", Fov = 70, Current = true };
        worldRoot.AddChild(cam);

        host.AddChild(new Driver(host, worldRoot, cam, avatar, mirrors, centre, normal, quad,
                                 spawn.Y, outPrefix));
    }

    private sealed partial class Driver : Node
    {
        private readonly Node _host;
        private readonly Node3D _worldRoot;
        private readonly Camera3D _cam;
        private readonly SerikaSocial.Avatar.AvatarInstance _avatar;
        private readonly List<Mirror> _mirrors;
        private readonly Vector3 _centre, _normal;
        private readonly Vector2 _quad;
        private readonly float _floorY;
        private readonly string _out;

        // Viewpoints around the target glass: head-on, two obliques, and one further back.
        private readonly (string Name, float Dist, float YawDeg, float Height)[] _shots =
        {
            ("head_on",  3.6f,   0f, 1.60f),
            ("oblique_l", 3.8f, -42f, 1.60f),
            ("oblique_r", 3.8f,  42f, 1.60f),
            ("far_back",  6.0f,   0f, 1.75f),
            ("low",       3.4f,  18f, 1.05f),
        };

        private int _frames, _shotIndex, _shotFrames;
        private bool _ok = true;
        private int _peakLive;

        public Driver(Node host, Node3D worldRoot, Camera3D cam,
                      SerikaSocial.Avatar.AvatarInstance avatar, List<Mirror> mirrors,
                      Vector3 centre, Vector3 normal, Vector2 quad, float floorY, string outPrefix)
        {
            _host = host; _worldRoot = worldRoot; _cam = cam; _avatar = avatar;
            _mirrors = mirrors; _centre = centre; _normal = normal; _quad = quad;
            _floorY = floorY; _out = outPrefix;
        }

        public override void _Process(double delta)
        {
            _frames++;
            _avatar.Animate(1.0 / 60.0, 0f, true);
            if (_frames < WarmupFrames) return;

            if (_shotIndex >= _shots.Length) { Finish(); return; }

            var shot = _shots[_shotIndex];
            if (_shotFrames == 0)
            {
                // Orbit about the glass centre, in its own plane's frame.
                var up = Vector3.Up;
                var right = _normal.Cross(up).Normalized();
                float rad = Mathf.DegToRad(shot.YawDeg);
                Vector3 dir = (_normal * Mathf.Cos(rad) + right * Mathf.Sin(rad)).Normalized();
                Vector3 eye = _centre + dir * shot.Dist;
                eye.Y = _floorY + shot.Height;
                _cam.GlobalPosition = eye;
                _cam.LookAt(_centre, Vector3.Up);
                _shotFrames++;
                return;
            }

            _shotFrames++;
            if (_shotFrames < SettleFrames) return;

            Capture(shot.Name);
            _shotFrames = 0;
            _shotIndex++;
        }

        private void Capture(string name)
        {
            int live = _mirrors.Count(m => m.IsLive);
            _peakLive = Mathf.Max(_peakLive, live);
            int budget = UI.DeviceProfile.MirrorBudget;

            // The budget is the whole point of the check: a room full of mirrors must never have
            // more than `budget` of them doing a full scene render in one frame.
            bool budgetOk = live <= budget;
            _ok &= budgetOk;

            var img = _worldRoot.GetViewport().GetTexture()?.GetImage();
            string path = $"{_out}_{name}.png";
            if (img != null)
            {
                if (img.GetFormat() != Image.Format.Rgba8) img.Convert(Image.Format.Rgba8);
                img.SavePng(path);
            }

            GD.Print($"MIRRORWORLD [{name}] live={live}/{_mirrors.Count} (budget {budget}) " +
                     $"{(budgetOk ? "ok" : "FAIL — over budget")} → {path}");
        }

        private void Finish()
        {
            GD.Print($"MIRRORWORLD peak live = {_peakLive}, budget = {UI.DeviceProfile.MirrorBudget}");
            GD.Print(_ok ? "MIRRORWORLD PASS" : "MIRRORWORLD FAIL");
            _host.GetTree().Quit(_ok ? 0 : 1);
        }
    }

    /// A mirror's reflective quad: `Mirror._Ready` adds the SubViewport first and the surface
    /// last, and the surface's transform carries the authored size and orientation.
    private static MeshInstance3D SurfaceOf(Mirror m)
        => m.GetChildCount() > 0 ? m.GetChild(m.GetChildCount() - 1) as MeshInstance3D : null;

    private static IEnumerable<Node> Descendants(Node n)
    {
        foreach (Node c in n.GetChildren())
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }
}
