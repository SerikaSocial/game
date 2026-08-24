using Godot;

namespace SerikaSocial.World;

/// A planar-reflection mirror using the frustum-camera technique from Mirror3D
/// (https://github.com/Joy-less/Mirror3D, MIT). A SubViewport renders the shared world
/// from a camera that is the reflection of the active camera across the mirror plane.
/// The frustum offset ensures the projection is correct from the viewer's perspective.
///
/// This produces a true mirror reflection — you see yourself and the room behind you,
/// with correct perspective that shifts as you move.
[GlobalClass]
public partial class Mirror : Node3D
{
    private readonly Vector2 _size;
    private float _frustumScale;
    private SubViewport _viewport;
    private Camera3D _mirrorCam;
    private MeshInstance3D _surface;
    private float _activeRange;
    private float _cullFar = 50.0f;

    public Mirror(float width = 1.4f, float height = 2.2f, float activeRange = 12f, float frustumScale = 1.0f)
    {
        _size = new Vector2(width, height);
        _activeRange = activeRange;
        _frustumScale = frustumScale;
    }

    public static Mirror Create(Vector3 position, float yawDeg, float width = 1.4f, float height = 2.2f, float frustumScale = 1.0f)
    {
        var m = new Mirror(width, height, 12f, frustumScale)
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
        // otherwise the near-plane push would reveal the back of the wall in an ugly outline.
        // The main player cameras render all layers, so they still see the mirror normally.

        // Offscreen render target — renders the SAME world (OwnWorld3D=false) so the mirror
        // camera sees the real scene: the player, the room, everything.
        // The viewport aspect ratio must match the main screen so the SCREEN_UV shader
        // mapping produces a correct reflection. The height is based on the mirror size
        // for resolution; the width follows the main screen's aspect ratio (updated per-frame).
        int texH = Mathf.Max(256, (int)(_size.Y * 300));
        int texW = Mathf.Max(256, (int)(texH * 16f / 9f));
        _viewport = new SubViewport
        {
            Size = new Vector2I(texW, texH),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
            OwnWorld3D = false,
            // Ensure the viewport renders with the same 3D world and environment as the main view.
            CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.LinearWithMipmaps,
        };
        AddChild(_viewport);

        // Current must be TRUE: `Current` is per-viewport, so this only makes the camera the
        // active one INSIDE the SubViewport (it never touches the main window's camera). With
        // it false the SubViewport had no active camera and rendered nothing.
        _mirrorCam = new Camera3D
        {
            Current = true,
            // TopLevel so its GlobalTransform is written in world space, not influenced by the
            // SubViewport's (identity) transform chain. This makes the reflection math reliable.
            TopLevel = true,
            // Do not render the mirror surface (layer 2), only the room beyond it (layer 1).
            CullMask = 1048575u & ~(1u << 1),
        };
        _viewport.AddChild(_mirrorCam);

        // The glass quad, textured with the viewport via the mirror shader.
        _surface = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = _size },
            Position = new Vector3(0, _size.Y * 0.5f, 0),
            Layers = 1u << 1,
        };
        var mirrorShader = ResourceLoader.Load<Shader>("res://Shaders/mirror.gdshader");
        if (mirrorShader != null)
        {
            var mat = new ShaderMaterial { Shader = mirrorShader };
            mat.SetShaderParameter("color", new Color(0.9f, 0.97f, 0.94f));
            mat.SetShaderParameter("mirror_texture", _viewport.GetTexture());
            _surface.MaterialOverride = mat;
        }
        else
        {
            // The shader must be packed with the build (it is, via all_resources) — if this
            // ever fires in an exported build it is a real regression, so make it loud and
            // fall back to showing the raw reflection texture rather than a broken-pink quad.
            GD.PrintErr("Mirror: res://Shaders/mirror.gdshader failed to load — using unshaded fallback");
            _surface.MaterialOverride = new StandardMaterial3D
            {
                AlbedoTexture = _viewport.GetTexture(),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
        }
        AddChild(_surface);
    }

    public override void _Process(double _)
    {
        var viewer = GetViewport().GetCamera3D();
        if (viewer == null) return;

        // Ensure the mirror's SubViewport shares the main scene's World3D
        if (_viewport.World3D == null || _viewport.World3D != GetViewport().World3D)
        {
            var w3d = GetViewport().World3D;
            if (w3d != null) _viewport.World3D = w3d;
        }

        // Keep the SubViewport aspect ratio in sync with the main screen so the
        // SCREEN_UV shader mapping stays correct across window resizes / orientation changes.
        var mainRect = GetViewport().GetVisibleRect();
        if (mainRect.Size.X > 0 && mainRect.Size.Y > 0)
        {
            float mainAspect = mainRect.Size.X / mainRect.Size.Y;
            int wantH = Mathf.Max(256, (int)(_size.Y * 300));
            int wantW = Mathf.Max(256, (int)(wantH * mainAspect));
            var want = new Vector2I(wantW, wantH);
            if (_viewport.Size != want)
                _viewport.Size = want;
        }

        // Skip the extra render when far away. The active range follows the device tier —
        // a mirror is a whole extra scene render, the first thing to pull in on a Quest.
        Vector3 planePos = _surface.GlobalPosition;
        Vector3 viewerPos = viewer.GlobalPosition;
        float range = Mathf.Min(_activeRange, UI.DeviceProfile.MirrorRange);
        if (viewerPos.DistanceTo(planePos) > range)
        {
            _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            return;
        }

        // Mirror plane normal = the surface's +Z (facing out toward the viewer).
        Vector3 mirrorNormal = _surface.GlobalBasis.Z.Normalized();

        // Build the reflection transform matrix (mirrors through the plane).
        Transform3D mirrorTransform = GetMirrorTransform(mirrorNormal, _surface.GlobalPosition);

        // Apply: mirror_camera = mirror_transform * player_camera
        _mirrorCam.GlobalTransform = mirrorTransform * viewer.GlobalTransform;

        // The reflection matrix produces a left-handed (improper) coordinate system.
        // Flip the camera's right (X) axis to restore right-handedness so the image
        // is not inside-out. This is the key fix for the "head gone" / distorted view.
        var b = _mirrorCam.GlobalBasis;
        b.X = -b.X;
        _mirrorCam.GlobalBasis = b;

        // Near clip: place it exactly at the mirror plane so the wall behind the
        // mirror is culled. No raycast needed — the distance from the reflected
        // camera to the mirror plane is the correct near value.
        Vector3 cameraToMirrorOffset = planePos - _mirrorCam.GlobalPosition;
        float distToPlane = Mathf.Abs(mirrorNormal.Dot(cameraToMirrorOffset));
        float near = Mathf.Max(0.01f, distToPlane - 0.02f);

        // Asymmetric frustum offset: shift the projection so the mirror quad
        // fills the viewport with correct perspective. The offset is the mirror
        // center position in the reflected camera's local space.
        Vector3 camToMirrorLocal = _mirrorCam.GlobalBasis.Inverse() * cameraToMirrorOffset;
        Vector2 frustumOffset = new Vector2(-camToMirrorLocal.X, -camToMirrorLocal.Y);

        // Frustum size must match the main camera's FOV so the SCREEN_UV shader
        // mapping is correct. Using the mirror's physical width made the frustum
        // far too narrow, zooming the reflection in massively.
        float fovRad = Mathf.DegToRad(viewer.Fov);
        float frustumSize = 2.0f * near * Mathf.Tan(fovRad * 0.5f) * _frustumScale;
        _mirrorCam.SetFrustum(frustumSize, frustumOffset, near, viewer.Far);

        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
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
