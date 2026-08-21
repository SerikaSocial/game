using Godot;

namespace SerikaSocial.Player;

/// The player you control. Desktop controls for M1: WASD move, mouse look, space to jump.
/// VR (OpenXR) is M3. Produces the transform that Main samples into pose frames.
public partial class LocalPlayer : CharacterBody3D
{
    private const float Speed = 4.0f;
    private const float JumpVelocity = 4.5f;
    private const float MouseSensitivity = 0.003f;

    private Node3D _yaw;      // horizontal look, also the body facing
    private Camera3D _camera; // pitch
    private float _gravity = 9.8f;

    public override void _Ready()
    {
        _gravity = (float)ProjectSettings.GetSetting("physics/3d/default_gravity", 9.8f);

        // Capsule body + collision.
        var col = new CollisionShape3D { Shape = new CapsuleShape3D { Height = 1.8f, Radius = 0.3f } };
        col.Position = new Vector3(0, 0.9f, 0);
        AddChild(col);

        var mesh = new MeshInstance3D
        {
            Mesh = new CapsuleMesh { Height = 1.8f, Radius = 0.3f },
            Position = new Vector3(0, 0.9f, 0),
        };
        mesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.9f, 0.7f, 0.2f) };
        AddChild(mesh);

        _yaw = new Node3D { Name = "Yaw", Position = new Vector3(0, 1.6f, 0) };
        AddChild(_yaw);
        _camera = new Camera3D();
        _yaw.AddChild(_camera);

        // Headless smoke runs have no window to capture the mouse in.
        if (!DisplayServer.GetName().Equals("headless"))
            Input.MouseMode = Input.MouseModeEnum.Captured;
    }

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
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
            Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    public override void _PhysicsProcess(double delta)
    {
        var v = Velocity;
        if (!IsOnFloor()) v.Y -= _gravity * (float)delta;
        if (Input.IsPhysicalKeyPressed(Key.Space) && IsOnFloor()) v.Y = JumpVelocity;

        // Movement is relative to where we're looking (yaw only).
        var input = new Vector2(
            (Input.IsPhysicalKeyPressed(Key.D) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.A) ? 1 : 0),
            (Input.IsPhysicalKeyPressed(Key.S) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.W) ? 1 : 0));
        var dir = (_yaw.GlobalTransform.Basis * new Vector3(input.X, 0, input.Y)) with { Y = 0 };
        if (dir.LengthSquared() > 0) dir = dir.Normalized();

        v.X = dir.X * Speed;
        v.Z = dir.Z * Speed;
        Velocity = v;
        MoveAndSlide();
    }

    /// The transform we broadcast: body position, facing = yaw. Pitch stays local (head).
    public Transform3D PoseTransform()
    {
        var basis = new Basis(_yaw.Basis.GetRotationQuaternion());
        return new Transform3D(basis, GlobalPosition);
    }
}
