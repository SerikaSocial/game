using System;
using Godot;

namespace SerikaSocial.Avatar;

/// Renders an avatar to PNG so the cel/toon look can actually be judged.
///
///   Godot --path game -- --serika-shot --ska /path/to/avatar.ska --out /tmp/shot
///
/// Note there is no `--headless` here: headless has no rendering server, so the viewport
/// capture comes back blank. This needs a real (or virtual, via xvfb-run) display.
///
/// Every other avatar diagnostic in this folder deliberately measures numbers instead of
/// looking at pixels, because secondary physics and retargeting are things you can talk
/// yourself into believing from a still frame. Shading is the opposite: banding, shadow tint
/// and outline width have no correct value to assert, only a look. So this one is a camera.
///
/// It loads the avatar twice — once with `ToonShading.Enabled` on, once off — and stands them
/// side by side under identical lighting, because the useful question is never "does this
/// image look nice" but "what did the shader change". Toon is on the left, flat PBR on the
/// right. Two framings are written: a full body and a head close-up, since banding and the
/// outline read at completely different distances.
public static partial class ShotDiagnostic
{
    /// Frames to let the renderer settle before capturing. The avatar's skeleton, spring-bone
    /// rest calibration and the toon materials all land over the first few frames, and the
    /// first capture of a fresh viewport is routinely black.
    private const int WarmupFrames = 30;

    /// Capture resolution.
    private const int ShotWidth = 1280;
    private const int ShotHeight = 1100;

    /// `--outline <w>` overrides the outline hull width on the toon rig after load, and
    /// `--shader <name>=<value>` overrides any other toon uniform. Re-running
    /// `ToonShading.ApplyToAvatar` cannot do this — it reads the *active* material, which by
    /// then is already the ShaderMaterial it produced, so it finds no `BaseMaterial3D` and
    /// does nothing. Poking the uniforms is the only way to sweep a value without an export.
    public static void Run(Node host, string skaPath, string outPrefix,
                           string outlineWidth, string shaderOverride)
    {
        try { RunInner(host, skaPath, outPrefix, outlineWidth, shaderOverride); }
        catch (Exception e)
        {
            GD.Print($"SHOT FAIL exception: {e}");
            host.GetTree().Quit(1);
        }
    }

    private static void RunInner(Node host, string skaPath, string outPrefix,
                                 string outlineWidth, string shaderOverride)
    {
        outPrefix ??= "user://shot";
        GD.Print($"SHOT ska={skaPath ?? "(bean)"} out={outPrefix} " +
                 $"deviceToon={UI.DeviceProfile.ToonShading} outline={UI.DeviceProfile.AvatarOutline}");

        if (DisplayServer.GetName() == "headless")
        {
            GD.Print("SHOT FAIL: running headless — the viewport has nothing to capture. " +
                     "Drop --headless (use xvfb-run if there is no display).");
            host.GetTree().Quit(1);
            return;
        }

        // Render into an isolated SubViewport rather than the main one. `Main._Ready` has
        // already put the boot UI on screen, and a root-viewport capture is that UI — the
        // first attempt at this produced a flawless screenshot of the purple login gradient.
        // `OwnWorld3D` also keeps the game's own environment and cameras out of the shot.
        var vp = new SubViewport
        {
            Name = "ShotViewport",
            Size = new Vector2I(ShotWidth, ShotHeight),
            OwnWorld3D = true,
            TransparentBg = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        host.AddChild(vp);

        var root = new Node3D { Name = "ShotRoot" };
        vp.AddChild(root);
        LightScene(root);

        // Toon first, then flat: `ToonShading.Enabled` is read at load time, so the two
        // instances have to be built on either side of the flag flip.
        //
        // Both stand on the same spot and are photographed one at a time, hidden and shown
        // between captures. Standing them side by side in one frame seems more useful and
        // isn't: it puts the burden on the reader to remember which half is which, and
        // screen-left is world-*plus*-X here (the camera looks down -Z), which is exactly the
        // kind of thing that gets read backwards. A filename cannot be read backwards.
        ToonShading.Enabled = true;
        var toon = Place(root, skaPath);
        ToonShading.Enabled = false;
        var flat = Place(root, skaPath);
        ToonShading.Enabled = true;

        if (toon == null || flat == null)
        {
            GD.Print("SHOT FAIL: avatar failed to instantiate");
            host.GetTree().Quit(1);
            return;
        }

        float height = toon.Height > 0.1f ? toon.Height : 1.6f;
        GD.Print($"SHOT avatarHeight={height:F3}");
        Summarise(toon, "toon");
        Summarise(flat, "flat");

        if (!string.IsNullOrEmpty(outlineWidth) && float.TryParse(outlineWidth, out float w))
        {
            int n = OverrideUniform(toon, "outline_width", w, outlinePass: true);
            GD.Print($"SHOT override outline_width={w} on {n} pass(es)");
        }
        if (!string.IsNullOrEmpty(shaderOverride))
        {
            var bits = shaderOverride.Split('=', 2);
            if (bits.Length == 2 && float.TryParse(bits[1], out float v))
            {
                int n = OverrideUniform(toon, bits[0], v, outlinePass: false);
                GD.Print($"SHOT override {bits[0]}={v} on {n} surface(s)");
            }
            else GD.Print($"SHOT: ignoring malformed --shader '{shaderOverride}' (want name=number)");
        }

        var cam = new Camera3D { Name = "ShotCam", Fov = 45 };
        root.AddChild(cam);
        cam.Current = true;

        host.AddChild(new ShotTaker(vp, cam, outPrefix, height, toon, flat));
    }

    /// Sets `name` on every toon material in the subtree — or on their outline `next_pass`
    /// when `outlinePass` is set. Returns how many it touched.
    private static int OverrideUniform(Node n, string name, float value, bool outlinePass)
    {
        int hits = 0;
        if (n is MeshInstance3D mi && mi.Mesh != null)
        {
            for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
            {
                if (mi.GetActiveMaterial(s) is not ShaderMaterial sm) continue;
                var target = outlinePass ? sm.NextPass as ShaderMaterial : sm;
                if (target == null) continue;
                target.SetShaderParameter(name, value);
                hits++;
            }
        }
        foreach (var c in n.GetChildren()) hits += OverrideUniform(c, name, value, outlinePass);
        return hits;
    }

    /// How many surfaces ended up shaded, and how many are still on a `BaseMaterial3D`. An
    /// empty frame and a correctly-rendered-but-unshaded frame look nothing alike once you
    /// see them, but this distinguishes them without opening the PNG — and it catches the
    /// case where the toon walk silently skipped a mesh.
    private static void Summarise(Node n, string tag)
    {
        var c = new Counts();
        Count(n, c);
        GD.Print($"SHOT surfaces[{tag}] shader={c.Shaded} standard={c.Plain} " +
                 $"outline={c.Outlined} unshadedStandard={c.Unshaded}");
    }

    private sealed class Counts
    {
        public int Shaded, Plain, Outlined, Unshaded;
    }

    private static void Count(Node n, Counts c)
    {
        if (n is MeshInstance3D mi && mi.Mesh != null)
        {
            for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
            {
                var m = mi.GetActiveMaterial(s);
                if (m is ShaderMaterial sm)
                {
                    c.Shaded++;
                    if (sm.NextPass is ShaderMaterial) c.Outlined++;
                }
                else if (m is BaseMaterial3D bm)
                {
                    c.Plain++;
                    // VRM ships `KHR_materials_unlit`, which imports as an unshaded
                    // StandardMaterial3D. Those surfaces ignore every light in the scene, so
                    // the un-toon-shaded rig renders identically under any lighting — worth
                    // knowing before concluding anything from a lit comparison.
                    if (bm.ShadingMode == BaseMaterial3D.ShadingModeEnum.Unshaded) c.Unshaded++;
                }
                else if (m != null) c.Plain++;
            }
        }
        foreach (var ch in n.GetChildren()) Count(ch, c);
    }

    /// A rig's forward is -Z and the camera sits on +Z, so an un-rotated avatar shows the
    /// camera its back — which is what the first working render produced.
    private static AvatarInstance Place(Node3D root, string skaPath)
    {
        var av = AvatarLibrary.InstantiateOrDefault(skaPath);
        if (av == null) return null;
        root.AddChild(av);
        av.RotationDegrees = new Vector3(0, 180, 0);
        return av;
    }

    /// A three-quarter key from the camera's front-left, a dim fill from the other side, and a
    /// mid-grey background (the rim light reads against neither black nor white).
    ///
    /// The direction matters more than it looks. A `DirectionalLight3D` shines down its own -Z,
    /// so a yaw near 180° is a *back* light — which is what the first version of this rig had,
    /// and it threw the whole face into shadow. Under a backlight the toon ramp collapses to its
    /// lowest band and the avatar looks filthy, which reads exactly like a broken shader rather
    /// than a badly-placed lamp. Keep the yaw within ~45° of 0 or this measures nothing.
    private static void LightScene(Node3D root)
    {
        var key = new DirectionalLight3D
        {
            Name = "Key",
            LightEnergy = 1.5f,
            ShadowEnabled = true,
        };
        root.AddChild(key);
        key.RotationDegrees = new Vector3(-30, 28, 0);

        var fill = new DirectionalLight3D { Name = "Fill", LightEnergy = 0.4f };
        root.AddChild(fill);
        fill.RotationDegrees = new Vector3(-10, -45, 0);

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.24f, 0.22f, 0.30f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.42f, 0.40f, 0.50f),
            AmbientLightEnergy = 0.45f,
        };
        root.AddChild(new WorldEnvironment { Name = "Env", Environment = env });
    }

    /// Waits out the warm-up, then walks the framings, capturing one PNG each.
    ///
    /// A camera move does not reach the render thread until the *next* draw, so setting the
    /// transform and reading the texture back in the same frame captures the previous framing
    /// — which is why the first working run wrote the body shot into `_head.png` and an empty
    /// scene into `_body.png`. Each framing therefore gets its own frame to settle before the
    /// read-back.
    private sealed partial class ShotTaker : Node
    {
        /// Framings, in order. Y positions and look-at heights are fractions of avatar height
        /// so this works on a 1.5 m schoolgirl and a 1.9 m robot alike.
        private static readonly (string Name, float EyeY, float Dist, float LookY)[] Shots =
        {
            ("body", 0.58f, 2.60f, 0.52f),  // full height
            ("head", 0.92f, 0.95f, 0.92f),  // head and shoulders
            ("face", 0.94f, 0.45f, 0.94f),  // tight — banding and outline width
        };

        private readonly SubViewport _vp;
        private readonly Camera3D _cam;
        private readonly string _prefix;
        private readonly float _height;
        private readonly AvatarInstance _toon;
        private readonly AvatarInstance _flat;
        private int _frames;
        private int _step;
        private bool _framed;

        public ShotTaker(SubViewport vp, Camera3D cam, string prefix, float height,
                         AvatarInstance toon, AvatarInstance flat)
        {
            _vp = vp;
            _cam = cam;
            _prefix = prefix;
            _height = height;
            _toon = toon;
            _flat = flat;
        }

        public override void _Process(double delta)
        {
            _frames++;
            if (_frames < WarmupFrames) return;

            // Each framing is captured twice, toon then flat.
            int total = Shots.Length * 2;
            if (_step >= total)
            {
                GD.Print("SHOT: done");
                GetTree().Quit(0);
                return;
            }

            var s = Shots[_step / 2];
            bool isToon = _step % 2 == 0;

            if (!_framed)
            {
                _toon.Visible = isToon;
                _flat.Visible = !isToon;
                _cam.Position = new Vector3(0, _height * s.EyeY, s.Dist);
                _cam.LookAt(new Vector3(0, _height * s.LookY, 0), Vector3.Up);
                _framed = true;
                return; // let this framing render before reading it back
            }

            Capture($"{s.Name}_{(isToon ? "toon" : "flat")}");
            _framed = false;
            _step++;
        }

        private void Capture(string name)
        {
            string path = $"{_prefix}_{name}.png";
            var img = _vp.GetTexture()?.GetImage();
            if (img == null) { GD.Print($"SHOT FAIL: no viewport image for {name}"); return; }

            var err = img.SavePng(path);
            if (err != Error.Ok) { GD.Print($"SHOT FAIL: SavePng({path}) = {err}"); return; }
            GD.Print($"SHOT wrote {path} ({img.GetWidth()}x{img.GetHeight()})");
        }
    }
}
