using System;
using Godot;

namespace SerikaSocial.World;

/// A walk-through portal: a glowing arch with a swirling surface. When the local player's body
/// enters the trigger volume it raises `Entered` (once, with a short re-arm cooldown) so Main can
/// route them — e.g. into the multiplayer Commons. Purely presentational + a trigger; the actual
/// travel is decided by the subscriber.
public partial class Portal : Area3D
{
    public event Action Entered;

    private double _cooldown;
    private MeshInstance3D _surface;
    private double _t;

    public static Portal Create(string label, Color tint, Vector3 position, float yawDeg = 0)
    {
        var p = new Portal
        {
            Name = $"Portal_{label}",
            Position = position,
            RotationDegrees = new Vector3(0, yawDeg, 0),
            Monitoring = true,
        };
        p.Build(label, tint);
        return p;
    }

    private void Build(string label, Color tint)
    {
        // Frame: a torus-ish arch made from a flattened cylinder ring.
        var frame = new MeshInstance3D
        {
            Mesh = new TorusMesh { InnerRadius = 0.95f, OuterRadius = 1.15f },
            Position = new Vector3(0, 1.3f, 0),
            RotationDegrees = new Vector3(90, 0, 0),
        };
        frame.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = tint * 0.6f,
            Emission = tint,
            EmissionEnergyMultiplier = 1.4f,
            Metallic = 0.3f,
            Roughness = 0.4f,
        };
        AddChild(frame);

        // Swirling surface — an emissive translucent disc.
        _surface = new MeshInstance3D
        {
            Mesh = new CylinderMesh { Height = 0.04f, TopRadius = 0.95f, BottomRadius = 0.95f },
            Position = new Vector3(0, 1.3f, 0),
            RotationDegrees = new Vector3(90, 0, 0),
        };
        _surface.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(tint.R, tint.G, tint.B, 0.55f),
            Emission = tint * 1.2f,
            EmissionEnergyMultiplier = 2.0f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        };
        AddChild(_surface);

        AddChild(new OmniLight3D
        {
            Position = new Vector3(0, 1.3f, 0),
            LightColor = tint,
            LightEnergy = 0.8f,
            OmniRange = 4f,
        });

        AddChild(new Label3D
        {
            Text = label,
            Position = new Vector3(0, 2.9f, 0),
            Billboard = BaseMaterial3D.BillboardModeEnum.Disabled,
            FontSize = 64,
            PixelSize = 0.006f,
            Modulate = tint.Lightened(0.4f),
        });

        // Trigger volume filling the arch.
        var col = new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(1.8f, 2.4f, 1.0f) },
            Position = new Vector3(0, 1.2f, 0),
        };
        AddChild(col);

        BodyEntered += OnBodyEntered;
    }

    private void OnBodyEntered(Node3D body)
    {
        // Only the local player triggers travel (remote avatars are Node3D, not CharacterBody3D).
        if (body is not CharacterBody3D) return;
        if (_cooldown > 0) return;
        _cooldown = 2.0;
        Entered?.Invoke();
    }

    public override void _Process(double delta)
    {
        if (_cooldown > 0) _cooldown -= delta;
        // Gentle spin on the surface for life.
        _t += delta;
        _surface.RotationDegrees = new Vector3(90, 0, (float)(_t * 40.0));
    }
}
