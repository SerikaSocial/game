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

    public float MouseSensitivity { get; set; } = 0.003f;

    /// When false (e.g. the chat box is open), movement and mouse-look are suppressed so typed
    /// keys don't walk the avatar around. Gravity still applies.
    public bool ControlsEnabled { get; set; } = true;

    /// Analog movement from touch controls (−1..1 per axis), added to the keyboard vector. Set by
    /// TouchControls on mobile; zero on desktop.
    public Vector2 ExternalMove { get; set; } = Vector2.Zero;
    /// One-shot jump request from a touch button.
    public bool ExternalJump { get; set; }
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
    private float _smoothedHeadY; // low-pass filtered head bone Y for first-person camera

    public override void _Ready()
    {
        _gravity = (float)ProjectSettings.GetSetting("physics/3d/default_gravity", 9.8f);

        // Own layer so the camera ray can exclude us, and a mask that includes remote players
        // so we can't walk through them.
        CollisionLayer = PhysicsLayers.LocalPlayer;
        CollisionMask = PhysicsLayers.LocalPlayerMask;

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
        // In first person the tag is always hidden regardless of the preference.
        if (_firstPerson) _nameTag.SetShown(false);
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
        _smoothedHeadY = _currentCameraY;
        // Place avatar visuals on render layers: head meshes go on FpHeadLayer (culled in
        // first-person), body meshes go on FpAvatarLayer (visible in all modes). This gives
        // a VRChat-style first-person view where you can see your hands, torso, and legs
        // but not the inside of your own head.
        SetAvatarVisualLayers(avatar, FpAvatarLayer, FpHeadLayer);
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

        // In first person: cull only the head layer (FpHeadLayer) so the player sees their
        // hands, torso, and legs — VRChat-style. In third person: render everything.
        _camera.CullMask = isFP ? 1048575u & ~FpHeadLayer : 1048575u;

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

        _nameTag.SetShown(!isFP);
    }

    // First-person eye placement. The camera is pushed forward from the head bone toward where
    // the eyes are, so it sits in front of the face. The avatar's body (hands, torso, legs) is
    // visible in first-person — like VRChat — while the head is hidden via a separate render
    // layer (FpHeadLayer). The mirror camera renders all layers, so the reflection shows the
    // full avatar including the head.
    private const float FpEyeForward = 0.10f;
    private const float FpNear = 0.04f;

    // Render layer for the local player's avatar body (hands, torso, legs). Visible in ALL modes
    // including first-person, so the player can see their own body — VRChat-style.
    private const uint FpAvatarLayer = 1u << 2; // visual layer 3

    // Render layer for the local player's avatar head mesh only. Culled in first-person so the
    // inside of the skull doesn't block the camera; visible in third-person and mirror.
    private const uint FpHeadLayer = 1u << 3; // visual layer 4

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
        GlobalPosition = spot.AnchorPosition;

        var yr = _yaw.Rotation;
        yr.Y = spot.AnchorYaw;
        _yaw.Rotation = yr;

        var mr = _avatarMount.Rotation;
        mr.Y = spot.AnchorYaw;
        _avatarMount.Rotation = mr;

        // Set the emote directly rather than through PlayEmote, which toggles: sitting down on
        // a seat while already mid-Sit emote would have cancelled the pose instead of holding it.
        _avatar?.PlayEmote(spot.Pose);
    }

    /// Release the current seat/bed. Safe to call when already standing.
    public void StandUp()
    {
        if (_occupying == null) return;
        var spot = _occupying;
        _occupying = null;
        spot.Vacate();
        _avatar?.PlayEmote(AvatarInstance.Emote.None);

        // Step clear of the anchor, otherwise we're still inside the seat's trigger volume and
        // the prompt immediately offers to sit back down.
        var forward = new Vector3(Mathf.Sin(spot.AnchorYaw), 0, Mathf.Cos(spot.AnchorYaw));
        GlobalPosition = spot.AnchorPosition + forward * 0.7f;
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
            var r = _pitch.Rotation;
            r.X = Mathf.Clamp(r.X, -1.4f, 1.4f);
            _pitch.Rotation = r;
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
            var cr = _pitch.Rotation;
            cr.X = Mathf.Clamp(cr.X, -1.4f, 1.4f);
            _pitch.Rotation = cr;
        }
        _pendingLook = Vector2.Zero;

        // Seated/lying: the anchor owns our position, so movement, gravity and locomotion all
        // stop. Look is still live — you can glance around from a chair. The pose is held by
        // the emote the seat asked for, so nothing here has to drive the rig.
        if (_occupying != null)
        {
            Velocity = Vector3.Zero;
            GlobalPosition = _occupying.AnchorPosition;
            _avatar?.Animate(delta, 0f, true, false, false);
            SolveCamera(delta);
            UpdateCameraOffset(delta, moving: false, sprinting: false);
            return;
        }

        if (!onFloor) v.Y -= _gravity * (float)delta;
        bool wantJump = ControlsEnabled && (Input.IsPhysicalKeyPressed(Key.Space) || ExternalJump);
        if (wantJump && onFloor && !_isCrouching)
            v.Y = JumpVelocity;
        ExternalJump = false;

        // Crouch toggle
        bool wantCrouch = ControlsEnabled && Input.IsPhysicalKeyPressed(Key.Ctrl);
        if (wantCrouch != _isCrouching)
        {
            _isCrouching = wantCrouch;
            _currentHeight = _isCrouching ? CrouchHeight : StandHeight;
            // Crouched eye height scales with the avatar rather than using a fixed 0.8 m, which
            // sat above a short avatar's head and below a tall one's shoulders.
            _currentCameraY = _isCrouching ? _standEyeY * (CrouchHeight / StandHeight) : _standEyeY;
            _smoothedHeadY = _currentCameraY;
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

        // Eye height. In first person, follow the rig's actual head bone rather than a computed
        // constant: the crouch clip drops the pelvis by an amount only the animation knows, so a
        // fixed crouch height left the camera buried inside the avatar's own shoulders.
        // A low-pass filter on the head Y smooths out rapid oscillations from run/walk cycles
        // (which dipped the camera into the torso) while still tracking the slower crouch drop.
        float targetCamY = _currentCameraY;
        if (_firstPerson && _avatar != null && _avatar.TryGetHeadGlobal(out var headXf))
        {
            float localHeadY = ToLocal(headXf.Origin).Y;
            if (localHeadY > 0.2f)
            {
                _smoothedHeadY = Mathf.Lerp(_smoothedHeadY, localHeadY, (float)delta * 3f);
                targetCamY = _smoothedHeadY;
            }
        }

        var yawPos = _yaw.Position;
        yawPos.Y = Mathf.Lerp(yawPos.Y, targetCamY, (float)delta * 10f);
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
        bool sprinting = Input.IsPhysicalKeyPressed(Key.Shift) && !_isCrouching && input.Y < 0;
        if (sprinting) speed = SprintSpeed;

        v.X = dir.X * speed;
        v.Z = dir.Z * speed;
        Velocity = v;
        MoveAndSlide();

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
            _avatar.Animate(delta, planar.Length(), IsOnFloor(), _isCrouching, sprinting);
        }

        SolveCamera(delta);
        UpdateCameraOffset(delta, input.LengthSquared() > 0.01f && IsOnFloor(), sprinting);
    }

    /// Place the camera on the solved arm, plus head bob. Bob is first-person only — in third
    /// person it just makes the whole frame wobble.
    private void UpdateCameraOffset(double delta, bool moving, bool sprinting)
    {
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
            // Forward is -Z; push the eye ahead of the head bone so we look out of the face,
            // not out of the middle of the skull.
            _camera.Position = new Vector3(0, targetY, -FpEyeForward);
            return;
        }

        float sign = _cameraMode == CameraModeEnum.ThirdPersonFront ? -1f : 1f;
        _camera.Position = new Vector3(0, targetY, _camDistance * sign);
    }

    /// Recursively assign render layers to avatar visuals: head meshes → headLayer (culled in
    /// first-person), all other visuals → bodyLayer (visible in all modes).
    private void SetAvatarVisualLayers(Node root, uint bodyLayer, uint headLayer)
    {
        if (root is MeshInstance3D mesh)
        {
            bool isHead = _avatar != null && _avatar.IsHeadMesh(mesh);
            mesh.Layers = isHead ? headLayer : bodyLayer;
        }
        else if (root is VisualInstance3D vi)
        {
            vi.Layers = bodyLayer;
        }
        foreach (var child in root.GetChildren())
            SetAvatarVisualLayers(child, bodyLayer, headLayer);
    }

    /// The transform we broadcast: body position, facing = yaw. Pitch stays local (head).
    public Transform3D PoseTransform()
    {
        var basis = new Basis(_yaw.Basis.GetRotationQuaternion());
        return new Transform3D(basis, GlobalPosition);
    }

}
