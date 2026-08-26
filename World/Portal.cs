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
    private ShaderMaterial _portalShader;

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

    /// Load a world thumbnail image onto the portal surface so it displays like a framed picture.
    public void SetWorldThumbnail(byte[] pngOrWebpBytes)
    {
        if (_surface == null) return;
        var img = new Image();
        Error err = img.LoadPngFromBuffer(pngOrWebpBytes);
        if (err != Error.Ok)
        {
            err = img.LoadWebpFromBuffer(pngOrWebpBytes);
            if (err != Error.Ok) return;
        }
        var tex = ImageTexture.CreateFromImage(img);
        if (_portalShader != null)
        {
            // Switch from shader to textured material when we have a real image.
            var mat = new StandardMaterial3D
            {
                AlbedoTexture = tex,
                AlbedoColor = Colors.White,
                Emission = new Color(0.49f, 0.23f, 0.93f) * 0.3f,
                EmissionEnergyMultiplier = 0.5f,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                NoDepthTest = true,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            };
            _surface.MaterialOverride = mat;
        }
    }

    private void Build(string label, Color tint)
    {
        // Portal surface: a flat, double-sided plane with an animated swirling vortex shader.
        // Using Orientation.Z so the plane faces the player without rotation.
        var plane = new PlaneMesh
        {
            Size = new Vector2(PortalWidth, PortalHeight),
            Orientation = PlaneMesh.OrientationEnum.Z,
        };
        _surface = new MeshInstance3D
        {
            Mesh = plane,
            Position = new Vector3(0, PortalHeight * 0.5f, 0),
        };
        var shader = GD.Load<Shader>("res://Shaders/portal.gdshader");
        _portalShader = new ShaderMaterial { Shader = shader };
        _portalShader.SetShaderParameter("portal_color", tint);
        _portalShader.SetShaderParameter("glow_color", tint.Lightened(0.3f));
        _surface.MaterialOverride = _portalShader;
        AddChild(_surface);

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
    }
}
