using Godot;

namespace SerikaSocial.UI;

/// A world-space panel that the whole 2D UI is rendered onto in VR.
///
/// Every screen in this project is a `CanvasLayer` (see `UI/`). A `CanvasLayer` draws into its
/// parent viewport's *2D canvas*, and Godot does not composite that canvas into the OpenXR eye
/// buffers — so in the headset the 3D world rendered fine while every menu, the login screen
/// included, was simply absent. `VrPlayer` previously assumed the opposite ("draws into the eye
/// buffers at screen space") and built a pointer around `Camera3D.UnprojectPosition`, which
/// aimed at a surface that was never on screen.
///
/// The fix is the standard VR one: give the UI its own `SubViewport`, parent the `CanvasLayer`s
/// to that instead of to the scene root, and draw the viewport's texture on a quad floating in
/// front of the player. The `Control`s are untouched — they still lay out against a normal
/// screen-sized viewport and still handle ordinary mouse events, which `PushInput` delivers.
public partial class VrUiSurface : Node3D
{
    /// Backing resolution of the panel. 4:3-ish at a size that stays legible through the Quest's
    /// lenses without costing a full extra 1080p render target per eye.
    public static readonly Vector2I Resolution = new(1600, 1000);

    /// Panel size in metres, and how far in front of the camera it floats.
    private const float PanelWidth = 1.6f;
    private const float PanelDistance = 1.6f;

    public SubViewport Viewport { get; private set; }
    public MeshInstance3D Panel { get; private set; }

    private QuadMesh _quad;
    private bool _initialized;

    public override void _Ready()
    {
        Viewport = new SubViewport
        {
            Name = "UiViewport",
            Size = Resolution,
            // The UI is mostly transparent chrome over the world; without this the panel would
            // be an opaque black slab.
            TransparentBg = true,
            // The UI animates and must redraw every frame, not only when something requests it.
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            // This viewport exists purely to be sampled onto a quad; it must never try to
            // become an XR viewport itself.
            UseXR = false,
            // Menus are driven by synthetic mouse events pushed in from the controller ray.
            GuiDisableInput = false,
            HandleInputLocally = true,
        };
        AddChild(Viewport);

        float aspect = (float)Resolution.Y / Resolution.X;
        _quad = new QuadMesh { Size = new Vector2(PanelWidth, PanelWidth * aspect) };

        Panel = new MeshInstance3D
        {
            Name = "UiPanel",
            Mesh = _quad,
            // The panel is a UI surface, not scenery: it must not take lighting, cast shadows or
            // be culled by the world's environment.
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        Panel.MaterialOverride = new StandardMaterial3D
        {
            AlbedoTexture = Viewport.GetTexture(),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            // Menus must stay readable even when the player is standing inside geometry.
            NoDepthTest = true,
            RenderPriority = 100,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        AddChild(Panel);

        Position = new Vector3(0, 1.2f, -PanelDistance);
    }

    /// Snap the panel a fixed distance directly in front of `camera`, upright and facing the player.
    public void FaceCamera(Camera3D camera)
    {
        if (camera == null) return;

        var camPos = camera.GlobalPosition;
        var forward = -camera.GlobalTransform.Basis.Z;
        var flat = new Vector3(forward.X, 0, forward.Z);
        if (flat.LengthSquared() < 0.0001f) return;
        flat = flat.Normalized();

        GlobalPosition = camPos + flat * PanelDistance;
        // QuadMesh front face (+Z) turns toward player when LookAt target is along +flat away from player.
        LookAt(GlobalPosition + flat, Vector3.Up);
        _initialized = true;
    }

    /// Smoothly updates panel position only if the camera has moved significantly away or rotated > 50 degrees away.
    public void LazyFollow(Camera3D camera, float dt)
    {
        if (camera == null) return;
        if (!_initialized)
        {
            FaceCamera(camera);
            return;
        }

        var camPos = camera.GlobalPosition;
        var forward = -camera.GlobalTransform.Basis.Z;
        var flat = new Vector3(forward.X, 0, forward.Z);
        if (flat.LengthSquared() < 0.0001f) return;
        flat = flat.Normalized();

        var toPanel = (GlobalPosition - camPos);
        var toPanelFlat = new Vector3(toPanel.X, 0, toPanel.Z);
        float dist = toPanelFlat.Length();

        float angle = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(flat.Dot(toPanelFlat / Mathf.Max(dist, 0.001f)), -1f, 1f)));

        // If the player walked far away or turned away from the UI, smoothly bring it back into view.
        if (Mathf.Abs(dist - PanelDistance) > 0.8f || angle > 50f)
        {
            var targetPos = camPos + flat * PanelDistance;
            GlobalPosition = GlobalPosition.Lerp(targetPos, Mathf.Min(1f, dt * 4f));
            LookAt(GlobalPosition + flat, Vector3.Up);
        }
    }

    /// Map a world-space point on the panel to pixel coordinates inside the UI viewport.
    /// Returns false when the point misses the panel.
    public bool WorldToViewport(Vector3 worldPoint, out Vector2 viewportPos)
    {
        viewportPos = default;
        var local = Panel.GlobalTransform.AffineInverse() * worldPoint;
        var size = _quad.Size;

        float u = local.X / size.X + 0.5f;
        // Quad local +Y is up, viewport +Y is down.
        float v = 0.5f - local.Y / size.Y;
        if (u < 0f || u > 1f || v < 0f || v > 1f) return false;

        viewportPos = new Vector2(u * Resolution.X, v * Resolution.Y);
        return true;
    }

    /// Intersect a ray with the panel plane. Returns false if the ray is parallel to or points
    /// away from the front of the panel.
    public bool RayHit(Vector3 origin, Vector3 direction, out Vector3 hit)
    {
        hit = default;
        var xform = Panel.GlobalTransform;
        var normal = xform.Basis.Z.Normalized();
        float denom = normal.Dot(direction);
        if (denom >= -0.0001f) return false;

        float t = normal.Dot(xform.Origin - origin) / denom;
        if (t < 0f) return false;

        hit = origin + direction * t;
        return true;
    }
}
