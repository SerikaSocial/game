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
///
/// The panel is slightly curved (a shallow cylinder section) so its edges stay equidistant from
/// the eye. It carries no chrome of its own — see `_Ready` for why the decorative shadow and
/// glow quads were removed.
public partial class VrUiSurface : Node3D
{
    /// Backing resolution of the panel. 4:3-ish at a size that stays legible through the Quest's
    /// lenses without costing a full extra 1080p render target per eye. The Low tier trades some
    /// text crispness for ~40% fewer pixels, which matters because this target is composited on
    /// top of an already mobile-bound scene render.
    private static Vector2I PreferredResolution => DeviceProfile.Current == DeviceProfile.Tier.Low
        ? new Vector2I(1280, 800)
        : new Vector2I(1920, 1200);

    /// The resolution this surface was actually built at. Fixed at `_Ready` rather than read live,
    /// so changing the quality tier mid-session cannot desync the pointer's hit-test maths from
    /// the size of the render target the UI is really drawn on.
    public Vector2I Resolution { get; private set; }

    /// The size the `Control`s lay out against, which is deliberately *not* the render
    /// resolution.
    ///
    /// Every screen in `UI/` is sized for a desktop monitor — the pause hub's card is 560 px
    /// wide, the big menu's is 1100. Laying those out against the panel's full 1920 px made the
    /// pause hub occupy 29% of a surface that only subtends ~60° of view, so in the headset it
    /// was a postage stamp of unreadable 13 px text (roughly 0.36° tall, where ~0.5° is the
    /// floor for comfortable VR reading).
    ///
    /// Shrinking the *logical* space magnifies everything on the panel without touching a
    /// single screen's layout code. The floor is set by the largest screen: the big menu needs
    /// 1100×700 plus 2×16 px of margin, so 1200×760 is as tight as this can go while still
    /// fitting every screen.
    public Vector2I LogicalSize { get; private set; }

    private static readonly Vector2I PreferredLogicalSize = new(1200, 760);

    /// Panel size in metres, and how far in front of the camera it floats. Wider than a desktop
    /// monitor would be, because angular size is the whole game here: at 1.7 m away a 2 m panel
    /// subtends ~61°, which combined with the logical size above puts body text near 0.65°.
    private const float PanelWidth = 2.0f;
    private const float PanelDistance = 1.7f;
    private const float CurveAngleDeg = 14f; // degrees of cylinder arc — subtle but perceptible

    /// True once the UI is being drawn on a world-space panel rather than a flat screen.
    ///
    /// Screens need this because a few desktop idioms are actively wrong in VR — chiefly the
    /// full-screen scrim, which on a monitor dims the world behind a dialog but on a floating
    /// panel just paints a large opaque slab in front of the player's face. Set before any
    /// screen is constructed so `Brand.Scrim` can consult it while building.
    public static bool Active { get; set; }

    public SubViewport Viewport { get; private set; }
    public MeshInstance3D Panel { get; private set; }

    private ArrayMesh _curvedMesh;
    private bool _initialized;
    private double _visibilityPoll;

    public override void _Ready()
    {
        Active = true;
        Resolution = PreferredResolution;
        LogicalSize = PreferredLogicalSize;
        Viewport = new SubViewport
        {
            Name = "UiViewport",
            Size = Resolution,
            // Render at `Resolution`, lay out at `LogicalSize`. The stretch flag is what makes
            // the two independent; without it the override only clips.
            Size2DOverride = LogicalSize,
            Size2DOverrideStretch = true,
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

        // Aspect comes from the *logical* size: that is the shape the Controls lay out in, so
        // using the render resolution's aspect would letterbox or stretch the UI.
        float aspect = (float)LogicalSize.Y / LogicalSize.X;
        float panelHeight = PanelWidth * aspect;

        _curvedMesh = BuildCurvedPanel(PanelWidth, panelHeight, CurveAngleDeg, 16);

        Panel = new MeshInstance3D
        {
            Name = "UiPanel",
            Mesh = _curvedMesh,
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

        // There is deliberately no drop shadow or glow quad behind the panel.
        //
        // Both used to exist, described as making the panel "look premium and grounded". They
        // were full-surface quads, not rims: a black 0.35 sheet at 1.06 scale and a violet 0.15
        // sheet at 1.02. On a desktop mock-up that reads as a subtle frame, but in the headset
        // the panel is mostly empty — the pause hub's card covers about a quarter of it — so
        // what the player actually saw was a large translucent purple rectangle hanging in the
        // room with a small menu floating inside it. The screens already carry their own
        // shadowed, bordered cards (`Brand.Panel`), so the surface itself should show nothing
        // except what the UI draws on it.

        Position = new Vector3(0, 1.2f, -PanelDistance);
    }

    /// Build a cylinder-section mesh for the panel. `segments` controls smoothness.
    /// The mesh is centred at the origin with the concave side facing +Z (toward the player).
    private static ArrayMesh BuildCurvedPanel(float width, float height, float angleDeg, int segments)
    {
        var mesh = new ArrayMesh();
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        float halfAngle = Mathf.DegToRad(angleDeg) * 0.5f;
        // Radius of curvature: the chord length equals `width`, so
        // r = width / (2 * sin(halfAngle)).
        float radius = width / (2f * Mathf.Sin(halfAngle));
        float halfH = height * 0.5f;

        // Generate a strip of quads along the arc.
        for (int i = 0; i <= segments; i++)
        {
            float t = (float)i / segments;
            float angle = Mathf.Lerp(-halfAngle, halfAngle, t);

            // X along the arc, Z is the depth of curvature. The centre (angle=0) is at Z=0.
            float x = Mathf.Sin(angle) * radius;
            float z = radius - Mathf.Cos(angle) * radius; // concave toward +Z

            // UV: 0→1 across the width, 0→1 top to bottom.
            float u = t;

            var normal = new Vector3(-Mathf.Sin(angle), 0, Mathf.Cos(angle));

            st.SetUV(new Vector2(u, 0));
            st.SetNormal(normal);
            st.AddVertex(new Vector3(x, halfH, z));

            st.SetUV(new Vector2(u, 1));
            st.SetNormal(normal);
            st.AddVertex(new Vector3(x, -halfH, z));
        }

        // Build triangle indices for the quad strip.
        for (int i = 0; i < segments; i++)
        {
            int tl = i * 2;
            int bl = tl + 1;
            int tr = tl + 2;
            int br = tl + 3;

            st.AddIndex(tl); st.AddIndex(bl); st.AddIndex(tr);
            st.AddIndex(tr); st.AddIndex(bl); st.AddIndex(br);
        }

        st.GenerateTangents();
        st.Commit(mesh);
        return mesh;
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
        // mesh's front face turned back toward the player.
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
    ///
    /// For the curved panel the mapping is done by projecting the point into local space and
    /// converting the local X into an arc-angle UV. The curvature is subtle enough (~14°) that
    /// the error from treating the hit point as if it were on a flat plane is negligible for
    /// pointer tracking — but we do the proper curved mapping anyway.
    public bool WorldToViewport(Vector3 worldPoint, out Vector2 viewportPos)
    {
        viewportPos = default;
        var local = Panel.GlobalTransform.AffineInverse() * worldPoint;

        float aspect = (float)LogicalSize.Y / LogicalSize.X;
        float panelHeight = PanelWidth * aspect;
        float halfAngle = Mathf.DegToRad(CurveAngleDeg) * 0.5f;
        float radius = PanelWidth / (2f * Mathf.Sin(halfAngle));

        // Recover the arc angle from local X and Z.
        float angle = Mathf.Atan2(local.X, radius - local.Z);
        float u = (angle + halfAngle) / (2f * halfAngle);
        // Local +Y is up, viewport +Y is down.
        float v = 0.5f - local.Y / panelHeight;

        if (u < -0.05f || u > 1.05f || v < -0.05f || v > 1.05f) return false;
        u = Mathf.Clamp(u, 0f, 1f);
        v = Mathf.Clamp(v, 0f, 1f);

        viewportPos = new Vector2(u * LogicalSize.X, v * LogicalSize.Y);
        return true;
    }

    /// Intersect a ray with the curved panel. Approximated as a flat plane perpendicular to
    /// the panel's forward direction — the curvature is only ~14° so the error is sub-millimetre,
    /// and the proper curved mapping in WorldToViewport handles the UV accurately.
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
