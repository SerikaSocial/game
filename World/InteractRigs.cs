using Godot;
using SerikaSocial.Player;

namespace SerikaSocial.World;

/// What `Interactor` needs from whatever is doing the interacting.
///
/// `Interactor` used to hold a `LocalPlayer` directly, which is the reason it was created only in
/// the desktop branch of `SpawnLocalPlayer` and why VR and mobile had no way to use a seat. Behind
/// this interface the scan logic is identical for all three; only the origin, the aim, and how
/// picky the facing test should be differ.
public interface IInteractRig
{
    /// Where to measure range from — the eye, or a tracked hand.
    Vector3 Origin { get; }
    Vector3 Aim { get; }

    /// False when the rig can't interact right now (an untracked VR controller). The prompt is
    /// hidden rather than left showing a stale target.
    bool Active { get; }

    /// How far behind `Aim` a candidate may sit before it is ignored. Meaningful when aiming with
    /// the head; meaningless when the "aim" is a hand you are already touching something with.
    float MinFacingDot { get; }

    /// Multiplier on each candidate's declared `Range`. A hand should have to actually reach a
    /// thing, whereas a 2 m eye-range is what makes desktop interaction feel responsive.
    float RangeScale { get; }

    /// Non-null while seated/lying. Only the desktop rig can currently occupy anything.
    IOccupiable Occupying { get; }
    void StandUp();

    InteractionContext MakeContext();

    /// True once the underlying player node has been freed, so `Interactor` can stop.
    bool Valid { get; }
}

/// Desktop and mobile. Both drive `LocalPlayer`; only the source tag differs, which matters for
/// the on-screen prompt and for which "use" input a held item listens to.
public sealed class DesktopInteractRig : IInteractRig
{
    private readonly LocalPlayer _player;
    private readonly InteractSource _source;

    /// How far in front of the eye a grabbed object is held. Matches the offset `PhysicsProp`
    /// previously computed inline when it attached to the player.
    private const float HoldDistance = 0.6f;
    private const float HoldDrop = 0.3f;

    public DesktopInteractRig(LocalPlayer player, InteractSource source = InteractSource.Desktop)
    {
        _player = player;
        _source = source;
    }

    public Vector3 Origin => _player.EyePosition;
    public Vector3 Aim => _player.AimForward;
    public bool Active => true;

    // Unchanged from the original constant: interactables more than this far behind where you are
    // looking are ignored, so standing between two chairs picks the one you face.
    public float MinFacingDot => -0.3f;
    public float RangeScale => 1f;

    public IOccupiable Occupying => _player.Occupying;
    public void StandUp() => _player.StandUp();
    public bool Valid => GodotObject.IsInstanceValid(_player);

    public InteractionContext MakeContext()
    {
        var eye = _player.EyePosition;
        var aim = _player.AimForward;
        var hand = eye + aim * HoldDistance - new Vector3(0, HoldDrop, 0);
        return new InteractionContext(_source, _player, _player, eye, aim, hand, hasHand: false);
    }
}

/// One VR controller. Two of these exist, one per hand.
///
/// Deliberately additive: grip-to-grab in `VrPlayer.UpdateHand` is untouched and remains the way
/// physics props are picked up, because it already feels right. This exists for everything grip
/// cannot do — seats, lay spots, interaction points, doors — which VR simply had no access to.
public sealed class VrHandInteractRig : IInteractRig
{
    private readonly VrPlayer _player;
    private readonly XRController3D _hand;
    private readonly InteractSource _source;

    public VrHandInteractRig(VrPlayer player, XRController3D hand, bool isLeft)
    {
        _player = player;
        _hand = hand;
        _source = isLeft ? InteractSource.VrLeft : InteractSource.VrRight;
    }

    public Vector3 Origin => _hand.GlobalPosition;
    public Vector3 Aim => -_hand.GlobalTransform.Basis.Z;

    /// An untracked controller reports a stale pose, so the ray would point somewhere arbitrary.
    public bool Active => GodotObject.IsInstanceValid(_hand) && _hand.GetHasTrackingData();

    /// A hand does not have a meaningful "facing": if it is close enough to a thing, you meant it.
    /// Keeping the desktop value here would reject candidates you are practically touching.
    public float MinFacingDot => -1f;

    /// Hand-scale reach. A seat declaring 1.6 m is sized for eye-distance interaction; at arm's
    /// length that would let you sit on a chair across the room by waving at it.
    public float RangeScale => 0.5f;

    public IOccupiable Occupying => null;
    public void StandUp() { }
    public bool Valid => GodotObject.IsInstanceValid(_player) && GodotObject.IsInstanceValid(_hand);

    public InteractionContext MakeContext()
    {
        var pos = _hand.GlobalPosition;
        return new InteractionContext(
            _source, _player, desktop: null,
            origin: pos, aim: -_hand.GlobalTransform.Basis.Z,
            handPosition: pos, hasHand: true);
    }
}
