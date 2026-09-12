using System;
using Godot;
using SerikaSocial.Player;

namespace Serika.Script;

/// A trigger volume built from a `SERIKA_ZONE<n>` marker. Turns physics-body overlap into the
/// on_enter_zone / on_exit_zone hooks.
///
/// Zone ids come from the marker name and are the world author's numbering — the script refers to
/// them as plain integers, so nothing here hands a script a node, a path or a name.
public partial class ScriptZone : Area3D
{
    public int ZoneId { get; private set; }

    /// (zoneId, body) — the world runtime maps the body to a player index. This node deliberately
    /// does not know what a player is.
    public event Action<int, Node3D> Entered;
    public event Action<int, Node3D> Exited;

    public static ScriptZone Create(int zoneId, Vector3 extents)
    {
        var zone = new ScriptZone
        {
            ZoneId = zoneId,
            Name = $"ScriptZone{zoneId}",
            CollisionLayer = 0,
            CollisionMask = PhysicsLayers.LocalPlayer | PhysicsLayers.RemotePlayer,
            Monitoring = true,
            // Zones observe; they never push. Monitorable off keeps them out of other queries.
            Monitorable = false,
        };
        var shape = new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = extents },
        };
        zone.AddChild(shape);
        return zone;
    }

    public override void _Ready()
    {
        BodyEntered += OnBodyEntered;
        BodyExited += OnBodyExited;
    }

    private void OnBodyEntered(Node3D body) => Entered?.Invoke(ZoneId, body);
    private void OnBodyExited(Node3D body) => Exited?.Invoke(ZoneId, body);
}
