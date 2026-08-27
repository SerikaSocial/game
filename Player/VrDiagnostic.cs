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
        host.AddChild(new Driver(vr));
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

        public Driver(VrPlayer vr)
        {
            _vr = vr;
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
        }

        private float _dashTravel;
        private float _startYaw;
        private bool _panelIdle;
        private UI.VrUiSurface _surface;
        private CanvasLayer _menuLayer;

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

            poser.Apply(new[] { 0f, 0f, 0f, 0f, 0f });
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
