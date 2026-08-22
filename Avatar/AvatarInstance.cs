using System;
using System.Collections.Generic;
using Godot;

namespace SerikaSocial.Avatar;

/// A live, in-scene humanoid avatar built from a `.ska` file.
///
/// Wraps the imported glTF scene, exposes the `Skeleton3D`, and resolves Serika's normalized
/// humanoid roles to bone indices so callers can place the first-person camera at the head,
/// hang name tags, and (later) drive IK — without knowing anything about VRM internals.
///
/// Loading uses Godot's runtime `GltfDocument`, which parses the GLB payload embedded in the
/// `.ska`. This is the single place mesh import happens, on both local and remote avatars.
public sealed partial class AvatarInstance : Node3D
{
    public SkaMeta Meta { get; private set; }
    public Skeleton3D Skeleton { get; private set; }
    public float EyeHeight => Meta?.EyeHeightMeters ?? 1.6f;
    public float Height => Meta?.HeightMeters ?? 1.7f;

    private readonly Dictionary<string, int> _roleToBone = new();
    private Node3D _model;

    /// Build an avatar from raw `.ska` bytes. Returns null (and logs) if the payload can't be
    /// imported — callers fall back to the capsule so a bad avatar never leaves you invisible.
    public static AvatarInstance FromBytes(byte[] skaBytes)
    {
        SkaFile ska;
        try { ska = SkaFile.Parse(skaBytes); }
        catch (Exception e) { GD.PrintErr($"avatar: bad .ska ({e.Message})"); return null; }

        var doc = new GltfDocument();
        var state = new GltfState();
        Error err = doc.AppendFromBuffer(ska.Glb, "", state);
        if (err != Error.Ok) { GD.PrintErr($"avatar: glTF import failed ({err})"); return null; }

        var scene = doc.GenerateScene(state);
        if (scene is not Node3D model) { GD.PrintErr("avatar: glTF produced no Node3D"); return null; }

        var inst = new AvatarInstance { Meta = ska.Meta, Name = "Avatar" };
        inst._model = model;
        // VRM 0.x faces +Z; rotate so the avatar faces Godot-forward (−Z).
        model.RotationDegrees = new Vector3(0, ska.Meta.FaceYawDegrees, 0);
        inst.AddChild(model);

        inst.Skeleton = FindSkeleton(model);
        if (inst.Skeleton != null) inst.ResolveHumanoid();
        else GD.PrintErr("avatar: no Skeleton3D found in imported scene");
        inst.SetupAnimation();

        return inst;
    }

    /// Load a `.ska` from a Godot path (res:// bundled default, or user:// download).
    public static AvatarInstance FromPath(string path)
    {
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (f == null) { GD.PrintErr($"avatar: cannot open {path} ({FileAccess.GetOpenError()})"); return null; }
        return FromBytes(f.GetBuffer((long)f.GetLength()));
    }

    private void ResolveHumanoid()
    {
        foreach (var (role, boneName) in Meta.Humanoid)
        {
            int idx = Skeleton.FindBone(boneName);
            if (idx >= 0) _roleToBone[role] = idx;
        }
    }

    public int BoneOf(string role) => _roleToBone.GetValueOrDefault(role, -1);

    /// Global transform of the head bone in world space (for camera / first-person hiding).
    public bool TryGetHeadGlobal(out Transform3D xf)
    {
        xf = Transform3D.Identity;
        if (Skeleton == null) return false;
        int head = BoneOf("head");
        if (head < 0) return false;
        xf = Skeleton.GlobalTransform * Skeleton.GetBoneGlobalPose(head);
        return true;
    }

    /// Hide the head (and its children: face, hair) so first-person view isn't blocked by the
    /// inside of the skull. Implemented by collapsing the head bone's local pose scale — cheap,
    /// reversible, and doesn't touch the mesh or materials.
    public void SetHeadVisible(bool visible)
    {
        if (Skeleton == null) return;
        int head = BoneOf("head");
        if (head < 0) return;
        Skeleton.SetBonePoseScale(head, visible ? Vector3.One : new Vector3(1e-3f, 1e-3f, 1e-3f));
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

    // ── Animation ─────────────────────────────────────────────────────────────────
    //
    // Most .ska payloads (VRM sources) ship with no animation clips at all, which left every
    // character frozen in its rest pose. If the GLB does embed clips we play one looped;
    // otherwise we procedurally swing the humanoid limbs from movement state — walk cycle
    // while moving, gentle breathing/sway while idle. Driven by `Animate` each frame from
    // the owning player/remote-avatar script.

    private AnimationPlayer _animPlayer;

    private readonly struct AnimBone
    {
        public readonly int Index;
        public readonly Quaternion Rest; // rest-pose local rotation, our swing baseline
        public AnimBone(int index, Quaternion rest) { Index = index; Rest = rest; }
    }

    private readonly Dictionary<string, AnimBone> _animBones = new();
    private static readonly string[] ProceduralRoles =
        { "hips", "leftUpperLeg", "rightUpperLeg", "leftUpperArm", "rightUpperArm", "chest", "head" };

    private float _idleTime;
    private float _walkPhase;
    private float _moveBlend; // 0 = idle, 1 = walking, >1 = sprinting

    private void SetupAnimation()
    {
        _animPlayer = FindAnimPlayer(_model);
        if (_animPlayer != null && _animPlayer.GetAnimationList().Length > 0)
        {
            string pick = null;
            foreach (string name in _animPlayer.GetAnimationList())
            {
                if (name.Contains("idle", StringComparison.OrdinalIgnoreCase)) { pick = name; break; }
            }
            pick ??= _animPlayer.GetAnimationList()[0];
            _animPlayer.GetAnimation(pick).LoopMode = Animation.LoopModeEnum.Linear;
            _animPlayer.Play(pick);
            return;
        }

        // No clips — capture rest rotations for the procedural fallback.
        _animPlayer = null;
        if (Skeleton == null) return;
        foreach (string role in ProceduralRoles)
        {
            int idx = BoneOf(role);
            if (idx < 0) continue;
            _animBones[role] = new AnimBone(idx, Skeleton.GetBoneRest(idx).Basis.GetRotationQuaternion());
        }
    }

    /// Advance the avatar's animation. `speed` is planar m/s; pass 0 when standing still.
    /// Call every frame from the owning node (`_PhysicsProcess`/`_Process`).
    public void Animate(double delta, float speed, bool onFloor)
    {
        if (Skeleton == null || _animBones.Count == 0) return;
        if (_animPlayer != null) return; // embedded clips drive the rig

        float dt = (float)delta;
        _idleTime += dt;
        _moveBlend = Mathf.Lerp(_moveBlend, Mathf.Clamp(speed / 4f, 0f, 1.6f), dt * 8f);
        _walkPhase += dt * Mathf.Max(speed, 0f) * 2.2f;

        float b = _moveBlend;
        float t = _idleTime;
        float legSwing = Mathf.Sin(_walkPhase) * 0.55f * b;
        float armSwing = Mathf.Sin(_walkPhase) * 0.4f * b;
        float breathe = Mathf.Sin(t * 1.7f) * 0.025f * (1f - 0.5f * Mathf.Min(b, 1f));
        float armIdle = Mathf.Sin(t * 1.3f) * 0.035f * (1f - Mathf.Min(b, 1f));

        // Airborne: trail the legs a little instead of cycling them.
        if (!onFloor) legSwing = 0.25f;

        Swing("leftUpperLeg", legSwing);
        Swing("rightUpperLeg", -legSwing);
        Swing("leftUpperArm", -armSwing + armIdle);
        Swing("rightUpperArm", armSwing + armIdle);
        Swing("chest", breathe);
        Swing("head", Mathf.Sin(t * 0.9f) * 0.02f);
        Swing("hips", Mathf.Abs(Mathf.Sin(_walkPhase)) * -0.04f * b); // subtle stride dip
    }

    /// Rotate a bone forward/back about its parent's X axis, on top of its rest rotation.
    private void Swing(string role, float angle)
    {
        if (!_animBones.TryGetValue(role, out var ab)) return;
        Skeleton.SetBonePoseRotation(ab.Index, new Quaternion(Vector3.Right, angle) * ab.Rest);
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
