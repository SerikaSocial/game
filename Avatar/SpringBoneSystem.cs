using System;
using System.Collections.Generic;
using Godot;

namespace SerikaSocial.Avatar;

/// Runtime spring-bone physics for avatars. Applies a simplified Verlet integration to bone
/// chains marked as PhysBones in the .ska metadata. Each PhysBone root starts a chain that
/// propagates forces down its children: gravity, stiffness (spring back to rest pose), damping,
/// and collision with sphere/capsule colliders.
///
/// This is not a full VRC PhysBone 3.0 replica — it covers the common case: hair, tail, skirt,
/// and accessory jiggle. Grabbing/posing and stretch are stubbed for future implementation.
public sealed partial class SpringBoneSystem : Node
{
    private Skeleton3D _skeleton;
    private readonly List<SpringChain> _chains = new();
    private readonly List<SphereCollider> _colliders = new();

    public void Setup(Skeleton3D skeleton, IReadOnlyList<PhysBoneMeta> physBones,
        IReadOnlyList<PhysBoneColliderMeta> colliderMetas)
    {
        _skeleton = skeleton;
        _chains.Clear();
        _colliders.Clear();

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

        foreach (var chain in _chains)
        {
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

                // Verlet integration
                var velocity = (p.CurrentPos - p.PrevPos) * (1f - chain.Damping);
                p.PrevPos = p.CurrentPos;
                p.CurrentPos += velocity;

                // Gravity
                p.CurrentPos += new Vector3(0, -chain.Gravity * chain.Force * dt, 0);

                // Stiffness: spring back toward rest position
                var restWorld = parentWorld + (_skeleton.GlobalTransform.Basis * p.RestLocalPos);
                p.CurrentPos = p.CurrentPos.Lerp(restWorld, chain.Stiffness * dt);

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
                    var currentPose = _skeleton.GetBonePose(p.BoneIdx);
                    _skeleton.SetBonePosePosition(p.BoneIdx, relative);
                }
            }
        }
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
}
