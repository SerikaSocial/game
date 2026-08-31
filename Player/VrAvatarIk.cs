using Godot;
using SerikaSocial.Avatar;

namespace SerikaSocial.Player;

/// Drives a humanoid `.ska` avatar from the three things a headset actually tracks — the head
/// and the two hands — and infers a whole body from them.
///
/// The solve is the standard VR full-body arrangement (the same shape as FinalIK's VRIK, which
/// is what VRChat and most social VR run): the **head is the anchor**, the hips hang off it, the
/// spine bridges the two, the feet are **planted in the world** and stepped when the body walks
/// away from them, and the arms reach for the controllers. Everything is derived from the rig's
/// own rest pose, so there are no per-avatar magic numbers.
///
/// Ordering matters and is not arbitrary:
///
///   1. **Hips** are placed from the head, because that is the only tracked point on the torso.
///      Crouching physically drops the head, so the hips drop with it and the knees take up the
///      slack — the avatar crouches because the player did, not because a button was pressed.
///   2. **Spine** bridges hips → head as a single swing distributed over the chain, then the
///      hips are corrected by whatever the chain failed to close. One iteration is exact here:
///      the chain is rigid, so the residual is a pure translation.
///   3. **Head** is written last on that chain and *absolutely* — it must equal the camera, or
///      first person looks out of somewhere that is not the player's eyes.
///   4. **Legs** solve to planted world-space feet, so standing still leaves the feet still.
///   5. **Arms** solve to the controllers, including the wrist's own rotation.
///
/// All maths runs in *skeleton space* (the space `GetBoneGlobalPose` reports in). World-space
/// targets are pulled into it once, up front.
///
/// What this deliberately does NOT do: recover bone roll from the retargeter's clips (the same
/// trade-off `AnimRetargeter` documents), or model the shoulder girdle beyond a single clavicle
/// swing. Both are visible only next to a much more expensive solver.
public sealed class VrAvatarIk
{
    private readonly AvatarInstance _avatar;
    private readonly Skeleton3D _skel;

    private readonly int _head, _neck, _hips, _spine, _chest, _upperChest;
    private readonly int _leftShoulder, _rightShoulder;
    private readonly Arm _leftArm, _rightArm;
    private readonly Leg _leftLeg, _rightLeg;

    private readonly bool _valid;

    // ── Rest measurements, all skeleton space, all taken once. ───────────────────────
    private readonly Vector3 _restHipsPos, _restHeadPos;
    private readonly Vector3 _restHipsToHead;      // hips → head in the bind pose
    private readonly Quaternion _restHipsRot;
    private readonly float _restHipsY;             // standing hip height above the rig's origin
    private readonly float _stanceHalfWidth;       // how far each foot sits off centre at rest
    private readonly float _legReach;              // hip → foot at full extension

    /// The rest rotation of each spine bone, and the share of the hips→head swing it carries.
    /// Shares sum to 1, so composing them reproduces the whole swing exactly — they are
    /// fractions of a single rotation about a single axis, which commute.
    private readonly (int bone, Quaternion rest, float share)[] _spineChain;

    /// Maps a controller's own orientation onto the hand bone. Derived from the rig, not
    /// hardcoded — see `BuildHandFromController`.
    private readonly Quaternion _leftHandFromCtrl, _rightHandFromCtrl;

    /// One arm chain, resolved once. `RestDirUpper` is the T-pose direction from shoulder to
    /// elbow; `L1`/`L2` are the segment lengths the law-of-cosines solve needs.
    private readonly struct Arm
    {
        public readonly int Upper, Lower, Hand;
        public readonly Vector3 RestDirUpper, RestDirLower;
        public readonly Quaternion RestRotUpper, RestRotLower, RestRotHand;
        public readonly float L1, L2;
        public readonly Vector3 ShoulderRestPos; // skeleton-space shoulder position
        public readonly bool Ok;

        public Arm(Skeleton3D skel, int upper, int lower, int hand)
        {
            Upper = upper; Lower = lower; Hand = hand;
            if (upper < 0 || lower < 0 || hand < 0)
            {
                RestDirUpper = Vector3.Down; RestDirLower = Vector3.Down;
                RestRotUpper = Quaternion.Identity; RestRotLower = Quaternion.Identity;
                RestRotHand = Quaternion.Identity;
                L1 = L2 = 0; Ok = false;
                ShoulderRestPos = Vector3.Zero;
                return;
            }

            var ru = skel.GetBoneGlobalRest(upper);
            var rl = skel.GetBoneGlobalRest(lower);
            var rh = skel.GetBoneGlobalRest(hand);

            var du = rl.Origin - ru.Origin;
            var dl = rh.Origin - rl.Origin;
            L1 = du.Length();
            L2 = dl.Length();

            // A degenerate chain (zero-length segments) would divide by zero in the solve.
            Ok = L1 > 1e-4f && L2 > 1e-4f;
            RestDirUpper = Ok ? du / L1 : Vector3.Down;
            RestDirLower = Ok ? dl / L2 : Vector3.Down;
            RestRotUpper = ru.Basis.GetRotationQuaternion();
            RestRotLower = rl.Basis.GetRotationQuaternion();
            RestRotHand = rh.Basis.GetRotationQuaternion();
            ShoulderRestPos = ru.Origin;
        }
    }

    /// One leg chain. `RestDirUpper` is the rest direction from hip to knee; `L1`/`L2` are
    /// segment lengths. `RestFootPos` is where the foot sits in the bind pose, which is what the
    /// stance width and the planted-foot targets are measured from.
    private readonly struct Leg
    {
        public readonly int Upper, Lower, Foot, Toes;
        public readonly Vector3 RestDirUpper, RestDirLower;
        public readonly Quaternion RestRotUpper, RestRotLower, RestRotFoot;
        public readonly Vector3 RestFootPos, RestHipPos;
        public readonly float L1, L2;
        public readonly bool Ok;

        public Leg(Skeleton3D skel, int upper, int lower, int foot, int toes)
        {
            Upper = upper; Lower = lower; Foot = foot; Toes = toes;
            if (upper < 0 || lower < 0 || foot < 0)
            {
                RestDirUpper = Vector3.Down; RestDirLower = Vector3.Down;
                RestRotUpper = Quaternion.Identity; RestRotLower = Quaternion.Identity;
                RestRotFoot = Quaternion.Identity;
                RestFootPos = Vector3.Zero; RestHipPos = Vector3.Zero;
                L1 = L2 = 0; Ok = false;
                return;
            }

            var ru = skel.GetBoneGlobalRest(upper);
            var rl = skel.GetBoneGlobalRest(lower);
            var rf = skel.GetBoneGlobalRest(foot);

            var du = rl.Origin - ru.Origin;
            var dl = rf.Origin - rl.Origin;
            L1 = du.Length();
            L2 = dl.Length();

            Ok = L1 > 1e-4f && L2 > 1e-4f;
            RestDirUpper = Ok ? du / L1 : Vector3.Down;
            RestDirLower = Ok ? dl / L2 : Vector3.Down;
            RestRotUpper = ru.Basis.GetRotationQuaternion();
            RestRotLower = rl.Basis.GetRotationQuaternion();
            RestRotFoot = rf.Basis.GetRotationQuaternion();
            RestFootPos = rf.Origin;
            RestHipPos = ru.Origin;
        }
    }

    public VrAvatarIk(AvatarInstance avatar)
    {
        _avatar = avatar;
        _skel = avatar?.Skeleton;
        if (_skel == null) { _valid = false; return; }

        _head = avatar.BoneOf("head");
        _neck = avatar.BoneOf("neck");
        _hips = avatar.BoneOf("hips");
        _spine = avatar.BoneOf("spine");
        _chest = avatar.BoneOf("chest");
        _upperChest = avatar.BoneOf("upperChest");
        _leftShoulder = avatar.BoneOf("leftShoulder");
        _rightShoulder = avatar.BoneOf("rightShoulder");

        _leftArm = new Arm(_skel, avatar.BoneOf("leftUpperArm"), avatar.BoneOf("leftLowerArm"),
                        avatar.BoneOf("leftHand"));
        _rightArm = new Arm(_skel, avatar.BoneOf("rightUpperArm"), avatar.BoneOf("rightLowerArm"),
                         avatar.BoneOf("rightHand"));

        _leftLeg = new Leg(_skel, avatar.BoneOf("leftUpperLeg"), avatar.BoneOf("leftLowerLeg"),
                        avatar.BoneOf("leftFoot"), avatar.BoneOf("leftToes"));
        _rightLeg = new Leg(_skel, avatar.BoneOf("rightUpperLeg"), avatar.BoneOf("rightLowerLeg"),
                         avatar.BoneOf("rightFoot"), avatar.BoneOf("rightToes"));

        if (_hips >= 0)
        {
            var restHips = _skel.GetBoneGlobalRest(_hips);
            _restHipsPos = restHips.Origin;
            _restHipsRot = restHips.Basis.GetRotationQuaternion();
            _restHipsY = _restHipsPos.Y;
        }
        if (_head >= 0) _restHeadPos = _skel.GetBoneGlobalRest(_head).Origin;
        _restHipsToHead = _restHeadPos - _restHipsPos;

        // Stance width straight off the rig: whatever the author spaced the feet by is what this
        // avatar stands like. Floored so a rig modelled with its feet touching still gets a
        // stance rather than balancing on one point.
        _stanceHalfWidth = _leftLeg.Ok
            ? Mathf.Max(0.06f, Mathf.Abs(_leftLeg.RestFootPos.X))
            : 0.09f;
        // The ankle's own height off the floor, straight from the bind pose — see `GroundAt`.
        _ankleHeight = _leftLeg.Ok ? Mathf.Clamp(_leftLeg.RestFootPos.Y, 0.0f, 0.25f) : 0.07f;
        _legReach = _leftLeg.Ok ? _leftLeg.L1 + _leftLeg.L2 : 0.8f;

        _spineChain = BuildSpineChain();
        _leftHandFromCtrl = BuildHandFromController(_leftArm);
        _rightHandFromCtrl = BuildHandFromController(_rightArm);

        // Arms are the point of the exercise; a rig without them is not worth solving.
        _valid = _leftArm.Ok || _rightArm.Ok;
    }

    public bool Valid => _valid;

    /// Whether this rig has enough of a lower body to plant and step its feet. A rig without
    /// legs (or with degenerate ones) keeps whatever `AvatarInstance.Animate` did.
    public bool HasLegs => _leftLeg.Ok && _rightLeg.Ok && _hips >= 0;

    /// The bones the hips→head swing is spread across, with the share each one carries.
    ///
    /// A real spine bends over its whole length; putting the entire bend on one joint gives the
    /// snapped-neck look that reads instantly as "this is a VR avatar". Shares are weighted
    /// toward the lower spine because that is where a human actually bends — the neck contributes
    /// least, and gets the smallest share, or looking down folds the chin into the chest.
    ///
    /// Whatever bones the rig actually has get the shares; a rig with no `upperChest` (common)
    /// has its share redistributed rather than silently losing that fraction of the bend.
    private (int, Quaternion, float)[] BuildSpineChain()
    {
        var candidates = new (int bone, float weight)[]
        {
            (_spine, 0.34f),
            (_chest, 0.30f),
            (_upperChest, 0.22f),
            (_neck, 0.14f),
        };

        float total = 0f;
        foreach (var (bone, w) in candidates) if (bone >= 0) total += w;
        if (total <= 1e-4f) return System.Array.Empty<(int, Quaternion, float)>();

        var list = new System.Collections.Generic.List<(int, Quaternion, float)>(4);
        foreach (var (bone, w) in candidates)
        {
            if (bone < 0) continue;
            list.Add((bone, _skel.GetBoneGlobalRest(bone).Basis.GetRotationQuaternion(), w / total));
        }
        return list.ToArray();
    }

    /// The fixed rotation that takes a controller's orientation onto the hand bone's.
    ///
    /// **Derived from the rig, never hardcoded.** The requirement is: when the player holds the
    /// controller in the neutral orientation — Godot's node convention, −Z forward and +Y up —
    /// the avatar's hand should sit exactly where its bind pose puts it. So the offset is
    /// whatever satisfies `restHandRot = neutralCtrlFrame * offset`, i.e.
    /// `offset = neutralCtrlFrame⁻¹ * restHandRot`.
    ///
    /// `neutralCtrlFrame` is built from the rig's own forearm direction, so a T-posed VRM and a
    /// PMX rig modelled at 30° both get the alignment their own geometry implies. The only thing
    /// assumed about the hardware is the −Z/+Y convention Godot publishes controller poses in,
    /// which is the same convention `XRController3D`'s own forward axis uses.
    ///
    /// Because this is a *rotation offset*, wrist motion is correct regardless: turning the
    /// controller 90° turns the avatar's wrist 90° the same way. Only the neutral alignment
    /// depends on the convention above.
    private Quaternion BuildHandFromController(in Arm arm)
    {
        if (!arm.Ok) return Quaternion.Identity;

        // The finger direction continues the forearm, and VRM 1.0 requires a T-pose with the
        // palms facing down — so "up" out of the back of the hand is the rig's +Y.
        var fingers = arm.RestDirLower;
        var up = Vector3.Up;
        if (Mathf.Abs(fingers.Dot(up)) > 0.99f) up = Vector3.Back; // arm modelled straight up/down

        var neutral = Basis.LookingAt(fingers, up).GetRotationQuaternion();
        return neutral.Inverse() * arm.RestRotHand;
    }

    // ── Foot planting state ──────────────────────────────────────────────────────────
    // World space, because the point of a planted foot is that it does not move when the rest of
    // the avatar does. Index 0 = left, 1 = right.

    private readonly Vector3[] _footPlant = new Vector3[2];
    private readonly Vector3[] _footNormal = { Vector3.Up, Vector3.Up };
    private readonly float[] _footYaw = new float[2];
    private readonly Vector3[] _stepFrom = new Vector3[2];
    private readonly Vector3[] _stepTo = new Vector3[2];
    private readonly float[] _stepT = { 1f, 1f };   // 1 = planted, <1 = mid-step
    private readonly float[] _stepYawFrom = new float[2];
    private readonly float[] _stepYawTo = new float[2];
    private bool _feetInitialised;
    private int _lastSteppedFoot = 1;

    /// How far a foot may drift from where it wants to be before it takes a step. Roughly a
    /// half-stride: shorter and the avatar shuffles constantly, longer and it does the splits.
    private const float StepTriggerMetres = 0.32f;

    /// How far the body may rotate under a planted foot before the foot re-seats. Humans do not
    /// pivot on a fixed foot past about this much.
    private const float StepTriggerDegrees = 42f;

    private const float StepHeightMetres = 0.09f;
    private const float StepSecondsWalk = 0.26f;
    private const float StepSecondsRun = 0.16f;

    /// Physics layers a foot may stand on. World geometry only — a foot must not plant itself on
    /// another player's collider, and emphatically not on the local body it hangs off.
    private uint _groundMask = PhysicsLayers.World;
    private Rid _selfRid;

    /// Tell the solver which body to ignore when it probes for the ground. Without this the
    /// downward ray hits the player's own capsule and every foot plants at hip height.
    public void SetGroundProbe(Rid selfBody, uint mask)
    {
        _selfRid = selfBody;
        if (mask != 0) _groundMask = mask;
    }

    /// Solve one frame.
    ///
    /// `headWorld` is the headset. `leftHandWorld`/`rightHandWorld` are full transforms, not
    /// positions — the rotation is what makes a wrist a wrist, and dropping it is why hands used
    /// to point wherever the forearm happened to. `hipWorld` and the foot targets override the
    /// derived body when real trackers are present.
    ///
    /// Call this *after* `AvatarInstance.Animate` so the IK overrides the procedural walk cycle.
    ///
    /// `solveHead` exists so a head gesture can own the head bone for its duration. The headset
    /// pose is written every frame, so an additive nod would be overwritten the instant it was
    /// applied and the gesture would be invisible in VR.
    public void Solve(
        Transform3D headWorld,
        Transform3D leftHandWorld,
        Transform3D rightHandWorld,
        float dt,
        Vector3? hipWorld = null,
        Vector3? leftFootWorld = null,
        Vector3? rightFootWorld = null,
        bool solveHead = true,
        float playerArmReach = 0f,
        float planarSpeed = 0f,
        bool grounded = true
    )
    {
        if (!_valid) return;
        _playerArmReach = playerArmReach;

        var toSkel = _skel.GlobalTransform.AffineInverse();
        var headLocal = toSkel * headWorld;
        var leftHandLocal = toSkel * leftHandWorld;
        var rightHandLocal = toSkel * rightHandWorld;

        // ── 1-3. Torso: hips, spine, head ────────────────────────────────────────────
        bool solvedBody = SolveTorso(headLocal, hipWorld, toSkel, solveHead);

        // ── 4. Legs ──────────────────────────────────────────────────────────────────
        // Explicit foot trackers win outright. Otherwise the feet are planted in the world and
        // stepped, which is the only way an avatar can stand still while its owner leans, or
        // crouch without its shins telescoping through the floor.
        if (leftFootWorld.HasValue && _leftLeg.Ok)
            SolveLeg(_leftLeg, toSkel * leftFootWorld.Value, null);
        if (rightFootWorld.HasValue && _rightLeg.Ok)
            SolveLeg(_rightLeg, toSkel * rightFootWorld.Value, null);

        if (solvedBody && HasLegs && !leftFootWorld.HasValue && !rightFootWorld.HasValue
            && UI.DeviceProfile.Settings.VrFootIk)
            SolvePlantedFeet(dt, planarSpeed, grounded, toSkel);

        // ── 5. Arms ──────────────────────────────────────────────────────────────────
        if (_leftArm.Ok) SolveArmChain(_leftArm, _leftShoulder, leftHandLocal, _leftHandFromCtrl, +1f);
        if (_rightArm.Ok) SolveArmChain(_rightArm, _rightShoulder, rightHandLocal, _rightHandFromCtrl, -1f);
    }

    // ---------------------------------------------------------------- torso

    /// Place the hips from the head, bend the spine to bridge them, and pin the head to the
    /// headset. Returns false when the rig has no hips to work with, in which case the caller
    /// leaves the legs alone too — half a body solve looks worse than none.
    ///
    /// The hips are the *derived* joint here, and that is the whole trick. A headset reports one
    /// point on a body; everything below the neck has to be inferred from where that point is
    /// relative to where it would be if the player were standing up straight. Drop the head 40 cm
    /// and the only human interpretation is a crouch, so the hips drop with it and the knees
    /// absorb it. Lean the head forward and the hips trail, because that is what a spine does.
    private bool SolveTorso(Transform3D headSkel, Vector3? hipWorld, Transform3D toSkel, bool solveHead)
    {
        if (_hips < 0 || _head < 0) return false;

        // **Aim the head BONE, not the headset.** The headset is at the player's eyes and the
        // head bone is at the base of the skull, so putting the bone where the camera is stands
        // the entire skeleton up by the gap between them — 8–15 cm on a typical rig. That is not
        // subtle: it lifts the avatar's feet off the floor by a hand's width and undoes the play
        // space calibration, which had already put the *eyes* in the right place. Working back
        // from the eyes to the bone is the only version that agrees with the calibration.
        var eyeOffset = _avatar != null ? _avatar.EyeRestOffset : new Vector3(0f, 0.08f, 0f);
        var headTarget = headSkel.Origin - headSkel.Basis.GetRotationQuaternion() * eyeOffset;
        Vector3 hipsPos;

        if (hipWorld.HasValue)
        {
            // A real hip tracker outranks anything derived.
            hipsPos = (toSkel * hipWorld.Value);
        }
        else
        {
            // Vertical: the head's drop from its standing height is a crouch, and the hips take
            // it one-for-one until the legs run out of fold. Past that the player is crouching
            // lower than the avatar's legs can, and the hips simply stop — the alternative is
            // shins that pass through the floor.
            float headDrop = _restHeadPos.Y - headTarget.Y;
            float maxCrouch = Mathf.Max(0f, _restHipsY - MinHipsHeightFraction * _restHipsY);
            float crouch = Mathf.Clamp(headDrop, -MaxHipRise, maxCrouch);
            float hipsY = _restHipsY - crouch;

            // Horizontal: the hips follow the head part of the way. A person leaning over a table
            // does not drag their hips with them, and they do not leave them behind either — the
            // spine takes most of it and the pelvis shifts a little to keep the balance.
            var leanXZ = new Vector2(headTarget.X - _restHeadPos.X, headTarget.Z - _restHeadPos.Z);
            if (leanXZ.Length() > MaxHipLeanMetres / HipLeanFollow)
                leanXZ = leanXZ.Normalized() * (MaxHipLeanMetres / HipLeanFollow);
            hipsPos = new Vector3(_restHipsPos.X + leanXZ.X * HipLeanFollow, hipsY,
                                  _restHipsPos.Z + leanXZ.Y * HipLeanFollow);
        }

        // The spine bends along whatever line now runs hips → head.
        var spineDir = headTarget - hipsPos;
        if (spineDir.LengthSquared() < 1e-6f) return false;
        spineDir = spineDir.Normalized();
        var restSpineDir = _restHipsToHead.LengthSquared() > 1e-6f
            ? _restHipsToHead.Normalized() : Vector3.Up;

        var bend = SwingTo(restSpineDir, spineDir);

        // Twist: the head can face 90° off the body without the neck doing all of it. The head is
        // written absolutely below, so pre-twisting its parents does not change where the head
        // ends up — it only decides how much of the turn each vertebra shows. Without it the neck
        // is a swivel and the shoulders never follow a glance.
        float twist = YawBetween(spineDir, headSkel.Basis);
        twist = Mathf.Clamp(twist, -MaxSpineTwist, MaxSpineTwist);

        SetGlobalPosition(_hips, hipsPos);
        SetGlobalRotation(_hips, _restHipsRot);

        foreach (var (bone, rest, share) in _spineChain)
        {
            var partialBend = Quaternion.Identity.Slerp(bend, share);
            var partialTwist = new Quaternion(spineDir, twist * share);
            SetGlobalRotation(bone, partialTwist * partialBend * rest);
        }

        // Close the loop. The chain above is rigid, so whatever gap is left between where the
        // head bone landed and where the headset actually is, is a pure translation — adding it
        // to the hips closes it exactly, in one pass, with no iteration.
        var landed = _skel.GetBoneGlobalPose(_head).Origin;
        var residual = headTarget - landed;
        if (residual.LengthSquared() > 1e-8f)
        {
            // Only ever a correction, never a teleport: an avatar whose legs cannot reach should
            // stretch its spine a little, not detach its pelvis and float away.
            if (residual.Length() > MaxHipCorrection)
                residual = residual.Normalized() * MaxHipCorrection;
            SetGlobalPosition(_hips, hipsPos + residual);
        }

        if (solveHead) SetGlobalRotation(_head, headSkel.Basis.GetRotationQuaternion());
        return true;
    }

    /// How low the hips may go, as a fraction of their standing height. A deep human squat puts
    /// the pelvis at roughly a third of standing hip height; below that the knees have nowhere
    /// left to fold and the shins start passing through the floor.
    private const float MinHipsHeightFraction = 0.34f;

    /// How far the hips may rise above standing. Small and non-zero: a player on tiptoe or
    /// jumping in place should lift a little, but the avatar must not become a stilt.
    private const float MaxHipRise = 0.08f;

    /// Resting knee bend, as a fraction of leg length.
    ///
    /// **A bind-pose humanoid stands with its legs dead straight**, so the hip-to-foot distance
    /// already equals the leg's full reach and the leg has no slack whatsoever. A foot planted
    /// anywhere except exactly under the hip is then geometrically unreachable, the solver leaves
    /// it short, and the foot gets dragged along by the body — the planting is defeated by the
    /// anatomy rather than by the step logic. Locked knees are also the other classic tell of an
    /// IK rig.
    ///
    /// A few centimetres of bend buys a surprising amount of room: the reachable lateral offset
    /// goes as sqrt(2·L·drop), so ~3.4 cm on a 75 cm leg allows over 20 cm of lean.
    ///
    /// **It cannot be applied by lowering the hips here.** `SolveTorso` ends by correcting the
    /// hips so the head bone lands exactly on the headset, and that correction cancels any hip
    /// offset introduced earlier — exactly, to the last bit. The bend has to come from the PLAY
    /// SPACE sitting a little lower over the avatar instead, which is what
    /// `VrPlayer.ApplyHeightOffset` does with `StandingCrouchMetres`.
    private const float StandingKneeBend = 0.045f;

    /// How far the play space must sit below the avatar's nominal eye height to leave the knees
    /// softly bent rather than locked. See `StandingKneeBend`.
    public float StandingCrouchMetres => _legReach * StandingKneeBend;

    private const float HipLeanFollow = 0.32f;
    private const float MaxHipLeanMetres = 0.22f;
    private const float MaxHipCorrection = 0.35f;
    private static readonly float MaxSpineTwist = Mathf.DegToRad(75f);

    /// The signed yaw of `basis`'s forward about `axis`, relative to the skeleton's own forward.
    /// Used to work out how far the head is turned off the body so the turn can be shared out.
    private static float YawBetween(Vector3 axis, Basis basis)
    {
        var bodyFwd = Vector3.Forward;       // skeleton space: the rig faces −Z
        var headFwd = -basis.Z;

        // Flatten both onto the plane the spine actually twists about.
        bodyFwd = (bodyFwd - axis * bodyFwd.Dot(axis));
        headFwd = (headFwd - axis * headFwd.Dot(axis));
        if (bodyFwd.LengthSquared() < 1e-6f || headFwd.LengthSquared() < 1e-6f) return 0f;
        bodyFwd = bodyFwd.Normalized();
        headFwd = headFwd.Normalized();

        float cos = Mathf.Clamp(bodyFwd.Dot(headFwd), -1f, 1f);
        float angle = Mathf.Acos(cos);
        return bodyFwd.Cross(headFwd).Dot(axis) < 0f ? -angle : angle;
    }

    // ---------------------------------------------------------------- legs

    /// Keep both feet where they were put, and step them when the body has walked away.
    ///
    /// **This is what stops the feet skating.** The old rig hung the avatar off the headset's XZ
    /// and played a canned walk cycle, so the feet were wherever the animation said and the whole
    /// body slid to follow the head — lean an inch and your avatar shuffled an inch, standing
    /// still. Planting the feet in *world* space inverts that: the feet are the fixed thing, the
    /// body moves over them, and a step happens when the geometry demands one.
    ///
    /// Only one foot is ever in the air. That single rule is most of what makes procedural
    /// stepping read as walking rather than as hovering.
    private void SolvePlantedFeet(float dt, float planarSpeed, bool grounded, Transform3D toSkel)
    {
        var skelXform = _skel.GlobalTransform;
        var hipsSkel = _skel.GetBoneGlobalPose(_hips).Origin;

        // Where each foot wants to be: under the hips, at the rig's own stance width, on the
        // ground. Computed in skeleton space then taken to the world, so it turns with the body.
        var want = new Vector3[2];
        for (int i = 0; i < 2; i++)
        {
            float side = i == 0 ? -1f : +1f;
            var local = new Vector3(hipsSkel.X + side * _stanceHalfWidth, 0f, hipsSkel.Z);
            want[i] = skelXform * local;
        }

        if (!_feetInitialised)
        {
            for (int i = 0; i < 2; i++)
            {
                _footPlant[i] = GroundAt(want[i], skelXform.Basis.GetEuler().Y, out _footNormal[i], out _);
                _stepT[i] = 1f;
                _footYaw[i] = skelXform.Basis.GetEuler().Y;
            }
            _feetInitialised = true;
        }

        float bodyYaw = skelXform.Basis.GetEuler().Y;
        bool anyStepping = _stepT[0] < 1f || _stepT[1] < 1f;
        float stepSeconds = Mathf.Lerp(StepSecondsWalk, StepSecondsRun,
                                       Mathf.Clamp(planarSpeed / 4.2f, 0f, 1f));

        for (int k = 0; k < 2; k++)
        {
            int i = (k + _lastSteppedFoot + 1) % 2; // alternate foot priority so neither foot is starved
            if (_stepT[i] < 1f)
            {
                // Mid-step: fly the foot to its target along a low arc.
                _stepT[i] = Mathf.Min(1f, _stepT[i] + dt / Mathf.Max(0.05f, stepSeconds));
                float e = Ease(_stepT[i]);
                var flat = _stepFrom[i].Lerp(_stepTo[i], e);
                float lift = Mathf.Sin(_stepT[i] * Mathf.Pi) * StepHeightMetres;
                _footPlant[i] = flat + Vector3.Up * lift;
                _footYaw[i] = Mathf.LerpAngle(_stepYawFrom[i], _stepYawTo[i], e);
                continue;
            }

            if (anyStepping) continue; // never both feet in the air

            // In the air the legs have nothing to stand on, so they follow the hips directly
            // rather than reaching for a floor that is not under them.
            if (!grounded)
            {
                _footPlant[i] = GroundAt(want[i], bodyYaw, out _footNormal[i], out _);
                _footYaw[i] = bodyYaw;
                continue;
            }

            var legHere = i == 0 ? _leftLeg : _rightLeg;
            float drift = new Vector2(_footPlant[i].X - want[i].X, _footPlant[i].Z - want[i].Z).Length();
            float yawErr = Mathf.Abs(Mathf.AngleDifference(_footYaw[i], bodyYaw));
            // A leg that cannot reach its own plant has to move whatever the thresholds say,
            // or the avatar does the splits down a staircase.
            float span = _skel.GetBoneGlobalPose(legHere.Upper).Origin.DistanceTo(toSkel * _footPlant[i]);

            if (drift > StepTriggerMetres || yawErr > Mathf.DegToRad(StepTriggerDegrees)
                || span > (legHere.L1 + legHere.L2) * 0.98f)
            {
                _stepFrom[i] = _footPlant[i];
                _stepTo[i] = GroundAt(want[i], bodyYaw, out _footNormal[i], out _);
                _stepYawFrom[i] = _footYaw[i];
                _stepYawTo[i] = bodyYaw;
                _stepT[i] = 0f;
                _lastSteppedFoot = i;
                anyStepping = true;
            }
        }

        for (int i = 0; i < 2; i++)
        {
            var leg = i == 0 ? _leftLeg : _rightLeg;
            if (!leg.Ok) continue;
            SolveLeg(leg, toSkel * _footPlant[i],
                     FootRotation(_footNormal[i], _footYaw[i], bodyYaw, skelXform, leg));
        }
    }

    /// Ease-in-out. A foot that leaves and lands at constant speed looks like it is on a conveyor.
    private static float Ease(float t) => t * t * (3f - 2f * t);

    /// Multi-point ground and slope probe. Probes ankle center, toe, and heel to detect stairs,
    /// ledges, and slopes, returning the ankle bone target height and surface normal.
    private Vector3 GroundAt(Vector3 point, float yaw, out Vector3 normal, out float toeLift)
    {
        normal = Vector3.Up;
        toeLift = 0f;
        var space = _skel.GetWorld3D()?.DirectSpaceState;
        if (space == null) return point + Vector3.Up * _ankleHeight;

        var fwd = new Vector3(-Mathf.Sin(yaw), 0f, -Mathf.Cos(yaw)).Normalized();

        // 3-point sole contact probe: Center ankle, Toe (+10cm fwd), Heel (-8cm fwd)
        var pCenter = point;
        var pToe = point + fwd * 0.10f;
        var pHeel = point - fwd * 0.08f;

        bool hitC = ProbeFloor(space, pCenter, out var posC, out var normC);
        bool hitT = ProbeFloor(space, pToe, out var posT, out var normT);
        bool hitH = ProbeFloor(space, pHeel, out var posH, out var normH);

        if (!hitC && !hitT && !hitH)
            return point + Vector3.Up * _ankleHeight;

        Vector3 solePos = hitC ? posC : (hitT ? posT : posH);
        Vector3 avgNormal = Vector3.Zero;
        int normalCount = 0;
        if (hitC) { avgNormal += normC; normalCount++; }
        if (hitT) { avgNormal += normT; normalCount++; }
        if (hitH) { avgNormal += normH; normalCount++; }
        normal = normalCount > 0 ? (avgNormal / normalCount).Normalized() : Vector3.Up;

        // If toe and heel landed on different heights (stair step or slope):
        if (hitT && hitH)
        {
            float dy = posT.Y - posH.Y;
            float pitchAngle = Mathf.Atan2(dy, 0.18f);
            pitchAngle = Mathf.Clamp(pitchAngle, -Mathf.DegToRad(45f), Mathf.DegToRad(45f));
            
            var right = fwd.Cross(Vector3.Up).Normalized();
            normal = normal.Rotated(right, pitchAngle * 0.5f).Normalized();

            if (dy > 0.03f)
                toeLift = Mathf.Clamp(dy * 0.5f, 0f, 0.08f);
        }

        return solePos + Vector3.Up * (_ankleHeight + toeLift * 0.5f);
    }

    private bool ProbeFloor(PhysicsDirectSpaceState3D space, Vector3 probePos, out Vector3 hitPos, out Vector3 hitNormal)
    {
        hitPos = probePos;
        hitNormal = Vector3.Up;
        var q = PhysicsRayQueryParameters3D.Create(
            probePos + Vector3.Up * GroundProbeUp, probePos + Vector3.Down * GroundProbeDown, _groundMask);
        if (_selfRid.IsValid) q.Exclude = new Godot.Collections.Array<Rid> { _selfRid };

        var hit = space.IntersectRay(q);
        if (hit.Count == 0) return false;
        hitPos = hit["position"].AsVector3();
        hitNormal = hit["normal"].AsVector3();
        return true;
    }

    /// How far the foot bone sits above the sole in the bind pose. Taken from the rig, so a
    /// chunky boot and a bare foot each get their own.
    private readonly float _ankleHeight;

    private const float GroundProbeUp = 0.45f;
    private const float GroundProbeDown = 1.2f;

    /// The skeleton-space orientation to plant a foot at: flat on the surface it is standing on,
    /// toes pointing where the foot is facing.
    private static Quaternion FootRotation(Vector3 worldNormal, float footYaw, float bodyYaw,
                                           Transform3D skelXform, in Leg leg)
    {
        // The ground normal, expressed in the skeleton's own space.
        var n = skelXform.Basis.Inverse() * worldNormal;
        if (n.LengthSquared() < 1e-6f) n = Vector3.Up;
        else n = n.Normalized();

        // Limit tilt to natural anatomical ankle limits (up to 45° pitch, 28° roll)
        float maxTilt = Mathf.DegToRad(45f);
        float tiltAngle = Mathf.Acos(Mathf.Clamp(Vector3.Up.Dot(n), -1f, 1f));
        if (tiltAngle > maxTilt)
        {
            var axis = Vector3.Up.Cross(n).Normalized();
            n = Vector3.Up.Rotated(axis, maxTilt);
        }

        var tilt = SwingTo(Vector3.Up, n);
        var yawDelta = new Quaternion(Vector3.Up, Mathf.AngleDifference(bodyYaw, footYaw));
        return tilt * yawDelta * leg.RestRotFoot;
    }

    /// Analytic two-bone leg IK. Places the knee using the law of cosines, then swings each
    /// segment's rest direction onto the solved direction. Knees bend forward.
    private void SolveLeg(in Leg leg, Vector3 targetSkel, Quaternion? footRot)
    {
        var hip = _skel.GetBoneGlobalPose(leg.Upper).Origin;

        var toTarget = targetSkel - hip;
        float dist = toTarget.Length();
        if (dist < 1e-4f) return;

        // Clamp inside full extension — knees do not hyperextend either. See `MaxExtension`.
        float d = Mathf.Clamp(dist, Mathf.Abs(leg.L1 - leg.L2) + 1e-3f, MaxExtension(leg.L1, leg.L2));
        var aim = toTarget / dist;

        // Hip-to-knee angle.
        float cosA = (leg.L1 * leg.L1 + d * d - leg.L2 * leg.L2) / (2f * leg.L1 * d);
        float a = Mathf.Acos(Mathf.Clamp(cosA, -1f, 1f));

        // Leg pole: knees should always bend forward (-Z is forward in skeleton space).
        var pole = new Vector3(0, 0, -1f);
        var axis = aim.Cross(pole);
        if (axis.LengthSquared() < 1e-6f) axis = aim.Cross(Vector3.Right);
        axis = axis.Normalized();

        var upperDir = aim.Rotated(axis, a).Normalized();
        var knee = hip + upperDir * leg.L1;
        var lowerDir = (targetSkel - knee);
        if (lowerDir.LengthSquared() < 1e-8f) return;
        lowerDir = lowerDir.Normalized();

        var upperRot = SwingTo(leg.RestDirUpper, upperDir) * leg.RestRotUpper;
        var lowerRot = SwingTo(leg.RestDirLower, lowerDir) * leg.RestRotLower;

        SetGlobalRotation(leg.Upper, upperRot);
        _skel.SetBonePoseRotation(leg.Lower, upperRot.Inverse() * lowerRot);

        var footGlobalRot = footRot ?? (SwingTo(leg.RestDirLower, lowerDir) * leg.RestRotFoot);
        if (leg.Foot >= 0)
        {
            _skel.SetBonePoseRotation(leg.Foot, lowerRot.Inverse() * footGlobalRot);
        }

        if (leg.Toes >= 0)
        {
            var toeRest = _skel.GetBoneGlobalRest(leg.Toes).Basis.GetRotationQuaternion();
            var toeRot = footGlobalRot * toeRest;
            _skel.SetBonePoseRotation(leg.Toes, footGlobalRot.Inverse() * toeRot);
        }
    }

    // ---------------------------------------------------------------- arms

    /// How far the *player's* arm can reach, in metres. Zero disables the remap below.
    private float _playerArmReach;

    /// Map the player's reach onto the avatar's, about the shoulder.
    ///
    /// **The avatar's arms are not the player's arms, and pretending otherwise leaves the hands
    /// permanently behind.** Stylised humanoids — which is nearly every avatar people actually
    /// wear — have short limbs: a 1.57 m rig measures about 43 cm from shoulder to wrist, while an
    /// adult holding the controllers reaches close to 60 cm. Feed the raw controller position to a
    /// two-bone solver and it does the only thing it can, which is extend fully and stop 15 cm
    /// short. Every reach ends with the avatar's hands trailing the player's, the elbows locked
    /// straight, and — because the arm is pinned at full extension — no elbow bend at all through
    /// the whole outer half of the working volume. That reads as stiff, laggy arms, and it is not
    /// a solver bug: the target is simply outside the arm.
    ///
    /// So the reach is rescaled rather than clipped. The direction from the shoulder is preserved
    /// exactly — point at something and the avatar points at it — while the distance is scaled by
    /// the ratio of the two arm lengths, so full player extension is full avatar extension and
    /// everything in between lands proportionally. This is the same trick as scaling the world to
    /// the avatar, but confined to the arms, so it cannot disturb the height calibration or make
    /// locomotion feel different.
    ///
    /// The height calibration already aligns the avatar's eyes with the headset, which puts its
    /// shoulders roughly where the player's are, so the shoulder is a sound pivot.
    private Vector3 ScaleToArm(in Arm arm, Vector3 shoulder, Vector3 targetSkel)
    {
        if (_playerArmReach <= 0.05f) return targetSkel;

        float avatarReach = arm.L1 + arm.L2;
        if (avatarReach <= 0.01f) return targetSkel;

        float ratio = avatarReach / _playerArmReach;
        // Only ever shrink. A player shorter in the arms than their avatar can already reach
        // everywhere the avatar can, and stretching their input would send the avatar's hands
        // further out than their own — which is worse than falling short, because it breaks the
        // one thing that has to hold: what you touch is what your avatar touches.
        if (ratio >= 1f) return targetSkel;

        return shoulder + (targetSkel - shoulder) * ratio;
    }

    /// Shoulder, then arm. Split out because the clavicle has to move *before* the arm solves —
    /// it changes where the shoulder joint is, which is the origin the whole two-bone solve
    /// measures from.
    private void SolveArmChain(in Arm arm, int shoulderBone, Transform3D handSkel,
                               Quaternion handFromCtrl, float poleSign)
    {
        SolveShoulder(arm, shoulderBone, handSkel.Origin);
        SolveArm(arm, handSkel, handFromCtrl, poleSign);
    }

    /// Lift the clavicle toward a raised or far-forward hand.
    ///
    /// Reach for something above your head and your shoulder rises several centimetres before
    /// your arm has finished moving; reach across your body and it rolls forward. An avatar with
    /// a rigid clavicle can only get the hand there by socketing the humerus at an angle no
    /// shoulder makes, which is the "chicken wing" every fixed-clavicle VR rig has. It is a small
    /// rotation with a disproportionate effect on whether an arm looks attached.
    private void SolveShoulder(in Arm arm, int shoulderBone, Vector3 handSkel)
    {
        if (shoulderBone < 0) return;

        var shoulderPos = _skel.GetBoneGlobalPose(arm.Upper).Origin;
        var delta = handSkel - shoulderPos;
        float reach = Mathf.Max(1e-3f, arm.L1 + arm.L2);

        float side = Mathf.Sign(arm.RestDirUpper.X);
        if (side == 0f) side = 1f;

        // Normalised reach per axis: vertical shrug, forward reach, and reaching across the torso
        float up = Mathf.Clamp(delta.Y / reach, -0.4f, 1f);
        float fwd = Mathf.Clamp(-delta.Z / reach, 0f, 1f);
        float cross = Mathf.Clamp(-delta.X * side / reach, -0.5f, 1f);

        // Scapulohumeral elevation & protraction rhythm (1:2 ratio)
        float shrugAngle = up * ShoulderLift;
        float protractAngle = (fwd * 0.65f + cross * 0.35f) * ShoulderRoll;
        if (Mathf.Abs(shrugAngle) + Mathf.Abs(protractAngle) < Mathf.DegToRad(0.5f)) return;

        var rest = _skel.GetBoneGlobalRest(shoulderBone).Basis.GetRotationQuaternion();
        // Raise about the forward axis (shrug), roll about the vertical (protraction). Both are
        // signed by which side of the body this arm is on, which `RestDirUpper` already knows.
        var shrug = new Quaternion(Vector3.Forward, -shrugAngle * side);
        var protract = new Quaternion(Vector3.Up, protractAngle * side);
        SetGlobalRotation(shoulderBone, shrug * protract * rest);
    }

    private static readonly float ShoulderLift = Mathf.DegToRad(26f);
    private static readonly float ShoulderRoll = Mathf.DegToRad(15f);

    /// Analytic two-bone IK. Places the elbow using the law of cosines, then swings each
    /// segment's rest direction onto the solved direction, and finally sets the wrist from the
    /// controller's own orientation.
    ///
    /// `poleSign` pushes the elbow outward and backward — +1 for the left arm, -1 for the
    /// right — so elbows bend like elbows instead of snapping through the torso.
    private void SolveArm(in Arm arm, Transform3D handSkel, Quaternion handFromCtrl, float poleSign)
    {
        var shoulder = _skel.GetBoneGlobalPose(arm.Upper).Origin;
        var targetSkel = ScaleToArm(arm, shoulder, handSkel.Origin);

        var toTarget = targetSkel - shoulder;
        float dist = toTarget.Length();
        if (dist < 1e-4f) return;

        // Clamp inside full extension. Not merely to avoid the degenerate cosine solve at exactly
        // `reach` — a real elbow does not straighten to 180° either. Overte's IK caps it at
        // `MAX_ELBOW_ANGLE = 11π/12` (165°) for the same reason: a limb locked dead straight is
        // both anatomically wrong and the pose where the bend axis is least well-defined, so the
        // elbow flicks around under noise exactly when the arm is fully extended.
        float d = Mathf.Clamp(dist, Mathf.Abs(arm.L1 - arm.L2) + 1e-3f, MaxExtension(arm.L1, arm.L2));
        var aim = toTarget / dist;

        // Shoulder-to-elbow angle off the straight line to the target.
        float cosA = (arm.L1 * arm.L1 + d * d - arm.L2 * arm.L2) / (2f * arm.L1 * d);
        float a = Mathf.Acos(Mathf.Clamp(cosA, -1f, 1f));

        var pole = ComputeDynamicPole(shoulder, targetSkel, handSkel.Basis, poleSign);
        var axis = aim.Cross(pole);
        if (axis.LengthSquared() < 1e-6f) axis = aim.Cross(Vector3.Up);
        if (axis.LengthSquared() < 1e-6f) axis = aim.Cross(Vector3.Right);
        axis = axis.Normalized();

        var upperDir = aim.Rotated(axis, a).Normalized();
        var elbow = shoulder + upperDir * arm.L1;
        var lowerDir = (targetSkel - elbow);
        if (lowerDir.LengthSquared() < 1e-8f) return;
        lowerDir = lowerDir.Normalized();

        var upperRot = SwingTo(arm.RestDirUpper, upperDir) * arm.RestRotUpper;
        var lowerRot = SwingTo(arm.RestDirLower, lowerDir) * arm.RestRotLower;

        SetGlobalRotation(arm.Upper, upperRot);
        // The lower arm's parent is the upper arm, whose global rotation we just solved — use
        // it directly rather than re-querying a skeleton that may not have flushed yet.
        _skel.SetBonePoseRotation(arm.Lower, upperRot.Inverse() * lowerRot);

        // ── The wrist ────────────────────────────────────────────────────────────────
        // **The hand takes the controller's rotation, not the forearm's.** This used to derive
        // the hand purely from the direction of the lower arm, which meant the avatar's wrist
        // physically could not rotate: turning a controller palm-up, giving a thumbs-up, waving,
        // pointing at something, holding a mug level — every one of them left the avatar's hand
        // rigidly aligned with its own forearm. In a social space the hands are most of what
        // people read off each other, and a wrist that never turns is the single loudest tell
        // that an avatar is being puppeted rather than worn.
        if (arm.Hand >= 0)
        {
            var handGlobal = handSkel.Basis.GetRotationQuaternion() * handFromCtrl;
            // Clamp against the forearm. A tracked controller can be rolled 180° past what a
            // wrist does, and honouring that snaps the hand round backwards; a real one runs out
            // and takes the forearm with it, which the twist limit approximates.
            var anatomical = SwingTo(arm.RestDirLower, lowerDir) * arm.RestRotHand;
            handGlobal = LimitTwist(anatomical, handGlobal, lowerDir, MaxWristTwist);
            _skel.SetBonePoseRotation(arm.Hand, lowerRot.Inverse() * handGlobal);
        }
    }

    private static readonly float MaxWristTwist = Mathf.DegToRad(100f);

    /// Limit wrist twist (axial pronation/supination along forearm axis) to anatomical range
    /// using twist-swing decomposition without penalizing forearm swing.
    private static Quaternion LimitTwist(Quaternion reference, Quaternion desired, Vector3 axis, float limit)
    {
        var delta = (desired * reference.Inverse()).Normalized();
        var twistAxis = new Vector3(delta.X, delta.Y, delta.Z);
        float proj = twistAxis.Dot(axis);
        var twist = new Quaternion(axis.X * proj, axis.Y * proj, axis.Z * proj, delta.W);
        if (twist.LengthSquared() < 1e-6f) return desired;
        twist = twist.Normalized();

        float twistAngle = 2f * Mathf.Acos(Mathf.Clamp(Mathf.Abs(twist.W), -1f, 1f));
        if (twistAngle <= limit) return desired;

        float t = limit / Mathf.Max(twistAngle, 1e-4f);
        var clampedTwist = Quaternion.Identity.Slerp(twist, t).Normalized();
        var swing = delta * twist.Inverse();
        return (swing * clampedTwist * reference).Normalized();
    }

    /// Compute a pole target that adapts to where the hand is and how it is rotated.
    ///
    /// The static part is anatomy: elbows sit outward and back, drop when the hand goes overhead,
    /// and swing wide when the hand goes behind the body.
    ///
    /// The dynamic part is the wrist. **A human elbow follows the palm** — rotate your palm up
    /// and your elbow tucks against your ribs, rotate it down and the elbow swings out. That
    /// coupling is why an IK arm with a fixed pole looks robotic no matter how good the position
    /// tracking is: the elbow is the one joint the player never sees moving on themselves but
    /// always notices being wrong on someone else.
    private static Vector3 ComputeDynamicPole(Vector3 shoulder, Vector3 target, Basis handBasis,
                                              float poleSign)
    {
        var delta = target - shoulder;

        // Base pole: outward and backward.
        float outward = poleSign;
        float backward = -0.65f;
        float downward = 0f;

        // If the hand is above the shoulder, bias the elbow downward.
        if (delta.Y > 0.05f)
        {
            float aboveness = Mathf.Clamp(delta.Y / 0.4f, 0f, 1f);
            downward = -0.8f * aboveness;
            backward = Mathf.Lerp(-0.65f, -0.2f, aboveness);
        }

        // If the hand is behind the body (positive Z in skeleton space), push elbow outward.
        if (delta.Z > 0.05f)
        {
            float behindness = Mathf.Clamp(delta.Z / 0.3f, 0f, 1f);
            outward = poleSign * Mathf.Lerp(1f, 1.5f, behindness);
            backward = Mathf.Lerp(-0.65f, 0f, behindness);
        }

        var anatomical = new Vector3(outward, downward, backward).Normalized();

        // The back of the hand is the controller's +Y. Blend the elbow toward it, so the pole
        // rides the wrist the way a forearm's two bones really do.
        var handUp = handBasis.Y;
        if (handUp.LengthSquared() < 1e-6f) return anatomical;
        var blended = anatomical.Lerp(handUp.Normalized(), WristPoleBlend);
        return blended.LengthSquared() < 1e-6f ? anatomical : blended.Normalized();
    }

    private const float WristPoleBlend = 0.45f;

    // ---------------------------------------------------------------- primitives

    /// The furthest a two-bone limb may be asked to span, given a hinge that stops short of
    /// straight. Law of cosines on the joint angle: c² = a² + b² − 2ab·cos(θ).
    private static float MaxExtension(float l1, float l2)
        => Mathf.Sqrt(l1 * l1 + l2 * l2 - 2f * l1 * l2 * Mathf.Cos(MaxJointAngle));

    /// How far a knee or elbow may open. 165°, matching Overte's `MAX_ELBOW_ANGLE`.
    private static readonly float MaxJointAngle = Mathf.DegToRad(165f);

    /// Shortest-arc rotation taking `from` onto `to`, guarding the antiparallel case where the
    /// cross product vanishes and the axis is ambiguous.
    private static Quaternion SwingTo(Vector3 from, Vector3 to)
    {
        float dot = from.Dot(to);
        if (dot > 0.9999f) return Quaternion.Identity;
        if (dot < -0.9999f)
        {
            var ortho = from.Cross(Vector3.Up);
            if (ortho.LengthSquared() < 1e-6f) ortho = from.Cross(Vector3.Right);
            return new Quaternion(ortho.Normalized(), Mathf.Pi);
        }
        return new Quaternion(from, to);
    }

    /// Write a skeleton-space rotation onto a bone as the local pose rotation Godot expects.
    private void SetGlobalRotation(int bone, Quaternion globalRot)
    {
        int parent = _skel.GetBoneParent(bone);
        var parentRot = parent >= 0
            ? _skel.GetBoneGlobalPose(parent).Basis.GetRotationQuaternion()
            : Quaternion.Identity;
        _skel.SetBonePoseRotation(bone, (parentRot.Inverse() * globalRot).Normalized());
    }

    /// Write a skeleton-space *position* onto a bone, as the parent-relative pose position Godot
    /// expects.
    ///
    /// The hips are usually the root bone, where local and skeleton space coincide — which is why
    /// assigning a skeleton-space position directly appeared to work. Plenty of rigs put an
    /// `Armature`/`Root` bone above the hips though, and on those the hips would be placed in the
    /// wrong space entirely: a crouch would translate the pelvis by the root's own offset instead
    /// of downward, and the whole body solve would come apart on exactly the avatars nobody
    /// happened to test with.
    private void SetGlobalPosition(int bone, Vector3 globalPos)
    {
        int parent = _skel.GetBoneParent(bone);
        if (parent < 0) { _skel.SetBonePosePosition(bone, globalPos); return; }
        _skel.SetBonePosePosition(bone, _skel.GetBoneGlobalPose(parent).AffineInverse() * globalPos);
    }
}
