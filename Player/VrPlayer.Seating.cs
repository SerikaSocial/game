using Godot;
using SerikaSocial.Avatar;
using SerikaSocial.World;

namespace SerikaSocial.Player;

/// Chair occupancy offsets the XR origin once; the runtime continues to own tracked poses.
public partial class VrPlayer
{
    private IOccupiable _occupying;
    private Transform3D _standingReturn;
    private Vector3 _standingOriginOffset;
    private float _standingBodyYaw;
    public IOccupiable Occupying => _occupying;

    public bool TryOccupy(IOccupiable spot)
    {
        if (spot == null || spot.Pose != AvatarInstance.Emote.Sit || _origin == null) return false;
        if (ReferenceEquals(_occupying, spot)) return true;
        if (_occupying != null) StandUp();
        _standingReturn = GlobalTransform;
        _standingOriginOffset = _origin.Position;
        _standingBodyYaw = _bodyYaw;
        _occupying = spot;
        Velocity = Vector3.Zero;
        _teleportPad.Visible = false;
        _teleportArc.Visible = false;

        // Match desktop's marker direction (-Z). Cancel the current tracked yaw only once,
        // so turning your head afterwards remains a real head turn relative to the chair.
        GlobalRotation = new Vector3(0, spot.AnchorYaw - _camera.Rotation.Y, 0);
        float hipH = (_avatar?.HipHeight ?? .9f) * .5f;
        GlobalPosition = spot.AnchorPosition - Vector3.Up * hipH;
        _bodyYaw = spot.AnchorYaw + Mathf.Pi; // body-facing angle is atan2(x,z)
        float torso = Mathf.Max(.35f, (_avatar?.EyeHeight ?? 1.65f) - (_avatar?.HipHeight ?? .9f));
        _origin.Position = new Vector3(-_camera.Position.X,
            hipH + torso - _camera.Position.Y, -_camera.Position.Z);
        _avatar?.PlayEmote(spot.Pose);
        ResetHeadTracking();
        _avatar?.ResetPhysics();
        return true;
    }

    public void StandUp() => ReleaseSeat(returnToApproach: true);

    private void ReleaseSeat(bool returnToApproach)
    {
        if (_occupying == null) return;
        var spot = _occupying;
        _occupying = null;
        if (spot is not GodotObject obj || GodotObject.IsInstanceValid(obj)) spot.Vacate();
        _avatar?.PlayEmote(AvatarInstance.Emote.None);
        if (returnToApproach) GlobalTransform = _standingReturn;
        _bodyYaw = returnToApproach ? _standingBodyYaw : GlobalRotation.Y + Mathf.Pi;
        // Stand at the known-clear approach point, not an arbitrary offset inside the sofa.
        // Re-centre XZ on the current tracked pose; height returns to the standing calibration.
        _origin.Position = _standingOriginOffset with { X = -_camera.Position.X, Z = -_camera.Position.Z };
        ApplyHeightOffset();
        Velocity = Vector3.Zero;
        ResetHeadTracking();
        _avatar?.ResetPhysics();
    }

    /// True means the seated frame was handled and standing locomotion must not run.
    private bool UpdateSeat(float dt)
    {
        if (_occupying == null) return false;
        if (_occupying is Node seat && (!GodotObject.IsInstanceValid(seat) || seat.IsQueuedForDeletion()))
        {
            ReleaseSeat(returnToApproach: false);
            return false;
        }
        var moveHand = MoveHand;
        var stick = ControlsEnabled ? StickOf(moveHand, moveHand == _leftHand) : Vector2.Zero;
        var headOffset = _origin.Position + _camera.Position;
        if (stick.LengthSquared() > .09f ||
            new Vector2(headOffset.X, headOffset.Z).Length() > .7f)
        {
            StandUp();
            return false;
        }
        Velocity = Vector3.Zero;
        if (_avatar != null && _avatar.CurrentEmote != AvatarInstance.Emote.Sit)
            _avatar.PlayEmote(AvatarInstance.Emote.Sit);
        UpdateFbtTrackers();
        UpdateHands(dt);
        UpdateHeadVelocity(dt);
        if (ControlsEnabled) HandleGrab(dt);
        HandleButtons(dt);
        UpdatePointer();
        UpdateAvatar(dt, 0);
        UpdateHoldHaptics(dt);
        UpdateVignette(dt, 0);
        return true;
    }

    public override void _ExitTree()
    {
        // A world swap may destroy the rig before the old seat; make it available immediately.
        ReleaseSeat(returnToApproach: false);
    }
}
