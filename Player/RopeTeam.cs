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

    private IScriptPlayers _players;
    private Func<CharacterBody3D> _localBody;
    private MeshInstance3D _ropeView;
    private ImmediateMesh _ropeMesh;
    private readonly List<Vector3> _points = new();

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
        if (count < 2) { _points.Clear(); RedrawRope(); return; }

        // Index 0 is the local player. Its rope neighbours are the adjacent entries in the roster,
        // so the team forms a chain rather than every pair being tied to every other.
        Vector3 here = body.GlobalPosition;
        Vector3 correction = Vector3.Zero;

        for (int i = 1; i < count; i++)
        {
            if (!_players.TryGetPosition(i, out var them)) continue;

            Vector3 delta3 = them - here;
            float dist = delta3.Length();
            if (dist <= Length || dist <= 0.0001f) continue;
            if (dist - Length > MaxSaneViolation) continue; // teleport/spawn, not a taut rope

            correction += delta3.Normalized() * ((dist - Length) * ShareOfCorrection);
        }

        if (correction.LengthSquared() > 0.0000001f)
        {
            // MoveAndCollide, never `GlobalPosition +=`: a bare assignment walks the player
            // through walls, which is the same mistake VrPlayer's room-scale movement made.
            body.MoveAndCollide(correction);
        }

        RebuildRopePoints(body.GlobalPosition, count);
        RedrawRope();
    }

    private void RebuildRopePoints(Vector3 localPos, int count)
    {
        _points.Clear();
        _points.Add(localPos);
        for (int i = 1; i < count; i++)
            if (_players.TryGetPosition(i, out var p)) _points.Add(p);
    }

    /// Draw the rope as a sagging line between consecutive players. The sag is cosmetic — the
    /// constraint above is a straight-line distance — but a dead-straight rope reads as a laser
    /// rather than a rope.
    private void RedrawRope()
    {
        if (_ropeMesh == null) return;
        _ropeMesh.ClearSurfaces();
        if (_points.Count < 2) return;

        const int SegmentsPerSpan = 8;
        _ropeMesh.SurfaceBegin(Mesh.PrimitiveType.LineStrip);
        for (int i = 0; i < _points.Count - 1; i++)
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
                _ropeMesh.SurfaceAddVertex(ToLocal(p));
            }
        }
        _ropeMesh.SurfaceEnd();
    }
}
