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
    /// lenses without costing a full extra 1080p render target per eye. The Low tier trades some
    /// text crispness for ~40% fewer pixels, which matters because this target is composited on
    /// top of an already mobile-bound scene render.
    private static Vector2I PreferredResolution => DeviceProfile.Current == DeviceProfile.Tier.Low
        ? new Vector2I(1200, 750)
        : new Vector2I(1600, 1000);

    /// The resolution this surface was actually built at. Fixed at `_Ready` rather than read live,
    /// so changing the quality tier mid-session cannot desync the pointer's hit-test maths from
    /// the size of the render target the UI is really drawn on.
    public Vector2I Resolution { get; private set; }

    /// Panel size in metres, and how far in front of the camera it floats.
    private const float PanelWidth = 1.6f;
    private const float PanelDistance = 1.6f;

    public SubViewport Viewport { get; private set; }
    public MeshInstance3D Panel { get; private set; }

    private QuadMesh _quad;
    private bool _initialized;
    private double _visibilityPoll;

    public override void _Ready()
    {
        Resolution = PreferredResolution;
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

    /// Stop rendering the panel while nothing is on it.
    ///
    /// `UpdateMode.Always` re-rendered a 1600×1000 target and composited a transparent quad every
    /// single frame for the whole session, menu open or not — a fixed tax on the exact device that
    /// can least afford it. The panel only has to be live while some layer is visible.
    public override void _Process(double delta)
    {
        // Polled a few times a second rather than every frame: walking the child list allocates a
        // Godot Array and boxes each entry into a Variant, and this runs for the whole session.
        _visibilityPoll -= delta;
        if (_visibilityPoll > 0) return;
        _visibilityPoll = 0.2;

        bool anyVisible = false;
        for (int i = 0, n = Viewport.GetChildCount(); i < n; i++)
        {
            if (Viewport.GetChild(i) is CanvasLayer { Visible: true }) { anyVisible = true; break; }
        }

        Panel.Visible = anyVisible;
        Viewport.RenderTargetUpdateMode = anyVisible
            ? SubViewport.UpdateMode.Always
            : SubViewport.UpdateMode.Disabled;
    }

    // Follow behaviour. The panel parks in place and only chases the player when it has drifted
    // far enough out of view to be a nuisance — then it settles again and stops moving.
    private const float ReAnchorAngle = 45f;   // degrees off-centre before the panel starts moving
    private const float SettledAngle = 6f;     // ...and how close it must get before it stops
    private const float ReAnchorDistance = 0.7f; // metres of range error before it starts moving
    private const float SettledDistance = 0.05f;
    private bool _following;

    /// Point the panel at the camera, upright. Always billboards off the camera→panel vector.
    ///
    /// This used to aim with the camera's *forward* vector instead, which is only equivalent when
    /// the panel is exactly dead ahead. Any time the panel was off to one side — which is most of
    /// a lazy follow — it yawed to align with your gaze axis rather than turning to face you, so
    /// the UI visibly skewed sideways as it moved.
    private void OrientTo(Vector3 camPos)
    {
        var away = GlobalPosition - camPos;
        away = new Vector3(away.X, 0, away.Z);
        if (away.LengthSquared() < 0.0001f) return;
        // LookAt points -Z at the target, so aiming further *away* from the camera leaves the
        // QuadMesh's front face (+Z) turned back toward the player.
        LookAt(GlobalPosition + away.Normalized(), Vector3.Up);
    }

    /// Snap the panel a fixed distance directly in front of `camera`, upright and facing the player.
    public void FaceCamera(Camera3D camera)
    {
        if (camera == null) return;
        if (!TryAnchor(camera, out var target)) return;

        GlobalPosition = target;
        OrientTo(camera.GlobalPosition);
        _initialized = true;
        _following = false;
    }

    /// Where the panel wants to sit: `PanelDistance` ahead of the camera's horizontal gaze, at eye
    /// height. Returns false when the player is looking straight up or down, where "ahead" has no
    /// meaningful horizontal direction and re-anchoring would fling the panel somewhere arbitrary.
    private bool TryAnchor(Camera3D camera, out Vector3 target)
    {
        target = default;
        var forward = -camera.GlobalTransform.Basis.Z;
        var flat = new Vector3(forward.X, 0, forward.Z);
        if (flat.LengthSquared() < 0.02f) return false;
        target = camera.GlobalPosition + flat.Normalized() * PanelDistance;
        return true;
    }

    /// Keep the panel roughly in front of the player without it sliding around underfoot.
    ///
    /// The previous version re-evaluated its target every frame while the error was over
    /// threshold, so once you started walking or turning the panel chased you continuously and
    /// never came to rest. This uses hysteresis instead: nothing moves until the panel is well
    /// out of view, then it travels to a fixed anchor and *stops* once it arrives.
    public void LazyFollow(Camera3D camera, float dt)
    {
        if (camera == null) return;
        if (!_initialized) { FaceCamera(camera); return; }

        var camPos = camera.GlobalPosition;
        var toPanel = GlobalPosition - camPos;
        var toPanelFlat = new Vector3(toPanel.X, 0, toPanel.Z);
        float dist = toPanelFlat.Length();
        if (dist < 0.001f) { FaceCamera(camera); return; }

        var forward = -camera.GlobalTransform.Basis.Z;
        var flat = new Vector3(forward.X, 0, forward.Z);
        float angle = flat.LengthSquared() < 0.02f
            ? 0f // looking straight up/down: no meaningful yaw error, so don't trigger a chase
            : Mathf.RadToDeg(Mathf.Acos(
                Mathf.Clamp(flat.Normalized().Dot(toPanelFlat / dist), -1f, 1f)));
        float rangeError = Mathf.Abs(dist - PanelDistance);

        if (!_following && (angle > ReAnchorAngle || rangeError > ReAnchorDistance))
            _following = true;

        if (_following)
        {
            if (!TryAnchor(camera, out var target)) return;
            // Exponential smoothing that is independent of frame rate — a raw `dt * k` lerp moves
            // further per second at 120 Hz than at 72 Hz, so the panel felt different in-headset
            // than on desktop.
            GlobalPosition = GlobalPosition.Lerp(target, 1f - Mathf.Exp(-6f * dt));

            if (angle < SettledAngle && rangeError < SettledDistance) _following = false;
        }

        // Billboard every frame regardless: it is cheap, and it means the panel always squarely
        // faces the player even while parked and walked around.
        OrientTo(camPos);
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
        // An idle panel is not drawn, so it must not be clickable either — otherwise the ray
        // still lands on an invisible slab and pushes events at hidden layers.
        if (Panel == null || !Panel.Visible) return false;
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
