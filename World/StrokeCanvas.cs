using System.Collections.Generic;
using Godot;

namespace SerikaSocial.World;

/// A world-space drawing surface that renders strokes as tube segments via <c>MultiMeshInstance3D</c>.
///
/// Each stroke is a polyline of <c>StrokePoint</c>s. Between consecutive points we render a
/// cylinder (a "segment") oriented along the line. All segments share one MultiMesh, so the
/// entire canvas is a single draw call regardless of how many strokes exist.
///
/// Strokes arrive from two sources:
/// <list type="bullet">
/// <item>Local: the marker pen calls <c>AddPoint</c> while drawing.</item>
/// <item>Network: <c>Main.cs</c> routes aliased ObjectSync packets to <c>ApplyNetworkPoint</c>.</item>
/// </list>
///
/// The canvas is a child of the world root and persists for the instance lifetime. Late-joiners
/// receive the full stroke history in Phase 3; for now strokes are only live-synced.
public partial class StrokeCanvas : MultiMeshInstance3D
{
    /// Maximum segments before we stop accepting new ones. Prevents unbounded GPU memory on a
    /// very long session. Each segment is one MultiMesh instance (a cylinder).
    private const int MaxSegments = 8192;

    private readonly MultiMesh _mesh;
    private readonly List<StrokePoint> _allPoints = new();
    private readonly Dictionary<ushort, List<StrokePoint>> _strokes = new();
    private int _segmentCount;

    /// Local stroke id allocator. The wire carries no sender discriminator — the stroke id is
    /// all a receiver gets — so the old counter starting at 1 meant every client's FIRST stroke
    /// was also id 1: two people drawing at once interleaved their points into one polyline,
    /// and stray cylinders sprang between unrelated pens. Each canvas now starts at a random
    /// base inside the stroke range: ~32k ids against the dozens a session actually draws, so
    /// two clients meeting is vanishingly unlikely, and a rejoin re-rolls the dice. Ids below
    /// StrokeRangeStart never collide with this scheme, so strokes from older clients are safe.
    private ushort _localStrokeId = (ushort)(NetIds.StrokeRangeStart
        + GD.Randi() % (NetIds.StrokeRangeEnd - NetIds.StrokeRangeStart));

    /// Set by Main.cs so the canvas can broadcast stroke points over the ObjectSync channel.
    /// Signature matches <c>ISerikaTransport.SendObjectSync</c> for direct wiring.
    public System.Action<ushort, float, float, float, float, float, float, float, float, float, float>? SendStrokePoint;

    public StrokeCanvas()
    {
        Name = "StrokeCanvas";
        _mesh = new MultiMesh
        {
            Mesh = new CylinderMesh
            {
                TopRadius = 0.005f,
                BottomRadius = 0.005f,
                Height = 1f,
                Material = new StandardMaterial3D
                {
                    AlbedoColor = new Color(0.9f, 0.9f, 0.95f),
                    Roughness = 0.6f,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                },
            },
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true, // hue per-instance via custom data
        };
        Multimesh = _mesh;
        _mesh.InstanceCount = MaxSegments;
        _mesh.VisibleInstanceCount = 0;
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
    }

    /// Start a new local stroke. Returns the stroke id.
    public ushort BeginStroke(Vector3 pos, float hue, float radius)
    {
        ushort id = _localStrokeId++;
        if (_localStrokeId > NetIds.StrokeRangeEnd) _localStrokeId = NetIds.StrokeRangeStart; // wrap
        AddPoint(id, 0, pos, hue, radius, StrokeNetwork.FlagStart);
        return id;
    }

    /// Continue a local stroke.
    public void ContinueStroke(ushort strokeId, Vector3 pos)
    {
        if (!_strokes.TryGetValue(strokeId, out var pts)) return;
        uint idx = (uint)pts.Count;
        var last = pts[^1];
        AddPoint(strokeId, idx, pos, last.Hue, last.Radius, StrokeNetwork.FlagContinue);
    }

    /// End a local stroke.
    public void EndStroke(ushort strokeId)
    {
        if (!_strokes.TryGetValue(strokeId, out var pts) || pts.Count == 0) return;
        var last = pts[^1];
        AddPoint(strokeId, (uint)pts.Count, last.Position, last.Hue, last.Radius, StrokeNetwork.FlagEnd);
    }

    /// Apply a stroke point received from the network (aliased ObjectSync).
    public void ApplyNetworkPoint(in StrokePoint pt)
    {
        AddPoint(pt.StrokeId, pt.Index, pt.Position, pt.Hue, pt.Radius, pt.Flag, broadcast: false);
    }

    /// Core: add a point, build a segment, and optionally broadcast.
    private void AddPoint(ushort strokeId, uint index, Vector3 pos, float hue, float radius, float flag, bool broadcast = true)
    {
        var pt = new StrokePoint(strokeId, index, pos, hue, radius, flag);
        _allPoints.Add(pt);

        if (!_strokes.TryGetValue(strokeId, out var pts))
        {
            pts = new List<StrokePoint>();
            _strokes[strokeId] = pts;
        }
        pts.Add(pt);

        // Build a segment between the last two points of this stroke (skip on start/end flags).
        if (!pt.IsStart && !pt.IsEnd && pts.Count >= 2)
        {
            BuildSegment(pts[^2], pts[^1]);
        }
        else if (pts.Count >= 2 && !pt.IsEnd)
        {
            // Also build segments between consecutive continuation points that arrived via
            // the start point's immediate successor.
            if (!pts[^1].IsStart && !pts[^1].IsEnd)
                BuildSegment(pts[^2], pts[^1]);
        }

        if (broadcast && SendStrokePoint != null)
        {
            var (x, y, z, qx, qy, qz, qw, lvx, lvy, lvz) = StrokeNetwork.Pack(
                strokeId, index, pos, hue, radius, flag);
            SendStrokePoint(NetIds.StrokeRangeStart, x, y, z, qx, qy, qz, qw, lvx, lvy, lvz);
        }
    }

    /// Orient and position a cylinder between two stroke points.
    private void BuildSegment(StrokePoint a, StrokePoint b)
    {
        if (_segmentCount >= MaxSegments) return;

        var mid = (a.Position + b.Position) * 0.5f;
        var dir = b.Position - a.Position;
        float len = dir.Length();
        if (len < 0.001f) return;

        dir /= len;
        // CylinderMesh is aligned along Y by default. Build a transform that maps Y → dir.
        var transform = new Transform3D(
            Basis.LookingAt(dir, Vector3.Up).Orthonormalized(),
            mid);

        // Scale the basis so the 1-unit cylinder matches the segment length and pen radius.
        float r = Mathf.Max(a.Radius, b.Radius);
        transform = transform.ScaledLocal(new Vector3(r * 2f, len, r * 2f));

        _mesh.SetInstanceTransform(_segmentCount, transform);
        // Custom data: hue in .x so the shader (or material override) can colour per-instance.
        _mesh.SetInstanceCustomData(_segmentCount, new Color(Color.FromHsv(a.Hue, 0.8f, 1f)));
        _segmentCount++;
        _mesh.VisibleInstanceCount = _segmentCount;
    }

    /// Clear all strokes (e.g. on world unload).
    public void Clear()
    {
        _allPoints.Clear();
        _strokes.Clear();
        _segmentCount = 0;
        _mesh.VisibleInstanceCount = 0;
    }
}
