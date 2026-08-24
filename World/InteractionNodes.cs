using Godot;
using SerikaSocial.Player;

namespace SerikaSocial.World;

/// Anything the player can walk up to and press E on.
///
/// Implemented by the marker nodes `WorldLoader.ResolveMarkers` drops into a world (seats, lay
/// spots, generic interaction points). `Interactor` finds the best candidate each tick by
/// walking the `Interactable.Group` scene group, so worlds get interaction for free just by
/// exporting the marker — no per-world wiring.
public interface IInteractable
{
    /// Verb shown in the prompt, e.g. "Sit". The key hint is added by the prompt widget.
    string PromptText { get; }

    /// How close the player must be, in metres, measured to `FocusPoint`.
    float Range { get; }

    /// The point proximity is measured to and the prompt is anchored above.
    Vector3 FocusPoint { get; }

    /// False when the thing is busy (an occupied seat), so it stops offering a prompt.
    bool CanInteract { get; }

    /// Do the thing. Called with the player that pressed E.
    void Interact(LocalPlayer player);
}

/// Something the player is currently *in* — a seat or a bed. Pressing E again releases it.
public interface IOccupiable : IInteractable
{
    /// Where the avatar is planted while occupying, and which way it faces (radians).
    Vector3 AnchorPosition { get; }
    float AnchorYaw { get; }

    /// Pose held while occupying.
    Avatar.AvatarInstance.Emote Pose { get; }

    void Vacate();
}

public static class Interactable
{
    /// Scene group every interactable joins on ready. Group membership is how `Interactor`
    /// enumerates candidates without the world builder having to register anything.
    public const string Group = "serika_interactable";
}

/// A seat the player can sit on by pressing E when near. Plants the avatar at the seat and
/// holds a sitting pose; E again stands back up.
[GlobalClass]
public partial class SeatNode : Area3D, IOccupiable
{
    [Export] public string SeatLabel { get; set; } = "Sit";
    [Export] public Vector3 SitOffset { get; set; } = new(0, 0.45f, 0);
    [Export] public float SitYaw { get; set; } = 0f;
    [Export] public float InteractionRange { get; set; } = 1.6f;

    /// Seats are placed on top of a chair mesh the world already provides, so the translucent
    /// cushion box is debug scaffolding, not decoration — off unless a world asks for it.
    [Export] public bool ShowDebugMarker { get; set; } = false;

    private bool _occupied;

    public override void _Ready()
    {
        AddToGroup(Interactable.Group);
        AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(0.8f, 0.8f, 0.8f) },
        });

        if (!ShowDebugMarker) return;
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

    public string PromptText => SeatLabel;
    public float Range => InteractionRange;
    public Vector3 FocusPoint => GlobalPosition;
    public bool CanInteract => !_occupied;

    public Vector3 AnchorPosition => GlobalPosition + SitOffset;
    public float AnchorYaw => SitYaw;
    public Avatar.AvatarInstance.Emote Pose => Avatar.AvatarInstance.Emote.Sit;

    public void Interact(LocalPlayer player)
    {
        if (_occupied) return;
        _occupied = true;
        player.Occupy(this);
    }

    public void Vacate() => _occupied = false;
}

/// A surface the player can lie down on (a bed, a couch). Same contract as `SeatNode` with a
/// lying pose and a longer footprint.
[GlobalClass]
public partial class LayNode : Area3D, IOccupiable
{
    [Export] public string LayLabel { get; set; } = "Lie down";
    [Export] public Vector3 LayOffset { get; set; } = new(0, 0.3f, 0);
    [Export] public float LayYaw { get; set; } = 0f;
    [Export] public float InteractionRange { get; set; } = 2.0f;
    [Export] public bool ShowDebugMarker { get; set; } = false;

    private bool _occupied;

    public override void _Ready()
    {
        AddToGroup(Interactable.Group);
        AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(1.0f, 0.6f, 2.0f) },
        });

        if (!ShowDebugMarker) return;
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

    public string PromptText => LayLabel;
    public float Range => InteractionRange;
    public Vector3 FocusPoint => GlobalPosition;
    public bool CanInteract => !_occupied;

    public Vector3 AnchorPosition => GlobalPosition + LayOffset;
    public float AnchorYaw => LayYaw;
    public Avatar.AvatarInstance.Emote Pose => Avatar.AvatarInstance.Emote.Sleeping;

    public void Interact(LocalPlayer player)
    {
        if (_occupied) return;
        _occupied = true;
        player.Occupy(this);
    }

    public void Vacate() => _occupied = false;
}

/// A generic interaction point. When the player is near and presses E, fires `Interacted`.
/// World builders connect this to whatever they want — open a door, play a sound, cue a video.
[GlobalClass]
public partial class InteractionPoint : Area3D, IInteractable
{
    [Export] public string Prompt { get; set; } = "Interact";
    [Export] public float InteractionRange { get; set; } = 2.0f;

    /// Set false to grey the point out (a door that's locked, a screen with no queue).
    [Export] public bool Enabled { get; set; } = true;

    /// The small glowing orb that advertises the point. Worlds whose own mesh already reads as
    /// interactive can turn it off.
    [Export] public bool ShowOrb { get; set; } = true;

    [Signal] public delegate void InteractedEventHandler(Node3D interactor);

    public override void _Ready()
    {
        AddToGroup(Interactable.Group);
        AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(1f, 1.5f, 1f) },
        });

        if (!ShowOrb) return;
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

    public string PromptText => Prompt;
    public float Range => InteractionRange;
    public Vector3 FocusPoint => GlobalPosition + new Vector3(0, 1.0f, 0);
    public bool CanInteract => Enabled;

    public void Interact(LocalPlayer player) => Trigger(player);

    public void Trigger(Node3D interactor) => EmitSignal(SignalName.Interacted, interactor);
}
