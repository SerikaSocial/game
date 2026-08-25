using Godot;

namespace SerikaSocial.World;

/// A prop that is *carried* rather than merely shoved around — it attaches to the hand at a known
/// offset instead of being held wherever it happened to be grabbed.
public interface IHoldable
{
    void OnEquip(in InteractionContext ctx);
    void OnUnequip();

    /// Where the item sits relative to the hand (or the desktop hold point) while carried.
    Transform3D HoldOffset { get; }
}

/// A held item that *does* something when you pull the trigger / click / press the touch button.
///
/// Split from `IHoldable` on purpose: plenty of things are worth carrying without being usable,
/// and a few things are usable without being carried.
public interface IUsable
{
    /// Verb for the on-screen affordance, e.g. "Draw".
    string UseVerb { get; }

    /// True for things that act continuously while held down (a pen), false for one-shot presses.
    bool ContinuousUse { get; }

    void UseBegin(in UseContext ctx);
    void UseTick(in UseContext ctx, float dt);
    void UseEnd(in UseContext ctx);
}

/// Where the business end of a held item currently is.
///
/// The item resolves its own tip rather than the controller doing it, because only the item knows
/// where its tip is — a pen writes from its nib, not from the middle of the player's fist.
public readonly struct UseContext
{
    public readonly InteractSource Source;
    public readonly Vector3 TipPosition;
    public readonly Vector3 TipForward;
    public readonly Node3D Holder;

    public UseContext(InteractSource source, Vector3 tipPosition, Vector3 tipForward, Node3D holder)
    {
        Source = source;
        TipPosition = tipPosition;
        TipForward = tipForward;
        Holder = holder;
    }
}
