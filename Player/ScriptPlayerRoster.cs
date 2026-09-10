using System;
using System.Collections.Generic;
using Godot;
using Serika.Script;
using SerikaSocial.Avatar;

namespace SerikaSocial.Player;

/// Presents the local player and the remote peers to world scripts as a flat, stably-ordered
/// roster. This is the only thing a script ever learns about who else is here.
///
/// Ordering is local-first, then remote peers by ascending peer id. Stability is the whole point:
/// a script that ties a rope to "player 2" on one tick must get the same person on the next, so
/// the order cannot be dictionary enumeration order (which Godot/.NET do not guarantee and which
/// changes as peers come and go).
public sealed class ScriptPlayerRoster : IScriptPlayers
{
    private readonly Func<IPlayer> _local;
    private readonly IReadOnlyDictionary<uint, RemoteAvatar> _remotes;

    private readonly List<uint> _order = new();
    private bool _dirty = true;

    /// Cached BoneAttachment3D per (player, attach point), so repeated attaches do not grow a
    /// skeleton a node per call.
    private readonly Dictionary<(ulong Skeleton, ScriptAttachPoint Point), BoneAttachment3D> _mounts = new();

    public ScriptPlayerRoster(Func<IPlayer> local, IReadOnlyDictionary<uint, RemoteAvatar> remotes)
    {
        _local = local;
        _remotes = remotes;
    }

    /// Call when a peer joins or leaves. Cheaper and more correct than re-sorting every frame:
    /// the count alone cannot detect a simultaneous join+leave.
    public void Invalidate() => _dirty = true;

    private void Rebuild()
    {
        if (!_dirty && _order.Count == _remotes.Count) return;
        _order.Clear();
        foreach (var id in _remotes.Keys) _order.Add(id);
        _order.Sort();
        _dirty = false;
    }

    public int Count
    {
        get
        {
            Rebuild();
            return (_local() != null ? 1 : 0) + _order.Count;
        }
    }

    /// Index 0 is the local player when there is one; remotes follow.
    private bool Resolve(int index, out IPlayer local, out RemoteAvatar remote)
    {
        local = null;
        remote = null;
        Rebuild();

        var me = _local();
        if (index < 0) return false;

        if (me != null)
        {
            if (index == 0) { local = me; return true; }
            index -= 1;
        }

        if (index >= _order.Count) return false;
        return _remotes.TryGetValue(_order[index], out remote) && GodotObject.IsInstanceValid(remote);
    }

    public bool TryGetPosition(int index, out Vector3 position)
    {
        position = Vector3.Zero;
        if (!Resolve(index, out var local, out var remote)) return false;

        if (local != null) { position = local.PoseTransform().Origin; return true; }
        position = remote.GlobalPosition;
        return true;
    }

    public Node3D GetAttachTarget(int index, ScriptAttachPoint point)
    {
        if (!Resolve(index, out var local, out var remote)) return null;

        var avatar = local != null ? local.Avatar : remote.Avatar;
        // A peer whose avatar is still downloading has no skeleton yet. Null is the right answer:
        // attach() returns 0 and the script can try again next tick.
        if (avatar?.Skeleton is not { } skeleton) return null;

        int bone = avatar.BoneOf(BoneRole(point));
        if (bone < 0) return null;

        var key = (skeleton.GetInstanceId(), point);
        if (_mounts.TryGetValue(key, out var existing) && GodotObject.IsInstanceValid(existing) && existing.IsInsideTree())
            return existing;

        var mount = new BoneAttachment3D
        {
            Name = $"ScriptMount_{point}",
            BoneName = skeleton.GetBoneName(bone),
        };
        skeleton.AddChild(mount);
        _mounts[key] = mount;
        return mount;
    }

    /// Fixed enum -> humanoid role. Roles, not raw bone names: every rig names its bones
    /// differently and AvatarInstance.BoneOf is what already knows the mapping.
    private static string BoneRole(ScriptAttachPoint point) => point switch
    {
        ScriptAttachPoint.Head => "head",
        ScriptAttachPoint.LeftHand => "leftHand",
        ScriptAttachPoint.RightHand => "rightHand",
        ScriptAttachPoint.Chest => "chest",
        _ => "hips",
    };

    public int IndexOfBody(Node body)
    {
        if (body == null) return -1;
        Rebuild();

        var me = _local();
        if (me is Node meNode && (body == meNode || IsAncestorOf(meNode, body))) return 0;

        int offset = me != null ? 1 : 0;
        for (int i = 0; i < _order.Count; i++)
        {
            if (!_remotes.TryGetValue(_order[i], out var r) || !GodotObject.IsInstanceValid(r)) continue;
            if (body == r || IsAncestorOf(r, body)) return offset + i;
        }
        return -1;
    }

    /// A zone reports whichever body overlapped, which for a player is often a collision child
    /// rather than the player node itself.
    private static bool IsAncestorOf(Node ancestor, Node node)
    {
        for (var p = node.GetParent(); p != null; p = p.GetParent())
            if (p == ancestor) return true;
        return false;
    }
}
