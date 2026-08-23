using Godot;

namespace SerikaSocial.Player;

/// VR player controller using OpenXR. Uses XROrigin3D with XRCamera3D for head tracking
/// and XRController3D for hand tracking. Supports smooth locomotion (thumbstick) and
/// snap turning (right thumbstick left/right). Falls back to desktop mode if no HMD.
///
/// The pose we broadcast is the camera's world transform — position + orientation of the
/// head — which is what remote peers need to position the avatar.
public partial class VrPlayer : CharacterBody3D, IPlayer
{
    private const float WalkSpeed = 2.0f;
    private const float SprintSpeed = 4.0f;
    private const float SnapTurnAngle = 30f;
    private const float TurnDeadzone = 0.7f;

    public float MouseSensitivity { get; set; } = 0.003f; // unused in VR but satisfies IPlayer

    private XROrigin3D _origin;
    private XRCamera3D _camera;
    private XRController3D _leftHand;
    private XRController3D _rightHand;

    private float _gravity = 9.8f;
    private float _snapTurnCooldown;
    private bool _wasTurning;

    private MeshInstance3D _bodyMesh;
    private Label3D _nameTag;

    public override void _Ready()
    {
        _gravity = (float)ProjectSettings.GetSetting("physics/3d/default_gravity", 9.8f);

        // Collision capsule
        var col = new CollisionShape3D { Shape = new CapsuleShape3D { Height = 1.8f, Radius = 0.3f } };
        col.Position = new Vector3(0, 0.9f, 0);
        AddChild(col);

        // Visible body (capsule) — other players see this
        _bodyMesh = new MeshInstance3D
        {
            Mesh = new CapsuleMesh { Height = 1.8f, Radius = 0.3f },
            Position = new Vector3(0, 0.9f, 0),
        };
        _bodyMesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.9f, 0.7f, 0.2f) };
        AddChild(_bodyMesh);

        // XR Origin — the play space. Camera tracks head, controllers track hands.
        _origin = new XROrigin3D { Name = "XROrigin" };
        AddChild(_origin);

        _camera = new XRCamera3D { Name = "XRCamera" };
        _origin.AddChild(_camera);

        _leftHand = new XRController3D { Name = "LeftHand", Tracker = "/user/hand/left" };
        _origin.AddChild(_leftHand);

        _rightHand = new XRController3D { Name = "RightHand", Tracker = "/user/hand/right" };
        _origin.AddChild(_rightHand);

        // Hand visuals — simple boxes so you can see your controllers
        AddHandVisual(_leftHand, new Color(0.3f, 0.6f, 0.9f));
        AddHandVisual(_rightHand, new Color(0.9f, 0.4f, 0.3f));

        // Name tag floating above head
        _nameTag = new Label3D
        {
            Position = new Vector3(0, 0.5f, 0),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            FontSize = 48,
            PixelSize = 0.005f,
        };
        _camera.AddChild(_nameTag);
    }

    public void SetUsername(string name) => _nameTag.Text = name;

    private void AddHandVisual(XRController3D controller, Color color)
    {
        var mesh = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.05f, 0.05f, 0.1f) },
        };
        mesh.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = color,
            Roughness = 0.5f,
        };
        controller.AddChild(mesh);
    }

    public override void _PhysicsProcess(double delta)
    {
        var v = Velocity;

        // Gravity
        if (!IsOnFloor()) v.Y -= _gravity * (float)delta;

        // VR locomotion: left thumbstick to move in the direction the camera faces
        var moveVec = _leftHand.GetVector2("primary");
        if (moveVec.Length() > 0.1f)
        {
            // Deadzone
            moveVec = moveVec.Normalized() * Mathf.Min(moveVec.Length(), 1.0f);

            // Move relative to camera yaw
            var camBasis = _camera.GlobalTransform.Basis;
            var forward = -camBasis.Z with { Y = 0 };
            if (forward.LengthSquared() > 0) forward = forward.Normalized();
            var right = camBasis.X with { Y = 0 };
            if (right.LengthSquared() > 0) right = right.Normalized();

            bool sprinting = _leftHand.GetFloat("grip") > 0.5f || _rightHand.GetFloat("grip") > 0.5f;
            float speed = sprinting ? SprintSpeed : WalkSpeed;

            var dir = (forward * -moveVec.Y + right * moveVec.X) * speed;
            v.X = dir.X;
            v.Z = dir.Z;
        }
        else
        {
            v.X = 0;
            v.Z = 0;
        }

        // Snap turning: right thumbstick left/right
        _snapTurnCooldown -= (float)delta;
        var turnVec = _rightHand.GetVector2("primary");
        if (_snapTurnCooldown <= 0 && Mathf.Abs(turnVec.X) > TurnDeadzone)
        {
            float angle = turnVec.X > 0 ? SnapTurnAngle : -SnapTurnAngle;
            _origin.RotateY(Mathf.DegToRad(angle));
            _snapTurnCooldown = 0.3f;
        }

        // Jump: right hand trigger
        if (_rightHand.GetFloat("trigger") > 0.5f && IsOnFloor())
            v.Y = 4.5f;

        Velocity = v;
        MoveAndSlide();
    }

    /// The transform we broadcast: camera world position + yaw rotation.
    public Transform3D PoseTransform()
    {
        var pos = _camera.GlobalPosition;
        var rot = Basis.Identity;
        // Use camera yaw only for body orientation
        var camForward = -_camera.GlobalTransform.Basis.Z with { Y = 0 };
        if (camForward.LengthSquared() > 0.001f)
        {
            camForward = camForward.Normalized();
            // Build a basis looking along camForward with up = Y
            var right = camForward.Cross(Vector3.Up).Normalized();
            var up = right.Cross(camForward).Normalized();
            rot = new Basis(right, up, -camForward);
        }
        return new Transform3D(rot, pos);
    }

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
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree?.Root == null) return false;

        var iface = XRServer.FindInterface("OpenXR");
        if (iface == null) return false;
        if (!iface.IsInitialized() && !iface.Initialize()) return false;

        // Drive the main viewport through the headset.
        tree.Root.UseXR = true;
        return iface.IsInitialized();
    }
}
