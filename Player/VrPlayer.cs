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
    private const string ActHaptic = "haptic";

    private const float WalkSpeed = 2.2f;
    private const float SprintSpeed = 4.2f;
    private const float JumpVelocity = 4.5f;
    private const float StickDeadzone = 0.18f;
    private const float TurnDeadzone = 0.65f;
    private const float SnapTurnCooldown = 0.28f;
    private const float GrabRadius = 0.18f;
    private const float MaxTeleportRange = 12f;

    // Head tracking smoothing — one-tap latency reduction for standalone headsets.
    // The headset pose arrives at the start of _PhysicsProcess; we predict where it
    // will be at the next display frame by extrapolating the last known velocity.
    private Vector3 _headVel;
    private Vector3 _headAngVel;
    private const float HeadPredictTime = 0.020f; // ~1 frame at 72Hz

    public float MouseSensitivity { get; set; } = 0.003f; // unused in VR; satisfies IPlayer

    /// Raised when the player presses the menu button, so `Main` can open the pause hub.
    public event System.Action MenuPressed;
    /// Raised on the secondary face button — `Main` maps this to the radial action menu.
    public event System.Action ActionMenuPressed;

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
    private bool _menuLatch, _actionLatch, _recenterLatch;

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

    public override void _Ready()
    {
        _gravity = (float)ProjectSettings.GetSetting("physics/3d/default_gravity", 9.8f);

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

        AddHandVisual(_leftHand, new Color(0.45f, 0.62f, 0.95f));
        AddHandVisual(_rightHand, new Color(0.95f, 0.5f, 0.42f));

        _nameTag = new NameTag3D { Name = "NameTag", Position = new Vector3(0, 1.95f, 0) };
        AddChild(_nameTag);
        _nameTag.SetShown(false); // never show your own card

        BuildVignette();
        BuildTeleportVisuals();
        BuildLaser();

        ApplyHeightOffset();
    }

    // ---------------------------------------------------------------- rig construction

    private void AddHandVisual(Node3D parent, Color color)
    {
        var mesh = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.045f, 0.045f, 0.09f) },
        };
        mesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = color, Roughness = 0.5f };
        parent.AddChild(mesh);
    }

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

        if (avatar == null)
        {
            _bodyMesh.Visible = true;
            _nameTag.Position = new Vector3(0, 1.95f, 0);
            return;
        }

        _bodyMesh.Visible = false;
        _avatarMount.AddChild(avatar);
        _nameTag.Position = new Vector3(0, avatar.Height + 0.3f, 0);

        _ik = new VrAvatarIk(avatar);
        if (!_ik.Valid)
        {
            // No usable arm chain (the procedural bean, or a malformed rig). Fall back to the
            // capsule rather than shipping a T-posed avatar into a social space.
            GD.Print("VR: avatar has no solvable arm chain — keeping procedural animation only");
            _ik = null;
        }

        HideOwnHead(avatar);
        ApplyHeightOffset();
    }

    // Same visual layers the desktop rig uses, so mirrors — which render every layer — keep
    // showing a complete avatar while our own camera drops just the head.
    private const uint FpAvatarLayer = 1u << 2; // visual layer 3
    private const uint FpHeadLayer = 1u << 3;   // visual layer 4

    /// Cull the head meshes from our own view only. The meshes stay in the scene so mirrors,
    /// shadows, and every remote peer still see a complete avatar.
    private void HideOwnHead(AvatarInstance avatar)
    {
        foreach (var child in avatar.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is not MeshInstance3D mesh) continue;
            mesh.Layers = avatar.IsHeadMesh(mesh) ? FpHeadLayer : FpAvatarLayer;
        }
        _camera.CullMask = 1048575u & ~FpHeadLayer;
    }

    /// Raise or lower the play space so the avatar's eyes line up with the headset. Without
    /// this a tall avatar on a short player floats, and the hands never reach the IK targets.
    private void ApplyHeightOffset()
    {
        if (_origin == null) return;
        float y = UI.DeviceProfile.Settings.VrHeightOffset;
        _origin.Position = new Vector3(_origin.Position.X, y, _origin.Position.Z);
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

    /// Re-centre the play space so the player faces world-forward from where they stand.
    /// Bound to the secondary button held with the trigger — an accidental recenter mid-session
    /// is disorienting, so it deliberately needs two hands.
    public void Recenter()
    {
        XRServer.CenterOnHmd(XRServer.RotationMode.ResetButKeepTilt, true);
        Pulse(_leftHand, 0.4f, 0.08f);
        Pulse(_rightHand, 0.4f, 0.08f);
    }

    // ---------------------------------------------------------------- frame loop

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;

        // The headset keeps tracking even while a menu is up; only *input* is suspended.
        UpdateHeadVelocity(dt);
        SyncBodyToHead();

        if (ControlsEnabled)
        {
            HandleTurn(dt);
            HandleGrab(dt);
        }

        // Menu buttons stay live while a menu is open — that is how you close it again.
        HandleButtons();
        UpdatePointer();

        var planarSpeed = HandleLocomotion(dt);

        UpdateVignette(dt, planarSpeed);
        UpdateAvatar(dt, planarSpeed);
    }

    /// Physically walking in the guardian moves the camera inside the play space. Carry that
    /// offset onto the `CharacterBody3D` and cancel it out of the origin, so the collider stays
    /// under the headset and the world stays put.
    ///
    /// Also applies predictive head tracking: extrapolates the headset pose forward by ~1 frame
    /// to compensate for render pipeline latency on standalone headsets (Quest 2/3 at 72Hz).
    private void SyncBodyToHead()
    {
        // Predictive head offset: extrapolate from last known velocity.
        var camLocal = _camera.Position;
        var predictedLocal = camLocal + _headVel * HeadPredictTime;
        var flat = new Vector3(predictedLocal.X, 0, predictedLocal.Z);
        if (flat.LengthSquared() < 1e-6f) return;

        var worldDelta = GlobalTransform.Basis * flat;
        GlobalPosition += worldDelta;
        _origin.Position -= flat;
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

    /// Returns planar speed so the caller can drive both the vignette and the walk cycle.
    private float HandleLocomotion(float dt)
    {
        var v = Velocity;
        if (!IsOnFloor()) v.Y -= _gravity * dt;

        var stick = ControlsEnabled ? _leftHand.GetVector2(ActStick) : Vector2.Zero;
        bool teleportMode = UI.DeviceProfile.Settings.VrTeleport;

        if (teleportMode)
        {
            v.X = 0; v.Z = 0;
            UpdateTeleport(stick);
        }
        else
        {
            var move = ApplyDeadzone(stick);
            if (move != Vector2.Zero)
            {
                var (fwd, right) = HeadBasis();
                bool sprinting = _leftHand.GetFloat(ActGrip) > 0.7f;
                float speed = sprinting ? SprintSpeed : WalkSpeed;
                var dir = (fwd * -move.Y + right * move.X) * speed;
                v.X = dir.X;
                v.Z = dir.Z;
            }
            else
            {
                v.X = 0; v.Z = 0;
            }
        }

        Velocity = v;
        MoveAndSlide();
        return new Vector2(Velocity.X, Velocity.Z).Length();
    }

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
        float x = _rightHand.GetVector2(ActStick).X;

        if (UI.DeviceProfile.Settings.VrSnapTurn)
        {
            _snapCooldown -= dt;
            if (_snapCooldown <= 0f && Mathf.Abs(x) > TurnDeadzone)
            {
                float angle = Mathf.DegToRad(UI.DeviceProfile.Settings.VrSnapTurnAngle) * Mathf.Sign(x);
                RotateAroundHead(-angle);
                _snapCooldown = SnapTurnCooldown;
                Pulse(_rightHand, 0.35f, 0.04f);
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
        bool menuOpen = !ControlsEnabled;
        bool tracked = _rightHand.GetHasTrackingData();
        _laser.Visible = menuOpen && tracked;
        if (!menuOpen || !tracked)
        {
            if (_laserDot != null) _laserDot.Visible = false;
            if (_pointerDown) ReleasePointer();
            return;
        }
        if (UiSurface == null) return;

        var origin = _rightHand.GlobalPosition;
        var aim = -_rightHand.GlobalTransform.Basis.Z;

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

        bool pressed = _rightHand.GetFloat(ActTrigger) > 0.6f;
        if (pressed != _pointerDown)
        {
            _pointerDown = pressed;
            UiSurface.Viewport.PushInput(new InputEventMouseButton
            {
                Position = hitPanel ? screen : _pointerPos,
                GlobalPosition = hitPanel ? screen : _pointerPos,
                ButtonIndex = MouseButton.Left,
                Pressed = pressed,
            }, true);
            if (pressed) Pulse(_rightHand, 0.3f, 0.02f);
        }
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

    private void HandleButtons()
    {
        // Jump on the primary face button (A/X), edge-triggered via the floor check. Suppressed
        // while a menu is up — the same button is the menu's select.
        if (ControlsEnabled && _rightHand.IsButtonPressed(ActPrimaryBtn) && IsOnFloor())
            Velocity = Velocity with { Y = JumpVelocity };

        // Menu — latch so a held button opens the menu once.
        bool menu = _leftHand.IsButtonPressed(ActMenu) || _rightHand.IsButtonPressed(ActMenu);
        if (menu && !_menuLatch) MenuPressed?.Invoke();
        _menuLatch = menu;

        bool action = _rightHand.IsButtonPressed(ActSecondaryBtn);
        if (action && !_actionLatch) ActionMenuPressed?.Invoke();
        _actionLatch = action;

        // Two-handed recenter: left secondary + right trigger.
        bool recenter = _leftHand.IsButtonPressed(ActSecondaryBtn) && _rightHand.GetFloat(ActTrigger) > 0.8f;
        if (recenter && !_recenterLatch) Recenter();
        _recenterLatch = recenter;
    }

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
        var pos = hand.GlobalPosition;
        if (dt > 0f)
        {
            var raw = (pos - _lastHandPos[i]) / dt;
            // Light smoothing: a single jittery frame at release should not launch the prop.
            _handVelocity[i] = _handVelocity[i].Lerp(raw, 0.45f);
        }
        _lastHandPos[i] = pos;

        bool gripped = hand.GetFloat(ActGrip) > 0.6f;

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
        bool trigger = hand.GetFloat(ActTrigger) > 0.6f;
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

    /// Push the left stick forward to aim a ballistic arc; release to teleport to where it
    /// lands. Only surfaces flat enough to stand on are accepted.
    private void UpdateTeleport(Vector2 stick)
    {
        bool aiming = ControlsEnabled && stick.Y < -0.5f;

        if (aiming)
        {
            _teleportAiming = true;
            var origin = _leftHand.GlobalPosition;
            var dir = -_leftHand.GlobalTransform.Basis.Z;
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
                Pulse(_leftHand, 0.6f, 0.08f);
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

        // Keep the avatar facing the way the headset faces, and standing where we stand.
        var (fwd, _) = HeadBasis();
        var flatCam = _camera.GlobalPosition with { Y = GlobalPosition.Y };
        _avatarMount.GlobalPosition = flatCam;
        _avatarMount.GlobalBasis = Basis.LookingAt(fwd, Vector3.Up);

        // Procedural locomotion first, then IK overrides the head and arms on top of it.
        _avatar.Animate(dt, speed, IsOnFloor());

        // An emote owns the whole body; letting the hand IK write over it afterwards would
        // reduce a wave or a dance to a twitch.
        if (_avatar.CurrentEmote != AvatarInstance.Emote.None) return;

        // Use predicted head transform for IK — the camera pose is one frame stale by the time
        // the avatar renders, so extrapolating the head position/rotation closes the gap.
        var headTransform = _camera.GlobalTransform;
        if (_headVel.LengthSquared() > 1e-8f || _headAngVel.LengthSquared() > 1e-8f)
        {
            var predictedPos = headTransform.Origin + _headVel * HeadPredictTime;
            var predictedRot = headTransform.Basis.GetRotationQuaternion();
            // Small-angle rotational prediction: apply angular velocity as a swing.
            if (_headAngVel.LengthSquared() > 1e-8f)
            {
                var angAxis = _headAngVel.Normalized();
                float angMag = _headAngVel.Length() * HeadPredictTime;
                predictedRot = new Quaternion(angAxis, angMag) * predictedRot;
            }
            headTransform = new Transform3D(new Basis(predictedRot), predictedPos);
        }
        _ik?.Solve(headTransform, _leftHand.GlobalPosition, _rightHand.GlobalPosition);
    }

    private static void Pulse(XRController3D hand, float amplitude, float seconds)
    {
        if (!UI.DeviceProfile.Settings.VrHaptics) return;
        hand?.TriggerHapticPulse(ActHaptic, 0, amplitude, seconds, 0);
    }

    // ---------------------------------------------------------------- networking

    /// The transform we broadcast: the headset's world position with body yaw. Remote peers
    /// position the avatar from this, and the bone pose rides alongside it.
    public Transform3D PoseTransform()
    {
        var pos = _camera.GlobalPosition;
        var (fwd, _) = HeadBasis();
        return new Transform3D(Basis.LookingAt(fwd, Vector3.Up), pos);
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

    /// Attempt to bring up OpenXR at runtime and route rendering to the headset. Returns true
    /// only if a runtime/headset actually initialises. Auto-init is disabled in project
    /// settings (so a headless desktop never touches OpenXR); this is called deliberately —
    /// only on Android/Quest or when launched with `--vr` — so no HMD means no OpenXR errors.
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
}
