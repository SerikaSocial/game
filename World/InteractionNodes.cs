using Godot;
using SerikaSocial.Player;

namespace SerikaSocial.World;

/// A seat the player can sit on by pressing E (or the interaction button) when near.
/// Teleports the player to the seat position and plays a sit animation. Press E again to stand.
[GlobalClass]
public partial class SeatNode : Area3D
{
    [Export] public string SeatLabel { get; set; } = "Seat";
    [Export] public Vector3 SitOffset { get; set; } = new(0, 0.45f, 0);
    [Export] public float SitYaw { get; set; } = 0f;

    private bool _occupied;

    public override void _Ready()
    {
        AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(0.8f, 0.8f, 0.8f) },
        });

        // Visual marker — a subtle cushion-colored box.
        var marker = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.5f, 0.1f, 0.5f) },
            Position = new Vector3(0, 0.25f, 0),
        };
        marker.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.4f, 0.3f, 0.5f, 0.6f),
            Roughness = 0.9f,
        };
        AddChild(marker);
    }

    public bool TryOccupy(LocalPlayer player)
    {
        if (_occupied) return false;
        _occupied = true;
        return true;
    }

    public void Vacate() => _occupied = false;

    public Vector3 SitPosition => GlobalPosition + SitOffset;
    public float SitRotation => SitYaw;
}

/// A block the player can lie down on (lay/bed). Similar to SeatNode but lying pose.
[GlobalClass]
public partial class LayNode : Area3D
{
    [Export] public string LayLabel { get; set; } = "Lay";
    [Export] public Vector3 LayOffset { get; set; } = new(0, 0.3f, 0);
    [Export] public float LayYaw { get; set; } = 0f;

    private bool _occupied;

    public override void _Ready()
    {
        AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(1.0f, 0.6f, 2.0f) },
        });

        var marker = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.9f, 0.15f, 1.8f) },
            Position = new Vector3(0, 0.15f, 0),
        };
        marker.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.3f, 0.35f, 0.5f, 0.6f),
            Roughness = 0.9f,
        };
        AddChild(marker);
    }

    public bool TryOccupy(LocalPlayer player)
    {
        if (_occupied) return false;
        _occupied = true;
        return true;
    }

    public void Vacate() => _occupied = false;

    public Vector3 LayPosition => GlobalPosition + LayOffset;
    public float LayRotation => LayYaw;
}

/// A generic interaction point. When the player is near and presses E, fires the Interacted signal.
/// World builders connect this to whatever logic they want (open a door, play a sound, toggle a light, etc.)
[GlobalClass]
public partial class InteractionPoint : Area3D
{
    [Export] public string Prompt { get; set; } = "Interact";
    [Export] public float InteractionRange { get; set; } = 2.0f;

    [Signal] public delegate void InteractedEventHandler(Node3D interactor);

    public override void _Ready()
    {
        AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(1f, 1.5f, 1f) },
        });

        // Visual marker — a small glowing sphere.
        var marker = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.08f, Height = 0.16f },
            Position = new Vector3(0, 1.0f, 0),
        };
        marker.MaterialOverride = new StandardMaterial3D
        {
            Emission = new Color(0.6f, 0.4f, 0.9f),
            EmissionEnergyMultiplier = 2f,
            AlbedoColor = new Color(0.6f, 0.4f, 0.9f),
        };
        AddChild(marker);
    }

    public void Trigger(Node3D interactor)
    {
        EmitSignal(SignalName.Interacted, interactor);
    }
}
