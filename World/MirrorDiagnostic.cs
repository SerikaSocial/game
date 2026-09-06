using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SerikaSocial.World;

/// End-to-end planar-mirror verification: builds a small studio with TWO mirrors of different
/// sizes/orientations, scatters six uniquely-coloured emissive markers through the room, stands
/// avatars in front, and sweeps an orbiting camera across the front hemisphere. For every pose
/// it computes where each marker's reflection MUST appear — the light path eye → bounce point
/// → marker has exactly one solution (intersect the eye↔mirrored-marker line with the plane,
/// the billiards construction) — unprojects that point to a pixel, and checks the captured PNG
/// really shows the right colour there. Wrong colours mean flipped L/R, wrong parallax, bad
/// zoom/scale, or near-plane leaks; missing means the reflection isn't rendered at all.
///
/// Alongside the pixel checks it probes the live internals numerically each pose: the
/// reflection camera must sit at the exact mirrored eye point, map the physical glass corners
/// to its texture corners, and exclude backing geometry across the whole glass.
///
///   Godot --path game -- --serika-mirrortest [--ska path.ska] [--out /tmp/mirror]
///
/// Needs a display or xvfb-run (headless cannot render). Writes one PNG per pose prefixed
/// `ok_`/`fail_` so failures are findable at a glance, prints a per-defect summary, exits 0/1.
public static partial class MirrorDiagnostic
{
    private const int Width = 960, Height = 720;
    private const int WarmupFrames = 30;   // let DeviceProfile/subsystems settle before pose 1
    private const int SettleFrames = 9;    // frames between arriving at a pose and capturing

    /// Marker palette: hues chosen to stay separable after filmic tonemap + sRGB output.
    private static readonly (string Name, Color Col)[] Palette =
    {
        ("red",     new Color(1.00f, 0.07f, 0.07f)),
        ("green",   new Color(0.07f, 1.00f, 0.07f)),
        ("blue",    new Color(0.12f, 0.18f, 1.00f)),
        ("yellow",  new Color(1.00f, 0.93f, 0.10f)),
        ("cyan",    new Color(0.10f, 0.88f, 0.97f)),
        ("magenta", new Color(1.00f, 0.10f, 0.92f)),
    };

    /// Room layout constants — mirrored in the analytic plane descriptions below.
    // Main mirror wall behind +Z; side wall on −X carrying the second, differently-proportioned
    // mirror so scale and non-axis-aligned orientation get exercised, not just one flat case.
    private const float MainWallZ = 3.4f;
    private const float SideWallX = -4.6f;
    private const float BackWallZ = -3.6f;

    private sealed class PlaneInfo
    {
        public Mirror Node;          // the live Mirror under test
        public Vector3 PointOnPlane; // centre of the reflective quad (world)
        public Vector3 Normal;       // unit outward normal (toward viewers)
        public Vector2 QuadSize;     // world-space width × height
        public Vector3 TangentU;     // unit horizontal axis across the quad
        public Vector3 SlabMin;      // host wall box (world AABB) — occludes reflections the
        public Vector3 SlabMax;      //   same way it occludes them in the rendered mirror
        public string Label;

        public Vector3 QuadCentre => PointOnPlane;
    }

    private sealed class RunState
    {
        public Node3D Root;
        public List<PlaneInfo> Planes = new();
        public Dictionary<string, Vector3> Markers = new();  // palette name → world pos
        public List<(double phiDeg, double r, double h)> Poses = new();
        public Camera3D Cam;
        public string OutPrefix;
        public int Frames, PoseIndex, PoseFrames;
        public List<SerikaSocial.Avatar.AvatarInstance> Avatars = new();
        /// Avatar world AABBs, refreshed each pose (the idle animation moves them).
        public readonly List<(Vector3 min, Vector3 max)> AvatarBounds = new();

        // defect tallies over the whole sweep
        public readonly Dictionary<string, (int good, int bad)> PerMarker = new();
        public readonly List<string> Defects = new();
    }

    // Independent geometric contract: project the actual glass corners through the live
    // camera, and probe both sides of its clipping plane. This does not assume a particular
    // FOV or frustum-offset formula, so it detects both mapping errors and backing-wall leaks.
    internal static IEnumerable<string> WindowDefects(Mirror mirror, Camera3D camera, SubViewport viewport)
    {
        var surface = Grab<MeshInstance3D>(mirror, "_surface");
        var size = ((QuadMesh)surface.Mesh).Size;
        var normal = surface.GlobalBasis.Z.Normalized();
        if ((-camera.GlobalBasis.Z).Dot(normal) < .9999f)
            yield return "reflection near plane is not parallel to the glass";
        Vector4 rect = mirror.TextureWindowForDiagnostic(camera);
        Vector2 rectMin = new(rect.X, rect.Y);
        Vector2 rectSize = new(rect.Z - rect.X, rect.W - rect.Y);
        // With a close-up texture window the physical glass corners deliberately sit outside
        // the reflection frustum. Probe the cropped rectangle's interior corners and centre:
        // those are the texels the player can actually see, and they retain enough inset that
        // the lateral frustum boundary cannot be mistaken for near-plane clipping.
        for (int i = 0; i < 5; i++)
        {
            float tx = i == 4 ? .5f : (i & 1) == 0 ? .15f : .85f;
            float ty = i == 4 ? .5f : (i & 2) == 0 ? .15f : .85f;
            Vector2 expectedUv = new(tx, ty);
            Vector2 fullUv = rectMin + rectSize * expectedUv;
            float x = .5f - fullUv.X;
            float y = .5f - fullUv.Y;
            Vector3 point = surface.GlobalTransform * new Vector3(size.X * x, size.Y * y, 0);
            Vector2 uv = camera.UnprojectPosition(point) / (Vector2)viewport.Size;
            if (uv.DistanceTo(expectedUv) > .002f)
                yield return $"glass sample {i}: reflected UV {uv} does not match its corner";
            if (camera.IsPositionInFrustum(point - normal * .05f))
                yield return $"glass sample {i}: backing wall leaks through near plane";
            if (!camera.IsPositionInFrustum(point + normal * .03f))
                yield return $"glass sample {i}: valid room geometry clipped away";
        }
    }

    public static void Run(Node host, string skaPath, string outPrefix)
    {
        // `--serika-mirrortest --stereo` runs the VR/stereo suite instead. It is dispatched from
        // here rather than from Main.cs's flag table so the stereo work needs no edit to Main.
        foreach (var a in OS.GetCmdlineArgs().Concat(OS.GetCmdlineUserArgs()))
            if (a == "--stereo" || a == "stereo")
            {
                MirrorStereoDiagnostic.Run(host, skaPath, outPrefix);
                return;
            }
        try { RunInner(host, skaPath, outPrefix); }
        catch (Exception e)
        {
            GD.Print($"MIRRORTEST FAIL exception: {e}");
            host.GetTree().Quit(1);
        }
    }

    private static void RunInner(Node host, string skaPath, string outPrefix)
    {
        outPrefix ??= "/tmp/mirrortest";
        GD.Print($"MIRRORTEST out={outPrefix}");

        if (DisplayServer.GetName() == "headless")
        {
            GD.Print("MIRRORTEST FAIL: headless cannot render. Use xvfb-run.");
            host.GetTree().Quit(1);
            return;
        }

        var st = new RunState { OutPrefix = outPrefix };
        GD.Print($"MIRRORTEST tier={UI.DeviceProfile.Current} mirrorRange={UI.DeviceProfile.MirrorRange}");

        var root = st.Root = new Node3D { Name = "MirrorRoom" };
        host.AddChild(root);

        BuildRoom(st);

        // Avatars in front of the main mirror: the requested .ska if resolvable, else beans.
        // Avatars in front of the main mirror. The real .ska faces the glass (yaw 180°, since a
        // Godot rig's forward is −Z and the mirror is at +Z), because "what do I look like" is
        // the thing mirrors are actually for — and a reflected FACE is a far better read on
        // whether the optics are right than a reflected back.
        // Both stand to the +X side of the room. Centred, they sat squarely on the sightlines
        // from the virtual eye to the lower marker row and the occlusion skip quietly dropped
        // those checks — 'yellow' went a whole sweep without ever being verified. Off to one
        // side they still stand in front of the glass and still appear in every reflection,
        // but the six probe columns stay visible.
        var av1 = SerikaSocial.Avatar.AvatarLibrary.InstantiateOrDefault(skaPath);
        root.AddChild(av1);
        av1.GlobalPosition = new Vector3(0.55f, 0f, 0.85f);
        av1.GlobalRotation = new Vector3(0, Mathf.Pi, 0);
        var av2 = SerikaSocial.Avatar.AvatarLibrary.InstantiateBean();
        root.AddChild(av2);
        av2.GlobalPosition = new Vector3(1.90f, 0f, 2.35f);
        av2.GlobalRotation = new Vector3(0, Mathf.Pi, 0);
        st.Avatars.Add(av1);
        st.Avatars.Add(av2);
        GD.Print($"MIRRORTEST avatars: {(skaPath ?? "(bean)")} + bean, both facing the glass");

        // Orbit poses: azimuth around the plane normal (0° = dead-centre), horizontal radius,
        // absolute eye height. Full ring at reading height first, then height/radius extremes,
        // ending outside the active range to see what LOD leaves on the glass.
        double[] phis = { 0, 18, -18, 38, -38, 56, -56, 70 };
        foreach (var phi in phis) st.Poses.Add((phi, 2.6, 1.6));
        foreach (var phi in phis) st.Poses.Add((phi, 1.3, 1.6));
        double[] heights = { 0.55, 1.05, 2.35 };
        foreach (var h in heights) st.Poses.Add((0, 2.6, h));
        st.Poses.Add((-50, 4.2, 1.9));
        st.Poses.Add((50, 4.2, 1.9));
        st.Poses.Add((0, 5.2, 1.6));
        st.Poses.Add((24, 4.6, 1.0));
        st.Poses.Add((-24, 4.6, 1.0));
        // A mirror fills the camera here. It used to stretch a 15%-wide centre patch of the
        // full-glass texture across the display, so the usual 1.3 m closest pose could not
        // expose the resolution regression reported from the hub.
        st.Poses.Add((0, .30, 1.1));

        st.Cam = new Camera3D
        {
            Name = "OrbitCam",
            Fov = 68,
            Current = true,
            Position = new Vector3(0, 1.6f, -2),
        };
        st.Root.AddChild(st.Cam);

        host.AddChild(new Driver(host, st));
    }

    /// Floor, the two mirror-bearing walls (kept 6 cm BEHIND their glass so the analytic planes
    /// and quads sit clean), the markers, and the mirrors themselves created through the same
    /// `Mirror.Create` entry point WorldLoader's marker pass uses.
    private static void BuildRoom(RunState st)
    {
        var floor = new MeshInstance3D
        {
            Name = "Floor",
            Mesh = new PlaneMesh { Size = new Vector2(22, 22), Orientation = PlaneMesh.OrientationEnum.Y },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.42f, 0.40f, 0.47f),
                Roughness = 0.85f,
            },
        };
        st.Root.AddChild(floor);

        var floorBody = new StaticBody3D();
        floorBody.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(22, 0.1f, 22) },
            Position = new Vector3(0, -0.05f, 0),
        });
        st.Root.AddChild(floorBody);

        AddWallPanel(st.Root, "MainWall", new Vector3(0, 2f, MainWallZ + 0.06f), 0f,  new Vector2(11, 4));
        AddWallPanel(st.Root, "SideWall", new Vector3(SideWallX - 0.06f, 2f, 0f), -90f, new Vector2(11, 4));
        // Back wall behind the viewer. It closes the room, and it is the surface the main mirror
        // actually images — a mirror shows what is BEHIND whoever looks into it, so this is where
        // the probe markers have to live if they are to land on the glass from most viewpoints.
        AddWallPanel(st.Root, "BackWall", new Vector3(0, 2f, BackWallZ - 0.06f), 180f, new Vector2(11, 4));

        // Environment kept simple: ambient + one soft key light, glow OFF so emissive marker
        // hues arrive uncontaminated at the sensor.
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.03f, 0.03f, 0.045f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.52f, 0.50f, 0.58f),
            AmbientLightEnergy = 0.62f,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
        };
        st.Root.AddChild(new WorldEnvironment { Environment = env });
        var sun = new DirectionalLight3D { Name = "Key", LightEnergy = 1.05f, ShadowEnabled = true };
        st.Root.AddChild(sun);
        sun.RotationDegrees = new Vector3(-48, 210, 0); // shining toward the main mirror wall

        // Markers — pure-hue emissive spheres, placed clear of the two avatars' silhouettes so
        // an avatar standing between the virtual eye and a marker never eats a check (the
        // host-wall occlusion that legitimately eats some oblique reflections IS modelled).
        int i = 0;
        foreach (var (name, col) in Palette)
        {
            // A 3×2 grid standing just off the back wall — the region the main mirror images.
            // The old layout scattered these around the room in front of the glass, where the
            // 1.4 × 2.2 m quad only ever caught one or two of them: the whole 24-pose sweep
            // verified eight samples total, which is far too thin to call the optics proven.
            // Columns are wide enough (±2.4 m) to clear both avatars' silhouettes.
            Vector3 p = name switch
            {
                "red"     => new Vector3(-2.40f, 1.95f, BackWallZ + 0.35f),
                "green"   => new Vector3( 0.00f, 1.95f, BackWallZ + 0.35f),
                "blue"    => new Vector3( 2.40f, 1.95f, BackWallZ + 0.35f),
                // The outer columns cannot go wider than about ±2.4 m: from the dead-centre
                // pose the reflection of a point at (x, ~-3.25) crosses the glass at 0.28x, so
                // anything past ±2.49 m images outside the 1.4 m quad and can never be checked.
                "yellow"  => new Vector3(-2.40f, 0.75f, BackWallZ + 0.35f),
                "cyan"    => new Vector3( 0.00f, 0.55f, BackWallZ + 0.35f),
                _         => new Vector3( 2.40f, 0.75f, BackWallZ + 0.35f), // magenta
            };
            st.Markers[name] = p;
            var ball = new MeshInstance3D
            {
                Name = $"MK_{name}",
                Mesh = new SphereMesh { Radius = 0.09f, Height = 0.18f },
                Position = p,
            };
            var mat = new StandardMaterial3D
            {
                AlbedoColor = col,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            };
            ball.MaterialOverride = mat;
            st.Root.AddChild(ball);
            i++;
        }

        // The mirrors themselves — production nodes, not reimplementations.
        var main = Mirror.Create(new Vector3(0, 0, MainWallZ), 180f, 1.4f, 2.2f);
        st.Root.AddChild(main);
        st.Planes.Add(new PlaneInfo
        {
            Node = main,
            PointOnPlane = new Vector3(0, 1.1f, MainWallZ),
            Normal = new Vector3(0, 0, -1),
            QuadSize = new Vector2(1.4f, 2.2f),
            TangentU = new Vector3(-1, 0, 0), // yaw 180 flips local X
            SlabMin = new Vector3(-5.5f, 0f, MainWallZ),
            SlabMax = new Vector3(5.5f, 4f, MainWallZ + 0.12f),
            Label = "main",
        });
        // yaw +90 → quad normal +X (into the room). A −90 here faces the glass at the wall
        // and the mirror correctly refuses to reflect for a viewer behind it.
        var side = Mirror.Create(new Vector3(SideWallX, 0, 0.8f), 90f, 2.3f, 0.95f);
        st.Root.AddChild(side);
        st.Planes.Add(new PlaneInfo
        {
            Node = side,
            PointOnPlane = new Vector3(SideWallX, 0.475f, 0.8f),
            Normal = new Vector3(1, 0, 0),
            QuadSize = new Vector2(2.3f, 0.95f),
            TangentU = new Vector3(0, 0, -1),
            SlabMin = new Vector3(SideWallX - 0.12f, 0f, -5.5f),
            SlabMax = new Vector3(SideWallX, 4f, 5.5f),
            Label = "side",
        });

        GD.Print("MIRRORTEST room built");

        // Snap the analytic planes onto the actual glass: Mirror.cs offsets the quad 1 cm off
        // its host wall to avoid coplanar z-fighting, and the optics must be evaluated at the
        // glass itself, not at the wall behind it.
        foreach (var pl in st.Planes)
        {
            if (pl.Node.GetChildCount() > 0 && pl.Node.GetChild(pl.Node.GetChildCount() - 1) is Node3D surf)
                pl.PointOnPlane = surf.GlobalPosition;
        }
    }

    private static void AddWallPanel(Node3D parent, string name, Vector3 pos, float yawDeg, Vector2 size)
    {
        var wall = new MeshInstance3D
        {
            Name = name,
            Mesh = new BoxMesh { Size = new Vector3(size.X, size.Y, 0.12f) },
            Position = pos,
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.60f, 0.58f, 0.66f),
                Roughness = 0.95f,
            },
        };
        wall.RotationDegrees = new Vector3(0, yawDeg, 0);
        parent.AddChild(wall);
    }

    /// Reflection of a point about the plane. All optical expectations derive from this map.
    private static Vector3 Reflect(Vector3 p, PlaneInfo pl)
    {
        Vector3 d = p - pl.PointOnPlane;
        return p - 2f * pl.Normal.Dot(d) * pl.Normal;
    }

    /// Reflection of a DIRECTION about the plane's axis (no translation term).
    private static Vector3 ReflectDir(Vector3 d, PlaneInfo pl)
    {
        return d - 2f * pl.Normal.Dot(d) * pl.Normal;
    }

    /// Where the eye sees a marker's reflection: intersect plane Π with the eye→R(marker) line.
    private static Vector3? BouncePoint(Vector3 eye, Vector3 markerPos, PlaneInfo pl)
    {
        Vector3 dir = Reflect(markerPos, pl) - eye;
        float denom = pl.Normal.Dot(dir);
        if (Mathf.Abs(denom) < 1e-5f) return null;
        float t = pl.Normal.Dot(pl.PointOnPlane - eye) / denom;
        if (t <= 0.02f || t >= 0.999f) return null; // plane must lie strictly between eye and image
        return eye + dir * t;
    }

    /// True when a world point lies inside the reflective quad (with `margin` metres slack).
    private static bool InsideQuad(PlaneInfo pl, Vector3 p, float margin)
    {
        Vector3 rel = p - pl.PointOnPlane;
        float u = pl.TangentU.Dot(rel);
        float v = rel.Y; // both walls are upright
        return MathF.Abs(u) <= pl.QuadSize.X * 0.5f + margin &&
               v >= -pl.QuadSize.Y * 0.5f - margin && v <= pl.QuadSize.Y * 0.5f + margin;
    }

    private static Vector3 EyeAt(double phiDeg, double r, double h)
    {
        double rad = Mathf.DegToRad(phiDeg);
        // Ring centred on the main mirror, normal −Z into the room: 0° = straight ahead.
        float x = (float)(Math.Sin(rad) * r);
        float z = MainWallZ - (float)(Math.Cos(rad) * r);
        return new Vector3(x, (float)h, z);
    }

    /// True when the host wall blocks the mirrored eye's line to a marker — the same
    /// occlusion the rendered reflection exhibits. The near clip only removes wall fragments
    /// nearer than `near` ALONG THE MIRRORED GAZE, so a slab crossing must additionally be
    /// deeper than that to count as a real blocker; shallower crossings fall inside the
    /// clipped-away hole and do not occlude.
    private static bool HostBlocked(Vector3 ePrime, Vector3 marker, PlaneInfo pl, Vector3 mirroredGaze, float near)
        => BoxBlocked(ePrime, marker, pl.SlabMin, pl.SlabMax, mirroredGaze, near);

    /// True when any avatar standing in the room blocks the mirrored eye's line to a marker.
    ///
    /// A reflected avatar occludes exactly like a real one, so an avatar between the virtual eye
    /// and a marker is the reflection being CORRECT, not a defect — and with two rigs standing in
    /// front of a 1.4 m quad it happens constantly. Bounds come from the rigs' own meshes each
    /// pose rather than a guessed capsule, because the idle animation moves them.
    private static bool AvatarBlocked(RunState st, Vector3 ePrime, Vector3 marker, Vector3 mirroredGaze, float near)
    {
        foreach (var (min, max) in st.AvatarBounds)
            if (BoxBlocked(ePrime, marker, min, max, mirroredGaze, near)) return true;
        return false;
    }

    /// World-space AABB of every visible mesh under `node`, or null when it has none.
    private static (Vector3 min, Vector3 max)? WorldBounds(Node node)
    {
        Vector3 lo = Vector3.Inf, hi = -Vector3.Inf;
        bool any = false;
        var stack = new Stack<Node>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            foreach (Node c in n.GetChildren()) stack.Push(c);
            if (n is not VisualInstance3D vi || !vi.IsVisibleInTree()) continue;
            var aabb = vi.GlobalTransform * vi.GetAabb();
            for (int corner = 0; corner < 8; corner++)
            {
                var p = aabb.GetEndpoint(corner);
                lo = new Vector3(Mathf.Min(lo.X, p.X), Mathf.Min(lo.Y, p.Y), Mathf.Min(lo.Z, p.Z));
                hi = new Vector3(Mathf.Max(hi.X, p.X), Mathf.Max(hi.Y, p.Y), Mathf.Max(hi.Z, p.Z));
            }
            any = true;
        }
        return any ? (lo, hi) : null;
    }

    /// Ray-vs-AABB along the segment virtual-eye → marker, with the same near-clip rule the
    /// rendered reflection obeys: an occluder nearer than the clip plane along the mirrored gaze
    /// has been clipped away and cannot block anything.
    private static bool BoxBlocked(Vector3 ePrime, Vector3 marker, Vector3 boxMin, Vector3 boxMax,
                                   Vector3 mirroredGaze, float near)
    {
        Vector3 d = marker - ePrime;
        float tEnter = float.NegativeInfinity, tExit = float.PositiveInfinity;
        for (int a = 0; a < 3; a++)
        {
            float da = a == 0 ? d.X : a == 1 ? d.Y : d.Z;
            float oa = a == 0 ? ePrime.X : a == 1 ? ePrime.Y : ePrime.Z;
            float mn = a == 0 ? boxMin.X : a == 1 ? boxMin.Y : boxMin.Z;
            float mx = a == 0 ? boxMax.X : a == 1 ? boxMax.Y : boxMax.Z;
            if (Mathf.Abs(da) < 1e-6f)
            {
                if (oa < mn || oa > mx) return false; // parallel and outside the slab
                continue;
            }
            float t0 = (mn - oa) / da, t1 = (mx - oa) / da;
            if (t0 > t1) (t0, t1) = (t1, t0);
            tEnter = Mathf.Max(tEnter, t0);
            tExit = Mathf.Min(tExit, t1);
        }
        if (tEnter >= tExit || tExit <= 0.02f || tEnter >= 0.98f) return false;

        float tt = Mathf.Clamp(tEnter, 0.02f, 0.98f);
        Vector3 p = ePrime + d * tt;
        float depth = (p - ePrime).Dot(mirroredGaze);
        return depth > near + 0.005f;
    }

    /// Classify an RGB pixel into a palette name. Uses channel-ratio structure rather than
    /// absolute values because filmic tonemap shifts brightness; ratios survive exposure.
    private static string Classify(Color c)
    {
        float sum = c.R + c.G + c.B;
        if (sum < 0.11f) return null;                       // near-black: not a lit marker
        float r = c.R / sum, g = c.G / sum, b = c.B / sum;

        bool Red()     => r > 0.52f && g < 0.34f && b < 0.34f;
        bool Green()   => g > 0.52f && r < 0.34f && b < 0.34f;
        bool Blue()    => b > 0.52f && r < 0.36f && g < 0.44f;
        bool Yellow()  => r > 0.42f && g > 0.42f && b < 0.26f;
        bool Cyan()    => g > 0.40f && b > 0.40f && r < 0.26f;
        bool Magenta() => r > 0.42f && b > 0.40f && g < 0.26f;

        if (Red()) return "red";
        if (Green()) return "green";
        if (Blue()) return "blue";
        if (Yellow()) return "yellow";
        if (Cyan()) return "cyan";
        if (Magenta()) return "magenta";
        return null;
    }

    private sealed partial class Driver : Node
    {
        private readonly Node _host;
        private readonly RunState _st;
        private bool _traced;

        public Driver(Node host, RunState st) { _host = host; _st = st; }

        public override void _Process(double delta)
        {
            _st.Frames++;
            // Hold a natural idle. Left un-animated the rigs sit in their bind T-pose, which is
            // both an unrealistic thing to photograph a mirror with and nearly two metres wide —
            // arms spread across the reflection and ate the marker checks behind them.
            // Screenshot encoding stalls must not feed multi-second deltas into avatar IK.
            foreach (var av in _st.Avatars) av.Animate(1.0 / 60.0, 0f, true);
            if (_st.Frames < WarmupFrames) return;

            if (_st.PoseIndex >= _st.Poses.Count)
            {
                Finish();
                return;
            }

            if (_st.PoseFrames == 0)
            {
                var (phi, r, h) = _st.Poses[_st.PoseIndex];
                var eye = EyeAt(phi, r, h);
                _st.Cam.GlobalPosition = eye;
                _st.Cam.LookAt(new Vector3(0, Mathf.Clamp(eye.Y, 0.6f, 2.1f), MainWallZ), Vector3.Up);
                _st.PoseFrames++;
                return;
            }

            _st.PoseFrames++;
            if (_st.PoseFrames < SettleFrames) return;

            Capture(_st.Poses[_st.PoseIndex]);
            _st.PoseIndex++;
            _st.PoseFrames = 0;
        }

        private void Capture((double phiDeg, double r, double h) pose)
        {
            var img = _st.Root.GetViewport().GetTexture()?.GetImage();
            if (img == null)
            {
                GD.Print("MIRRORTEST FAIL: viewport produced no image");
                _st.Defects.Add("viewport-no-image");
                return;
            }
            if (img.GetFormat() != Image.Format.Rgba8) img.Convert(Image.Format.Rgba8);

            var (phi, r, h) = pose;
            string tag = $"phi{phi:+0;-0;0}_r{r:0.0}_h{h:0.0}";
            var eye = _st.Cam.GlobalPosition;

            // Refresh the avatar occluders for THIS frame's pose — the idle animation moves the
            // rigs, so a bound captured once at startup would drift out of register.
            _st.AvatarBounds.Clear();
            foreach (var av in _st.Avatars)
                if (WorldBounds(av) is { } b2) _st.AvatarBounds.Add(b2);

            // One-shot deep trace of the first pose: prints the whole optical chain for the
            // yellow marker (bounce point, both projections) so mapping mismatches are
            // measurable instead of eyeballed.
            if (!_traced)
            {
                _traced = true;
                var ym = _st.Markers["yellow"];
                var mpl = _st.Planes[0];
                var bounce0 = BouncePoint(eye, ym, mpl);
                GD.Print($"MIRRORTEST TRACE eye={eye} fov={_st.Cam.Fov} vpRect={_st.Root.GetViewport().GetVisibleRect().Size}");
                GD.Print($"MIRRORTEST TRACE yellow={ym} mirrored={Reflect(ym, mpl)} bounce={bounce0}");
                if (bounce0 != null)
                {
                    GD.Print($"MIRRORTEST TRACE unproj(bounce)={_st.Cam.UnprojectPosition(bounce0.Value)} " +
                             $"unproj(markerDirect)={_st.Cam.UnprojectPosition(ym)} " +
                             $"inFrustum(bounce)={_st.Cam.IsPositionInFrustum(bounce0.Value)}");
                }
            }

            var problems = new List<string>();

            // UnprojectPosition answers in the viewport's visible-rect space; the captured PNG
            // is the framebuffer, which can differ (content scaling). Scale once here so every
            // screen-space comparison below runs in PNG pixels.
            var vpRect = _st.Root.GetViewport().GetVisibleRect().Size;
            float scaleX = img.GetWidth() / Mathf.Max(1f, vpRect.X);
            float scaleY = img.GetHeight() / Mathf.Max(1f, vpRect.Y);
            Vector2 ToImg(Vector2 v) => new(v.X * scaleX, v.Y * scaleY);

            // ── Numeric probes on the live internals ──────────────────────────────────
            foreach (var pl in _st.Planes)
            {
                var mirCam = Grab<Camera3D>(pl.Node, "_mirrorCam");
                var vp = Grab<SubViewport>(pl.Node, "_viewport");
                bool showingLive = GrabVal<bool>(pl.Node, "_showingLive");
                // Mirror the production gate exactly (range + hysteresis + front-of-glass):
                // a mirror that correctly went to its far-LOD material keeps a stale camera,
                // which is by design and must not be flagged.
                Vector3 n = pl.Normal;
                float range = Math.Min(12f, UI.DeviceProfile.MirrorRange);
                float signed = n.Dot(eye - pl.PointOnPlane);
                float dist = eye.DistanceTo(pl.PointOnPlane); // == the surface node's position
                bool shouldBeLive = signed > 0.03f &&
                                    dist < (showingLive ? range : range - 0.5f);
                if (!shouldBeLive || mirCam == null || vp == null) continue;

                Vector3 wantEye = Reflect(eye, pl);
                float eyeErr = mirCam.GlobalPosition.DistanceTo(wantEye);
                if (eyeErr > 0.002f)
                    problems.Add($"{pl.Label}: reflection-cam off mirrored-eye by {eyeErr * 1000:0.0} mm");

                problems.AddRange(WindowDefects(pl.Node, mirCam, vp));

                if (r <= .55 && ReferenceEquals(pl, _st.Planes[0]))
                {
                    Vector4 window = pl.Node.TextureWindowForDiagnostic(mirCam);
                    Vector2 span = new(window.Z - window.X, window.W - window.Y);
                    Vector2 mainSize = _st.Root.GetViewport().GetVisibleRect().Size;
                    float budgetPixels = mainSize.X * mainSize.Y *
                                         UI.DeviceProfile.MirrorResolutionScale * UI.DeviceProfile.MirrorResolutionScale;
                    float targetPixels = vp.Size.X * vp.Size.Y;
                    if (span.X >= .75f || span.Y >= .5f)
                        problems.Add($"{pl.Label}: close full-screen view did not crop its source window ({span})");
                    if (targetPixels > budgetPixels * 1.05f)
                        problems.Add($"{pl.Label}: close target uses {targetPixels:0} px over its {budgetPixels:0} px budget");
                    GD.Print($"MIRRORTEST close {pl.Label}: crop={span} target={vp.Size} budget={budgetPixels:0}");
                }
            }

            // ── Optical probes: does the glass show the physically correct image? ─────
            var imgW = img.GetWidth();
            var imgH = img.GetHeight();

            foreach (var pl in _st.Planes)
            {
                // Occlusion context for this mirror: the virtual eye, its gaze, and the near
                // plane the reflection camera clips with. Reflections whose light path the
                // host wall blocks are legitimately absent from the glass.
                var nrm = pl.Normal;
                var ePrime = Reflect(eye, pl);
                var gaze = nrm; // the clipping plane is parallel to the physical glass
                float nearPlane = Mathf.Abs(nrm.Dot(pl.PointOnPlane - ePrime)) + 0.01f;

                // Projected screen polygon of the quad (conservative bounding box).
                Vector2 bbMin = new(float.MaxValue, float.MaxValue), bbMax = new(float.MinValue, float.MinValue);
                bool anyCornerVisible = false;
                for (int ci = 0; ci < 4; ci++)
                {
                    var corner = pl.PointOnPlane +
                                 pl.TangentU * ((ci & 1) == 0 ? -pl.QuadSize.X / 2 : pl.QuadSize.X / 2) +
                                 Vector3.Up * ((ci & 2) == 0 ? -pl.QuadSize.Y / 2 : pl.QuadSize.Y / 2);
                    if (!_st.Cam.IsPositionInFrustum(corner)) continue;
                    anyCornerVisible = true;
                    var sp = ToImg(_st.Cam.UnprojectPosition(corner));
                    bbMin = bbMin.Min(sp);
                    bbMax = bbMax.Max(sp);
                }
                if (!anyCornerVisible) continue;
                bbMin = (bbMin - new Vector2(14, 14)).Max(Vector2.Zero);
                bbMax = (bbMax + new Vector2(14, 14)).Min(new Vector2(imgW - 1, imgH - 1));
                if (bbMax.X - bbMin.X < 24 || bbMax.Y - bbMin.Y < 24) continue;

                foreach (var (name, mpos) in _st.Markers)
                {
                    if (HostBlocked(ePrime, mpos, pl, gaze, nearPlane)) continue;
                    if (AvatarBlocked(_st, ePrime, mpos, gaze, nearPlane)) continue;

                    var bounce = BouncePoint(eye, mpos, pl);
                    if (bounce == null) continue;
                    if (!InsideQuad(pl, bounce.Value, 0f)) continue;

                    var px = ToImg(_st.Cam.UnprojectPosition(bounce.Value));
                    if (px.X < bbMin.X || px.X > bbMax.X || px.Y < bbMin.Y || px.Y > bbMax.Y) continue;

                    // Search a ±10 px neighbourhood for the expected hue rather than demanding
                    // it dead-centre: an avatar occluding part of a reflection shifts the
                    // visible remnant a few pixels, which is not an optics defect. A real
                    // mapping error (wrong flip / offset / scale) displaces content by half
                    // the quad, so the tolerance cleanly separates the two.
                    bool hit = false;
                    for (int dy = -10; dy <= 10 && !hit; dy += 2)
                    for (int dx = -10; dx <= 10 && !hit; dx += 2)
                    {
                        int ix = Mathf.Clamp((int)px.X + dx, 0, imgW - 1);
                        int iy = Mathf.Clamp((int)px.Y + dy, 0, imgH - 1);
                        hit = Classify(img.GetPixel(ix, iy)) == name;
                    }

                    (int good, int bad) tally = _st.PerMarker.GetValueOrDefault(name, (0, 0));
                    if (hit)
                        tally.good++;
                    else
                    {
                        tally.bad++;
                        problems.Add($"{pl.Label}: '{name}' should appear at ({px.X:0},{px.Y:0}) but " +
                                     "nothing of that hue is there (absent, occluded, or wrong content)");
                    }
                    _st.PerMarker[name] = tally;
                }
            }

            string prefix = problems.Count == 0 ? "ok" : "fail";
            string path = $"{_st.OutPrefix}_{prefix}_{tag}.png";
            img.SavePng(path);

            // Dump each mirror's SubViewport render next to the composite shot: comparing the
            // two splits "mirrored camera drew the wrong thing" from "the glass sampled the
            // wrong texels" — which eyeballing the composite alone cannot do.
            for (int pi = 0; pi < _st.Planes.Count; pi++)
            {
                var vp = Grab<SubViewport>(_st.Planes[pi].Node, "_viewport");
                var mc = Grab<Camera3D>(_st.Planes[pi].Node, "_mirrorCam");
                if (vp == null || mc == null) continue;
                var timg = vp.GetTexture()?.GetImage();
                timg?.SavePng($"{_st.OutPrefix}_tex{pi}_{tag}.png");
                if (pi == 0)
                {
                    GD.Print($"MIRRORTEST [{tag}] {_st.Planes[pi].Label} cam pos={mc.GlobalPosition} " +
                             $"fwd={mc.GlobalBasis.Z} right={mc.GlobalBasis.X} vpSize={vp.Size}");
                    if (timg != null)
                    {
                        var probe = timg;
                        if (probe.GetFormat() != Image.Format.Rgba8)
                        {
                            probe = timg.Duplicate() as Image;
                            probe.Convert(Image.Format.Rgba8);
                        }
                        int tw = probe.GetWidth(), th = probe.GetHeight();
                        GD.Print($"MIRRORTEST [{tag}] tex format={timg.GetFormat()} " +
                                 $"alpha bg(0.08w,0.15h)={probe.GetPixel((int)(tw * 0.08f), (int)(th * 0.15f)).A:0.00} " +
                                 $"right-half(0.75w,0.45h)={probe.GetPixel((int)(tw * 0.75f), (int)(th * 0.45f)).A:0.00} " +
                                 $"floor(0.33w,0.75h)={probe.GetPixel((int)(tw * 0.33f), (int)(th * 0.75f)).A:0.00}");
                    }
                }
            }

            if (problems.Count == 0)
                GD.Print($"MIRRORTEST pass [{tag}] → {System.IO.Path.GetFileName(path)}");
            else
                foreach (var p in problems.Take(4))
                    GD.PrintErr($"MIRRORTEST FAIL [{tag}] {p}");
            if (problems.Count > 4)
                GD.PrintErr($"MIRRORTEST FAIL [{tag}] … and {problems.Count - 4} more");
            _st.Defects.AddRange(problems);
        }

        private void Finish()
        {
            GD.Print("── MIRRORTEST summary ──────────────────────────────");
            foreach (var (name, (good, bad)) in _st.PerMarker.OrderBy(kv => kv.Key))
                GD.Print($"MIRRORTEST marker {name,-8}: {good} verified, {bad} wrong");
            foreach (var (name, (good, bad)) in _st.PerMarker)
                if (bad > 0 && good == 0)
                    GD.PrintErr($"MIRRORTEST NOTE: '{name}' never matched anywhere — suspect " +
                                $"marker placement/occlusion before assuming an optics bug");

            bool ok = _st.Defects.Count == 0 && _st.PerMarker.Count >= 4 &&
                      _st.PerMarker.Values.All(t => t.bad == 0);
            GD.Print(ok
                ? $"MIRRORTEST PASS: {_st.Poses.Count} poses, all reflections optically correct."
                : $"MIRRORTEST FAIL: {_st.Defects.Count} defective samples across {_st.Poses.Count} poses.");
            GetTree().Quit(ok ? 0 : 1);
        }
    }

    private static T Grab<T>(GodotObject obj, string field) where T : class
    {
        var fi = obj.GetType().GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return fi?.GetValue(obj) as T;
    }

    private static T GrabVal<T>(GodotObject obj, string field)
    {
        var fi = obj.GetType().GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return fi != null && fi.GetValue(obj) is T v ? v : default;
    }
}
