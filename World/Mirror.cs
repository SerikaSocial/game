using Godot;

namespace SerikaSocial.World;

/// A planar-reflection mirror using the frustum-camera technique from Mirror3D
/// (https://github.com/Joy-less/Mirror3D, MIT). A SubViewport renders the shared world
/// from a camera that is the reflection of the active camera across the mirror plane.
/// The frustum offset ensures the projection is correct from the viewer's perspective.
///
/// This produces a true mirror reflection — you see yourself and the room behind you,
/// with correct perspective that shifts as you move.
public partial class Mirror : Node3D
{
    private readonly Vector2 _size;
    private SubViewport _viewport;
    private Camera3D _mirrorCam;
    private MeshInstance3D _surface;
    private float _activeRange;
    private float _cullNear = 0.05f;
    private float _cullFar = 50.0f;

    public Mirror(float width = 1.4f, float height = 2.2f, float activeRange = 12f)
    {
        _size = new Vector2(width, height);
        _activeRange = activeRange;
    }

    public static Mirror Create(Vector3 position, float yawDeg, float width = 1.4f, float height = 2.2f)
    {
        var m = new Mirror(width, height)
        {
            Name = "Mirror",
            Position = position,
            RotationDegrees = new Vector3(0, yawDeg, 0),
        };
        return m;
    }

    public override void _Ready()
    {
        // Ornate frame around the glass.
        var frame = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(_size.X + 0.16f, _size.Y + 0.16f, 0.08f) },
            Position = new Vector3(0, _size.Y * 0.5f, -0.04f),
        };
        frame.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.35f, 0.26f, 0.16f),
            Metallic = 0.5f, Roughness = 0.35f,
        };
        AddChild(frame);

        // Offscreen render target — renders the SAME world (OwnWorld3D=false) so the mirror
        // camera sees the real scene: the player, the room, everything.
        int texW = Mathf.Max(256, (int)(_size.X * 300));
        int texH = Mathf.Max(256, (int)(_size.Y * 300));
        _viewport = new SubViewport
        {
            Size = new Vector2I(texW, texH),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
            OwnWorld3D = false,
        };
        AddChild(_viewport);

        _mirrorCam = new Camera3D { Current = false };
        _viewport.AddChild(_mirrorCam);

        // The glass quad, textured with the viewport via the mirror shader.
        _surface = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = _size },
            Position = new Vector3(0, _size.Y * 0.5f, 0),
        };
        var mirrorShader = ResourceLoader.Load<Shader>("res://Shaders/mirror.gdshader");
        var mat = new ShaderMaterial { Shader = mirrorShader };
        mat.SetShaderParameter("color", new Color(0.9f, 0.97f, 0.94f));
        mat.SetShaderParameter("mirror_texture", _viewport.GetTexture());
        _surface.MaterialOverride = mat;
        AddChild(_surface);
    }

    public override void _Process(double _)
    {
        var viewer = GetViewport().GetCamera3D();
        if (viewer == null) return;

        // Skip the extra render when far away.
        Vector3 planePos = _surface.GlobalPosition;
        if (viewer.GlobalPosition.DistanceTo(planePos) > _activeRange)
        {
            _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            return;
        }
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;

        // Mirror plane normal = the surface's +Z (facing out toward the viewer).
        Vector3 mirrorNormal = _surface.GlobalBasis.Z.Normalized();

        // Build the reflection transform matrix (mirrors through the plane).
        Transform3D mirrorTransform = GetMirrorTransform(mirrorNormal, _surface.GlobalPosition);

        // Apply: mirror_camera = mirror_transform * player_camera
        _mirrorCam.GlobalTransform = mirrorTransform * viewer.GlobalTransform;

        // Look perpendicular into the mirror plane (toward the midpoint between
        // the mirror camera and the player camera).
        Vector3 lookTarget = (_mirrorCam.GlobalPosition / 2f) + (viewer.GlobalPosition / 2f);
        _mirrorCam.LookAt(lookTarget, _surface.GlobalBasis.Y);

        // Frustum offset: the camera needs to "see" through the mirror surface from
        // its reflected position. This is the key trick from Mirror3D that makes the
        // projection correct — without it, the reflection has the wrong perspective.
        Vector3 cameraToMirror = _surface.GlobalPosition - _mirrorCam.GlobalPosition;
        float near = Mathf.Abs(cameraToMirror.Dot(mirrorNormal)) + _cullNear;
        float far = cameraToMirror.Length() + _cullFar;

        // Transform offset to camera's local coordinate system for set_frustum.
        Vector3 localOffset = _mirrorCam.GlobalBasis.Inverse() * cameraToMirror;
        var frustumOffset = new Vector2(localOffset.X, localOffset.Y);

        _mirrorCam.SetFrustum(_size.X, frustumOffset, near, far);
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
