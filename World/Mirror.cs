using Godot;

namespace SerikaSocial.World;

/// A planar-reflection mirror so you can see your own avatar. A SubViewport renders the shared
/// world from a camera that is the reflection of the active camera across the mirror plane; the
/// result texture is shown on the mirror quad.
///
/// This is the standard planar-reflection trick: reflect the viewer's transform across the mirror
/// plane each frame and render the scene from there. It's one extra scene render, so the mirror is
/// modestly sized and only updates while the player is reasonably close.
public partial class Mirror : Node3D
{
    private readonly Vector2 _size;
    private SubViewport _viewport;
    private Camera3D _mirrorCam;
    private MeshInstance3D _surface;
    private float _activeRange;

    public Mirror(float width = 1.4f, float height = 2.2f, float activeRange = 8f)
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

        // Offscreen render target.
        // A SubViewport with OwnWorld3D=false renders the SAME world it's parented into, so the
        // mirror camera sees the real scene (us, the room, everything).
        _viewport = new SubViewport
        {
            Size = new Vector2I(720, (int)(720 * _size.Y / _size.X)),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
            OwnWorld3D = false,
        };
        AddChild(_viewport);

        _mirrorCam = new Camera3D { Current = false };
        _viewport.AddChild(_mirrorCam);

        // The glass quad, textured with the viewport.
        _surface = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = _size },
            Position = new Vector3(0, _size.Y * 0.5f, 0),
        };
        var mat = new StandardMaterial3D
        {
            AlbedoTexture = _viewport.GetTexture(),
            // The reflected image is already lit; don't relight it.
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Metallic = 0.6f,
            Roughness = 0.05f,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
        };
        _surface.MaterialOverride = mat;
        AddChild(_surface);
    }

    public override void _Process(double _)
    {
        var viewer = GetViewport().GetCamera3D();
        if (viewer == null) return;

        // Skip the extra render when far away.
        Vector3 planePos = GlobalPosition;
        if (viewer.GlobalPosition.DistanceTo(planePos) > _activeRange)
        {
            _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            return;
        }
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;

        // Mirror plane: passes through the surface, normal = the mirror's +Z (facing out).
        Basis b = GlobalTransform.Basis;
        Vector3 n = b.Z.Normalized();

        // Reflect the viewer's position across the plane.
        Vector3 toViewer = viewer.GlobalPosition - planePos;
        float d = toViewer.Dot(n);
        Vector3 reflectedPos = viewer.GlobalPosition - 2f * d * n;

        // Reflect the viewer's look/up directions across the plane normal.
        Vector3 fwd = -viewer.GlobalTransform.Basis.Z;
        Vector3 up = viewer.GlobalTransform.Basis.Y;
        Vector3 rFwd = Reflect(fwd, n);
        Vector3 rUp = Reflect(up, n);

        var t = new Transform3D(Basis.Identity, reflectedPos);
        _mirrorCam.GlobalTransform = t;
        // LookingAt handles building the basis; target is a point along the reflected forward.
        _mirrorCam.LookAtFromPosition(reflectedPos, reflectedPos + rFwd, rUp);
        _mirrorCam.Fov = viewer.Fov;
    }

    private static Vector3 Reflect(Vector3 v, Vector3 n) => v - 2f * v.Dot(n) * n;
}
