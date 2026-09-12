using System;
using System.Collections.Generic;
using Godot;
using Serika.Script;

namespace SerikaSocial.Player;

/// The rope that ties a co-op team together. Native, not scripted, and that is a design
/// constraint rather than a shortcut: a constraint that pulls on a player body is the one
/// capability the SerikaScript sandbox withholds (docs/serikascript.md §5.1). The world's script
/// owns the round — checkpoints, timers, tie-in — and this owns the physics.
///
/// **Authority.** The relay is not an authoritative sim; whoever owns an object broadcasts its
/// state and everyone trusts it. A joint spanning two bodies owned by two different machines has
/// no owner, which is the whole difficulty. The resolution here is a *symmetric local* solve:
/// every client corrects ONLY its own player, by half of each violation, using the peer positions
/// it already receives in pose frames. Both ends independently apply their half, so the pair
/// converges on the constraint without either machine writing to a body it does not own — and no
/// new wire message is needed, because the inputs are poses that were already being sent.
///
/// The halving matters. Correcting the full error locally means both ends move the whole distance
/// each frame and the team oscillates; correcting half converges.
public partial class RopeTeam : Node3D
{
    /// How far apart two roped players may drift before the rope goes taut.
    [Export] public float Length { get; set; } = 6.0f;

    /// Fraction of a violation this client resolves per physics step. 0.5 because the peer's
    /// client resolves the other half. Damped slightly under a true half to avoid ringing when
    /// both ends see slightly different positions due to interpolation lag.
    private const float ShareOfCorrection = 0.45f;

    /// Beyond this the rope is treated as broken rather than snapping a player across the map —
    /// a peer who teleported, spawned, or whose pose arrived after a stall must not yank the
    /// whole team. This is the difference between a rope and a grappling hook.
    private const float MaxSaneViolation = 25.0f;

    public bool Enabled { get; set; } = true;
    public Func<int, bool> IncludePeer { get; set; }
    private IScriptPlayers _players;
    private Func<CharacterBody3D> _localBody;
    private MeshInstance3D _ropeView;
    private ImmediateMesh _ropeMesh;
    private readonly List<Vector3> _points = new();
    private readonly Dictionary<int, (Vector3 Position, bool Linked)> _links = new();
    private Vector3? _lastLocalPosition;

    public static RopeTeam Create(IScriptPlayers players, Func<CharacterBody3D> localBody, float length)
    {
        return new RopeTeam
        {
            Name = "RopeTeam",
            _players = players,
            _localBody = localBody,
            Length = length,
        };
    }

    public override void _Ready()
    {
        _ropeMesh = new ImmediateMesh();
        _ropeView = new MeshInstance3D
        {
            Name = "RopeView",
            Mesh = _ropeMesh,
            // The rope is a visual aid that must stay legible when it passes through geometry;
            // an unshaded, always-visible line reads better than a lit tube at this scale.
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = new Color(0.85f, 0.72f, 0.45f),
                VertexColorUseAsAlbedo = false,
            },
        };
        AddChild(_ropeView);
    }

    public override void _PhysicsProcess(double delta)
    {
        var body = _localBody?.Invoke();
        if (_players == null || body == null || !GodotObject.IsInstanceValid(body)) return;

        int count = _players.Count;
        if (!Enabled || count < 2)
        {
            _links.Clear(); _lastLocalPosition = null;
            _points.Clear(); RedrawRope(); return;
        }

        // Index 0 is local; each client solves its own links to the rest of the team.
        // Respawning/restarting releases a link until its ends regroup within rope length.
        Vector3 here = body.GlobalPosition;
        bool localTeleported = _lastLocalPosition.HasValue &&
            here.DistanceTo(_lastLocalPosition.Value) > Length;
        foreach (int index in new List<int>(_links.Keys))
            if (index >= count) _links.Remove(index);
        Vector3 correction = Vector3.Zero;

        for (int i = 1; i < count; i++)
        {
            if ((IncludePeer != null && !IncludePeer(i)) || !_players.TryGetPosition(i, out var them)) { _links.Remove(i); continue; }

            Vector3 delta3 = them - here;
            float dist = delta3.Length();
            bool known = _links.TryGetValue(i, out var previous);
            bool linked = known && previous.Linked;
            if (localTeleported || (known && them.DistanceTo(previous.Position) > Length) ||
                dist - Length > MaxSaneViolation) linked = false;
            if (dist <= Length) linked = true;
            _links[i] = (them, linked);
            if (!linked || dist <= Length || dist <= 0.0001f) continue;

            correction += delta3.Normalized() * ((dist - Length) * ShareOfCorrection);
        }

        if (correction.LengthSquared() > 0.0000001f)
        {
            // MoveAndCollide, never `GlobalPosition +=`: a bare assignment walks the player
            // through walls, which is the same mistake VrPlayer's room-scale movement made.
            body.MoveAndCollide(correction);
        }

        _lastLocalPosition = body.GlobalPosition;
        RebuildRopePoints(body.GlobalPosition, count);
        RedrawRope();
    }

    private void RebuildRopePoints(Vector3 localPos, int count)
    {
        _points.Clear();
        for (int i = 1; i < count; i++)
            if (_links.TryGetValue(i, out var link) && link.Linked)
            { _points.Add(localPos); _points.Add(link.Position); }
    }

    /// Draw each active link as a sagging line. The sag is cosmetic — the
    /// constraint above is a straight-line distance — but a dead-straight rope reads as a laser
    /// rather than a rope.
    private void RedrawRope()
    {
        if (_ropeMesh == null) return;
        _ropeMesh.ClearSurfaces();
        if (_points.Count < 2) return;

        const int SegmentsPerSpan = 8;
        _ropeMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        for (int i = 0; i < _points.Count - 1; i += 2)
        {
            Vector3 a = _points[i], b = _points[i + 1];
            float span = a.DistanceTo(b);
            // Slack is what is left of the rope's length after the span is accounted for.
            float slack = Mathf.Max(0f, Length - span);
            for (int s = 0; s <= SegmentsPerSpan; s++)
            {
                float t = (float)s / SegmentsPerSpan;
                Vector3 p = a.Lerp(b, t);
                p.Y -= Mathf.Sin(t * Mathf.Pi) * slack * 0.35f;
                if (s > 0) _ropeMesh.SurfaceAddVertex(ToLocal(p));
                if (s < SegmentsPerSpan) _ropeMesh.SurfaceAddVertex(ToLocal(p));
            }
        }
        _ropeMesh.SurfaceEnd();
    }
}
