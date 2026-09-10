using System;
using System.Collections.Generic;
using Godot;

namespace SerikaSocial.World;

/// Generic minigame building blocks resolved from world markers. These are deliberately NOT tied
/// to any game mode or the `/v1/games` API: every one of them is usable by an ordinary creator's
/// world, paired with SerikaScript hooks. That is the line between "a platform feature a world
/// can opt into" and "a bespoke game backend" — these markers are the former.
///
///   SERIKA_CHECK<n>   a checkpoint: entering it moves the local respawn point here, and the
///                     CheckpointMonitor returns a fallen player to the last one reached
///   SERIKA_WARP<n>    a teleport pad; pads sharing a group (n % 10) cycle. Walk-on, works for
///                     desktop, touch and VR through the same physics body
///
/// Buttons (`SERIKA_BUTTON<n>`) are resolved as plain `InteractionPoint`s by WorldLoader — they
/// already carry the prompt/orb/IInteractable plumbing — and the loader binds their signal to
/// `ScriptWorld.InteractFromBody` so a press reaches the script's `on_interact` hook.

/// A checkpoint pad. Entering one with your own body raises `Reached`: the loader forwards that
/// both to the monitor (so fall recovery returns to where YOU last stood) and to the shared
/// checkpoint event (so the respawn point follows you). Remote players' bodies are ignored —
/// their own machines do the same bookkeeping for them.
public partial class CheckpointNode : Area3D
{
    public int Index { get; private set; }

    /// Where a body that fell past the monitor's plane is put back to — slightly above the pad
    /// so gravity settles the avatar onto it rather than interpenetrating it.
    public Vector3 RespawnPoint { get; private set; }

    /// (pad) — the local player walked onto this checkpoint.
    public event Action<CheckpointNode> Reached;

    public static CheckpointNode Create(int index, Vector3 respawnPoint, Vector3 extents)
    {
        var pad = new CheckpointNode
        {
            Name = $"Checkpoint{index}",
            Index = index,
            RespawnPoint = respawnPoint,
            Monitoring = true,
            Monitorable = false,
        };
        pad.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D
            {
                // Marker scale is the trigger's full extents (floored so a default-scaled marker
                // still catches a player), same contract as SERIKA_ZONE.
                Size = new Vector3(Mathf.Max(0.5f, extents.X), Mathf.Max(1.2f, extents.Y),
                                   Mathf.Max(0.5f, extents.Z)),
            },
        });
        return pad;
    }

    public override void _Ready()
    {
        BodyEntered += body =>
        {
            if (body == WorldLoader.LocalBody?.Invoke()) Reached?.Invoke(this);
        };
    }
}

/// Watches the local body against the checkpoint set and puts it back when it falls off the
/// course. The recovery plane sits 6 m under the last checkpoint the player reached, so the
/// AUTHOR decides the behaviour per map: a plinth anywhere above that plane catches a fall
/// (climb towers), and a gap floored below it recovers to the last checkpoint (race courses).
/// Without checkpoints this node never exists and the engine's own void respawn at −50 m is the
/// only net.
public partial class CheckpointMonitor : Node
{
    private const float PlaneMargin = 6f;
    private readonly Func<CharacterBody3D> _localBody;
    private CheckpointNode _last;
    private double _cooldown;

    public CheckpointMonitor(CheckpointNode start, Func<CharacterBody3D> localBody)
    {
        Name = "CheckpointMonitor";
        _localBody = localBody;
        // "Last reached" falls back to the lowest pad, which for a fresh spawn is wherever the
        // author put checkpoint 0 — normally the start platform under the player's feet.
        _last = start;
    }

    public void NotifyReached(CheckpointNode pad) => _last = pad;

    public override void _Process(double delta)
    {
        if (_cooldown > 0) _cooldown -= delta;
        var body = _localBody?.Invoke();
        if (body == null || _last == null) return;

        // Landing surfaces at or above the plane are safe by authoring — only a fall THROUGH it
        // recovers. The deep-void guard is belt and braces: the engine's own −50 m respawn would
        // also catch it, but by then the player has watched themselves fall for four seconds.
        float plane = _last.RespawnPoint.Y - PlaneMargin;
        if (body.GlobalPosition.Y >= plane) return;
        if (body.GlobalPosition.Y < _last.RespawnPoint.Y - 40f) return;
        if (_cooldown > 0) return;
        _cooldown = 1.0;

        body.GlobalPosition = _last.RespawnPoint + new Vector3(0, 0.6f, 0);
        body.Velocity = Vector3.Zero;
        WorldLoader.RaiseCheckpointRecovered();
    }
}

/// A teleport pad. Pads share a group by marker index modulo 10 (`SERIKA_WARP3` and
/// `SERIKA_WARP13` are one group of two; a group of more cycles). Triggered by walking on, so
/// the same physics path serves desktop, touch and VR without any per-input wiring.
public partial class WarpPad : Area3D
{
    /// Shared per pad group. The recovery window is group-wide because the common accident —
    /// arriving on top of the twin pad — must not ping-pong.
    public sealed class Group
    {
        public List<WarpPad> Pads = new();
        public double ReadyAtUnixSeconds;
    }

    public Group GroupRef { get; set; }
    public int SlotInGroup { get; set; }

    public static WarpPad Create(int group, int slot, Group groupRef)
    {
        var pad = new WarpPad
        {
            Name = $"WarpPad{group}_{slot}",
            GroupRef = groupRef,
            SlotInGroup = slot,
            Monitoring = true,
            Monitorable = false,
        };
        pad.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1.8f, 1.2f, 1.8f) } });

        // A faint ring so a player can tell a pad is a pad. Deliberately subtle: authoring places
        // real geometry under it; this is just the affordance.
        var ring = new MeshInstance3D
        {
            Mesh = new TorusMesh { InnerRadius = 0.55f, OuterRadius = 0.7f },
            Position = new Vector3(0, 0.08f, 0),
        };
        ring.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.45f, 0.32f, 0.75f),
            EmissionEnabled = true,
            Emission = new Color(0.45f, 0.32f, 0.75f),
            EmissionEnergyMultiplier = 1.4f,
        };
        pad.AddChild(ring);
        return pad;
    }

    public override void _Ready()
    {
        BodyEntered += OnBodyEntered;
    }

    private void OnBodyEntered(Node3D body)
    {
        if (body != WorldLoader.LocalBody?.Invoke()) return;
        var group = GroupRef;
        if (group == null || group.Pads.Count < 2) return;

        double now = Time.GetUnixTimeFromSystem();
        if (now < group.ReadyAtUnixSeconds) return;
        group.ReadyAtUnixSeconds = now + 1.5;

        var dest = group.Pads[(SlotInGroup + 1) % group.Pads.Count];
        body.GlobalPosition = dest.GlobalPosition + new Vector3(0, 1.0f, 0);
        if (body is CharacterBody3D character) character.Velocity = Vector3.Zero;
    }
}
