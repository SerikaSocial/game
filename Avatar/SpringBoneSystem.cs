using System;
using System.Collections.Generic;
using Godot;

namespace SerikaSocial.Avatar;

/// Secondary-motion solver for avatars: hair, skirts, ears, tails, ribbons, sleeves, capes and
/// breasts. Drives every bone chain flagged as a PhysBone, whether that flag came from the
/// avatar's own VRM spring rig (<see cref="VrmSpringImport"/>), from `.ska` metadata, or from
/// auto-detection.
///
/// Model — deliberately the VRM/UniVRM formulation, because it is what every avatar in the wild
/// was authored and hand-tuned against, and reproducing it is the difference between "the
/// author's numbers work" and "every avatar needs re-tuning". Each *joint* is a bone that
/// rotates; the thing actually simulated is its **tail**, the world position its child bone's
/// head wants to be at. Each step the tail carries its own inertia, is pulled back toward the
/// direction the rest pose points it, takes gravity, gets pushed out of colliders, and is then
/// snapped back onto the sphere of its fixed bone length. The joint is finally rotated by the
/// swing that takes its rest direction onto the solved tail direction.
///
/// Only rotation is ever written (<see cref="Skeleton3D.SetBonePoseRotation"/>). Bone
/// translation is never touched, so skinned geometry cannot stretch or shear.
///
/// Why this is a rewrite rather than a patch — the previous solver had defects that compounded:
/// particles were seeded with a *bone-local* rest offset but integrated as *world* positions;
/// the rest target was built from the skeleton node's basis instead of the parent bone's, so it
/// pointed the wrong way the moment a hip or head rotated (this is what let a skirt hang through
/// the legs while standing perfectly still); gravity was integrated as `a·dt` instead of `a·dt²`;
/// stiffness arrived as `Lerp(t = stiffness·dt)` ≈ 0.012, a restoring force so weak the result
/// read as frozen; damping and gravity were per-frame so Quest's 45 Hz physics tick and the
/// desktop's 60 Hz behaved like different games; branching chains (a skirt is ten panels under
/// one root) were flattened into a single list; and a hard 25° bend clamp — added to paper over
/// the collision failures — capped total swing regardless of chain length.
///
/// Timestep — the solver runs its own fixed 60 Hz accumulator from `_Process`, *not* the engine
/// physics tick, which `DeviceProfile` drops to 45/50 Hz on Quest. Spring behaviour is therefore
/// identical on desktop and standalone, and independent of frame rate.
public sealed partial class SpringBoneSystem : Node
{
    /// Fixed solver step. Spring constants in the wild are tuned against 60 Hz.
    private const float FixedStep = 1f / 60f;

    /// Cap on catch-up steps per frame, so a hitch or a load stall can't spiral.
    private const int MaxSubSteps = 4;

    /// An anchor jump larger than this in a single frame is a teleport, spawn or avatar swap,
    /// not motion — resolve it by snapping the rig to rest instead of flinging it across the map.
    private const float TeleportThreshold = 0.75f;

    /// Per-step tail motion below which a chain counts as still. A fifth of a millimetre is well
    /// under a pixel at any distance you can see an avatar from.
    private const float SleepThreshold = 0.0002f;

    /// How long a chain must stay still before it sleeps. Long enough that a chain settling
    /// through its last millimetre isn't cut short and left visibly off its rest pose.
    private const float SleepDelay = 0.4f;

    private Skeleton3D _skeleton;
    private readonly List<Chain> _chains = new();
    private readonly List<Collider> _colliders = new();
    private readonly Dictionary<int, GrabState> _grabs = new();

    private float _accumulator;
    private bool _needsReset = true;
    private Camera3D _camera;
    private float _lodTimer;
    private bool _halfRatePhase;

    /// Set false on avatars that should never simulate (a distant crowd filler, a thumbnail).
    public bool Enabled { get; set; } = true;

    /// Total simulated joints, for the perf HUD.
    public int JointCount { get; private set; }

    // Distance tiers. Beyond `_lodFarDistance` a rig is parked at its rest pose entirely.
    private float _lodNearDistance;
    private float _lodFarDistance;

    public void Setup(Skeleton3D skeleton, IReadOnlyList<PhysBoneMeta> physBones,
        IReadOnlyList<PhysBoneColliderMeta> colliderMetas)
    {
        _skeleton = skeleton;
        _chains.Clear();
        _colliders.Clear();
        _grabs.Clear();
        JointCount = 0;
        _needsReset = true;

        // Run late in the frame: the owning player calls AvatarInstance.Animate() from its
        // _PhysicsProcess, and VR hand IK writes on top of that. Secondary motion must observe
        // the final animated pose, so it reads it in _Process with a low priority.
        ProcessPriority = 500;
        SetProcess(true);
        SetPhysicsProcess(false);

        bool mobile = UI.DeviceProfile.IsStandaloneXr;
        _lodNearDistance = mobile ? 4f : 10f;
        _lodFarDistance = mobile ? 12f : 25f;

        if (_skeleton == null || physBones == null) return;

        BuildColliders(colliderMetas);

        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < _colliders.Count; i++) byName[_colliders[i].Name] = i;

        foreach (var pb in physBones)
        {
            if (pb == null || string.IsNullOrEmpty(pb.RootTransform)) continue;
            int rootBone = _skeleton.FindBone(pb.RootTransform);
            if (rootBone < 0) continue;

            var chain = BuildChain(pb, rootBone, byName);
            if (chain != null && chain.Joints.Count > 0)
            {
                _chains.Add(chain);
                JointCount += chain.Joints.Count;
            }
        }

        if (_chains.Count > 0)
            GD.Print($"SpringBoneSystem: {_chains.Count} chains, {JointCount} joints, {_colliders.Count} colliders");
    }

    private void BuildColliders(IReadOnlyList<PhysBoneColliderMeta> metas)
    {
        if (metas == null) return;
        foreach (var c in metas)
        {
            if (c == null || string.IsNullOrEmpty(c.RootTransform)) continue;
            int boneIdx = _skeleton.FindBone(c.RootTransform);
            if (boneIdx < 0) continue;
            if (c.Radius <= 0f) continue;

            _colliders.Add(new Collider
            {
                Name = string.IsNullOrEmpty(c.Name) ? c.RootTransform : c.Name,
                BoneIdx = boneIdx,
                Radius = c.Radius,
                Shape = (ColliderShape)Mathf.Clamp(c.ShapeType, 0, 2),
                LocalOffset = ToVector(c.Offset, Vector3.Zero),
                LocalTail = ToVector(c.Tail, Vector3.Zero),
            });
        }
    }

    /// Flatten the bone subtree under `rootBone` into a depth-first joint array. DFS order
    /// guarantees a joint always appears after its parent, so a single forward pass over the
    /// array can solve the whole tree with each joint reading its parent's already-solved world
    /// rotation. This is what makes branching work: a skirt root with ten panels hanging off it
    /// is one chain of ten independent branches, not ten bones in a line.
    private Chain BuildChain(PhysBoneMeta pb, int rootBone, Dictionary<string, int> colliderByName)
    {
        var chain = new Chain
        {
            Name = string.IsNullOrEmpty(pb.Name) ? _skeleton.GetBoneName(rootBone) : pb.Name,
            RootBone = rootBone,
            AnchorBone = _skeleton.GetBoneParent(rootBone),
            Stiffness = Mathf.Max(pb.Stiffness, 0f),
            Gravity = Mathf.Max(pb.Gravity, 0f),
            GravityDir = ToVector(pb.GravityDir, Vector3.Down).LimitLength(1f),
            Drag = Mathf.Clamp(pb.Damping, 0f, 0.99f),
            Radius = Mathf.Max(pb.Radius, 0f),
            IsGrabbable = pb.IsGrabbable,
            MaxAngleCos = MaxAngleToCos(pb.MaxAngleDegrees),
        };
        if (chain.GravityDir.LengthSquared() < 1e-8f) chain.GravityDir = Vector3.Down;
        else chain.GravityDir = chain.GravityDir.Normalized();

        // Which colliders this chain is allowed to hit. `allowCollision: false` means none —
        // distinct from an empty list, which means "no preference, use all of them". Chest
        // physics relies on that distinction: its bones live inside the torso collider, so
        // letting it collide with the body would shove it straight back out every frame.
        if (!pb.AllowCollision)
        {
            // No colliders.
        }
        else if (pb.Colliders != null && pb.Colliders.Count > 0)
        {
            foreach (var n in pb.Colliders)
                if (n != null && colliderByName.TryGetValue(n, out int ci)) chain.ColliderIdx.Add(ci);
        }
        else
        {
            for (int i = 0; i < _colliders.Count; i++) chain.ColliderIdx.Add(i);
        }

        // Children lookup, built once — Skeleton3D has no child accessor that doesn't allocate.
        var children = new Dictionary<int, List<int>>();
        for (int i = 0; i < _skeleton.GetBoneCount(); i++)
        {
            int p = _skeleton.GetBoneParent(i);
            if (p < 0) continue;
            if (!children.TryGetValue(p, out var list)) children[p] = list = new List<int>();
            list.Add(i);
        }

        AddJoint(chain, rootBone, -1, children);
        return chain;
    }

    private void AddJoint(Chain chain, int bone, int parentJoint, Dictionary<int, List<int>> children)
    {
        children.TryGetValue(bone, out var kids);

        var rest = _skeleton.GetBoneRest(bone);

        // The tail is where this joint's child sits. With several children — a skirt root, a
        // hair root — the average is the honest single direction for the parent to point, and
        // each child still gets its own joint and swings independently.
        Vector3 childLocal;
        if (kids != null && kids.Count > 0)
        {
            childLocal = Vector3.Zero;
            foreach (int k in kids) childLocal += _skeleton.GetBoneRest(k).Origin;
            childLocal /= kids.Count;
        }
        else
        {
            // A leaf has no child to aim at. Continue the direction it arrived from, at 70% of
            // its own length — the same fallback UniVRM uses, and it matters: leaf joints are
            // the tips of hair strands and skirt hems, the parts most visibly in motion.
            float len = rest.Origin.Length();
            childLocal = len > 1e-5f ? rest.Origin.Normalized() * (len * 0.7f) : new Vector3(0, -0.05f, 0);
        }

        float boneLength = childLocal.Length();

        // A zero-length joint cannot define a direction, so it can't be rotated meaningfully.
        // Skip it as a joint but keep walking, or an entire branch below it would be dropped.
        if (boneLength > 1e-5f)
        {
            chain.Joints.Add(new Joint
            {
                BoneIdx = bone,
                ParentJoint = parentJoint,
                RestLocalPos = rest.Origin,
                RestLocalRot = rest.Basis.GetRotationQuaternion().Normalized(),
                BoneAxis = childLocal / boneLength,
                Length = boneLength,
            });
            parentJoint = chain.Joints.Count - 1;
        }

        if (kids == null) return;
        foreach (int k in kids) AddJoint(chain, k, parentJoint, children);
    }

    // ── frame loop ────────────────────────────────────────────────────────────────────────

    public override void _Process(double delta)
    {
        if (!Enabled || _skeleton == null || _chains.Count == 0) return;

        // Refresh the camera reference occasionally rather than every frame — it changes on
        // first/third-person toggles and on entering VR, not per frame.
        _lodTimer -= (float)delta;
        if (_camera == null || !IsInstanceValid(_camera) || _lodTimer <= 0f)
        {
            _camera = GetViewport()?.GetCamera3D();
            _lodTimer = 0.5f;
        }

        var lod = SelectLod();
        if (lod == Lod.Off)
        {
            // Park at rest rather than freezing mid-swing, so an avatar that walks back into
            // range starts from a clean pose instead of popping out of a stale one.
            if (!_needsReset) RestAll();
            _accumulator = 0f;
            return;
        }

        // Bank the elapsed time *before* the half-rate skip. Skipping the accumulation too would
        // throw away half of every second, and a distant avatar's hair would swing at half speed
        // — the LOD is meant to halve how often we solve, not how fast time passes.
        _accumulator = Mathf.Min(_accumulator + (float)delta, FixedStep * MaxSubSteps);

        _halfRatePhase = !_halfRatePhase;
        if (lod == Lod.Half && _halfRatePhase) return;   // solve on alternate frames only

        if (_accumulator < FixedStep) return;

        PrepareFrame(out bool teleported);
        if (teleported)
        {
            // Push the rest pose out the same frame. Deferring it to the next one would leave
            // the rig holding its pre-teleport shape for a frame at the new location.
            ResetToRest();
            WriteBack();
            _accumulator = 0f;
            return;
        }

        int steps = 0;
        while (_accumulator >= FixedStep && steps < MaxSubSteps)
        {
            Step(FixedStep);
            _accumulator -= FixedStep;
            steps++;
        }

        WriteBack();
        SolvedFrames++;
    }

    private enum Lod { Full, Half, Off }

    private Lod SelectLod()
    {
        if (_camera == null) return Lod.Full;   // headless or pre-camera: don't silently disable
        float d = _camera.GlobalPosition.DistanceTo(_skeleton.GlobalPosition);
        if (d <= _lodNearDistance) return Lod.Full;
        if (d <= _lodFarDistance) return Lod.Half;
        return Lod.Off;
    }

    /// Sample everything that comes from the animated skeleton exactly once per frame: the
    /// anchor transform each chain hangs from, and every collider's world placement. The solver
    /// substeps then run as pure arithmetic with no skeleton queries at all — which is what
    /// keeps this affordable on Quest, where `GetBoneGlobalPose` on a 136-bone VRM is not
    /// something to call per joint per collider per step.
    private void PrepareFrame(out bool teleported)
    {
        teleported = false;

        var skelXform = _skeleton.GlobalTransform;
        var skelRot = skelXform.Basis.GetRotationQuaternion().Normalized();

        // Uniform scale factor, so a scaled-down avatar's bone lengths still match its mesh.
        Vector3 scale = skelXform.Basis.Scale;
        float uniformScale = (Mathf.Abs(scale.X) + Mathf.Abs(scale.Y) + Mathf.Abs(scale.Z)) / 3f;
        if (uniformScale < 1e-4f) uniformScale = 1f;

        bool anyAwake = false;

        foreach (var chain in _chains)
        {
            Transform3D anchor = chain.AnchorBone >= 0
                ? skelXform * _skeleton.GetBoneGlobalPose(chain.AnchorBone)
                : skelXform;

            var pos = anchor.Origin;
            var rot = anchor.Basis.GetRotationQuaternion().Normalized();

            if (!_needsReset && pos.DistanceTo(chain.PrevAnchorPos) > TeleportThreshold)
                teleported = true;

            // Anything that moves the bone this chain hangs from has to wake it, including a
            // pure rotation — an avatar turning on the spot swings a skirt without translating
            // the hips at all.
            if (pos.DistanceSquaredTo(chain.PrevAnchorPos) > 1e-10f ||
                Mathf.Abs(rot.Dot(chain.AnchorRot)) < 0.999999f)
                chain.Wake();

            chain.AnchorPos = pos;
            chain.AnchorRot = rot;
            chain.Scale = uniformScale;
            chain.GravityWorld = skelRot * chain.GravityDir;
            chain.PrevAnchorPos = pos;

            if (chain.Awake) anyAwake = true;
        }

        // Collider placement is only worth reading if something is going to test against it.
        // A room of idle avatars is the common case in a social app, and this is what makes it
        // close to free — the whole per-frame cost here is skeleton interop, not arithmetic.
        if (anyAwake || _needsReset)
        {
            for (int i = 0; i < _colliders.Count; i++)
            {
                var col = _colliders[i];
                var world = skelXform * _skeleton.GetBoneGlobalPose(col.BoneIdx);
                col.WorldCenter = world * col.LocalOffset;
                col.WorldTail = col.Shape == ColliderShape.Capsule ? world * col.LocalTail : col.WorldCenter;
                col.WorldRadius = col.Radius * uniformScale;
            }
        }

        if (_needsReset)
        {
            ResetToRest();
            _needsReset = false;
        }
    }

    /// Advance every chain by one fixed step.
    private void Step(float dt)
    {
        for (int ci = 0; ci < _chains.Count; ci++)
        {
            var chain = _chains[ci];
            bool grabbed = _grabs.TryGetValue(ci, out var grab);
            if (grabbed) chain.Wake();
            if (!chain.Awake) continue;

            float maxDelta = 0f;

            // A grabbed chain goes limp so it hangs from the hand rather than fighting back to
            // its rest pose — but not fully limp, or it collapses into the body.
            float stiffness = grabbed ? chain.Stiffness * 0.15f : chain.Stiffness;

            var joints = chain.Joints;
            for (int ji = 0; ji < joints.Count; ji++)
            {
                var j = joints[ji];

                // Where this joint's head is, and how its parent is oriented, both follow from
                // the parent joint solved earlier in this same pass — DFS order guarantees it.
                Quaternion parentRot;
                Vector3 head;
                if (j.ParentJoint < 0)
                {
                    parentRot = chain.AnchorRot;
                    head = chain.AnchorPos + parentRot * (j.RestLocalPos * chain.Scale);
                }
                else
                {
                    var p = joints[j.ParentJoint];
                    parentRot = p.WorldRot;
                    head = p.Head + parentRot * (j.RestLocalPos * chain.Scale);
                }
                j.Head = head;

                float length = j.Length * chain.Scale;

                // The direction the rest pose wants the tail to point, in world space. Note this
                // is built from the *parent bone's* current rotation — the thing the old solver
                // got wrong by using the skeleton node's basis, which is why a skirt drifted into
                // the legs the instant the hips were anything but identity.
                Quaternion restWorldRot = parentRot * j.RestLocalRot;
                Vector3 restDir = (restWorldRot * j.BoneAxis).Normalized();

                Vector3 tail;
                if (grabbed && ji == 0)
                {
                    // Pin the grabbed root straight at the hand; children follow through inertia.
                    tail = head + (grab.WorldPosition - head).LimitLength(length);
                    if (tail.DistanceSquaredTo(head) < 1e-8f) tail = head + restDir * length;
                }
                else
                {
                    Vector3 inertia = (j.Tail - j.PrevTail) * (1f - chain.Drag);

                    // Constant-magnitude pull toward the rest direction, as VRM defines it, so
                    // author-tuned stiffness values mean what they meant in Unity. Clamped to the
                    // bone length so very short joints (hair tips) can't overshoot and buzz.
                    float pull = Mathf.Min(stiffness * dt, length);
                    Vector3 external = chain.GravityWorld * (chain.Gravity * dt);

                    tail = j.Tail + inertia + restDir * pull + external;
                }

                // Bone length is rigid: project back onto the sphere around the head.
                tail = ProjectToLength(head, tail, restDir, length);

                if (chain.ColliderIdx.Count > 0)
                {
                    float hitRadius = chain.Radius * chain.Scale;

                    // Resolve and re-constrain twice. One pass is not enough: pushing the tail
                    // out of a collider moves it off the bone-length sphere, and snapping it back
                    // onto that sphere can drop it straight back inside — which is how a hair
                    // strand ended up 3 cm inside a shoulder during a hard sidestep. A second
                    // pass converges on the point that satisfies both, and two is where the
                    // returns stop; a third changes nothing measurable and costs Quest frames.
                    for (int iter = 0; iter < 2; iter++)
                    {
                        for (int k = 0; k < chain.ColliderIdx.Count; k++)
                            tail = Resolve(_colliders[chain.ColliderIdx[k]], tail, hitRadius);

                        // Ordering matters: resolve first, constrain second, so the joint rotates
                        // *around* the collider rather than being stretched off it.
                        tail = ProjectToLength(head, tail, restDir, length);
                    }
                }

                // Optional cone limit around the rest direction. Off by default: with real
                // colliders present a hard clamp is not needed, and a tight one is exactly what
                // made the old system look like it had no physics at all.
                if (chain.MaxAngleCos > -1f)
                {
                    Vector3 dir = (tail - head) / length;
                    float dot = restDir.Dot(dir);
                    if (dot < chain.MaxAngleCos)
                    {
                        dir = restDir.Slerp(dir, SafeSlerpT(dot, chain.MaxAngleCos)).Normalized();
                        tail = head + dir * length;
                    }
                }

                maxDelta = Mathf.Max(maxDelta, tail.DistanceSquaredTo(j.Tail));
                j.PrevTail = j.Tail;
                j.Tail = tail;

                // Publish this joint's world rotation for its children, still inside the step —
                // children solved later in this pass need the *current* orientation, not last
                // frame's, or a chain lags one joint per link and whips.
                j.WorldRot = SwingTo(restDir, (tail - head) / length) * restWorldRot;
            }

            // A chain that has stopped moving, hanging off a body that has stopped moving, is
            // producing the same bone rotations every frame. Let it sleep — the cost of this
            // solver is dominated by skeleton interop, not arithmetic, so not writing is the
            // saving. It wakes on any anchor motion, so nothing is missed.
            if (maxDelta < SleepThreshold * SleepThreshold)
            {
                chain.StillTime += dt;
                if (chain.StillTime >= SleepDelay) chain.Awake = false;
            }
            else chain.StillTime = 0f;
        }
    }

    /// Push the skeleton once per frame, after all substeps — writing per substep would be
    /// three or four redundant pose invalidations for a pose nothing reads in between.
    private void WriteBack()
    {
        foreach (var chain in _chains)
        {
            if (!chain.Awake) continue;   // its bones already hold the pose it settled into

            var joints = chain.Joints;
            for (int ji = 0; ji < joints.Count; ji++)
            {
                var j = joints[ji];
                Quaternion parentRot = j.ParentJoint < 0 ? chain.AnchorRot : joints[j.ParentJoint].WorldRot;
                var local = (parentRot.Inverse() * j.WorldRot).Normalized();
                _skeleton.SetBonePoseRotation(j.BoneIdx, local);
            }
        }
    }

    /// Snap every tail onto its rest position with zero velocity. Used on spawn, on teleport,
    /// on avatar swap, and when a rig comes back into LOD range.
    public void ResetToRest()
    {
        foreach (var chain in _chains)
        {
            var joints = chain.Joints;
            for (int ji = 0; ji < joints.Count; ji++)
            {
                var j = joints[ji];
                Quaternion parentRot;
                Vector3 head;
                if (j.ParentJoint < 0)
                {
                    parentRot = chain.AnchorRot;
                    head = chain.AnchorPos + parentRot * (j.RestLocalPos * chain.Scale);
                }
                else
                {
                    var p = joints[j.ParentJoint];
                    parentRot = p.WorldRot;
                    head = p.Head + parentRot * (j.RestLocalPos * chain.Scale);
                }

                Quaternion restWorldRot = parentRot * j.RestLocalRot;
                j.Head = head;
                j.WorldRot = restWorldRot;
                j.Tail = head + (restWorldRot * j.BoneAxis).Normalized() * (j.Length * chain.Scale);
                j.PrevTail = j.Tail;
            }
            chain.PrevAnchorPos = chain.AnchorPos;
            // A reset changes every bone rotation, so the new pose has to be written out before
            // the chain is allowed to go quiet again.
            chain.Wake();
        }
        _accumulator = 0f;
    }

    /// Restore the authored rest rotations on the skeleton, releasing the bones entirely.
    private void RestAll()
    {
        foreach (var chain in _chains)
            foreach (var j in chain.Joints)
                _skeleton.SetBonePoseRotation(j.BoneIdx, j.RestLocalRot);
        _needsReset = true;
    }

    /// Call after teleporting or respawning the avatar so the rig doesn't whip.
    public void NotifyTeleport() => _needsReset = true;

    // ── geometry ──────────────────────────────────────────────────────────────────────────

    private static Vector3 ProjectToLength(Vector3 head, Vector3 tail, Vector3 fallbackDir, float length)
    {
        Vector3 d = tail - head;
        float lenSq = d.LengthSquared();
        if (lenSq < 1e-12f) return head + fallbackDir * length;
        return head + d * (length / Mathf.Sqrt(lenSq));
    }

    /// Push a tail point of radius `hitRadius` out of (or, for an inside-sphere, into) a collider.
    private static Vector3 Resolve(Collider col, Vector3 tail, float hitRadius)
    {
        switch (col.Shape)
        {
            case ColliderShape.Capsule:
            {
                // Closest point on the capsule's core segment, then treat it as a sphere there.
                Vector3 seg = col.WorldTail - col.WorldCenter;
                float segLenSq = seg.LengthSquared();
                Vector3 nearest = segLenSq < 1e-12f
                    ? col.WorldCenter
                    : col.WorldCenter + seg * Mathf.Clamp(seg.Dot(tail - col.WorldCenter) / segLenSq, 0f, 1f);
                return PushOut(tail, nearest, col.WorldRadius + hitRadius);
            }

            case ColliderShape.Inside:
            {
                // Keep the tail within the sphere instead of outside it — VRM 1.0 uses this to
                // fence hair inside a hood or a helmet.
                float limit = col.WorldRadius - hitRadius;
                if (limit <= 0f) return col.WorldCenter;
                Vector3 d = tail - col.WorldCenter;
                float distSq = d.LengthSquared();
                if (distSq <= limit * limit) return tail;
                return col.WorldCenter + d * (limit / Mathf.Sqrt(distSq));
            }

            default:
                return PushOut(tail, col.WorldCenter, col.WorldRadius + hitRadius);
        }
    }

    private static Vector3 PushOut(Vector3 p, Vector3 center, float minDist)
    {
        Vector3 d = p - center;
        float distSq = d.LengthSquared();
        if (distSq >= minDist * minDist) return p;
        if (distSq < 1e-12f) return center + Vector3.Up * minDist;   // exactly concentric
        return center + d * (minDist / Mathf.Sqrt(distSq));
    }

    /// Shortest-arc rotation taking unit vector `from` onto unit vector `to`.
    private static Quaternion SwingTo(Vector3 from, Vector3 to)
    {
        float dot = Mathf.Clamp(from.Dot(to), -1f, 1f);
        if (dot > 0.99999f) return Quaternion.Identity;
        if (dot < -0.99999f)
        {
            // Antiparallel: any perpendicular axis is a valid 180° swing. Pick a stable one.
            Vector3 axis = from.Cross(Vector3.Up);
            if (axis.LengthSquared() < 1e-6f) axis = from.Cross(Vector3.Right);
            return new Quaternion(axis.Normalized(), Mathf.Pi);
        }
        Vector3 a = from.Cross(to);
        if (a.LengthSquared() < 1e-12f) return Quaternion.Identity;
        return new Quaternion(a.Normalized(), Mathf.Acos(dot));
    }

    /// Slerp fraction that lands exactly on the cone edge, given the current and limit cosines.
    private static float SafeSlerpT(float dot, float limitCos)
    {
        float angle = Mathf.Acos(Mathf.Clamp(dot, -1f, 1f));
        if (angle < 1e-5f) return 1f;
        return Mathf.Clamp(Mathf.Acos(Mathf.Clamp(limitCos, -1f, 1f)) / angle, 0f, 1f);
    }

    private static float MaxAngleToCos(float degrees)
    {
        if (degrees <= 0f || degrees >= 180f) return -1f;   // sentinel: no limit
        return Mathf.Cos(Mathf.DegToRad(degrees));
    }

    private static Vector3 ToVector(float[] v, Vector3 fallback)
        => v != null && v.Length >= 3 ? new Vector3(v[0], v[1], v[2]) : fallback;

    // ── grab API ──────────────────────────────────────────────────────────────────────────

    /// Grab the nearest grabbable chain to `worldPos`. Returns its index, or -1 if none is close
    /// enough. Picks the closest rather than the first match so reaching for a pigtail doesn't
    /// snag the skirt that happens to be earlier in the list.
    public int StartGrab(Vector3 worldPos, float maxDist = 0.15f)
    {
        int best = -1;
        float bestSq = maxDist * maxDist;

        for (int ci = 0; ci < _chains.Count; ci++)
        {
            if (!_chains[ci].IsGrabbable || _grabs.ContainsKey(ci)) continue;
            foreach (var j in _chains[ci].Joints)
            {
                float dSq = j.Tail.DistanceSquaredTo(worldPos);
                if (dSq < bestSq) { bestSq = dSq; best = ci; }
            }
        }

        if (best >= 0)
        {
            _grabs[best] = new GrabState { WorldPosition = worldPos, IsLocal = true };
            _chains[best].Wake();
        }
        return best;
    }

    public void UpdateGrab(int chainIdx, Vector3 worldPos)
    {
        if (_grabs.TryGetValue(chainIdx, out var grab) && grab.IsLocal)
        {
            grab.WorldPosition = worldPos;
            _grabs[chainIdx] = grab;
        }
    }

    public void ReleaseGrab(int chainIdx)
    {
        if (_grabs.TryGetValue(chainIdx, out var grab) && grab.IsLocal) _grabs.Remove(chainIdx);
    }

    public void ApplyRemoteGrab(int chainIdx, Vector3 worldPos)
    {
        if (chainIdx < 0 || chainIdx >= _chains.Count) return;
        if (!_chains[chainIdx].IsGrabbable) return;
        _grabs[chainIdx] = new GrabState { WorldPosition = worldPos, IsLocal = false };
        _chains[chainIdx].Wake();
    }

    public void ReleaseRemoteGrab(int chainIdx)
    {
        if (_grabs.TryGetValue(chainIdx, out var grab) && !grab.IsLocal) _grabs.Remove(chainIdx);
    }

    public IReadOnlyList<(int Index, string Name)> GetGrabbableChains()
    {
        var result = new List<(int, string)>();
        for (int i = 0; i < _chains.Count; i++)
            if (_chains[i].IsGrabbable) result.Add((i, _chains[i].Name ?? $"chain_{i}"));
        return result;
    }

    public int FindChainByRootBoneName(string boneName)
    {
        for (int i = 0; i < _chains.Count; i++)
            if (_skeleton.GetBoneName(_chains[i].RootBone) == boneName) return i;
        return -1;
    }

    // ── diagnostics ───────────────────────────────────────────────────────────────────────
    // Used by `--serika-phystest`. Secondary motion is very easy to fool yourself about from a
    // screenshot — a skirt that clips through a thigh and a skirt that rests on it look nearly
    // identical in a still frame — so the acceptance check is numeric.

    public int ChainCount => _chains.Count;
    public int ColliderCount => _colliders.Count;

    /// Frames on which the live `_Process` loop actually solved. The diagnostic drives the
    /// solver directly, which would happily pass even if the node were never ticked in a real
    /// scene — this is how that gap gets checked.
    public int SolvedFrames { get; private set; }

    /// Run one frame of the solver deterministically, bypassing the LOD and camera checks.
    public void DebugStep(float dt)
    {
        if (_skeleton == null || _chains.Count == 0) return;
        _accumulator = Mathf.Min(_accumulator + dt, FixedStep * MaxSubSteps);

        PrepareFrame(out bool teleported);
        if (teleported) { ResetToRest(); return; }

        int steps = 0;
        while (_accumulator >= FixedStep && steps < MaxSubSteps)
        {
            Step(FixedStep);
            _accumulator -= FixedStep;
            steps++;
        }
        WriteBack();
    }

    /// Signed geometric clearance from every simulated joint tail to the nearest body collider
    /// it respects: negative means the bone is literally inside the body. A skirt hanging
    /// through the legs shows up here and nowhere else.
    ///
    /// `Anchored` flags joints whose *head* is already inside a collider. Those are geometrically
    /// unresolvable rather than mis-simulated — a bone of fixed length rooted inside a sphere has
    /// no rotation that puts its tip outside — and they are legitimately common: hair roots sit
    /// under the scalp, a skirt's top ring sits inside the hips. They are reported separately
    /// instead of being silently counted as either pass or fail.
    public IReadOnlyList<(string Chain, string Bone, float Clearance, bool Anchored)> DebugClearances()
    {
        var report = new List<(string, string, float, bool)>();
        foreach (var chain in _chains)
        {
            foreach (var j in chain.Joints)
            {
                float best = float.MaxValue;
                bool anchored = false;

                foreach (int ci in chain.ColliderIdx)
                {
                    var col = _colliders[ci];
                    if (col.Shape == ColliderShape.Inside) continue;

                    best = Mathf.Min(best, j.Tail.DistanceTo(Nearest(col, j.Tail)) - col.WorldRadius);
                    if (j.Head.DistanceTo(Nearest(col, j.Head)) < col.WorldRadius) anchored = true;
                }

                if (best < float.MaxValue)
                    report.Add((chain.Name, _skeleton.GetBoneName(j.BoneIdx), best, anchored));
            }
        }
        return report;
    }

    /// Closest point on a collider's core (its centre for a sphere, its segment for a capsule).
    private static Vector3 Nearest(Collider col, Vector3 p)
    {
        if (col.Shape != ColliderShape.Capsule) return col.WorldCenter;
        Vector3 seg = col.WorldTail - col.WorldCenter;
        float segLenSq = seg.LengthSquared();
        if (segLenSq < 1e-12f) return col.WorldCenter;
        return col.WorldCenter + seg * Mathf.Clamp(seg.Dot(p - col.WorldCenter) / segLenSq, 0f, 1f);
    }

    /// World-space tail of every joint, for measuring how far a rig actually moves.
    public IReadOnlyList<(string Bone, Vector3 Tail)> DebugTails()
    {
        var report = new List<(string, Vector3)>();
        foreach (var chain in _chains)
            foreach (var j in chain.Joints)
                report.Add((_skeleton.GetBoneName(j.BoneIdx), j.Tail));
        return report;
    }

    // ── state ─────────────────────────────────────────────────────────────────────────────

    private enum ColliderShape { Sphere = 0, Capsule = 1, Inside = 2 }

    private sealed class Chain
    {
        public string Name;
        public int RootBone;
        public int AnchorBone;          // parent of RootBone; -1 means hang off the skeleton node
        public float Stiffness;
        public float Gravity;
        public Vector3 GravityDir = Vector3.Down;
        public float Drag;
        public float Radius;
        public bool IsGrabbable;
        public float MaxAngleCos = -1f; // -1 = unlimited
        public readonly List<Joint> Joints = new();
        public readonly List<int> ColliderIdx = new();

        // Sleeping state.
        public bool Awake = true;
        public float StillTime;

        public void Wake() { Awake = true; StillTime = 0f; }

        // Per-frame sampled state.
        public Vector3 AnchorPos;
        public Vector3 PrevAnchorPos;
        public Quaternion AnchorRot = Quaternion.Identity;
        public Vector3 GravityWorld = Vector3.Down;
        public float Scale = 1f;
    }

    private sealed class Joint
    {
        public int BoneIdx;
        public int ParentJoint;         // index into Chain.Joints, -1 for the chain root
        public Vector3 RestLocalPos;    // rest origin, in the parent bone's space
        public Quaternion RestLocalRot; // rest rotation, relative to the parent bone
        public Vector3 BoneAxis;        // unit direction to the (average) child, in bone space
        public float Length;

        public Vector3 Head;
        public Vector3 Tail;
        public Vector3 PrevTail;
        public Quaternion WorldRot = Quaternion.Identity;
    }

    private sealed class Collider
    {
        public string Name;
        public int BoneIdx;
        public float Radius;
        public ColliderShape Shape;
        public Vector3 LocalOffset;
        public Vector3 LocalTail;

        public Vector3 WorldCenter;
        public Vector3 WorldTail;
        public float WorldRadius;
    }

    private struct GrabState
    {
        public Vector3 WorldPosition;
        public bool IsLocal;
    }
}
