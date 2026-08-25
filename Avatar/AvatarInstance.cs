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

    /// Humanoid role → bone index, exposed for the headless animation diagnostic.
    public Dictionary<string, int> RoleToBoneForDiagnostics() => _roleToBone;

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
        // Face direction: VRM models face +Z by default, but Godot's forward is -Z. Add 180°
        // to the metadata's faceYawDegrees so the avatar faces the correct way in-world.
        model.RotationDegrees = new Vector3(0, ska.Meta.FaceYawDegrees + 180f, 0);
        inst.AddChild(model);

        inst.Skeleton = FindSkeleton(model);
        if (inst.Skeleton != null) inst.ResolveHumanoid();
        else GD.PrintErr("avatar: no Skeleton3D found in imported scene");
        inst.SetupAnimation();
        inst.SetupPhysBones();
        inst.SetupToggles();

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

    /// Read this rig's current local bone rotations into wire order, for streaming to peers.
    /// Roles this avatar doesn't have are written as identity. `dst` must hold at least
    /// `HumanoidBones.Lod1.Length` entries; nothing is allocated here (hot path, 20 Hz).
    public void CaptureBonePose(Serika.Net.Codec.Quat[] dst)
    {
        if (Skeleton == null) return;
        for (int i = 0; i < HumanoidBones.Lod1.Length && i < dst.Length; i++)
        {
            int b = BoneOf(HumanoidBones.Lod1[i]);
            if (b < 0) { dst[i] = Serika.Net.Codec.Quat.Identity; continue; }
            var q = Skeleton.GetBonePoseRotation(b);
            dst[i] = new Serika.Net.Codec.Quat(q.X, q.Y, q.Z, q.W);
        }
    }

    /// Drive this rig from bone rotations received off the wire. This is what makes a remote
    /// player's crouch, emote and locomotion match what the sender actually sees, rather than
    /// being re-guessed locally from their observed velocity.
    public void ApplyBonePose(System.Collections.Generic.IReadOnlyList<Serika.Net.Codec.Quat> src)
    {
        if (Skeleton == null || src == null) return;
        int n = System.Math.Min(src.Count, HumanoidBones.Lod1.Length);
        for (int i = 0; i < n; i++)
        {
            int b = BoneOf(HumanoidBones.Lod1[i]);
            if (b < 0) continue;
            var q = src[i];
            var rot = new Quaternion(q.X, q.Y, q.Z, q.W);
            if (!rot.IsNormalized()) rot = rot.Normalized();
            Skeleton.SetBonePoseRotation(b, rot);
        }
    }

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

    /// Hide the head, face, hair, and head accessories in first-person view so they don't block the camera.
    public void SetHeadVisible(bool visible)
    {
        if (Skeleton == null) return;
        var headBones = GetHeadBoneSet();
        var scale = visible ? Vector3.One : new Vector3(1e-3f, 1e-3f, 1e-3f);
        foreach (int bone in headBones)
        {
            Skeleton.SetBonePoseScale(bone, scale);
        }
    }

    /// Returns the set of bones that are head, face, hair, or head accessory bones.
    public System.Collections.Generic.HashSet<int> GetHeadBoneSet()
    {
        var result = new System.Collections.Generic.HashSet<int>();
        if (Skeleton == null) return result;
        int head = BoneOf("head");
        if (head >= 0) result.Add(head);

        string[] headKeywords = { "hair", "head", "face", "bang", "eye", "ear", "halo", "horn", "glasses", "cap", "hat", "ribbon", "頭", "髪", "顔", "目", "耳" };

        for (int i = 0; i < Skeleton.GetBoneCount(); i++)
        {
            if (i == head) continue;
            // Check if descendant of head
            int p = Skeleton.GetBoneParent(i);
            bool isDescendant = false;
            while (p >= 0)
            {
                if (p == head) { isDescendant = true; break; }
                p = Skeleton.GetBoneParent(p);
            }

            if (isDescendant)
            {
                result.Add(i);
                continue;
            }

            // Also check bone name for head/hair/face keywords
            string name = Skeleton.GetBoneName(i).ToLowerInvariant();
            foreach (var kw in headKeywords)
            {
                if (name.Contains(kw)) { result.Add(i); break; }
            }
        }
        return result;
    }

    /// Returns true if a MeshInstance3D is skinned primarily to head/hair bones or has hair/face in its name.
    /// Used to separate head & hair meshes (culled in first-person) from body meshes (visible).
    public bool IsHeadMesh(MeshInstance3D mesh)
    {
        if (mesh == null) return false;

        string meshName = mesh.Name.ToString().ToLowerInvariant();
        string[] meshHeadKeywords = { "hair", "face", "head", "eye", "halo", "bangs", "front_hair", "back_hair", "頭", "髪", "顔" };
        foreach (var kw in meshHeadKeywords)
        {
            if (meshName.Contains(kw)) return true;
        }

        if (Skeleton == null) return false;
        var headBones = GetHeadBoneSet();
        if (headBones.Count == 0) return false;

        var skin = mesh.Skin;
        if (skin == null) return false;

        int headCount = 0, totalCount = 0;
        for (int i = 0; i < skin.GetBindCount(); i++)
        {
            int boneIdx = skin.GetBindBone(i);
            totalCount++;
            if (headBones.Contains(boneIdx)) headCount++;
        }

        return totalCount > 0 && headCount * 10 >= totalCount * 3;
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

    // Custom, avatar-authored emote/dance clips. Kept separate from `_animPlayer` (which stays
    // null while the retargeter drives locomotion) so a creator's own clips can be played on
    // demand from the action menu without disturbing walk/run. Empty when the avatar ships none,
    // in which case the action menu falls back to the built-in emote set.
    private AnimationPlayer _customAnimPlayer;
    private readonly System.Collections.Generic.List<string> _customEmotes = new();
    private bool _customClipActive;

    /// Clip names the avatar itself provides (dances/emotes), for the action menu to list.
    public System.Collections.Generic.IReadOnlyList<string> CustomEmotes => _customEmotes;
    public bool HasCustomEmotes => _customEmotes.Count > 0;

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

    /// Emotes are chosen from the Action menu (R) — there are deliberately no key binds.
    public enum Emote
    {
        None, Sit, Dance, DanceCharleston, Bow, Greeting, Victory, VictoryFist,
        Meditate, Sleeping, Confused, Dizzy, Yes, Reject, Backflip, Shivering,
    }
    private Emote _emote = Emote.None;
    private float _emoteTime;
    private float _emoteBlend;

    /// Direction of travel relative to facing, so locomotion picks the matching clip.
    public enum MoveDirection { Forward, Back, Left, Right }
    public MoveDirection MoveDir { get; set; } = MoveDirection.Forward;

    /// Play an emote animation. Pass Emote.None to return to normal.
    public void PlayEmote(Emote e)
    {
        // A built-in emote overrides any custom clip — otherwise the two would drive the skeleton
        // at once and the avatar would twitch between them.
        if (_customClipActive) StopCustomEmote();
        _emote = e;
        _emoteTime = 0f;
    }

    public Emote CurrentEmote => _emote;

    private float _idleTime;
    private float _walkPhase;
    private float _moveBlend; // 0 = idle, 1 = walking, >1 = sprinting
    private SpringBoneSystem _springBones;
    private AvatarToggleSystem _toggles;

    /// Avatar toggle system — manages on/off state for mesh groups (VRC expression toggles).
    public AvatarToggleSystem Toggles => _toggles;

    /// Set up spring-bone physics from .ska v2 PhysBones metadata or auto-detect secondary
    /// physics bones (hair, skirt, ears, tail, breasts, ribbons) if metadata is missing.
    /// Character creators who supply their own PhysBones metadata override auto-detection,
    /// and creators can also set disableAutoPhysBones=true to disable physics entirely.
    private void SetupPhysBones()
    {
        if (Skeleton == null) return;

        var list = Meta?.PhysBones;
        var colliders = Meta?.PhysBoneColliders;

        // If creator authored custom PhysBones (list.Count > 0), their custom setup is used!
        // If list is empty/null, run auto-detection UNLESS creator set disableAutoPhysBones=true.
        if ((list == null || list.Count == 0) && Meta?.DisableAutoPhysBones != true)
        {
            list = AutoDetectPhysBones();
        }

        if (list == null || list.Count == 0) return;

        _springBones = new SpringBoneSystem { Name = "SpringBones" };
        AddChild(_springBones);
        _springBones.Setup(Skeleton, list, colliders);
    }

    /// Scan the skeleton for bones with secondary physics keywords and auto-generate PhysBone chains.
    private System.Collections.Generic.List<PhysBoneMeta> AutoDetectPhysBones()
    {
        var result = new System.Collections.Generic.List<PhysBoneMeta>();
        if (Skeleton == null) return result;

        string[] keywords = {
            "hair", "skirt", "ear", "tail", "bust", "breast", "titty", "mune", "oppai", "boob",
            "cleavage", "ribbon", "cape", "wing", "sleeve", "胸", "乳", "髪", "耳", "尾", "スカート", "リボン", "袖"
        };

        string[] breastKeywords = { "bust", "breast", "titty", "mune", "oppai", "boob", "cleavage", "胸", "乳" };

        for (int i = 0; i < Skeleton.GetBoneCount(); i++)
        {
            string name = Skeleton.GetBoneName(i).ToLowerInvariant();
            bool matches = false;
            foreach (var kw in keywords)
            {
                if (name.Contains(kw)) { matches = true; break; }
            }

            if (!matches) continue;

            // Only pick top-level chain roots: if parent also matches a keyword, skip it
            int parent = Skeleton.GetBoneParent(i);
            if (parent >= 0)
            {
                string parentName = Skeleton.GetBoneName(parent).ToLowerInvariant();
                bool parentMatches = false;
                foreach (var kw in keywords)
                {
                    if (parentName.Contains(kw)) { parentMatches = true; break; }
                }
                if (parentMatches) continue; // child of an existing chain root
            }

            bool isBreast = false;
            foreach (var bkw in breastKeywords)
            {
                if (name.Contains(bkw)) { isBreast = true; break; }
            }

            result.Add(new PhysBoneMeta
            {
                Name = Skeleton.GetBoneName(i),
                RootTransform = Skeleton.GetBoneName(i),
                Stiffness = isBreast ? 0.35f : 0.65f,
                Gravity = isBreast ? 0.15f : 0.12f,
                Force = isBreast ? 1.25f : 1.0f,
                Pull = isBreast ? 0.35f : 0.35f,
                Spring = isBreast ? 0.75f : 0.65f,
                Damping = isBreast ? 0.08f : 0.22f,
                MaxStretch = 0.05f,
                IsGrabbable = true,
                IsPosable = false,
            });
        }

        if (result.Count > 0)
            GD.Print($"AvatarInstance: auto-detected {result.Count} spring-bone chains for avatar");

        return result;
    }

    /// Set up avatar toggles from .ska v2 metadata. No-op for v1 files.
    private void SetupToggles()
    {
        if (Meta?.Toggles == null || Meta.Toggles.Count == 0) return;
        _toggles = new AvatarToggleSystem(this);
    }

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

        CollectCustomEmotes();
    }

    // Clip-name fragments that mean "locomotion", not "emote" — excluded from the custom list so
    // the action menu doesn't offer "Walk" as a dance.
    private static readonly string[] LocomotionWords =
        { "idle", "walk", "run", "loco", "stand", "jump", "fall", "crouch", "strafe", "sprint",
          "tpose", "t-pose", "bind", "rest", "apose", "a-pose" };

    /// Find the avatar's own AnimationPlayer and register every non-locomotion clip as a custom
    /// emote. This is how a creator's authored dances reach the action menu — they just ship the
    /// clips in the model. No clips → `_customEmotes` stays empty and the menu uses the defaults.
    private void CollectCustomEmotes()
    {
        _customAnimPlayer = FindAnimPlayer(_model);
        if (_customAnimPlayer == null) return;

        foreach (string name in _customAnimPlayer.GetAnimationList())
        {
            string n = name.ToLowerInvariant();
            bool locomotion = false;
            foreach (var w in LocomotionWords)
                if (n.Contains(w)) { locomotion = true; break; }
            if (!locomotion) _customEmotes.Add(name);
        }
    }

    /// Play one of the avatar's own clips by name (from `CustomEmotes`). While it runs, the
    /// procedural/retargeter animation yields so the two don't fight over the skeleton; it ends
    /// on its own, or when `StopCustomEmote` / any built-in `PlayEmote` is called.
    public void PlayCustomEmote(string clipName)
    {
        if (_customAnimPlayer == null || string.IsNullOrEmpty(clipName)) return;
        if (!_customAnimPlayer.HasAnimation(clipName)) return;

        _emote = Emote.None;           // cancel any built-in emote
        _customClipActive = true;
        if (!_customAnimPlayer.IsConnected(AnimationPlayer.SignalName.AnimationFinished,
                Callable.From<StringName>(OnCustomClipFinished)))
            _customAnimPlayer.AnimationFinished += OnCustomClipFinished;
        _customAnimPlayer.Play(clipName);
    }

    public void StopCustomEmote()
    {
        if (!_customClipActive) return;
        _customClipActive = false;
        _customAnimPlayer?.Stop();
    }

    private void OnCustomClipFinished(StringName _) => _customClipActive = false;

    /// Advance the avatar's animation. `speed` is planar m/s; pass 0 when standing still.
    /// `crouching` and `sprinting` refine the state for clip selection.
    /// Call every frame from the owning node (`_PhysicsProcess`/`_Process`).
    public void Animate(double delta, float speed, bool onFloor, bool crouching = false, bool sprinting = false)
    {
        if (Skeleton == null || _animBones.Count == 0) return;
        if (_customClipActive) return;   // a custom clip owns the skeleton this frame
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
                Emote.Sit             => AnimRetargeter.State.Sit,
                Emote.Dance           => AnimRetargeter.State.Dance,
                Emote.DanceCharleston => AnimRetargeter.State.DanceCharleston,
                Emote.Bow             => AnimRetargeter.State.Bow,
                Emote.Greeting        => AnimRetargeter.State.Greeting,
                Emote.Victory         => AnimRetargeter.State.Victory,
                Emote.VictoryFist     => AnimRetargeter.State.VictoryFist,
                Emote.Meditate        => AnimRetargeter.State.Meditate,
                Emote.Sleeping        => AnimRetargeter.State.Sleeping,
                Emote.Confused        => AnimRetargeter.State.Confused,
                Emote.Dizzy           => AnimRetargeter.State.Dizzy,
                Emote.Yes             => AnimRetargeter.State.Yes,
                Emote.Reject          => AnimRetargeter.State.Reject,
                Emote.Backflip        => AnimRetargeter.State.Backflip,
                Emote.Shivering       => AnimRetargeter.State.Shivering,
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
        {
            if (sprinting) return speed > 5.5f ? AnimRetargeter.State.Sprint : AnimRetargeter.State.Run;
            // Pick the clip that matches the direction of travel relative to facing, so
            // backing up and strafing don't play a forward walk.
            return MoveDir switch
            {
                MoveDirection.Back => AnimRetargeter.State.WalkBack,
                MoveDirection.Left => AnimRetargeter.State.StrafeLeft,
                MoveDirection.Right => AnimRetargeter.State.StrafeRight,
                _ => AnimRetargeter.State.Walk,
            };
        }

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
