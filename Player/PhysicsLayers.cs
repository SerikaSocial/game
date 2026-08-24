namespace SerikaSocial.Player;

/// Collision layer assignments, as bit masks.
///
/// Everything used to sit on the default layer 1, which meant players passed through each
/// other (nothing gave a remote avatar a body at all) and the third-person camera had no way
/// to ask "world geometry only" when deciding where to pull in to. Split so those two
/// questions have different answers.
public static class PhysicsLayers
{
    /// Static world geometry — the layer Godot gives everything by default, so worlds loaded
    /// from a `.serikaworld` land here without the loader having to set anything.
    public const uint World = 1 << 0;

    /// The local player's character body.
    public const uint LocalPlayer = 1 << 1;

    /// Other players' bodies, so you can bump into them.
    public const uint RemotePlayer = 1 << 2;

    /// Interaction trigger volumes (seats, lay spots, interaction points). Query-only — no
    /// body collides with these, they're found by group walk, not by physics.
    public const uint Interactable = 1 << 3;

    /// What the local player's body collides against.
    public const uint LocalPlayerMask = World | RemotePlayer;

    /// What the third-person camera pulls in from. World only, deliberately: including other
    /// players makes the camera lurch every time someone walks behind you.
    public const uint CameraMask = World;
}
