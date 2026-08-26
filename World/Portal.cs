using System;
using System.Collections.Generic;
using Godot;
using SerikaSocial.Player;

namespace SerikaSocial.World;

/// How a portal routes the player when entered.
public enum PortalMode
{
    /// Open the full world browser (default — the Home portal behaviour).
    WorldPicker,
    /// Jump directly to a specific world by ID. No picker UI.
    DirectWorld,
    /// Open the world browser but filtered to a curated list of world IDs.
    CuratedList,
    /// A user-spawned invite portal. Routes to a specific world+instance for the inviter.
    /// World rules govern whether these are allowed to spawn (off by default).
    Invite,
}

/// A walk-through portal: a glowing rectangular plane with world imagery on both sides and a
/// purple glow frame around its edges. When the local player's body enters the trigger volume it
/// raises `Entered` (once, with a short re-arm cooldown) so Main can route them — e.g. into the
/// multiplayer Commons. Purely presentational + a trigger; the actual travel is decided by the
/// subscriber.
public partial class Portal : Area3D
{
    public event Action<Portal> Entered;

    /// Routing mode for this portal.
    public PortalMode Mode { get; set; } = PortalMode.WorldPicker;

    /// Target world ID for DirectWorld and Invite modes.
    public string TargetWorldId { get; set; } = "";

    /// Curated list of world IDs for CuratedList mode.
    public List<string> AllowedWorldIds { get; set; }

    /// Display label shown above the portal.
    public string Label { get; private set; } = "";

    private double _cooldown;
    private MeshInstance3D _surface;
    private MeshInstance3D _frameGlow;
    private double _t;

    private const float PortalWidth = 1.6f;
    private const float PortalHeight = 2.2f;

    public static Portal Create(string label, Color tint, Vector3 position, float yawDeg = 0)
    {
        return Create(label, tint, position, yawDeg, PortalMode.WorldPicker, null, null);
    }

    public static Portal Create(string label, Color tint, Vector3 position, float yawDeg,
        PortalMode mode, string targetWorldId = null, List<string> allowedWorlds = null)
    {
        var p = new Portal
        {
            Name = $"Portal_{label}",
            Position = position,
            RotationDegrees = new Vector3(0, yawDeg, 0),
            Monitoring = true,
            CollisionMask = PhysicsLayers.LocalPlayer,
            Mode = mode,
            TargetWorldId = targetWorldId ?? "",
            AllowedWorldIds = allowedWorlds ?? new List<string>(),
            Label = label,
        };
        p.Build(label, tint);
        return p;
    }

    private void Build(string label, Color tint)
    {
        // Portal surface: a double-sided plane showing a swirling portal effect.
        _surface = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(PortalWidth, PortalHeight) },
            Position = new Vector3(0, PortalHeight * 0.5f, 0),
            RotationDegrees = new Vector3(90, 0, 0),
        };
        var surfMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(tint.R * 0.3f, tint.G * 0.3f, tint.B * 0.5f, 0.35f),
            Emission = tint * 0.8f,
            EmissionEnergyMultiplier = 1.5f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            NoDepthTest = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        _surface.MaterialOverride = surfMat;
        AddChild(_surface);

        // Purple glow frame: a slightly larger plane behind the surface, emissive purple,
        // visible only as a rim around the portal edges.
        _frameGlow = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(PortalWidth + 0.15f, PortalHeight + 0.15f) },
            Position = new Vector3(0, PortalHeight * 0.5f, 0.02f),
            RotationDegrees = new Vector3(90, 0, 0),
        };
        _frameGlow.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(tint.R, tint.G, tint.B, 0.9f),
            Emission = tint * 1.5f,
            EmissionEnergyMultiplier = 2.5f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            NoDepthTest = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            RenderPriority = -1,
        };
        AddChild(_frameGlow);

        // A second glow plane on the back side for symmetry.
        var backGlow = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(PortalWidth + 0.15f, PortalHeight + 0.15f) },
            Position = new Vector3(0, PortalHeight * 0.5f, -0.02f),
            RotationDegrees = new Vector3(90, 180, 0),
        };
        backGlow.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(tint.R, tint.G, tint.B, 0.9f),
            Emission = tint * 1.5f,
            EmissionEnergyMultiplier = 2.5f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            NoDepthTest = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            RenderPriority = -1,
        };
        AddChild(backGlow);

        AddChild(new OmniLight3D
        {
            Position = new Vector3(0, PortalHeight * 0.5f, 0),
            LightColor = tint,
            LightEnergy = 1.2f,
            OmniRange = 5f,
        });

        AddChild(new Label3D
        {
            Text = label,
            Position = new Vector3(0, PortalHeight + 0.3f, 0),
            Billboard = BaseMaterial3D.BillboardModeEnum.Disabled,
            FontSize = 64,
            PixelSize = 0.006f,
            Modulate = tint.Lightened(0.4f),
        });

        // Trigger volume filling the portal.
        var col = new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(PortalWidth + 0.3f, PortalHeight + 0.2f, 0.8f) },
            Position = new Vector3(0, PortalHeight * 0.5f, 0),
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
        Entered?.Invoke(this);
    }

    public override void _Process(double delta)
    {
        if (_cooldown > 0) _cooldown -= delta;
        // Subtle pulsing on the glow frame for life.
        _t += delta;
        float pulse = 0.85f + Mathf.Sin((float)_t * 2.0f) * 0.15f;
        if (_frameGlow.MaterialOverride is StandardMaterial3D mat)
            mat.EmissionEnergyMultiplier = 2.0f * pulse;
    }
}
