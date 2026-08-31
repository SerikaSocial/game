using Godot;
using SerikaSocial.Avatar;

namespace SerikaSocial.Player;

/// VR player controller (OpenXR). An `XROrigin3D` play space rides a `CharacterBody3D`, the
/// headset drives an `XRCamera3D`, and the two controllers drive the avatar's hands through
/// `VrAvatarIk`.
///
/// **Action names come from Godot's built-in OpenXR action map** (`primary`, `trigger`,
/// `grip`, `menu_button`, `ax_button`, `by_button`, `haptic`). The project previously pointed
/// `openxr/default_action_map` at an `xr_actions.json` — but that setting expects an
/// `OpenXRActionMap` *resource*, so a JSON file can never load as one and OpenXR silently fell
/// back to `create_default_action_sets()` anyway. Targeting the built-in names directly means
/// the bindings are whatever Godot ships for each interaction profile (Touch, Index, simple),
/// which is both correct and the same thing that was really happening before.
///
/// Locomotion is head-relative, the body follows the headset so you can physically walk around
/// the guardian, and comfort options (snap vs smooth turn, tunnelling vignette, teleport) come
/// from `DeviceProfile.Settings`.
public partial class VrPlayer : CharacterBody3D, IPlayer
{
    // Godot built-in OpenXR action names.
    private const string ActStick = "primary";
    private const string ActTrigger = "trigger";
    private const string ActGrip = "grip";
    private const string ActMenu = "menu_button";
    private const string ActPrimaryBtn = "ax_button"; // A (right) / X (left)
    private const string ActSecondaryBtn = "by_button"; // B (right) / Y (left)
    private const string ActStickClick = "primary_click";
    private const string ActStickTouch = "primary_touch";
    private const string ActPrimaryBtnTouch = "ax_touch";
    private const string ActSecondaryBtnTouch = "by_touch";
    private const string ActHaptic = "haptic";

    /// How far the trigger or grip must travel before it counts as "pulled" for gesture purposes.
    /// Well past any resting contact, so a finger merely laid on the trigger does not change the
    /// player's gesture, and well short of the top so the shape forms before the hardware bottoms
    /// out.
    private const float GestureThreshold = 0.5f;

    private const float WalkSpeed = 2.2f;
    private const float SprintSpeed = 4.2f;
    private const float JumpVelocity = 4.5f;
    private const float StickDeadzone = 0.18f;
    private const float TurnDeadzone = 0.65f;
    private const float SnapTurnCooldown = 0.28f;
    private const float GrabRadius = 0.18f;
    private const float MaxTeleportRange = 12f;
    private const float DefaultPlayerEyeHeight = 1.65f; // average standing eye height
    private const float MaxPredictOffset = 0.06f; // clamp prediction to prevent wild jumps
    private const float BodyYawLerpRate = 8f; // how fast the body catches up to head yaw
    private const float VoidThreshold = -50f;

    // Head tracking smoothing — one-tap latency reduction for standalone headsets.
    // The headset pose arrives at the start of _PhysicsProcess; we predict where it
    // will be at the next display frame by extrapolating the last known velocity.
    private Vector3 _headVel;
    private Vector3 _headAngVel;
    private const float HeadPredictTime = 0.020f; // ~1 frame at 72Hz

    // Auto-calibration: record the headset's floor distance on the first valid tracking
    // frame, then use it to scale the avatar mount so the camera sits at eye level.
    private float _measuredEyeHeight;
    private bool _heightCalibrated;
    private float _bodyYaw; // smoothed body yaw to avoid snapping
    private float _holdHapticTimer; // cooldown for continuous hold rumble
    private float _walkHapticPhase; // phase accumulator for walk-cycle rumble

    // Full Body Tracking nodes.
    private readonly System.Collections.Generic.Dictionary<string, XRNode3D> _fbtNodes = new();
    private readonly string[] _fbtTrackerNames = { "hip", "left_foot", "right_foot" };

    // ── Hands ────────────────────────────────────────────────────────────────────────
    // Index 0 = left, 1 = right throughout, matching the grab arrays below.

    /// Optical hand tracking, one per hand. Always polled; `Active` is only ever true when a
    /// camera genuinely sees the hand (see `VrHandTracking`).
    private readonly VrHandTracking[] _handTrack = { new(isLeft: true), new(isLeft: false) };

    /// Per-finger curls, fed either from the tracked joints or synthesised from the controller's
    /// trigger/grip. Allocated once — this is written every frame.
    private readonly float[][] _curl = { new float[5], new float[5] };

    /// Per-finger lateral splay (spread) angles in radians.
    private readonly float[][] _splay = { new float[5], new float[5] };

    /// Where each finger is heading. `_curl` chases this rather than being assigned, so a change
    /// of gesture is a movement instead of a jump — see `HandGestures.Approach`.
    private readonly float[][] _target = { new float[5], new float[5] };
    private readonly float[][] _targetSplay = { new float[5], new float[5] };

    private readonly HandGesture[] _gesture = { HandGesture.Neutral, HandGesture.Neutral };

    /// Curls the avatar's own finger bones. Rebuilt with each avatar, null when the rig has no
    /// finger bones to pose (the procedural bean, and plenty of real rigs).
    private readonly HandPoser[] _poser = new HandPoser[2];

    /// Which hand is currently driving the UI ray. Bare hands point with the index finger, so
    /// whichever hand is actually tracked and raised should own the pointer rather than the
    /// right hand always owning it.
    private int _pointerHand = 1;

    /// Pinch edge-detection for hand-tracked UI clicks, the bare-hands equivalent of the
    /// trigger's Schmitt trigger in `VrUiPointer`.
    private bool _pinchLatch;

    public float MouseSensitivity { get; set; } = 0.003f; // unused in VR; satisfies IPlayer

    /// Raised when the player presses the menu button, so `Main` can open the pause hub.
    public event System.Action MenuPressed;
    /// Raised on the secondary face button — `Main` maps this to the radial action menu.
    public event System.Action ActionMenuPressed;

    /// Raised when the player falls below the void threshold so Main can respawn.
    public event System.Action RespawnRequested;

    private XROrigin3D _origin;
    private XRCamera3D _camera;
    private XRController3D _leftHand;
    private XRController3D _rightHand;

    private Node3D _avatarMount;
    private AvatarInstance _avatar;
    private VrAvatarIk _ik;
    public AvatarInstance Avatar => _avatar;

    private CollisionShape3D _collider;
    private MeshInstance3D _bodyMesh;
    private NameTag3D _nameTag;

    private MeshInstance3D _vignette;
    private ShaderMaterial _vignetteMat;
    private float _vignetteAperture = 1f;

    private MeshInstance3D _teleportArc;
    private MeshInstance3D _teleportPad;
    private bool _teleportAiming;
    private bool _teleportValid;
    private Vector3 _teleportTarget;

    private MeshInstance3D _laser;
    private MeshInstance3D _laserDot;
    private Vector2 _pointerPos;
    private bool _pointerDown;

    /// The world-space panel the 2D UI is rendered onto. Set by `Main` after construction; the
    /// controller pointer and the menu-facing logic both drive off it.
    public UI.VrUiSurface UiSurface { get; set; }

    /// The headset camera, so `Main` can park the UI panel in front of the player's gaze.
    public XRCamera3D HeadCamera => _camera;

    private float _gravity = 9.8f;
    private float _snapCooldown;
    private bool _menuLatch, _actionLatch, _recenterLatch, _jumpLatch;

    // Per-hand grab state. Index 0 = left, 1 = right.
    private readonly SerikaSocial.World.PhysicsProp[] _heldProp = new SerikaSocial.World.PhysicsProp[2];
    private readonly Vector3[] _lastHandPos = new Vector3[2];
    private readonly Vector3[] _handVelocity = new Vector3[2];
    private readonly bool[] _gripLatch = new bool[2];
    private readonly bool[] _triggerLatch = new bool[2];

    /// Raised on a trigger press for hand `i` (0 = left, 1 = right) during normal play. `Main`
    /// routes it to that hand's `Interactor`; the return value says whether anything happened, so
    /// the press can be acknowledged with a haptic pulse only when it actually did something.
    public event System.Func<int, bool> InteractPressed;

    /// The controllers, so `Main` can build a per-hand interaction rig against them.
    public XRController3D LeftHand => _leftHand;
    public XRController3D RightHand => _rightHand;

    /// The prop currently held in hand `i` (0 = left, 1 = right), or null. Read by
    /// `HeldItemController` to discover whether the held item is usable without scanning.
    public SerikaSocial.World.PhysicsProp GetHeldProp(int i) => i >= 0 && i < 2 ? _heldProp[i] : null;

    /// Trigger float (0–1) for hand `i`, so `HeldItemController` can poll use input without
    /// knowing the OpenXR action name.
    public float GetTriggerValue(int i)
    {
        var hand = i == 0 ? _leftHand : _rightHand;
        return GodotObject.IsInstanceValid(hand) ? hand.GetFloat(ActTrigger) : 0f;
    }

    public bool ControlsEnabled { get; set; } = true;

    // ── Input reads ──────────────────────────────────────────────────────────────────
    // Routed through these three accessors so the headless diagnostic can inject controller
    // state (see Player/VrDiagnostic.cs). With no OpenXR runtime every real read returns zero,
    // which would make each input check pass by doing nothing at all. `VrTestInput.Active` is
    // false in every real build, so this costs one static bool test per read.

    private static Vector2 StickOf(XRController3D hand, bool left)
    {
        if (VrTestInput.Active) return left ? VrTestInput.LeftStick : VrTestInput.RightStick;
        return hand.GetVector2(ActStick);
    }

    private static float GripOf(XRController3D hand, bool left)
    {
        if (VrTestInput.Active) return left ? VrTestInput.LeftGrip : 0f;
        return hand.GetFloat(ActGrip);
    }

    private static bool JumpButton(XRController3D hand)
        => VrTestInput.Active ? VrTestInput.JumpHeld : hand.IsButtonPressed(ActPrimaryBtn);

    public override void _Ready()
    {
        _gravity = (float)ProjectSettings.GetSetting("physics/3d/default_gravity", 9.8f);

        // Same layer/mask as LocalPlayer. This body used to sit on Godot's default layer 1
        // (World), so `Portal`'s `CollisionMask = LocalPlayer` never matched a VR player and
        // `BodyEntered` never fired. Walls keep working because `LocalPlayerMask` includes
        // World — that is already what the default mask was hitting. Remotes become bumpable
        // (mask includes RemotePlayer), matching desktop; they have `CollisionMask = 0`, so
        // this is one-way. Seats and other interactables are found by group walk, not by
        // physics, so they are unaffected. The only other `CollisionMask = LocalPlayer`
        // consumer is the portal.
        CollisionLayer = PhysicsLayers.LocalPlayer;
        CollisionMask = PhysicsLayers.LocalPlayerMask;

        // Stair and slope traversal
        FloorSnapLength = 0.35f;
        FloorConstantSpeed = true;
        FloorBlockOnWall = false;
        FloorMaxAngle = Mathf.DegToRad(48f);

        _collider = new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Height = 1.6f, Radius = 0.25f },
            Position = new Vector3(0, 0.8f, 0),
        };
        AddChild(_collider);

        // Capsule stand-in, shown only until a real avatar is equipped.
        _bodyMesh = new MeshInstance3D
        {
            Mesh = new CapsuleMesh { Height = 1.6f, Radius = 0.25f },
            Position = new Vector3(0, 0.8f, 0),
        };
        _bodyMesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.35f, 0.85f) };
        AddChild(_bodyMesh);

        _avatarMount = new Node3D { Name = "AvatarMount" };
        AddChild(_avatarMount);

        _origin = new XROrigin3D { Name = "XROrigin" };
        AddChild(_origin);

        _camera = new XRCamera3D { Name = "XRCamera", Near = 0.05f, Far = 1000f };
        _origin.AddChild(_camera);

        _leftHand = new XRController3D { Name = "LeftHand", Tracker = "left_hand", ShowWhenTracked = true };
        _origin.AddChild(_leftHand);

        _rightHand = new XRController3D { Name = "RightHand", Tracker = "right_hand", ShowWhenTracked = true };
        _origin.AddChild(_rightHand);

        // No controller meshes. The player's hands are their avatar's hands: `VrAvatarIk` already
        // puts a modelled, skinned, correctly sized hand at each wrist, and drawing a capsule there
        // as well leaves a coloured bean floating inside it — visible in first person, in mirrors
        // and in every shadow pass. The rig is the hand representation; there is no second one.

        _jointMarkers[0] = BuildJointMarkers(new Color(0.45f, 0.62f, 0.95f));
        _jointMarkers[1] = BuildJointMarkers(new Color(0.95f, 0.5f, 0.42f));
        AddChild(_jointMarkers[0]);
        AddChild(_jointMarkers[1]);

        _nameTag = new NameTag3D { Name = "NameTag", Position = new Vector3(0, 1.95f, 0) };
        AddChild(_nameTag);
        _nameTag.SetShown(false); // never show your own card

        BuildVignette();
        BuildTeleportVisuals();
        BuildLaser();

        ApplyHeightOffset();
        _bodyYaw = GlobalRotation.Y;
    }

    // ---------------------------------------------------------------- rig construction

    /// A procedural bare-hand visual: a small sphere on each tracked joint.
    ///
    /// This is deliberately not a hand *mesh*. The player's hands are their avatar's hands — the
    /// arm IK already puts a fully modelled, correctly skinned hand at the wrist — so a second
    /// rendered hand would be a duplicate hovering inside the first. These markers exist only
    /// while an avatar has not loaded yet, and on the pre-login boot rig, where there is no
    /// avatar hand to look at.
    private static Node3D BuildJointMarkers(Color color)
    {
        var group = new Node3D { Name = "HandJoints" };
        var mat = new StandardMaterial3D
        {
            AlbedoColor = color,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        for (int i = 0; i < JointMarkerCount; i++)
        {
            group.AddChild(new MeshInstance3D
            {
                Name = $"J{i}",
                Mesh = new SphereMesh { Radius = 0.008f, Height = 0.016f },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = mat,
                TopLevel = true, // positioned in world space straight from the tracker
                Visible = false,
            });
        }
        return group;
    }

    private const int JointMarkerCount = 5; // the five fingertips — enough to read a hand shape

    /// Tunnelling vignette: a black quad parented to the camera whose centre aperture closes
    /// while you move. This is the single most effective motion-sickness mitigation in VR, so
    /// it is on by default and only opt-out.
    private void BuildVignette()
    {
        var shader = new Shader
        {
            Code = @"
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, depth_test_disabled, cull_disabled, shadows_disabled;

uniform float aperture : hint_range(0.0, 1.0) = 1.0;

void fragment() {
    float d = distance(UV, vec2(0.5));
    float edge = mix(0.10, 0.78, aperture);
    ALBEDO = vec3(0.0);
    ALPHA = smoothstep(edge, edge + 0.16, d);
}",
        };
        _vignetteMat = new ShaderMaterial { Shader = shader };
        _vignetteMat.SetShaderParameter("aperture", 1f);

        _vignette = new MeshInstance3D
        {
            Name = "ComfortVignette",
            Mesh = new QuadMesh { Size = new Vector2(1.4f, 1.4f) },
            Position = new Vector3(0, 0, -0.32f),
            MaterialOverride = _vignetteMat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        // Draw last so it sits over the world regardless of what else is on screen.
        _vignette.SortingOffset = 100.0f;
        _camera.AddChild(_vignette);
    }

    /// A short beam on the right hand, shown only while a menu is open, so the synthetic cursor
    /// has something to visibly come from.
    private void BuildLaser()
    {
        _laser = new MeshInstance3D
        {
            Name = "Pointer",
            Mesh = new BoxMesh { Size = new Vector3(0.004f, 0.004f, 2.0f) },
            Position = new Vector3(0, 0, -1.0f), // extends forward from the controller
            Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        _laser.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = Brand.Accent,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            NoDepthTest = true,
            RenderPriority = 101,
        };
        _rightHand.AddChild(_laser);

        _laserDot = new MeshInstance3D
        {
            Name = "PointerDot",
            Mesh = new SphereMesh { Radius = 0.012f, Height = 0.024f },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        _laserDot.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = Brand.PrimaryHi,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            NoDepthTest = true,
            RenderPriority = 102,
        };
        _laserDot.TopLevel = true;
        AddChild(_laserDot);
    }

    private void BuildTeleportVisuals()
    {
        var arcMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.65f, 0.45f, 0.95f, 0.85f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true, // the arc tints itself red when the landing is invalid
        };
        _teleportArc = new MeshInstance3D
        {
            Name = "TeleportArc",
            Mesh = new ImmediateMesh(),
            MaterialOverride = arcMat,
            Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_teleportArc);
        // The arc is built in world coordinates each frame, so it must not inherit our motion.
        _teleportArc.TopLevel = true;

        _teleportPad = new MeshInstance3D
        {
            Name = "TeleportPad",
            Mesh = new CylinderMesh { TopRadius = 0.3f, BottomRadius = 0.3f, Height = 0.02f },
            MaterialOverride = arcMat,
            Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_teleportPad);
        _teleportPad.TopLevel = true;
    }

    // ---------------------------------------------------------------- public API

    public void SetUsername(string name) => _nameTag.SetLabel(name);

    public void SetProfilePicture(byte[] bytes) => _nameTag.SetProfilePicture(bytes);

    public void SetTagPrefs(bool tags, bool pfp)
    {
        _nameTag.SetPrefs(tags, pfp);
        _nameTag.SetShown(false);
    }

    /// Equip a humanoid avatar. Unlike the desktop rig there is no first/third-person split —
    /// in VR you are always inside your own head — but the head meshes still have to go, or
    /// you spend the session looking at the inside of your own skull.
    public void SetAvatar(AvatarInstance avatar)
    {
        _avatar?.QueueFree();
        _avatar = avatar;
        _ik = null;
        _poser[0] = null;
        _poser[1] = null;

        if (avatar == null)
        {
            _bodyMesh.Visible = true;
            _nameTag.Position = new Vector3(0, 1.95f, 0);
            ScaleCollider(1.6f);
            return;
        }

        _bodyMesh.Visible = false;
        _avatarMount.AddChild(avatar);
        _nameTag.Position = new Vector3(0, avatar.Height + 0.3f, 0);

        // Scale the collider to the avatar so a 1.2m chibi doesn't have a capsule floating
        // above its head and a 2m giant doesn't have its feet inside the floor.
        ScaleCollider(avatar.Height);

        _ik = new VrAvatarIk(avatar);
        // The foot planter probes downward for the floor; without excluding our own capsule
        // every ray hits it and both feet plant at hip height.
        _ik.SetGroundProbe(GetRid(), PhysicsLayers.World);
        if (!_ik.Valid)
        {
            // No usable arm chain (the procedural bean, or a malformed rig). Fall back to the
            // capsule rather than shipping a T-posed avatar into a social space.
            GD.Print("VR: avatar has no solvable arm chain — keeping procedural animation only");
            _ik = null;
        }

        // Finger posing is independent of the arm IK: a rig can have perfectly good finger bones
        // and no solvable arm chain, and gestures are worth having either way.
        for (int i = 0; i < 2; i++)
        {
            var poser = new HandPoser(avatar, left: i == 0);
            _poser[i] = poser.Valid ? poser : null;
        }
        if (_poser[0] == null && _poser[1] == null)
            GD.Print("VR: avatar has no finger bones — hand gestures will not show on the avatar");

        HideOwnHead(avatar);
        ApplyHeightOffset();
    }

    /// Resize the collider capsule to match the avatar's height.
    private void ScaleCollider(float height)
    {
        if (_collider?.Shape is CapsuleShape3D cap)
        {
            cap.Height = Mathf.Max(0.5f, height * 0.9f);
            cap.Radius = Mathf.Clamp(height * 0.15f, 0.15f, 0.35f);
            _collider.Position = new Vector3(0, cap.Height * 0.5f, 0);
            // Remember full height: `UpdateCrouch` shrinks the live capsule every frame, so it
            // needs a standing reference that a crouch cannot ratchet downward.
            _standingCapsuleHeight = cap.Height;
            _crouchFraction = 0f;
        }
        if (_bodyMesh != null)
        {
            if (_bodyMesh.Mesh is CapsuleMesh bm)
            {
                bm.Height = Mathf.Max(0.5f, height * 0.9f);
                bm.Radius = Mathf.Clamp(height * 0.15f, 0.15f, 0.35f);
            }
            _bodyMesh.Position = _collider?.Position ?? new Vector3(0, height * 0.45f, 0);
        }
    }

    /// First-person head removal for the VR rig — the exact same three-representation split the
    /// desktop rig uses (see Avatar/FirstPersonProxy.cs), so baked-in hair planes, head ornaments
    /// and the jawline underside are gone from the HMD view too, while mirrors, other clients and
    /// every shadow pass still get the complete avatar.
    private void HideOwnHead(AvatarInstance avatar)
    {
        FirstPersonProxy.Apply(avatar, LocalPlayer.AvatarOthersLayer, LocalPlayer.AvatarFpLayer,
                               LocalPlayer.AvatarShadowLayer);
        _camera.CullMask = 1048575u & ~LocalPlayer.FpCullLayers;
    }

    /// Raise or lower the play space so the avatar's eyes line up with the headset. Without
    /// this a tall avatar on a short player floats, and the hands never reach the IK targets.
    ///
    /// Now uses the avatar's actual eye height and the measured headset floor distance to
    /// auto-calibrate, so equipping a 1.2 m chibi shrinks the world and a 2 m giant expands it.
    private void ApplyHeightOffset()
    {
        if (_origin == null) return;

        float manualOffset = UI.DeviceProfile.Settings.VrHeightOffset;
        float avatarEyeHeight = _avatar != null ? _avatar.Height * 0.93f : DefaultPlayerEyeHeight;
        float eyeRef = _heightCalibrated ? _measuredEyeHeight : DefaultPlayerEyeHeight;

        // Offset = how far to shift the play space so the avatar's eyes sit where the headset is.
        // A positive value lifts the origin (making the player shorter in-world).
        float autoOffset = avatarEyeHeight - eyeRef;

        // ...and then a few centimetres lower again, so the avatar stands with its knees softly
        // bent instead of locked straight.
        //
        // Aligning the eyes exactly means the avatar occupies precisely its bind-pose height,
        // which for a humanoid rig is legs-dead-straight — the hip-to-foot distance equals the
        // leg's full reach, and the leg has no slack at all. `VrAvatarIk` then cannot hold a
        // planted foot through even a small lean, because a foot that stays put while the hips
        // move is a target the leg physically cannot span. The feet get dragged instead, which is
        // the skating this was all meant to fix. Standing the player a centimetre or three "into"
        // the avatar is invisible in a headset (you never see your own eye height) and is anyway
        // truer to how a person stands than a locked-knee mannequin.
        float knees = _ik?.StandingCrouchMetres ?? 0f;
        float y = manualOffset + autoOffset - knees;
        _origin.Position = new Vector3(_origin.Position.X, y, _origin.Position.Z);
    }

    /// Work out how tall the player is, from the headset's height above the floor.
    ///
    /// **The headset is not on the player's face when the app starts.** It is on a desk, or in
    /// their hands, or halfway to their head — that is what launching an app *is*. The previous
    /// version latched the very first frame whose reading fell inside a generous 0.3–2.5 m band
    /// and never looked again, so a headset resting on a table calibrated the player at 1.15 m and
    /// locked it in for the session. Everything downstream is built on that number: the play space
    /// is shifted by it, so the world sits half a metre too high, the avatar's eyes are nowhere
    /// near the camera, and the hands come out in the wrong place. It reads as "tracking is
    /// awful", because every tracked thing is in the wrong position — while tracking itself is
    /// perfect.
    ///
    /// A desk reading is not noisy, it is *wrong and stable*, so no amount of averaging fixes it.
    /// What distinguishes a worn headset is that it is **high and it moves**: a head bobs, a table
    /// does not. So this waits for a sustained run of samples that are both plausibly head-height
    /// and showing real motion, and only then commits.
    ///
    /// It also keeps watching afterwards. A player who takes the headset off and hands it to
    /// someone shorter, or who calibrated while sitting and then stands, gets re-calibrated rather
    /// than being stuck with a number from a minute ago.
    private void TryAutoCalibrate()
    {
        if (XRServer.GetTracker("head") is not XRPositionalTracker headTracker || !headTracker.HasPose("default")) return;

        float camY = _camera.Position.Y;
        float dt = 1f / Mathf.Max(1, Engine.PhysicsTicksPerSecond);

        // A headset below this is on a desk, on the floor, or being carried — not being worn.
        // Sitting players are the reason it is not higher: a seated adult's eyes are around 1.2 m.
        const float WornMinimum = 1.10f;
        const float PlausibleMaximum = 2.2f;

        // Ask the runtime whether the headset is ON A FACE, rather than inferring it.
        //
        // OpenXR exposes user presence (the proximity sensor) and a session state, and Godot
        // surfaces both. That is a direct answer to the question this method was guessing at, and
        // the guess was wrong on device: a headset being picked up off a desk at 1.15 m is both
        // "plausibly head height" and "moving", so the heuristic accepted it and calibrated the
        // player 40 cm too short — the same 1.15 m as before the fix.
        //
        // Not every runtime implements XR_EXT_user_presence, so the heuristic stays as the
        // fallback; it is much better than nothing on a PCVR runtime that reports no presence.
        if (!UserIsWearingHeadset()) { ResetCalibrateWindow(); return; }

        bool plausible = camY >= WornMinimum && camY <= PlausibleMaximum;

        // Liveness is measured as the SPREAD of readings across the settle window, not as a
        // frame-to-frame difference. Differencing successive frames compares against the frame
        // rate: a head swaying a centimetre over a second moves ~0.2 mm per tick at 72 Hz, which
        // is under any threshold that would still reject a table, so the test rejected real
        // players too. Range over a window is frame-rate independent and says the thing actually
        // meant — did this thing move at all while we watched it.
        _calibrateMin = Mathf.Min(_calibrateMin, camY);
        _calibrateMax = Mathf.Max(_calibrateMax, camY);

        if (!plausible)
        {
            ResetCalibrateWindow();
            if (!_heightCalibrated)
            {
                _calibrateWarnTimer += dt;
                if (_calibrateWarnTimer > 8f)
                {
                    _calibrateWarnTimer = float.NegativeInfinity; // warn once
                    GD.PrintErr($"VR: headset height reads {camY:0.00}m after 8s, outside the " +
                                $"{WornMinimum:0.00}–{PlausibleMaximum:0.00}m a worn headset occupies — " +
                                "not calibrating. If this never resolves, check " +
                                "xr/openxr/reference_space is a floor-relative space (2 = Local Floor).");
                }
            }
            return;
        }

        _calibrateSteady += dt;
        _calibrateAccum += camY;
        _calibrateSamples++;
        if (_calibrateSteady < CalibrateSettleSeconds) return;

        float measured = _calibrateAccum / Mathf.Max(1, _calibrateSamples);
        float spread = _calibrateMax - _calibrateMin;
        ResetCalibrateWindow();

        // A window that spans half a metre is not a head, it is a headset being LIFTED — and
        // averaging across that transition is how this latched 1.29 m on a player standing at
        // 1.65: the mean of "on the desk" and "on the face". It self-corrects a second or two
        // later, which is precisely long enough for the play space to visibly jump under someone
        // who has only just put the headset on. A worn head does not travel this far in 1.5 s, so
        // waiting for the motion to settle costs nothing and removes the wrong answer entirely.
        if (spread > SettledSpreadMetres)
        {
            ResetCalibrateWindow();
            return;
        }

        // A head sways by centimetres over a second and a half; a headset on a table does not move
        // at all. Anything under a few millimetres of total travel is furniture.
        if (spread < LivenessMetres)
        {
            if (!_heightCalibrated)
            {
                _calibrateWarnTimer += CalibrateSettleSeconds;
                if (_calibrateWarnTimer > 8f)
                {
                    _calibrateWarnTimer = float.NegativeInfinity;
                    GD.PrintErr($"VR: headset reads {camY:0.00}m but has not moved ({spread * 1000f:0.0} mm " +
                                "over 1.5s) — treating it as put down, not worn, so height is not " +
                                "calibrated yet. Put the headset on.");
                }
            }
            return;
        }

        // Once calibrated, only move for a change big enough to be a different posture or a
        // different person — otherwise the play space would creep every few seconds.
        if (_heightCalibrated && Mathf.Abs(measured - _measuredEyeHeight) < 0.12f) return;

        bool first = !_heightCalibrated;
        _measuredEyeHeight = measured;
        _heightCalibrated = true;
        GD.Print($"VR: {(first ? "auto-calibrated" : "re-calibrated")} eye height = {measured:0.00}m");
        ApplyHeightOffset();
    }

    /// How long the headset must read as worn before its height is trusted.
    private const float CalibrateSettleSeconds = 1.5f;

    /// Whether the player's height has been measured yet, and what it measured. Exposed so the
    /// simulated-device diagnostic can assert that a headset lying on a desk is NOT accepted —
    /// which is the exact failure that shipped, and which is invisible from outside otherwise.
    public bool HeightCalibrated => _heightCalibrated;
    public float MeasuredEyeHeight => _measuredEyeHeight;

    /// Total vertical travel over the settle window below which the headset is furniture.
    private const float LivenessMetres = 0.003f;

    /// ...and above which it is in transit rather than being worn. Comfortably more than a head
    /// sways or nods, comfortably less than the ~50 cm of a headset coming up off a desk.
    private const float SettledSpreadMetres = 0.18f;

    /// Whether the headset is actually being worn.
    ///
    /// `IsUserPresent` is the proximity sensor — the only signal that distinguishes "on a face"
    /// from "on a table" without guessing. It requires XR_EXT_user_presence, so when the runtime
    /// does not support it this falls back to the session state: `Focused` means the runtime has
    /// given this app input focus, which a headset sitting in a tray does not get.
    private static bool UserIsWearingHeadset()
    {
        if (XRServer.FindInterface("OpenXR") is not OpenXRInterface xr || !xr.IsInitialized())
            return true; // no runtime at all (the simulated device): fall through to the heuristic

        if (xr.IsUserPresenceSupported()) return xr.IsUserPresent();
        return xr.GetSessionState() == OpenXRInterface.SessionState.Focused;
    }

    private float _calibrateWarnTimer;
    private float _calibrateSteady, _calibrateAccum;
    private float _calibrateMin = float.MaxValue, _calibrateMax = float.MinValue;
    private int _calibrateSamples;

    private void ResetCalibrateWindow()
    {
        _calibrateSteady = 0f;
        _calibrateAccum = 0f;
        _calibrateSamples = 0;
        _calibrateMin = float.MaxValue;
        _calibrateMax = float.MinValue;
    }

    /// How far the player's own arm reaches, shoulder to wrist, in metres.
    ///
    /// There is no way to measure this without asking the player to hold a calibration pose, and a
    /// pose nobody performs is worth nothing — so it is estimated from the one body measurement
    /// that *is* taken automatically. Shoulder-to-wrist is a stable fraction of stature across
    /// adults (~0.36), and stature follows from the calibrated eye height, so a number good to a
    /// couple of centimetres falls out of the existing calibration for free.
    ///
    /// Accuracy matters less than it looks: this feeds a *ratio* against the avatar's arm, so
    /// being a centimetre out moves the avatar's hand by a couple of millimetres.
    private float PlayerArmReach
    {
        get
        {
            if (!UI.DeviceProfile.Settings.VrArmScaling) return 0f;
            float eye = _heightCalibrated ? _measuredEyeHeight : DefaultPlayerEyeHeight;
            return eye / 0.93f * 0.36f;
        }
    }

    /// Toggle a built-in emote, matching `LocalPlayer.PlayEmote`. While an emote is playing the
    /// arm IK stands down — otherwise the controllers would fight the animation and the emote
    /// would read as a twitch.
    public void PlayEmote(AvatarInstance.Emote emote)
    {
        if (_avatar == null) return;
        _avatar.PlayEmote(_avatar.CurrentEmote == emote ? AvatarInstance.Emote.None : emote);
    }

    public void PlayCustomEmote(string clip)
    {
        if (_avatar == null) return;
        if (string.IsNullOrEmpty(clip)) _avatar.StopCustomEmote();
        else _avatar.PlayCustomEmote(clip);
    }

    /// Whether the UI laser is currently drawn. Exposed for the headless diagnostic, which has no
    /// other way to observe pointer gating.
    public bool PointerVisible => _laser != null && _laser.Visible;

    /// Re-read the height offset setting and move the play space. Called when the settings slider
    /// changes, since `ApplyHeightOffset` otherwise only runs on avatar swaps and calibration.
    public void ReapplyHeightOffset() => ApplyHeightOffset();

    /// Raised by `Recenter`, so `Main` can also snap the floating UI panel back in front of the
    /// player. Recentring the play space without moving the panel leaves the menu hanging off at
    /// an angle, which is the opposite of what "recentre" is asked for.
    public event System.Action Recentred;

    /// Re-centre the play space so the player faces world-forward from where they stand.
    /// Bound to the secondary button held with the trigger — an accidental recenter mid-session
    /// is disorienting, so it deliberately needs two hands.
    public void Recenter()
    {
        XRServer.CenterOnHmd(XRServer.RotationMode.ResetButKeepTilt, true);
        // Recentring is what a player reaches for when the world is in the wrong place, and the
        // usual reason for that is a height calibrated before the headset was on. Re-measure it
        // here so there is a way out that does not involve restarting the app.
        _heightCalibrated = false;
        _calibrateWarnTimer = 0f;
        ResetCalibrateWindow();
        Pulse(_leftHand, 0.4f, 0.08f);
        Pulse(_rightHand, 0.4f, 0.08f);
        Recentred?.Invoke();
    }

    // ---------------------------------------------------------------- frame loop

    public override void _PhysicsProcess(double delta)
    {
        // A resumed session delivers one enormous delta covering however long the headset was
        // off. Gravity and every velocity integrator act on it at once, so the player is fired
        // through the floor before the first tracked frame arrives. Clamp to a long-but-sane
        // frame; nothing here needs to simulate a real 30-second step.
        float dt = Mathf.Min((float)delta, 0.1f);

        TryAutoCalibrate();
        UpdateFbtTrackers();
        UpdateHands(dt);
        // Before locomotion: the capsule height this sets is what `MoveAndSlide` is about to be
        // resolved against, so a crouch has to be in effect on the frame the player ducks, not
        // the frame after it.
        UpdateCrouch(dt);

        // The headset keeps tracking even while a menu is up; only *input* is suspended.
        UpdateHeadVelocity(dt);
        SyncBodyToHead();

        if (ControlsEnabled)
        {
            HandleTurn(dt);
            HandleGrab(dt);
        }

        // Menu buttons stay live while a menu is open — that is how you close it again.
        HandleButtons(dt);
        UpdatePointer();

        var planarSpeed = HandleLocomotion(dt);
        LogLocomotionDebug(dt, planarSpeed);

        UpdateVignette(dt, planarSpeed);
        UpdateAvatar(dt, planarSpeed);
        UpdateHoldHaptics(dt);
        UpdateWalkHaptics(dt, planarSpeed);

        if (GlobalPosition.Y < VoidThreshold) RespawnRequested?.Invoke();
    }

    /// Physically walking in the guardian moves the camera inside the play space. Carry that
    /// offset onto the `CharacterBody3D` and cancel it out of the origin, so the collider stays
    /// under the headset and the world stays put.
    ///
    /// Also applies predictive head tracking: extrapolates the headset pose forward by ~1 frame
    /// to compensate for render pipeline latency on standalone headsets (Quest 2/3 at 72Hz).
    /// Prediction is clamped to `MaxPredictOffset` to prevent wild jumps from momentary jitter.
    private void SyncBodyToHead()
    {
        // The offset must be measured from the BODY, not from the play space.
        //
        // This read `_camera.Position` alone — the head's pose relative to `_origin` — and then
        // subtracted whatever the body travelled from `_origin.Position`. Those are two different
        // frames of reference, so the correction never touched the quantity being measured: the
        // XR runtime rewrites the camera's local pose from the tracker every frame, so `flat`
        // came back the same size no matter how far the origin had been shifted.
        //
        // The body therefore moved by the full head offset EVERY FRAME instead of once. That is a
        // velocity, not a catch-up: standing 5 cm off-centre in your room dragged the player at
        // 0.05 m x 72 fps = 3.6 m/s, forever, in a fixed direction. On-device telemetry caught it
        // travelling 70 m in 24 seconds with the stick centred and `Velocity` reading exactly
        // zero — which is why it was reported as "I can't walk": locomotion worked fine, but it
        // was being fought by a constant drift nothing could cancel.
        //
        // `_origin.Position + _camera.Position` is the head relative to the body, which IS what
        // the origin correction below reduces, so it converges to zero after one frame.
        var camFromBody = _origin.Position + _camera.Position;
        var flat = new Vector3(camFromBody.X, 0, camFromBody.Z);

        // Take the headset off and put it back on and the runtime drops tracking, re-seats the
        // reference space and hands back a pose metres from the last one. Treating that as
        // "the player walked there" catapults the body across the world and throws the avatar,
        // the vignette and the head-velocity prediction with it — which is what "glitches out
        // like crazy if I remove the headset once" is.
        //
        // A real person cannot walk two metres between frames, so a jump that large is a
        // discontinuity by definition. Absorb it into the play space instead: the body stays
        // where it was and the world stays put, which is the correct outcome for a player who
        // has not actually moved.
        if (flat.Length() > TrackingJumpMetres)
        {
            _origin.Position -= new Vector3(flat.X, 0, flat.Z);
            ResetHeadTracking();
            GD.Print($"VR: absorbed a {flat.Length():0.00} m tracking jump (headset re-seated?)");
            return;
        }
        // Saccadic deadzone (1.5cm) to ignore micro HMD tracking jitter so the player
        // body does not jitter or slide down slopes when standing completely still.
        if (flat.Length() < 0.015f) return;

        // Swept against world collision rather than teleported. This used to be a bare
        // `GlobalPosition += worldDelta`, which moves a CharacterBody3D straight through geometry:
        // physically walking in your guardian took you through walls and off ledges, with the
        // stick the only thing collision ever applied to.
        var before = GlobalPosition;
        MoveAndCollide(GlobalTransform.Basis * flat);

        // Cancel out of the play space only what the body actually travelled. When a wall stops
        // the body short, the leftover offset stays on the origin, so the headset keeps its real
        // position relative to the room while the avatar stays outside the wall — the standard
        // outcome, and self-correcting as soon as the player steps back.
        var movedLocal = GlobalTransform.Basis.Inverse() * (GlobalPosition - before);
        _origin.Position -= new Vector3(movedLocal.X, 0, movedLocal.Z);
    }

    /// Track head velocity for predictive tracking. Called from _PhysicsProcess before
    /// SyncBodyToHead so the prediction uses the freshest pose data.
    private void UpdateHeadVelocity(float dt)
    {
        if (dt <= 0f) return;
        var camLocal = _camera.Position;
        var camBasis = _camera.Transform.Basis;
        var rot = camBasis.GetRotationQuaternion();

        // Exponential smoothing — raw OpenXR poses have jitter that amplifies through
        // differentiation, so a light low-pass keeps the prediction stable.
        var rawVel = (camLocal - _prevHeadLocal) / dt;
        _headVel = _headVel.Lerp(rawVel, 0.35f);

        if (_hasPrevHeadRot)
        {
            var delta = rot * _prevHeadRot.Inverse();
            var axis = new Vector3(delta.X, delta.Y, delta.Z);
            float angle = 2f * Mathf.Asin(Mathf.Clamp(axis.Length(), -1f, 1f));
            if (angle > 1e-5f)
            {
                axis = axis.Normalized();
                var rawAng = axis * (angle / dt);
                _headAngVel = _headAngVel.Lerp(rawAng, 0.3f);
            }
        }

        _prevHeadLocal = camLocal;
        _prevHeadRot = rot;
        _hasPrevHeadRot = true;
    }

    private Vector3 _prevHeadLocal;
    private Quaternion _prevHeadRot;
    private bool _hasPrevHeadRot;

    /// Beyond this, a frame-to-frame head move is a tracking discontinuity, not walking.
    private const float TrackingJumpMetres = 1.0f;

    /// Forget the differentiated head state. The velocity and angular velocity are derived by
    /// differencing successive poses, so a discontinuity produces an enormous bogus velocity that
    /// the IK prediction then acts on for several frames.
    private void ResetHeadTracking()
    {
        _headVel = Vector3.Zero;
        _headAngVel = Vector3.Zero;
        _prevHeadLocal = _camera.Position;
        _prevHeadRot = _camera.Transform.Basis.GetRotationQuaternion();
        _hasPrevHeadRot = false;
    }

    /// Returns planar speed so the caller can drive both the vignette and the walk cycle.
    ///
    /// There is no bare-hands special case here any more. There used to be an
    /// `IsOpticalHandTrackingActive()` guard meant to disable stick locomotion for optical hand
    /// tracking, but Godot registers *controllers* under the tracker names `left_hand` and
    /// `right_hand` too, so it was true whenever controllers were connected; both call sites then
    /// AND-ed it with "the stick is centred", which made it a no-op that merely looked like a
    /// feature. Bare hands report a zero stick anyway, so simply reading the stick does the right
    /// thing for both input types — and cannot accidentally strand a controller user in place.
    private float HandleLocomotion(float dt)
    {
        var v = Velocity;
        if (!IsOnFloor()) v.Y -= _gravity * dt;

        var moveHand = MoveHand;
        var stick = ControlsEnabled ? StickOf(moveHand, left: moveHand == _leftHand) : Vector2.Zero;

        if (UI.DeviceProfile.Settings.VrLocomotion == UI.DeviceProfile.Settings.Locomotion.Teleport)
        {
            v.X = 0; v.Z = 0;
            UpdateTeleport(stick, moveHand);
        }
        else
        {
            var move = ApplyDeadzone(stick);
            if (move != Vector2.Zero)
            {
                var (fwd, right) = MoveBasis();
                float speed = IsSprinting(stick) ? SprintSpeed : WalkSpeed;
                // Thumbstick Y sign is a per-runtime detail, not a constant: this shipped as
                // `-move.Y`, which walked backwards on the Quest's OpenXR runtime. The setting
                // exists because hardcoding one sign and hoping is what produced that bug.
                float forward = UI.DeviceProfile.Settings.VrInvertForward ? -move.Y : move.Y;
                var dir = (fwd * forward + right * move.X) * speed;
                v.X = dir.X;
                v.Z = dir.Z;
            }
            else
            {
                v.X = 0; v.Z = 0;
            }

            // Dash: teleport stays reachable in smooth mode, aimed with the *right* hand so it
            // never fights the left stick's walking. Pushing the right stick forward is otherwise
            // an unused input — turning is on its X axis.
            if (UI.DeviceProfile.Settings.VrDashTeleport)
            {
                var turnHand = TurnHand;
                var right = ControlsEnabled ? StickOf(turnHand, left: turnHand == _leftHand) : Vector2.Zero;
                // Only the forward push arms the dash, and only when the stick is not being used
                // to turn, so a diagonal flick turns rather than blinking you across the room.
                var dashStick = Mathf.Abs(right.X) < TurnDeadzone ? right : Vector2.Zero;
                UpdateTeleport(dashStick, turnHand);
            }
        }

        // Bare hands have no stick to walk with and no button to teleport with, so without this a
        // player who puts their controllers down is rooted to the spot. Point where you want to
        // go, pinch to go there.
        Velocity = v;
        var planarMove = new Vector3(v.X, 0, v.Z);
        if (planarMove.LengthSquared() > 0.01f)
        {
            StepUp(dt, planarMove.Normalized(), planarMove.Length());
        }
        MoveAndSlide();
        return new Vector2(Velocity.X, Velocity.Z).Length();
    }

    /// True while hand `i` is held in the aiming shape: index extended, the other three curled.
    ///
    /// Deliberately read off the curls rather than off `HandGesture`, because pinching to commit
    /// moves the thumb and would reclassify the pose mid-aim (Point becomes Gun becomes something
    /// else), cancelling the aim on the exact frame it is supposed to fire. The three fingers this
    /// tests are the ones a pinch does not move.
    private bool IsAimShape(int i)
        => IsHandTracked(i)
           && _curl[i][(int)HandPoser.Finger.Index] < 0.35f
           && (_curl[i][(int)HandPoser.Finger.Middle]
               + _curl[i][(int)HandPoser.Finger.Ring]
               + _curl[i][(int)HandPoser.Finger.Little]) / 3f > 0.6f;

    private bool _bareAiming;
    private bool _barePinchLatch;

    /// Bare-hands locomotion: hold the pointing shape to aim a teleport arc out of the index
    /// fingertip, then pinch to commit.
    ///
    /// Suppressed entirely while a menu is up (the same pinch is the UI click) and while any
    /// controller is driving — a player holding controllers gets the stick, not this.
    private void UpdateBareHandTeleport()
    {
        if (UiSurface != null && UiSurface.HasInteractiveUi) { CancelBareAim(); return; }

        int i = IsAimShape(1) ? 1 : IsAimShape(0) ? 0 : -1;
        if (i < 0) { CancelBareAim(); return; }
        if (!_handTrack[i].TryGetPointerRay(out var origin, out var dir)) { CancelBareAim(); return; }

        _bareAiming = true;
        _teleportAiming = true;
        _teleportValid = TraceArc(origin, dir, out _teleportTarget);
        DrawArc(origin, dir);

        bool pinched = _handTrack[i].Pinch > 0.8f;
        if (pinched && !_barePinchLatch && _teleportValid)
        {
            var camFlat = GlobalTransform.Basis * new Vector3(_camera.Position.X, 0, _camera.Position.Z);
            GlobalPosition = _teleportTarget - camFlat;
            Velocity = Vector3.Zero;
            // Same reason as the controller teleport: several metres in one frame otherwise
            // arrives with the avatar's hair and skirt streaming out behind it.
            _avatar?.ResetPhysics();
            CancelBareAim();
        }
        _barePinchLatch = pinched;
    }

    private void CancelBareAim()
    {
        if (!_bareAiming) return;
        _bareAiming = false;
        _teleportAiming = false;
        _barePinchLatch = false;
        _teleportArc.Visible = false;
        _teleportPad.Visible = false;
    }

    // ── Locomotion telemetry ─────────────────────────────────────────────────────────
    // "I can't walk" has no visible cause in a headset: the stick is either not reaching the app,
    // or controls are suspended, or the body is being blocked, and all three look identical from
    // inside. This prints the three of them together for the first minute after spawn, which is
    // long enough to try walking and short enough not to be a permanent log tax.
    private double _locoLogTimer;
    private double _locoLogAge;
    private const double LocoLogWindowSeconds = 90;

    private void LogLocomotionDebug(float dt, float planarSpeed)
    {
        _locoLogAge += dt;
        if (_locoLogAge > LocoLogWindowSeconds) return;

        _locoLogTimer -= dt;
        if (_locoLogTimer > 0) return;
        _locoLogTimer = 1.0;

        var left = _leftHand.GetVector2(ActStick);
        var right = _rightHand.GetVector2(ActStick);
        // Height telemetry, alongside the locomotion line and for the same reason: "I am at my
        // avatar's feet" is a claim about a chain of four numbers — the tracker's reported head
        // height, the play-space offset derived from it, the avatar's eye height, and the camera
        // position that results — and from inside a headset all four are invisible. Printing them
        // together turns an unfalsifiable complaint into one obviously wrong value.
        GD.Print($"VRHEIGHT camLocalY={_camera.Position.Y:0.000} camWorldY={_camera.GlobalPosition.Y:0.000} " +
                 $"originY={_origin.Position.Y:0.000} bodyY={GlobalPosition.Y:0.000} " +
                 $"avatarH={(_avatar != null ? _avatar.Height : -1f):0.000} " +
                 $"calibrated={_heightCalibrated} measured={_measuredEyeHeight:0.000} " +
                 $"present={UserIsWearingHeadset()} " +
                 $"refSpace={ProjectSettings.GetSetting("xr/openxr/reference_space", -1)}");

        GD.Print($"VRLOCO controls={ControlsEnabled} leftStick=({left.X:0.00},{left.Y:0.00}) " +
                 $"rightStick=({right.X:0.00},{right.Y:0.00}) " +
                 $"leftTracked={_leftHand.GetHasTrackingData()} " +
                 $"mode={UI.DeviceProfile.Settings.VrLocomotion} " +
                 $"speed={planarSpeed:0.00} onFloor={IsOnFloor()} " +
                 $"pos=({GlobalPosition.X:0.0},{GlobalPosition.Y:0.0},{GlobalPosition.Z:0.0})");
    }

    /// The hand that walks, and the hand that turns. Swappable because stick-hand preference is
    /// close to religious and neither layout is wrong; the default here is movement on the RIGHT.
    private XRController3D MoveHand =>
        UI.DeviceProfile.Settings.VrMoveOnRightStick ? _rightHand : _leftHand;

    private XRController3D TurnHand =>
        UI.DeviceProfile.Settings.VrMoveOnRightStick ? _leftHand : _rightHand;

    /// The basis stick input is interpreted against, per the movement-orientation setting.
    ///
    /// Head-relative walks where you look. Hand-relative walks where the left controller points,
    /// which decouples travel from gaze — you can circle something while watching it. Both are
    /// flattened to the ground plane; without that, looking at your feet in head-relative mode
    /// scales your walking speed down to nothing.
    private (Vector3 fwd, Vector3 right) MoveBasis()
    {
        if (UI.DeviceProfile.Settings.VrMoveOrientation != UI.DeviceProfile.Settings.MoveOrientation.Hand)
            return HeadBasis();

        var b = MoveHand.GlobalTransform.Basis;
        var fwd = -b.Z with { Y = 0 };
        var right = b.X with { Y = 0 };
        // Pointing the controller straight up or down leaves no horizontal heading at all, and
        // normalising near-zero here would send the player off in a direction driven by noise.
        if (fwd.LengthSquared() < 1e-4f || right.LengthSquared() < 1e-4f) return HeadBasis();
        return (fwd.Normalized(), right.Normalized());
    }

    /// Sprint by pushing the stick to its rim, rather than by squeezing the left grip.
    ///
    /// Grip is how props are picked up (`UpdateHand`, threshold 0.6), and sprint used to read the
    /// same axis at 0.7 — so grabbing anything with your left hand also made you run. A stick
    /// magnitude past `SprintThreshold` conflicts with no binding at all, needs no new action, and
    /// is the ordinary convention for stick-driven VR locomotion.
    private static bool IsSprinting(Vector2 stick) => stick.Length() >= SprintThreshold;

    private const float SprintThreshold = 0.9f;

    /// Head-relative forward/right, flattened to the ground plane.
    private (Vector3 fwd, Vector3 right) HeadBasis()
    {
        var b = _camera.GlobalTransform.Basis;
        var fwd = -b.Z with { Y = 0 };
        var right = b.X with { Y = 0 };
        fwd = fwd.LengthSquared() > 1e-6f ? fwd.Normalized() : Vector3.Forward;
        right = right.LengthSquared() > 1e-6f ? right.Normalized() : Vector3.Right;
        return (fwd, right);
    }

    /// Rescale past the deadzone so there is no speed discontinuity at the threshold.
    private static Vector2 ApplyDeadzone(Vector2 v)
    {
        float len = v.Length();
        if (len < StickDeadzone) return Vector2.Zero;
        return v / len * Mathf.Min((len - StickDeadzone) / (1f - StickDeadzone), 1f);
    }

    /// Turning rotates the *body*, and the origin is a child, so the play space comes with it.
    /// Snap turning is the default because continuous rotation is the biggest sickness trigger.
    private void HandleTurn(float dt)
    {
        var turnHand = TurnHand;
        float x = StickOf(turnHand, left: turnHand == _leftHand).X;

        if (UI.DeviceProfile.Settings.VrSnapTurn)
        {
            _snapCooldown -= dt;
            if (_snapCooldown <= 0f && Mathf.Abs(x) > TurnDeadzone)
            {
                float angle = Mathf.DegToRad(UI.DeviceProfile.Settings.VrSnapTurnAngle) * Mathf.Sign(x);
                RotateAroundHead(-angle);
                _snapCooldown = SnapTurnCooldown;
                Pulse(turnHand, 0.35f, 0.04f);
            }
            else if (Mathf.Abs(x) <= TurnDeadzone)
            {
                _snapCooldown = 0f; // allow an immediate snap on the next flick
            }
        }
        else if (Mathf.Abs(x) > StickDeadzone)
        {
            float rate = Mathf.DegToRad(UI.DeviceProfile.Settings.VrSmoothTurnSpeed);
            RotateAroundHead(-rate * x * dt);
        }
    }

    /// Rotate about the headset rather than the body origin. Turning about the origin swings
    /// the player through an arc, which feels like being on a fairground ride.
    private void RotateAroundHead(float angle)
    {
        var pivot = _camera.GlobalPosition;
        var offset = GlobalPosition - pivot;
        var rotated = offset.Rotated(Vector3.Up, angle);
        GlobalPosition = pivot + rotated;
        RotateY(angle);
    }

    /// Drive the 2D menus from the right controller.
    ///
    /// The menus live on `UiSurface`'s SubViewport, drawn onto a world-space quad (see
    /// `UI/VrUiSurface`). This casts the controller's aim ray at that quad, converts the hit to
    /// viewport pixels and pushes the mouse events the existing `Control`s already handle.
    ///
    /// This previously used `Camera3D.UnprojectPosition` against the main viewport, on the
    /// assumption that `CanvasLayer` UI reaches the XR eye buffers. It does not — the menus were
    /// never on screen at all, so the pointer was aiming at nothing.
    ///
    /// Only runs while a menu owns input (`ControlsEnabled == false`), so the laser never
    /// interferes with normal play.
    private void UpdatePointer()
    {
        // The pointer exists to click menus, so it appears only when there is a menu — not merely
        // whenever control is suspended, and emphatically not because some always-on HUD chrome
        // happens to be drawn. See `VrUiSurface.HasInteractiveUi`.
        bool menuOpen = UiSurface != null && UiSurface.HasInteractiveUi;

        int i = _pointerHand;
        var hand = i == 0 ? _leftHand : _rightHand;
        bool bare = IsHandTracked(i);

        // With bare hands the ray leaves the index fingertip; with a controller it leaves the
        // controller's aim axis.
        Vector3 origin, aim;
        bool haveRay = bare
            ? _handTrack[i].TryGetPointerRay(out origin, out aim)
            : TryControllerRay(hand, out origin, out aim);

        // The laser is a stand-in for a finger. When the player has an actual finger to point
        // with, drawing a beam out of it is redundant clutter — the fingertip and the dot on the
        // panel already say everything the beam would.
        _laser.Visible = menuOpen && haveRay && !bare;
        // The laser is parented to the right controller but the pointer can be either hand.
        if (_laser.Visible && _laser.GetParent() != hand) Reparent(_laser, hand);

        if (!menuOpen || !haveRay)
        {
            if (_laserDot != null) _laserDot.Visible = false;
            if (_pointerDown) ReleasePointer();
            _pinchLatch = false;
            return;
        }

        Vector2 screen = Vector2.Zero;
        bool hitPanel = UiSurface.RayHit(origin, aim, out var hit) && UiSurface.WorldToViewport(hit, out screen);

        if (hitPanel)
        {
            if (_laserDot != null)
            {
                _laserDot.Visible = true;
                _laserDot.GlobalPosition = hit;
            }

            if (screen != _pointerPos)
            {
                _pointerPos = screen;
                UiSurface.Viewport.PushInput(
                    new InputEventMouseMotion { Position = screen, GlobalPosition = screen }, true);
            }
        }
        else
        {
            if (_laserDot != null) _laserDot.Visible = false;
        }

        // Click: a pinch with bare hands, the trigger with a controller. Both use a Schmitt
        // trigger — a single threshold makes an analogue input held near it chatter press/release
        // for several frames, which reads as a dead or double-firing button.
        bool pressed;
        if (bare)
        {
            float pinch = _handTrack[i].Pinch;
            pressed = _pinchLatch ? pinch > 0.55f : pinch > 0.8f;
            _pinchLatch = pressed;
        }
        else
        {
            pressed = _pointerDown ? hand.GetFloat(ActTrigger) > 0.4f : hand.GetFloat(ActTrigger) > 0.7f;
        }

        // A press only counts on the panel; a release always fires, so dragging off the panel can
        // never latch a button down forever.
        if (pressed != _pointerDown && (hitPanel || !pressed))
        {
            _pointerDown = pressed;
            UiSurface.Viewport.PushInput(new InputEventMouseButton
            {
                Position = hitPanel ? screen : _pointerPos,
                GlobalPosition = hitPanel ? screen : _pointerPos,
                ButtonIndex = MouseButton.Left,
                ButtonMask = pressed ? MouseButtonMask.Left : 0,
                Pressed = pressed,
            }, true);
            if (pressed) Pulse(hand, 0.3f, 0.02f);
        }
    }

    /// A controller's aim ray, or false when the controller is not tracked — a stale pose points
    /// somewhere arbitrary, and firing blind clicks at the panel from it is worse than no pointer.
    private static bool TryControllerRay(XRController3D hand, out Vector3 origin, out Vector3 aim)
    {
        origin = default;
        aim = default;
        if (hand == null || !hand.GetHasTrackingData()) return false;
        origin = hand.GlobalPosition;
        aim = -hand.GlobalTransform.Basis.Z;
        return true;
    }

    /// Move `node` under `parent`, preserving nothing — the laser's transform is entirely local
    /// to whichever hand holds it.
    private static void Reparent(Node3D node, Node parent)
    {
        node.GetParent()?.RemoveChild(node);
        parent.AddChild(node);
    }

    /// Let go of a synthetic click when the menu closes mid-press, so the UI never latches on a
    /// button that will never see its release event.
    private void ReleasePointer()
    {
        _pointerDown = false;
        if (_laserDot != null) _laserDot.Visible = false;
        UiSurface?.Viewport.PushInput(new InputEventMouseButton
        {
            Position = _pointerPos,
            GlobalPosition = _pointerPos,
            ButtonIndex = MouseButton.Left,
            Pressed = false,
        }, true);
    }

    /// The button layout, which is VRChat's.
    ///
    /// Matching it is a deliberate compatibility decision rather than a stylistic one. Social VR
    /// players arrive with the bindings already in their hands, and every deviation is a thing
    /// they have to unlearn *while wearing a headset*, where there is nowhere to look them up. So:
    ///
    ///   A (right)            jump
    ///   X (left)             mute
    ///   B / Y (either)       quick menu on a tap, action menu on a hold
    ///   stick click          action menu
    ///   grip                 pick up            (see `UpdateHand`)
    ///   trigger              use / interact     (see `UpdateHand`)
    ///   left stick           locomote           (see `HandleLocomotion`)
    ///   right stick          turn               (see `HandleTurn`)
    ///   both menu buttons    recentre
    ///
    /// Recentring is the one binding with no VRChat equivalent — there it is the runtime's own
    /// system gesture — and it used to sit on "left Y + right trigger", which is now the quick
    /// menu plus interact, i.e. a combination players will hit constantly by accident. Holding
    /// both menu buttons cannot be reached by mistake and follows VRChat's own precedent of
    /// putting a rarely-wanted global action behind a two-handed menu-button hold.
    private void HandleButtons(float dt)
    {
        // Jump — A. Suppressed while a menu is up, where the same button is the menu's select.
        //
        // Explicitly latched. The floor check was never an edge trigger, whatever the old comment
        // claimed: holding A while grounded re-applied jump velocity on every single frame, so the
        // player pogoed continuously instead of jumping once.
        bool jump = ControlsEnabled && JumpButton(_rightHand);
        if (jump && !_jumpLatch && IsOnFloor())
            Velocity = Velocity with { Y = JumpVelocity };
        _jumpLatch = jump;

        // Mute — X. Live while a menu is open: needing to close a menu before you can stop
        // transmitting is exactly backwards.
        bool mute = _leftHand.IsButtonPressed(ActPrimaryBtn);
        if (mute && !_muteLatch) MutePressed?.Invoke();
        _muteLatch = mute;

        // B / Y — tap for the quick menu, hold for the action menu.
        //
        // The action menu fires the moment the hold threshold passes rather than on release, so
        // the radial appears under a thumb that is still down, which is how it is then used. The
        // release is swallowed in that case, or every hold would also open the quick menu behind
        // it on the way out.
        bool face = _leftHand.IsButtonPressed(ActSecondaryBtn) || _rightHand.IsButtonPressed(ActSecondaryBtn)
                    || _leftHand.IsButtonPressed(ActMenu) || _rightHand.IsButtonPressed(ActMenu);
        if (face)
        {
            _faceHeld += dt;
            if (!_faceConsumed && _faceHeld >= ActionMenuHoldSeconds)
            {
                _faceConsumed = true;
                ActionMenuPressed?.Invoke();
            }
        }
        else
        {
            if (_faceHeld > 0f && !_faceConsumed) MenuPressed?.Invoke();
            _faceHeld = 0f;
            _faceConsumed = false;
        }

        // Stick click — the action menu directly, VRChat's "Action Menu Left/Right".
        bool stickClick = _leftHand.IsButtonPressed(ActStickClick) || _rightHand.IsButtonPressed(ActStickClick);
        if (stickClick && !_actionLatch) ActionMenuPressed?.Invoke();
        _actionLatch = stickClick;

        // Bare hands have no buttons at all, so they reach the quick menu by the wrist tap.
        bool wrist = WristTapped();
        if (wrist && !_menuLatch) MenuPressed?.Invoke();
        _menuLatch = wrist;

        // Recentre — both menu buttons, held.
        bool bothMenu = _leftHand.IsButtonPressed(ActMenu) && _rightHand.IsButtonPressed(ActMenu);
        _recenterHeld = bothMenu ? _recenterHeld + dt : 0f;
        if (_recenterHeld >= RecenterHoldSeconds && !_recenterLatch) { _recenterLatch = true; Recenter(); }
        if (!bothMenu) _recenterLatch = false;
    }

    /// How long B/Y must be held before it means the action menu rather than the quick menu. Long
    /// enough not to trip on a deliberate tap, short enough not to feel like a stuck button.
    private const float ActionMenuHoldSeconds = 0.35f;
    private const float RecenterHoldSeconds = 1.0f;

    private float _faceHeld, _recenterHeld;
    private bool _faceConsumed, _muteLatch;

    /// Raised on the mute button, so `Main` can toggle the microphone.
    public event System.Action MutePressed;

    /// The bare-hands menu gesture: touch one index fingertip to the other wrist and pinch.
    ///
    /// This is the one place a hand-tracking UI genuinely needs a convention, and it is worth
    /// following the platform's: Meta puts a menu button on the wrist, so players already reach
    /// there. It also has no orientation maths in it. A "palm facing your face" test — the obvious
    /// alternative — needs a palm *normal*, and the sign of that normal flips between hands and
    /// between runtime conventions, so getting it wrong silently means the gesture only works on
    /// one hand. Touching a point is unambiguous.
    ///
    /// The wrist HUD is drawn at exactly this spot, so there is something visible to aim at.
    private bool WristTapped()
    {
        for (int i = 0; i < 2; i++)
        {
            int other = 1 - i;
            if (!IsHandTracked(i) || !IsHandTracked(other)) continue;
            if (_handTrack[i].Pinch < 0.8f) continue;
            if (!_handTrack[i].TryGetJoint(XRHandTracker.HandJoint.IndexFingerTip, out var tip)) continue;
            if (!_handTrack[other].TryGetWrist(out var wrist)) continue;
            if (tip.Origin.DistanceTo(wrist.Origin) < WristTapRadius) return true;
        }
        return false;
    }

    private const float WristTapRadius = 0.07f;

    // ---------------------------------------------------------------- grabbing

    /// Grip to grab a nearby `PhysicsProp`, release to throw it with the hand's actual
    /// velocity. Hand velocity is measured from world positions rather than read from the
    /// controller, so it stays correct while the play space itself is moving.
    private void HandleGrab(float dt)
    {
        UpdateHand(0, _leftHand, dt);
        UpdateHand(1, _rightHand, dt);
    }

    private void UpdateHand(int i, XRController3D hand, float dt)
    {
        bool bare = IsHandTracked(i);

        // Grab from the hand the player is actually using. With bare hands the grab point is the
        // palm, not the wrist: closing your hand around something puts it in your palm, and
        // grabbing from the wrist means reaching a hand's width past everything you aim at.
        var pos = hand.GlobalPosition;
        if (bare && _handTrack[i].TryGetJoint(XRHandTracker.HandJoint.Palm, out var palm))
            pos = palm.Origin;

        if (dt > 0f)
        {
            var raw = (pos - _lastHandPos[i]) / dt;
            // Light smoothing: a single jittery frame at release should not launch the prop.
            _handVelocity[i] = _handVelocity[i].Lerp(raw, 0.45f);
        }
        _lastHandPos[i] = pos;

        // Closing your hand is the grab, whichever input reports it. For bare hands that is the
        // three fingers that actually wrap an object — the index is excluded because it is also
        // the pointing/pinching finger, and including it would make every pinch a grab attempt.
        bool gripped = bare
            ? (_curl[i][(int)HandPoser.Finger.Middle]
               + _curl[i][(int)HandPoser.Finger.Ring]
               + _curl[i][(int)HandPoser.Finger.Little]) / 3f > 0.6f
            : GripOf(hand, left: i == 0) > 0.6f;

        if (gripped && !_gripLatch[i])
        {
            var prop = FindPropNear(pos);
            if (prop != null && prop.GrabAt(pos))
            {
                _heldProp[i] = prop;
                Pulse(hand, 0.7f, 0.08f);
            }
        }
        else if (!gripped && _heldProp[i] != null)
        {
            _heldProp[i].ReleaseWithVelocity(_handVelocity[i]);
            _heldProp[i] = null;
            Pulse(hand, 0.3f, 0.04f);
        }

        _gripLatch[i] = gripped;

        // Trigger-to-interact, strictly additive to the grip path above.
        //
        // Grip stays the way physics props are picked up — it works and it feels right. The
        // trigger exists for everything grip *cannot* reach: seats, lay spots, interaction points,
        // video screens. There is no three-way ambiguity with the UI pointer, because this whole
        // method only runs while `ControlsEnabled` is true and `UpdatePointer` only acts while it
        // is false — a menu being open already routes the trigger away from here.
        // A pinch made while pointing is the teleport commit, not an interact — without this
        // exclusion, blinking across the room also sits you on whatever chair you aimed past.
        bool trigger = bare
            ? !IsAimShape(i) && _handTrack[i].Pinch > 0.8f
            : hand.GetFloat(ActTrigger) > 0.6f;
        if (trigger && !_triggerLatch[i])
        {
            // A held item claims the trigger first: while you are holding a marker pen, pulling
            // the trigger has to mean "draw", not "sit on the nearest chair".
            if (_heldProp[i] is SerikaSocial.World.IUsable) { /* handled by HeldItemController */ }
            else if (InteractPressed?.Invoke(i) == true) Pulse(hand, 0.5f, 0.05f);
        }
        _triggerLatch[i] = trigger;

        // A prop can be taken from us by the network layer; drop our claim if so.
        if (_heldProp[i] != null)
        {
            if (!_heldProp[i].HeldByLocal) _heldProp[i] = null;
            else _heldProp[i].UpdateHeldPosition(pos);
        }
    }

    private SerikaSocial.World.PhysicsProp FindPropNear(Vector3 point)
    {
        var space = GetWorld3D()?.DirectSpaceState;
        if (space == null) return null;

        var shape = new SphereShape3D { Radius = GrabRadius };
        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = shape,
            Transform = new Transform3D(Basis.Identity, point),
            CollideWithBodies = true,
            CollideWithAreas = false,
        };
        query.Exclude = new Godot.Collections.Array<Rid> { GetRid() };

        foreach (var hit in space.IntersectShape(query, 8))
        {
            if (hit.TryGetValue("collider", out var c)
                && c.As<GodotObject>() is SerikaSocial.World.PhysicsProp prop
                && prop.CanInteract)
                return prop;
        }
        return null;
    }

    // ---------------------------------------------------------------- teleport

    /// Push the stick forward to aim a ballistic arc; release to teleport to where it lands.
    /// Only surfaces flat enough to stand on are accepted.
    ///
    /// `aimHand` is the hand the arc comes out of — the left in teleport mode, the right when
    /// this is the dash available alongside smooth locomotion.
    private void UpdateTeleport(Vector2 stick, XRController3D aimHand)
    {
        bool aiming = ControlsEnabled && stick.Y < -0.5f;

        if (aiming)
        {
            _teleportAiming = true;
            var origin = aimHand.GlobalPosition;
            var dir = -aimHand.GlobalTransform.Basis.Z;
            _teleportValid = TraceArc(origin, dir, out _teleportTarget);
            DrawArc(origin, dir);
        }
        else if (_teleportAiming)
        {
            // Released — commit.
            _teleportAiming = false;
            _teleportArc.Visible = false;
            _teleportPad.Visible = false;
            if (_teleportValid)
            {
                // Move the body, keeping the headset's offset within the play space intact.
                var camFlat = GlobalTransform.Basis * new Vector3(_camera.Position.X, 0, _camera.Position.Z);
                GlobalPosition = _teleportTarget - camFlat;
                Velocity = Vector3.Zero;
                // Up to 12 m in one frame: without this the avatar arrives with its hair and
                // skirt streaming out behind it, which in VR reads as a glitch, not as motion.
                _avatar?.ResetPhysics();
                Pulse(aimHand, 0.6f, 0.08f);
            }
        }
    }

    /// Step a projectile arc until it hits something. Returns true when the landing spot is a
    /// floor (normal within ~40° of up) rather than a wall or ceiling.
    private bool TraceArc(Vector3 origin, Vector3 dir, out Vector3 landing)
    {
        landing = Vector3.Zero;
        var space = GetWorld3D()?.DirectSpaceState;
        if (space == null) return false;

        var vel = dir * 8f;
        var p = origin;
        const float step = 0.06f;

        for (int i = 0; i < 90; i++)
        {
            var next = p + vel * step;
            vel.Y -= _gravity * step;

            var q = PhysicsRayQueryParameters3D.Create(p, next);
            q.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
            var hit = space.IntersectRay(q);

            if (hit.Count > 0)
            {
                landing = hit["position"].AsVector3();
                var normal = hit["normal"].AsVector3();
                if (origin.DistanceTo(landing) > MaxTeleportRange) return false;
                return normal.Dot(Vector3.Up) > 0.76f;
            }
            p = next;
        }
        return false;
    }

    private void DrawArc(Vector3 origin, Vector3 dir)
    {
        var mesh = (ImmediateMesh)_teleportArc.Mesh;
        mesh.ClearSurfaces();
        mesh.SurfaceBegin(Mesh.PrimitiveType.LineStrip);

        var vel = dir * 8f;
        var p = origin;
        const float step = 0.06f;
        var col = _teleportValid ? new Color(0.65f, 0.45f, 0.95f) : new Color(0.9f, 0.3f, 0.3f);

        for (int i = 0; i < 90; i++)
        {
            mesh.SurfaceSetColor(col);
            mesh.SurfaceAddVertex(p);
            p += vel * step;
            vel.Y -= _gravity * step;
            if (_teleportValid && p.DistanceTo(_teleportTarget) < 0.12f) break;
        }
        mesh.SurfaceEnd();

        _teleportArc.Visible = true;
        _teleportPad.Visible = _teleportValid;
        if (_teleportValid)
            _teleportPad.GlobalPosition = _teleportTarget + new Vector3(0, 0.015f, 0);
    }

    // ---------------------------------------------------------------- comfort + avatar

    private void UpdateVignette(float dt, float speed)
    {
        if (_vignette == null) return;

        if (!UI.DeviceProfile.Settings.VrVignette)
        {
            _vignette.Visible = false;
            return;
        }

        // Close the aperture in proportion to speed, and ease it so the vignette itself is not
        // a jarring transition.
        float strength = Mathf.Clamp(UI.DeviceProfile.Settings.VrVignetteStrength, 0f, 1f);
        float moving = Mathf.Clamp(speed / SprintSpeed, 0f, 1f);
        float target = Mathf.Lerp(1f, 1f - strength, moving);

        _vignetteAperture = Mathf.Lerp(_vignetteAperture, target, Mathf.Min(1f, dt * 6f));
        _vignetteMat.SetShaderParameter("aperture", _vignetteAperture);
        _vignette.Visible = _vignetteAperture < 0.995f;
    }

    private void UpdateAvatar(float dt, float speed)
    {
        if (_avatar == null) return;

        UpdateBodyYaw(dt, speed);
        var bodyFwd = new Vector3(Mathf.Sin(_bodyYaw), 0, Mathf.Cos(_bodyYaw));

        // **The avatar stands on the BODY, not under the headset.**
        //
        // This used to mount the rig at the camera's XZ. That looks identical most of the time —
        // `SyncBodyToHead` keeps the body under the head — and is wrong in the two moments that
        // matter. Leaning became a translation: tip forward to look at something and your whole
        // avatar, feet included, slid forward with your skull, which is the single largest source
        // of the "feet are skating" report. And when a wall stops the body, the head keeps going,
        // so the avatar was drawn standing *inside* the wall it had just been prevented from
        // entering.
        //
        // Mounting on the body makes the head's offset from it a real quantity, which is exactly
        // what `VrAvatarIk` now consumes: it leans the spine and hips to reach the head instead of
        // teleporting the body under it. Leaning is a lean, and the feet stay where they were put.
        _avatarMount.GlobalPosition = GlobalPosition;
        _avatarMount.GlobalBasis = Basis.LookingAt(bodyFwd, Vector3.Up);

        // Procedural locomotion first, then IK overrides the head, spine, legs and arms on top.
        _avatar.Animate(dt, speed, IsOnFloor(), crouching: _crouchFraction > 0.35f,
                        sprinting: speed > (WalkSpeed + SprintSpeed) * 0.5f);

        // An emote owns the whole body; letting the hand IK write over it afterwards would
        // reduce a wave or a dance to a twitch.
        if (_avatar.CurrentEmote != AvatarInstance.Emote.None) return;

        // Use predicted head transform for IK — the camera pose is one frame stale by the time
        // the avatar renders, so extrapolating the head position/rotation closes the gap.
        // Prediction is clamped to prevent wild jumps.
        var headTransform = _camera.GlobalTransform;
        if (_headVel.LengthSquared() > 1e-8f || _headAngVel.LengthSquared() > 1e-8f)
        {
            var posPredict = _headVel * HeadPredictTime;
            if (posPredict.LengthSquared() > MaxPredictOffset * MaxPredictOffset)
                posPredict = posPredict.Normalized() * MaxPredictOffset;
            var predictedPos = headTransform.Origin + posPredict;
            var predictedRot = headTransform.Basis.GetRotationQuaternion();
            // Small-angle rotational prediction: apply angular velocity as a swing.
            if (_headAngVel.LengthSquared() > 1e-8f)
            {
                var angAxis = _headAngVel.Normalized();
                float angMag = Mathf.Clamp(_headAngVel.Length() * HeadPredictTime, 0f, 0.15f);
                predictedRot = new Quaternion(angAxis, angMag) * predictedRot;
            }
            headTransform = new Transform3D(new Basis(predictedRot), predictedPos);
        }

        // Query active Full Body Tracking (FBT) trackers.
        Vector3? hipPos = null;
        Vector3? leftFootPos = null;
        Vector3? rightFootPos = null;

        if (_fbtNodes.TryGetValue("hip", out var hipNode) && hipNode.GetHasTrackingData())
            hipPos = hipNode.GlobalPosition;
        if (_fbtNodes.TryGetValue("left_foot", out var lfNode) && lfNode.GetHasTrackingData())
            leftFootPos = lfNode.GlobalPosition;
        if (_fbtNodes.TryGetValue("right_foot", out var rfNode) && rfNode.GetHasTrackingData())
            rightFootPos = rfNode.GlobalPosition;

        // Bare hands put the wrist where the camera sees it; controllers put it where the
        // controller is. The wrist rather than the palm, because that is what the humanoid
        // `leftHand`/`rightHand` bone is — see `VrHandTracking.TryGetWrist`.
        //
        // Full transforms, not positions. The rotation is the wrist, and `VrAvatarIk` needs it to
        // put the hand anywhere other than in line with the forearm.
        var leftTarget = HandTarget(0, _leftHand);
        var rightTarget = HandTarget(1, _rightHand);

        // While a nod or shake is running the gesture owns the head bone; re-solving it from the
        // headset every frame would overwrite the gesture before anyone could see it.
        _ik?.Solve(headTransform, leftTarget, rightTarget, dt,
                   hipPos, leftFootPos, rightFootPos, solveHead: !_avatar.GestureActive,
                   playerArmReach: PlayerArmReach, planarSpeed: speed, grounded: IsOnFloor());

        // Fingers go on last, for the same reason `HeadAim` does: whatever writes a bone last
        // wins, and the arm IK above rewrites the hand bone these hang off.
        //
        // Local only, for now. `HumanoidBones.Lod1` is the wire order and carries 22 body bones —
        // no fingers — so a gesture is visible to its owner and in mirrors but does not reach
        // peers. Widening the pose frame is a proto change, which is a breaking-change review.
        if (UI.DeviceProfile.Settings.VrFingerPosing)
        {
            bool bare0 = IsHandTracked(0);
            bool bare1 = IsHandTracked(1);
            _poser[0]?.Apply(_curl[0], _splay[0], bare0 ? _handTrack[0].ThumbOpposition : 0f);
            _poser[1]?.Apply(_curl[1], _splay[1], bare1 ? _handTrack[1].ThumbOpposition : 0f);
        }
    }

    /// Where the avatar's chest points, which is not simply where the player is looking.
    ///
    /// **A body does not follow a gaze one-for-one.** The old rule lerped the body yaw straight
    /// at the head yaw every frame, so glancing at someone beside you slowly rotated your entire
    /// avatar to face them — and because `PoseTransform` broadcasts this yaw, every peer watched
    /// you swivel while you thought you were standing still. It also meant you could never look
    /// over your shoulder: the body chased the look and the shoulder was never behind you.
    ///
    /// Real necks have slack. The body stays put until the head is turned past a threshold, and
    /// only then rotates enough to bring the head back inside it — so a glance costs nothing and a
    /// sustained turn brings the shoulders round, which is what people actually do.
    ///
    /// Walking overrides all of it: you face the way you are going. That is both true of humans
    /// and necessary here, because strafing with your head turned would otherwise moonwalk.
    private void UpdateBodyYaw(float dt, float speed)
    {
        var (fwd, _) = HeadBasis();
        float headYaw = Mathf.Atan2(fwd.X, fwd.Z);

        // Moving: face travel, and quickly. `Velocity` is the honest source — it is what actually
        // happened after collision, so walking into a wall at an angle does not keep the avatar
        // striding at the wall.
        var vel = new Vector2(Velocity.X, Velocity.Z);
        if (speed > 0.35f && vel.LengthSquared() > 1e-6f)
        {
            float moveYaw = Mathf.Atan2(vel.X, vel.Y);
            // Blend toward the gaze so a sideways strafe reads as a sidestep rather than the
            // avatar rotating to walk face-first in the strafe direction.
            float blended = Mathf.LerpAngle(moveYaw, headYaw, MoveYawGazeBlend);
            _bodyYaw = Mathf.LerpAngle(_bodyYaw, blended, 1f - Mathf.Exp(-BodyYawLerpRate * dt));
            return;
        }

        float deadzone = Mathf.DegToRad(Mathf.Max(0f, UI.DeviceProfile.Settings.VrBodyTurnDeadzone));
        float err = Mathf.AngleDifference(_bodyYaw, headYaw);

        // Outside the neck's slack: chase only the excess, so the body comes to rest with the head
        // exactly at the edge of the deadzone rather than square on. Chasing the head itself would
        // make the body overshoot into the slack and then drift for as long as you kept looking.
        float target = Mathf.Abs(err) <= deadzone
            ? _bodyYaw
            : headYaw - Mathf.Sign(err) * deadzone;

        // **Where the hands are is better evidence than where the head is looking.**
        //
        // A head swivels freely on its neck, so head yaw says only where someone is *looking*.
        // Hands hang off the shoulders, so when the torso turns the hands come with it — which
        // makes the hands a direct measurement of the thing being guessed at. Overte (the
        // open-source High Fidelity fork) goes as far as weighting this 100% hands / 0% head,
        // deriving chest azimuth from the hip-to-hand midpoint.
        //
        // Pure hands is too strong here, because hands resting at the sides carry no azimuth at
        // all and the answer degenerates. So the hands lead only in proportion to how much they
        // actually have to say: arms out in front or reaching to one side are strong evidence,
        // arms hanging down are none, and in that case this falls through to the head rule above.
        if (TryHandAzimuth(out float handYaw, out float confidence))
            target = Mathf.LerpAngle(target, handYaw, confidence);

        if (Mathf.Abs(Mathf.AngleDifference(_bodyYaw, target)) < 1e-4f) return;
        _bodyYaw = Mathf.LerpAngle(_bodyYaw, target, 1f - Mathf.Exp(-BodyYawLerpRate * dt));
    }

    /// The direction the player's arms say their chest is facing, and how much to trust it.
    ///
    /// Confidence is how far the hands' midpoint sits from the body's vertical axis: a midpoint
    /// on the axis has no direction at all, and normalising it would hand back pure noise.
    private bool TryHandAzimuth(out float yaw, out float confidence)
    {
        yaw = 0f;
        confidence = 0f;

        var mid = (_leftHand.GlobalPosition + _rightHand.GlobalPosition) * 0.5f;
        var off = mid - GlobalPosition;
        var flat = new Vector2(off.X, off.Z);
        float reach = flat.Length();
        if (reach < HandAzimuthMin) return false;

        yaw = Mathf.Atan2(flat.X, flat.Y);
        confidence = Mathf.Clamp((reach - HandAzimuthMin) / (HandAzimuthFull - HandAzimuthMin), 0f, 1f)
                     * HandAzimuthWeight;
        return confidence > 1e-3f;
    }

    /// Below this the hands are simply hanging and say nothing about the torso; above the second
    /// they are extended enough to be the whole answer.
    private const float HandAzimuthMin = 0.25f;
    private const float HandAzimuthFull = 0.60f;
    private const float HandAzimuthWeight = 0.8f;

    private const float MoveYawGazeBlend = 0.35f;

    /// How far the player has physically crouched, 0 (standing) to 1 (as low as tracked).
    /// Drives the collider height, the avatar's `crouching` animation state, and — through
    /// `VrAvatarIk` — how far the hips drop and the knees fold.
    public float CrouchFraction => _crouchFraction;
    private float _crouchFraction;
    private float _standingCapsuleHeight = 1.6f;

    /// Track how low the player is standing, and shrink the collider to match.
    ///
    /// **Ducking has to actually duck.** Every real VR game lets you crouch under things by
    /// crouching, and this one had a capsule fixed at the avatar's full standing height: the
    /// player could put their head under a beam and their body would still refuse to fit. The
    /// capsule now follows the headset down, which is what makes low doorways, tables and cover
    /// behave the way the player's own body tells them they should.
    ///
    /// The floor of the capsule stays on the floor — only the top comes down — or shrinking it
    /// would drop the player through the ground on the frame they crouched.
    private void UpdateCrouch(float dt)
    {
        if (!UI.DeviceProfile.Settings.VrPhysicalCrouch)
        {
            _crouchFraction = 0f;
            return;
        }

        // Measured against the calibrated standing height, so a short player is not permanently
        // "crouching" and a tall one has to actually bend before anything happens.
        // `_camera.Position` is already the headset's height above the play-space floor — the
        // camera is a child of the origin, so its local Y is exactly the quantity wanted, and it
        // is the same number `TryAutoCalibrate` measures the standing height from. Subtracting
        // `_origin.Position.Y` as well takes the calibration offset off twice, which reports a
        // permanent crouch on every avatar whose height differs from its wearer's.
        float standing = _heightCalibrated ? _measuredEyeHeight : DefaultPlayerEyeHeight;
        float drop = standing - _camera.Position.Y;

        // A dead band absorbs the couple of centimetres a head moves just by breathing and
        // looking around; without it the collider would resize every frame of normal standing.
        float target = Mathf.Clamp((drop - CrouchDeadband) / (standing * MaxCrouchFraction), 0f, 1f);
        _crouchFraction = Mathf.Lerp(_crouchFraction, target, 1f - Mathf.Exp(-12f * dt));

        if (_collider?.Shape is not CapsuleShape3D cap) return;
        float wanted = Mathf.Max(MinCapsuleHeight, _standingCapsuleHeight * (1f - _crouchFraction * MaxCrouchFraction));
        if (Mathf.Abs(cap.Height - wanted) < 0.005f) return;

        // Standing back up must not push the player's head through a ceiling they crouched under.
        if (wanted > cap.Height && !CanStandTo(wanted)) return;

        cap.Height = wanted;
        _collider.Position = new Vector3(0, wanted * 0.5f, 0);
    }

    /// Whether there is room overhead to grow the capsule back to `height`.
    private bool CanStandTo(float height)
    {
        var space = GetWorld3D()?.DirectSpaceState;
        if (space == null || _collider?.Shape is not CapsuleShape3D cap) return true;

        float grow = height - cap.Height;
        if (grow <= 0f) return true;

        var q = PhysicsRayQueryParameters3D.Create(
            GlobalPosition + Vector3.Up * cap.Height,
            GlobalPosition + Vector3.Up * (height + 0.05f),
            PhysicsLayers.World);
        q.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
        return space.IntersectRay(q).Count == 0;
    }

    /// Head travel below standing before a crouch registers at all.
    private const float CrouchDeadband = 0.10f;
    /// How much of the player's height a full crouch takes off the capsule.
    private const float MaxCrouchFraction = 0.55f;
    private const float MinCapsuleHeight = 0.55f;

    /// The IK target for hand `i` — where the wrist is, and which way it is turned.
    ///
    /// Falls back to a rotation derived from the forearm when wrist tracking is off, by handing
    /// the solver a basis it will recognise as "no opinion": the controller's own position with
    /// the play space's orientation. `VrAvatarIk` clamps wrist twist against the anatomical
    /// solution regardless, so a runtime with an unexpected grip convention degrades to stiff
    /// rather than to backwards.
    private Transform3D HandTarget(int i, XRController3D hand)
    {
        bool bare = _handTrack[i].Active && UI.DeviceProfile.Settings.VrHandTracking;
        var xform = bare && _handTrack[i].TryGetWrist(out var wrist) ? wrist : hand.GlobalTransform;
        if (UI.DeviceProfile.Settings.VrWristTracking) return xform;
        return new Transform3D(GlobalTransform.Basis, xform.Origin);
    }

    // ---------------------------------------------------------------- hands

    /// True when the runtime is optically tracking hand `i` and the player has hand tracking on.
    public bool IsHandTracked(int i)
        => UI.DeviceProfile.Settings.VrHandTracking && i >= 0 && i < 2 && _handTrack[i].Active;

    /// The gesture hand `i` is currently making. Exposed for the diagnostic and for `Main`, which
    /// maps a couple of gestures onto emotes.
    public HandGesture GetGesture(int i) => i >= 0 && i < 2 ? _gesture[i] : HandGesture.Neutral;

    /// Raised when a hand settles into a new gesture. Carries (hand, gesture) with hand 0 = left.
    public event System.Action<int, HandGesture> GestureChanged;

    /// Poll both hands, derive curls and gestures, and swap the controller visual for bare hands.
    ///
    /// Runs whether or not `ControlsEnabled` is set: a gesture is a *pose*, not an action, and it
    /// should keep reading correctly while a menu is open — otherwise your avatar's hands snap
    /// flat the moment you open the pause hub, which every peer sees.
    private void UpdateHands(double delta)
    {
        bool wantTracking = UI.DeviceProfile.Settings.VrHandTracking;
        var originXform = _origin.GlobalTransform;

        for (int i = 0; i < 2; i++)
        {
            var hand = i == 0 ? _leftHand : _rightHand;
            var track = _handTrack[i];

            if (wantTracking) track.Poll(delta, originXform);

            bool bare = wantTracking && track.Active;

            if (_jointMarkers[i] != null)
            {
                // Markers only stand in for an avatar hand that does not exist yet.
                bool wantMarkers = bare && _avatar == null;
                _jointMarkers[i].Visible = wantMarkers;
                if (wantMarkers) PlaceJointMarkers(i, track);
            }

            HandGesture next;
            if (bare)
            {
                // Optical hands are measured, not looked up: the camera reports every finger
                // independently, so the shape it sees IS the shape, and classification is the
                // right way round.
                System.Array.Copy(track.Curl, _target[i], _target[i].Length);
                System.Array.Copy(track.Splay, _targetSplay[i], _targetSplay[i].Length);
                next = HandGestures.Classify(track.Curl, _gesture[i]);
            }
            else
            {
                // A controller has three signals, not twenty joints, so the gesture is read from a
                // table and the fingers are posed from the gesture — never the other way round.
                // See `HandGestures.FromController` for why measuring curls here cannot work.
                // Thumb rest is a TOUCH, not a press.
                bool thumbDown = hand.IsButtonPressed(ActPrimaryBtnTouch)
                                 || hand.IsButtonPressed(ActSecondaryBtnTouch)
                                 || hand.IsButtonPressed(ActStickTouch)
                                 || hand.IsButtonPressed(ActPrimaryBtn)
                                 || hand.IsButtonPressed(ActSecondaryBtn)
                                 || hand.IsButtonPressed(ActStickClick)
                                 || StickOf(hand, left: i == 0).LengthSquared() > 0.04f;
                float trigger = hand.GetFloat(ActTrigger);
                float grip = GripOf(hand, left: i == 0);

                next = HandGestures.FromController(thumbDown, trigger > GestureThreshold, grip > GestureThreshold);
                HandGestures.CurlsForGesture(next, _target[i]);
                HandGestures.ApplyAnalogue(next, trigger, grip, thumbDown, _target[i]);
                HandGestures.SplaysForGesture(next, _targetSplay[i]);
            }

            // Fingers travel to the target rather than snapping to it. A hand that changes shape
            // in a single frame is correct in every screenshot and wrong in motion.
            HandGestures.Approach(_curl[i], _target[i], (float)delta);
            HandGestures.Approach(_splay[i], _targetSplay[i], (float)delta);

            if (next != _gesture[i])
            {
                _gesture[i] = next;
                GestureChanged?.Invoke(i, next);
            }
        }

        // Bare hands point with a finger, so the pointer follows whichever hand is tracked.
        // Right wins a tie, matching where the laser lived before hand tracking existed.
        if (IsHandTracked(1)) _pointerHand = 1;
        else if (IsHandTracked(0)) _pointerHand = 0;
        else _pointerHand = 1;
    }

    private readonly Node3D[] _jointMarkers = new Node3D[2];

    private static readonly XRHandTracker.HandJoint[] MarkerJoints =
    {
        XRHandTracker.HandJoint.ThumbTip,
        XRHandTracker.HandJoint.IndexFingerTip,
        XRHandTracker.HandJoint.MiddleFingerTip,
        XRHandTracker.HandJoint.RingFingerTip,
        XRHandTracker.HandJoint.PinkyFingerTip,
    };

    private void PlaceJointMarkers(int i, VrHandTracking track)
    {
        var group = _jointMarkers[i];
        for (int j = 0; j < MarkerJoints.Length && j < group.GetChildCount(); j++)
        {
            if (group.GetChild(j) is not MeshInstance3D m) continue;
            if (track.TryGetJoint(MarkerJoints[j], out var xform))
            {
                m.Visible = true;
                m.GlobalPosition = xform.Origin;
            }
            else m.Visible = false;
        }
    }

    /// Dynamically discover and update Full Body Tracking (FBT) trackers.
    private void UpdateFbtTrackers()
    {
        var trackers = XRServer.GetTrackers((int)XRServer.TrackerType.Any);
        foreach (var name in _fbtTrackerNames)
        {
            bool hasTracker = trackers.ContainsKey(name);
            if (hasTracker)
            {
                if (!_fbtNodes.TryGetValue(name, out var node) || !GodotObject.IsInstanceValid(node))
                {
                    node = new XRNode3D
                    {
                        Name = $"Fbt_{name}",
                        Tracker = name,
                        ShowWhenTracked = true
                    };
                    _origin.AddChild(node);
                    _fbtNodes[name] = node;
                    GD.Print($"VR: Full Body Tracker found and registered: {name}");
                }
            }
            else
            {
                if (_fbtNodes.TryGetValue(name, out var node) && GodotObject.IsInstanceValid(node))
                {
                    node.QueueFree();
                    _fbtNodes.Remove(name);
                    GD.Print($"VR: Full Body Tracker lost: {name}");
                }
            }
        }
    }

    private static void Pulse(XRController3D hand, float amplitude, float seconds)
    {
        if (!UI.DeviceProfile.Settings.VrHaptics) return;
        hand?.TriggerHapticPulse(ActHaptic, 0, amplitude, seconds, 0);
    }

    /// Continuous light vibration while holding a prop — provides tactile awareness that
    /// something is in hand without being annoying.
    private void UpdateHoldHaptics(float dt)
    {
        if (!UI.DeviceProfile.Settings.VrHaptics) return;
        _holdHapticTimer -= dt;
        if (_holdHapticTimer > 0f) return;
        _holdHapticTimer = 0.25f; // 4 Hz pulse train

        for (int i = 0; i < 2; i++)
        {
            if (_heldProp[i] == null) continue;
            var hand = i == 0 ? _leftHand : _rightHand;
            hand?.TriggerHapticPulse(ActHaptic, 0, 0.04f, 0.012f, 0); // barely perceptible
        }
    }

    /// Very subtle haptic ticks synced to the walk cycle so locomotion has presence.
    private void UpdateWalkHaptics(float dt, float planarSpeed)
    {
        if (!UI.DeviceProfile.Settings.VrHaptics) return;
        if (!ControlsEnabled) return;
        if (planarSpeed < 0.3f) { _walkHapticPhase = 0f; return; }

        // Walk cadence: ~2 steps/sec at walk speed, ~3 at sprint.
        float cadence = Mathf.Lerp(2f, 3.5f, Mathf.Clamp(planarSpeed / SprintSpeed, 0f, 1f));
        float prev = _walkHapticPhase;
        _walkHapticPhase += cadence * dt;

        // Fire on each full cycle (one "step").
        if (Mathf.Floor(_walkHapticPhase) > Mathf.Floor(prev))
        {
            float amp = Mathf.Lerp(0.015f, 0.04f, Mathf.Clamp(planarSpeed / SprintSpeed, 0f, 1f));
            _leftHand?.TriggerHapticPulse(ActHaptic, 0, amp, 0.015f, 0);
            _rightHand?.TriggerHapticPulse(ActHaptic, 0, amp, 0.015f, 0);
        }
    }

    // ---------------------------------------------------------------- networking

    /// The transform we broadcast: where the avatar's FEET are, facing the way the body faces.
    /// Remote peers position the avatar from this, and the bone pose rides alongside it.
    ///
    /// This used to send `_camera.GlobalPosition` — the headset, roughly 1.6 m up. The root of a
    /// pose frame is the avatar's origin, and `RemoteAvatar` plants the rig's feet on it, so
    /// every peer saw VR players floating a head-height above the floor and sliding around as
    /// they leaned. `LocalPlayer` has always sent its feet; this now matches.
    ///
    /// The avatar mount is the right source rather than `GlobalPosition`: the mount is literally
    /// where this client draws the avatar (headset XZ at body height, see `UpdateAvatar`), so
    /// peers see it standing exactly where its owner sees it. Facing is the smoothed `_bodyYaw`
    /// for the same reason — the raw head yaw would whip the broadcast avatar around on every
    /// glance, which is precisely what the smoothing exists to prevent locally.
    public Transform3D PoseTransform()
    {
        var fwd = new Vector3(Mathf.Sin(_bodyYaw), 0, Mathf.Cos(_bodyYaw));
        return new Transform3D(Basis.LookingAt(fwd, Vector3.Up), _avatarMount.GlobalPosition);
    }

    /// Zero all momentum — used by respawn, matching `LocalPlayer.ResetMotion`.
    public void ResetMotion()
    {
        Velocity = Vector3.Zero;
        for (int i = 0; i < 2; i++)
        {
            _heldProp[i]?.ReleaseWithVelocity(Vector3.Zero);
            _heldProp[i] = null;
            _handVelocity[i] = Vector3.Zero;
        }
        // Same reason as the desktop rig — and it matters more here, because teleport locomotion
        // moves the body several metres between frames as a matter of routine.
        _avatar?.ResetPhysics();
    }

    // ---------------------------------------------------------------- lifecycle

    /// Check if OpenXR is already initialised (a headset is live).
    public static bool IsVrAvailable()
    {
        var xr = Engine.GetMainLoop() as SceneTree;
        if (xr?.Root == null) return false;
        var interface_ = XRServer.FindInterface("OpenXR");
        return interface_ != null && interface_.IsInitialized();
    }

    /// True when a PCVR session is already happening: OpenXR came up at engine start
    /// (`openxr/enabled=true` + a live runtime), or SteamVR's compositor is running.
    ///
    /// This is "the user is in VR", not "SteamVR is installed". Checking the latter would
    /// launch SteamVR on a normal desktop click. Checking the former is how SteamVR, Steam
    /// Link, ALVR and Wivrn reach this exe without a `--vr` flag.
    public static bool PcVrSessionLooksLive()
    {
        try
        {
            var iface = XRServer.FindInterface("OpenXR");
            if (iface != null && iface.IsInitialized()) return true;

            if (!string.IsNullOrEmpty(OS.GetEnvironment("VR_OVERRIDE")))
                return true;

            foreach (var name in new[] { "vrserver", "vrcompositor", "vrmonitor" })
            {
                if (System.Diagnostics.Process.GetProcessesByName(name).Length > 0)
                    return true;
            }
        }
        catch (System.Exception e)
        {
            GD.Print($"PcVrSessionLooksLive: {e.Message}");
        }
        return false;
    }

    /// Attempt to bring up OpenXR at runtime and route rendering to the headset. Returns true
    /// only if a runtime/headset actually initialises. Called on Quest/Android, `--vr`, or
    /// when `PcVrSessionLooksLive` — a headless desktop never reaches here.
    public static bool TryInitVr()
    {
        try
        {
            var tree = Engine.GetMainLoop() as SceneTree;
            if (tree?.Root == null) { GD.Print("TryInitVr: no scene tree"); return false; }

            var iface = XRServer.FindInterface("OpenXR");
            GD.Print($"TryInitVr: FindInterface(\"OpenXR\") = {(iface != null ? iface.ToString() : "null")}");
            if (iface == null) { GD.Print("TryInitVr: no OpenXR interface found"); return false; }
            GD.Print($"TryInitVr: IsInitialized = {iface.IsInitialized()}");
            if (!iface.IsInitialized() && !iface.Initialize()) { GD.Print("TryInitVr: Initialize() returned false"); return false; }
            GD.Print($"TryInitVr: after Initialize, IsInitialized = {iface.IsInitialized()}");

            if (!iface.IsInitialized()) { GD.Print("TryInitVr: IsInitialized still false after Initialize"); return false; }

            tree.Root.UseXR = true;
            Engine.MaxFps = 0;
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            GD.Print("TryInitVr: XR enabled on viewport, returning true");
            return true;
        }
        catch (System.Exception e)
        {
            GD.Print($"TryInitVr: exception: {e.Message}");
            return false;
        }
    }

    private const float MaxStepHeight = 0.42f;

    private void StepUp(float delta, Vector3 moveDir, float speed)
    {
        if (!IsOnFloor() || moveDir.LengthSquared() < 0.01f || speed < 0.1f) return;

        var horizMove = moveDir * (speed * delta);
        if (horizMove.LengthSquared() < 1e-6f) return;

        var xform = GlobalTransform;
        var testParams = new PhysicsTestMotionParameters3D
        {
            From = xform,
            Motion = horizMove,
            Margin = 0.02f,
        };

        var result = new PhysicsTestMotionResult3D();
        if (PhysicsServer3D.BodyTestMotion(GetRid(), testParams, result))
        {
            var upXform = xform;
            upXform.Origin += Vector3.Up * MaxStepHeight;

            var testUpParams = new PhysicsTestMotionParameters3D
            {
                From = upXform,
                Motion = horizMove,
                Margin = 0.02f,
            };
            var upResult = new PhysicsTestMotionResult3D();
            if (!PhysicsServer3D.BodyTestMotion(GetRid(), testUpParams, upResult))
            {
                var forwardUpXform = upXform;
                forwardUpXform.Origin += horizMove;

                var testDownParams = new PhysicsTestMotionParameters3D
                {
                    From = forwardUpXform,
                    Motion = Vector3.Down * (MaxStepHeight + 0.05f),
                    Margin = 0.02f,
                };
                var downResult = new PhysicsTestMotionResult3D();
                if (PhysicsServer3D.BodyTestMotion(GetRid(), testDownParams, downResult))
                {
                    var colNormal = downResult.GetCollisionNormal();
                    if (colNormal.Y >= 0.65f)
                    {
                        var targetPos = downResult.GetCollisionPoint();
                        float stepRise = targetPos.Y - GlobalPosition.Y;
                        if (stepRise > 0.02f && stepRise <= MaxStepHeight)
                        {
                            GlobalPosition = new Vector3(GlobalPosition.X + horizMove.X, targetPos.Y, GlobalPosition.Z + horizMove.Z);
                        }
                    }
                }
            }
        }
    }
}
