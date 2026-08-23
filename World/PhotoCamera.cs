using System;
using Godot;

namespace SerikaSocial.World;

/// A detached free-flying camera used for photos — the "phantom cam".
///
/// Renders the live world into its own `SubViewport`, which the camera UI displays as a phone
/// screen so you frame the shot on the handset while the camera itself flies independently of
/// the player. Photos are written to `user://photos/`.
///
/// The SubViewport must be handed the main viewport's `World3D` explicitly: with
/// `OwnWorld3D = false` Godot does not inherit it for a SubViewport created in code, and the
/// texture comes back empty (this is the same trap the mirror hit).
public sealed partial class PhotoCamera : Node3D
{
    public const int Width = 1280;
    public const int Height = 720;

    private SubViewport _vp;
    private Camera3D _cam;
    private float _yaw, _pitch;
    private bool _linked;

    /// Whether the camera is flying and consuming movement input.
    public bool Active { get; private set; }

    public float MouseSensitivity { get; set; } = 0.003f;
    private const float SlowSpeed = 2.0f;
    private const float FastSpeed = 8.0f;

    public Texture2D ViewTexture => _vp?.GetTexture();

    public override void _Ready()
    {
        _vp = new SubViewport
        {
            Size = new Vector2I(Width, Height),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            OwnWorld3D = false,
            TransparentBg = false,
        };
        AddChild(_vp);

        _cam = new Camera3D { Current = true, Fov = 70f };
        _vp.AddChild(_cam);
    }

    public override void _Process(double delta)
    {
        // World3D isn't available until this node is in the tree with a viewport above it.
        if (!_linked)
        {
            var world = GetViewport()?.World3D;
            if (world != null) { _vp.World3D = world; _linked = true; }
        }

        if (!Active) return;
        FreeFly((float)delta);
    }

    /// Drop the camera into the world at `origin`, looking the same way `basis` does.
    public void Deploy(Transform3D from)
    {
        GlobalPosition = from.Origin;
        var e = from.Basis.GetEuler();
        _yaw = e.Y;
        _pitch = Mathf.Clamp(e.X, -1.4f, 1.4f);
        ApplyRotation();
        Active = true;
    }

    public void Stow() => Active = false;

    private void FreeFly(float delta)
    {
        var input = new Vector3(
            (Input.IsPhysicalKeyPressed(Key.D) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.A) ? 1 : 0),
            (Input.IsPhysicalKeyPressed(Key.E) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.Q) ? 1 : 0),
            (Input.IsPhysicalKeyPressed(Key.S) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.W) ? 1 : 0));
        if (input == Vector3.Zero) return;

        float speed = Input.IsPhysicalKeyPressed(Key.Shift) ? FastSpeed : SlowSpeed;
        // Horizontal motion follows where the camera looks; Q/E stay world-vertical so the
        // camera rises straight up even when pitched down at the subject.
        Vector3 move = (GlobalTransform.Basis * new Vector3(input.X, 0, input.Z)) with { Y = 0 };
        if (move.LengthSquared() > 0) move = move.Normalized();
        move.Y = input.Y;
        GlobalPosition += move * speed * delta;
    }

    public void AddLook(Vector2 relative)
    {
        if (!Active) return;
        _yaw -= relative.X * MouseSensitivity;
        _pitch = Mathf.Clamp(_pitch - relative.Y * MouseSensitivity, -1.4f, 1.4f);
        ApplyRotation();
    }

    private void ApplyRotation() => Rotation = new Vector3(_pitch, _yaw, 0);

    /// Save the viewfinder image as a PNG. Returns the absolute path, or null on failure.
    public string TakePhoto()
    {
        try
        {
            var tex = _vp?.GetTexture();
            var img = tex?.GetImage();
            if (img == null) return null;

            DirAccess.MakeDirRecursiveAbsolute("user://photos");
            string rel = $"user://photos/serika-{DateTime.Now:yyyyMMdd-HHmmss}.png";
            if (img.SavePng(rel) != Error.Ok) return null;
            return ProjectSettings.GlobalizePath(rel);
        }
        catch (Exception e)
        {
            GD.PrintErr($"photo capture failed: {e.Message}");
            return null;
        }
    }
}
