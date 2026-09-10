using Godot;

namespace Serika.Script;

/// Where a script may parent one of its own nodes. A fixed enum, deliberately: accepting a bone
/// *name* from a script would be an arbitrary-lookup channel by another route, and arbitrary
/// lookup is the thing the whole host API is built to deny.
public enum ScriptAttachPoint
{
    Hips = 0,
    Head = 1,
    LeftHand = 2,
    RightHand = 3,
    Chest = 4,
}

/// The player roster as a script is allowed to see it: a count, read-only positions, and an
/// attachment target. Nothing here can move a player.
///
/// This exists so ScriptHostBridge never touches `Main` — the bridge is the sandbox wall, and a
/// wall that holds a reference to the whole client is not much of a wall. It also makes the
/// bridge testable without the engine's networking.
///
/// Indices are stable within a frame and ordered local-first, then remote peers by ascending peer
/// id. Stability matters: a script that attaches a rope to "player 2" must get the same person on
/// the next tick.
public interface IScriptPlayers
{
    int Count { get; }

    /// World-space position of a player. False for an out-of-range index — never an exception,
    /// because an out-of-range index is a malformed script, and the sandbox answers those with a
    /// benign value rather than unwinding into the frame loop.
    bool TryGetPosition(int index, out Vector3 position);

    /// The node a prop should be parented to in order to ride the given bone. Null when the index
    /// is out of range or the player has no avatar loaded yet (a peer mid-download, for example).
    Node3D GetAttachTarget(int index, ScriptAttachPoint point);

    /// Which player a physics body belongs to, or -1. Used to turn a zone's body_entered into a
    /// player index for on_enter_zone.
    int IndexOfBody(Node body);
}
