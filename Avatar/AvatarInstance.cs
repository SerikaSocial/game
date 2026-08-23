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
        // Face direction comes straight from the .ska metadata (faceYawDegrees), authored by
        // the converter per source format. No client-side override — see the orientation notes
        // in the header comment.
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

    /// Build a simple "bean" avatar from primitives — a rounded body, a head, and two hands.
    /// This is the offline fallback when no cloud default is available or a remote user's model
    /// is blocked/missing. It has a minimal humanoid skeleton so the procedural animation and
    /// retargeter still drive it, but no mesh file is needed.
    public static AvatarInstance CreateBean()
    {
        var inst = new AvatarInstance
        {
            Meta = new SkaMeta { Name = "Bean", HeightMeters = 1.6f, EyeHeightMeters = 1.5f },
            Name = "BeanAvatar",
        };

        var skel = new Skeleton3D { Name = "BeanSkeleton" };
        inst.AddChild(skel);
        inst.Skeleton = skel;
        inst._model = skel;

        // Build a minimal humanoid skeleton. Rest transforms are relative to the parent bone.
        // The layout matches a simple capsule person: hips at center, spine/chest/head up,
        // arms at the sides, legs below.
        int AddBone(string name, int parent, Vector3 offset)
        {
            skel.AddBone(name);
            int idx = skel.GetBoneCount() - 1;
            if (parent >= 0) skel.SetBoneParent(idx, parent);
            var rest = new Transform3D(Basis.Identity, offset);
            skel.SetBoneRest(idx, rest);
            skel.SetBonePose(idx, rest);
            return idx;
        }

        int hips = AddBone("BeanHips", -1, new Vector3(0, 0.9f, 0));
        int spine = AddBone("BeanSpine", hips, new Vector3(0, 0.15f, 0));
        int chest = AddBone("BeanChest", spine, new Vector3(0, 0.15f, 0));
        int head = AddBone("BeanHead", chest, new Vector3(0, 0.22f, 0));
        int lArm = AddBone("BeanLeftArm", chest, new Vector3(0.22f, 0.12f, 0));
        int lForearm = AddBone("BeanLeftForeArm", lArm, new Vector3(0, -0.28f, 0));
        int lHand = AddBone("BeanLeftHand", lForearm, new Vector3(0, -0.22f, 0));
        int rArm = AddBone("BeanRightArm", chest, new Vector3(-0.22f, 0.12f, 0));
        int rForearm = AddBone("BeanRightForeArm", rArm, new Vector3(0, -0.28f, 0));
        int rHand = AddBone("BeanRightHand", rForearm, new Vector3(0, -0.22f, 0));
        int lUpLeg = AddBone("BeanLeftUpLeg", hips, new Vector3(0.1f, -0.05f, 0));
        int lLeg = AddBone("BeanLeftLeg", lUpLeg, new Vector3(0, -0.38f, 0));
        int rUpLeg = AddBone("BeanRightUpLeg", hips, new Vector3(-0.1f, -0.05f, 0));
        int rLeg = AddBone("BeanRightLeg", rUpLeg, new Vector3(0, -0.38f, 0));

        // Map Serika humanoid roles → bean bone indices so animation works.
        inst._roleToBone["hips"] = hips;
        inst._roleToBone["spine"] = spine;
        inst._roleToBone["chest"] = chest;
        inst._roleToBone["head"] = head;
        inst._roleToBone["leftUpperArm"] = lArm;
        inst._roleToBone["leftLowerArm"] = lForearm;
        inst._roleToBone["leftHand"] = lHand;
        inst._roleToBone["rightUpperArm"] = rArm;
        inst._roleToBone["rightLowerArm"] = rForearm;
        inst._roleToBone["rightHand"] = rHand;
        inst._roleToBone["leftUpperLeg"] = lUpLeg;
        inst._roleToBone["leftLowerLeg"] = lLeg;
        inst._roleToBone["rightUpperLeg"] = rUpLeg;
        inst._roleToBone["rightLowerLeg"] = rLeg;

        // Attach primitive meshes to bones via BoneAttachment3D so they follow bone rotations.
        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.55f, 0.45f, 0.75f), // purple brand
            Roughness = 0.8f,
        };
        var handMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.65f, 0.55f, 0.85f),
            Roughness = 0.8f,
        };

        void Attach(string boneName, Mesh mesh, Material material, Vector3 offset)
        {
            var att = new BoneAttachment3D { BoneName = boneName };
            skel.AddChild(att);
            var mi = new MeshInstance3D { Mesh = mesh, Position = offset };
            mi.MaterialOverride = material;
            att.AddChild(mi);
        }

        // Body: a capsule centered on the upper torso.
        Attach("BeanSpine", new CapsuleMesh { Height = 0.7f, Radius = 0.22f }, mat, new Vector3(0, 0.25f, 0));
        // Head: a sphere on the head bone.
        Attach("BeanHead", new SphereMesh { Radius = 0.16f, Height = 0.32f }, mat, Vector3.Zero);
        // Hands: small spheres on the hand bones.
        Attach("BeanLeftHand", new SphereMesh { Radius = 0.07f, Height = 0.14f }, handMat, Vector3.Zero);
        Attach("BeanRightHand", new SphereMesh { Radius = 0.07f, Height = 0.14f }, handMat, Vector3.Zero);

        inst.SetupAnimation();
        return inst;
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
    private AnimRetargeter _retargeter;
    private bool _retargeterReady;

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
        // Try to load the Mixamo locomotion retargeter — this provides real Walk, Run, Jump,
        // Crouch, Sit, and Dance clips retargeted onto the VRM skeleton by humanoid role.
        // If it loads, it becomes the primary animation driver; procedural fills in idle/wave.
        if (Skeleton != null)
        {
            _retargeter = AnimRetargeter.Create("res://Assets/Animations/locomotion.glb",
                Skeleton, _roleToBone);
            if (_retargeter != null)
            {
                AddChild(_retargeter);
                _retargeterReady = true;
            }
        }

        // If the retargeter loaded, skip embedded clips — the retargeter is better than
        // whatever the VRM might embed, and we always need the procedural bones for idle/wave.
        if (!_retargeterReady)
        {
            // Only hand the rig to an embedded AnimationPlayer if it actually ships a locomotion
            // or idle clip. Most VRM exports embed nothing useful (or a single bind/T-pose), and
            // handing control to that leaves the avatar frozen in a T-pose — which is exactly what
            // it did. When there's no real clip we drive the rig procedurally instead.
            _animPlayer = FindAnimPlayer(_model);
            if (_animPlayer != null)
            {
                string pick = null;
                foreach (string name in _animPlayer.GetAnimationList())
                {
                    string n = name.ToLowerInvariant();
                    if (n.Contains("idle") || n.Contains("walk") || n.Contains("loco") ||
                        n.Contains("run") || n.Contains("stand"))
                    { pick = name; break; }
                }
                if (pick != null)
                {
                    _animPlayer.GetAnimation(pick).LoopMode = Animation.LoopModeEnum.Linear;
                    _animPlayer.Play(pick);
                    return;
                }
            }
        }
        else
        {
            _animPlayer = null;
        }

        // Procedural path — capture rest rotations as the baseline for our hand-authored motion.
        // This is always needed: it's the fallback for idle/wave and when the retargeter has no
        // clip for the current state.
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
    /// `crouching` and `sprinting` refine the state for clip selection.
    /// Call every frame from the owning node (`_PhysicsProcess`/`_Process`).
    public void Animate(double delta, float speed, bool onFloor, bool crouching = false, bool sprinting = false)
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

        // Determine the retargeter state from movement + emote.
        AnimRetargeter.State rState = DetermineState(speed, onFloor, crouching, sprinting);
        bool hasClip = _retargeterReady && _retargeter.HasClip(rState);

        float b = _moveBlend;
        float t = _idleTime;
        float eb = _emoteBlend;
        float idle = 1f - Mathf.Min(b, 1f); // 1 while standing, 0 while moving

        // ── Walk cycle ──────────────────────────────────────────────────────────────
        // Contralateral swing (opposite arm/leg), with the knee only bending on the back
        // half of each step (heel-off → toe-off) so legs don't hyperextend forward. Arm
        // swing lags the legs slightly for a more natural, less metronomic gait.
        float legSwing = Mathf.Sin(_walkPhase) * 0.55f * b;
        float armSwing = Mathf.Sin(_walkPhase - 0.35f) * 0.45f * b;
        float lowerLegBend = Mathf.Max(0, -Mathf.Sin(_walkPhase)) * 0.7f * b;
        float lowerArmBend = 0.28f + Mathf.Abs(Mathf.Sin(_walkPhase)) * 0.18f * b;

        // ── Idle life ───────────────────────────────────────────────────────────────
        // A frozen A-pose reads as dead. Layer three slow, out-of-phase motions that only
        // exist while standing: chest breathing, a slow weight shift hip↔hip, and a small
        // wandering head. Different frequencies keep them from looking like one pulse.
        float breathe = Mathf.Sin(t * 1.7f) * 0.028f * (0.5f + 0.5f * idle);
        float weightShift = Mathf.Sin(t * 0.8f) * idle;      // −1..1, slow
        float armIdle = Mathf.Sin(t * 1.15f) * 0.04f * idle; // arms drift with breath

        // Airborne: tuck legs into a jump pose.
        if (!onFloor)
        {
            legSwing = 0.3f;
            lowerLegBend = 0.7f;
            armSwing = -0.3f;
            lowerArmBend = 0.5f;
            weightShift = 0f;
        }

        // Apply emote poses (blended over walk/idle).
        if (eb > 0.01f)
        {
            ApplyEmote(eb, t, ref legSwing, ref armSwing, ref breathe, ref armIdle, ref lowerLegBend, ref lowerArmBend);
        }

        // Weight-shift contribution: when standing on the left foot, the left knee softens
        // and the hips roll toward that side. Only meaningful while idle (weightShift→0 moving).
        float wl = Mathf.Max(0, weightShift);
        float wr = Mathf.Max(0, -weightShift);

        Swing("leftUpperLeg", legSwing);
        Swing("rightUpperLeg", -legSwing);
        Swing("leftLowerLeg", lowerLegBend + wr * 0.10f);   // unloaded knee softens
        Swing("rightLowerLeg", lowerLegBend + wl * 0.10f);
        // Arms: the VRM bind pose is a T-pose, so the upper arms must first be rotated down to
        // the sides (about the character's forward axis, mirrored per side) before the walk
        // swing (about the side axis) is layered on. Without the down-rotation the avatar just
        // stands there in a T. ArmRestAngle is the rest droop; tune if a model's arms clip.
        ApplyArm("leftUpperArm", +1f, -armSwing + armIdle);
        ApplyArm("rightUpperArm", -1f, armSwing + armIdle);
        Swing("leftLowerArm", lowerArmBend);
        Swing("rightLowerArm", lowerArmBend);
        Swing("chest", breathe);
        Swing("spine", breathe * 0.5f);
        // Head: gentle wander while idle, damped out during emotes.
        SwingQuat("head",
            new Quaternion(Vector3.Right, Mathf.Sin(t * 0.9f) * 0.025f * idle * (1f - eb)) *
            new Quaternion(Vector3.Up, Mathf.Sin(t * 0.6f) * 0.05f * idle * (1f - eb)));
        // Hips: bob with the walk, and roll side-to-side with the idle weight shift.
        SwingQuat("hips",
            new Quaternion(Vector3.Right, Mathf.Abs(Mathf.Sin(_walkPhase)) * -0.04f * b * (1f - eb)) *
            new Quaternion(Vector3.Forward, weightShift * 0.04f * (1f - eb)));

        // ── Retargeter override ─────────────────────────────────────────────────────
        // If the retargeter has a clip for the current state, it overrides the procedural
        // rotations we just set. For states without a clip (idle, wave), the procedural
        // animation stays and the retargeter fades out gracefully.
        if (_retargeterReady)
        {
            _retargeter.SetState(rState);
            // fallbackWeight: 0 = retargeter fully overrides, 1 = procedural stays
            float fw = hasClip ? 0f : 1f;
            _retargeter.Update(delta, fw);
        }
    }

    /// Map movement + emote state to a retargeter animation state.
    private AnimRetargeter.State DetermineState(float speed, bool onFloor, bool crouching, bool sprinting)
    {
        // Emotes take priority when standing still.
        if (_emote != Emote.None && _emoteBlend > 0.3f && speed < 0.5f && onFloor)
        {
            return _emote switch
            {
                Emote.Sit => AnimRetargeter.State.Sit,
                Emote.Dance => AnimRetargeter.State.Dance,
                Emote.Wave => AnimRetargeter.State.Wave,
                _ => AnimRetargeter.State.Idle,
            };
        }

        if (!onFloor)
        {
            // Could refine with velocity.y for start vs air vs land; use Fall for now.
            return AnimRetargeter.State.Fall;
        }

        if (crouching)
            return speed > 0.5f ? AnimRetargeter.State.CrouchWalk : AnimRetargeter.State.CrouchIdle;

        if (speed > 0.5f)
            return sprinting ? AnimRetargeter.State.Run : AnimRetargeter.State.Walk;

        return AnimRetargeter.State.Idle;
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

    /// How far the upper arms droop from the T-pose bind toward the sides, in radians.
    /// ~66°. Lower it if a model's arms punch into the torso, raise it if they float.
    private const float ArmRestAngle = 1.15f;

    /// Rotate a bone forward/back about its parent's X axis, on top of its rest rotation.
    private void Swing(string role, float angle)
    {
        if (!_animBones.TryGetValue(role, out var ab)) return;
        Skeleton.SetBonePoseRotation(ab.Index, new Quaternion(Vector3.Right, angle) * ab.Rest);
    }

    /// Pose an upper arm: droop it down to the side (about the forward axis, `sideSign`
    /// mirrors left/right) and layer the walk/idle swing (about the side axis) on top of the
    /// rest rotation. This is what turns the T-pose bind into arms-at-sides.
    private void ApplyArm(string role, float sideSign, float swing)
    {
        if (!_animBones.TryGetValue(role, out var ab)) return;
        // Rotate about Back (not Forward): with Forward the arms swung *up* into a Y-pose;
        // Back droops them down to the sides from the T-pose bind.
        var down = new Quaternion(Vector3.Back, sideSign * ArmRestAngle);
        var fwd = new Quaternion(Vector3.Right, swing);
        Skeleton.SetBonePoseRotation(ab.Index, down * fwd * ab.Rest);
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
