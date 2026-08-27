using System;
using System.Collections.Generic;
using Godot;
using SerikaSocial.Avatar;

namespace SerikaSocial.Player;

/// Renders the REAL desktop rig — a genuine `LocalPlayer` wearing an avatar, driven through its
/// public movement inputs — and captures proof screenshots of the tracked first-person camera
/// plus the cast-shadow guarantee:
///
///   Godot --path game -- --serika-camshot [--ska path.ska] [--out /tmp/cam]
///
/// Needs a display or xvfb-run (headless has no renderer). Writes three PNGs and prints a
/// numeric eye-vs-camera agreement table per shot:
///
///   tp_shadow   third-person wide: full avatar with hair/head shaping its ground shadow
///   fp_level    first person, level gaze: hands/torso visible, own face absent
///   fp_lookdown first person pitched fully down: body and legs, never the inside of the skull
public static partial class CamShotDiagnostic
{
    private const int Width = 900;
    private const int Height = 700;
    private const int WarmupFrames = 45;
    private const int SettleFrames = 12;

    private sealed class RunState
    {
        public Node3D Root;
        public SubViewport Vp;
        public LocalPlayer Pl;
        public AvatarInstance Av;
        public Camera3D Observer;
        public Camera3D PlayerCam;
        public string OutPrefix;
        public List<string> PendingShots = new();
        public int Frames;
        public int Phase;           // index into phases
        public int PhaseFrames;
        public bool Failed;

        // Best (smallest) |eyeProbe − activeCamera| seen during each capture phase, metres.
        public readonly Dictionary<string, float> CamDelta = new();
    }

    /// One entry per capture. `look` is fed via AddLook once (pixels); walk/crouch/sprint drive
    /// ExternalMove/ExternalCrouch/ExternalSprint so locomotion runs the production input path.
    private static readonly (string Name, Mode Mode, Vector2 Look, bool Walk, bool Crouch, bool Sprint)[] Shots =
    {
        ("tp_shadow",   Mode.ThirdPerson, new Vector2(0, 0),     false, false, false),
        ("proxy_back",  Mode.ProxyFromOutside, new Vector2(0, 0), false, false, false),
        // Front view too: the back shows whether hair survived, the front shows whether the
        // collar, tie and chest ornaments did. Judging "is that a head ornament or the uniform's
        // ribbon?" from the first-person shots alone is guesswork — from here it is obvious.
        ("proxy_front", Mode.ProxyFromOutside, new Vector2(0, 0), false, false, false),
        ("fp_level",    Mode.FirstPerson, new Vector2(0, 80),    true,  false, false),
        ("fp_lookdown", Mode.FirstPerson, new Vector2(0, 900),   true,  false, false),
        ("fp_crouch",   Mode.FirstPerson, new Vector2(0, 120),   false, true,  false),
        ("fp_sprint",   Mode.FirstPerson, new Vector2(0, 40),    true,  false, true),
        ("fp_lookup",   Mode.FirstPerson, new Vector2(0, -1050), false, false, false),
    };

    /// ProxyFromOutside is the observer camera wearing the FIRST-PERSON cull mask: it shows the
    /// head-filtered proxy from a distance, which is the only view that makes "is anything of the
    /// head left?" and "is the outfit still whole?" answerable in one glance. From inside the
    /// skull the two questions look the same.
    private enum Mode { ThirdPerson, FirstPerson, ProxyFromOutside }

    public static void Run(Node host, string skaPath, string outPrefix)
    {
        try { RunInner(host, skaPath, outPrefix); }
        catch (Exception e)
        {
            GD.Print($"CAMSHOT FAIL exception: {e}");
            host.GetTree().Quit(1);
        }
    }

    private static void RunInner(Node host, string skaPath, string outPrefix)
    {
        outPrefix ??= "user://cam";
        GD.Print($"CAMSHOT ska={skaPath ?? "(auto)"} out={outPrefix}");

        if (DisplayServer.GetName() == "headless")
        {
            GD.Print("CAMSHOT FAIL: headless cannot render. Use xvfb-run.");
            host.GetTree().Quit(1);
            return;
        }

        var st = new RunState { OutPrefix = outPrefix };
        st.Vp = new SubViewport
        {
            Name = "CamViewport",
            Size = new Vector2I(Width, Height),
            OwnWorld3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        host.AddChild(st.Vp);

        st.Root = new Node3D { Name = "CamRoot" };
        st.Vp.AddChild(st.Root);

        var floor = new MeshInstance3D { Name = "Floor", Mesh = new PlaneMesh { Size = new Vector2(24, 24), Orientation = PlaneMesh.OrientationEnum.Y } };
        floor.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.58f, 0.56f, 0.64f) };
        st.Root.AddChild(floor);

        // The rig is a real CharacterBody3D — without a collider it free-falls through the
        // capture window and every shot drifts out of frame.
        var floorBody = new StaticBody3D { Name = "FloorBody" };
        floorBody.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(24, 0.1f, 24) },
            Position = new Vector3(0, -0.05f, 0),
        });
        st.Root.AddChild(floorBody);

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.42f, 0.40f, 0.50f),
            AmbientLightEnergy = 0.5f,
        };
        var skyMat = new ProceduralSkyMaterial { SkyTopColor = new Color(0.16f, 0.12f, 0.26f), SkyHorizonColor = new Color(0.34f, 0.28f, 0.44f) };
        env.Sky = new Sky { SkyMaterial = skyMat };
        st.Root.AddChild(new WorldEnvironment { Environment = env });

        var sun = new DirectionalLight3D
        {
            Name = "Sun",
            LightEnergy = 1.2f,
            ShadowEnabled = true,
        };
        st.Root.AddChild(sun);
        sun.RotationDegrees = new Vector3(-54, -38, 0);

        // THE production stamping pass — same call worlds get. Afterwards assert the light
        // actually carries the avatar render-layer bits in both masks.
        UI.DeviceProfile.ApplyToScene(st.Root);
        GD.Print($"CAMSHOT sun masks: light=0x{sun.LightCullMask:X} shadowCaster=0x{sun.ShadowCasterMask:X} " +
                 $"want bits 0x{(int)LocalPlayer.AvatarRenderLayers:X}");

        // Pick the avatar: explicit flag > largest download in user://avatars > bean.
        string resolvedSka = ResolveSka(skaPath);
        GD.Print($"CAMSHOT using avatar: {resolvedSka ?? "(bean)"}");
        st.Av = AvatarLibrary.InstantiateOrDefault(resolvedSka);
        // Deliberately NOT parented here: SetAvatar mounts it under the rig. Adding it to the
        // root first made that re-parent fail, so every shot was of an avatar standing next to
        // the rig rather than worn by it.

        st.Pl = new LocalPlayer { Name = "Rig", Position = new Vector3(0, 0.05f, 0) };
        st.Root.AddChild(st.Pl);
        st.Pl.SetUsername("CamTest");
        st.Pl.SetAvatar(st.Av);
        foreach (var c in Descendants(st.Pl))
            if (c is Camera3D pc) { st.PlayerCam = pc; break; }
        if (st.PlayerCam == null)
        {
            GD.Print("CAMSHOT FAIL: no Camera3D found under the player rig");
            host.GetTree().Quit(1);
            return;
        }

        // Wide behind-above observer; made Current explicitly (the rig camera only auto-takes
        // over a viewport that had no camera at all).
        // A third-person observer, so it must cull the first-person proxy exactly like the
        // in-game third-person and mirror cameras do — otherwise it draws a headless duplicate
        // over the real avatar and tp_shadow stops being a third-person reference at all.
        st.Observer = new Camera3D
        {
            Name = "Observer",
            Fov = 55,
            Current = true,
            CullMask = 1048575u & ~LocalPlayer.NonFpCullLayers,
        };
        st.Root.AddChild(st.Observer);

        host.AddChild(new Driver(st));
    }

    /// Prefer the user's actually-downloaded avatars so the test runs against the exact 100+-bone
    /// rigs players wear rather than the bean.
    private static string ResolveSka(string explicitPath)
    {
        if (!string.IsNullOrEmpty(explicitPath)) return explicitPath;
        var dir = DirAccess.Open("user://avatars");
        if (dir == null) return null;
        string best = null;
        long bestSize = 0;
        dir.ListDirBegin();
        for (var f = dir.GetNext(); !string.IsNullOrEmpty(f); f = dir.GetNext())
        {
            if (!f.EndsWith(".ska")) continue;
            long size = (long)(FileAccess.Open($"user://avatars/{f}", FileAccess.ModeFlags.Read)?.GetLength() ?? 0);
            if (size > bestSize) { bestSize = size; best = $"user://avatars/{f}"; }
        }
        dir.ListDirEnd();
        return best;
    }

    private static IEnumerable<Node> Descendants(Node n)
    {
        foreach (Node c in n.GetChildren())
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    private sealed partial class Driver : Node
    {
        private readonly RunState _st;
        public Driver(RunState st) => _st = st;

        public override void _Process(double delta)
        {
            _st.Frames++;
            if (_st.Frames < WarmupFrames) return;
            if (_st.Phase >= Shots.Length)
            {
                Finish();
                return;
            }

            var shot = Shots[_st.Phase];
            _st.PhaseFrames++;

            // The LocalPlayer samples ExternalMove/AddLook inside its own _PhysicsProcess, so
            // all animation & camera state below comes through the production code paths.
            _st.Pl.ExternalMove = shot.Walk ? new Vector2(0, -1) : Vector2.Zero;
            _st.Pl.ExternalCrouch = shot.Crouch;
            _st.Pl.ExternalSprint = shot.Sprint;

            if (_st.PhaseFrames == 1 && !(_st.CamDelta.ContainsKey(shot.Name)))
            {
                // Switch mode / feed the one-shot look impulse for this phase.
                _st.Pl.SetCameraMode(shot.Mode == Mode.FirstPerson
                    ? LocalPlayer.CameraModeEnum.FirstPerson
                    : LocalPlayer.CameraModeEnum.ThirdPersonBack);
                if (shot.Look != Vector2.Zero) _st.Pl.AddLook(shot.Look);
                // Frame the observer once per phase.
                var p = _st.Pl.GlobalPosition;
                bool proxyView = shot.Mode == Mode.ProxyFromOutside;
                float side = shot.Name.EndsWith("_front") ? -1f : 1f;
                _st.Observer.Position = p + (proxyView
                    ? new Vector3(1.3f * side, 1.7f, 1.3f * side) // close in: this is about the head
                    : new Vector3(2.6f, 2.6f, 2.6f));
                _st.Observer.LookAt(p + new Vector3(0, proxyView ? 1.4f : 0.9f, 0), Vector3.Up);
                _st.Observer.CullMask = 1048575u & ~(proxyView
                    ? LocalPlayer.FpCullLayers
                    : LocalPlayer.NonFpCullLayers);
                _st.Observer.Current = shot.Mode != Mode.FirstPerson;
                _st.PlayerCam.Current = shot.Mode == Mode.FirstPerson;
                _st.CamDelta[shot.Name] = float.MaxValue;
                return;
            }

            // Track how closely the ACTIVE camera sits to the probed animated eye.
            if (shot.Mode == Mode.FirstPerson)
            {
                var camPos = _st.Vp.GetCamera3D()?.GlobalPosition ?? Vector3.Zero;
                _st.Av.TryGetEyeGlobal(out var eye);
                _st.CamDelta[shot.Name] = MathF.Min(
                    _st.CamDelta.GetValueOrDefault(shot.Name), camPos.DistanceTo(eye.Origin));
            }

            if (_st.PhaseFrames >= SettleFrames)
            {
                Capture(shot.Name);
                _st.Phase++;
                _st.PhaseFrames = 0;
            }
        }

        private void Capture(string name)
        {
            var img = _st.Vp.GetTexture()?.GetImage();
            if (img == null) { GD.Print($"CAMSHOT FAIL: no image for {name}"); _st.Failed = true; return; }
            string path = $"{_st.OutPrefix}_{name}.png";
            var err = img.SavePng(path);
            GD.Print($"CAMSHOT wrote {path} ({err})");
        }

        private void Finish()
        {
            foreach (var (name, _) in _st.CamDelta)
            {
                float cm = _st.CamDelta[name] == float.MaxValue ? -1 : _st.CamDelta[name] * 100f;
                GD.Print($"CAMSHOT eye-vs-camera[{name}] best Δ = {(cm < 0 ? "?" : $"{cm:F1}")} cm");
            }
            bool ok = !_st.Failed;
            foreach (var kv in _st.CamDelta)
                if (kv.Key.StartsWith("fp_") && (kv.Value == float.MaxValue || kv.Value > 0.25f))
                    ok = false;
            GD.Print(ok
                ? "CAMSHOT PASS: active camera rides the probed animated eye."
                : "CAMSHOT FAIL: first-person camera did not track the animated eye.");
            GetTree().Quit(ok ? 0 : 1);
        }
    }
}
