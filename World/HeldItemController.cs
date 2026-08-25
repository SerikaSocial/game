using System;
using Godot;

namespace SerikaSocial.World;

/// Drives "hold a usable item and pull the trigger / click / tap to use it".
///
/// One controller per interaction slot: one for desktop (mouse), two for VR (one per hand).
/// Each controller polls a <c>getHeldUsable</c> delegate to discover what it's carrying and a
/// <c>getUseDown</c> delegate to read the use button, then translates the raw input into
/// <see cref="IUsable"/> calls. Position updates for <see cref="IHoldable"/> items are also
/// handled here so the item snaps to the hand at its declared <c>HoldOffset</c> rather than
/// floating at the grab point.
public partial class HeldItemController : Node
{
    private readonly IInteractRig _rig;
    private readonly Func<IUsable> _getHeldUsable;
    private readonly Func<bool> _getUseDown;
    private readonly Func<Transform3D> _getHoldTransform;

    private IUsable _current;
    private bool _using;

    private HeldItemController(IInteractRig rig, Func<IUsable> getHeldUsable,
        Func<bool> getUseDown, Func<Transform3D> getHoldTransform)
    {
        _rig = rig;
        _getHeldUsable = getHeldUsable;
        _getUseDown = getUseDown;
        _getHoldTransform = getHoldTransform;
    }

    /// <param name="rig">Where the hand/eye is — used to build UseContext.</param>
    /// <param name="getHeldUsable">Returns the IUsable currently held in this slot, or null.</param>
    /// <param name="getUseDown">Returns true while the use button is held (mouse/trigger/touch).</param>
    /// <param name="getHoldTransform">The world transform of the hand (for HoldOffset application).</param>
    public static HeldItemController Create(
        IInteractRig rig, Func<IUsable> getHeldUsable, Func<bool> getUseDown,
        Func<Transform3D> getHoldTransform, string name = "HeldItemController")
    {
        var c = new HeldItemController(rig, getHeldUsable, getUseDown, getHoldTransform)
        {
            Name = name,
        };
        return c;
    }

    public override void _Process(double delta)
    {
        if (_rig == null || !_rig.Valid) { StopUsing(); return; }

        var usable = _getHeldUsable?.Invoke();
        if (usable != _current)
        {
            StopUsing();
            _current = usable;
        }

        if (_current == null) return;

        // Position the held item at the hand * HoldOffset.
        if (_current is IHoldable holdable && _current is Node3D node)
        {
            var holdXform = _getHoldTransform?.Invoke() ?? Transform3D.Identity;
            node.GlobalTransform = holdXform * holdable.HoldOffset;
        }

        // Use input — edge-triggered Begin/End, continuous Tick.
        bool down = _getUseDown?.Invoke() ?? false;
        var ctx = BuildUseContext();
        if (down && !_using)
        {
            _using = true;
            _current.UseBegin(in ctx);
        }
        else if (down && _using)
        {
            _current.UseTick(in ctx, (float)delta);
        }
        else if (!down && _using)
        {
            _using = false;
            _current.UseEnd(in ctx);
        }
    }

    private UseContext BuildUseContext()
    {
        var holdXform = _getHoldTransform?.Invoke() ?? Transform3D.Identity;
        var tip = holdXform.Origin;
        var fwd = -holdXform.Basis.Z;
        return new UseContext(_rig.Source, tip, fwd, null);
    }

    private void StopUsing()
    {
        if (_using && _current != null)
        {
            _using = false;
            var ctx = BuildUseContext();
            _current.UseEnd(in ctx);
        }
    }

    public override void _ExitTree() => StopUsing();
}
