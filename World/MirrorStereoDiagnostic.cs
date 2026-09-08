using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SerikaSocial.World;

/// Verification for the STEREO (VR) mirror path — `Godot --path game -- --serika-mirrortest
/// --stereo [--ska path] [--out /tmp/ms]`. Needs a display; headless renders nothing.
///
/// A. Decode symmetric and asymmetric frustum matrices to verify projection inspection.
/// B. Render the real mirror from symmetric and asymmetric viewer projections against a
///    flush backing wall, checking independently calculated reflected marker positions.
/// C. Install a simulated two-eye XRInterface and verify separate reflected eye positions,
///    window-corner mapping, backing exclusion and preservation of geometry in front of glass.
/// Actual headset multiview texture selection, comfort and device cost still need hardware.
public static partial class MirrorStereoDiagnostic
{
    private const int WarmupFrames = 30;
    private const int SettleFrames = 9;

    private const float MirrorZ = 3.4f;
    private const float BackWallZ = -3.6f;

    private static readonly (string Name, Color Col)[] Palette =
    {
        ("red",     new Color(1.00f, 0.07f, 0.07f)),
        ("green",   new Color(0.07f, 1.00f, 0.07f)),
        ("blue",    new Color(0.12f, 0.18f, 1.00f)),
        ("yellow",  new Color(1.00f, 0.93f, 0.10f)),
        ("cyan",    new Color(0.10f, 0.88f, 0.97f)),
        ("magenta", new Color(1.00f, 0.10f, 0.92f)),
    };

    // ── A simulated stereo headset ───────────────────────────────────────────────────────
    // Half-angle tangents per view, taken to be deliberately ASYMMETRIC and slightly different
    // between the eyes, the way real HMD optics are: a symmetric pair would pass even with the
    // offset handling deleted, which is the bug this whole file exists to catch.
    private static readonly float[][] EyeTan =
    {
        //          left,   right,    down,     up
        new[] { -1.196f,  0.966f, -1.279f,  1.072f }, // view 0 (left eye)
        new[] { -0.966f,  1.196f, -1.279f,  1.072f }, // view 1 (right eye)
    };
    private const float Ipd = 0.063f;

    private sealed partial class SimStereoXr : XRInterfaceExtension
    {
        private bool _init;
        /// Head pose in play space — kept in sync with the `VrSimDevice` head tracker so the
        /// XRCamera3D and the per-view transforms agree, exactly as a real runtime's do.
        public Transform3D Head = new(Basis.Identity, new Vector3(0, 1.65f, 0));
        public Vector2 RenderTarget = new(1024, 1120);

        public static Transform3D EyeLocal(uint view) =>
            new(Basis.Identity, new Vector3(view == 0 ? -Ipd * 0.5f : Ipd * 0.5f, 0, 0));

        public override StringName _GetName() => "SerikaSimStereo";
        public override uint _GetCapabilities() =>
            (uint)(XRInterface.Capabilities.Stereo | XRInterface.Capabilities.Vr);
        public override bool _IsInitialized() => _init;
        public override bool _Initialize() { _init = true; return true; }
        public override void _Uninitialize() { _init = false; }
        public override XRInterface.TrackingStatus _GetTrackingStatus() =>
            XRInterface.TrackingStatus.NormalTracking;
        public override uint _GetViewCount() => 2;
        public override Vector2 _GetRenderTargetSize() => RenderTarget;
        public override Transform3D _GetCameraTransform() => Head;
        public override Transform3D _GetTransformForView(uint view, Transform3D camXf) =>
            camXf * Head * EyeLocal(view);

        /// The extension API hands the projection back as 16 doubles, COLUMN-MAJOR — the same
        /// layout as `Projection`'s four Vector4 columns.
        public override double[] _GetProjectionForView(uint view, double aspect, double near, double far)
        {
            var p = ProjFor(view, (float)near, (float)far);
            return new double[]
            {
                p.X.X, p.X.Y, p.X.Z, p.X.W,
                p.Y.X, p.Y.Y, p.Y.Z, p.Y.W,
                p.Z.X, p.Z.Y, p.Z.Z, p.Z.W,
                p.W.X, p.W.Y, p.W.Z, p.W.W,
            };
        }

        public static Projection ProjFor(uint view, float near, float far)
        {
            var t = EyeTan[view];
            return Projection.CreateFrustum(t[0] * near, t[1] * near, t[2] * near, t[3] * near, near, far);
        }
    }

    // ── Entry ────────────────────────────────────────────────────────────────────────────

    private sealed class RunState
    {
        public Node3D Root;
        public Camera3D Cam;
        public Mirror Glass;
        public Vector3 PlanePoint;
        public readonly Vector3 Normal = new(0, 0, -1);
        public Vector2 QuadSize;
        public readonly Vector3 TangentU = new(-1, 0, 0); // yaw 180 flips local X
        public readonly Dictionary<string, Vector3> Markers = new();
        public readonly List<(double phiDeg, double r, double h, Vector2 off, string tag)> Poses = new();
        public string OutPrefix;
        public int Frames, PoseIndex, PoseFrames;
        public readonly Dictionary<string, (int good, int bad)> PerMarker = new();
        public readonly List<string> Defects = new();
        public SerikaSocial.Avatar.AvatarInstance Avatar;
    }

    public static void Run(Node host, string skaPath, string outPrefix)
    {
        try { RunInner(host, skaPath, outPrefix); }
        catch (Exception e)
        {
            GD.Print($"MIRRORSTEREO FAIL exception: {e}");
            host.GetTree().Quit(1);
        }
    }

    private static void RunInner(Node host, string skaPath, string outPrefix)
    {
        outPrefix ??= "/tmp/mirrorstereo";
        GD.Print($"MIRRORSTEREO out={outPrefix} tier={UI.DeviceProfile.Current}");

        if (DisplayServer.GetName() == "headless")
        {
            GD.Print("MIRRORSTEREO FAIL: headless cannot render. Use DISPLAY=:1 or xvfb-run.");
            host.GetTree().Quit(1);
            return;
        }

        var st = new RunState { OutPrefix = outPrefix };

        // ── Phase A: the projection decode, numerically ──────────────────────────────────
        int aBad = PhaseA(st);
        GD.Print(aBad == 0
            ? "MIRRORSTEREO PHASE-A pass: DecodeFrustum exact for symmetric and asymmetric frusta"
            : $"MIRRORSTEREO PHASE-A FAIL: {aBad} decode mismatches");

        // ── Phase B: rendered optics with an asymmetric viewer ───────────────────────────
        var root = st.Root = new Node3D { Name = "StereoMirrorRoom" };
        host.AddChild(root);
        BuildRoom(st);

        var av = SerikaSocial.Avatar.AvatarLibrary.InstantiateOrDefault(skaPath);
        root.AddChild(av);
        // Off to one side and well INSIDE the 2.6 m orbit ring. Standing it at (0.95, 1.1) put
        // it exactly on the ring at +22°, so that pose's camera sat inside the avatar's head and
        // every marker check at that azimuth failed on a studio layout mistake, not on optics.
        av.GlobalPosition = new Vector3(1.30f, 0f, 2.45f);
        av.GlobalRotation = new Vector3(0, Mathf.Pi, 0);
        st.Avatar = av;

        // Poses: each azimuth is shot three times — with a zero offset (the desktop case, a
        // control that must keep passing), and with the two eyes' horizontal frustum centres.
        // The eye offsets are the whole point: they are what the old code silently forced to 0.
        double[] phis = { 0, 22, -22, 44, -44 };
        foreach (var phi in phis)
        {
            st.Poses.Add((phi, 2.6, 1.6, Vector2.Zero, "mono"));
            st.Poses.Add((phi, 2.6, 1.6, EyeCentreAtNear(0), "eyeL"));
            st.Poses.Add((phi, 2.6, 1.6, EyeCentreAtNear(1), "eyeR"));
        }
        st.Poses.Add((0, 1.4, 1.15, EyeCentreAtNear(0), "eyeL"));
        st.Poses.Add((0, 1.4, 1.15, EyeCentreAtNear(1), "eyeR"));
        st.Poses.Add((30, 4.4, 1.9, EyeCentreAtNear(1), "eyeR"));
        st.Poses.Add((-30, 4.4, 1.9, EyeCentreAtNear(0), "eyeL"));

        st.Cam = new Camera3D { Name = "ProbeCam", Current = true };
        root.AddChild(st.Cam);

        host.AddChild(new Driver(host, st, aBad));
    }

    /// The viewer camera's frustum for a given eye, at the camera's own near plane. `SetFrustum`
    /// wants (height, centre-offset); the height is derived from the eye's vertical tangents so
    /// the probe camera's rect really is that eye's rect, scaled to the near plane it uses.
    private const float ProbeNear = 0.05f, ProbeFar = 400f;

    private static float EyeHeightAtNear(uint view) => (EyeTan[view][3] - EyeTan[view][2]) * ProbeNear;

    /// The horizontal/vertical frustum CENTRE the probe viewer is given, per "eye". Deliberately
    /// larger than a real HMD's (about a quarter of the half-width rather than a twentieth): the
    /// point is to make a dropped or un-negated offset displace the reflection by a quarter of
    /// the screen, far outside the ±10 px hue search, instead of by a few pixels.
    private static Vector2 EyeCentreAtNear(uint view) =>
        view == 0 ? new Vector2(-0.026f, 0.009f) : new Vector2(0.026f, 0.009f);

    // ── Phase A ──────────────────────────────────────────────────────────────────────────

    /// `DecodeFrustum` must recover (height, centre, aspect-ratio) from a projection matrix, and
    /// must do so **restated at a different near plane** — the reflection camera's near plane sits
    /// past the glass, metres away from whatever near the viewer's matrix was built with. Both
    /// are checked here, on symmetric and asymmetric rects.
    private static int PhaseA(RunState st)
    {
        int bad = 0;
        var cases = new List<(string name, float l, float r, float b, float t, float n)>
        {
            ("desktop-symmetric-16:9", -0.0889f, 0.0889f, -0.0500f, 0.0500f, 0.05f),
            ("desktop-symmetric-4:3",  -0.0667f, 0.0667f, -0.0500f, 0.0500f, 0.05f),
            ("hmd-left",  EyeTan[0][0] * 0.05f, EyeTan[0][1] * 0.05f, EyeTan[0][2] * 0.05f, EyeTan[0][3] * 0.05f, 0.05f),
            ("hmd-right", EyeTan[1][0] * 0.05f, EyeTan[1][1] * 0.05f, EyeTan[1][2] * 0.05f, EyeTan[1][3] * 0.05f, 0.05f),
            ("hmd-left-canted", -0.070f, 0.041f, -0.052f, 0.061f, 0.05f),
        };

        foreach (var (name, l, r, b, t, n) in cases)
        foreach (float restate in new[] { n, 0.9f, 3.55f })
        {
            var proj = Projection.CreateFrustum(l, r, b, t, n, 400f);
            Mirror.DecodeFrustum(proj, restate, out float h, out Vector2 c, out float ratio);
            float k = restate / n;
            float wantH = (t - b) * k, wantCx = (l + r) * 0.5f * k, wantCy = (t + b) * 0.5f * k;
            float wantRatio = (r - l) / (t - b);
            float eh = Mathf.Abs(h - wantH), ex = Mathf.Abs(c.X - wantCx), ey = Mathf.Abs(c.Y - wantCy);
            float er = Mathf.Abs(ratio - wantRatio);
            // Relative tolerances: these are float32 matrix round-trips, not exact rationals.
            bool ok = eh <= 1e-5f * Mathf.Max(1f, wantH) + 1e-6f &&
                      ex <= 1e-5f * Mathf.Max(1f, Mathf.Abs(wantCx)) + 1e-6f &&
                      ey <= 1e-5f * Mathf.Max(1f, Mathf.Abs(wantCy)) + 1e-6f &&
                      er <= 1e-5f;
            GD.Print($"MIRRORSTEREO decode {name,-24} near={restate:0.###} " +
                     $"h={h:0.000000}/{wantH:0.000000} cx={c.X:+0.000000;-0.000000} /{wantCx:+0.000000;-0.000000} " +
                     $"cy={c.Y:+0.000000;-0.000000} ratio={ratio:0.00000}/{wantRatio:0.00000} {(ok ? "ok" : "MISMATCH")}");
            if (!ok) { bad++; st.Defects.Add($"decode {name}@{restate}"); }
        }
        return bad;
    }

    // ── Room ─────────────────────────────────────────────────────────────────────────────

    private static void BuildRoom(RunState st)
    {
        st.Root.AddChild(new MeshInstance3D
        {
            Name = "Floor",
            Mesh = new PlaneMesh { Size = new Vector2(24, 24), Orientation = PlaneMesh.OrientationEnum.Y },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.40f, 0.39f, 0.46f),
                Roughness = 0.85f,
            },
        });
        var floorBody = new StaticBody3D();
        floorBody.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(24, 0.1f, 24) },
            Position = new Vector3(0, -0.05f, 0),
        });
        st.Root.AddChild(floorBody);

        st.Root.AddChild(new MeshInstance3D
        {
            Name = "MirrorBackingRegression",
            Mesh = new BoxMesh { Size = new Vector3(12, 6, .18f) },
            Position = new Vector3(0, 2, MirrorZ + .1f),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(.5f, .27f, .08f) },
        });

        var wall = new MeshInstance3D
        {
            Name = "BackWall",
            Mesh = new BoxMesh { Size = new Vector3(12, 4, 0.12f) },
            Position = new Vector3(0, 2f, BackWallZ - 0.10f),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.58f, 0.56f, 0.64f),
                Roughness = 0.95f,
            },
        };
        st.Root.AddChild(wall);

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
        var sun = new DirectionalLight3D { Name = "Key", LightEnergy = 1.0f, ShadowEnabled = true };
        st.Root.AddChild(sun);
        sun.RotationDegrees = new Vector3(-48, 210, 0);

        foreach (var (name, col) in Palette)
        {
            Vector3 p = name switch
            {
                "red"     => new Vector3(-1.75f, 1.95f, BackWallZ + 0.35f),
                "green"   => new Vector3( 0.00f, 1.95f, BackWallZ + 0.35f),
                "blue"    => new Vector3( 1.75f, 1.95f, BackWallZ + 0.35f),
                "yellow"  => new Vector3(-1.75f, 0.70f, BackWallZ + 0.35f),
                "cyan"    => new Vector3( 0.00f, 0.55f, BackWallZ + 0.35f),
                _         => new Vector3( 1.75f, 0.70f, BackWallZ + 0.35f),
            };
            st.Markers[name] = p;
            var ball = new MeshInstance3D
            {
                Name = $"MK_{name}",
                Mesh = new SphereMesh { Radius = 0.10f, Height = 0.20f },
                Position = p,
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = col,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                },
            };
            st.Root.AddChild(ball);
        }

        // A wide quad, so most of the marker grid images on the glass from most poses.
        st.QuadSize = new Vector2(2.6f, 2.2f);
        st.Glass = Mirror.Create(new Vector3(0, 0, MirrorZ), 180f, st.QuadSize.X, st.QuadSize.Y);
        st.Root.AddChild(st.Glass);
        // Snap onto the ACTUAL glass. Mirror pushes its quad 1 cm along the normal to escape
        // the coplanar-wall z-fight, and the optics have to be evaluated at the glass: 1 cm of
        // plane error is 2 cm of virtual-eye error, which the numeric probe flags immediately.
        st.PlanePoint = new Vector3(0, st.QuadSize.Y * 0.5f, MirrorZ);
        if (st.Glass.GetChildCount() > 0 &&
            st.Glass.GetChild(st.Glass.GetChildCount() - 1) is Node3D surf)
            st.PlanePoint = surf.GlobalPosition;
        GD.Print($"MIRRORSTEREO glass plane at {st.PlanePoint} (quad pushed off its nominal z={MirrorZ})");
    }

    // ── Optics helpers (same construction as MirrorDiagnostic) ────────────────────────────

    private static Vector3 Reflect(RunState st, Vector3 p) =>
        p - 2f * st.Normal.Dot(p - st.PlanePoint) * st.Normal;

    private static Vector3? BouncePoint(RunState st, Vector3 eye, Vector3 marker)
    {
        Vector3 dir = Reflect(st, marker) - eye;
        float denom = st.Normal.Dot(dir);
        if (Mathf.Abs(denom) < 1e-5f) return null;
        float t = st.Normal.Dot(st.PlanePoint - eye) / denom;
        if (t <= 0.02f || t >= 0.999f) return null;
        return eye + dir * t;
    }

    private static bool InsideQuad(RunState st, Vector3 p, float margin)
    {
        Vector3 rel = p - st.PlanePoint;
        float u = st.TangentU.Dot(rel), v = rel.Y;
        return MathF.Abs(u) <= st.QuadSize.X * 0.5f + margin &&
               MathF.Abs(v) <= st.QuadSize.Y * 0.5f + margin;
    }

    private static Vector3 EyeAt(double phiDeg, double r, double h)
    {
        double rad = Mathf.DegToRad(phiDeg);
        return new Vector3((float)(Math.Sin(rad) * r), (float)h, MirrorZ - (float)(Math.Cos(rad) * r));
    }

    private static string Classify(Color c)
    {
        float sum = c.R + c.G + c.B;
        if (sum < 0.11f) return null;
        float r = c.R / sum, g = c.G / sum, b = c.B / sum;
        if (r > 0.52f && g < 0.34f && b < 0.34f) return "red";
        if (g > 0.52f && r < 0.34f && b < 0.34f) return "green";
        if (b > 0.52f && r < 0.36f && g < 0.44f) return "blue";
        if (r > 0.42f && g > 0.42f && b < 0.26f) return "yellow";
        if (g > 0.40f && b > 0.40f && r < 0.26f) return "cyan";
        if (r > 0.42f && b > 0.40f && g < 0.26f) return "magenta";
        return null;
    }

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

    /// Segment (virtual eye → marker) against an AABB. The avatar is the only occluder in the
    /// studio, and a reflected avatar occludes exactly like a real one, so a blocked line is the
    /// reflection being CORRECT and the check has to be skipped rather than failed.
    private static bool BoxBlocked(Vector3 a, Vector3 bpt, Vector3 boxMin, Vector3 boxMax)
    {
        Vector3 d = bpt - a;
        float tEnter = float.NegativeInfinity, tExit = float.PositiveInfinity;
        for (int ax = 0; ax < 3; ax++)
        {
            float da = d[ax], oa = a[ax], mn = boxMin[ax], mx = boxMax[ax];
            if (Mathf.Abs(da) < 1e-6f) { if (oa < mn || oa > mx) return false; continue; }
            float t0 = (mn - oa) / da, t1 = (mx - oa) / da;
            if (t0 > t1) (t0, t1) = (t1, t0);
            tEnter = Mathf.Max(tEnter, t0);
            tExit = Mathf.Min(tExit, t1);
        }
        return tEnter < tExit && tExit > 0.02f && tEnter < 0.98f;
    }

    // ── Driver ───────────────────────────────────────────────────────────────────────────

    private sealed partial class Driver : Node
    {
        private readonly Node _host;
        private readonly RunState _st;
        private readonly int _aBad;
        private int _phaseCFrames = -1;
        private SimStereoXr _xr;
        private Player.VrSimDevice _sim;
        private XROrigin3D _origin;
        private XRCamera3D _xrCam;
        private readonly List<string> _cNotes = new();

        public Driver(Node host, RunState st, int aBad) { _host = host; _st = st; _aBad = aBad; }

        public override void _Process(double delta)
        {
            if (_phaseCFrames >= 0) { PhaseCStep(); return; }

            _st.Frames++;
            _st.Avatar?.Animate(1.0 / 60.0, 0f, true);
            if (_st.Frames < WarmupFrames) return;

            if (_st.PoseIndex >= _st.Poses.Count) { BeginPhaseC(); return; }

            if (_st.PoseFrames == 0)
            {
                var (phi, r, h, off, _) = _st.Poses[_st.PoseIndex];
                var eye = EyeAt(phi, r, h);
                _st.Cam.GlobalPosition = eye;
                _st.Cam.LookAt(new Vector3(0, Mathf.Clamp(eye.Y, 0.6f, 2.1f), MirrorZ), Vector3.Up);
                // The viewer's projection is the eye's: a real frustum with a real offset. Height
                // is the eye's vertical extent; a zero offset reproduces the desktop control.
                _st.Cam.SetFrustum(EyeHeightAtNear(0), off, ProbeNear, ProbeFar);
                _st.PoseFrames++;
                return;
            }

            _st.PoseFrames++;
            if (_st.PoseFrames < SettleFrames) return;

            Capture(_st.Poses[_st.PoseIndex]);
            _st.PoseIndex++;
            _st.PoseFrames = 0;
        }

        private void Capture((double phiDeg, double r, double h, Vector2 off, string tag) pose)
        {
            var img = _st.Root.GetViewport().GetTexture()?.GetImage();
            if (img == null) { _st.Defects.Add("viewport-no-image"); return; }
            if (img.GetFormat() != Image.Format.Rgba8) img.Convert(Image.Format.Rgba8);

            var (phi, r, h, off, tag) = pose;
            string label = $"{tag}_phi{phi:+0;-0;0}_r{r:0.0}_h{h:0.0}";
            var eye = _st.Cam.GlobalPosition;
            var problems = new List<string>();

            var vpRect = _st.Root.GetViewport().GetVisibleRect().Size;
            float sx = img.GetWidth() / Mathf.Max(1f, vpRect.X);
            float sy = img.GetHeight() / Mathf.Max(1f, vpRect.Y);
            Vector2 ToImg(Vector2 v) => new(v.X * sx, v.Y * sy);

            // ── Numeric probe on the live mirror ──────────────────────────────────────
            var mirCam = Grab<Camera3D>(_st.Glass, "_mirrorCam");
            var vp = Grab<SubViewport>(_st.Glass, "_viewport");
            if (mirCam == null || vp == null)
            {
                // Reflection into Mirror's privates is how every mirror diagnostic probes it; a
                // null here means a rename, and silently skipping would look like a pass.
                problems.Add("could not reach Mirror._mirrorCam/_viewport (field renamed?)");
            }
            else if (_st.Glass.IsLive)
            {
                float eyeErr = mirCam.GlobalPosition.DistanceTo(Reflect(_st, eye));
                if (eyeErr > 0.002f)
                    problems.Add($"reflection-cam off mirrored-eye by {eyeErr * 1000:0.0} mm");

                problems.AddRange(MirrorDiagnostic.WindowDefects(_st.Glass, mirCam, vp));
            }

            // ── Optical probes ────────────────────────────────────────────────────────
            var bounds = _st.Avatar != null ? WorldBounds(_st.Avatar) : null;
            var ePrime = Reflect(_st, eye);
            int imgW = img.GetWidth(), imgH = img.GetHeight();

            Vector2 bbMin = new(float.MaxValue, float.MaxValue), bbMax = new(float.MinValue, float.MinValue);
            bool anyCorner = false;
            for (int ci = 0; ci < 4; ci++)
            {
                var corner = _st.PlanePoint +
                             _st.TangentU * ((ci & 1) == 0 ? -_st.QuadSize.X / 2 : _st.QuadSize.X / 2) +
                             Vector3.Up * ((ci & 2) == 0 ? -_st.QuadSize.Y / 2 : _st.QuadSize.Y / 2);
                if (!_st.Cam.IsPositionInFrustum(corner)) continue;
                anyCorner = true;
                var sp = ToImg(_st.Cam.UnprojectPosition(corner));
                bbMin = bbMin.Min(sp); bbMax = bbMax.Max(sp);
            }
            if (anyCorner)
            {
                bbMin = (bbMin - new Vector2(14, 14)).Max(Vector2.Zero);
                bbMax = (bbMax + new Vector2(14, 14)).Min(new Vector2(imgW - 1, imgH - 1));
            }

            if (anyCorner && bbMax.X - bbMin.X >= 24 && bbMax.Y - bbMin.Y >= 24 && _st.Glass.IsLive)
            {
                foreach (var (name, mpos) in _st.Markers)
                {
                    if (bounds is { } bb && BoxBlocked(ePrime, mpos, bb.min, bb.max)) continue;
                    var bounce = BouncePoint(_st, eye, mpos);
                    if (bounce == null || !InsideQuad(_st, bounce.Value, 0f)) continue;
                    var px = ToImg(_st.Cam.UnprojectPosition(bounce.Value));
                    if (px.X < bbMin.X || px.X > bbMax.X || px.Y < bbMin.Y || px.Y > bbMax.Y) continue;

                    bool hit = false;
                    for (int dy = -10; dy <= 10 && !hit; dy += 2)
                    for (int dx = -10; dx <= 10 && !hit; dx += 2)
                    {
                        int ix = Mathf.Clamp((int)px.X + dx, 0, imgW - 1);
                        int iy = Mathf.Clamp((int)px.Y + dy, 0, imgH - 1);
                        hit = Classify(img.GetPixel(ix, iy)) == name;
                    }

                    string key = $"{tag}/{name}";
                    (int good, int bad) tally = _st.PerMarker.GetValueOrDefault(key, (0, 0));
                    if (hit) tally.good++;
                    else
                    {
                        tally.bad++;
                        problems.Add($"'{name}' should appear at ({px.X:0},{px.Y:0}) but nothing of that hue is there");
                    }
                    _st.PerMarker[key] = tally;
                }
            }

            string prefix = problems.Count == 0 ? "ok" : "fail";
            img.SavePng($"{_st.OutPrefix}_{prefix}_{label}.png");
            if (problems.Count == 0) GD.Print($"MIRRORSTEREO pass [{label}]");
            else foreach (var p in problems.Take(4)) GD.PrintErr($"MIRRORSTEREO FAIL [{label}] {p}");
            _st.Defects.AddRange(problems);
        }

        // ── Phase C ──────────────────────────────────────────────────────────────────────

        /// Install a simulated two-view interface and switch the root viewport into XR mode, so
        /// `Mirror` takes its stereo branch against real `XRInterface` calls and a real
        /// `XROrigin3D`/`XRCamera3D`. This proves the plumbing and the per-eye maths; it does NOT
        /// prove the shader's VIEW_INDEX selection, which needs an actual multiview render.
        private void BeginPhaseC()
        {
            _phaseCFrames = 0;
            GD.Print("── MIRRORSTEREO PHASE-C: simulated stereo interface ──");
            try
            {
                _sim = new Player.VrSimDevice();
                _sim.Install();
                // Stand close enough that the glass fills each eye. This exercises the per-eye
                // source-window crop rather than merely proving the normal full-glass path.
                _origin = new XROrigin3D { Name = "SimOrigin" };
                _st.Root.AddChild(_origin);
                _origin.GlobalPosition = new Vector3(0, 0, MirrorZ - .30f);
                // Godot cameras look along local -Z. The glass faces this test position from
                // +Z, so turn the simulated head toward it just as the mono probe does. Leaving
                // the origin at identity made its corner rays point away from the glass and
                // correctly disabled close-window cropping as an unsafe edge case.
                _origin.GlobalRotation = new Vector3(0, Mathf.Pi, 0);
                _xrCam = new XRCamera3D { Name = "SimXrCam", Near = 0.05f, Far = 400f, Current = true };
                _origin.AddChild(_xrCam);

                _xr = new SimStereoXr { Head = new Transform3D(Basis.Identity, new Vector3(0, 1.62f, 0)) };
                _sim.Head = _xr.Head;
                _sim.Commit();
                XRServer.AddInterface(_xr);
                _xr.Initialize();
                _xr.InterfaceIsPrimary = true;
                GetTree().Root.UseXR = true;
                GD.Print($"MIRRORSTEREO xr primary={XRServer.PrimaryInterface?.GetName()} " +
                         $"views={_xr.GetViewCount()} rt={_xr.GetRenderTargetSize()} useXr={GetTree().Root.UseXR}");
            }
            catch (Exception e)
            {
                _cNotes.Add($"phase-C setup threw: {e.Message}");
                GD.PrintErr($"MIRRORSTEREO PHASE-C setup FAIL: {e}");
                _phaseCFrames = 900;
            }
        }

        private void PhaseCStep()
        {
            _phaseCFrames++;
            if (_phaseCFrames < 12) return;
            if (_phaseCFrames > 12) { FinishPhaseC(); return; }

            var mirCamL = Grab<Camera3D>(_st.Glass, "_mirrorCam");
            var mirCamR = Grab<Camera3D>(_st.Glass, "_mirrorCamR");
            var vpL = Grab<SubViewport>(_st.Glass, "_viewport");
            var vpR = Grab<SubViewport>(_st.Glass, "_viewportR");

            GD.Print($"MIRRORSTEREO stereo={_st.Glass.IsStereo} live={_st.Glass.IsLive} " +
                     $"vpL={vpL?.Size} vpR={vpR?.Size}");

            if (!_st.Glass.IsStereo) { _cNotes.Add("Mirror did not enter stereo mode"); FinishPhaseC(); return; }
            if (!_st.Glass.IsLive) { _cNotes.Add("Mirror not live in stereo"); FinishPhaseC(); return; }
            if (mirCamR == null || vpR == null) { _cNotes.Add("right-eye view was never built"); FinishPhaseC(); return; }
            if (vpL.RenderTargetUpdateMode != SubViewport.UpdateMode.Always ||
                vpR.RenderTargetUpdateMode != SubViewport.UpdateMode.Always)
                _cNotes.Add("a stereo view's render target is not updating");

            Transform3D originXf = _origin.GlobalTransform;
            for (uint view = 0; view < 2; view++)
            {
                var cam = view == 0 ? mirCamL : mirCamR;
                var vp = view == 0 ? vpL : vpR;
                Transform3D eye = _xr.GetTransformForView(view, originXf);

                float posErr = cam.GlobalPosition.DistanceTo(Reflect(_st, eye.Origin));
                if (posErr > 0.002f)
                    _cNotes.Add($"view {view}: reflection cam off mirrored eye by {posErr * 1000:0.0} mm");

                foreach (var defect in MirrorDiagnostic.WindowDefects(_st.Glass, cam, vp))
                    _cNotes.Add($"view {view}: {defect}");
                Vector4 crop = _st.Glass.TextureWindowForDiagnostic(cam);
                Vector2 cropSpan = new(crop.Z - crop.X, crop.W - crop.Y);
                if (cropSpan.X >= .75f || cropSpan.Y >= .5f)
                    _cNotes.Add($"view {view}: close reflection did not crop its source window ({cropSpan})");
                float targetPixels = vp.Size.X * vp.Size.Y;
                float budgetPixels = _xr.GetRenderTargetSize().X * _xr.GetRenderTargetSize().Y *
                                     UI.DeviceProfile.MirrorResolutionScale * UI.DeviceProfile.MirrorResolutionScale;
                if (targetPixels > budgetPixels * 1.05f)
                    _cNotes.Add($"view {view}: close target uses {targetPixels:0} px over its {budgetPixels:0} px budget");
                GD.Print($"MIRRORSTEREO view{view} eye={eye.Origin} camera={cam.GlobalPosition} " +
                    $"posErr={posErr * 1000:0.00}mm near={cam.Near:0.000} vp={vp.Size} crop={cropSpan}");
            }

            // The two eyes must not collapse onto one camera — that is precisely the old bug.
            float sep = mirCamL.GlobalPosition.DistanceTo(mirCamR.GlobalPosition);
            GD.Print($"MIRRORSTEREO eye separation at the reflection cameras = {sep * 1000:0.0} mm (ipd {Ipd * 1000:0.0} mm)");
            if (Mathf.Abs(sep - Ipd) > 0.002f)
                _cNotes.Add($"reflection cameras {sep * 1000:0.0} mm apart, expected the IPD {Ipd * 1000:0.0} mm");

            // …and their projections must differ, or both eyes are being shown the same image.
            var pl = mirCamL.GetCameraProjection();
            var pr = mirCamR.GetCameraProjection();
            if (Mathf.Abs(pl.Z.X - pr.Z.X) < 1e-4f)
                _cNotes.Add("both eyes got the same horizontal frustum offset");
        }

        private void FinishPhaseC()
        {
            try
            {
                GetTree().Root.UseXR = false;
                if (_xr != null) { _xr.Uninitialize(); XRServer.RemoveInterface(_xr); }
                _sim?.Remove();
            }
            catch (Exception e) { GD.Print($"MIRRORSTEREO phase-C teardown note: {e.Message}"); }

            GD.Print("── MIRRORSTEREO summary ────────────────────────────");
            foreach (var (key, (good, bad)) in _st.PerMarker.OrderBy(kv => kv.Key))
                GD.Print($"MIRRORSTEREO marker {key,-18}: {good} verified, {bad} wrong");
            foreach (var n in _cNotes) GD.PrintErr($"MIRRORSTEREO PHASE-C FAIL {n}");

            // Every marker that was checked at all must have been checked in all three viewer
            // configurations; a hue that only ever passed with a zero offset proves nothing about
            // the asymmetric case, which is the one VR runs.
            var tags = new[] { "mono", "eyeL", "eyeR" };
            int thin = 0;
            foreach (var t in tags)
            {
                int verified = _st.PerMarker.Where(kv => kv.Key.StartsWith(t + "/")).Sum(kv => kv.Value.Item1);
                GD.Print($"MIRRORSTEREO viewer '{t}': {verified} marker reflections verified");
                if (verified < 8) { thin++; GD.PrintErr($"MIRRORSTEREO FAIL: viewer '{t}' only verified {verified} — too thin to prove anything"); }
            }

            bool ok = _aBad == 0 && _st.Defects.Count == 0 && _cNotes.Count == 0 && thin == 0 &&
                      _st.PerMarker.Values.All(v => v.Item2 == 0);
            GD.Print(ok
                ? $"MIRRORSTEREO PASS: decode exact, {_st.Poses.Count} rendered poses optically correct " +
                  "under asymmetric viewers, stereo path places both eyes correctly."
                : $"MIRRORSTEREO FAIL: {_st.Defects.Count} rendered defects, {_cNotes.Count} stereo-path defects.");
            GetTree().Quit(ok ? 0 : 1);
        }

        private static T Grab<T>(GodotObject obj, string field) where T : class
        {
            var fi = obj.GetType().GetField(field,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return fi?.GetValue(obj) as T;
        }
    }
}
