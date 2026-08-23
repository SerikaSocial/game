using System;
using System.Collections.Generic;
using Godot;

namespace SerikaSocial.Avatar;

/// Retargets Mixamo animation clips onto a VRM avatar skeleton by humanoid role.
///
/// Loads the locomotion GLB as a hidden source skeleton with its own AnimationPlayer,
/// plays a clip on it each frame, and copies each bone's local rotation onto the target
/// VRM skeleton — rest-compensated so the avatar's own bind pose is preserved.
///
/// The Mixamo rig and the VRM rig have different rest poses (Mixamo: slight A-pose,
/// VRM: T-pose), so we can't copy rotations directly. Instead we compute the delta
/// from the source bone's rest rotation and apply that delta onto the target's rest
/// rotation: <c>targetRot = delta * targetRest</c> where <c>delta = srcCurrent * srcRest^-1</c>.
public sealed partial class AnimRetargeter : Node
{
    private Skeleton3D _srcSkeleton;
    private AnimationPlayer _srcPlayer;

    private readonly Skeleton3D _target;
    private readonly Dictionary<string, int> _targetRoleToBone;

    // source bone index per role
    private readonly Dictionary<string, int> _srcRoleToBone = new();
    // source rest rotation per role
    private readonly Dictionary<string, Quaternion> _srcRestRot = new();
    // target rest rotation per role
    private readonly Dictionary<string, Quaternion> _tgtRestRot = new();

    // state machine
    public enum State { Idle, Walk, Run, CrouchIdle, CrouchWalk, Jump, Fall, Land, Sit, Dance, Wave, None }
    private State _state = State.None;
    private State _prevState = State.None;
    private float _blend = 1f;
    private float _blendSpeed = 10f;

    private static readonly Dictionary<State, string> StateClip = new()
    {
        { State.Idle,       "Idle" },
        { State.Walk,       "Walk" },
        { State.Run,        "Run_Anime" },
        { State.CrouchIdle, "Crouch_Idle" },
        { State.CrouchWalk, "Crouch_Walk" },
        { State.Jump,       "Jump_Start" },
        { State.Fall,       "Jump_air" },
        { State.Land,       "Jump_Land" },
        { State.Sit,        "Sitting_Idle" },
        { State.Dance,      "Dance_Simple" },
        { State.Wave,       "Wave" },
    };

    // Mixamo bone name → Serika humanoid role
    private static readonly Dictionary<string, string> MixamoRoleMap = new()
    {
        { "mixamorigHips", "hips" },
        { "mixamorigSpine", "spine" },
        { "mixamorigSpine1", "spine" },   // maps to same role — we use Spine2 as chest
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

    private AnimRetargeter(Skeleton3D target, Dictionary<string, int> targetRoleToBone)
    {
        _target = target;
        _targetRoleToBone = targetRoleToBone;
    }

    /// Create a retargeter loaded from `glbPath`, targeting `target` skeleton with
    /// the given role→bone-index map. Returns null if the GLB can't be loaded.
    public static AnimRetargeter Create(string glbPath, Skeleton3D target,
        Dictionary<string, int> targetRoleToBone)
    {
        if (target == null) return null;
        var retargeter = new AnimRetargeter(target, targetRoleToBone);

        // Load the GLB at runtime via GltfDocument — same path as AvatarInstance.FromBytes,
        // so it works in both editor and exported builds without needing editor import.
        using var f = FileAccess.Open(glbPath, FileAccess.ModeFlags.Read);
        if (f == null) { GD.PrintErr($"retargeter: cannot open {glbPath}"); return null; }
        byte[] glbBytes = f.GetBuffer((long)f.GetLength());

        var doc = new GltfDocument();
        var state = new GltfState();
        Error err = doc.AppendFromBuffer(glbBytes, "", state);
        if (err != Error.Ok) { GD.PrintErr($"retargeter: can't load {glbPath} ({err})"); return null; }
        var scene = doc.GenerateScene(state);
        if (scene == null) { GD.PrintErr("retargeter: GLB produced no scene"); return null; }

        retargeter.AddChild(scene);
        if (scene is Node3D scene3d) scene3d.Visible = false; // hidden — we only sample rotations from it

        retargeter._srcSkeleton = FindSkeleton(scene);
        if (retargeter._srcSkeleton == null) { GD.PrintErr("retargeter: no Skeleton3D in source GLB"); return null; }

        retargeter._srcPlayer = FindAnimPlayer(scene);
        if (retargeter._srcPlayer == null) { GD.PrintErr("retargeter: no AnimationPlayer in source GLB"); return null; }

        // Build source role→bone map and rest rotations
        for (int i = 0; i < retargeter._srcSkeleton.GetBoneCount(); i++)
        {
            string boneName = retargeter._srcSkeleton.GetBoneName(i);
            if (MixamoRoleMap.TryGetValue(boneName, out string role))
            {
                // For duplicate roles (Spine/Spine1), keep the first match
                if (!retargeter._srcRoleToBone.ContainsKey(role))
                    retargeter._srcRoleToBone[role] = i;
            }
        }

        // Capture rest rotations for both source and target
        foreach (var (role, srcIdx) in retargeter._srcRoleToBone)
        {
            retargeter._srcRestRot[role] = retargeter._srcSkeleton.GetBoneRest(srcIdx).Basis.GetRotationQuaternion();
            if (targetRoleToBone.TryGetValue(role, out int tgtIdx))
                retargeter._tgtRestRot[role] = target.GetBoneRest(tgtIdx).Basis.GetRotationQuaternion();
        }

        int clipCount = 0;
        foreach (string _ in retargeter._srcPlayer.GetAnimationList()) clipCount++;
        GD.Print($"retargeter: loaded {retargeter._srcRoleToBone.Count} role mappings, {clipCount} clips");
        return retargeter;
    }

    public bool HasClip(State s) => StateClip.TryGetValue(s, out var name) && ClipExists(name);

    private bool ClipExists(string name)
    {
        if (_srcPlayer == null) return false;
        foreach (string n in _srcPlayer.GetAnimationList())
            if (n == name) return true;
        return false;
    }

    /// Transition to a new animation state with smooth blending.
    public void SetState(State s, float blendSpeed = 10f)
    {
        if (s == _state) return;
        _prevState = _state;
        _state = s;
        _blend = 0f;
        _blendSpeed = blendSpeed;

        string clip = StateClip.GetValueOrDefault(s, null);
        if (clip != null && ClipExists(clip))
        {
            var anim = _srcPlayer.GetAnimation(clip);
            anim.LoopMode = Animation.LoopModeEnum.Linear;
            _srcPlayer.Play(clip, 0.1, 1f, false);
        }
    }

    public State CurrentState => _state;

    /// Advance the source animation and copy rotations onto the target skeleton.
    /// `fallbackWeight` 0..1 blends toward whatever the caller set on the target
    /// before calling (e.g. procedural idle). 1 = full retargeter, 0 = full fallback.
    public void Update(double delta, float fallbackWeight = 0f)
    {
        if (_srcSkeleton == null || _srcPlayer == null) return;

        _blend = Mathf.Lerp(_blend, 1f, (float)delta * _blendSpeed);
        float w = _blend * (1f - fallbackWeight);

        foreach (var (role, srcIdx) in _srcRoleToBone)
        {
            if (!_tgtRestRot.TryGetValue(role, out var tgtRest)) continue;
            if (!_targetRoleToBone.TryGetValue(role, out int tgtIdx)) continue;

            Quaternion srcCurrent = _srcSkeleton.GetBonePoseRotation(srcIdx);
            // delta from source rest → current
            Quaternion deltaRot = srcCurrent * _srcRestRot[role].Inverse();
            // apply delta onto target rest
            Quaternion tgtRot = deltaRot * tgtRest;

            // Blend with whatever the target bone currently has (procedural fallback)
            if (w < 0.999f)
            {
                Quaternion existing = _target.GetBonePoseRotation(tgtIdx);
                tgtRot = existing.Slerp(tgtRot, w);
            }

            _target.SetBonePoseRotation(tgtIdx, tgtRot);
        }
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
