using Godot;

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

    private Node3D _yaw;      // horizontal look, also the body facing
    private Camera3D _camera; // pitch
    private float _gravity = 9.8f;
    private MeshInstance3D _bodyMesh;
    private CollisionShape3D _collision;
    private CapsuleShape3D _capsuleShape;
    private Label3D _nameTag;

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

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion m && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            _yaw.RotateY(-m.Relative.X * MouseSensitivity);
            _camera.RotateX(-m.Relative.Y * MouseSensitivity);
            var r = _camera.Rotation;
            r.X = Mathf.Clamp(r.X, -1.4f, 1.4f);
            _camera.Rotation = r;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        var v = Velocity;
        bool onFloor = IsOnFloor();

        if (!onFloor) v.Y -= _gravity * (float)delta;
        if (Input.IsPhysicalKeyPressed(Key.Space) && onFloor && !_isCrouching)
            v.Y = JumpVelocity;

        // Crouch toggle
        bool wantCrouch = Input.IsPhysicalKeyPressed(Key.Ctrl);
        if (wantCrouch != _isCrouching)
        {
            _isCrouching = wantCrouch;
            _currentHeight = _isCrouching ? CrouchHeight : StandHeight;
            _currentCameraY = _isCrouching ? CrouchCameraY : StandCameraY;
            _capsuleShape.Height = _currentHeight;
            _collision.Position = new Vector3(0, _currentHeight * 0.5f, 0);
            if (_bodyMesh.Mesh is CapsuleMesh cm) cm.Height = _currentHeight;
            _bodyMesh.Position = new Vector3(0, _currentHeight * 0.5f, 0);
        }

        // Smooth camera height transition
        var yawPos = _yaw.Position;
        yawPos.Y = Mathf.Lerp(yawPos.Y, _currentCameraY, (float)delta * 10f);
        _yaw.Position = yawPos;

        // Movement is relative to where we're looking (yaw only).
        var input = new Vector2(
            (Input.IsPhysicalKeyPressed(Key.D) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.A) ? 1 : 0),
            (Input.IsPhysicalKeyPressed(Key.S) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.W) ? 1 : 0));
        var dir = (_yaw.GlobalTransform.Basis * new Vector3(input.X, 0, input.Y)) with { Y = 0 };
        if (dir.LengthSquared() > 0) dir = dir.Normalized();

        float speed = _isCrouching ? CrouchSpeed : WalkSpeed;
        bool sprinting = Input.IsPhysicalKeyPressed(Key.Shift) && !_isCrouching && input.Y < 0;
        if (sprinting) speed = SprintSpeed;

        v.X = dir.X * speed;
        v.Z = dir.Z * speed;
        Velocity = v;
        MoveAndSlide();

        // Head bob — only when moving on the ground
        bool moving = input.LengthSquared() > 0.01f && IsOnFloor();
        if (moving)
        {
            _bobTimer += (float)delta * BobFrequency * (sprinting ? 1.4f : 1f);
            float bob = Mathf.Sin(_bobTimer) * BobAmplitude * (sprinting ? 1.5f : 1f);
            var camPos = _camera.Position;
            camPos.Y = bob;
            _camera.Position = camPos;
        }
        else
        {
            _bobTimer = 0;
            var camPos = _camera.Position;
            camPos.Y = Mathf.Lerp(camPos.Y, 0, (float)delta * 8f);
            _camera.Position = camPos;
        }
    }

    /// The transform we broadcast: body position, facing = yaw. Pitch stays local (head).
    public Transform3D PoseTransform()
    {
        var basis = new Basis(_yaw.Basis.GetRotationQuaternion());
        return new Transform3D(basis, GlobalPosition);
    }

    public void SetAvatarColor(Color color)
    {
        if (_bodyMesh?.MaterialOverride is StandardMaterial3D mat)
            mat.AlbedoColor = color;
    }
}
