using Godot;
using SerikaSocial;

namespace SerikaSocial.UI;

/// A laser pointer that drives a `VrUiSurface` from an `XRController3D`.
///
/// `VrPlayer` has its own equivalent for in-world menus, but that rig does not exist until after
/// login. The login screen therefore had no controllers, no laser and no way to click anything —
/// the headset showed a panel the player could not reach. This node is attached to the pre-login
/// boot rig so the very first screen is usable.
public partial class VrUiPointer : Node3D
{
    private const string ActTrigger = "trigger";
    private const float LaserLength = 2.0f;

    private readonly XRController3D _hand;

    /// The panel this ray drives. Settable rather than constructor-only: the surface and the boot
    /// rig are built in the same frame, and a null captured here is silent — the laser still
    /// renders but every ray, hover and click is dropped, which is exactly how the login screen
    /// ended up visible but unclickable.
    public VrUiSurface Surface { get; set; }

    private MeshInstance3D _laser;
    private MeshInstance3D _dot;
    private Vector2 _pointerPos;
    private bool _down;

    public VrUiPointer(XRController3D hand, VrUiSurface surface)
    {
        _hand = hand;
        Surface = surface;
        if (surface == null)
            GD.PushWarning("VrUiPointer constructed without a UI surface — menus will not respond.");
    }

    public override void _Ready()
    {
        _laser = new MeshInstance3D
        {
            Name = "Pointer",
            Mesh = new BoxMesh { Size = new Vector3(0.004f, 0.004f, LaserLength) },
            Position = new Vector3(0, 0, -LaserLength * 0.5f), // extends forward from the controller
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        _laser.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = Brand.Accent,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            NoDepthTest = true,
            RenderPriority = 101,
        };
        _hand.AddChild(_laser);

        _dot = new MeshInstance3D
        {
            Name = "PointerDot",
            Mesh = new SphereMesh { Radius = 0.012f, Height = 0.024f },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        _dot.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = Brand.PrimaryHi,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            NoDepthTest = true,
            RenderPriority = 102,
        };
        _dot.TopLevel = true;
        AddChild(_dot);
    }

    public override void _Process(double delta)
    {
        var surface = Surface;
        if (surface == null || _hand == null)
        {
            if (_laser != null) _laser.Visible = false;
            if (_dot != null) _dot.Visible = false;
            return;
        }

        // Without tracking the controller pose is stale, so the ray would point somewhere
        // arbitrary; hide the laser rather than fire blind clicks at the panel.
        bool tracked = _hand.GetHasTrackingData();
        _laser.Visible = tracked;
        if (!tracked)
        {
            if (_dot != null) _dot.Visible = false;
            if (_down) Release();
            return;
        }

        var origin = _hand.GlobalPosition;
        var aim = -_hand.GlobalTransform.Basis.Z;

        Vector2 screen = Vector2.Zero;
        bool hitPanel = surface.RayHit(origin, aim, out var hit) && surface.WorldToViewport(hit, out screen);

        if (hitPanel)
        {
            if (_dot != null)
            {
                _dot.Visible = true;
                _dot.GlobalPosition = hit;
            }

            // Stop the beam at the panel instead of spearing through it.
            float reach = Mathf.Clamp(origin.DistanceTo(hit), 0.05f, LaserLength);
            SetLaserLength(reach);

            if (screen != _pointerPos)
            {
                _pointerPos = screen;
                surface.Viewport.PushInput(
                    new InputEventMouseMotion { Position = screen, GlobalPosition = screen }, true);
            }
        }
        else
        {
            if (_dot != null) _dot.Visible = false;
            SetLaserLength(LaserLength);
        }

        // Schmitt trigger: a single threshold makes an analogue trigger held near 0.6 chatter
        // press/release for several frames, which reads as a dead or double-firing button.
        bool pressed = _down ? _hand.GetFloat(ActTrigger) > 0.4f : _hand.GetFloat(ActTrigger) > 0.7f;
        // A press only counts on the panel; a release always fires, so dragging off the panel
        // can never latch the button down forever.
        if (pressed != _down && (hitPanel || !pressed))
        {
            _down = pressed;
            surface.Viewport.PushInput(new InputEventMouseButton
            {
                Position = hitPanel ? screen : _pointerPos,
                GlobalPosition = hitPanel ? screen : _pointerPos,
                ButtonIndex = MouseButton.Left,
                ButtonMask = pressed ? MouseButtonMask.Left : 0,
                Pressed = pressed,
            }, true);
        }
    }

    /// Rescale the beam so it ends at `length` metres in front of the controller. The BoxMesh is
    /// centred on its origin, so the offset is half the length.
    private void SetLaserLength(float length)
    {
        if (_laser == null) return;
        _laser.Scale = new Vector3(1, 1, length / LaserLength);
        _laser.Position = new Vector3(0, 0, -length * 0.5f);
    }

    private void Release()
    {
        _down = false;
        if (_dot != null) _dot.Visible = false;
        Surface?.Viewport.PushInput(new InputEventMouseButton
        {
            Position = _pointerPos,
            GlobalPosition = _pointerPos,
            ButtonIndex = MouseButton.Left,
            Pressed = false,
        }, true);
    }
}
