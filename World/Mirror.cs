using Godot;

namespace SerikaSocial.World;

/// A planar-reflection mirror using the frustum-camera technique from Mirror3D
/// (https://github.com/Joy-less/Mirror3D, MIT). A SubViewport renders the shared world
/// from a camera that is the reflection of the active camera across the mirror plane.
///
/// The optical contract, derived end-to-end (see World/MirrorDiagnostic.cs which verifies it
/// with rendered probes): the reflection camera must be the reflected VIEWER EYE with the
/// viewer's local X negated (which converts the improper reflection transform into a proper
/// camera whose image is exactly the true reflection mirrored horizontally), and its
/// projection must equal the viewer's projection — same FOV, same aspect, ZERO offset. The
/// shader's `1 - SCREEN_UV.x` sample un-mirrors the image at display time. Sampling that
/// ties texture pixels to screen pixels only holds under that exact combination: adding an
/// asymmetric "frustum offset" toward the quad centre tilts the reflection axis and breaks
/// every viewpoint that isn't dead-centre, which was the long-standing wrong-image bug.
///
/// This produces a true mirror reflection — you see yourself and the room behind you,
/// with correct perspective that shifts as you move.
///
/// ── Stereo (VR) ──────────────────────────────────────────────────────────────────────
/// In XR the main viewport is rendered TWICE per frame, once per eye, with two different
/// projections — and HMD optics make those projections ASYMMETRIC (nonzero frustum offset is
/// the norm, not an error). `GetViewport().GetCamera3D()` hands back the single `XRCamera3D`,
/// which sits at the midpoint between the eyes, so one reflection rendered from "the camera"
/// with a symmetric frustum is wrong for *both* eyes: stereo fusion breaks and the reflection
/// swims. Screen-space sampling is not the problem — the single view is.
///
/// So a mirror renders one reflection PER VIEW: two SubViewports, two reflection cameras, each
/// one the reflection of that eye's own pose carrying that eye's own projection, and the shader
/// picks between the two textures on `VIEW_INDEX`. Mono is just the one-view case of the same
/// code (`VIEW_INDEX` is 0 outside multiview, so desktop samples the same texture it always did).
///
/// Reproducing an eye's projection needs two things the mono case never exercised:
///
///  * **Frustum offset.** `Camera3D.SetFrustum(height, offset, near, far)` builds the rect
///    `[-w/2+off.x, +w/2+off.x] × [-h/2+off.y, +h/2+off.y]` at the near plane, with
///    `w = height × the SubViewport's aspect`. The eye's rect is recovered from its projection
///    matrix (`DecodeFrustum` below) and the SubViewport is then SIZED to that rect's aspect, so
///    the one axis `SetFrustum` will not let us state independently is stated by pixel count.
///  * **The offset's X must be NEGATED.** The reflection camera is the reflected eye with local
///    X flipped, and the shader un-flips with `1 - SCREEN_UV.x`. Writing the display coordinate
///    of a scene point through both flips and asking it to equal the true reflection's gives
///    `[l', r'] = [-r, -l]` — same width, mirrored centre. For a symmetric rect the centre is 0
///    and the negation is invisible, which is exactly why mono never noticed.
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
        // Its SIZE tracks the main viewport's pixels, not the mirror's physical metres. The
        // shader samples this texture by SCREEN_UV, which makes it a screen-space image: one
        // texel must land on one screen pixel or the reflection is a blur. Sizing from metres
        // (the old `_size.Y * 300`) rendered a 2.2 m mirror at 660 px and upscaled it ~1.6× on a
        // 1080p display — visibly softer than everything around it, and it got *worse* the
        // smaller the mirror.
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
            // Mono. The viewer's own projection matrix IS the contract — decoding it recovers
            // exactly the symmetric rect the old `2·near·tan(fov/2)` formula produced, and the
            // main viewport's aspect, and a zero offset.
            RenderView(_viewport, _mirrorCam, viewer.GlobalTransform, viewer.GetCameraProjection(),
                       viewer.Far, mirrorNormal, planePos, WantedSize());
            // The right eye's target must not keep ticking after a headset is taken off.
            if (_viewportR != null) _viewportR.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            return;
        }

        // Stereo. Build the right-eye pair on first use, then render one reflection per view
        // from that eye's OWN pose and OWN (asymmetric) projection.
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
        RenderEye(xr, 0, _viewport, _mirrorCam, originXf, viewer.Far, mirrorNormal, planePos, baseSize);
        RenderEye(xr, 1, _viewportR, _mirrorCamR, originXf, viewer.Far, mirrorNormal, planePos, baseSize);
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
    /// This is the stereo equivalent of `WantedSize` — in XR the "screen" the shader's SCREEN_UV
    /// runs across is the eye's render target, not the desktop window.
    private static Vector2I XrViewSize(XRInterface xr)
    {
        var rt = xr.GetRenderTargetSize();
        if (rt.X < 1 || rt.Y < 1) rt = new Vector2(1024, 1024);
        float s = Mathf.Clamp(UI.DeviceProfile.MirrorResolutionScale, 0.25f, 1f);
        return new Vector2I(Mathf.Max(256, Mathf.RoundToInt(rt.X * s)),
                            Mathf.Max(256, Mathf.RoundToInt(rt.Y * s)));
    }

    private void RenderEye(XRInterface xr, uint view, SubViewport vp, Camera3D cam,
                           Transform3D originXf, float far, Vector3 mirrorNormal, Vector3 planePos,
                           Vector2I baseSize)
    {
        // `GetProjectionForView`'s aspect argument is ignored by OpenXR — the rect comes from the
        // runtime's own FOV tangents — and the matrix's shape coefficients are independent of the
        // near plane anyway, so any near/far here yields the same decoded rect ratio and offsets.
        RenderView(vp, cam, xr.GetTransformForView(view, originXf),
                   xr.GetProjectionForView(view, 1.0, 0.05, far), far, mirrorNormal, planePos, baseSize);
    }

    /// Point one reflection view at one viewer eye.
    ///
    /// `baseSize` only sets the pixel BUDGET: the render target is re-proportioned to the eye's
    /// own frustum aspect, because `SetFrustum` takes a height and derives the width from the
    /// viewport's aspect — pixel count is the only place the horizontal extent can be stated.
    private void RenderView(SubViewport vp, Camera3D cam, Transform3D eye, Projection proj,
                            float far, Vector3 mirrorNormal, Vector3 planePos, Vector2I baseSize)
    {
        // Reflection camera = reflection matrix applied to the viewer eye…
        cam.GlobalTransform = GetMirrorTransform(mirrorNormal, planePos) * eye;

        // …which is improper (left-handed), so negate the local X axis to restore a proper
        // right-handed camera. The resulting image is the true reflection mirrored once
        // horizontally; the shader's `1 - SCREEN_UV.x` un-mirrors it at display time.
        var b = cam.GlobalBasis;
        b.X = -b.X;
        cam.GlobalBasis = b;

        // Near clip just BEYOND the mirror plane (a centimetre past it). The wall a mirror
        // hangs on is usually coplanar with the glass; clipping at-or-short-of the plane lets
        // that wall's face render into the reflection as a striped smear. A centimetre of
        // clip depth past the glass is invisible and kills the wall dead.
        float distToPlane = mirrorNormal.Dot(planePos - cam.GlobalPosition);
        float near = Mathf.Max(0.01f, distToPlane + 0.01f);

        // The projection must EQUAL the viewer's for the SCREEN_UV mapping to be pixel-correct —
        // see the class doc. Moving the near plane along an unchanged frustum preserves the rays,
        // so the image still matches the viewer's while the deep near plane clips the wall.
        DecodeFrustum(proj, near, out float height, out Vector2 centre, out float ratio);

        // Size the target to the eye's frustum aspect, spending `baseSize`'s vertical pixels.
        int h = Mathf.Max(64, baseSize.Y);
        var want = new Vector2I(Mathf.Max(64, Mathf.RoundToInt(h * ratio)), h);
        // Mono keeps the exact main-viewport size it always had: the decoded ratio is the main
        // aspect to within rounding, and re-deriving it would churn the target on odd sizes.
        if (Mathf.Abs(want.X - baseSize.X) <= 1) want.X = baseSize.X;
        if (vp.Size != want) vp.Size = want;

        // X of the offset is NEGATED because this camera renders X-flipped and the shader
        // un-flips it: matching the display coordinate to the true reflection's forces the
        // horizontal rect to [-r, -l]. Zero for any symmetric (desktop) projection.
        cam.SetFrustum(height, new Vector2(-centre.X, centre.Y), near, far);

        vp.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
    }

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

    /// Calculates the transformation that mirrors through the plane with the given normal
    /// and offset. This is a reflection matrix — it flips one axis across the plane.
    private static Transform3D GetMirrorTransform(Vector3 normal, Vector3 offset)
    {
        float nx = normal.X, ny = normal.Y, nz = normal.Z;
        var basisX = new Vector3(1, 0, 0) - 2f * new Vector3(nx * nx, nx * ny, nx * nz);
        var basisY = new Vector3(0, 1, 0) - 2f * new Vector3(ny * nx, ny * ny, ny * nz);
        var basisZ = new Vector3(0, 0, 1) - 2f * new Vector3(nz * nx, nz * ny, nz * nz);
        Vector3 origin = 2f * normal.Dot(offset) * normal;
        return new Transform3D(basisX, basisY, basisZ, origin);
    }
}
