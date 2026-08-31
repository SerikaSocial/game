using Godot;
using SerikaSocial.Avatar;

namespace SerikaSocial.Player;

/// Desktop player controller: WASD move, mouse look, Space jump, Shift sprint, Ctrl crouch.
/// Includes head bob for immersion and a crouch that lowers the camera and collision shape.
/// Produces the transform that Main samples into pose frames.
public partial class LocalPlayer : CharacterBody3D, IPlayer
{
    private const float WalkSpeed = 4.0f;
    private const float SprintSpeed = 7.0f;
    private const float CrouchSpeed = 1.8f;
    private const float JumpVelocity = 4.5f;
    private const float CrouchHeight = 1.2f;
    private const float StandHeight = 1.8f;
    private const float CrouchCameraY = 0.8f;
    private const float StandCameraY = 1.6f;
    private const float BobFrequency = 10f;
    private const float BobAmplitude = 0.04f;
    private const float VoidThreshold = -50f;

    public float MouseSensitivity { get; set; } = 0.003f;

    /// When false (e.g. the chat box is open), movement and mouse-look are suppressed so typed
    /// keys don't walk the avatar around. Gravity still applies.
    public bool ControlsEnabled { get; set; } = true;

    /// Analog movement from touch controls (−1..1 per axis), added to the keyboard vector. Set by
    /// TouchControls on mobile; zero on desktop.
    public Vector2 ExternalMove { get; set; } = Vector2.Zero;
    /// One-shot jump request from a touch button.
    public bool ExternalJump { get; set; }

    /// Raised when the player falls below the void threshold so Main can respawn.
    public event System.Action RespawnRequested;
    /// Held crouch request from touch controls (or diagnostics); OR-ed with the Ctrl key.
    public bool ExternalCrouch { get; set; }
    /// Held sprint request from touch controls (or diagnostics); OR-ed with the Shift key.
    public bool ExternalSprint { get; set; }
    private Vector2 _pendingLook; // accumulated touch look delta (pixels), applied next physics tick

    /// Feed a look delta (in pixels) from touch drag; applied like mouse-look.
    public void AddLook(Vector2 delta) => _pendingLook += delta;

    private Node3D _yaw;      // horizontal look, also the body facing
    private Node3D _pitch;    // vertical look — the orbit pivot the camera hangs off
    private Camera3D _camera;
    private float _gravity = 9.8f;
    private MeshInstance3D _bodyMesh;
    private CollisionShape3D _collision;
    private CapsuleShape3D _capsuleShape;
    private NameTag3D _nameTag;

    private Node3D _avatarMount;      // sits at feet, rotates to match body yaw
    private AvatarInstance _avatar;   // null until an avatar is equipped (capsule shown meanwhile)

    /// The equipped rig, so the network layer can sample its bone pose for streaming.
    public AvatarInstance Avatar => _avatar;
    // Derived from _cameraMode, never stored: this used to be a bool that the 3-way camera
    // cycle stopped updating, which left the third-person camera height being lerped back to
    // zero every frame and head-bob running in third person.
    private bool _firstPerson => _cameraMode == CameraModeEnum.FirstPerson;

    private bool _isCrouching;
    private float _currentHeight = StandHeight;
    private float _currentCameraY = StandCameraY;
    private float _bobTimer;

    // ── First-person eye tracking ────────────────────────────────────────────────
    // While in first person with an avatar equipped, the camera is detached from the yaw/pitch
    // arm (TopLevel): its POSITION is the avatar's animated eye point each tick and its
    // ORIENTATION is purely mouse look. Animations therefore move the viewpoint — a run cycle's
    // chest lean, a crouch's drop, a sit clip's height — instead of the body visibly bouncing
    // away underneath a fixed-height camera, which is what the previous Y-only filter did.
    private bool _fpTopLevel;       // camera is currently riding the skeleton, off the rig arm

    public override void _Ready()
    {
        _gravity = (float)ProjectSettings.GetSetting("physics/3d/default_gravity", 9.8f);

        // Own layer so the camera ray can exclude us, and a mask that includes remote players
        // so we can't walk through them.
        CollisionLayer = PhysicsLayers.LocalPlayer;
        CollisionMask = PhysicsLayers.LocalPlayerMask;

        // Stair and slope traversal
        FloorSnapLength = 0.35f;
        FloorConstantSpeed = true;
        FloorBlockOnWall = false;
        FloorMaxAngle = Mathf.DegToRad(48f);

        // Capsule body + collision.
        _capsuleShape = new CapsuleShape3D { Height = StandHeight, Radius = 0.3f };
        _collision = new CollisionShape3D { Shape = _capsuleShape };
        _collision.Position = new Vector3(0, StandHeight * 0.5f, 0);
        AddChild(_collision);

        var mesh = new MeshInstance3D
        {
            Mesh = new CapsuleMesh { Height = StandHeight, Radius = 0.3f },
            Position = new Vector3(0, StandHeight * 0.5f, 0),
        };
        mesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.9f, 0.7f, 0.2f) };
        AddChild(mesh);
        _bodyMesh = mesh;

        // Look rig: yaw → pitch → camera. Pitch used to live on the camera itself, which meant
        // the third-person camera tilted in place at a fixed offset instead of orbiting the
        // player — look up and the camera stayed behind your shoulder pointing at the sky.
        // Hanging it off a pitch pivot makes both views share one orbit.
        _yaw = new Node3D { Name = "Yaw", Position = new Vector3(0, StandCameraY, 0) };
        AddChild(_yaw);
        _pitch = new Node3D { Name = "Pitch" };
        _yaw.AddChild(_pitch);
        _camera = new Camera3D();
        _pitch.AddChild(_camera);

        // Avatar mount: at the feet, rotated each frame to face where the body faces.
        _avatarMount = new Node3D { Name = "AvatarMount" };
        AddChild(_avatarMount);

        // Name tag above head. Hangs off the body, not the camera: parented to the camera it
        // orbited with the third-person view instead of staying over the avatar's head.
        _nameTag = new NameTag3D { Position = new Vector3(0, StandHeight + 0.3f, 0) };
        AddChild(_nameTag);

        _camQuery = new PhysicsRayQueryParameters3D
        {
            CollisionMask = PhysicsLayers.CameraMask,
            CollideWithAreas = false,
            Exclude = new Godot.Collections.Array<Rid> { GetRid() },
        };

        // The cursor is owned by InputMode; taking it here raced with whatever screen was up
        // when the rig spawned (the loading screen, the tutorial) and stole the pointer from it.
        UI.InputMode.Apply();
    }

    public void SetUsername(string name) => _nameTag.SetLabel(name);

    /// Apply a downloaded profile picture to the name card.
    public void SetProfilePicture(byte[] bytes) => _nameTag.SetProfilePicture(bytes);

    /// Toggle name-tag / profile-picture visibility from settings.
    public void SetTagPrefs(bool tags, bool pfp)
    {
        _nameTag.SetPrefs(tags, pfp);
        // The local player's own card is always hidden — only other players' cards are shown.
        _nameTag.SetShown(false);
    }

    /// Equip a humanoid avatar built from a `.ska`. Hides the capsule stand-in and moves the
    /// eye-level camera to the avatar's measured eye height. Pass null to go back to the capsule.
    public void SetAvatar(AvatarInstance avatar)
    {
        _avatar?.QueueFree();
        _avatar = avatar;

        if (avatar == null)
        {
            _bodyMesh.Visible = true;
            return;
        }

        _bodyMesh.Visible = false;
        _avatarMount.AddChild(avatar);
        _nameTag.Position = new Vector3(0, avatar.Height + 0.3f, 0);
        _standEyeY = avatar.EyeHeight;
        if (!_isCrouching) _currentCameraY = _standEyeY;
        // Split the avatar into its three render representations. Nothing here mutates the
        // original meshes — third person, mirrors and shadows keep the complete avatar, while
        // first person gets a separate copy with every head triangle filtered out.
        FirstPersonProxy.Apply(avatar, AvatarOthersLayer, AvatarFpLayer, AvatarShadowLayer);
        ApplyCameraMode();
    }

    // Standing eye height: the constant until an avatar reports its measured eye level.
    private float _standEyeY = StandCameraY;

    public enum CameraModeEnum { FirstPerson, ThirdPersonBack, ThirdPersonFront }
    private CameraModeEnum _cameraMode = CameraModeEnum.FirstPerson;
    public CameraModeEnum CurrentCameraMode => _cameraMode;

    public CameraModeEnum CycleCameraMode()
    {
        _cameraMode = _cameraMode switch
        {
            CameraModeEnum.FirstPerson => CameraModeEnum.ThirdPersonBack,
            CameraModeEnum.ThirdPersonBack => CameraModeEnum.ThirdPersonFront,
            _ => CameraModeEnum.FirstPerson,
        };
        ApplyCameraMode();
        return _cameraMode;
    }

    public void SetCameraMode(CameraModeEnum mode)
    {
        _cameraMode = mode;
        ApplyCameraMode();
    }

    public void SetFirstPerson(bool firstPerson)
    {
        _cameraMode = firstPerson ? CameraModeEnum.FirstPerson : CameraModeEnum.ThirdPersonBack;
        ApplyCameraMode();
    }

    public bool ToggleCameraMode() => CycleCameraMode() == CameraModeEnum.FirstPerson;

    private void ApplyCameraMode()
    {
        bool isFP = _cameraMode == CameraModeEnum.FirstPerson;

        // The head bone scale is always reset to full — we use render layers, not bone scale,
        // to hide the head in first person. This preserves the mirror reflection and shadow.
        _avatar?.SetHeadVisible(true);

        // Which of the avatar's three representations this camera sees. First person renders the
        // head-filtered proxy and nothing else of the avatar; every other mode renders the
        // untouched originals and must not also draw the proxy on top of them.
        _camera.CullMask = 1048575u & ~(isFP ? FpCullLayers : NonFpCullLayers);

        // Near clip: tight in first person so the forward-pushed camera doesn't clip the face;
        // default otherwise.
        _camera.Near = isFP ? FpNear : 0.05f;

        // The camera's *offset* is solved per-frame in SolveCamera (it depends on geometry);
        // all that's fixed per mode is which way it faces. The selfie view looks back down the
        // arm at the player, so it's the one that needs a yaw flip.
        _camera.Rotation = _cameraMode == CameraModeEnum.ThirdPersonFront
            ? new Vector3(0, Mathf.Pi, 0)
            : Vector3.Zero;

        // Snap rather than glide when the player deliberately switches view.
        _camDistance = isFP ? 0f : _thirdPersonDistance;

        // First person's range is narrower than the orbit's, so entering it from a steep orbit
        // has to pull the pivot back inside a neck's reach.
        ClampPitch();

        // The local player's own card is always hidden — only other players' cards are shown.
        _nameTag.SetShown(false);
    }

    // First-person near clip. Tight, because the camera sits exactly where the eyes are —
    // inside the skull — and anything within a few centimetres (lashes, fringe tips) must be
    // clipped rather than seen.
    private const float FpNear = 0.03f;

    /// Owns camera placement while in first person with an avatar equipped. Runs every physics
    /// tick from both the free-move and the seated paths, AFTER Animate() has posed the skeleton,
    /// so the sample reflects this frame's final animation state.
    ///
    /// Rotation stays mouse-driven (yaw node × pitch node, composed by hand here because the
    /// camera no longer hangs beneath them). Position is the avatar's exact animated eye point
    /// from AvatarInstance.TryGetEyeGlobal — assigned DIRECTLY, never smoothed. That is the
    /// whole fix for "the camera goes out of the head": any filter, however fast, lags the
    /// sprint bob by a frame or two, and a lagging eye point sits behind the skull looking at
    /// the avatar's neck. Direct tracking keeps the camera inside the head through every
    /// animation, which is also what lets the hair stay visible — it frames the view from
    /// around the head instead of walling off a camera stranded in the hair mass behind it.
    private void UpdateFirstPersonCamera()
    {
        if (!_firstPerson || _avatar?.TryGetEyeGlobal(out var eyeXf) != true)
        {
            DetachFirstPersonCamera();
            return; // capsule mode / degenerate rig falls back to the static rig + bob below
        }

        // Mouse look, recomposed: world rotation = yaw around Y then pitch around X — exactly
        // what the yaw→pitch node chain produced when the camera still hung off it.
        var look = new Quaternion(Vector3.Up, _yaw.Rotation.Y)
                 * new Quaternion(Vector3.Right, _pitch.Rotation.X);
        var basis = new Basis(look);

        if (!_fpTopLevel)
        {
            _camera.TopLevel = true;
            _fpTopLevel = true;
        }

        _camera.GlobalTransform = new Transform3D(basis, eyeXf.Origin);
    }

    /// Return the camera to the third-person rig arm. ApplyCameraMode re-runs to restore the
    /// mode's rotation/near/cull settings idempotently — it may have executed while the camera
    /// was TopLevel and wrote properties (e.g. the selfie flip) that only make sense once the
    /// camera is re-parented behaviourally.
    private void DetachFirstPersonCamera()
    {
        if (!_fpTopLevel) return;
        _fpTopLevel = false;
        _camera.TopLevel = false;
        _camera.Position = Vector3.Zero;
        ApplyCameraMode();
    }

    // Legacy avatar render layers: whole meshes split into "body" and "strictly head". Still used
    // by the shadow diagnostic's fixture; the live rigs use the three-representation layers below.
    public const uint FpAvatarLayer = 1u << 2; // visual layer 3
    public const uint FpHeadLayer = 1u << 3;   // visual layer 4

    // The local avatar's three representations (see Avatar/FirstPersonProxy.cs). Every camera
    // renders exactly one of the first two; the third is ShadowsOnly and never appears in a
    // colour pass at all.
    /// The untouched original meshes — third person, mirrors, and every shadow pass.
    public const uint AvatarOthersLayer = 1u << 5; // visual layer 6
    /// The head-filtered proxy — only the local player's own first-person camera.
    public const uint AvatarFpLayer = 1u << 6;     // visual layer 7
    /// The complete ShadowsOnly copy — keeps the silhouette whole in the FP camera's shadow pass.
    public const uint AvatarShadowLayer = 1u << 7; // visual layer 8

    /// Every layer a shadow-casting light must keep for avatars. Shadow lookup is per-LIGHT
    /// (mask ∩ shadow_caster_mask), never per-camera, so lights keeping these bits keep hair
    /// and face present in your cast shadow even while no first-person camera renders them.
    /// DeviceProfile.ApplyToScene stamps this onto all lights on every world load.
    public const uint AvatarRenderLayers =
        FpAvatarLayer | FpHeadLayer | AvatarOthersLayer | AvatarFpLayer | AvatarShadowLayer;

    /// What a local first-person camera must NOT render: the originals (their head is intact) and
    /// the legacy head layer. The proxy on AvatarFpLayer takes their place.
    public const uint FpCullLayers = AvatarOthersLayer | FpHeadLayer;

    /// What every OTHER camera — third person, mirrors, diagnostics — must not render: the
    /// first-person proxy, which is a headless duplicate of geometry they already show.
    public const uint NonFpCullLayers = AvatarFpLayer;

    // Camera collision. The camera used to sit at a hard-coded offset behind the player, so it
    // happily sank into walls and through the floor whenever the player backed into something.
    // Now the desired offset is raycast from the orbit pivot each frame and pulled in to the
    // first thing it hits.
    private const float CameraPullMargin = 0.28f;  // keep the near plane clear of the surface
    private const float CameraMinDistance = 0.55f; // never so close it ends up inside our own head
    private PhysicsRayQueryParameters3D _camQuery;
    private float _camDistance;

    private void SolveCamera(double delta)
    {
        if (_cameraMode == CameraModeEnum.FirstPerson)
        {
            _camDistance = 0f;
            return;
        }

        // Behind the shoulder for the back view, in front of the face for the selfie view.
        // -Z is forward in Godot, so "behind" is +Z.
        float sign = _cameraMode == CameraModeEnum.ThirdPersonBack ? 1f : -1f;
        var pivot = _pitch.GlobalPosition;
        var dir = _pitch.GlobalBasis * new Vector3(0, 0, sign);
        float want = _thirdPersonDistance;

        var space = GetWorld3D().DirectSpaceState;
        _camQuery.From = pivot;
        _camQuery.To = pivot + dir * (want + CameraPullMargin);
        var hit = space.IntersectRay(_camQuery);
        if (hit.Count > 0 && hit.TryGetValue("position", out var p))
        {
            float d = pivot.DistanceTo(p.AsVector3()) - CameraPullMargin;
            want = Mathf.Max(CameraMinDistance, Mathf.Min(want, d));
        }

        // Snapping *in* is instant (a wall must never be crossed, even for a frame); easing
        // *out* is smoothed, otherwise the camera pops the moment you clear a doorway.
        _camDistance = want < _camDistance
            ? want
            : Mathf.Lerp(_camDistance, want, (float)delta * 8f);
    }

    /// Zero all momentum on respawn. Also releases any seat — respawning out of a chair while
    /// still bound to it left the anchor pinning us straight back into it every frame.
    public void ResetMotion()
    {
        StandUp();
        Velocity = Vector3.Zero;
        PlayEmote(AvatarInstance.Emote.None);
        // Secondary physics runs in world space, so a respawn across the map reads to the solver
        // as a several-metre-per-frame acceleration and throws hair and skirt horizontal.
        _avatar?.ResetPhysics();
    }

    // ── Sitting / lying ──────────────────────────────────────────────────────────────

    private World.IOccupiable _occupying;

    /// The seat or bed we're currently bound to, or null when standing.
    public World.IOccupiable Occupying => _occupying;

    /// Where the interaction ray starts and which way it points — the eye, not the camera, so
    /// the reach is the same in first and third person.
    public Vector3 EyePosition => _yaw.GlobalPosition;
    public Vector3 AimForward => -_pitch.GlobalBasis.Z;

    /// Bind to a seat/bed: snap onto its anchor, face the way it faces, hold its pose.
    public void Occupy(World.IOccupiable spot)
    {
        if (spot == null) return;
        if (_occupying != null && !ReferenceEquals(_occupying, spot)) StandUp();

        _occupying = spot;
        Velocity = Vector3.Zero;

        // The anchor is where the avatar's hips should land (the seat surface). The player
        // origin is at the feet, so subtract the seated hip height to put the feet on the
        // ground below the seat. When sitting with legs bent 90°, the hips drop to roughly
        // knee level — about half the standing hip height.
        float hipH = (_avatar?.HipHeight ?? 0.9f) * 0.5f;
        GlobalPosition = spot.AnchorPosition - new Vector3(0, hipH, 0);

        var yr = _yaw.Rotation;
        yr.Y = spot.AnchorYaw;
        _yaw.Rotation = yr;

        var mr = _avatarMount.Rotation;
        mr.Y = spot.AnchorYaw;
        _avatarMount.Rotation = mr;

        // Set the emote directly rather than through PlayEmote, which toggles: sitting down on
        // a seat while already mid-Sit emote would have cancelled the pose instead of holding it.
        _avatar?.PlayEmote(spot.Pose);

        // Lower camera height so third-person orbits at seated eye level. First person reads
        // the real seated pose out of the Sit clip's skeleton instead (UpdateFirstPersonCamera).
        _currentCameraY = _standEyeY * 0.72f;
    }

    /// Release the current seat/bed. Safe to call when already standing.
    public void StandUp()
    {
        if (_occupying == null) return;
        var spot = _occupying;
        _occupying = null;
        spot.Vacate();
        _avatar?.PlayEmote(AvatarInstance.Emote.None);

        _currentCameraY = _standEyeY;

        // Step clear of the anchor, otherwise we're still inside the seat's trigger volume and
        // the prompt immediately offers to sit back down. The anchor is at hip height, so
        // subtract the avatar's hip height to land the feet on the ground.
        var forward = new Vector3(Mathf.Sin(spot.AnchorYaw), 0, Mathf.Cos(spot.AnchorYaw));
        float hipH = (_avatar?.HipHeight ?? 0.9f) * 0.5f;
        GlobalPosition = spot.AnchorPosition + forward * 0.7f - new Vector3(0, hipH, 0);
        Velocity = Vector3.Zero;
    }

    /// Play one of the equipped avatar's own custom clips by name (empty = stop it).
    public void PlayCustomEmote(string clip)
    {
        if (_avatar == null) return;
        if (string.IsNullOrEmpty(clip)) _avatar.StopCustomEmote();
        else _avatar.PlayCustomEmote(clip);
    }

    /// Trigger an emote animation on the equipped avatar.
    public void PlayEmote(AvatarInstance.Emote emote)
    {
        if (_avatar == null) return;
        if (_avatar.CurrentEmote == emote)
            _avatar.PlayEmote(AvatarInstance.Emote.None);
        else
            _avatar.PlayEmote(emote);
    }

    // Pitch limits. In first person the camera IS the avatar's head, so it may only travel as
    // far as a neck does. Past roughly 60° of flexion the viewpoint clears the chin, and with the
    // head geometry filtered out of this view there is nothing left to stop it looking straight
    // down the collar into the chest — a shot no human head can take. Extension (looking up) is
    // the tighter of the two on a real neck. Third person is an orbit around the body rather than
    // a head, so it keeps the old, wider range.
    private const float FpPitchDown = 1.05f;    // 60° — cervical flexion
    private const float FpPitchUp = 0.96f;      // 55° — cervical extension
    private const float OrbitPitchLimit = 1.4f; // 80° — free orbit

    /// Hold the look pivot inside the active mode's pitch range. Called from both look paths and
    /// from ApplyCameraMode, so dropping out of a steep third-person orbit into first person
    /// lands inside the neck's range instead of starting outside it.
    private void ClampPitch()
    {
        var r = _pitch.Rotation;
        r.X = _firstPerson
            ? Mathf.Clamp(r.X, -FpPitchDown, FpPitchUp)
            : Mathf.Clamp(r.X, -OrbitPitchLimit, OrbitPitchLimit);
        _pitch.Rotation = r;
    }

    private const float ThirdPersonCameraY = 0.35f;
    private const float ThirdPersonMin = 1.2f;
    private const float ThirdPersonMax = 6.0f;
    private float _thirdPersonDistance = 2.0f;

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!ControlsEnabled) return;
        if (@event is InputEventMouseMotion m && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            _yaw.RotateY(-m.Relative.X * MouseSensitivity);
            _pitch.RotateX(-m.Relative.Y * MouseSensitivity);
            ClampPitch();
        }

        // Scroll wheel zooms the third-person camera.
        if (!_firstPerson && @event is InputEventMouseButton { Pressed: true } mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp)
                _thirdPersonDistance = Mathf.Clamp(_thirdPersonDistance - 0.4f, ThirdPersonMin, ThirdPersonMax);
            else if (mb.ButtonIndex == MouseButton.WheelDown)
                _thirdPersonDistance = Mathf.Clamp(_thirdPersonDistance + 0.4f, ThirdPersonMin, ThirdPersonMax);
            else
                return;
            ApplyCameraMode();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        var v = Velocity;
        bool onFloor = IsOnFloor();

        // Touch look — apply accumulated drag like mouse-look (yaw + clamped pitch).
        if (ControlsEnabled && _pendingLook != Vector2.Zero)
        {
            _yaw.RotateY(-_pendingLook.X * MouseSensitivity);
            _pitch.RotateX(-_pendingLook.Y * MouseSensitivity);
            ClampPitch();
        }
        _pendingLook = Vector2.Zero;

        // Seated/lying: the anchor owns our position, so movement, gravity and locomotion all
        // stop. Look is still live — you can glance around from a chair. The pose is held by
        // the emote the seat asked for. Pressing WASD, Space/Jump or moving steps out of the seat.
        if (_occupying != null)
        {
            if (ControlsEnabled)
            {
                var moveInput = Input.GetVector("move_left", "move_right", "move_forward", "move_backward");
                if (ExternalMove.LengthSquared() > 0.01f) moveInput = ExternalMove;
                bool jumpInput = Input.IsPhysicalKeyPressed(Key.Space) || ExternalJump;
                if (moveInput.LengthSquared() > 0.05f || jumpInput)
                {
                    StandUp();
                    ExternalJump = false;
                }
            }

            if (_occupying != null)
            {
                Velocity = Vector3.Zero;
                GlobalPosition = _occupying.AnchorPosition;
                UpdateLookAim();
                _avatar?.Animate(delta, 0f, true, false, false);
                SolveCamera(delta);
                UpdateFirstPersonCamera();
                UpdateCameraOffset(delta, moving: false, sprinting: false);
                return;
            }
        }

        if (!onFloor) v.Y -= _gravity * (float)delta;
        bool wantJump = ControlsEnabled && (Input.IsPhysicalKeyPressed(Key.Space) || ExternalJump);
        if (wantJump && onFloor && !_isCrouching)
            v.Y = JumpVelocity;
        ExternalJump = false;

        // Crouch toggle
        bool wantCrouch = ControlsEnabled && (Input.IsPhysicalKeyPressed(Key.Ctrl) || ExternalCrouch);
        if (wantCrouch != _isCrouching)
        {
            _isCrouching = wantCrouch;
            _currentHeight = _isCrouching ? CrouchHeight : StandHeight;
            // Crouched pivot height scales with the avatar rather than using a fixed 0.8 m.
            // Third-person only these days: in first person the CrouchIdle/CrouchWalk clips
            // drop the head bone themselves and the tracked eye follows them.
            _currentCameraY = _isCrouching ? _standEyeY * (CrouchHeight / StandHeight) : _standEyeY;
            _capsuleShape.Height = _currentHeight;
            _collision.Position = new Vector3(0, _currentHeight * 0.5f, 0);
            if (_bodyMesh.Mesh is CapsuleMesh cm) cm.Height = _currentHeight;
            _bodyMesh.Position = new Vector3(0, _currentHeight * 0.5f, 0);
        }

        // Avatar body faces where we're looking (yaw only — pitch is head/camera).
        if (_avatar != null)
        {
            var rot = _avatarMount.Rotation;
            rot.Y = _yaw.Rotation.Y;
            _avatarMount.Rotation = rot;
        }

        // Orbit pivot height — third person, the interaction ray and the name tag all hang off
        // the yaw node, so it keeps a computed standing/crouched/seated eye level. First person
        // no longer reads it: UpdateFirstPersonCamera rides the animated skeleton instead,
        // which is what keeps a run cycle's lean or a crouch's drop from detaching the view
        // from the body.
        var yawPos = _yaw.Position;
        yawPos.Y = Mathf.Lerp(yawPos.Y, _currentCameraY, (float)delta * 10f);
        _yaw.Position = yawPos;

        // Movement is relative to where we're looking (yaw only). Zeroed while controls are off
        // (e.g. typing in chat) so WASD text doesn't drive the avatar.
        var input = Vector2.Zero;
        if (ControlsEnabled)
        {
            input = new Vector2(
                (Input.IsPhysicalKeyPressed(Key.D) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.A) ? 1 : 0),
                (Input.IsPhysicalKeyPressed(Key.S) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.W) ? 1 : 0)) + ExternalMove;
            if (input.LengthSquared() > 1f) input = input.Normalized();
        }
        var dir = (_yaw.GlobalTransform.Basis * new Vector3(input.X, 0, input.Y)) with { Y = 0 };
        if (dir.LengthSquared() > 0) dir = dir.Normalized();

        float speed = _isCrouching ? CrouchSpeed : WalkSpeed;
        bool sprinting = (Input.IsPhysicalKeyPressed(Key.Shift) || ExternalSprint)
                         && !_isCrouching && input.Y < 0;
        if (sprinting) speed = SprintSpeed;

        v.X = dir.X * speed;
        v.Z = dir.Z * speed;
        Velocity = v;

        StepUp((float)delta, dir, speed);
        MoveAndSlide();

        if (GlobalPosition.Y < VoidThreshold) RespawnRequested?.Invoke();

        // Animate the equipped avatar from actual movement state (embedded clips override this).
        var planar = new Vector2(Velocity.X, Velocity.Z);
        if (_avatar != null)
        {
            // Which way we're travelling relative to facing, so the avatar plays walk-back and
            // strafe clips instead of a forward walk in every direction. Forward/back wins over
            // strafe when both are held, matching how the movement itself reads.
            _avatar.MoveDir =
                input.Y < -0.3f ? AvatarInstance.MoveDirection.Forward :
                input.Y > 0.3f ? AvatarInstance.MoveDirection.Back :
                input.X > 0.3f ? AvatarInstance.MoveDirection.Right :
                input.X < -0.3f ? AvatarInstance.MoveDirection.Left :
                AvatarInstance.MoveDirection.Forward;
            UpdateLookAim();
            _avatar.Animate(delta, planar.Length(), IsOnFloor(), _isCrouching, sprinting);
        }

        SolveCamera(delta);
        UpdateFirstPersonCamera();
        UpdateCameraOffset(delta, input.LengthSquared() > 0.01f && IsOnFloor(), sprinting);
    }

    /// Place the camera on the solved arm, plus head bob. Bob is first-person only — in third
    /// person it just makes the whole frame wobble.
    ///
    /// First person with an avatar is owned by UpdateFirstPersonCamera and skips everything
    /// here. This method still serves two first-person cases: the capsule stand-in (no avatar),
    /// where synthetic bob is all the life the view has, and a rig whose eye probe never became
    /// ready — a broken avatar must degrade to the old static camera rather than to no camera.
    private void UpdateCameraOffset(double delta, bool moving, bool sprinting)
    {
        if (_firstPerson && _fpTopLevel) return;

        float baseCamY = _firstPerson ? 0f : ThirdPersonCameraY;
        float targetY;

        if (moving && _firstPerson)
        {
            _bobTimer += (float)delta * BobFrequency * (sprinting ? 1.4f : 1f);
            targetY = baseCamY + Mathf.Sin(_bobTimer) * BobAmplitude * (sprinting ? 1.5f : 1f);
        }
        else
        {
            _bobTimer = 0;
            targetY = Mathf.Lerp(_camera.Position.Y, baseCamY, (float)delta * 8f);
        }

        if (_firstPerson)
        {
            _camera.Position = new Vector3(0, targetY, 0);
            return;
        }

        float sign = _cameraMode == CameraModeEnum.ThirdPersonFront ? -1f : 1f;
        _camera.Position = new Vector3(0, targetY, _camDistance * sign);
    }

    /// Hand the mouse's look angle to the avatar so the head bone points where the player is
    /// looking. Call immediately before `Animate`, which applies it as its last stage.
    ///
    /// This is what closes the desktop half of head aim. The broadcast transform still carries
    /// body yaw only (see `PoseTransform`) — the pitch reaches peers through the head and neck
    /// bones, which `HumanoidBones.Lod1` already streams, so no wire change is involved.
    ///
    /// Yaw offset is zero: the body snaps to the mouse yaw every tick a few lines above, so the
    /// head has nothing left to make up. It is a parameter rather than a constant because a
    /// future body-lag pass would need exactly this to keep the head on target while the torso
    /// catches up.
    private void UpdateLookAim() => _avatar?.SetLookAim(0f, _pitch.Rotation.X);

    /// The transform we broadcast: body position, facing = yaw. Pitch reaches peers through the
    /// head/neck bones in the pose frame, not through this basis.
    public Transform3D PoseTransform()
    {
        var basis = new Basis(_yaw.Basis.GetRotationQuaternion());
        return new Transform3D(basis, GlobalPosition);
    }

    private const float MaxStepHeight = 0.55f;

    private void StepUp(float delta, Vector3 moveDir, float speed)
    {
        if (!IsOnFloor() || moveDir.LengthSquared() < 0.001f || speed < 0.1f) return;

        var horizMove = moveDir.Normalized() * Mathf.Max(speed * delta, 0.08f);
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
            upXform.Origin += Vector3.Up * (MaxStepHeight + 0.05f);

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
                    Motion = Vector3.Down * (MaxStepHeight + 0.1f),
                    Margin = 0.02f,
                };
                var downResult = new PhysicsTestMotionResult3D();
                if (PhysicsServer3D.BodyTestMotion(GetRid(), testDownParams, downResult))
                {
                    var colNormal = downResult.GetCollisionNormal();
                    if (colNormal.Y >= 0.55f)
                    {
                        var targetPos = downResult.GetCollisionPoint();
                        float stepRise = targetPos.Y - GlobalPosition.Y;
                        if (stepRise > 0.02f && stepRise <= MaxStepHeight)
                        {
                            GlobalPosition = new Vector3(GlobalPosition.X + horizMove.X, targetPos.Y + 0.02f, GlobalPosition.Z + horizMove.Z);
                        }
                    }
                }
            }
        }
    }
}
