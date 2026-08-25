using Godot;
using SerikaSocial.Player;

namespace SerikaSocial.World;

/// Which input produced an interaction. Interactables mostly ignore this, but anything that has to
/// place itself relative to the player — a held item, a grab — needs to know whether there is a
/// real hand in the world or just an eye and an aim direction.
public enum InteractSource
{
    Desktop,
    Touch,
    VrLeft,
    VrRight,
}

/// Everything an `IInteractable` is told about the act of interacting with it.
///
/// `Interact` used to take a `LocalPlayer` outright, which is why VR could never drive the
/// interaction pipeline: `VrPlayer` is a different type, and the desktop rig's `Occupy`/`StandUp`/
/// `EyePosition` members do not exist on it. Widening `IPlayer` instead would have forced `VrPlayer`
/// to grow seating semantics before anyone decided what sitting means in room-scale VR, so the
/// player stays weakly typed here and callers that genuinely need the desktop rig ask for it.
public readonly struct InteractionContext
{
    public readonly InteractSource Source;

    /// Always set. Enough for anything that just needs to know *who*.
    public readonly IPlayer Player;

    /// The desktop rig, or null in VR. Only seating currently needs this.
    public readonly LocalPlayer Desktop;

    /// Where the interaction came from — the eye on desktop/touch, the hand in VR.
    public readonly Vector3 Origin;
    public readonly Vector3 Aim;

    /// Where a held object should end up. Equals `Origin` offset along `Aim` on desktop/touch,
    /// and the actual tracked hand position in VR.
    public readonly Vector3 HandPosition;

    /// True only when a tracked hand produced this. Lets a prop choose between "snap into the hand"
    /// and "float in front of the camera" without sniffing the source enum.
    public readonly bool HasHand;

    public InteractionContext(
        InteractSource source, IPlayer player, LocalPlayer desktop,
        Vector3 origin, Vector3 aim, Vector3 handPosition, bool hasHand)
    {
        Source = source;
        Player = player;
        Desktop = desktop;
        Origin = origin;
        Aim = aim;
        HandPosition = handPosition;
        HasHand = hasHand;
    }

    public bool IsVr => Source is InteractSource.VrLeft or InteractSource.VrRight;
}
