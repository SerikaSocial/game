using Godot;
using SerikaSocial.Player;

namespace SerikaSocial.World;

/// A networked physics prop that any player can pick up, move, and throw.
///
/// Uses Godot's RigidBody3D for local physics simulation. When a player grabs the prop,
/// it switches to kinematic mode (controlled by the player's hand) and broadcasts its
/// transform via ObjectSync. Other clients interpolate toward the received position.
/// Ownership is implicit: whoever last sent an ObjectSync for this prop's id owns it.
///
/// When not held by anyone, the prop simulates locally with full RigidBody3D physics.
/// Only the owner sends ObjectSync updates — non-owners just interpolate.
[GlobalClass]
public partial class PhysicsProp : RigidBody3D, IInteractable
{
    [Export] public string PropName { get; set; } = "Pick up";
    [Export] public float InteractionRange { get; set; } = 2.0f;
    [Export] public bool ReturnOnRelease { get; set; } = false;
    [Export] public float ReturnDelay { get; set; } = 5.0f;

    /// Stable network id (0-65535). Must be unique within the world.
    [Export] public ushort NetId { get; set; } = 0;

    /// If true, this prop syncs over the network. Single-player worlds can leave this off.
    [Export] public bool Networked { get; set; } = true;

    /// Group every prop joins, so `Main` can register a whole world's props for network sync in
    /// one sweep after the world builds.
    ///
    /// Registration used to be a manual call at each spawn site, and only the marker-pen site
    /// ever made it: the six throwable boxes in the Items Lab were built, added to the tree and
    /// never registered, so each client simulated them independently and no two players saw a
    /// box in the same place. A group sweep cannot be forgotten by the next world builder.
    public const string Group = "serika_physics_prop";

    public string PromptText => _held ? "Drop" : PropName;
    public float Range => InteractionRange;
    public Vector3 FocusPoint => GlobalPosition + new Vector3(0, 0.3f, 0);
    public bool CanInteract => !_held || _heldByLocal;

    protected bool _held;
    protected bool _heldByLocal;
    private uint _holderPeerId;
    private float _returnTimer;
    private Vector3 _restPosition;
    private Quaternion _restRotation;

    // Network interpolation state (for non-owners)
    private Vector3 _targetPos;
    private Quaternion _targetRot;
    private Vector3 _targetVel;
    private bool _hasTarget;
    private double _syncTimer;

    // The static body we swap to when held (so we can move it manually)
    private bool _wasSleeping;

    public override void _Ready()
    {
        AddToGroup(Interactable.Group);
        AddToGroup(Group);
        _restPosition = GlobalPosition;
        _restRotation = Quaternion.FromEuler(GlobalRotation);
        _targetPos = GlobalPosition;
        _targetRot = _restRotation;

        // Ensure we have a collision shape
        if (GetChildCount() == 0 || FindChild("CollisionShape3D") == null)
        {
            // Auto-add a small box collider if none exists
            var col = new CollisionShape3D
            {
                Name = "AutoCollider",
                Shape = new BoxShape3D { Size = new Vector3(0.3f, 0.3f, 0.3f) },
            };
            AddChild(col);
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;

        if (_held && _heldByLocal)
        {
            // Local player is holding: broadcast our transform
            _syncTimer += delta;
            if (_syncTimer >= 0.05) // 20 Hz sync rate
            {
                _syncTimer = 0;
                SendSync();
            }
        }
        else if (!_held && ReturnOnRelease)
        {
            // Return-to-rest logic
            _returnTimer += dt;
            if (_returnTimer >= ReturnDelay)
            {
                LinearVelocity = Vector3.Zero;
                AngularVelocity = Vector3.Zero;
                GlobalPosition = GlobalPosition.Lerp(_restPosition, dt * 3f);
                var curRot = Quaternion.FromEuler(GlobalRotation);
                var newRot = curRot.Slerp(_restRotation, dt * 3f);
                GlobalRotation = newRot.GetEuler();
                if (GlobalPosition.DistanceTo(_restPosition) < 0.05f)
                {
                    GlobalPosition = _restPosition;
                    GlobalRotation = _restRotation.GetEuler();
                }
            }
        }

        // Non-owner interpolation: smoothly move toward the networked target
        if (_hasTarget && !_heldByLocal)
        {
            if (_held)
            {
                // Someone else is holding — snap more aggressively
                GlobalPosition = GlobalPosition.Lerp(_targetPos, dt * 15f);
                var curRot = Quaternion.FromEuler(GlobalRotation);
                var newRot = curRot.Slerp(_targetRot, dt * 15f);
                GlobalRotation = newRot.GetEuler();
            }
            else
            {
                // Free physics — apply velocity from network
                LinearVelocity = LinearVelocity.Lerp(_targetVel, dt * 5f);
            }
        }
    }

    public virtual void Interact(in InteractionContext ctx)
    {
        if (_held && !_heldByLocal)
            return; // someone else has it

        if (_held)
        {
            Release();
            return;
        }

        // Both platforms end up at the same place: a world position to hold the prop at. In VR
        // that is the tracked hand; on desktop `InteractionContext` derives it from eye + aim.
        // `GrabAt` rejects an already-held prop, so a trigger-interact and a grip-grab landing in
        // the same frame cannot both take it.
        GrabAt(ctx.HandPosition);
    }

    /// Grab straight to a world position — the single entry point for taking hold of a prop,
    /// whether that came from a VR grip, a VR trigger, a desktop E, or a touch button.
    /// Returns false when someone (including another hand) is already holding this prop.
    public bool GrabAt(Vector3 handPos)
    {
        if (_held) return false;
        _held = true;
        _heldByLocal = true;
        _holderPeerId = 0;
        _returnTimer = 0;
        Freeze = true;
        _wasSleeping = Sleeping;
        GlobalPosition = handPos;
        return true;
    }

    /// True while we are the ones holding this prop — lets a VR hand confirm it still owns it.
    public bool HeldByLocal => _heldByLocal;

    /// Called by LocalPlayer each frame while holding to update the prop position.
    public void UpdateHeldPosition(Vector3 handPos)
    {
        if (!_heldByLocal) return;
        GlobalPosition = handPos;
    }

    protected void Release()
    {
        _held = false;
        _heldByLocal = false;
        Freeze = false;
        _returnTimer = 0;
    }

    /// Apply a throw impulse when releasing with a velocity.
    public void ReleaseWithVelocity(Vector3 velocity)
    {
        Release();
        LinearVelocity = velocity;
    }

    private void SendSync()
    {
        // The transport is accessed via the static WorldNetwork singleton or a delegate
        // set by Main.cs. This decouples PhysicsProp from the transport directly.
        if (WorldNetwork.Send != null && Networked)
        {
            var pos = GlobalPosition;
            var rot = Quaternion.FromEuler(GlobalRotation);
            WorldNetwork.Send(NetId, pos.X, pos.Y, pos.Z, rot.X, rot.Y, rot.Z, rot.W, 0, 0, 0);
        }
    }

    /// Called by Main.cs when an ObjectSync is received from another peer.
    public void ApplyNetworkSync(float x, float y, float z, float qx, float qy, float qz, float qw, float lvx, float lvy, float lvz)
    {
        if (_heldByLocal) return; // ignore our own updates echoed back

        _targetPos = new Vector3(x, y, z);
        _targetRot = new Quaternion(qx, qy, qz, qw);
        _targetVel = new Vector3(lvx, lvy, lvz);
        _hasTarget = true;

        // If someone else is now holding, mark as held
        if (!_held)
        {
            _held = true;
            _heldByLocal = false;
            Freeze = true;
        }
    }

    /// Called by Main.cs when a PhysGrab release is received from another peer.
    public void ApplyNetworkRelease(float lvx, float lvy, float lvz)
    {
        _held = false;
        _heldByLocal = false;
        Freeze = false;
        LinearVelocity = new Vector3(lvx, lvy, lvz);
        _returnTimer = 0;
    }
}

/// Static bridge for PhysicsProp to send network updates without a direct transport reference.
/// Main.cs sets the Send delegate on connect.
public static class WorldNetwork
{
    /// (objId, x, y, z, qx, qy, qz, qw, lvx, lvy, lvz)
    public static System.Action<ushort, float, float, float, float, float, float, float, float, float, float>? Send;
}
