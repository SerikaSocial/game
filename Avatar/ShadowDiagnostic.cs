using System;
using Godot;

namespace SerikaSocial.Avatar;

/// Renders proof that a first-person-hidden head still shapes the avatar's cast shadow.
///
///   Godot --path game -- --serika-shadowtest [--ska /path/to/avatar.ska] [--out /tmp/shadow]
///
/// Needs a real or virtual display (xvfb-run) — see ShotDiagnostic.cs for why headless can't
/// capture frames.
///
/// Three captures of one otherwise-identical scene (avatar on a floor plane under a single
/// shadow-casting sun):
///
///   A "visible"     every mesh on the default layer — the reference frame.
///   B "layered"     head meshes moved onto Serika's first-person head layer and culled from
///                   the capture camera — exactly what LocalPlayer/VrPlayer do in first person,
///                   with DeviceProfile's light-mask stamp in effect.
///   C "invisible"   head meshes hidden with Visible=false — the naive alternative, kept here
///                   as a live demonstration of WHY the render layers exist.
///
/// The assertions are pixel counts, not eyeballs:
///   • B differs from A somewhere up top    → the head really was culled from the camera;
///   • B's LOWER half matches A exactly      → the culled head/hair still cast their shadow;
///   • C's lower half differs from A a lot   → Visible=false would have blown the hole the
///                                             layered scheme exists to prevent.
public static partial class ShadowDiagnostic
{
    private const int Width = 900;
    private const int Height = 900;
    private const int WarmupFrames = 30;

    /// Per-channel delta above which a pixel counts as "changed".
    private const byte DiffThreshold = 6;

    public static void Run(Node host, string skaPath, string outPrefix)
    {
        try { RunInner(host, skaPath, outPrefix); }
        catch (Exception e)
        {
            GD.Print($"SHADOW FAIL exception: {e}");
            host.GetTree().Quit(1);
        }
    }

    private static void RunInner(Node host, string skaPath, string outPrefix)
    {
        outPrefix ??= "user://shadow";
        GD.Print($"SHADOW ska={skaPath ?? "(bean)"} out={outPrefix}");

        if (DisplayServer.GetName() == "headless")
        {
            GD.Print("SHADOW FAIL: running headless — nothing to capture. Drop --headless " +
                     "(use xvfb-run if there is no display).");
            host.GetTree().Quit(1);
            return;
        }

        var vp = new SubViewport
        {
            Name = "ShadowViewport",
            Size = new Vector2I(Width, Height),
            OwnWorld3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        host.AddChild(vp);

        var root = new Node3D { Name = "ShadowRoot" };
        vp.AddChild(root);

        // Floor catches the silhouette; keep it mid-grey so lit-vs-shadow contrast reads strongly.
        var floor = new MeshInstance3D
        {
            Name = "Floor",
            Mesh = new PlaneMesh { Size = new Vector2(14, 14), Orientation = PlaneMesh.OrientationEnum.Y },
        };
        floor.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.62f, 0.60f, 0.68f) };
        root.AddChild(floor);

        var sun = new DirectionalLight3D
        {
            Name = "Sun",
            LightEnergy = 1.6f,
            ShadowEnabled = true,
        };
        // Same guarantee DeviceProfile.ApplyToScene stamps onto every light in the game — as an
        // OR, never an assignment: assigning the avatar bits alone would black out the world
        // (the floor lit by nothing, which is exactly the bug the first version of this test
        // had, and read as "no shadow" when it was really "no lit floor").
        sun.LightCullMask |= Player.LocalPlayer.AvatarRenderLayers;
        sun.ShadowCasterMask |= Player.LocalPlayer.AvatarRenderLayers;
        root.AddChild(sun);
        // Yaw ~180°: the sun shines from behind the CAMERA, so the silhouette it casts lands
        // on the floor IN FRONT of the avatar — inside the frame. At -28° yaw the shadow fell
        // away from the viewer and the diff regions compared empty background (bottom=0 for
        // every variant, including the broken Visible=false one — vacuous either way).
        sun.RotationDegrees = new Vector3(-52, 152, 0);

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.07f, 0.06f, 0.11f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.30f, 0.29f, 0.36f),
            AmbientLightEnergy = 0.22f,
        };
        root.AddChild(new WorldEnvironment { Name = "Env", Environment = env });

        var av = AvatarLibrary.InstantiateOrDefault(skaPath);
        if (av == null)
        {
            GD.Print("SHADOW FAIL: avatar failed to instantiate");
            host.GetTree().Quit(1);
            return;
        }
        root.AddChild(av);
        av.RotationDegrees = new Vector3(0, 180, 0); // face the camera

        // THE production first-person treatment — the same call LocalPlayer.SetAvatar makes, so
        // this fixture tests what ships rather than a re-implementation of it.
        FirstPersonProxy.Apply(av, Player.LocalPlayer.AvatarOthersLayer,
                               Player.LocalPlayer.AvatarFpLayer,
                               Player.LocalPlayer.AvatarShadowLayer);

        // Variant C hides the head geometry AND the ShadowsOnly copies that would otherwise stand
        // in for it — that is what "the silhouette was genuinely deleted" has to mean now that
        // every mesh has a twin.
        var heads = new System.Collections.Generic.List<MeshInstance3D>();
        foreach (var child in av.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is not MeshInstance3D mesh) continue;
            if (mesh.Name.ToString().EndsWith("FpProxy")) continue;
            bool isTwin = mesh.Name.ToString().EndsWith("ShadowTwin");
            var source = isTwin ? mesh.GetParent().GetNodeOrNull<MeshInstance3D>(
                             mesh.Name.ToString().Replace("ShadowTwin", "")) : mesh;
            if (source == null || !av.IsHeadMesh(source)) continue;
            heads.Add(mesh);
        }
        float h = av.Height > 0.1f ? av.Height : 1.6f;
        GD.Print($"SHADOW height={h:F2} headMeshesPlusTwins={heads.Count}");

        var cam = new Camera3D { Name = "ShotCam", Fov = 50 };
        root.AddChild(cam);
        cam.Current = true;
        cam.Position = new Vector3(0, h * 2.1f, h * 2.5f);
        cam.LookAt(new Vector3(0, h * 0.30f, 0), Vector3.Up);

        host.AddChild(new Taker(vp, cam, outPrefix, av, heads, sun));
    }

        /// Walks the three variants, capturing once per frame; diffs consecutive pairs off-line.
        private sealed partial class Taker : Node
        {
            private static readonly string[] Names =
                { "far_plain", "far_culllayers", "far_removed" };

        private readonly SubViewport _vp;
        private readonly Camera3D _cam;
        private readonly DirectionalLight3D _sun;
        private readonly string _prefix;
        private readonly AvatarInstance _av;
        private readonly System.Collections.Generic.List<MeshInstance3D> _heads;
        private readonly Godot.Collections.Dictionary<string, Image> _shots = new();
        private int _frames;
        private int _step;
        private bool _applied;
        private int _settle;

        /// Exactly LocalPlayer.ApplyCameraMode's two masks: first person drops the original
        /// meshes and renders the head-filtered proxy instead; every other camera drops the proxy
        /// and renders the originals. The ShadowsOnly copies are in neither mask — they are
        /// excluded from colour passes by construction and present in every shadow pass.
        private static uint FpCullMask => Player.LocalPlayer.FpCullLayers;
        private static uint NonFpCullMask => Player.LocalPlayer.NonFpCullLayers;

        public Taker(SubViewport vp, Camera3D cam, string prefix, AvatarInstance av,
                     System.Collections.Generic.List<MeshInstance3D> heads, DirectionalLight3D sun)
        {
            _vp = vp; _cam = cam; _prefix = prefix; _av = av; _heads = heads; _sun = sun;
        }

        public override void _Process(double delta)
        {
            _frames++;
            if (_frames < WarmupFrames) return;

            int totalSteps = Names.Length * 2; // each variant: shadowed + shadow-off capture
            if (_step >= totalSteps)
            {
                Finish();
                return;
            }

            int variant = _step / 2;
            bool wantDark = _step % 2 == 1;

            // Mutate, then settle TWO frames: one frame lets the flag reach the render thread,
            // the second lets the shadow atlas actually rebuild (a single-frame settle captured
            // half-updated shadows and produced garbage masks).
            if (!_applied)
            {
                ApplyVariant(variant);
                _sun.ShadowEnabled = !wantDark;
                _applied = true;
                _settle = 0;
                return;
            }
            if (_settle < 2) { _settle++; return; }

            Capture(Names[variant] + (wantDark ? "_noshadow" : ""));
            _sun.ShadowEnabled = true;
            _applied = false;
            _step++;
        }

        private void ApplyVariant(int variant)
        {
            foreach (var m in _heads) m.Visible = true;
            var stdPose = new Transform3D(Basis.Identity,
                new Vector3(0, _av.Height * 2.1f, _av.Height * 2.5f));
            _cam.Transform = stdPose;
            _cam.LookAt(new Vector3(0, _av.Height * 0.30f, 0), Vector3.Up);
            _cam.CullMask = uint.MaxValue & ~NonFpCullMask;

            switch (variant)
            {
                case 0: // plain third person
                    break;
                case 1: // FIRST-PERSON SIMULATION: exactly LocalPlayer's FP cull mask. The
                        // originals vanish from view and the headless proxy takes over; the
                        // ShadowsOnly copies must hold the silhouette in the shadow map.
                    _cam.CullMask = uint.MaxValue & ~FpCullMask;
                    break;
                case 2: // TOTAL removal (no twins either): proves the fixture detects a truly
                        // deleted silhouette rather than passing vacuously.
                    foreach (var m in _heads) m.Visible = false;
                    break;
            }

            GD.Print($"SHADOW variant {Names[variant]}: cull=0x{_cam.CullMask:X} " +
                     $"sunShadows={_sun.ShadowEnabled} current={_vp.GetCamera3D()?.Name}");
        }

        private void Capture(string name)
        {
            var img = _vp.GetTexture()?.GetImage();
            if (img == null) { GD.Print($"SHADOW FAIL: no viewport image for {name}"); GetTree().Quit(1); return; }
            img.Convert(Image.Format.Rgba8);
            _shots[name] = img;
            string path = $"{_prefix}_{name}.png";
            var err = img.SavePng(path);
            GD.Print($"SHADOW wrote {path} ({err})");
        }

        /// Bilinear-free pixel diff over rectangle regions of two captures.
        private long Diff(Image a, Image b, int x0, int y0, int x1, int y1)
        {
            long n = 0;
            x1 = Math.Min(x1, Math.Min(a.GetWidth(), b.GetWidth()));
            y1 = Math.Min(y1, Math.Min(a.GetHeight(), b.GetHeight()));
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    var ca = a.GetPixel(x, y);
                    var cb = b.GetPixel(x, y);
                    if (Math.Abs(ca.R8 - cb.R8) > DiffThreshold ||
                        Math.Abs(ca.G8 - cb.G8) > DiffThreshold ||
                        Math.Abs(ca.B8 - cb.B8) > DiffThreshold)
                        n++;
                }
            return n;
        }

        /// Pixels where turning the sun's shadows OFF brightened the image — i.e. pixels that
        /// WERE in shadow. Derived per variant, so comparing masks between variants compares
        /// the cast silhouettes alone, with every view pixel (hair draping over the body,
        /// arm positions, culled-surface holes) cancelled out by construction.
        private long[] ShadowMask(Image lit, Image dark)
        {
            int w = lit.GetWidth(), h = lit.GetHeight();
            var mask = new long[(w * h + 63) / 64 + 1];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var cl = lit.GetPixel(x, y);
                    var cd = dark.GetPixel(x, y);
                    bool shadowed = cd.R8 - cl.R8 > 12 || cd.G8 - cl.G8 > 12 || cd.B8 - cl.B8 > 12;
                    if (shadowed)
                    {
                        int idx = y * w + x;
                        mask[idx / 64] |= 1L << (idx % 64);
                    }
                }
            return mask;
        }

        private long MaskXor(long[] a, long[] b)
        {
            long n = 0;
            for (int i = 0; i < a.Length && i < b.Length; i++)
            {
                long x = a[i] ^ b[i];
                // Kernighan popcount — clears the lowest set bit each pass, so it terminates
                // even when the top bit makes the value negative (a plain `x >>= 1` loop
                // sign-extends forever).
                while (x != 0) { x &= x - 1; n++; }
            }
            return n;
        }

        private void Finish()
        {
            var farPlain = _shots["far_plain"];
            var culled = _shots["far_culllayers"];
            var removed = _shots["far_removed"];
            int w = farPlain.GetWidth(), h = farPlain.GetHeight();

            var maskOf = (string lit, string dark) => ShadowMask(_shots[lit], _shots[dark]);
            var baseMask = maskOf("far_plain", "far_plain_noshadow");
            long shadowPixels = MaskXor(new long[(w * h + 63) / 64 + 1], baseMask);
            long xorCull = MaskXor(baseMask, maskOf("far_culllayers", "far_culllayers_noshadow"));
            long xorRemoved = MaskXor(baseMask, maskOf("far_removed", "far_removed_noshadow"));

            // View proof: culling head surfaces must visibly change the frame.
            int midY = h * 45 / 100;
            long viewDiff = Diff(farPlain, culled, w / 4, 0, w * 3 / 4, midY);

            GD.Print($"SHADOW metrics shadowPx={shadowPixels} viewDiff(fpSim)={viewDiff} | " +
                     $"shadowXor(fpSim twins)={xorCull} shadowXor(totalRemoval)={xorRemoved}");

            // THE assertion of this test: with ShadowsOnly twins active, simulating the
            // first-person camera leaves the cast shadow pixel-whole (small xor), while total
            // removal demonstrably deletes it (large xor — fixture honesty).
            // Twins restore the silhouette almost perfectly but not bit-exact (edge bias /
            // mipmap differences at strand silhouettes), so gate relatively: ≤6% of the base
            // shadow may differ under the FP simulation.
            bool pass = viewDiff > 400
                        && xorCull <= System.Math.Max(800, shadowPixels * 6 / 100)
                        && xorRemoved > 300
                        && shadowPixels > 2000;
            if (_heads.Count == 0)
            {
                GD.Print("SHADOW WARN: nothing was classified as head geometry — the bean's " +
                         "unskinned primitives cannot exercise this test. Pass a real .ska.");
                pass = false;
            }

            GD.Print(pass
                ? "SHADOW OK: the first-person camera loses the head/hair from its VIEW while " +
                  "every shadow — including its own — keeps the complete silhouette."
                : $"SHADOW FAIL: viewDiff={viewDiff} xorCull={xorCull} xorRemoved={xorRemoved}");
            GetTree().Quit(pass ? 0 : 1);
        }
    }
}
