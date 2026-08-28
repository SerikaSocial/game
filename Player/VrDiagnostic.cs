using System;
using System.Collections.Generic;
using Godot;
using SerikaSocial.Avatar;

namespace SerikaSocial.Player;

/// Headless VR diagnostic — drives a real `VrPlayer` without a headset.
///
///   Godot --headless --path game -- --serika-vrtest [--ska &lt;path&gt;]
///
/// **Why this exists.** OpenXR cannot initialise on a machine with no HMD ("Failed to enumerate
/// number of extension properties"), so VR changes are otherwise unverifiable until an APK
/// reaches a headset. Everything that actually broke VR, though, was ordinary geometry and input
/// logic sitting downstream of tracking, and that is all testable: the rig is instantiated for
/// real, its camera and controller nodes are posed by hand the way OpenXR would pose them, and
/// the rig's own `_PhysicsProcess` runs on the real physics tick.
///
/// What this canNOT tell you: whether bindings land on the buttons you expect on a given
/// controller, whether the image is comfortable, or anything about tracking quality. Those still
/// need a Quest build. This is a regression net for the logic, not a substitute for wearing it.
///
/// The checks correspond one-to-one with the audited defects:
///
///   ROOT   — the broadcast pose root must be the avatar's FEET, not the headset. Sending the
///            headset put every VR player a head-height in the air for all of their peers.
///   SPRINT — squeezing the left grip (how props are grabbed) must not also make you run.
///   JUMP   — holding the jump button must produce exactly one jump, not one per frame.
///   STICK  — stick locomotion must move the body.
///   WALL   — physically walking in the guardian must be stopped by world collision.
///   ORIENT — hand-relative movement must follow the CONTROLLER's heading, not the head's.
///   DASH   — the right stick must teleport in smooth mode, and must not do so while turning.
///   GEST   — the gesture classifier must name each canonical hand shape, and must hold its last
///            answer in the dead space between shapes rather than strobing.
///   FINGER — applying a curl must actually fold the avatar's fingertip toward its palm.
///   WIRE   — that curl must survive Encode→Decode→ApplyBonePose onto a second rig at LOD0, and
///            must NOT appear at LOD1. Fingers live at wire indices 25-54, which only ride in a
///            LOD0 frame; the client sent LOD1 exclusively until v1.6.5, which is exactly why
///            gestures used to stop at the sender's own eyes.
///   PANEL  — the UI panel (and so the laser) must be idle unless a real menu is on it. HUD
///            chrome must not count, which is what pinned the panel and laser on permanently.
public static partial class VrDiagnostic
{
    // Far out of the way. The wall used to sit at z=-1.5, which put it directly in the path of
    // the locomotion phases: the rig walked into it, MoveAndSlide zeroed the velocity being
    // measured, and both sprint and stick reported motionless. The WALL phase teleports the rig
    // over here when it needs it.
    private const float WallZ = -20f;

    public static void Run(Node host, string skaPath)
    {
        GD.Print($"VRTEST ska={skaPath ?? "(bean)"}");
        GD.Print("VRTEST note: OpenXR is not running (no HMD). Tracking poses and button states " +
                "are injected; this covers rig geometry and input logic only, not bindings, " +
                "comfort or tracking quality.");

        var root = new Node3D { Name = "VrTestRoot" };
        host.AddChild(root);
        BuildWorld(root);

        var vr = new VrPlayer { Name = "VrRig", Position = new Vector3(0, 0.05f, 0) };
        root.AddChild(vr);

        var avatar = AvatarLibrary.InstantiateOrDefault(skaPath);
        if (avatar == null) { GD.Print("VRTEST FAIL: no avatar"); host.GetTree().Quit(1); return; }
        vr.SetAvatar(avatar);

        VrTestInput.Active = true;
        host.AddChild(new Driver(vr, skaPath));
    }

    private static void BuildWorld(Node3D root)
    {
        var floor = new StaticBody3D { Name = "Floor", Position = new Vector3(0, -0.5f, 0) };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(40, 1, 40) } });
        root.AddChild(floor);

        var wall = new StaticBody3D { Name = "Wall", Position = new Vector3(0, 2, WallZ - 0.2f) };
        wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(20, 4, 0.4f) } });
        root.AddChild(wall);
    }

    /// Runs the checks as a sequence of phases on the real physics tick.
    ///
    /// A driver node rather than a plain loop because `MoveAndSlide` and `MoveAndCollide` may only
    /// be called from inside physics processing — stepping `VrPlayer` by hand from `_Ready` would
    /// trip Godot's own guard on the first frame.
    private sealed partial class Driver : Node
    {
        private readonly VrPlayer _vr;
        // The rig under test, so the WIRE check can build a second one to receive the frame.
        private readonly string _skaPath;
        private readonly Node3D _cam, _left, _right;
        private readonly XROrigin3D _playSpace;
        private readonly List<Phase> _phases = new();
        private int _index, _frames;
        private bool _ok = true;

        // Injected headset pose, in play-space local coordinates.
        private Vector3 _head = new(0, 1.62f, 0);

        // Scratch shared between phases.
        private float _grippedSpeed, _relaxedSpeed, _rimSpeed;
        private Vector3 _stickStart;
        private int _takeoffs;
        private bool _airborne;

        // Injected controller headings, in play-space local coordinates.
        private float _leftYaw;    // yaws the left controller for the hand-relative movement check
        private float _rightPitch; // tips the right controller down so the dash arc finds the floor

        private sealed class Phase
        {
            public string Name;
            public Action Enter;
            public Action<int> Tick;   // called each frame with the frame index
            public int Frames;
            public Action Exit;
        }

        public Driver(VrPlayer vr, string skaPath)
        {
            _vr = vr;
            _skaPath = skaPath;
            _cam = vr.HeadCamera;
            _left = vr.LeftHand;
            _right = vr.RightHand;
            // The play space, so phases can start from a clean room offset — see Recentre.
            _playSpace = _cam.GetParent<XROrigin3D>();
            // Run ahead of the rig each tick, so the injected headset pose and button state are
            // in place before VrPlayer reads them rather than a frame behind.
            ProcessPhysicsPriority = -100;
            Build();
        }

        private void Add(string name, Action enter, int frames, Action exit = null, Action<int> tick = null)
            => _phases.Add(new Phase { Name = name, Enter = enter, Frames = frames, Exit = exit, Tick = tick });

        private void Build()
        {
            // Settle on the floor before measuring anything.
            Add("settle", () => { }, 40);

            // ── ROOT ─────────────────────────────────────────────────────────────────
            Add("root", () => { }, 10, () =>
            {
                var rootPos = _vr.PoseTransform().Origin;
                float feetY = _vr.GlobalPosition.Y;
                float headY = _cam.GlobalPosition.Y;
                float aboveFeet = rootPos.Y - feetY;

                // Generous: the avatar mount sits at body height, so any sane answer is within a
                // few cm of the feet. The failure this catches was off by a whole avatar height.
                bool ok = Mathf.Abs(aboveFeet) < 0.15f;
                _ok &= ok;
                GD.Print($"VRTEST ROOT   broadcast root Y={rootPos.Y:F3}, feet Y={feetY:F3}, " +
                         $"head Y={headY:F3} → {aboveFeet * 100f:F1} cm above the feet  " +
                         $"{(ok ? "ok" : $"FAIL — peers would see this avatar floating {aboveFeet:F2} m up")}");
            });

            // ── SPRINT ───────────────────────────────────────────────────────────────
            // Half-stick forward with the left grip fully squeezed: a player walking while
            // carrying something. Must be walk speed, and must match the ungripped case.
            Add("sprint-gripped",
                () => { Recentre(); VrTestInput.LeftStick = new Vector2(0, -0.5f); VrTestInput.LeftGrip = 1f; },
                30, () => _grippedSpeed = Planar(_vr.Velocity));

            Add("sprint-relaxed",
                () => { Recentre(); VrTestInput.LeftGrip = 0f; },
                30, () => _relaxedSpeed = Planar(_vr.Velocity));

            // Stick to the rim is the sprint gesture now, and must be clearly faster.
            Add("sprint-rim",
                () => { Recentre(); VrTestInput.LeftStick = new Vector2(0, -1f); },
                30, () =>
                {
                    _rimSpeed = Planar(_vr.Velocity);
                    VrTestInput.LeftStick = Vector2.Zero;

                    bool ok = Mathf.Abs(_grippedSpeed - _relaxedSpeed) < 0.05f
                              && _rimSpeed > _relaxedSpeed + 0.5f;
                    _ok &= ok;
                    GD.Print($"VRTEST SPRINT half-stick gripped={_grippedSpeed:F2} m/s, " +
                             $"relaxed={_relaxedSpeed:F2} m/s, rim={_rimSpeed:F2} m/s  " +
                             $"{(ok ? "ok — grab and sprint are independent" : "FAIL — grip still drives sprint")}");
                });

            // ── JUMP ─────────────────────────────────────────────────────────────────
            Add("jump-settle", () => { }, 40);
            Add("jump",
                () => { VrTestInput.JumpHeld = true; _takeoffs = 0; _airborne = false; },
                240,
                () =>
                {
                    VrTestInput.JumpHeld = false;
                    bool ok = _takeoffs == 1;
                    _ok &= ok;
                    GD.Print($"VRTEST JUMP   button held 240 frames → {_takeoffs} takeoff(s)  " +
                             $"{(ok ? "ok" : "FAIL — jump is not edge-latched, the player pogos")}");
                },
                _ =>
                {
                    bool up = _vr.Velocity.Y > 1f;
                    if (up && !_airborne) _takeoffs++;
                    _airborne = up;
                });

            // ── STICK ────────────────────────────────────────────────────────────────
            Add("stick-settle", () => { }, 60);
            Add("stick",
                () =>
                {
                    Recentre();
                    _stickStart = _vr.GlobalPosition;
                    VrTestInput.LeftStick = new Vector2(0, -0.6f);
                },
                60, () =>
                {
                    VrTestInput.LeftStick = Vector2.Zero;
                    var travelled = (_vr.GlobalPosition - _stickStart) with { Y = 0 };
                    bool ok = travelled.Length() > 0.5f;
                    _ok &= ok;
                    GD.Print($"VRTEST STICK  60 frames of forward stick → travelled " +
                             $"{travelled.Length():F2} m  " +
                             $"{(ok ? "ok" : "FAIL — stick locomotion does not move the body")}");
                });

            // ── WALL ─────────────────────────────────────────────────────────────────
            // Park next to a wall, then walk the headset straight through it — what happens when
            // a player physically crosses their guardian toward a virtual wall.
            Add("wall-place", () =>
            {
                _vr.GlobalPosition = new Vector3(0, 0.05f, WallZ + 0.9f);
                _vr.Velocity = Vector3.Zero;
                _head = new Vector3(0, 1.62f, 0);
            }, 30);

            Add("wall", () => { }, 40,
                () =>
                {
                    float z = _vr.GlobalPosition.Z;
                    bool ok = z > WallZ;
                    _ok &= ok;
                    GD.Print($"VRTEST WALL   walked 2.4 m into a wall at z={WallZ:F2} → body " +
                             $"stopped at z={z:F2}  " +
                             $"{(ok ? "ok" : "FAIL — room-scale walking passes through geometry")}");
                },
                _ => _head.Z -= 0.06f);

            // ── ORIENT ───────────────────────────────────────────────────────────────
            // Hand-relative movement: yaw the left controller 90° away from the head and walk
            // forward. Travel must follow the controller, not the gaze.
            //
            // The check is angular rather than axis-based on purpose — asserting "travel is along
            // -X" bakes in a sign that depends on Godot's basis conventions, and getting that
            // wrong writes a test that passes on a bug. Comparing travel against the two candidate
            // headings measures the thing the setting actually promises.
            Add("orient-place", () =>
            {
                Recentre();
                UI.DeviceProfile.Settings.VrMoveOrientation = UI.DeviceProfile.Settings.MoveOrientation.Hand;
                _leftYaw = Mathf.Pi * 0.5f;
                _stickStart = _vr.GlobalPosition;
            }, 5);

            Add("orient", () => VrTestInput.LeftStick = new Vector2(0, -0.8f), 60, () =>
            {
                VrTestInput.LeftStick = Vector2.Zero;
                UI.DeviceProfile.Settings.VrMoveOrientation = UI.DeviceProfile.Settings.MoveOrientation.Head;

                var travel = (_vr.GlobalPosition - _stickStart) with { Y = 0 };
                var headFwd = (-_cam.GlobalTransform.Basis.Z with { Y = 0 }).Normalized();
                var handFwd = (-_left.GlobalTransform.Basis.Z with { Y = 0 }).Normalized();

                if (travel.Length() < 0.2f)
                {
                    _ok = false;
                    GD.Print($"VRTEST ORIENT hand-relative walk travelled only {travel.Length():F2} m  " +
                             "FAIL — no movement to measure a heading from");
                }
                else
                {
                    var dir = travel.Normalized();
                    float toHand = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(dir.Dot(handFwd), -1f, 1f)));
                    float toHead = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(dir.Dot(headFwd), -1f, 1f)));
                    bool ok = toHand < 20f && toHead > 60f;
                    _ok &= ok;
                    GD.Print($"VRTEST ORIENT travelled {travel.Length():F2} m, {toHand:F1}° off the " +
                             $"controller heading and {toHead:F1}° off the gaze  " +
                             $"{(ok ? "ok — hand-relative movement follows the hand" : "FAIL — movement ignores the controller heading")}");
                }
                _leftYaw = 0f;
            });

            // ── DASH ─────────────────────────────────────────────────────────────────
            // Right stick forward must blink the player across the room in smooth mode; a
            // diagonal flick must turn instead, or every turn becomes an accidental teleport.
            Add("dash-place", () =>
            {
                Recentre();
                UI.DeviceProfile.Settings.VrLocomotion = UI.DeviceProfile.Settings.Locomotion.Smooth;
                UI.DeviceProfile.Settings.VrDashTeleport = true;
                // Aim the right controller down-forward so the arc lands on the floor ahead.
                _rightPitch = -Mathf.Pi * 0.12f;
                _stickStart = _vr.GlobalPosition;
            }, 10);

            // Hold forward to aim, then release: the teleport commits on release.
            Add("dash-aim", () => VrTestInput.RightStick = new Vector2(0, -1f), 20);
            Add("dash-fire", () => VrTestInput.RightStick = Vector2.Zero, 10, () =>
            {
                _dashTravel = ((_vr.GlobalPosition - _stickStart) with { Y = 0 }).Length();
            });

            Add("dash-turn-place", () =>
            {
                Recentre();
                _stickStart = _vr.GlobalPosition;
                _startYaw = _vr.GlobalRotation.Y;
            }, 10);
            // A diagonal: full forward AND full sideways. Turning owns this, so no teleport.
            Add("dash-turn-aim", () => VrTestInput.RightStick = new Vector2(1f, -1f).Normalized(), 20);
            Add("dash-turn-fire", () => VrTestInput.RightStick = Vector2.Zero, 10, () =>
            {
                float turnTravel = ((_vr.GlobalPosition - _stickStart) with { Y = 0 }).Length();
                float turned = Mathf.RadToDeg(Mathf.Abs(Mathf.AngleDifference(_startYaw, _vr.GlobalRotation.Y)));
                _rightPitch = 0f;

                // The diagonal must TURN, and must not teleport. It is not checked against zero
                // travel, because turning legitimately moves the body a little: `RotateAroundHead`
                // pivots about the headset rather than the body origin (deliberately — pivoting
                // about the origin swings the player through an arc that feels like a fairground
                // ride), so the body swings around that pivot. What distinguishes the two is scale:
                // a dash crosses metres, a turn's arc is a fraction of one.
                bool ok = _dashTravel > 1f && turned > 20f && turnTravel < _dashTravel * 0.5f;
                _ok &= ok;
                GD.Print($"VRTEST DASH   forward flick → {_dashTravel:F2} m travelled; diagonal " +
                         $"flick → turned {turned:F0}° and travelled {turnTravel:F2} m  " +
                         $"{(ok ? "ok — dash fires forward only, diagonals turn instead" : "FAIL — dash either does not fire or fires while turning")}");
            });

            // ── GEST ─────────────────────────────────────────────────────────────────
            Add("gesture", () => { }, 1, CheckGestures);

            // ── FINGER ───────────────────────────────────────────────────────────────
            Add("finger", () => { }, 1, CheckFingers);

            // ── PANEL ────────────────────────────────────────────────────────────────
            Add("panel-build", BuildPanel, 10);
            Add("panel-idle", () => { }, 10, () => _panelIdle = _surface.HasInteractiveUi);
            Add("panel-menu", () => _menuLayer.Visible = true, 10, () =>
            {
                bool panelMenu = _surface.HasInteractiveUi;
                // With no OpenXR runtime the controllers report no tracking data, so the laser is
                // suppressed by the tracking check regardless of the panel — only the negative
                // case is observable headless. It is also the case that regressed.
                bool laserIdle = !_vr.PointerVisible;

                bool ok = !_panelIdle && panelMenu && laserIdle;
                _ok &= ok;
                GD.Print($"VRTEST PANEL  chrome-only → interactive={_panelIdle}, " +
                         $"menu open → interactive={panelMenu}, laser while idle drawn={!laserIdle}  " +
                         $"{(ok ? "ok — the panel and pointer track real menus only" : "FAIL — HUD chrome still counts as a menu")}");
            });

            // ── KEYBD ────────────────────────────────────────────────────────────────
            // The VR keyboard only appears while a LineEdit holds focus, and the only way to
            // focus one in a headset is a synthetic mouse click pushed into the panel viewport
            // by the controller ray. That link had never been tested.
            //
            // Deliberately last: it has to put a visible field on the panel, which would poison
            // the "idle with only chrome" measurement above if it ran first.
            Add("keybd-click", PushClickAtField, 5, CheckFieldFocused);
            // The layout is rebuilt on every shift / 123 / symbols press, and the frees it issues
            // only take effect at end of frame — so a second Show() must land on a LATER frame to
            // catch a rebuild that deleted something it then reuses.
            // Each Show() must land on its own frame. `QueueFree` does not take effect until the
            // end of the frame it is called in, so a rebuild that frees the preview and re-adds it
            // in the same tick looks fine; the object only becomes disposed once a frame boundary
            // passes, and the throw lands on the NEXT rebuild or the next `_Process`.
            Add("keybd-build2", () => SafeShow("second"), 10);
            Add("keybd-build3", () => SafeShow("third"), 10, ReportKeyboardRebuild);
        }

        private float _dashTravel;
        private float _startYaw;
        private bool _panelIdle;
        private UI.VrUiSurface _surface;
        private CanvasLayer _menuLayer;

        private LineEdit _testField;

        /// Put a text field on the panel and click it the way the controller ray would.
        private void PushClickAtField()
        {
            if (_surface == null || _menuLayer == null) return;
            _menuLayer.Visible = true;

            var root = new Control { Name = "FieldRoot" };
            root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _menuLayer.AddChild(root);

            _testField = new LineEdit
            {
                Name = "TestField",
                Position = new Vector2(100, 100),
                Size = new Vector2(400, 48),
            };
            root.AddChild(_testField);

            // Centre of the field, in the panel's LOGICAL coordinates — the same space
            // `VrUiSurface.WorldToViewport` produces and `VrPlayer.UpdatePointer` pushes.
            var at = _testField.Position + _testField.Size * 0.5f;
            foreach (bool down in new[] { true, false })
            {
                _surface.Viewport.PushInput(new InputEventMouseButton
                {
                    Position = at,
                    GlobalPosition = at,
                    ButtonIndex = MouseButton.Left,
                    ButtonMask = down ? MouseButtonMask.Left : 0,
                    Pressed = down,
                }, true);
            }
        }

        private void CheckFieldFocused()
        {
            if (_testField == null) { GD.Print("VRTEST KEYBD  no panel — SKIPPED"); return; }

            var focused = _surface.Viewport.GuiGetFocusOwner();
            bool ok = focused == _testField;
            _ok &= ok;
            GD.Print($"VRTEST KEYBD  click at the field → focus owner is " +
                     $"{(focused == null ? "nothing" : focused.Name.ToString())}  " +
                     $"{(ok ? "ok — a laser click focuses a text field, so the keyboard triggers"
                            : "FAIL — text fields never take focus, so the VR keyboard can never appear")}");

            // The keyboard must also outrank every screen that can hold a text field, or it
            // renders correctly and is covered. Hud (the login screen) is the tallest at 100 and
            // paints an opaque full-viewport backdrop.
            _keyboard = new UI.VrKeyboard();
            AddChild(_keyboard);
            bool above = _keyboard.Layer > 100 && _keyboard.Layer > 106;
            _ok &= above;
            GD.Print($"VRTEST KEYBD  keyboard layer {_keyboard.Layer} vs login screen 100 / main menu 106  " +
                     $"{(above ? "ok — draws on top" : "FAIL — the keyboard renders beneath the screen it serves")}");
            _keyboard.Show(); // first layout build
        }

        private UI.VrKeyboard _keyboard;

        /// Rebuild the layout, one build per frame, recording anything that throws.
        ///
        /// `Rebuild` used to free the preview label along with the key rows and then re-add it.
        /// Within a single frame that is invisible — `QueueFree` only takes effect at end of
        /// frame — so the failure needs real frame boundaries between builds, which is why these
        /// are separate phases rather than three calls in a row.
        private void SafeShow(string which)
        {
            if (_keyboard == null || !GodotObject.IsInstanceValid(_keyboard))
            {
                _keyboardError ??= $"keyboard vanished before the {which} build";
                return;
            }
            try { _keyboard.Show(); }
            catch (Exception e) { _keyboardError ??= $"{which} build threw {e.GetType().Name}: {e.Message}"; }
        }

        private void ReportKeyboardRebuild()
        {
            // A throw inside the node's own `_Process` never reaches this try/catch — Godot logs
            // it and carries on — so also confirm the preview label is still a live object.
            bool previewAlive = false;
            if (_keyboard != null && GodotObject.IsInstanceValid(_keyboard))
            {
                foreach (var label in _keyboard.FindChildren("*", "Label", true, false))
                    if (GodotObject.IsInstanceValid(label)) { previewAlive = true; break; }
            }

            bool ok = _keyboardError == null && previewAlive;
            _ok &= ok;
            GD.Print($"VRTEST KEYBD  three layout rebuilds on separate frames, preview alive={previewAlive}" +
                     $"{(_keyboardError == null ? "" : $" — {_keyboardError}")}  " +
                     $"{(ok ? "ok — the preview survives a rebuild"
                            : "FAIL — rebuilding the layout frees something it reuses")}");

            if (_keyboard != null && GodotObject.IsInstanceValid(_keyboard)) _keyboard.QueueFree();
            _keyboard = null;
        }

        private string _keyboardError;

        /// A panel carrying one always-visible chrome layer and one menu layer that starts hidden.
        /// This is the exact arrangement that used to pin the panel on: `InWorldHud` is visible for
        /// the whole session, so "any visible layer" was always true.
        private void BuildPanel()
        {
            _surface = new UI.VrUiSurface { Name = "TestSurface" };
            AddChild(_surface);
            _vr.UiSurface = _surface;

            // Chrome now lives on the wrist, so it must NOT be mounted here. Mounting it is the
            // regression; leaving the panel with only a hidden menu is the fixed state.
            _menuLayer = new CanvasLayer { Name = "TestMenu", Visible = false };
            _surface.Viewport.AddChild(_menuLayer);
        }

        private void CheckGestures()
        {
            // curl order: thumb, index, middle, ring, little
            var cases = new (string name, float[] curl, HandGesture want)[]
            {
                ("fist",      new[] { 1f, 1f, 1f, 1f, 1f }, HandGesture.Fist),
                ("open",      new[] { 0f, 0f, 0f, 0f, 0f }, HandGesture.Open),
                ("point",     new[] { 1f, 0f, 1f, 1f, 1f }, HandGesture.Point),
                ("thumbsup",  new[] { 0f, 1f, 1f, 1f, 1f }, HandGesture.ThumbsUp),
                ("peace",     new[] { 1f, 0f, 0f, 1f, 1f }, HandGesture.Peace),
                ("gun",       new[] { 0f, 0f, 1f, 1f, 1f }, HandGesture.Gun),
                ("rock",      new[] { 1f, 0f, 1f, 1f, 0f }, HandGesture.RockNRoll),
            };

            bool ok = true;
            foreach (var (name, curl, want) in cases)
            {
                var got = HandGestures.Classify(curl);
                if (got == want) continue;
                ok = false;
                GD.Print($"VRTEST GEST   {name}: expected {want}, got {got}  FAIL");
            }

            // Dead space: everything at half curl matches no shape, so the previous gesture must
            // survive. Without this the gesture strobes as a finger drifts across a threshold.
            var ambiguous = new[] { 0.5f, 0.5f, 0.5f, 0.5f, 0.5f };
            var held = HandGestures.Classify(ambiguous, HandGesture.Peace);
            if (held != HandGesture.Peace)
            {
                ok = false;
                GD.Print($"VRTEST GEST   dead space: expected the previous gesture (Peace), got {held}  FAIL");
            }

            _ok &= ok;
            GD.Print($"VRTEST GEST   {cases.Length} canonical shapes + dead-space hold  " +
                     $"{(ok ? "ok" : "FAIL")}");
        }

        private void CheckFingers()
        {
            var avatar = _vr.Avatar;
            var skel = avatar?.Skeleton;
            var poser = new HandPoser(avatar, left: true);

            if (skel == null || !poser.Valid)
            {
                // Not a failure: the procedural bean genuinely has no finger bones. It IS worth
                // saying out loud, because a silent skip here reads exactly like a pass.
                GD.Print("VRTEST FINGER avatar has no finger bones — SKIPPED " +
                         "(pass a real --ska to exercise this)");
                return;
            }

            int wrist = avatar.BoneOf("leftHand");
            int tip = avatar.BoneOf("leftIndexDistal");
            if (wrist < 0 || tip < 0)
            {
                GD.Print("VRTEST FINGER rig has knuckles but no leftHand/leftIndexDistal — SKIPPED");
                return;
            }

            float Reach()
            {
                skel.ForceUpdateBoneChildTransform(tip);
                return skel.GetBoneGlobalPose(wrist).Origin.DistanceTo(skel.GetBoneGlobalPose(tip).Origin);
            }

            poser.Apply(new[] { 0f, 0f, 0f, 0f, 0f });
            float straight = Reach();
            poser.Apply(new[] { 1f, 1f, 1f, 1f, 1f });
            float curled = Reach();

            // A curled index fingertip sits markedly closer to the wrist than an extended one.
            // 15% is well under a real fist (~45%) but far outside numerical noise, so it fails
            // loudly on a wrong-axis bend (which splays sideways and barely changes the distance).
            float shrink = straight > 1e-5f ? 1f - curled / straight : 0f;
            bool ok = shrink > 0.15f;
            _ok &= ok;
            GD.Print($"VRTEST FINGER index tip → wrist: straight {straight * 100f:F1} cm, " +
                     $"curled {curled * 100f:F1} cm ({shrink:P0} closer)  " +
                     $"{(ok ? "ok — fingers fold toward the palm" : "FAIL — curl axis is wrong; fingers are not flexing")}");

            WireCheck(avatar, skel, wrist, tip, poser, straight);

            poser.Apply(new[] { 0f, 0f, 0f, 0f, 0f });
        }

        /// The link that makes a hand gesture visible to *other people* — asserted, not assumed.
        ///
        /// Fingers live at wire indices 25-54, which only ride in a LOD0 frame. The client sent
        /// LOD1 exclusively until this change, which is exactly why gestures stopped at the
        /// sender's own eyes. This encodes the curled rig with the production `PoseFrame.Encode`
        /// at `Lod.Full`, decodes it, and applies it to a second rig through `ApplyBonePose` —
        /// the precise path a remote peer's client takes.
        ///
        /// It also checks the negative: the same pose sent at `Lod.Body` must leave the receiver's
        /// fingers alone. Without that, a passing LOD0 result proves nothing about whether the
        /// LOD choice actually matters.
        private void WireCheck(AvatarInstance sender, Skeleton3D skel, int wrist, int tip,
                               HandPoser poser, float straightReach)
        {
            var peer = AvatarLibrary.InstantiateOrDefault(_skaPath);
            if (peer == null) { GD.Print("VRTEST WIRE   no peer avatar — SKIPPED"); return; }
            AddChild(peer);

            var peerSkel = peer.Skeleton;
            int peerWrist = peer.BoneOf("leftHand");
            int peerTip = peer.BoneOf("leftIndexDistal");
            if (peerSkel == null || peerWrist < 0 || peerTip < 0)
            {
                GD.Print("VRTEST WIRE   peer rig lacks the finger chain — SKIPPED");
                peer.QueueFree();
                return;
            }

            float PeerReach()
            {
                peerSkel.ForceUpdateBoneChildTransform(peerTip);
                return peerSkel.GetBoneGlobalPose(peerWrist).Origin
                    .DistanceTo(peerSkel.GetBoneGlobalPose(peerTip).Origin);
            }

            float peerRest = PeerReach();

            // Curl the sender's hand into a fist, then ship it.
            poser.Apply(new[] { 1f, 1f, 1f, 1f, 1f });
            skel.ForceUpdateBoneChildTransform(tip);

            var full = Player.AvatarPose.FromTransform(
                Transform3D.Identity, 0, sender, Serika.Net.Codec.Lod.Full);
            var fullBytes = full.Encode();
            var decodedFull = Serika.Net.Codec.PoseFrame.Decode(fullBytes);
            peer.ApplyBonePose(decodedFull.Bones);
            float peerCurled = PeerReach();

            // Reset the peer, then send the same curled pose at LOD1.
            peer.ApplyBonePose(new System.Collections.Generic.List<Serika.Net.Codec.Quat>());
            var body = Player.AvatarPose.FromTransform(
                Transform3D.Identity, 0, sender, Serika.Net.Codec.Lod.Body);
            var bodyBytes = body.Encode();
            var decodedBody = Serika.Net.Codec.PoseFrame.Decode(bodyBytes);
            // A fresh peer, so its fingers start at rest and must stay there.
            var peer2 = AvatarLibrary.InstantiateOrDefault(_skaPath);
            AddChild(peer2);
            var peer2Skel = peer2.Skeleton;
            int p2Wrist = peer2.BoneOf("leftHand"), p2Tip = peer2.BoneOf("leftIndexDistal");
            peer2.ApplyBonePose(decodedBody.Bones);
            peer2Skel.ForceUpdateBoneChildTransform(p2Tip);
            float peerBodyReach = peer2Skel.GetBoneGlobalPose(p2Wrist).Origin
                .DistanceTo(peer2Skel.GetBoneGlobalPose(p2Tip).Origin);

            float shrink = peerRest > 1e-5f ? 1f - peerCurled / peerRest : 0f;
            float bodyShrink = peerRest > 1e-5f ? 1f - peerBodyReach / peerRest : 0f;

            bool ok = fullBytes.Length == 232 && bodyBytes.Length == 100
                      && shrink > 0.15f && Mathf.Abs(bodyShrink) < 0.02f;
            _ok &= ok;
            GD.Print($"VRTEST WIRE   LOD0 {fullBytes.Length} B → peer index tip {shrink:P0} closer " +
                     $"to its wrist; LOD1 {bodyBytes.Length} B → {bodyShrink:P0} (fingers untouched)  " +
                     $"{(ok ? "ok — gestures reach peers over the wire" : "FAIL — finger rotations do not survive the trip")}");

            peer.QueueFree();
            peer2.QueueFree();
        }

        public override void _PhysicsProcess(double delta)
        {
            if (_index >= _phases.Count)
            {
                GD.Print(_ok ? "VRTEST PASS" : "VRTEST FAIL");
                GetTree().Quit(_ok ? 0 : 1);
                return;
            }

            var phase = _phases[_index];
            if (_frames == 0) phase.Enter();

            phase.Tick?.Invoke(_frames);

            // Pose the tracked nodes the way the OpenXR runtime would, BEFORE VrPlayer reads them.
            // The driver's process priority puts it ahead of the rig for exactly this reason.
            _cam.Position = _head;
            // Hands roughly at the sides, so the arm IK has a solvable target rather than a
            // degenerate one — a broken chain would print noise into every other check.
            _left.Position = _head + new Vector3(-0.25f, -0.45f, -0.15f);
            _right.Position = _head + new Vector3(0.25f, -0.45f, -0.15f);
            // Controller headings. Hand-relative movement and the teleport arc both read these,
            // so they are posed here alongside the positions rather than nudged inside a phase.
            _left.Rotation = new Vector3(0, _leftYaw, 0);
            _right.Rotation = new Vector3(_rightPitch, 0, 0);

            _frames++;
            if (_frames >= phase.Frames)
            {
                phase.Exit?.Invoke();
                _frames = 0;
                _index++;
            }
        }

        /// Put the rig back on open floor between phases, so a measurement is never taken with
        /// the body pinned against something — a blocked CharacterBody3D reports zero velocity,
        /// which reads identically to locomotion being broken.
        private void Recentre()
        {
            _vr.GlobalPosition = new Vector3(0, 0.05f, 0);
            _vr.Velocity = Vector3.Zero;
            // Put the injected headset back over the body's origin as well.
            //
            // The WALL phase walks `_head` 2.4 m forward inside the play space and leaves it
            // there. `SyncBodyToHead` carries that offset onto the body every frame, so any later
            // phase that only reset the body was measuring a rig sprinting 2.4 m per tick along
            // the gaze axis — which swamped the thing being measured and, worse, pointed the
            // travel down the head's heading, so a hand-relative movement check "failed" for a
            // reason that had nothing to do with hand-relative movement.
            _head = new Vector3(0, 1.62f, 0);

            // ...and put the play space back over the body too.
            //
            // `SyncBodyToHead` cancels whatever the body actually travelled out of the origin, so
            // the origin accumulates the player's standing offset within their room. Left over
            // from the WALL phase that is ~0.7 m, and since `RotateAroundHead` pivots about the
            // headset, a snap turn then swings the body around a 0.7 m radius — 0.67 m of travel
            // that has nothing to do with the locomotion being measured. Y is preserved: it
            // carries the height calibration, not the room offset.
            if (_playSpace != null) _playSpace.Position = _playSpace.Position with { X = 0, Z = 0 };
        }

        private static float Planar(Vector3 v) => new Vector2(v.X, v.Z).Length();
    }
}

/// Input stand-in for the headless VR diagnostic.
///
/// `XRController3D.GetVector2` / `GetFloat` / `IsButtonPressed` all return zero with no OpenXR
/// runtime, so with nothing injected every input check would trivially "pass" by doing nothing.
/// When `Active` is set, `VrPlayer` reads these fields instead of the controllers. The indirection
/// is confined to three small accessors in `VrPlayer` and is inert in a real build.
public static class VrTestInput
{
    public static bool Active;
    public static Vector2 LeftStick, RightStick;
    public static float LeftGrip;
    public static bool JumpHeld;
}
