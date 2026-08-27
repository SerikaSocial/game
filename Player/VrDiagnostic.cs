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
