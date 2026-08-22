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
        { "hips", "spine", "chest", "head",
          "leftUpperLeg", "rightUpperLeg", "leftLowerLeg", "rightLowerLeg",
          "leftUpperArm", "rightUpperArm", "leftLowerArm", "rightLowerArm" };

    public enum Emote { None, Sit, Dance, Wave }
    private Emote _emote = Emote.None;
    private float _emoteTime;
    private float _emoteBlend;

    /// Play an emote animation (sit, dance, wave). Pass Emote.None to return to normal.
    public void PlayEmote(Emote e)
    {
        _emote = e;
        _emoteTime = 0f;
    }

    public Emote CurrentEmote => _emote;

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
        _emoteTime += dt;
        _moveBlend = Mathf.Lerp(_moveBlend, Mathf.Clamp(speed / 4f, 0f, 1.6f), dt * 8f);
        _walkPhase += dt * Mathf.Max(speed, 0f) * 2.2f;

        // Emote blending: ramp in when an emote is active, ramp out when cancelled.
        float emoteTarget = _emote != Emote.None && speed < 0.5f && onFloor ? 1f : 0f;
        _emoteBlend = Mathf.Lerp(_emoteBlend, emoteTarget, dt * 6f);
        if (_emoteBlend < 0.01f && _emote != Emote.None && (speed > 0.5f || !onFloor))
            _emote = Emote.None;

        float b = _moveBlend;
        float t = _idleTime;
        float eb = _emoteBlend;

        // Default walk/idle values.
        float legSwing = Mathf.Sin(_walkPhase) * 0.55f * b;
        float armSwing = Mathf.Sin(_walkPhase) * 0.4f * b;
        float breathe = Mathf.Sin(t * 1.7f) * 0.025f * (1f - 0.5f * Mathf.Min(b, 1f));
        float armIdle = Mathf.Sin(t * 1.3f) * 0.035f * (1f - Mathf.Min(b, 1f));
        float lowerLegBend = Mathf.Max(0, -Mathf.Sin(_walkPhase)) * 0.6f * b;
        float lowerArmBend = 0.3f + Mathf.Abs(Mathf.Sin(_walkPhase)) * 0.2f * b;

        // Airborne: tuck legs into a jump pose.
        if (!onFloor)
        {
            legSwing = 0.3f;
            lowerLegBend = 0.7f;
            armSwing = -0.3f;
            lowerArmBend = 0.5f;
        }

        // Apply emote poses (blended over walk/idle).
        if (eb > 0.01f)
        {
            ApplyEmote(eb, t, ref legSwing, ref armSwing, ref breathe, ref armIdle, ref lowerLegBend, ref lowerArmBend);
        }

        Swing("leftUpperLeg", legSwing);
        Swing("rightUpperLeg", -legSwing);
        Swing("leftLowerLeg", lowerLegBend);
        Swing("rightLowerLeg", lowerLegBend);
        Swing("leftUpperArm", -armSwing + armIdle);
        Swing("rightUpperArm", armSwing + armIdle);
        Swing("leftLowerArm", lowerArmBend);
        Swing("rightLowerArm", lowerArmBend);
        Swing("chest", breathe);
        Swing("spine", breathe * 0.5f);
        Swing("head", Mathf.Sin(t * 0.9f) * 0.02f * (1f - eb));
        Swing("hips", Mathf.Abs(Mathf.Sin(_walkPhase)) * -0.04f * b * (1f - eb));
    }

    /// Override animation values for the active emote, blended by `eb` (0..1).
    private void ApplyEmote(float eb, float t,
        ref float legSwing, ref float armSwing, ref float breathe,
        ref float armIdle, ref float lowerLegBend, ref float lowerArmBend)
    {
        switch (_emote)
        {
            case Emote.Sit:
                // Sitting: legs bent 90°, arms resting in lap.
                legSwing = Mathf.Lerp(legSwing, 1.4f, eb);       // thighs forward
                lowerLegBend = Mathf.Lerp(lowerLegBend, 1.4f, eb); // knees bent
                armSwing = Mathf.Lerp(armSwing, 0.2f, eb);
                armIdle = Mathf.Lerp(armIdle, 0f, eb);
                lowerArmBend = Mathf.Lerp(lowerArmBend, 0.4f, eb);
                breathe = Mathf.Lerp(breathe, Mathf.Sin(t * 1.2f) * 0.03f, eb);
                break;

            case Emote.Dance:
            {
                // Dance: rhythmic sway + arm waving.
                float beat = Mathf.Sin(t * 4f);
                float beat2 = Mathf.Sin(t * 4f + Mathf.Pi * 0.5f);
                legSwing = Mathf.Lerp(legSwing, beat * 0.2f, eb);
                lowerLegBend = Mathf.Lerp(lowerLegBend, 0.1f + Mathf.Abs(beat) * 0.15f, eb);
                armSwing = Mathf.Lerp(armSwing, beat2 * 0.8f, eb);
                armIdle = Mathf.Lerp(armIdle, 0f, eb);
                lowerArmBend = Mathf.Lerp(lowerArmBend, 0.6f + Mathf.Abs(beat) * 0.3f, eb);
                breathe = Mathf.Lerp(breathe, beat * 0.08f, eb);
                // Sway hips and spine.
                SwingQuat("hips", new Quaternion(Vector3.Up, beat * 0.15f * eb));
                SwingQuat("spine", new Quaternion(Vector3.Up, -beat * 0.1f * eb));
                SwingQuat("chest", new Quaternion(Vector3.Up, beat * 0.08f * eb));
                break;
            }

            case Emote.Wave:
            {
                // Wave: right arm raised, hand waving. Left arm idle.
                float wave = Mathf.Sin(t * 6f);
                armSwing = Mathf.Lerp(armSwing, -1.2f, eb);  // right arm up
                lowerArmBend = Mathf.Lerp(lowerArmBend, 0.3f, eb);
                // Override right arm specifically for the wave.
                SwingQuat("rightUpperArm", new Quaternion(Vector3.Right, -1.2f * eb) *
                    new Quaternion(Vector3.Forward, wave * 0.2f * eb));
                SwingQuat("rightLowerArm", new Quaternion(Vector3.Right, (0.3f + wave * 0.3f) * eb));
                armIdle = Mathf.Lerp(armIdle, 0f, eb);
                breathe = Mathf.Lerp(breathe, Mathf.Sin(t * 1.5f) * 0.03f, eb);
                break;
            }
        }
    }

    /// Rotate a bone forward/back about its parent's X axis, on top of its rest rotation.
    private void Swing(string role, float angle)
    {
        if (!_animBones.TryGetValue(role, out var ab)) return;
        Skeleton.SetBonePoseRotation(ab.Index, new Quaternion(Vector3.Right, angle) * ab.Rest);
    }

    /// Set a bone's rotation to an explicit quaternion (replaces rest rotation entirely).
    private void SwingQuat(string role, Quaternion quat)
    {
        if (!_animBones.TryGetValue(role, out var ab)) return;
        Skeleton.SetBonePoseRotation(ab.Index, quat * ab.Rest);
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
