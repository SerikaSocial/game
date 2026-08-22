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
    private Camera3D _camera; // pitch
    private float _gravity = 9.8f;
    private MeshInstance3D _bodyMesh;
    private CollisionShape3D _collision;
    private CapsuleShape3D _capsuleShape;
    private Label3D _nameTag;

    private Node3D _avatarMount;      // sits at feet, rotates to match body yaw
    private AvatarInstance _avatar;   // null until an avatar is equipped (capsule shown meanwhile)
    private bool _firstPerson = true; // camera mode; the FP/TP toggle lives in Main

    private bool _isCrouching;
    private float _currentHeight = StandHeight;
    private float _currentCameraY = StandCameraY;
    private float _bobTimer;

    public override void _Ready()
    {
        _gravity = (float)ProjectSettings.GetSetting("physics/3d/default_gravity", 9.8f);

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

        _yaw = new Node3D { Name = "Yaw", Position = new Vector3(0, StandCameraY, 0) };
        AddChild(_yaw);
        _camera = new Camera3D();
        _yaw.AddChild(_camera);

        // Avatar mount: at the feet, rotated each frame to face where the body faces.
        _avatarMount = new Node3D { Name = "AvatarMount" };
        AddChild(_avatarMount);

        // Name tag above head
        _nameTag = new Label3D
        {
            Position = new Vector3(0, 0.5f, 0),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            FontSize = 48,
            PixelSize = 0.005f,
        };
        _camera.AddChild(_nameTag);

        // Headless smoke runs have no window to capture the mouse in.
        if (!DisplayServer.GetName().Equals("headless"))
            Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    public void SetUsername(string name) => _nameTag.Text = name;

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
        _standEyeY = avatar.EyeHeight;
        if (!_isCrouching) _currentCameraY = _standEyeY;
        ApplyCameraMode();
    }

    // Standing eye height: the constant until an avatar reports its measured eye level.
    private float _standEyeY = StandCameraY;

    /// Toggle first/third person. In first person we hide the avatar's head so it doesn't clip
    /// the camera; in third person we pull the camera back along the look direction.
    public void SetFirstPerson(bool firstPerson)
    {
        _firstPerson = firstPerson;
        ApplyCameraMode();
    }

    public bool ToggleCameraMode()
    {
        SetFirstPerson(!_firstPerson);
        return _firstPerson;
    }

    private void ApplyCameraMode()
    {
        _avatar?.SetHeadVisible(!_firstPerson);
        // Third-person: dolly the camera back and up a touch; first-person: at the eye.
        _camera.Position = _firstPerson ? Vector3.Zero : new Vector3(0, ThirdPersonCameraY, _thirdPersonDistance);
        // The name tag only makes sense floating above you in third person.
        _nameTag.Visible = !_firstPerson;
    }

    private const float ThirdPersonCameraY = 0.35f;
    private const float ThirdPersonMin = 1.2f;
    private const float ThirdPersonMax = 6.0f;
    private float _thirdPersonDistance = 3.0f;

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!ControlsEnabled) return;
        if (@event is InputEventMouseMotion m && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            _yaw.RotateY(-m.Relative.X * MouseSensitivity);
            _camera.RotateX(-m.Relative.Y * MouseSensitivity);
            var r = _camera.Rotation;
            r.X = Mathf.Clamp(r.X, -1.4f, 1.4f);
            _camera.Rotation = r;
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
            _camera.RotateX(-_pendingLook.Y * MouseSensitivity);
            var cr = _camera.Rotation;
            cr.X = Mathf.Clamp(cr.X, -1.4f, 1.4f);
            _camera.Rotation = cr;
        }
        _pendingLook = Vector2.Zero;

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
            _currentCameraY = _isCrouching ? CrouchCameraY : _standEyeY;
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

        // Smooth camera height transition
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
        bool sprinting = Input.IsPhysicalKeyPressed(Key.Shift) && !_isCrouching && input.Y < 0;
        if (sprinting) speed = SprintSpeed;

        v.X = dir.X * speed;
        v.Z = dir.Z * speed;
        Velocity = v;
        MoveAndSlide();

        // Animate the equipped avatar from actual movement state (embedded clips override this).
        var planar = new Vector2(Velocity.X, Velocity.Z);
        _avatar?.Animate(delta, planar.Length(), IsOnFloor());

        // Head bob — only when moving on the ground, and only meaningful in first person.
        float baseCamY = _firstPerson ? 0f : ThirdPersonCameraY;
        bool moving = input.LengthSquared() > 0.01f && IsOnFloor();
        if (moving && _firstPerson)
        {
            _bobTimer += (float)delta * BobFrequency * (sprinting ? 1.4f : 1f);
            float bob = Mathf.Sin(_bobTimer) * BobAmplitude * (sprinting ? 1.5f : 1f);
            var camPos = _camera.Position;
            camPos.Y = baseCamY + bob;
            _camera.Position = camPos;
        }
        else
        {
            _bobTimer = 0;
            var camPos = _camera.Position;
            camPos.Y = Mathf.Lerp(camPos.Y, baseCamY, (float)delta * 8f);
            _camera.Position = camPos;
        }
    }

    /// The transform we broadcast: body position, facing = yaw. Pitch stays local (head).
    public Transform3D PoseTransform()
    {
        var basis = new Basis(_yaw.Basis.GetRotationQuaternion());
        return new Transform3D(basis, GlobalPosition);
    }

}
