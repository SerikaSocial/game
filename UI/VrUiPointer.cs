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

    private readonly XRController3D _hand;
    private readonly VrUiSurface _surface;

    private MeshInstance3D _laser;
    private MeshInstance3D _dot;
    private Vector2 _pointerPos;
    private bool _down;

    public VrUiPointer(XRController3D hand, VrUiSurface surface)
    {
        _hand = hand;
        _surface = surface;
    }

    public override void _Ready()
    {
        _laser = new MeshInstance3D
        {
            Name = "Pointer",
            Mesh = new BoxMesh { Size = new Vector3(0.004f, 0.004f, 2.0f) },
            Position = new Vector3(0, 0, -1.0f), // extends forward from the controller
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        _laser.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = Brand.Accent,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
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
        };
        _dot.TopLevel = true;
        AddChild(_dot);
    }

    public override void _Process(double delta)
    {
        if (_surface == null || _hand == null) return;

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
        bool hitPanel = _surface.RayHit(origin, aim, out var hit) && _surface.WorldToViewport(hit, out screen);

        if (hitPanel)
        {
            if (_dot != null)
            {
                _dot.Visible = true;
                _dot.GlobalPosition = hit;
            }

            if (screen != _pointerPos)
            {
                _pointerPos = screen;
                _surface.Viewport.PushInput(
                    new InputEventMouseMotion { Position = screen, GlobalPosition = screen }, true);
            }
        }
        else
        {
            if (_dot != null) _dot.Visible = false;
        }

        bool pressed = _hand.GetFloat(ActTrigger) > 0.6f;
        if (pressed != _down)
        {
            _down = pressed;
            _surface.Viewport.PushInput(new InputEventMouseButton
            {
                Position = hitPanel ? screen : _pointerPos,
                GlobalPosition = hitPanel ? screen : _pointerPos,
                ButtonIndex = MouseButton.Left,
                Pressed = pressed,
            }, true);
        }
    }

    private void Release()
    {
        _down = false;
        if (_dot != null) _dot.Visible = false;
        _surface.Viewport.PushInput(new InputEventMouseButton
        {
            Position = _pointerPos,
            GlobalPosition = _pointerPos,
            ButtonIndex = MouseButton.Left,
            Pressed = false,
        }, true);
    }
}
