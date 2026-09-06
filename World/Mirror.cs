using Godot;

namespace SerikaSocial.World;

/// Planar reflection using an off-axis window through the physical glass. Each reflected eye
/// looks through that same window with its own asymmetric frustum. The camera's near plane is
/// parallel to and just past the glass, so a backing wall cannot intrude at oblique viewpoints.
/// The quad samples local UVs (horizontally reversed), preserving the eye → glass → scene light
/// path independently of viewer orientation, FOV, roll or asymmetric headset projection.
/// Stereo uses two eye positions and two textures selected by VIEW_INDEX; the existing range
/// and per-device scene-render budget apply to both eyes.
[GlobalClass]
public partial class Mirror : Node3D
{
    private readonly Vector2 _size;
    private float _activeRange;
    private SubViewport _viewport;
    private Camera3D _mirrorCam;
    // View 1 (right eye). Built lazily, the first frame this mirror runs in stereo — a desktop
    // player must not pay a second screen-sized render target for a case they never enter.
    private SubViewport _viewportR;
    private Camera3D _mirrorCamR;
    private MeshInstance3D _surface;
    private ShaderMaterial _liveMaterial;
    private StandardMaterial3D _farMaterial;
    private bool _showingLive;
    /// Whether this mirror rendered the last frame as two views. Read by the budget ranking
    /// (a stereo mirror costs twice a mono one) and by the diagnostics.
    private bool _stereoActive;

    // A reflection texture normally represents the complete physical sheet of glass. When the
    // player is close enough that the glass fills their view, only a tiny centre patch of that
    // texture reaches the display. This window lets the reflection camera render that visible
    // patch directly, while the shader remaps local glass UVs into the cropped texture.
    //
    // Coordinates use the reflection texture's natural orientation: (0, 0) is the full glass's
    // top-left in the off-axis camera, after its horizontal mirror flip.
    private readonly struct TextureWindow
    {
        public static readonly TextureWindow Full = new(Vector2.Zero, Vector2.One);

        public readonly Vector2 Min;
        public readonly Vector2 Max;

        public TextureWindow(Vector2 min, Vector2 max)
        {
            Min = min;
            Max = max;
        }

        public Vector2 Size => Max - Min;
        public Vector4 AsVector4 => new(Min.X, Min.Y, Max.X, Max.Y);

        public bool IsApprox(TextureWindow other) =>
            Min.DistanceSquaredTo(other.Min) < 1e-8f && Max.DistanceSquaredTo(other.Max) < 1e-8f;
    }

    private TextureWindow _textureWindowL = TextureWindow.Full;
    private TextureWindow _textureWindowR = TextureWindow.Full;

    public Mirror(float width = 1.4f, float height = 2.2f, float activeRange = 12f)
    {
        _size = new Vector2(width, height);
        _activeRange = activeRange;
    }

    public static Mirror Create(Vector3 position, float yawDeg, float width = 1.4f, float height = 2.2f)
    {
        var m = new Mirror(width, height, 12f)
        {
            Name = "Mirror",
            Position = position,
            RotationDegrees = new Vector3(0, yawDeg, 0),
        };
        return m;
    }

    public override void _Ready()
    {
        // A bare reflective plane — no frame, no collision, nothing but the glass. The surface
        // is kept on visual layer 2 so the reflection camera can cull it (see CullMask below);
        // otherwise the near-plane clip would reveal the glass itself inside the reflection.
        // The main player cameras render all layers, so they still see the mirror normally.

        // Offscreen render target — renders the SAME world (OwnWorld3D=false) so the mirror
        // camera sees the real scene: the player, the room, everything.
        //
        // Keep the existing device-scaled pixel budget. RenderView fits a target with the
        // physical glass's aspect inside it, so smaller/narrower mirrors do not allocate an
        // entire screen-shaped image that will mostly be discarded.
        (_viewport, _mirrorCam) = BuildView("L");

        // The glass quad, textured with the viewport via the mirror shader.
        //
        // It is pushed 1 cm out along the surface normal. World authoring puts mirror markers
        // flush on their host wall, so the quad and the wall's face end up exactly coplanar —
        // and coplanar glass z-fights with the wall per-pixel: head-on the quad wins, but from
        // oblique angles the steep depth gradient lets the wall win over half the glass, which
        // read as the mirror "showing the wall" or striped garbage depending on viewpoint.
        // Real mirror glass has thickness; 1 cm is invisible and settles the fight everywhere.
        _surface = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = _size },
            Position = new Vector3(0, _size.Y * 0.5f, 0.01f),
            Layers = 1u << 1,
        };
        var mirrorShader = ResourceLoader.Load<Shader>("res://Shaders/mirror.gdshader");
        if (mirrorShader != null)
        {
            _liveMaterial = new ShaderMaterial { Shader = mirrorShader };
            _liveMaterial.SetShaderParameter("color", new Color(0.9f, 0.97f, 0.94f));
            _liveMaterial.SetShaderParameter("mirror_texture", _viewport.GetTexture());
            _liveMaterial.SetShaderParameter("mirror_uv_rect", _textureWindowL.AsVector4);
            _liveMaterial.SetShaderParameter("mirror_uv_rect_r", _textureWindowR.AsVector4);
            _surface.MaterialOverride = _liveMaterial;
            _showingLive = true;
        }
        else
        {
            // The shader must be packed with the build (it is, via all_resources) — if this
            // ever fires in an exported build it is a real regression, so make it loud and
            // fall back to showing the raw reflection texture rather than a broken-pink quad.
            GD.PrintErr("Mirror: res://Shaders/mirror.gdshader failed to load — using unshaded fallback");
            _liveMaterial = null;
            _surface.MaterialOverride = FarMaterial();
            _showingLive = false;
        }
        AddChild(_surface);
    }

    /// One reflection view: an offscreen render target plus the camera that fills it. Called once
    /// for the mono/left view at `_Ready`, and again for the right eye the first stereo frame.
    private (SubViewport vp, Camera3D cam) BuildView(string suffix)
    {
        var vp = new SubViewport
        {
            Name = $"MirrorView{suffix}",
            Size = WantedSize(),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
            OwnWorld3D = false,
            // The reflection is a second full render of the scene; without MSAA its edges
            // shimmer badly next to the main view. Match the device tier's main-view MSAA.
            Msaa3D = (Viewport.Msaa)Mathf.Clamp(UI.DeviceProfile.MsaaLevel, 0, 2),
            // Ensure the viewport renders with the same 3D world and environment as the main view.
            CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.LinearWithMipmaps,
        };
        AddChild(vp);

        // Current must be TRUE: `Current` is per-viewport, so this only makes the camera the
        // active one INSIDE the SubViewport (it never touches the main window's camera). With
        // it false the SubViewport had no active camera and rendered nothing.
        var cam = new Camera3D
        {
            Name = $"MirrorCam{suffix}",
            Current = true,
            // TopLevel so its GlobalTransform is written in world space, not influenced by the
            // SubViewport's (identity) transform chain. This makes the reflection math reliable.
            TopLevel = true,
            // Do not render the mirror surface (layer 2), only the room beyond it (layer 1).
            // Also drop the local player's first-person proxy: a mirror must show the COMPLETE
            // avatar, so it renders the untouched originals and would otherwise draw the
            // headless copy over the top of them.
            CullMask = 1048575u & ~((1u << 1) | Player.LocalPlayer.NonFpCullLayers),
        };
        vp.AddChild(cam);
        return (vp, cam);
    }

    /// The reflection render target's pixel size: the main viewport, scaled by the device tier.
    /// Clamped below so a tiny or minimised window still leaves a usable texture.
    private Vector2I WantedSize()
    {
        var main = GetViewport().GetVisibleRect().Size;
        if (main.X < 1 || main.Y < 1) main = new Vector2(1280, 720);
        float s = Mathf.Clamp(UI.DeviceProfile.MirrorResolutionScale, 0.25f, 1f);
        return new Vector2I(
            Mathf.Max(256, Mathf.RoundToInt(main.X * s)),
            Mathf.Max(256, Mathf.RoundToInt(main.Y * s)));
    }

    // ── Render budget ────────────────────────────────────────────────────────────────
    // Every live mirror is a whole extra scene render and worlds do not ration them: the Mirror
    // Gallery hangs eight in one room, every one of them inside `MirrorRange`. Mirrors register
    // here so each can see how many nearer ones are already claiming the frame's budget, and the
    // ones past it show dark glass instead. Nearest-first, so whichever mirror the player is
    // actually looking into is the one that stays live.
    private static readonly System.Collections.Generic.List<Mirror> Registry = new();

    /// This mirror's straight-line distance to the viewer, or +inf when it is not a candidate
    /// (viewer behind the glass, or out of range). Published for the ranking below.
    private float _claimDistance = float.PositiveInfinity;

    public override void _EnterTree() => Registry.Add(this);

    public override void _ExitTree()
    {
        Registry.Remove(this);
        // A mirror leaving the tree must not keep a render target ticking over.
        if (_viewport != null) _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        if (_viewportR != null) _viewportR.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
    }

    /// True when at most `MirrorBudget - 1` other candidate mirrors are nearer than this one.
    ///
    /// Ranking reads the distances the other mirrors published on their own `_Process`, so early
    /// movers in a frame rank against last frame's values. That one-frame staleness is harmless
    /// and deliberate: it keeps the ordering stable instead of making the result depend on tree
    /// order, and a mirror swapping in or out a frame late is invisible.
    private bool WithinBudget(float myDistance)
    {
        // In stereo every live mirror is TWO extra scene renders, not one, so the tier's budget
        // buys half as many. `MirrorBudget` counts scene renders, not mirrors.
        int budget = Mathf.Max(1, UI.DeviceProfile.MirrorBudget / (_stereoActive ? 2 : 1));
        int nearer = 0;
        foreach (var other in Registry)
        {
            if (ReferenceEquals(other, this)) continue;
            if (other._claimDistance < myDistance && ++nearer >= budget) return false;
        }
        return true;
    }

    /// Cheap dark glass shown when the live reflection is off (out of active range or the
    /// viewer behind the plane). Without this the SubViewport freezes on its LAST rendered
    /// frame and the mirror keeps displaying a stale photo of an old viewpoint.
    private StandardMaterial3D FarMaterial()
    {
        _farMaterial ??= new StandardMaterial3D
        {
            AlbedoColor = new Color(0.05f, 0.06f, 0.07f),
            Metallic = 0.9f,
            Roughness = 0.18f,
        };
        return _farMaterial;
    }

    /// Whether this mirror is currently rendering a live reflection rather than dark glass.
    /// Read by the mirror diagnostic to check the render budget actually caps a room full of
    /// them; a world with eight mirrors must not be doing eight scene renders a frame.
    public bool IsLive => _showingLive;

    /// Whether the last frame rendered two views (one per eye) rather than one.
    public bool IsStereo => _stereoActive;

    private void SetLive(bool live)
    {
        if (_showingLive == live || _surface == null) return;
        _surface.MaterialOverride = live && _liveMaterial != null ? _liveMaterial : FarMaterial();
        _showingLive = live && _liveMaterial != null;
    }

    public override void _Process(double _)
    {
        var viewer = GetViewport().GetCamera3D();
        if (viewer == null) return;

        // Stereo or mono? Resolved before the budget ranking, because a stereo mirror claims
        // two scene renders out of the tier's budget rather than one.
        var xr = ResolveStereo(viewer, out var xrOrigin);
        _stereoActive = xr != null;

        // Ensure the mirror's SubViewport shares the main scene's World3D
        if (_viewport.World3D == null || _viewport.World3D != GetViewport().World3D)
        {
            var w3d = GetViewport().World3D;
            if (w3d != null) _viewport.World3D = w3d;
        }

        // Skip the extra render when far away. The active range follows the device tier —
        // a mirror is a whole extra scene render, the first thing to pull in on a Quest.
        // Hysteresis on the boundary so a viewer loitering at ~range doesn't flip the glass
        // between live and fake every frame.
        Vector3 planePos = _surface.GlobalPosition;
        Vector3 viewerPos = viewer.GlobalPosition;

        // Mirror plane normal = the surface's +Z (facing out toward the viewer).
        Vector3 mirrorNormal = _surface.GlobalBasis.Z.Normalized();
        float signedDist = mirrorNormal.Dot(viewerPos - planePos);

        float range = Mathf.Min(_activeRange, UI.DeviceProfile.MirrorRange);
        float dist = viewerPos.DistanceTo(planePos);

        // A candidate is a mirror the viewer is in FRONT of and close enough to. Hysteresis on
        // the range boundary so someone loitering at ~range doesn't flip the glass every frame.
        bool candidate = signedDist > 0.03f && dist < (_showingLive ? range : range - 0.5f);

        // Publish before ranking, so the other mirrors rank against a current value.
        _claimDistance = candidate ? dist : float.PositiveInfinity;

        bool live = candidate && WithinBudget(dist);
        if (!live)
        {
            _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            if (_viewportR != null) _viewportR.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            SetLive(false);
            return;
        }
        SetLive(true);

        if (xr == null)
        {
            RenderView(_viewport, _mirrorCam, viewer.GlobalTransform,
                       viewer.GetCameraProjection(), viewer.Far, mirrorNormal, planePos,
                       WantedSize(), false);
            // The right eye's target must not keep ticking after a headset is taken off.
            if (_viewportR != null) _viewportR.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            return;
        }

        // Stereo. Build the right-eye pair on first use, then render one reflection per view
        // from that eye's own position and off-axis window projection.
        if (_viewportR == null)
        {
            (_viewportR, _mirrorCamR) = BuildView("R");
            // Bind once, not per frame: SetShaderParameter boxes a Variant, and this is a hot path.
            _liveMaterial?.SetShaderParameter("mirror_texture_r", _viewportR.GetTexture());
        }
        if (_viewportR.World3D != GetViewport().World3D && GetViewport().World3D != null)
            _viewportR.World3D = GetViewport().World3D;

        Transform3D originXf = xrOrigin.GlobalTransform;
        Vector2I baseSize = XrViewSize(xr);
        RenderEye(xr, 0, _viewport, _mirrorCam, originXf, viewer.Near, viewer.Far,
                  mirrorNormal, planePos, baseSize);
        RenderEye(xr, 1, _viewportR, _mirrorCamR, originXf, viewer.Near, viewer.Far,
                  mirrorNormal, planePos, baseSize);
    }

    /// The stereo `XRInterface` driving the main viewport, or null when this frame is mono.
    /// Everything here has to hold: a viewport actually rendering through XR, a live primary
    /// interface reporting two views and real tracking, and an `XROrigin3D` above the active
    /// camera (the reference frame `GetTransformForView` resolves eye poses against).
    private XRInterface ResolveStereo(Camera3D viewer, out XROrigin3D origin)
    {
        origin = null;
        var vp = GetViewport();
        if (vp == null || !vp.UseXR) return null;
        var iface = XRServer.PrimaryInterface;
        if (iface == null || !iface.IsInitialized()) return null;
        if (iface.GetViewCount() != 2) return null;
        if (iface.GetTrackingStatus() == XRInterface.TrackingStatus.NotTracking) return null;
        Node n = viewer;
        while (n != null && n is not XROrigin3D) n = n.GetParent();
        origin = n as XROrigin3D;
        return origin != null ? iface : null;
    }

    /// Per-eye render target size: the headset's own per-view render target, scaled by the tier.
    /// The headset target determines the pixel budget; the desktop window is irrelevant in XR.
    private static Vector2I XrViewSize(XRInterface xr)
    {
        var rt = xr.GetRenderTargetSize();
        if (rt.X < 1 || rt.Y < 1) rt = new Vector2(1024, 1024);
        float s = Mathf.Clamp(UI.DeviceProfile.MirrorResolutionScale, 0.25f, 1f);
        return new Vector2I(Mathf.Max(256, Mathf.RoundToInt(rt.X * s)),
                            Mathf.Max(256, Mathf.RoundToInt(rt.Y * s)));
    }

    private void RenderEye(XRInterface xr, uint view, SubViewport vp, Camera3D cam,
                           Transform3D originXf, float viewerNear, float far,
                           Vector3 mirrorNormal, Vector3 planePos, Vector2I baseSize)
    {
        // Per-eye position determines the off-axis view through the same physical glass.
        // Head orientation and lens projection are applied when the headset draws that quad.
        float aspect = baseSize.X / Mathf.Max(1f, baseSize.Y);
        Transform3D eye = xr.GetTransformForView(view, originXf);
        RenderView(vp, cam, eye, xr.GetProjectionForView(view, aspect, viewerNear, far),
                   far, mirrorNormal, planePos, baseSize, view == 1);
    }

    /// Frame the physical glass from one reflected eye with a parallel clipping plane.
    private void RenderView(SubViewport vp, Camera3D cam, Transform3D eye, Projection viewerProjection,
                            float far, Vector3 mirrorNormal, Vector3 planePos, Vector2I baseSize,
                            bool rightEye)
    {
        // Treat the glass as a window seen from the reflected eye. Keeping the camera axes
        // parallel to that window makes the ENTIRE host wall fall behind the near plane.
        // A viewer-oriented camera with a scalar near distance cannot clip an oblique wall:
        // half the backing survives, while increasing near also erases valid room geometry.
        var surfaceBasis = _surface.GlobalBasis;
        Vector3 right = surfaceBasis.X.Normalized();
        Vector3 up = surfaceBasis.Y.Normalized();
        var basis = new Basis(-right, up, -mirrorNormal);
        Vector3 reflectedEye = eye.Origin - 2f * mirrorNormal.Dot(eye.Origin - planePos) * mirrorNormal;
        cam.GlobalTransform = new Transform3D(basis, reflectedEye);
        float fullWidth = _size.X * surfaceBasis.X.Length();
        float fullHeight = _size.Y * surfaceBasis.Y.Length();
        TextureWindow textureWindow = VisibleTextureWindow(eye, viewerProjection, planePos, mirrorNormal);
        SetTextureWindow(rightEye, textureWindow);

        // A full-screen close-up sees only this sub-rectangle. Frame it directly so the normal
        // per-view pixel budget is spent on pixels that can actually reach the player, rather
        // than on metres of glass outside their view.
        Vector2 cropSize = textureWindow.Size;
        float width = fullWidth * cropSize.X;
        float height = fullHeight * cropSize.Y;
        Vector2 cropMid = (textureWindow.Min + textureWindow.Max) * .5f;
        Vector3 cropCentre = planePos + right * ((.5f - cropMid.X) * fullWidth) +
                             up * ((.5f - cropMid.Y) * fullHeight);
        Vector3 window = basis.Inverse() * (cropCentre - reflectedEye);
        float distance = Mathf.Max(.001f, -window.Z);
        float near = distance + .01f;
        float scale = near / distance;
        float ratio = width / height;

        // Spend at most the existing per-view pixel budget. Quantising the height avoids a
        // render-target reallocation on every millimetre of head movement while preserving the
        // crop's exact aspect ratio.
        int rawHeight = Mathf.Max(64, Mathf.RoundToInt(Mathf.Min(baseSize.Y, baseSize.X / ratio)));
        int h = rawHeight > 96 ? Mathf.Max(64, rawHeight / 16 * 16) : rawHeight;
        var want = new Vector2I(Mathf.Max(64, Mathf.RoundToInt(h * ratio)), h);
        if (vp.Size != want) vp.Size = want;
        cam.SetFrustum(height * scale, new Vector2(window.X, window.Y) * scale,
            near, Mathf.Max(far, near + .1f));
        vp.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
    }

    /// Return the full-glass texture rectangle which covers the player's current view. It is
    /// only safe to crop when every screen-corner ray lands inside this mirror; otherwise a
    /// player can see the mirror's edge and needs the normal whole-glass texture. The padding
    /// gives fast head motion a few pixels of safety at the view edge.
    private TextureWindow VisibleTextureWindow(Transform3D eye, Projection viewerProjection,
                                               Vector3 planePos, Vector3 mirrorNormal)
    {
        float projectionNear = Mathf.Max(.001f, viewerProjection.GetZNear());
        DecodeFrustum(viewerProjection, projectionNear, out float viewHeight, out Vector2 viewCentre,
                       out float viewRatio);
        float viewWidth = viewHeight * viewRatio;
        if (viewWidth <= 0f || viewHeight <= 0f) return TextureWindow.Full;

        Vector2 min = new(float.PositiveInfinity, float.PositiveInfinity);
        Vector2 max = new(float.NegativeInfinity, float.NegativeInfinity);
        for (int yi = 0; yi < 2; yi++)
        for (int xi = 0; xi < 2; xi++)
        {
            float x = viewCentre.X + (xi == 0 ? -.5f : .5f) * viewWidth;
            float y = viewCentre.Y + (yi == 0 ? -.5f : .5f) * viewHeight;
            Vector3 ray = (eye.Basis * new Vector3(x, y, -projectionNear)).Normalized();
            float facing = mirrorNormal.Dot(ray);
            if (facing >= -1e-5f) return TextureWindow.Full;
            float travel = mirrorNormal.Dot(planePos - eye.Origin) / facing;
            if (travel <= .001f) return TextureWindow.Full;

            Vector3 local = _surface.ToLocal(eye.Origin + ray * travel);
            // `fullUv` is the texture-coordinate counterpart to `vec2(1.0 - UV.x, UV.y)` in
            // the shader. Its axes are therefore already in the reflected-camera orientation.
            Vector2 fullUv = new(.5f - local.X / _size.X, .5f - local.Y / _size.Y);
            if (fullUv.X <= 0f || fullUv.X >= 1f || fullUv.Y <= 0f || fullUv.Y >= 1f)
                return TextureWindow.Full;
            min = min.Min(fullUv);
            max = max.Max(fullUv);
        }

        Vector2 span = max - min;
        if (span.X >= .98f && span.Y >= .98f) return TextureWindow.Full;

        // Overscan is proportional to the visible patch, with a small absolute floor. It keeps
        // the shader from sampling right on a cropped texture border as a head turns.
        Vector2 pad = new(Mathf.Max(.01f, span.X * .10f), Mathf.Max(.01f, span.Y * .10f));
        min = (min - pad).Max(Vector2.Zero);
        max = (max + pad).Min(Vector2.One);
        if (max.X - min.X < .01f || max.Y - min.Y < .01f) return TextureWindow.Full;
        return new TextureWindow(min, max);
    }

    private void SetTextureWindow(bool rightEye, TextureWindow textureWindow)
    {
        if (rightEye)
        {
            if (_textureWindowR.IsApprox(textureWindow)) return;
            _textureWindowR = textureWindow;
        }
        else
        {
            if (_textureWindowL.IsApprox(textureWindow)) return;
            _textureWindowL = textureWindow;
        }
        _liveMaterial?.SetShaderParameter(rightEye ? "mirror_uv_rect_r" : "mirror_uv_rect",
                                           textureWindow.AsVector4);
    }

    // Kept internal for the rendered diagnostics. It lets them assert that a close full-screen
    // mirror really uses a cropped source window, not a high-cost oversized full-glass target.
    internal Vector4 TextureWindowForDiagnostic(Camera3D camera) =>
        ReferenceEquals(camera, _mirrorCamR) ? _textureWindowR.AsVector4 : _textureWindowL.AsVector4;

    internal Vector2I TextureTargetSizeForDiagnostic(Camera3D camera) =>
        ReferenceEquals(camera, _mirrorCamR) && _viewportR != null ? _viewportR.Size : _viewport.Size;

    /// Recover a frustum's near-plane rectangle from its projection matrix, restated at `near`.
    ///
    /// For any frustum projection the four shape coefficients are independent of the near plane
    /// (both numerator and denominator scale with it), so this is exact whatever near `proj` was
    /// built with — which is what lets the reflection camera push its near plane past the glass
    /// while keeping the viewer's rays:
    ///
    ///   P[0][0] = 2n/(r-l)   P[1][1] = 2n/(t-b)   P[2][0] = (r+l)/(r-l)   P[2][1] = (t+b)/(t-b)
    ///
    /// `height` = t-b, `centre` = the rect's midpoint, `ratio` = (r-l)/(t-b) — the aspect the
    /// render target must have for `Camera3D.SetFrustum(height, …)` to produce this exact rect.
    internal static void DecodeFrustum(Projection proj, float near,
                                       out float height, out Vector2 centre, out float ratio)
    {
        float p00 = proj.X.X, p11 = proj.Y.Y;
        float width = Mathf.Abs(p00) > 1e-6f ? 2f * near / p00 : 2f * near;
        height = Mathf.Abs(p11) > 1e-6f ? 2f * near / p11 : 2f * near;
        centre = new Vector2(proj.Z.X * width * 0.5f, proj.Z.Y * height * 0.5f);
        ratio = height > 1e-6f ? width / height : 1f;
    }

}
