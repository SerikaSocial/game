using System;
using System.Collections.Generic;
using Godot;

namespace SerikaSocial.Avatar;

/// Retargets Mixamo animation clips onto a VRM avatar skeleton by humanoid role.
///
/// Uses model-space (global) rest-compensated orientation transfer.
/// This translates rotations in character-root space (+Y up, -Z forward, +X right)
/// regardless of internal bone local axis variations between Mixamo and VRM rigs.
public sealed partial class AnimRetargeter : Node
{
    private Skeleton3D _srcSkeleton;
    private AnimationPlayer _srcPlayer;

    private readonly Skeleton3D _target;
    private readonly Dictionary<string, int> _targetRoleToBone;

    // source bone index per role
    private readonly Dictionary<string, int> _srcRoleToBone = new();
    // source global rest rotation per role
    private readonly Dictionary<string, Quaternion> _srcGlobalRestRot = new();
    // target global rest rotation per role
    private readonly Dictionary<string, Quaternion> _tgtGlobalRestRot = new();

    // Roles ordered hierarchy-topological (hips first, then spine, limbs)
    private readonly List<string> _orderedRoles = new();

    // Rotation aligning the SOURCE rig's model frame to the TARGET's. Resolved lazily from an
    // animated source frame (never the source bind pose — see the note in Create).
    private Quaternion _alignR = Quaternion.Identity;
    private Basis _tgtFrame = Basis.Identity;
    private bool _haveTgtFrame;
    private bool _alignResolved;

    // Ground clamp: keeps the pelvis at the source's hips-above-feet height, rig-scaled.
    private bool _canGroundClamp;
    private float _tgtHipsHeight;
    private float _srcHipsHeight;
    private int _hipsIdx = -1;
    private Vector3 _hipsRestLocal;

    // state machine
    public enum State
    {
        Idle, Walk, WalkBack, StrafeLeft, StrafeRight, Run, Sprint,
        CrouchIdle, CrouchWalk, Jump, Fall, Land,
        // Emotes — all reachable from the Action menu (R), no dedicated key binds.
        Sit, Dance, DanceCharleston, Bow, Greeting, Victory, VictoryFist,
        Meditate, Sleeping, Confused, Dizzy, Yes, Reject, Backflip, Shivering,
        None,
    }
    private State _state = State.None;
    private State _prevState = State.None;
    private float _blend = 1f;
    private float _blendSpeed = 10f;

    /// Clip names as they appear in `locomotion.glb`. Matching is exact — a missing entry
    /// means the state falls back to the procedural animation rather than a wrong clip.
    private static readonly Dictionary<State, string> StateClip = new()
    {
        { State.Idle,             "Idle_A" },
        { State.Walk,             "Walk" },
        { State.WalkBack,         "Walk_Backwards" },
        { State.StrafeLeft,       "Strafe_left" },
        { State.StrafeRight,      "Strafe_right" },
        { State.Run,              "Run_Anime" },
        { State.Sprint,           "Sprint" },
        { State.CrouchIdle,       "Crouch_Idle" },
        { State.CrouchWalk,       "Crouch_Walk" },
        { State.Jump,             "Jump_Start" },
        { State.Fall,             "Jump_air" },
        { State.Land,             "Jump_Land" },
        { State.Sit,              "Sitting_Idle" },
        { State.Dance,            "Dance_Simple" },
        { State.DanceCharleston,  "Dance_Charleston" },
        { State.Bow,              "Bow" },
        { State.Greeting,         "Greeting" },
        { State.Victory,          "Victory" },
        { State.VictoryFist,      "Victory_Fist_Pump" },
        { State.Meditate,         "Meditate" },
        { State.Sleeping,         "Sleeping" },
        { State.Confused,         "Confused" },
        { State.Dizzy,            "Dizzy" },
        { State.Yes,              "Yes" },
        { State.Reject,           "Reject" },
        { State.Backflip,         "Backflip" },
        { State.Shivering,        "Shivering" },
    };

    // Mixamo bone name → Serika humanoid role
    private static readonly Dictionary<string, string> MixamoRoleMap = new()
    {
        { "mixamorigHips", "hips" },
        { "mixamorigSpine", "spine" },
        { "mixamorigSpine1", "spine" },
        { "mixamorigSpine2", "chest" },
        { "mixamorigNeck", "neck" },
        { "mixamorigHead", "head" },
        { "mixamorigLeftShoulder", "leftShoulder" },
        { "mixamorigLeftArm", "leftUpperArm" },
        { "mixamorigLeftForeArm", "leftLowerArm" },
        { "mixamorigLeftHand", "leftHand" },
        { "mixamorigRightShoulder", "rightShoulder" },
        { "mixamorigRightArm", "rightUpperArm" },
        { "mixamorigRightForeArm", "rightLowerArm" },
        { "mixamorigRightHand", "rightHand" },
        { "mixamorigLeftUpLeg", "leftUpperLeg" },
        { "mixamorigLeftLeg", "leftLowerLeg" },
        { "mixamorigLeftFoot", "leftFoot" },
        { "mixamorigLeftToeBase", "leftToes" },
        { "mixamorigRightUpLeg", "rightUpperLeg" },
        { "mixamorigRightLeg", "rightLowerLeg" },
        { "mixamorigRightFoot", "rightFoot" },
        { "mixamorigRightToeBase", "rightToes" },
    };

    /// The humanoid chain: each bone's direction is defined by where its child sits. Bones with
    /// no entry here (head, hands, toes) are leaves — they inherit their parent's orientation.
    private static readonly Dictionary<string, string> ChildOf = new()
    {
        { "hips", "spine" },
        { "spine", "chest" },
        { "chest", "neck" },
        { "neck", "head" },
        { "leftShoulder", "leftUpperArm" },
        { "leftUpperArm", "leftLowerArm" },
        { "leftLowerArm", "leftHand" },
        { "rightShoulder", "rightUpperArm" },
        { "rightUpperArm", "rightLowerArm" },
        { "rightLowerArm", "rightHand" },
        { "leftUpperLeg", "leftLowerLeg" },
        { "leftLowerLeg", "leftFoot" },
        { "leftFoot", "leftToes" },
        { "rightUpperLeg", "rightLowerLeg" },
        { "rightLowerLeg", "rightFoot" },
        { "rightFoot", "rightToes" },
    };

    // Roles that have a usable bone direction, and that direction in target model space at rest.
    private readonly List<string> _dirRoles = new();
    private readonly Dictionary<string, Vector3> _tgtRestDir = new();

    /// Shortest-arc rotation taking unit vector `from` onto unit vector `to`.
    private static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        float d = from.Dot(to);
        if (d > 0.99999f) return Quaternion.Identity;
        if (d < -0.99999f)
        {
            // Opposed: any perpendicular axis gives a valid 180° turn.
            Vector3 axis = from.Cross(Vector3.Up);
            if (axis.LengthSquared() < 1e-8f) axis = from.Cross(Vector3.Right);
            return new Quaternion(axis.Normalized(), Mathf.Pi);
        }
        Vector3 c = from.Cross(to);
        var q = new Quaternion(c.X, c.Y, c.Z, 1f + d);
        return q.Normalized();
    }

    /// Orthonormal frame from a "right" and "up" hint (up wins; right is orthogonalised).
    private static Basis FrameFrom(Vector3 right, Vector3 up)
    {
        Vector3 u = up.Normalized();
        Vector3 r = right - u * right.Dot(u);
        if (r.LengthSquared() < 1e-8f) r = Vector3.Right;
        r = r.Normalized();
        Vector3 f = r.Cross(u).Normalized();
        return new Basis(r, u, f);
    }

    private static readonly string[] RoleHierarchyOrder = new[]
    {
        "hips", "spine", "chest", "neck", "head",
        "leftShoulder", "leftUpperArm", "leftLowerArm", "leftHand",
        "rightShoulder", "rightUpperArm", "rightLowerArm", "rightHand",
        "leftUpperLeg", "leftLowerLeg", "leftFoot", "leftToes",
        "rightUpperLeg", "rightLowerLeg", "rightFoot", "rightToes"
    };

    private AnimRetargeter(Skeleton3D target, Dictionary<string, int> targetRoleToBone)
    {
        _target = target;
        _targetRoleToBone = targetRoleToBone;
    }

    public static AnimRetargeter Create(string resPath, Skeleton3D target,
        Dictionary<string, int> targetRoleToBone)
    {
        var packed = ResourceLoader.Load<PackedScene>(resPath);
        return packed == null ? null : CreateFromScene(packed.Instantiate(), target, targetRoleToBone);
    }

    /// Runtime show GLBs use the same direction-based retargeter as locomotion.
    public static AnimRetargeter CreateFromScene(Node scene, Skeleton3D target, Dictionary<string, int> targetRoleToBone)
    {
        if (target == null || scene == null) return null;
        var retargeter = new AnimRetargeter(target, targetRoleToBone);
        retargeter.AddChild(scene);
        if (scene is Node3D scene3d) scene3d.Visible = false;

        retargeter._srcSkeleton = FindSkeleton(scene);
        if (retargeter._srcSkeleton == null) { GD.PrintErr("retargeter: no Skeleton3D in source GLB"); return null; }

        retargeter._srcPlayer = FindAnimPlayer(scene);
        if (retargeter._srcPlayer == null) { GD.PrintErr("retargeter: no AnimationPlayer in imported scene"); return null; }

        // Only our Update() may advance the clip. Left on the default callback mode the engine
        // also ticks it every rendered frame, so the clip advanced twice per frame — once in
        // real time and once by the physics delta — making playback both ~2x too fast and
        // dependent on framerate (much worse the higher the fps).
        retargeter._srcPlayer.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;

        // Build source role→bone map
        for (int i = 0; i < retargeter._srcSkeleton.GetBoneCount(); i++)
        {
            string boneName = retargeter._srcSkeleton.GetBoneName(i);
            string normalized = boneName.Replace(":", "");
            foreach (var pair in targetRoleToBone)
                if (boneName == target.GetBoneName(pair.Value).ToString() || boneName.Equals(pair.Key, StringComparison.OrdinalIgnoreCase))
                    retargeter._srcRoleToBone.TryAdd(pair.Key, i);
            if (MixamoRoleMap.TryGetValue(normalized, out string role))
            {
                if (!retargeter._srcRoleToBone.ContainsKey(role))
                    retargeter._srcRoleToBone[role] = i;
            }
        }

        // Build ordered role list based on hierarchy order
        foreach (string r in RoleHierarchyOrder)
        {
            if (retargeter._srcRoleToBone.ContainsKey(r) && targetRoleToBone.ContainsKey(r))
                retargeter._orderedRoles.Add(r);
        }

        // Capture global rest rotations for both skeletons
        foreach (string role in retargeter._orderedRoles)
        {
            int srcIdx = retargeter._srcRoleToBone[role];
            int tgtIdx = targetRoleToBone[role];

            retargeter._srcGlobalRestRot[role] = GetGlobalRest(retargeter._srcSkeleton, srcIdx).Basis.GetRotationQuaternion();
            retargeter._tgtGlobalRestRot[role] = GetGlobalRest(target, tgtIdx).Basis.GetRotationQuaternion();
        }

        // Reverse map so the per-frame target walk can ask "is this bone driven?" by index.
        foreach (string role in retargeter._orderedRoles)
            retargeter._tgtBoneToRole[targetRoleToBone[role]] = role;

        // Rest-pose bone positions, used for the direction-based retarget below.
        var srcRestPos = new Dictionary<string, Vector3>();
        var tgtRestPos = new Dictionary<string, Vector3>();
        foreach (string role in retargeter._orderedRoles)
        {
            srcRestPos[role] = GetGlobalRest(retargeter._srcSkeleton, retargeter._srcRoleToBone[role]).Origin;
            tgtRestPos[role] = GetGlobalRest(target, targetRoleToBone[role]).Origin;
        }

        // Model-space alignment between the two rigs, derived geometrically from each rig's
        // shoulder axis and spine axis. The hips bind rotation alone is unreliable — the Mixamo
        // source rig's bind pose is neither symmetric nor upright — whereas "which way is right"
        // and "which way is up" are well defined in both rigs by actual bone positions.
        // The TARGET half can be built now: a VRM's bind pose is a reliable upright T-pose.
        // The SOURCE half is deliberately deferred to the first Update — the Mixamo rig's bind
        // pose is a leaning, asymmetric pose while its *animations* are upright, so the frame
        // must be sampled from an animated pose or the whole avatar inherits that bind lean.
        if (tgtRestPos.ContainsKey("leftUpperArm") && tgtRestPos.ContainsKey("rightUpperArm") &&
            tgtRestPos.ContainsKey("hips") && tgtRestPos.ContainsKey("head"))
        {
            retargeter._tgtFrame = FrameFrom(tgtRestPos["rightUpperArm"] - tgtRestPos["leftUpperArm"],
                                             tgtRestPos["head"] - tgtRestPos["hips"]);
            retargeter._haveTgtFrame = true;
        }

        // Hips height reference, so crouch/jump can drop or raise the pelvis. Rotation-only
        // retargeting leaves the pelvis at standing height and folds the legs upward instead —
        // which reads as the avatar floating with its feet off the floor during a crouch.
        if (tgtRestPos.ContainsKey("hips") && tgtRestPos.ContainsKey("leftFoot") && tgtRestPos.ContainsKey("rightFoot") &&
            srcRestPos.ContainsKey("hips") && srcRestPos.ContainsKey("leftFoot") && srcRestPos.ContainsKey("rightFoot"))
        {
            retargeter._tgtHipsHeight = tgtRestPos["hips"].Y - Mathf.Min(tgtRestPos["leftFoot"].Y, tgtRestPos["rightFoot"].Y);
            if (retargeter._tgtHipsHeight > 0.05f)
            {
                retargeter._hipsIdx = targetRoleToBone["hips"];
                retargeter._hipsRestLocal = target.GetBoneRest(retargeter._hipsIdx).Origin;
                // _srcHipsHeight is measured lazily from an animated frame, for the same reason
                // the frame alignment is: the source bind pose leans, which would shorten the
                // measured leg length and leave the avatar hovering ~10cm off the floor.
                retargeter._canGroundClamp = true;
            }
        }

        // Per-bone rest direction (bone → its child) in TARGET model space. Retargeting then
        // just means swinging this direction onto whatever direction the source bone points.
        foreach (var (role, childRole) in ChildOf)
        {
            if (!tgtRestPos.ContainsKey(role) || !tgtRestPos.ContainsKey(childRole)) continue;
            if (!srcRestPos.ContainsKey(role) || !srcRestPos.ContainsKey(childRole)) continue;
            Vector3 d = tgtRestPos[childRole] - tgtRestPos[role];
            if (d.LengthSquared() < 1e-8f) continue;
            retargeter._tgtRestDir[role] = d.Normalized();
            retargeter._dirRoles.Add(role);
        }

        retargeter.ConfigureBakedConcert(scene);
        retargeter.Calibrate();

        int clipCount = 0;
        var animNames = new List<string>();
        foreach (string n in retargeter._srcPlayer.GetAnimationList()) { clipCount++; animNames.Add(n); }
        GD.Print($"retargeter: loaded {retargeter._orderedRoles.Count} role mappings, {clipCount} clips: [{string.Join(", ", animNames)}]");
        return retargeter;
    }

    public string[] ShowClips => _srcPlayer?.GetAnimationList() ?? Array.Empty<string>();
    public int MappedShowBones => _orderedRoles.Count;
    public double ShowClipLength(string clip) => _srcPlayer.HasAnimation(clip) ? _srcPlayer.GetAnimation(clip).Length : 0;
    public void SampleShowClip(string clip, double seconds)
    {
        if (!_srcPlayer.HasAnimation(clip)) throw new InvalidOperationException($"Animation clip '{clip}' was not found.");
        if (_srcPlayer.CurrentAnimation != clip) {
            _srcPlayer.GetAnimation(clip).LoopMode = Animation.LoopModeEnum.None;
            _srcPlayer.Play(clip, 0);
        }
        if (!_alignResolved) Calibrate(clip);
        _srcPlayer.Seek(Math.Clamp(seconds, 0, ShowClipLength(clip)), update: true);
        _blend = 1f;
        if (UsesBakedConcertPose) { SampleBakedConcert(); return; }
        Update(0);
    }

    private static Transform3D GetGlobalRest(Skeleton3D skel, int boneIdx)
    {
        Transform3D t = skel.GetBoneRest(boneIdx);
        int p = skel.GetBoneParent(boneIdx);
        while (p >= 0)
        {
            t = skel.GetBoneRest(p) * t;
            p = skel.GetBoneParent(p);
        }
        return t;
    }

    public bool HasClip(State s) => StateClip.TryGetValue(s, out var name) && ClipExists(name);

    private bool ClipExists(string name) => FindClip(name) != null;

    // Exact match only. Fuzzy substring matching caused Idle→"Crouch_Idle" and
    // Walk→"Walk_Backwards" mismatches; the StateClip table already uses the exact
    // clip names present in locomotion.glb, and "Idle" intentionally has no clip so
    // the procedural idle sway is used instead.
    private string FindClip(string name)
    {
        if (_srcPlayer == null) return null;
        foreach (string n in _srcPlayer.GetAnimationList())
            if (n == name) return n;
        return null;
    }

    public void SetState(State s, float blendSpeed = 10f)
    {
        if (s == _state) return;
        _prevState = _state;
        _state = s;
        _blend = 0f;
        _blendSpeed = blendSpeed;

        string clip = StateClip.GetValueOrDefault(s, null);
        string actual = clip != null ? FindClip(clip) : null;
        if (actual != null)
        {
            var anim = _srcPlayer.GetAnimation(actual);
            anim.LoopMode = Animation.LoopModeEnum.Linear;
            _srcPlayer.Play(actual, 0.1, 1f, false);
        }
    }

    public State CurrentState => _state;

    // Scratch buffers for the per-frame global-rotation chains (allocated once — this runs
    // every frame per avatar and the client's hot paths must stay allocation-free).
    private Transform3D[] _srcPose;
    private Vector3[] _srcPosePos;
    private Quaternion[] _tgtGlobal;
    private readonly Dictionary<int, string> _tgtBoneToRole = new();
    private readonly Dictionary<string, Vector3> _dirDesired = new();

    /// Accumulate the source skeleton's global bone transforms from its local poses.
    /// Skeleton3D.GetBoneGlobalPose() is a cache that is not recomputed synchronously after
    /// Advance()/SetBonePoseRotation(), so walking the parents ourselves is what keeps every
    /// bone in the chain consistent within a single frame.
    private void SampleSource()
    {
        int sn = _srcSkeleton.GetBoneCount();
        if (_srcPose == null || _srcPose.Length != sn)
        {
            _srcPose = new Transform3D[sn];
            _srcPosePos = new Vector3[sn];
        }
        for (int i = 0; i < sn; i++)
        {
            Transform3D local = _srcSkeleton.GetBonePose(i);
            int p = _srcSkeleton.GetBoneParent(i);
            _srcPose[i] = p >= 0 && p < i ? _srcPose[p] * local : local;
            _srcPosePos[i] = _srcPose[i].Origin;
        }
    }

    /// Calibrate the source→target frame alignment and the standing leg length from a known
    /// upright clip. Both references must come from a *standing* pose: sampling them from
    /// whatever clip happens to play first would calibrate "standing" against a crouch or a
    /// bow, which puts the pelvis at the wrong height for every other clip.
    private void Calibrate(string showClip = null)
    {
        string reference = showClip ?? FindClip("Idle_A") ?? FindClip("Walk");
        if (reference == null || !_haveTgtFrame) return;

        _srcPlayer.Play(reference);
        _srcPlayer.Seek(0.0, update: true);
        SampleSource();

        if (!_srcRoleToBone.ContainsKey("leftUpperArm") || !_srcRoleToBone.ContainsKey("rightUpperArm") ||
            !_srcRoleToBone.ContainsKey("hips") || !_srcRoleToBone.ContainsKey("head")) return;

        Basis bs = FrameFrom(
            _srcPosePos[_srcRoleToBone["rightUpperArm"]] - _srcPosePos[_srcRoleToBone["leftUpperArm"]],
            _srcPosePos[_srcRoleToBone["head"]] - _srcPosePos[_srcRoleToBone["hips"]]);
        _alignR = (_tgtFrame * bs.Inverse()).GetRotationQuaternion().Normalized();
        _alignResolved = true;

        if (_canGroundClamp)
        {
            float h = (_alignR * _srcPosePos[_srcRoleToBone["hips"]]).Y
                    - Mathf.Min((_alignR * _srcPosePos[_srcRoleToBone["leftFoot"]]).Y,
                                (_alignR * _srcPosePos[_srcRoleToBone["rightFoot"]]).Y);
            if (h > 0.05f) _srcHipsHeight = h; else _canGroundClamp = false;
        }
    }

    public void Update(double delta, float fallbackWeight = 0f)
    {
        if (_srcSkeleton == null || _srcPlayer == null) return;

        // Advance the hidden source AnimationPlayer ourselves so the sampled pose belongs to
        // this frame rather than whenever the engine happens to tick it.
        _srcPlayer.Advance(delta);

        _blend = Mathf.Lerp(_blend, 1f, (float)delta * _blendSpeed);
        float w = _blend * (1f - fallbackWeight);

        // ── 1. Source global transforms, accumulated from local bone poses ───────────────
        SampleSource();

        // ── 2. Desired model-space direction per bone, taken from the source rig ────────
        // Direction transfer rather than rest-delta transfer: the Mixamo source rig's bind
        // pose is not the VRM's T-pose, so "deviation from bind" is not comparable between
        // them (that mismatch left the target permanently T-posed). Where each bone *points*
        // is comparable, so we swing the target bone onto the source bone's direction.
        _dirDesired.Clear();
        foreach (string role in _dirRoles)
        {
            Vector3 a = _srcPosePos[_srcRoleToBone[role]];
            Vector3 b = _srcPosePos[_srcRoleToBone[ChildOf[role]]];
            Vector3 d = b - a;
            if (d.LengthSquared() < 1e-8f) continue;
            _dirDesired[role] = (_alignR * d.Normalized()).Normalized();
        }

        // ── 3. Walk the target skeleton root→leaf, applying motion and tracking globals ──
        int tn = _target.GetBoneCount();
        if (_tgtGlobal == null || _tgtGlobal.Length != tn) _tgtGlobal = new Quaternion[tn];
        for (int i = 0; i < tn; i++)
        {
            int p = _target.GetBoneParent(i);
            Quaternion parentGlobal = p >= 0 && p < i ? _tgtGlobal[p] : Quaternion.Identity;

            if (_tgtBoneToRole.TryGetValue(i, out string role) &&
                _dirDesired.TryGetValue(role, out Vector3 want) &&
                _tgtRestDir.TryGetValue(role, out Vector3 restDir))
            {
                // Swing the bone's rest direction onto the direction the source bone points.
                // Roll about the bone axis is not recovered by this (a known trade-off of
                // direction retargeting) — limb placement is correct, finger/wrist twist is not.
                Quaternion restRot = _tgtGlobalRestRot[role];
                Vector3 restDirModel = (restRot * restDir).Normalized();
                Quaternion swing = FromTo(restDirModel, want);
                Quaternion desiredGlobal = (swing * restRot).Normalized();

                Quaternion local = (parentGlobal.Inverse() * desiredGlobal).Normalized();
                if (w < 0.999f)
                    local = _target.GetBonePoseRotation(i).Slerp(local, w).Normalized();
                _target.SetBonePoseRotation(i, local);
                _tgtGlobal[i] = (parentGlobal * local).Normalized();
            }
            else
            {
                // Unmapped bone (fingers, twist bones, …) — leave its pose alone but still
                // account for it so mapped descendants get a correct parent frame.
                _tgtGlobal[i] = (parentGlobal * _target.GetBonePoseRotation(i)).Normalized();
            }
        }

        // ── 4. Ground clamp: drop/raise the pelvis to match the source's hips-above-feet ──
        if (_canGroundClamp)
        {
            int sh = _srcRoleToBone["hips"];
            int lf = _srcRoleToBone["leftFoot"], rf = _srcRoleToBone["rightFoot"];
            // Measure in aligned space so "up" means the target's up.
            float hipsY = (_alignR * _srcPosePos[sh]).Y;
            float footY = Mathf.Min((_alignR * _srcPosePos[lf]).Y, (_alignR * _srcPosePos[rf]).Y);
            float srcAbove = hipsY - footY;

            // Scale the source's pelvis height into this rig's proportions, then express the
            // difference from the bind pose as a local offset on the hips bone.
            float wantHeight = srcAbove * (_tgtHipsHeight / _srcHipsHeight);
            float dy = Mathf.Clamp(wantHeight - _tgtHipsHeight, -_tgtHipsHeight * 0.8f, _tgtHipsHeight * 0.8f);

            int hp = _target.GetBoneParent(_hipsIdx);
            Quaternion parentGlobal = hp >= 0 ? _tgtGlobal[hp] : Quaternion.Identity;
            Vector3 localOffset = parentGlobal.Inverse() * new Vector3(0, dy * w, 0);
            _target.SetBonePosePosition(_hipsIdx, _hipsRestLocal + localOffset);
        }
    }

    /// Diagnostic: the SOURCE rig's bind pose, hips-relative. Tells us whether the Mixamo rig
    /// is T-posed (arms out) or A-posed, which determines whether a raw rest-delta transfer
    /// onto the target's bind pose is even valid.
    public string DebugSourceRest()
    {
        if (_srcSkeleton == null) return "no source";
        var parts = new List<string>();
        if (!_srcRoleToBone.TryGetValue("hips", out int hp)) return "no hips";
        Transform3D hipsRest = GetGlobalRest(_srcSkeleton, hp);
        foreach (string role in new[] { "leftHand", "rightHand", "leftUpperArm", "head" })
        {
            if (!_srcRoleToBone.TryGetValue(role, out int i)) continue;
            Vector3 d = GetGlobalRest(_srcSkeleton, i).Origin - hipsRest.Origin;
            parts.Add($"{role}=({d.X:F2},{d.Y:F2},{d.Z:F2})");
        }
        return string.Join(" ", parts);
    }

    /// Diagnostic: where the source clip is and what the source rig is actually doing.
    public string DebugSourceState()
    {
        if (_srcPlayer == null || _srcSkeleton == null) return "no source";
        string anim = _srcPlayer.CurrentAnimation;
        double pos = _srcPlayer.CurrentAnimationPosition;
        double len = _srcPlayer.CurrentAnimationLength;

        string hand = "?";
        if (_srcRoleToBone.TryGetValue("leftHand", out int lh) &&
            _srcRoleToBone.TryGetValue("hips", out int hp))
        {
            Vector3 d = _srcSkeleton.GetBoneGlobalPose(lh).Origin - _srcSkeleton.GetBoneGlobalPose(hp).Origin;
            hand = $"({d.X:F2},{d.Y:F2},{d.Z:F2})";
        }
        return $"anim={anim} pos={pos:F2}/{len:F2} playing={_srcPlayer.IsPlaying()} srcLeftHand={hand}";
    }

    private static Skeleton3D FindSkeleton(Node node)
    {
        if (node is Skeleton3D s) return s;
        foreach (var child in node.GetChildren())
        {
            var found = FindSkeleton(child);
            if (found != null) return found;
        }
        return null;
    }

    private static AnimationPlayer FindAnimPlayer(Node node)
    {
        if (node is AnimationPlayer ap) return ap;
        foreach (var child in node.GetChildren())
        {
            var found = FindAnimPlayer(child);
            if (found != null) return found;
        }
        return null;
    }
}
