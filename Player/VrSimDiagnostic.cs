using System;
using System.Collections.Generic;
using Godot;
using SerikaSocial.Avatar;

namespace SerikaSocial.Player;

/// Rendered VR diagnostic driven by a simulated OpenXR device.
///
///   Godot --path game --windowed --audio-driver Dummy -- --serika-vrsim --ska &lt;path&gt; --out /tmp/vr
///
/// **How this differs from `--serika-vrtest`.** That one injects poses into `VrPlayer`'s nodes and
/// button states through `VrTestInput`, headless. It is a fast logic net and it stays. This one
/// installs a `VrSimDevice` — real `XRServer` trackers under the real OpenXR names — so
/// `XRCamera3D` binds to the head tracker, `XRController3D` reads inputs by action name, and
/// `VrHandTracking` finds actual `XRHandTracker`s. Everything from the tracker down is the
/// shipping code path, and because the headset camera is the scene's active camera in mono, every
/// phase can also be *looked at*: each writes a PNG from the player's own viewpoint.
///
/// That matters because a whole class of VR defect is invisible to numbers. "The menu is behind
/// me", "the panel is a black slab", "I can see the inside of my own head when I look down",
/// "my hands are two floating capsules" are all geometry that reads correctly in a transform and
/// wrongly in a headset. The observer shots catch the rest: they render the same instant from
/// outside, which is the only view where "where is my body actually standing" is answerable.
///
/// Needs a real display (`DISPLAY=:1` or `xvfb-run`) — headless renders nothing, so every capture
/// would be a blank image and every visual check would silently pass.
public static partial class VrSimDiagnostic
{
    /// Frames to let a phase settle before measuring and capturing. Long enough for the avatar's
    /// secondary physics and the UI panel's lazy follow to stop moving, both of which are
    /// deliberately damped and would otherwise be caught mid-transit in every shot.
    private const int SettleFrames = 24;

    /// Height of the table the headset is lying on before anyone picks it up. Matches the 1.15 m
    /// the device log showed being accepted as a player's eye height.
    private const float DeskHeight = 1.15f;

    public static void Run(Node host, string skaPath, string outPrefix)
    {
        outPrefix ??= "/tmp/vrsim";
        GD.Print($"VRSIM ska={skaPath ?? "(bean)"} out={outPrefix}");
        GD.Print("VRSIM note: OpenXR is not running. A simulated device publishes head, controller " +
                "and hand trackers to XRServer under the real names, so everything downstream of " +
                "tracking is the shipping path. Bindings, comfort and tracking quality still need " +
                "a headset.");

        var root = new Node3D { Name = "VrSimRoot" };
        host.AddChild(root);
        BuildWorld(root);

        // The panel must exist before any screen is built: `Brand` consults `VrUiSurface.Active`
        // while constructing, so a screen made before this reads as a desktop screen forever.
        UI.VrUiSurface.Active = true;
        var ui = new UI.VrUiSurface { Name = "VrUi" };
        root.AddChild(ui);

        var vr = new VrPlayer { Name = "VrRig", Position = new Vector3(0, 0.05f, 0) };
        vr.UiSurface = ui;
        root.AddChild(vr);

        var avatar = AvatarLibrary.InstantiateOrDefault(skaPath);
        if (avatar == null) { GD.Print("VRSIM FAIL: no avatar"); host.GetTree().Quit(1); return; }
        vr.SetAvatar(avatar);

        var device = new VrSimDevice();
        // The headset starts on a desk, because that is where a real one is when an app launches.
        // Starting the simulation with it already worn is what let the height-calibration bug ship:
        // the harness never presented the state the bug needed.
        device.Head.Origin = device.Head.Origin with { Y = DeskHeight };
        device.Install();

        host.AddChild(new Driver(vr, ui, root, device, outPrefix));
    }

    /// A room with unambiguous landmarks. Every wall is a different hue and the floor is a
    /// checkerboard, so a screenshot answers "which way am I facing" and "did I move" without
    /// needing the numbers next to it — which is the whole point of capturing them.
    private static void BuildWorld(Node3D root)
    {
        var floor = new StaticBody3D { Name = "Floor", Position = new Vector3(0, -0.5f, 0) };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(24, 1, 24) } });
        floor.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(24, 1, 24) },
            MaterialOverride = Flat(new Color(0.22f, 0.22f, 0.26f)),
        });
        root.AddChild(floor);

        // North is -Z (Godot's forward), so the player starts facing the blue wall.
        AddWall(root, new Vector3(0, 1.6f, -6f), new Vector3(12, 3.2f, 0.3f), new Color(0.25f, 0.45f, 0.95f)); // N blue
        AddWall(root, new Vector3(0, 1.6f, 6f), new Vector3(12, 3.2f, 0.3f), new Color(0.95f, 0.75f, 0.25f));  // S amber
        AddWall(root, new Vector3(-6f, 1.6f, 0), new Vector3(0.3f, 3.2f, 12), new Color(0.35f, 0.85f, 0.45f)); // W green
        AddWall(root, new Vector3(6f, 1.6f, 0), new Vector3(0.3f, 3.2f, 12), new Color(0.9f, 0.35f, 0.45f));   // E red

        // Parked off to the side, not in front. A prop within arm's reach of the spawn is also
        // within *walking* reach of it: the locomotion phases shouldered it along and were
        // deflected sideways, which read as a steering bug in the player rather than as the test
        // fixture standing in its own way. The grab phase brings it to hand when it needs it.
        var prop = new SerikaSocial.World.PhysicsProp { Name = "TestProp", Position = new Vector3(3.5f, 1.05f, 3.5f) };
        root.AddChild(prop);
    }

    private static void AddWall(Node3D root, Vector3 at, Vector3 size, Color color)
    {
        var wall = new StaticBody3D { Position = at };
        wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        wall.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = Flat(color) });
        root.AddChild(wall);
    }

    private static StandardMaterial3D Flat(Color c) => new()
    {
        AlbedoColor = c,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
    };

    // ---------------------------------------------------------------- the run

    private sealed partial class Driver : Node
    {
        private readonly VrPlayer _vr;
        private readonly UI.VrUiSurface _ui;
        private readonly Node3D _root;
        private readonly VrSimDevice _dev;
        private readonly string _out;

        /// An outside camera, so each phase can be seen from the player's eyes and from across the
        /// room. Half of what goes wrong in VR is only visible from one of the two.
        private Camera3D _observer;

        private readonly List<Phase> _phases = new();
        private int _index, _frames;
        private bool _ok = true;
        private readonly List<string> _failures = new();

        // Scratch carried between phases.
        private Vector3 _mark;
        private float _markYaw;
        private readonly Dictionary<string, string> _notes = new();

        public Driver(VrPlayer vr, UI.VrUiSurface ui, Node3D root, VrSimDevice dev, string outPrefix)
        {
            _vr = vr; _ui = ui; _root = root; _dev = dev; _out = outPrefix;
        }

        public override void _Ready()
        {
            _observer = new Camera3D
            {
                Name = "Observer",
                // The observer must see the third-person avatar, not the first-person cut-down
                // copy, or every outside shot shows a headless player and reads as a defect.
                CullMask = 1048575u & ~LocalPlayer.NonFpCullLayers,
                Position = new Vector3(2.6f, 1.9f, 2.6f),
            };
            _root.AddChild(_observer);
            _observer.LookAt(new Vector3(0, 1.1f, 0), Vector3.Up);

            // Without a light the unshaded landmarks still read, but the avatar does not.
            var sun = new DirectionalLight3D { Name = "Sun", RotationDegrees = new Vector3(-50, -35, 0) };
            _root.AddChild(sun);
            _root.AddChild(new WorldEnvironment
            {
                Environment = new Godot.Environment
                {
                    BackgroundMode = Godot.Environment.BGMode.Color,
                    BackgroundColor = new Color(0.05f, 0.05f, 0.08f),
                    AmbientLightSource = Godot.Environment.AmbientSource.Color,
                    AmbientLightColor = new Color(0.35f, 0.35f, 0.42f),
                    AmbientLightEnergy = 1.0f,
                },
            });

            // Subscribed before the phases run, so a binding that fires early is still counted.
            _vr.MenuPressed += () => _menuCount++;
            _vr.ActionMenuPressed += () => _actionCount++;
            _vr.MutePressed += () => _muteCount++;

            BuildPhases();
        }

        private int _menuCount, _actionCount, _muteCount;

        /// Set once the rig has left the floor. Sampled every frame rather than at the end of the
        /// phase: a jump is a brief arc, and by the time the settle period is over the player is
        /// back on the ground and indistinguishable from one who never jumped.
        private bool _jumped;
        private float _jumpPeak;

        // ── phase plumbing ───────────────────────────────────────────────────────────

        /// `Enter` runs once, `Check` runs after the settle period, and the phase then captures
        /// and moves on. Splitting them is what makes a capture meaningful: measuring on the frame
        /// an input changes catches the rig mid-response, which is how a working feature gets
        /// reported as broken.
        private sealed class Phase
        {
            public string Name;
            public Action Enter;
            /// Called every frame with the frame index, for phases whose input has to change part
            /// way through — a button tap is a press and a release, and a jump has to be pressed
            /// *after* the rig has landed or the press latches while it is still in the air.
            public Action<int> Update;
            public Action Check;
            public int Frames = SettleFrames;
            public bool Shoot = true;
            public bool Observe;
        }

        private void Add(string name, Action enter, Action check = null, bool shoot = true, bool observe = false,
                         Action<int> update = null, int frames = SettleFrames)
            => _phases.Add(new Phase
            {
                Name = name, Enter = enter, Update = update, Check = check,
                Frames = frames, Shoot = shoot, Observe = observe,
            });

        private void Fail(string what)
        {
            _ok = false;
            _failures.Add(what);
            GD.Print($"VRSIM FAIL {what}");
        }

        private void Expect(bool cond, string what)
        {
            if (cond) GD.Print($"VRSIM ok   {what}");
            else Fail(what);
        }

        private void Note(string key, string value)
        {
            _notes[key] = value;
            GD.Print($"VRSIM note {key} = {value}");
        }

        // ── the script ───────────────────────────────────────────────────────────────

        private void BuildPhases()
        {
            // ── height calibration ────────────────────────────────────────────────────
            //
            // This is the sequence that actually happens every time anyone launches the app, and
            // the one the first version of this harness never simulated: the headset is lying on a
            // desk when the scene loads, and gets picked up and put on some seconds later. The
            // shipped code latched the first plausible-looking reading and never looked again, so
            // it calibrated the player at the height of the table — 1.15 m on the device log — and
            // every tracked thing was then placed relative to a body half a metre too short. It
            // reads as broken tracking while tracking is perfect.
            Add("calib_desk", () =>
            {
                _dev.ClearInputs();
                _dev.LookAt(0);
                // Nothing to set up: the run already starts with the headset on the table.
                _dev.RestHands();
            }, () =>
            {
                Note("calib-desk", _vr.HeightCalibrated
                    ? $"calibrated to {_vr.MeasuredEyeHeight:0.00}m" : "not calibrated (correct)");
                Expect(!_vr.HeightCalibrated,
                       "CALIB: a headset sitting still on a desk is not accepted as the player's height");
            }, shoot: false, frames: 120);

            Add("calib_worn", () =>
            {
                _headStill = false;             // picked up and put on
                _baseHeadY = 1.65f;
            }, () =>
            {
                Note("calib-worn", $"{_vr.MeasuredEyeHeight:0.00}m");
                Expect(_vr.HeightCalibrated, "CALIB: a worn, moving headset is accepted");
                Expect(Mathf.Abs(_vr.MeasuredEyeHeight - 1.65f) < 0.06f,
                       "CALIB: the measured height is the height it is actually worn at");
            }, shoot: false, frames: 150);

            Add("boot", () =>
            {
                _dev.ClearInputs();
                _dev.LookAt(0);
                _dev.RestHands();
                _vr.HeadCamera.Current = true;
                _mark = _vr.GlobalPosition;
            }, () =>
            {
                // The camera has to actually be where the headset is. If `XRCamera3D` never bound
                // to the head tracker this reads as the play-space origin and every other visual
                // check in the run would be looking at the floor.
                float dy = Mathf.Abs(_vr.HeadCamera.GlobalPosition.Y - _dev.Head.Origin.Y);
                Note("head-y", $"{_vr.HeadCamera.GlobalPosition.Y:0.00}m (device {_dev.Head.Origin.Y:0.00})");
                Expect(dy < 0.35f, "HEAD: the XR camera follows the head tracker");

                // Standing perfectly still must not move the body. This is the drift bug that made
                // VR unplayable, and it only manifests when the head is off the play-space centre.
                float drift = new Vector2(_vr.GlobalPosition.X - _mark.X, _vr.GlobalPosition.Z - _mark.Z).Length();
                Note("idle-drift", $"{drift * 100f:0.0} cm over {SettleFrames} frames");
                Expect(drift < 0.05f, "DRIFT: standing still does not move the body");
            }, observe: true);

            Add("look_down", () => { _dev.LookAt(0, -55f); _dev.RestHands(); });
            Add("look_up", () => { _dev.LookAt(0, 35f); _dev.RestHands(); });

            Add("walk_forward", () =>
            {
                _dev.LookAt(0);
                _dev.RestHands();
                _mark = _vr.GlobalPosition;
                _dev.Stick[UI.DeviceProfile.Settings.VrMoveOnRightStick ? 1 : 0] = new Vector2(0, UI.DeviceProfile.Settings.VrInvertForward ? -1f : 1f);
            }, () =>
            {
                var moved = _vr.GlobalPosition - _mark;
                Note("walk", $"Δ=({moved.X:0.00},{moved.Z:0.00}) over {SettleFrames} frames");
                // Facing -Z, forward must be -Z and nothing sideways.
                Expect(moved.Z < -0.4f, "WALK: pushing the stick forward walks forward");
                Expect(Mathf.Abs(moved.X) < 0.15f, "WALK: forward does not drift sideways");
                _dev.ClearInputs();
            }, observe: true);

            Add("strafe_right", () =>
            {
                _mark = _vr.GlobalPosition;
                _dev.Stick[UI.DeviceProfile.Settings.VrMoveOnRightStick ? 1 : 0] = new Vector2(1f, 0);
            }, () =>
            {
                var moved = _vr.GlobalPosition - _mark;
                Note("strafe", $"Δ=({moved.X:0.00},{moved.Z:0.00})");
                Expect(moved.X > 0.4f, "STRAFE: pushing the stick right walks right");
                _dev.ClearInputs();
            });

            Add("snap_turn", () =>
            {
                _markYaw = _vr.GlobalRotation.Y;
                _dev.Stick[UI.DeviceProfile.Settings.VrMoveOnRightStick ? 0 : 1] = new Vector2(1f, 0);
            }, () =>
            {
                float turned = Mathf.RadToDeg(Mathf.AngleDifference(_markYaw, _vr.GlobalRotation.Y));
                Note("snap", $"{turned:0.0}° after one flick held {SettleFrames} frames");
                // One flick, one snap. Held past the cooldown a second is allowed, but the
                // magnitude must be a whole number of snap increments, never a smooth sweep.
                float per = UI.DeviceProfile.Settings.VrSnapTurnAngle;
                float steps = Mathf.Abs(turned) / Mathf.Max(1f, per);
                Expect(Mathf.Abs(turned) > per * 0.5f, "TURN: a stick flick turns the player");
                Expect(Mathf.Abs(steps - Mathf.Round(steps)) < 0.25f,
                       $"TURN: turning is in whole {per:0}° snaps, not a smooth sweep");
                _dev.ClearInputs();
            }, observe: true);

            Add("recentre", () =>
            {
                _dev.ClearInputs();
                _dev.LookAt(0);
                _dev.RestHands();
                _vr.GlobalPosition = new Vector3(0, 0.05f, 0);
                _vr.GlobalRotation = Vector3.Zero;
            }, shoot: false);

            // ── grabbing ──────────────────────────────────────────────────────────────
            Add("grab", () =>
            {
                var prop = _root.GetNodeOrNull<SerikaSocial.World.PhysicsProp>("TestProp");
                if (prop != null) prop.GlobalPosition = _dev.Hand[1].Origin;
                _dev.Grip[1] = 1f;
            }, () =>
            {
                Expect(_vr.GetHeldProp(1) != null, "GRAB: closing the grip picks up a prop in reach");
            }, observe: true);

            Add("release", () => { _dev.Grip[1] = 0f; }, () =>
            {
                Expect(_vr.GetHeldProp(1) == null, "GRAB: opening the grip lets go");
            }, shoot: false);

            // ── gestures, one phase each ──────────────────────────────────────────────
            foreach (var (name, thumb, trigger, grip, want) in GestureCases)
            {
                var n = name; var th = thumb; var tr = trigger; var gr = grip; var w = want;
                Add($"gesture_{n}", () =>
                {
                    _dev.ClearInputs();
                    _dev.RestHands();
                    // Thumb rest is reported as capacitive stick touch, not as a face-button press:
                    // the primary button is *jump*, so pressing it to signal "thumb down" would
                    // have the player hopping through every gesture in the table.
                    _dev.ThumbTouch[1] = th;
                    _dev.Trigger[1] = tr ? 1f : 0f;
                    _dev.Grip[1] = gr ? 1f : 0f;
                }, () =>
                {
                    var got = _vr.GetGesture(1);
                    Note($"gesture[{n}]", got.ToString());
                    Expect(got == w, $"GESTURE: thumb={(th ? "down" : "up")} trigger={tr} grip={gr} reads {w}");
                    _dev.ClearInputs();
                });
            }

            // ── the VRChat button layout ──────────────────────────────────────────────
            //
            // Worth testing as a unit rather than trusting the code to read correctly, because a
            // binding is a claim about which physical button does what, and the failure mode is
            // silent: a wrong action name reads zero forever and the button simply does nothing.
            Add("bind_jump", () =>
            {
                _dev.ClearInputs();
                _dev.RestHands();
                _vr.GlobalPosition = new Vector3(0, 0.05f, 0);
                _jumped = false;
                _jumpPeak = 0f;
            }, () =>
            {
                Note("jump", $"pressed={_dev.PrimaryButton[1]} onFloor={_vr.IsOnFloor()} " +
                              $"controls={_vr.ControlsEnabled} y={_vr.GlobalPosition.Y:0.00} " +
                              $"peak={_jumpPeak:0.00} vy={_vr.Velocity.Y:0.00}");
                Expect(_jumped, "BIND: A on the right controller jumps");
                _dev.ClearInputs();
            // Long enough to fall the 5 cm the phase teleports up, land, press, and reach apex.
            // At the default 24 frames this was marginal and passed only sometimes — a flaky
            // assertion is worse than no assertion, because it teaches you to ignore it.
            }, shoot: false, frames: 72, update: frame =>
            {
                // Press only once the rig is genuinely standing on the floor. Jump is edge-triggered
                // *and* ground-gated, so a press that begins in mid-air latches and is then ignored
                // for as long as it is held — correct behaviour, and a fixed frame number is not
                // good enough to avoid it: the phase teleports the rig 5 cm up and how long it
                // takes to land depends on the avatar's collider, which changes with the rig.
                if (!_dev.PrimaryButton[1] && _vr.IsOnFloor()) _dev.PrimaryButton[1] = true; // A
                _jumpPeak = Mathf.Max(_jumpPeak, _vr.GlobalPosition.Y);
                if (_vr.GlobalPosition.Y > 0.15f) _jumped = true;
            });

            Add("bind_mute", () => { _dev.ClearInputs(); _dev.PrimaryButton[0] = true; }, () =>
            {
                Expect(_muteCount == 1, $"BIND: X on the left controller mutes (fired {_muteCount}x)");
                _dev.ClearInputs();
            }, shoot: false);

            // A tap has to be *short*: held past the threshold it is a hold by definition, and a
            // press phase running the full settle period lasts 0.4 s, which is a hold.
            Add("bind_quickmenu_press", () => { _dev.ClearInputs(); _dev.SecondaryButton[1] = true; },
                shoot: false, frames: 4);
            Add("bind_quickmenu", () => { _dev.SecondaryButton[1] = false; }, () =>
            {
                Expect(_menuCount >= 1, "BIND: tapping B opens the quick menu");
                Expect(_actionCount == 0, "BIND: a tap does not also open the action menu");
            }, shoot: false);

            Add("bind_actionmenu", () => { _dev.ClearInputs(); _dev.SecondaryButton[1] = true; }, () =>
            {
                Expect(_actionCount >= 1, "BIND: holding B opens the action menu");
                _dev.ClearInputs();
            }, shoot: false);

            Add("bind_actionmenu_release", () =>
            {
                _menuCount = 0;
                _dev.ClearInputs();
            }, () =>
            {
                Expect(_menuCount == 0, "BIND: releasing after a hold does not also open the quick menu");
            }, shoot: false);

            Add("bind_stickclick", () =>
            {
                _actionCount = 0;
                _dev.ClearInputs();
                _dev.StickClick[1] = true;
            }, () =>
            {
                Expect(_actionCount == 1, $"BIND: clicking the stick opens the action menu (fired {_actionCount}x)");
                _dev.ClearInputs();
            }, shoot: false);

            // ── arm IK: do the avatar's hands go where the controllers are? ───────────
            //
            // This is the check the old rig had no way to make, and the one that matters most for
            // presence: an avatar whose arms hang at its sides while the player waves is not a
            // body, it is a puppet standing next to one. Measured against the hand *bone*, because
            // that is what everyone else sees — the controller pose being right proves nothing if
            // the solver never reached it.
            Add("hands_reach", () =>
            {
                _dev.ClearInputs();
                _dev.LookAt(0, -25f);   // look at your own hands, as you would to check them
                ReachOut();
            }, () =>
            {
                for (int i = 0; i < 2; i++) CheckArm(i, "reach");
            }, observe: true);

            Add("hands_wave", () =>
            {
                // Both hands up and out, the pose a player makes to greet someone.
                ReachOut(spread: 0.45f, up: 0.25f, forward: -0.35f);
            }, () =>
            {
                for (int i = 0; i < 2; i++) CheckArm(i, "wave");
            }, observe: true);

            // ── bare hands ────────────────────────────────────────────────────────────
            Add("hands_bare", () =>
            {
                _dev.ClearInputs();
                _dev.RestHands();
                // Controllers put down, cameras now see the hands.
                _dev.HandTracked[0] = _dev.HandTracked[1] = false;
                _dev.HandsVisible[0] = _dev.HandsVisible[1] = true;
                _dev.SetCurls(0, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f);
                _dev.SetCurls(1, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f);
            }, () =>
            {
                Expect(_vr.IsHandTracked(1), "HANDS: putting the controllers down engages optical tracking");
                Note("bare-gesture", _vr.GetGesture(1).ToString());
                Expect(_vr.GetGesture(1) == HandGesture.Open, "HANDS: an open hand reads as Open");
            }, observe: true);

            Add("hands_point", () =>
            {
                _dev.SetCurls(1, 0.9f, 0.05f, 0.95f, 0.95f, 0.95f);
            }, () =>
            {
                Note("point-gesture", _vr.GetGesture(1).ToString());
                Expect(_vr.GetGesture(1) == HandGesture.Point, "HANDS: index out, rest closed reads as Point");
            });

            Add("hands_off", () =>
            {
                _dev.HandsVisible[0] = _dev.HandsVisible[1] = false;
                _dev.HandTracked[0] = _dev.HandTracked[1] = true;
                _dev.ClearInputs();
                _dev.RestHands();
            }, () =>
            {
                Expect(!_vr.IsHandTracked(1), "HANDS: picking the controllers back up drops optical tracking");
            }, shoot: false);

            // ── the menu panel ────────────────────────────────────────────────────────
            Add("menu_open", () =>
            {
                _dev.ClearInputs();
                _dev.LookAt(0);
                _dev.RestHands();
                _vr.ControlsEnabled = false;
                _ui.FaceCamera(_vr.HeadCamera);
                MountProbeMenu();
                // Aim the right hand at the panel centre, which is straight ahead of the head.
                AimAtPanel(1);
            }, () =>
            {
                Expect(_ui.HasInteractiveUi, "MENU: a visible menu marks the panel as interactive");
                Expect(_vr.PointerVisible, "MENU: the laser appears when a menu is up");
                Expect(_probeHovered, "MENU: the controller ray reaches a button on the panel");

                // How big the panel actually is in the player's view, which is the only measure of
                // "is this menu readable" that means anything. Everything else — pixel size, panel
                // metres, distance — is an input to this one number.
                var cam = _vr.HeadCamera;
                float dist = cam.GlobalPosition.DistanceTo(_ui.Panel.GlobalPosition);
                float widthDeg = Mathf.RadToDeg(2f * Mathf.Atan(1.0f / Mathf.Max(0.01f, dist)));
                // `Camera3D.Fov` is the VERTICAL angle under the default keep-height aspect mode,
                // so comparing a horizontal subtense against it overstates how much of the view
                // the panel fills. Report the horizontal one the panel is actually competing with.
                var vpSize = GetViewport().GetVisibleRect().Size;
                float hFovDeg = Mathf.RadToDeg(2f * Mathf.Atan(
                    Mathf.Tan(Mathf.DegToRad(cam.Fov) * 0.5f) * (vpSize.X / Mathf.Max(1f, vpSize.Y))));
                Note("panel", $"{dist:0.00}m away, subtends {widthDeg:0}° of a {hFovDeg:0}° horizontal FOV");
                Expect(dist < 3.0f, "MENU: the panel sits within arm's reach, not across the room");
                Expect(widthDeg > 40f, "MENU: the panel is large enough in view to read");

                // Where the panel and the button on it actually land in pixels. The panel's metres
                // and the button's layout rect are two independent scales, and a menu that reads as
                // "tiny" can be either — the panel too far, or the UI drawn small on a correctly
                // sized panel. Only measuring both distinguishes them.
                var l = cam.UnprojectPosition(_ui.Panel.GlobalPosition + _ui.Panel.GlobalBasis.X * -1.0f);
                var r = cam.UnprojectPosition(_ui.Panel.GlobalPosition + _ui.Panel.GlobalBasis.X * 1.0f);
                Note("panel-px", $"quad spans {Mathf.Abs(r.X - l.X):0} px of {vpSize.X:0}");
                Note("viewport", $"render {_ui.Viewport.Size}, layout override {_ui.Viewport.Size2DOverride}, " +
                                 $"stretch={_ui.Viewport.Size2DOverrideStretch}");
                var rect = _probeButton.GetGlobalRect();
                Note("button-rect", $"{rect.Size.X:0}x{rect.Size.Y:0} at ({rect.Position.X:0},{rect.Position.Y:0}) " +
                                    $"in a {_ui.LogicalSize.X}x{_ui.LogicalSize.Y} logical panel");
            }, observe: true);

            // Press and release are separate phases because `Button.Pressed` fires on *release*:
            // asserting while the trigger is still held reports every working button as broken.
            Add("menu_press", () => { _dev.Trigger[1] = 1f; AimAtPanel(1); }, shoot: false);
            Add("menu_click", () => { _dev.Trigger[1] = 0f; AimAtPanel(1); }, () =>
            {
                Expect(_probeClicked, "MENU: pulling and releasing the trigger clicks the button under the ray");
                _dev.ClearInputs();
            });

            Add("menu_closed", () =>
            {
                _probeLayer.Visible = false;
                _vr.ControlsEnabled = true;
            }, () =>
            {
                Expect(!_ui.HasInteractiveUi, "MENU: closing the menu releases the panel");
                Expect(!_vr.PointerVisible, "MENU: the laser goes away with the menu");
            }, observe: true);
        }

        /// The VRChat gesture table, as the controller reports it: thumb resting on the
        /// controller, index on the trigger, remaining three on the grip. Every one of the seven
        /// shapes is a distinct point in that three-bit space.
        private static readonly (string name, bool thumb, bool trigger, bool grip, HandGesture want)[] GestureCases =
        {
            ("neutral",   true,  false, false, HandGesture.Neutral),
            ("open",      false, false, false, HandGesture.Open),
            ("fist",      true,  true,  true,  HandGesture.Fist),
            ("point",     true,  false, true,  HandGesture.Point),
            ("thumbsup",  false, true,  true,  HandGesture.ThumbsUp),
            ("gun",       false, false, true,  HandGesture.Gun),
            ("rock",      true,  true,  false, HandGesture.RockNRoll),
            ("peace",     false, true,  false, HandGesture.Peace),
        };

        // ── the probe menu ───────────────────────────────────────────────────────────

        private CanvasLayer _probeLayer;
        private Button _probeButton;
        private bool _probeHovered, _probeClicked;

        /// A stand-in menu with one large button, mounted the way `Main.AddUi` mounts a real
        /// screen. A real screen would drag in the whole session, and what is under test here is
        /// the *routing* — panel visibility, ray hit, viewport coordinate mapping, synthetic mouse
        /// events — which is identical for every screen in `UI/`.
        private void MountProbeMenu()
        {
            if (_probeLayer != null) { _probeLayer.Visible = true; return; }

            _probeLayer = new CanvasLayer { Name = "ProbeMenu" };
            var panel = new PanelContainer
            {
                AnchorRight = 1, AnchorBottom = 1,
                OffsetLeft = 240, OffsetTop = 160, OffsetRight = -240, OffsetBottom = -160,
            };
            _probeButton = new Button { Text = "CLICK TARGET" };
            _probeButton.MouseEntered += () => _probeHovered = true;
            _probeButton.Pressed += () => _probeClicked = true;
            panel.AddChild(_probeButton);
            _probeLayer.AddChild(panel);
            _ui.Viewport.AddChild(_probeLayer);
        }

        /// Put both controllers out in front of the head, where a player checking their own hands
        /// would hold them. Offsets are relative to the head, in its own yaw frame.
        private void ReachOut(float spread = 0.28f, float up = -0.28f, float forward = -0.42f)
        {
            var yaw = Basis.FromEuler(new Vector3(0, _dev.Head.Basis.GetEuler().Y, 0));
            for (int i = 0; i < 2; i++)
            {
                float x = i == 0 ? -spread : spread;
                _dev.Hand[i] = new Transform3D(yaw, _dev.Head.Origin + yaw * new Vector3(x, up, forward));
            }
        }

        /// World-space position of the avatar's hand bone, which is what peers and mirrors see.
        private bool TryHandBone(int i, out Vector3 world)
        {
            world = default;
            var avatar = _vr.Avatar;
            var skel = avatar?.Skeleton;
            if (skel == null) return false;
            int bone = avatar.BoneOf(i == 0 ? "leftHand" : "rightHand");
            if (bone < 0) return false;
            world = skel.GlobalTransform * skel.GetBoneGlobalPose(bone).Origin;
            return true;
        }

        /// Does the avatar's arm follow the player's arm?
        ///
        /// Deliberately *not* "is the hand bone on the controller". The avatar's arm is shorter
        /// than the player's — 43 cm against ~60 cm on a typical stylised rig — so on any real
        /// reach that distance can never go to zero, and asserting it does marks a correct solve as
        /// a failure. What has to hold is proportionality: the arm points where the player's arm
        /// points, and is extended as far as the player's is. Then full extension reads as full
        /// extension and there is elbow bend everywhere else, which is what the player sees.
        private void CheckArm(int i, string label)
        {
            string side = i == 0 ? "L" : "R";
            if (!TryHandBone(i, out var bone)) { Fail($"IK[{label}]: no {side} hand bone"); return; }
            if (!TryShoulder(i, out var shoulder, out float avatarReach)) { Fail($"IK[{label}]: no {side} shoulder"); return; }

            var target = (i == 0 ? _vr.LeftHand : _vr.RightHand).GlobalPosition;
            var want = target - shoulder;
            var got = bone - shoulder;
            if (want.LengthSquared() < 1e-6f || got.LengthSquared() < 1e-6f) { Fail($"IK[{label}]: {side} degenerate"); return; }

            float angle = Mathf.RadToDeg(want.Normalized().AngleTo(got.Normalized()));

            // Aim is the one thing that must hold under either setting: the avatar's arm points
            // where the player's arm points, or nothing else about the pose matters.
            Expect(angle < 12f, $"IK[{label}]: the {side} arm points where the player's arm points");

            if (UI.DeviceProfile.Settings.VrArmScaling)
            {
                // Scaled: the hand deliberately does NOT sit on the controller, so co-location is
                // the wrong assertion. What must hold is proportional extension — full player
                // extension is full avatar extension.
                float playerReach = _dev.Head.Origin.Y / 0.93f * 0.36f;
                float wantExt = Mathf.Min(1f, want.Length() / playerReach);
                float gotExt = Mathf.Min(1f, got.Length() / avatarReach);
                Note($"ik-{label}[{side}]", $"aim off by {angle:0.0}°, extension {gotExt:0.00} vs player {wantExt:0.00}");
                Expect(Mathf.Abs(gotExt - wantExt) < 0.15f,
                       $"IK[{label}]: the {side} arm is extended as far as the player's");
                return;
            }

            // Unscaled — the default. The hand must land ON the controller whenever the arm is
            // long enough to get there, because that co-location is what the player sees in first
            // person. Where the target is genuinely out of reach the arm may fall short, but it
            // must be fully extended toward it rather than hanging somewhere convenient.
            float err = bone.DistanceTo(target);
            float shortfall = Mathf.Max(0f, want.Length() - avatarReach);
            Note($"ik-{label}[{side}]", $"aim off by {angle:0.0}°, hand {err * 100f:0.0} cm from the " +
                                       $"controller, unreachable by {shortfall * 100f:0.0} cm");
            if (shortfall < 0.02f)
                Expect(err < 0.05f, $"IK[{label}]: the {side} hand sits on the controller when it can reach");
            else
                Expect(got.Length() > avatarReach - 0.04f,
                       $"IK[{label}]: the {side} arm extends fully toward an out-of-reach target");
        }

        /// The avatar's shoulder in world space, and how far that arm can physically reach.
        private bool TryShoulder(int i, out Vector3 world, out float reach)
        {
            world = default; reach = 0f;
            var avatar = _vr.Avatar;
            var skel = avatar?.Skeleton;
            if (skel == null) return false;
            int upper = avatar.BoneOf(i == 0 ? "leftUpperArm" : "rightUpperArm");
            int lower = avatar.BoneOf(i == 0 ? "leftLowerArm" : "rightLowerArm");
            int hand = avatar.BoneOf(i == 0 ? "leftHand" : "rightHand");
            if (upper < 0 || lower < 0 || hand < 0) return false;

            // Measured on the REST pose: the current pose is whatever the solver just produced, so
            // measuring segment lengths from it would report the arm as exactly long enough every
            // time, however far it actually fell short.
            var u = skel.GetBoneGlobalRest(upper).Origin;
            var l = skel.GetBoneGlobalRest(lower).Origin;
            var h = skel.GetBoneGlobalRest(hand).Origin;
            reach = u.DistanceTo(l) + l.DistanceTo(h);
            world = skel.GlobalTransform * skel.GetBoneGlobalPose(upper).Origin;
            return true;
        }

        /// Point hand `i` at the centre of the UI panel from where it currently is.
        private void AimAtPanel(int i)
        {
            var from = _vr.GlobalTransform.AffineInverse() * _ui.Panel.GlobalPosition;
            _ = from;
            var handWorld = (i == 0 ? _vr.LeftHand : _vr.RightHand).GlobalPosition;
            var target = _ui.Panel.GlobalPosition;
            var dir = (target - handWorld).Normalized();
            // The controller's aim axis is -Z, so build a basis whose -Z is `dir`.
            var basis = Basis.LookingAt(dir, Vector3.Up);
            // `Hand` is play-space local; convert the world-space aim back through the origin.
            var originInv = _vr.HeadCamera.GetParent<Node3D>().GlobalTransform.AffineInverse();
            _dev.Hand[i] = new Transform3D((originInv.Basis * basis).Orthonormalized(),
                                           originInv * handWorld);
        }

        // ── the loop ─────────────────────────────────────────────────────────────────

        /// A worn headset is never still — a head micro-bobs constantly, and that motion is one of
        /// the two signals height calibration uses to tell a head from a table. Every phase
        /// therefore gets a little life by default; `_headStill` opts out, which is how the desk
        /// case is simulated.
        private bool _headStill = true;
        private float _headBobPhase;

        private void ApplyHeadLife(float dt)
        {
            if (_headStill) return;
            _headBobPhase += dt * 3.1f;
            // Just over a centimetre peak to peak, which is what quiet standing really looks
            // like — and comfortably above the liveness floor that distinguishes a head from a table.
            _dev.Head.Origin = _dev.Head.Origin with { Y = _baseHeadY + Mathf.Sin(_headBobPhase) * 0.012f };
        }

        /// The height the current phase wants the headset at, before the bob is added.
        private float _baseHeadY = DeskHeight;

        public override void _Process(double delta)
        {
            ApplyHeadLife((float)delta);
            _dev.Commit();

            if (_index >= _phases.Count) { Finish(); return; }
            var phase = _phases[_index];

            if (_frames == 0) phase.Enter?.Invoke();
            phase.Update?.Invoke(_frames);
            _frames++;

            if (_frames < phase.Frames) return;

            phase.Check?.Invoke();
            if (phase.Shoot) Capture(phase.Name, phase.Observe);

            _index++;
            _frames = 0;
        }

        private void Capture(string name, bool alsoObserve)
        {
            var vp = GetViewport();

            _vr.HeadCamera.Current = true;
            _observer.Current = false;
            Save(vp, $"{_out}_{name}_eye.png");

            if (!alsoObserve) return;
            // Two captures in one frame would both read the same already-rendered buffer, so the
            // observer shot is taken on the next pass through — done by leaving the camera
            // switched and capturing at the top of the following phase would desync the names.
            // Force a redraw instead, which is exactly what the capture below needs.
            _observer.Current = true;
            RenderingServer.ForceDraw();
            Save(vp, $"{_out}_{name}_obs.png");
            _vr.HeadCamera.Current = true;
        }

        private void Save(Viewport vp, string path)
        {
            var img = vp.GetTexture()?.GetImage();
            if (img == null) { Fail($"CAPTURE: no image for {path}"); return; }
            var err = img.SavePng(path);
            if (err != Error.Ok) Fail($"CAPTURE: {path} ({err})");
            else GD.Print($"VRSIM wrote {path}");
        }

        private void Finish()
        {
            GD.Print("VRSIM ── summary ─────────────────────────────────────────────");
            foreach (var (k, v) in _notes) GD.Print($"VRSIM   {k} = {v}");
            if (_failures.Count > 0)
            {
                GD.Print($"VRSIM {_failures.Count} failure(s):");
                foreach (var f in _failures) GD.Print($"VRSIM   - {f}");
            }
            GD.Print(_ok ? "VRSIM PASS" : "VRSIM FAIL");
            _dev.Remove();
            GetTree().Quit(_ok ? 0 : 1);
        }
    }
}
