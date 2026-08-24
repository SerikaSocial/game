using System;
using System.Collections.Generic;
using Godot;

namespace SerikaSocial.Avatar;

/// Runtime spring-bone physics for avatars. Applies a simplified Verlet integration to bone
/// chains marked as PhysBones in the .ska metadata. Each PhysBone root starts a chain that
/// propagates forces down its children: gravity, stiffness (spring back to rest pose), damping,
/// and collision with sphere/capsule colliders.
///
/// Grabbing: PhysBones marked isGrabbable can be grabbed by the local player (click-drag) or
/// by remote players (PhysGrab network messages). When grabbed, the chain's root particle is
/// pinned to the grab position and the rest of the chain follows naturally via Verlet.
public sealed partial class SpringBoneSystem : Node
{
    private Skeleton3D _skeleton;
    private readonly List<SpringChain> _chains = new();
    private readonly List<SphereCollider> _colliders = new();

    // Grab state: maps chain index → grab offset
    private readonly Dictionary<int, GrabState> _grabs = new();

    public void Setup(Skeleton3D skeleton, IReadOnlyList<PhysBoneMeta> physBones,
        IReadOnlyList<PhysBoneColliderMeta> colliderMetas)
    {
        _skeleton = skeleton;
        _chains.Clear();
        _colliders.Clear();
        _grabs.Clear();

        if (_skeleton == null || physBones == null) return;

        // Build colliders
        if (colliderMetas != null)
        {
            foreach (var c in colliderMetas)
            {
                int boneIdx = -1;
                if (!string.IsNullOrEmpty(c.RootTransform))
                    boneIdx = _skeleton.FindBone(c.RootTransform);
                if (boneIdx < 0) continue;

                _colliders.Add(new SphereCollider
                {
                    BoneIdx = boneIdx,
                    Radius = Mathf.Max(c.Radius, 0.001f),
                });
            }
        }

        // Build spring chains from PhysBone metadata
        foreach (var pb in physBones)
        {
            int rootIdx = -1;
            if (!string.IsNullOrEmpty(pb.RootTransform))
                rootIdx = _skeleton.FindBone(pb.RootTransform);
            if (rootIdx < 0) continue;

            var chain = new SpringChain
            {
                Name = pb.Name,
                Stiffness = pb.Stiffness,
                Gravity = pb.Gravity,
                Force = pb.Force,
                Pull = pb.Pull,
                Spring = pb.Spring,
                Damping = Mathf.Clamp(pb.Damping, 0f, 1f),
                MaxStretch = Mathf.Max(pb.MaxStretch, 0f),
                IsGrabbable = pb.IsGrabbable,
                IsPosable = pb.IsPosable,
            };

            // Collect all bones in the chain (root + descendants)
            CollectChainBones(rootIdx, chain);

            // Initialize particle states
            foreach (var bone in chain.Bones)
            {
                var restPos = _skeleton.GetBoneRest(bone).Origin;
                chain.Particles.Add(new SpringParticle
                {
                    BoneIdx = bone,
                    RestLocalPos = restPos,
                    CurrentPos = restPos,
                    PrevPos = restPos,
                });
            }

            if (chain.Bones.Count > 0)
                _chains.Add(chain);
        }
    }

    private void CollectChainBones(int rootIdx, SpringChain chain)
    {
        chain.Bones.Add(rootIdx);
        for (int i = 0; i < _skeleton.GetBoneCount(); i++)
        {
            if (_skeleton.GetBoneParent(i) == rootIdx)
                CollectChainBonesRecursive(i, chain);
        }
    }

    private void CollectChainBonesRecursive(int idx, SpringChain chain)
    {
        chain.Bones.Add(idx);
        for (int i = 0; i < _skeleton.GetBoneCount(); i++)
        {
            if (_skeleton.GetBoneParent(i) == idx)
                CollectChainBonesRecursive(i, chain);
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_skeleton == null || _chains.Count == 0) return;
        float dt = (float)delta;

        for (int ci = 0; ci < _chains.Count; ci++)
        {
            var chain = _chains[ci];
            bool isGrabbed = _grabs.ContainsKey(ci);

            for (int i = 0; i < chain.Particles.Count; i++)
            {
                var p = chain.Particles[i];

                // Get world position of this bone
                var boneGlobal = _skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(p.BoneIdx);
                var worldPos = boneGlobal.Origin;

                // Parent bone world position (for constraint)
                int parentIdx = _skeleton.GetBoneParent(p.BoneIdx);
                Vector3 parentWorld = parentIdx >= 0
                    ? (_skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(parentIdx)).Origin
                    : _skeleton.GlobalPosition;

                // If this particle is grabbed, pin it to the grab position
                if (isGrabbed && i == 0)
                {
                    var grab = _grabs[ci];
                    p.PrevPos = p.CurrentPos;
                    p.CurrentPos = grab.WorldPosition;
                }
                else
                {
                    // Verlet integration
                    var velocity = (p.CurrentPos - p.PrevPos) * (1f - chain.Damping);
                    p.PrevPos = p.CurrentPos;
                    p.CurrentPos += velocity;

                    // Gravity
                    p.CurrentPos += new Vector3(0, -chain.Gravity * chain.Force * dt, 0);

                    // Stiffness: spring back toward rest position (reduced when grabbed)
                    var restWorld = parentWorld + (_skeleton.GlobalTransform.Basis * p.RestLocalPos);
                    float stiff = isGrabbed ? chain.Stiffness * 0.1f : chain.Stiffness;
                    p.CurrentPos = p.CurrentPos.Lerp(restWorld, stiff * dt);
                }

                // Collisions
                foreach (var col in _colliders)
                {
                    var colGlobal = _skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(col.BoneIdx);
                    var colPos = colGlobal.Origin;
                    float dist = p.CurrentPos.DistanceTo(colPos);
                    if (dist < col.Radius)
                    {
                        var normal = (p.CurrentPos - colPos).Normalized();
                        p.CurrentPos = colPos + normal * col.Radius;
                    }
                }

                // Distance constraint: maintain bone length
                if (i > 0)
                {
                    var prevP = chain.Particles[i - 1];
                    var diff = p.CurrentPos - prevP.CurrentPos;
                    float restLen = p.RestLocalPos.Length();
                    if (restLen > 0f && diff.Length() > restLen * (1f + chain.MaxStretch))
                    {
                        p.CurrentPos = prevP.CurrentPos + diff.Normalized() * restLen * (1f + chain.MaxStretch);
                    }
                }

                // Write back: convert world position to local and set bone pose
                var localPos = _skeleton.GlobalTransform.AffineInverse() * p.CurrentPos;
                // Get the parent's global pose to compute relative position
                if (parentIdx >= 0)
                {
                    var parentGlobal = _skeleton.GetBoneGlobalPose(parentIdx);
                    var relative = parentGlobal.AffineInverse() * localPos;
                    _skeleton.SetBonePosePosition(p.BoneIdx, relative);
                }
            }
        }
    }

    // ── Grab API ──────────────────────────────────────────────────────────────────────

    /// Start grabbing a grabbable PhysBone chain by world-space position.
    /// Returns the chain index if a grabbable chain was hit, -1 otherwise.
    public int StartGrab(Vector3 worldPos, float maxDist = 0.15f)
    {
        for (int ci = 0; ci < _chains.Count; ci++)
        {
            if (!_chains[ci].IsGrabbable) continue;
            if (_grabs.ContainsKey(ci)) continue; // already grabbed

            // Check if any particle in this chain is close enough to the grab point
            for (int i = 0; i < _chains[ci].Particles.Count; i++)
            {
                var p = _chains[ci].Particles[i];
                if (p.CurrentPos.DistanceTo(worldPos) <= maxDist)
                {
                    _grabs[ci] = new GrabState { WorldPosition = worldPos, IsLocal = true };
                    return ci;
                }
            }
        }
        return -1;
    }

    /// Update the grab position for a chain being held by the local player.
    public void UpdateGrab(int chainIdx, Vector3 worldPos)
    {
        if (_grabs.TryGetValue(chainIdx, out var grab) && grab.IsLocal)
        {
            grab.WorldPosition = worldPos;
            _grabs[chainIdx] = grab;
        }
    }

    /// Release a local grab.
    public void ReleaseGrab(int chainIdx)
    {
        if (_grabs.TryGetValue(chainIdx, out var grab) && grab.IsLocal)
            _grabs.Remove(chainIdx);
    }

    /// Apply a remote grab from another player (via PhysGrab network message).
    public void ApplyRemoteGrab(int chainIdx, Vector3 worldPos)
    {
        if (chainIdx < 0 || chainIdx >= _chains.Count) return;
        if (!_chains[chainIdx].IsGrabbable) return;
        _grabs[chainIdx] = new GrabState { WorldPosition = worldPos, IsLocal = false };
    }

    /// Release a remote grab.
    public void ReleaseRemoteGrab(int chainIdx)
    {
        if (_grabs.TryGetValue(chainIdx, out var grab) && !grab.IsLocal)
            _grabs.Remove(chainIdx);
    }

    /// Get all grabbable chain names and their indices, for network discovery.
    public IReadOnlyList<(int Index, string Name)> GetGrabbableChains()
    {
        var result = new List<(int, string)>();
        for (int i = 0; i < _chains.Count; i++)
        {
            if (_chains[i].IsGrabbable)
                result.Add((i, _chains[i].Name ?? $"chain_{i}"));
        }
        return result;
    }

    /// Find a chain index by bone name (for mapping remote grab bone ids to local chains).
    public int FindChainByRootBoneName(string boneName)
    {
        for (int i = 0; i < _chains.Count; i++)
        {
            if (_chains[i].Bones.Count > 0)
            {
                var boneName2 = _skeleton.GetBoneName(_chains[i].Bones[0]);
                if (boneName2 == boneName) return i;
            }
        }
        return -1;
    }

    private sealed class SpringChain
    {
        public string Name;
        public float Stiffness;
        public float Gravity;
        public float Force;
        public float Pull;
        public float Spring;
        public float Damping;
        public float MaxStretch;
        public bool IsGrabbable;
        public bool IsPosable;
        public readonly List<int> Bones = new();
        public readonly List<SpringParticle> Particles = new();
    }

    private sealed class SpringParticle
    {
        public int BoneIdx;
        public Vector3 RestLocalPos;
        public Vector3 CurrentPos;
        public Vector3 PrevPos;
    }

    private sealed class SphereCollider
    {
        public int BoneIdx;
        public float Radius;
    }

    private struct GrabState
    {
        public Vector3 WorldPosition;
        public bool IsLocal;
    }
}
