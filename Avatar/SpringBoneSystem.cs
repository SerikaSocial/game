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

    /// How much the restoring pull strengthens with deflection. At rest the pull is exactly the
    /// authored stiffness, so soft rigs still feel soft; at 90° off rest it is 11× that.
    ///
    /// Chosen, not guessed. At steady state a chain trailing a body moving at `v` satisfies
    /// `v·drag = s·(1 + Ramp·(1−cosθ))·sinθ`. For the softest hair in circulation (stiffness 0.4,
    /// drag 0.4) at this game's 4 m/s walk, Ramp = 10 settles it around 50–60°; at the 7 m/s
    /// sprint, around 70°. Below about 8 the chain cannot win at walking pace at all and pins
    /// flat against its length constraint, which is what "that much force backward, maybe in
    /// heavy wind" was describing.
    private const float StiffnessRamp = 10f;

    /// Hard cone limit applied to chains that don't specify one, as a last backstop against a
    /// chain ending up straight out sideways. Deliberately generous — the previous solver's 25°
    /// clamp is what made the rig look like it had no physics at all.
    private const float DefaultMaxAngleDegrees = 72f;

    private Skeleton3D _skeleton;
    private readonly List<Chain> _chains = new();
    private readonly List<Collider> _colliders = new();
    private readonly Dictionary<int, GrabState> _grabs = new();

    /// Anchor-bone index → its world position and rotation this frame. Cleared and refilled per
    /// frame; kept as a field so the per-frame path doesn't allocate. The rotation is cached
    /// alongside the position because extracting a quaternion from a basis orthonormalizes it,
    /// which is not something to repeat 45 times for the three distinct bones Suisei uses.
    private readonly Dictionary<int, (Vector3 Pos, Quaternion Rot)> _anchorCache = new();

    private float _accumulator;
    private bool _needsReset = true;
    private bool _calibrated;
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

    /// `colliderMetas` are the colliders the avatar's author placed. `bodyColliders` are the
    /// generated backstop — capsules down the limbs and torso, derived from the rig's own
    /// proportions — and every collidable chain gets them *in addition* to whatever the author
    /// assigned it.
    ///
    /// That additive rule exists because authored collider sets are routinely incomplete in ways
    /// that only show up in motion. Shiroko's hair is assigned the head and two shoulders and
    /// nothing for the torso, so her back hair swings straight through her back. Suisei's coat
    /// skirt is assigned *no* colliders at all. Treating the author's list as the complete story
    /// reproduces those holes; treating it as additions on top of a body that is always solid
    /// does not, and still honours every collider they did place.
    public void Setup(Skeleton3D skeleton, IReadOnlyList<PhysBoneMeta> physBones,
        IReadOnlyList<PhysBoneColliderMeta> colliderMetas,
        IReadOnlyList<PhysBoneColliderMeta> bodyColliders = null, float waistY = 0f)
    {
        _skeleton = skeleton;
        _chains.Clear();
        _colliders.Clear();
        _grabs.Clear();
        JointCount = 0;
        _needsReset = true;
        _calibrated = false;

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

        // The generated body backstop goes in after the authored set, and its indices are kept
        // so every chain can be given them regardless of what the author assigned.
        var backstop = new List<int>();
        int authoredCount = _colliders.Count;
        BuildColliders(bodyColliders);
        for (int i = authoredCount; i < _colliders.Count; i++) backstop.Add(i);

        foreach (var pb in physBones)
        {
            if (pb == null || string.IsNullOrEmpty(pb.RootTransform)) continue;
            int rootBone = _skeleton.FindBone(pb.RootTransform);
            if (rootBone < 0) continue;

            var chain = BuildChain(pb, rootBone, byName, backstop, waistY);
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
                Region = Mathf.Clamp(c.Region, 0, 2),
            });
        }
    }

    /// Flatten the bone subtree under `rootBone` into a depth-first joint array. DFS order
    /// guarantees a joint always appears after its parent, so a single forward pass over the
    /// array can solve the whole tree with each joint reading its parent's already-solved world
    /// rotation. This is what makes branching work: a skirt root with ten panels hanging off it
    /// is one chain of ten independent branches, not ten bones in a line.
    private Chain BuildChain(PhysBoneMeta pb, int rootBone, Dictionary<string, int> colliderByName,
        List<int> bodyBackstop, float waistY)
    {
        var chain = new Chain
        {
            Name = string.IsNullOrEmpty(pb.Name) ? _skeleton.GetBoneName(rootBone) : pb.Name,
            RootBone = rootBone,
            AnchorBone = _skeleton.GetBoneParent(rootBone),
            Stiffness = Mathf.Max(pb.Stiffness, 0f),
            Gravity = Mathf.Max(pb.Gravity, 0f),
            GravityDir = ToVector(pb.GravityDir, Vector3.Down).LimitLength(1f),
            // Floor the drag. VRoid writes `dragForce: 0` for coat skirts (Suisei's are all zero),
            // and zero drag is a perpetual-motion machine: the chain keeps every bit of velocity
            // it ever gains and rings forever, never settling and never sleeping.
            Drag = Mathf.Clamp(pb.Damping, 0.08f, 0.99f),
            Radius = Mathf.Max(pb.Radius, 0f),
            IsGrabbable = pb.IsGrabbable,
            MaxAngleCos = MaxAngleToCos(pb.MaxAngleDegrees > 0f ? pb.MaxAngleDegrees
                                                               : DefaultMaxAngleDegrees),
        };
        if (chain.GravityDir.LengthSquared() < 1e-8f) chain.GravityDir = Vector3.Down;
        else chain.GravityDir = chain.GravityDir.Normalized();

        // Which colliders this chain hits. `allowCollision: false` means none at all — chest
        // physics needs that, since its bones sit inside the torso by construction and body
        // collision would shove them out every frame.
        //
        // Otherwise: the author's assignments, if any, plus the body backstop always.
        // A *null* collider list means "unspecified, use every authored collider"; an *empty*
        // one means the author deliberately assigned none, which is what VRM's empty
        // `colliderGroups` says and what Suisei's coat skirt has. Collapsing those two into
        // "use everything" is what had that skirt colliding with her hands.
        if (pb.AllowCollision)
        {
            if (pb.Colliders == null)
            {
                foreach (var kv in colliderByName) chain.ColliderIdx.Add(kv.Value);
            }
            else
            {
                foreach (var n in pb.Colliders)
                    if (n != null && colliderByName.TryGetValue(n, out int ci)) chain.ColliderIdx.Add(ci);
            }

            // Which half of the body this chain can plausibly reach, inferred from where it
            // hangs rather than from its name — a skirt rooted on the shin (Suisei's coat) and
            // one rooted on the hips (Shiroko's hem) both come out lower, and hair off the head
            // comes out upper, with no keyword involved.
            int chainRegion = _skeleton.GetBoneGlobalRest(rootBone).Origin.Y < waistY ? 2 : 1;

            if (bodyBackstop != null)
                foreach (int i in bodyBackstop)
                    if (_colliders[i].Region == 0 || _colliders[i].Region == chainRegion)
                        if (!chain.ColliderIdx.Contains(i)) chain.ColliderIdx.Add(i);
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

        AddJoint(chain, rootBone, -1, children, Transform3D.Identity);
        return chain;
    }

    /// Walk the subtree, turning bones into joints.
    ///
    /// `carry` accumulates the rest transform of any bones skipped on the way down, so a joint's
    /// offset stays correct relative to the last bone actually simulated rather than to its
    /// immediate skeleton parent.
    private void AddJoint(Chain chain, int bone, int parentJoint, Dictionary<int, List<int>> children,
        Transform3D carry)
    {
        children.TryGetValue(bone, out var kids);

        var rest = _skeleton.GetBoneRest(bone);
        Transform3D local = carry * rest;   // rest, relative to the last simulated ancestor

        // The tail is where this joint's child sits; with several children the average is the
        // single direction the parent can be said to point.
        Vector3 childLocal;
        bool hub = false;
        if (kids != null && kids.Count > 0)
        {
            Vector3 sum = Vector3.Zero;
            float meanLen = 0f;
            foreach (int k in kids)
            {
                var o = _skeleton.GetBoneRest(k).Origin;
                sum += o;
                meanLen += o.Length();
            }
            childLocal = sum / kids.Count;
            meanLen /= kids.Count;

            // When children fan out in opposing directions they cancel, and the average is a
            // near-zero vector pointing nowhere — a *hub*, not a link. Simulating one is
            // degenerate: the axis is far shorter than the distance the body travels per step,
            // so the joint gets slammed to a different point on its length sphere every frame,
            // and every branch below it inherits the noise. Carry it through instead.
            //
            // This is a guard, not a fix for an observed rig: the two roots that prompted the
            // check (Shiroko's `hair_phys` and `hem_phys`) measure at ratios of 0.75 and 0.55,
            // comfortably above the threshold, and are simulated normally.
            hub = kids.Count > 1 && meanLen > 1e-5f && childLocal.Length() < meanLen * 0.35f;
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

        // A hub, or a bone too short to define a direction, is carried through rather than
        // simulated — skipping it outright would drop everything below it.
        if (hub || boneLength <= 1e-5f)
        {
            if (kids != null)
                foreach (int k in kids) AddJoint(chain, k, parentJoint, children, local);
            return;
        }

        chain.Joints.Add(new Joint
        {
            BoneIdx = bone,
            ParentJoint = parentJoint,
            RestLocalPos = local.Origin,
            RestLocalRot = local.Basis.GetRotationQuaternion().Normalized(),
            // Rotation contributed by skipped ancestors. The solver works relative to the last
            // simulated joint, but `SetBonePoseRotation` wants a pose relative to the bone's
            // real skeleton parent, so this is divided back out on write-back.
            CarryRot = carry.Basis.GetRotationQuaternion().Normalized(),
            BoneAxis = childLocal / boneLength,
            Length = boneLength,
        });
        parentJoint = chain.Joints.Count - 1;

        if (kids == null) return;
        // Children start a fresh carry: this bone is simulated, so it is their reference frame.
        foreach (int k in kids) AddJoint(chain, k, parentJoint, children, Transform3D.Identity);
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
        bool halfSkip = lod == Lod.Half && _halfRatePhase;

        // Step only when a full 60 Hz interval has banked, but interpolate and write *every*
        // frame regardless — that constant re-write at render rate is the whole point.
        if (_accumulator >= FixedStep && !halfSkip)
        {
            PrepareFrame(out bool teleported);
            if (teleported)
            {
                // Push the rest pose out the same frame. Deferring it would leave the rig holding
                // its pre-teleport shape for a frame at the new location.
                ResetToRest();
                WriteBack(1f);
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
            if (steps > 0) SolvedFrames++;
        }

        // Render the pose interpolated between the last two solved states, by how far into the
        // next step we already are. Without this the bones hold one 60 Hz pose for every render
        // frame until the next step lands and then snap to it — which at 90–144 fps is exactly
        // the "hair stuttering" the solver looked fine in numbers but janky on screen. Writing an
        // interpolated pose every frame, step or not, makes it smooth at any refresh rate.
        WriteBack(_accumulator / FixedStep);
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

        // Chains overwhelmingly share anchors — Suisei's 36 coat-skirt chains hang off two shin
        // bones and her nine hair chains off one head bone. Querying per chain made the anchor
        // lookups alone the dominant per-frame cost for that avatar, and they are the one thing
        // a sleeping chain still has to do (it needs the anchor to know when to wake).
        _anchorCache.Clear();

        foreach (var chain in _chains)
        {
            (Vector3 pos, Quaternion rot) anchor;
            if (chain.AnchorBone < 0) anchor = (skelXform.Origin, skelRot);
            else if (!_anchorCache.TryGetValue(chain.AnchorBone, out anchor))
            {
                var world = skelXform * _skeleton.GetBoneGlobalPose(chain.AnchorBone);
                anchor = (world.Origin, world.Basis.GetRotationQuaternion().Normalized());
                _anchorCache[chain.AnchorBone] = anchor;
            }

            var pos = anchor.pos;
            var rot = anchor.rot;

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
            if (!_calibrated) { CalibrateRestSeparation(); _calibrated = true; }
            _needsReset = false;
        }
    }

    /// Record how far each joint sits from each of its colliders in the authored rest pose.
    ///
    /// Run once, on the first frame the rig is placed in the world, because it needs the
    /// colliders in world space — which is only true after `PrepareFrame` has posed them. The
    /// numbers become the floor on how far collision may push each joint, so a rig that was
    /// authored resting against (or inside) the body keeps that shape.
    private void CalibrateRestSeparation()
    {
        foreach (var chain in _chains)
        {
            int n = chain.ColliderIdx.Count;
            if (n == 0) continue;

            foreach (var j in chain.Joints)
            {
                j.RestSeparation = new float[n];
                for (int k = 0; k < n; k++)
                {
                    var col = _colliders[chain.ColliderIdx[k]];
                    j.RestSeparation[k] = col.Shape == ColliderShape.Inside
                        ? float.MaxValue                       // inside-colliders are a ceiling, not a floor
                        : j.Tail.DistanceTo(Nearest(col, j.Tail));
                }
            }
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

                // Remember where this joint was before this substep, so the render frame can
                // interpolate from it toward the new solved orientation.
                j.WorldRotPrev = j.WorldRot;

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

                    // Restoring pull toward the rest direction. VRM's model uses a *constant*
                    // magnitude here, and that is precisely why hair flies out horizontally when
                    // you walk: a constant pull `s` can only balance a body moving at `v` while
                    // `s > v · drag`, and authored stiffness (0.4–1.0) loses that race at walking
                    // speed. Past that point the chain saturates against its length constraint —
                    // pinned at 90°, which reads as gale-force wind.
                    //
                    // So the pull ramps with how far the joint has strayed: identical to the
                    // authored value at rest, so soft rigs still feel soft and author tuning is
                    // preserved where it is actually observed, but several times stronger out at
                    // large deflections, where it turns a saturating chain into one that settles
                    // at a believable lag angle.
                    float deviation = 1f - Mathf.Clamp(restDir.Dot((j.Tail - head).Normalized()), -1f, 1f);
                    float pull = Mathf.Min(stiffness * (1f + StiffnessRamp * deviation) * dt, length);
                    Vector3 external = chain.GravityWorld * (chain.Gravity * dt);

                    tail = j.Tail + inertia + restDir * pull + external;
                }

                // Bone length is rigid: project back onto the sphere around the head.
                tail = ProjectToLength(head, tail, restDir, length);

                if (chain.ColliderIdx.Count > 0)
                {
                    float hitRadius = chain.Radius * chain.Scale;
                    var restSep = j.RestSeparation;

                    // Resolve and re-constrain twice. One pass is not enough: pushing the tail
                    // out of a collider moves it off the bone-length sphere, and snapping it back
                    // onto that sphere can drop it straight back inside — which is how a hair
                    // strand ended up 3 cm inside a shoulder during a hard sidestep. A second
                    // pass converges on the point that satisfies both, and two is where the
                    // returns stop; a third changes nothing measurable and costs Quest frames.
                    for (int iter = 0; iter < 2; iter++)
                    {
                        for (int k = 0; k < chain.ColliderIdx.Count; k++)
                        {
                            var col = _colliders[chain.ColliderIdx[k]];

                            // Never push a joint further out than where its author put it. A coat
                            // that hugs a thigh rests *inside* the leg capsule by design; forcing
                            // it out to the capsule's surface every frame inflated Suisei's skirt
                            // 15 cm off her legs while she stood still. The collider's job here is
                            // to stop the joint going deeper than it started, not to relocate it.
                            float minDist = restSep != null && k < restSep.Length
                                ? Mathf.Min(col.WorldRadius + hitRadius, restSep[k])
                                : col.WorldRadius + hitRadius;

                            tail = Resolve(col, tail, minDist);
                        }

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

    /// Push the skeleton every render frame. `alpha` is how far into the next 60 Hz step this
    /// frame sits (0 = just stepped, →1 = about to step again); each joint is rendered at the
    /// slerp between its previous and current solved orientation, so the motion is smooth at any
    /// refresh rate rather than snapping once per fixed step.
    ///
    /// Joints are in DFS order, so a child's interpolated parent orientation (`RenderRot`) is
    /// always computed before the child needs it — the interpolation stays consistent down the
    /// chain instead of mixing an interpolated child with a non-interpolated parent.
    private void WriteBack(float alpha)
    {
        alpha = Mathf.Clamp(alpha, 0f, 1f);

        foreach (var chain in _chains)
        {
            if (!chain.Awake) continue;   // its bones already hold the pose it settled into

            var joints = chain.Joints;
            for (int ji = 0; ji < joints.Count; ji++)
            {
                var j = joints[ji];
                j.RenderRot = j.WorldRotPrev.Slerp(j.WorldRot, alpha);

                Quaternion parentRot = j.ParentJoint < 0 ? chain.AnchorRot : joints[j.ParentJoint].RenderRot;
                // Divide out the skipped ancestors' rotation: the pose Godot wants is relative
                // to this bone's real parent, not to the last bone the solver simulated.
                var local = ((parentRot * j.CarryRot).Inverse() * j.RenderRot).Normalized();
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
                // No history to interpolate from on a reset — snap both so the first rendered
                // frame is the rest pose exactly, not a slerp toward it from stale data.
                j.WorldRotPrev = restWorldRot;
                j.RenderRot = restWorldRot;
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

    /// Keep a tail point at least `minDist` from a collider's core (or, for an inside-sphere,
    /// within it).
    private static Vector3 Resolve(Collider col, Vector3 tail, float minDist)
    {
        if (col.Shape == ColliderShape.Inside)
        {
            // Keep the tail within the sphere instead of outside it — VRM 1.0 uses this to
            // fence hair inside a hood or a helmet.
            if (minDist <= 0f) return col.WorldCenter;
            Vector3 d = tail - col.WorldCenter;
            float distSq = d.LengthSquared();
            if (distSq <= minDist * minDist) return tail;
            return col.WorldCenter + d * (minDist / Mathf.Sqrt(distSq));
        }

        return PushOut(tail, Nearest(col, tail), minDist);
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
        // The diagnostic reads solved tails, not rendered ones, so it wants the full current
        // state, not an interpolated fraction.
        WriteBack(1f);
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

                for (int k = 0; k < chain.ColliderIdx.Count; k++)
                {
                    var col = _colliders[chain.ColliderIdx[k]];
                    if (col.Shape == ColliderShape.Inside) continue;

                    // Measure against the floor the solver actually enforces: the body surface,
                    // or where the author rested this joint if that was already inside it. A hair
                    // root sits under the scalp by design, and calling that a 10 cm penetration
                    // says more about the metric than about the rig. What matters is whether the
                    // joint ends up *deeper* than it was authored.
                    float floor = col.WorldRadius;
                    if (j.RestSeparation != null && k < j.RestSeparation.Length)
                        floor = Mathf.Min(floor, j.RestSeparation[k]);

                    best = Mathf.Min(best, j.Tail.DistanceTo(Nearest(col, j.Tail)) - floor);
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

    /// Largest per-joint deflection from rest, in degrees. Far more interpretable than tip
    /// displacement: a long chain accumulates displacement across many joints even when each one
    /// is barely bent, so displacement alone can't tell "long hair trailing naturally" from
    /// "every joint pinned flat against its length constraint".
    /// Joints whose head is inside a collider are excluded: they are held off the body by the
    /// push-out every step, so their angle reports how deep the collider is, not how hard the
    /// chain is being thrown. Returns the worst free joint and names it.
    public (float Degrees, string Bone) DebugMaxAngleDegrees()
    {
        float worst = 0f;
        string worstBone = "(none)";

        foreach (var chain in _chains)
        {
            var joints = chain.Joints;
            for (int ji = 0; ji < joints.Count; ji++)
            {
                var j = joints[ji];

                // Skip joints whose head is inside a collider, and joints whose tail is resting
                // against one. Both are held at an angle the body's shape dictates rather than
                // one the motion produced — a hair root lying along the skull reads as a 65°
                // deflection and has nothing to do with how hard the chain is being thrown.
                bool constrained = false;
                float hit = chain.Radius * chain.Scale;
                foreach (int ci in chain.ColliderIdx)
                {
                    var col = _colliders[ci];
                    if (col.Shape == ColliderShape.Inside) continue;
                    if (j.Head.DistanceTo(Nearest(col, j.Head)) < col.WorldRadius ||
                        j.Tail.DistanceTo(Nearest(col, j.Tail)) < col.WorldRadius + hit + 0.001f)
                    {
                        constrained = true;
                        break;
                    }
                }
                if (constrained) continue;

                Quaternion parentRot = j.ParentJoint < 0 ? chain.AnchorRot : joints[j.ParentJoint].WorldRot;
                Vector3 restDir = (parentRot * j.RestLocalRot * j.BoneAxis).Normalized();
                Vector3 curDir = (j.Tail - j.Head).Normalized();
                float deg = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(restDir.Dot(curDir), -1f, 1f)));
                if (deg > worst) { worst = deg; worstBone = $"{chain.Name}/{_skeleton.GetBoneName(j.BoneIdx)}"; }
            }
        }
        return (worst, worstBone);
    }

    /// How far the settled rig sits from the pose its author actually built, per joint, in
    /// metres. Should be ~0 at rest: the solver's job is to hold the authored shape when nothing
    /// is moving. A large value means colliders are shoving the rig off the body — a garment
    /// that hugs a leg being pushed out into a balloon, say — which is a silent failure the
    /// clearance check cannot see, because it measures against those very colliders.
    public (float Max, string Bone) DebugRestDrift()
    {
        float worst = 0f;
        string worstBone = "(none)";

        foreach (var chain in _chains)
        {
            var joints = chain.Joints;
            for (int ji = 0; ji < joints.Count; ji++)
            {
                var j = joints[ji];
                Quaternion parentRot = j.ParentJoint < 0 ? chain.AnchorRot : joints[j.ParentJoint].WorldRot;
                Vector3 restTail = j.Head + (parentRot * j.RestLocalRot * j.BoneAxis).Normalized() *
                                   (j.Length * chain.Scale);
                float d = restTail.DistanceTo(j.Tail);
                if (d > worst) { worst = d; worstBone = $"{chain.Name}/{_skeleton.GetBoneName(j.BoneIdx)}"; }
            }
        }
        return (worst, worstBone);
    }

    /// Per-chain tail positions, so a diagnostic can tell which chains are moving and which are
    /// dead. An avatar-wide "it moves" average happily hides a skirt that is completely frozen
    /// while the hair next to it swings.
    public IReadOnlyList<(string Chain, int Joints, Vector3[] Tails)> DebugChainTails()
    {
        var report = new List<(string, int, Vector3[])>();
        foreach (var chain in _chains)
        {
            var tails = new Vector3[chain.Joints.Count];
            for (int i = 0; i < chain.Joints.Count; i++) tails[i] = chain.Joints[i].Tail;
            report.Add((chain.Name, chain.Joints.Count, tails));
        }
        return report;
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
        public Vector3 RestLocalPos;    // rest origin, relative to the last simulated ancestor
        public Quaternion RestLocalRot; // rest rotation, relative to the last simulated ancestor
        public Quaternion CarryRot = Quaternion.Identity; // rotation of ancestors we skipped
        public Vector3 BoneAxis;        // unit direction to the (average) child, in bone space
        public float Length;

        /// Distance from this joint's rest tail to each of its chain's colliders, parallel to
        /// `Chain.ColliderIdx`. Null until the first frame calibrates it.
        public float[] RestSeparation;

        public Vector3 Head;
        public Vector3 Tail;
        public Vector3 PrevTail;
        public Quaternion WorldRot = Quaternion.Identity;
        public Quaternion WorldRotPrev = Quaternion.Identity; // orientation one step ago
        public Quaternion RenderRot = Quaternion.Identity;    // interpolated, written to the bone
    }

    private sealed class Collider
    {
        public string Name;
        public int BoneIdx;
        public float Radius;
        public ColliderShape Shape;
        public Vector3 LocalOffset;
        public Vector3 LocalTail;
        public int Region;              // 0 = both halves, 1 = upper, 2 = lower

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
