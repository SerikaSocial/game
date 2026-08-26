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
        if (inst.Skeleton != null) { inst.ResolveHumanoid(); inst.ResolveEyeOffset(); }
        else GD.PrintErr("avatar: no Skeleton3D found in imported scene");
        inst.SetupAnimation();
        // The glTF state is passed through so the avatar's own VRM spring rig can be read out of
        // it — the author's chains and, crucially, their collider ladder.
        inst.SetupPhysBones(state);
        inst.SetupToggles();

        // Cel-shade the flat PBR the VRM/PMX imported as. Done last so it sees the final mesh,
        // including any chest geometry BreastRig re-skinned.
        ToonShading.ApplyToAvatar(model);

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

    /// How far above the head bone this rig's eyes actually sit, in metres.
    ///
    /// The head bone in a humanoid rig is at the base of the skull, not at the eyes — putting a
    /// first-person camera on it leaves the viewpoint down around the jaw and collar, which is
    /// why looking down showed the inside of the avatar's own chest. Measured from the eye bones
    /// when the rig has them, so it scales with the avatar rather than being a fixed nudge.
    public float EyeOffsetY { get; private set; } = 0.08f;

    private void ResolveEyeOffset()
    {
        int head = BoneOf("head");
        if (Skeleton == null || head < 0) return;

        float headY = Skeleton.GetBoneGlobalRest(head).Origin.Y;

        int le = BoneOf("leftEye"), re = BoneOf("rightEye");
        if (le >= 0 && re >= 0)
        {
            float eyeY = (Skeleton.GetBoneGlobalRest(le).Origin.Y +
                          Skeleton.GetBoneGlobalRest(re).Origin.Y) * 0.5f;
            EyeOffsetY = Mathf.Clamp(eyeY - headY, 0.02f, 0.25f);
            return;
        }

        // No eye bones — fall back to the metadata's eye height. It's the avatar's height less a
        // constant rather than a measurement, so clamp it to a plausible skull's worth of offset.
        EyeOffsetY = Mathf.Clamp(EyeHeight - headY, 0.03f, 0.18f);
    }

    /// Hide the head bone in first-person view so camera isn't blocked by skull geometry.
    /// Neck, chest, shoulders, arms, hands, legs, and body remain at full scale and fully visible.
    public void SetHeadVisible(bool visible)
    {
        if (Skeleton == null) return;
        int head = BoneOf("head");
        if (head >= 0)
        {
            Skeleton.SetBonePoseScale(head, visible ? Vector3.One : new Vector3(1e-3f, 1e-3f, 1e-3f));
        }
    }

    /// Returns the set of bones that are descendants of the head bone.
    public System.Collections.Generic.HashSet<int> GetHeadBoneSet()
    {
        var result = new System.Collections.Generic.HashSet<int>();
        if (Skeleton == null) return result;
        int head = BoneOf("head");
        if (head < 0) return result;
        result.Add(head);
        for (int i = 0; i < Skeleton.GetBoneCount(); i++)
        {
            int p = Skeleton.GetBoneParent(i);
            while (p >= 0)
            {
                if (p == head) { result.Add(i); break; }
                p = Skeleton.GetBoneParent(p);
            }
        }
        return result;
    }

    private readonly Dictionary<ulong, bool> _headMeshCache = new();

    /// Whether a mesh is purely head geometry, and so safe to cull in first person.
    ///
    /// Decided by where the mesh's skin *weight* actually sits, not by which bones its skin
    /// binds. That distinction is the whole problem: a VRM skin binds the entire skeleton to
    /// every mesh — all 136 of Shiroko's bones appear in the bind list of her skirt — so any test
    /// that asks "is a spine bone bound here?" answers yes for the face too, and no mesh is ever
    /// classified as a head. The result was that in first person your own face was never culled,
    /// and looking down put the camera inside it.
    ///
    /// Weight mass has no such ambiguity: a face mesh is ~100% weighted to head descendants, a
    /// jacket ~0%, and a mesh that genuinely spans both falls in between and is left visible —
    /// which is the safe answer, since culling it would delete the body too.
    public bool IsHeadMesh(MeshInstance3D mesh)
    {
        if (mesh == null || Skeleton == null) return false;
        if (_headMeshCache.TryGetValue(mesh.GetInstanceId(), out bool cached)) return cached;

        bool result = ClassifyHeadMesh(mesh);
        _headMeshCache[mesh.GetInstanceId()] = result;
        return result;
    }

    private bool ClassifyHeadMesh(MeshInstance3D mesh)
    {
        if (mesh.Mesh is not ArrayMesh am || mesh.Skin == null) return false;

        var headBones = GetHeadBoneSet();
        if (headBones.Count == 0) return false;

        int bindCount = mesh.Skin.GetBindCount();
        var bindMap = new int[bindCount];
        for (int i = 0; i < bindCount; i++)
        {
            int b = mesh.Skin.GetBindBone(i);
            if (b < 0)
            {
                // Godot's glTF importer creates *named* binds, so the index is -1 and the bone
                // has to be looked up by name.
                var bn = mesh.Skin.GetBindName(i);
                b = bn.IsEmpty ? -1 : Skeleton.FindBone(bn);
            }
            bindMap[i] = b;
        }

        double headWeight = 0, totalWeight = 0;
        for (int s = 0; s < am.GetSurfaceCount(); s++)
        {
            var arrays = am.SurfaceGetArrays(s);
            var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
            var weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
            if (verts.Length == 0 || bones.Length == 0 || weights.Length != bones.Length) continue;

            int inf = bones.Length / verts.Length;
            if (inf != 4 && inf != 8) continue;

            for (int i = 0; i < bones.Length; i++)
            {
                int bind = bones[i];
                if (bind < 0 || bind >= bindCount) continue;
                int bone = bindMap[bind];
                if (bone < 0) continue;

                float w = weights[i];
                totalWeight += w;
                if (headBones.Contains(bone)) headWeight += w;
            }
        }

        // Strictly head. Anything with real body weight in it stays visible.
        return totalWeight > 0 && headWeight / totalWeight > 0.95;
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

    /// The avatar's secondary-motion solver, or null if this rig has no physics bones.
    /// Exposed for the headless physics diagnostic.
    public SpringBoneSystem SpringBones => _springBones;
    private AvatarToggleSystem _toggles;

    /// Avatar toggle system — manages on/off state for mesh groups (VRC expression toggles).
    public AvatarToggleSystem Toggles => _toggles;

    /// Build the avatar's secondary physics. Sources are tried in order of how much the author
    /// knew about their own model:
    ///
    ///   1. `.ska` `physBones` metadata — an explicit, offline-authored rig.
    ///   2. The VRM's own spring rig, read straight out of the embedded GLB. Nearly every VRM
    ///      has one, hand-tuned, and it carries the collider ladder that keeps a skirt off the
    ///      legs. Serika discarded this for its whole life, which is exactly why skirts clipped.
    ///   3. Keyword auto-detection over bone names — the last resort, for rigs with neither.
    ///
    /// Chest physics is then synthesized on top if the rig has no bones for it, since no amount
    /// of detection can find bones that were never authored.
    ///
    /// `disableAutoPhysBones` opts an avatar out of everything except its own explicit metadata.
    private void SetupPhysBones(GltfState state)
    {
        if (Skeleton == null) return;

        var list = Meta?.PhysBones;
        var colliders = Meta?.PhysBoneColliders;
        bool autoAllowed = Meta?.DisableAutoPhysBones != true;

        if ((list == null || list.Count == 0) && state != null)
        {
            var vrm = VrmSpringImport.Extract(state, Skeleton);
            if (vrm.Any)
            {
                list = vrm.Chains;
                if (colliders == null || colliders.Count == 0) colliders = vrm.Colliders;
                GD.Print($"AvatarInstance: using the avatar's own VRM spring rig " +
                         $"({vrm.Chains.Count} chains, {vrm.Colliders.Count} colliders)");
            }
        }

        if ((list == null || list.Count == 0) && autoAllowed)
            list = AutoDetectPhysBones();

        // Copy before appending. `list` may still be the caller's `Meta.PhysBones`, and appending
        // the synthesized chest chains to that would write them back into the avatar's metadata —
        // which then accumulates a fresh pair on every re-instantiation of the same `.ska`.
        list = list == null
            ? new System.Collections.Generic.List<PhysBoneMeta>()
            : new System.Collections.Generic.List<PhysBoneMeta>(list);

        // Chest physics: only ever synthesized, never detected, because the bones don't exist.
        if (autoAllowed)
        {
            var breasts = BreastRig.Build(_model, Skeleton, _roleToBone);
            if (breasts.Added) list.AddRange(breasts.Chains);
        }

        if (list.Count == 0) return;

        // The generated body colliders are always built, even when the author supplied their
        // own. They are a backstop, not a fallback: authored sets routinely leave the torso or
        // the legs uncovered, and the result is hair through the back or a skirt through a thigh.
        _springBones = new SpringBoneSystem { Name = "SpringBones" };
        AddChild(_springBones);
        int hips = BoneOf("hips");
        float waistY = hips >= 0 ? Skeleton.GetBoneGlobalRest(hips).Origin.Y : 0f;
        _springBones.Setup(Skeleton, list, colliders, AutoDetectColliders(), waistY);
    }

    /// Reset secondary physics to rest. Call after teleporting or respawning, or every chain
    /// reads the jump as a metres-per-frame acceleration and the avatar's hair goes horizontal.
    public void ResetPhysics() => _springBones?.NotifyTeleport();

    /// How thick each body bone actually is, measured from the avatar's own skin.
    ///
    /// For every vertex, the bone holding most of its weight is asked how far away it is; each
    /// bone's radius is then a percentile of those distances. A percentile rather than the
    /// maximum because the collider wants to sit just under the skin: a backstop flush with the
    /// surface shoves clothing off the body, and one at the maximum would enclose fingertips and
    /// hair roots too. Bones with too few vertices to be meaningful are left out, and the caller
    /// falls back to a proportion of the shoulder span for those.
    private Dictionary<int, float> MeasureBoneRadii()
    {
        var samples = new Dictionary<int, List<float>>();
        if (Skeleton == null || _model == null) return new Dictionary<int, float>();

        var meshes = new List<MeshInstance3D>();
        CollectMeshInstances(_model, meshes);

        foreach (var mi in meshes)
        {
            if (mi.Mesh is not ArrayMesh am || mi.Skin == null) continue;

            // Vertex bone indices address the skin's bind list, not the skeleton.
            int bindCount = mi.Skin.GetBindCount();
            var bindMap = new int[bindCount];
            for (int i = 0; i < bindCount; i++)
            {
                int b = mi.Skin.GetBindBone(i);
                if (b < 0)
                {
                    string bn = mi.Skin.GetBindName(i);
                    b = string.IsNullOrEmpty(bn) ? -1 : Skeleton.FindBone(bn);
                }
                bindMap[i] = b;
            }

            for (int s = 0; s < am.GetSurfaceCount(); s++)
            {
                var arrays = am.SurfaceGetArrays(s);
                var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
                var weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
                if (verts.Length == 0 || bones.Length == 0 || weights.Length != bones.Length) continue;

                int inf = bones.Length / verts.Length;
                if (inf != 4 && inf != 8) continue;

                for (int v = 0; v < verts.Length; v++)
                {
                    // Dominant influence only. A vertex split across a bone and its neighbour
                    // sits at a joint, where "how thick is this bone" has no clean answer.
                    int best = -1;
                    float bestW = 0.6f;
                    for (int k = 0; k < inf; k++)
                    {
                        float w = weights[v * inf + k];
                        if (w <= bestW) continue;
                        int bind = bones[v * inf + k];
                        if (bind < 0 || bind >= bindCount) continue;
                        bestW = w;
                        best = bindMap[bind];
                    }
                    if (best < 0) continue;

                    if (!samples.TryGetValue(best, out var list)) samples[best] = list = new List<float>();
                    list.Add(DistanceToBoneAxis(best, verts[v]));
                }
            }
        }

        var radii = new Dictionary<int, float>();
        foreach (var (bone, list) in samples)
        {
            if (list.Count < 32) continue;   // too sparse to trust
            list.Sort();
            radii[bone] = list[(int)(list.Count * 0.75f)];
        }
        return radii;
    }

    /// Perpendicular distance from a point to the segment running from a bone to its first child
    /// — the axis its collider capsule will lie along. Falls back to the bone origin for a leaf.
    private float DistanceToBoneAxis(int bone, Vector3 p)
    {
        Vector3 a = Skeleton.GetBoneGlobalRest(bone).Origin;

        int child = -1;
        for (int i = 0; i < Skeleton.GetBoneCount(); i++)
            if (Skeleton.GetBoneParent(i) == bone) { child = i; break; }
        if (child < 0) return a.DistanceTo(p);

        Vector3 ab = Skeleton.GetBoneGlobalRest(child).Origin - a;
        float lenSq = ab.LengthSquared();
        if (lenSq < 1e-8f) return a.DistanceTo(p);

        float t = Mathf.Clamp(ab.Dot(p - a) / lenSq, 0f, 1f);
        return (a + ab * t).DistanceTo(p);
    }

    private static void CollectMeshInstances(Node node, List<MeshInstance3D> into)
    {
        if (node is MeshInstance3D mi) into.Add(mi);
        foreach (var c in node.GetChildren()) CollectMeshInstances(c, into);
    }

    /// Body colliders for rigs that ship none of their own.
    ///
    /// The old set was seven spheres on the torso, head and shoulders — and nothing at all below
    /// the hips. A skirt has no torso to bounce off; the thing it needs to not pass through is
    /// the legs, so it fell straight through them. This builds capsules along the limbs and
    /// torso instead, sized from the rig's own proportions rather than constants tuned to one
    /// model's height.
    private System.Collections.Generic.List<PhysBoneColliderMeta> AutoDetectColliders()
    {
        var result = new System.Collections.Generic.List<PhysBoneColliderMeta>();
        if (Skeleton == null) return result;

        // Scale everything off the rig's actual shoulder span, so this works on a 1.2 m chibi
        // and a 2 m tall avatar without either getting a collider the size of its torso.
        int leftArm = BoneOf("leftUpperArm");
        int rightArm = BoneOf("rightUpperArm");
        float span = leftArm >= 0 && rightArm >= 0
            ? Skeleton.GetBoneGlobalRest(leftArm).Origin.DistanceTo(Skeleton.GetBoneGlobalRest(rightArm).Origin)
            : 0.3f;
        if (span < 1e-3f) span = 0.3f;

        // Where possible the radius is measured from the avatar's own mesh rather than taken as
        // a fraction of the shoulder span. A proportion that fits one body shape fits the next
        // one badly: too fat and the capsule swallows the very bones it is meant to guide,
        // pinning skirts against their angle limit; too thin and hair sails through the back.
        var measured = MeasureBoneRadii();

        // A capsule spanning a bone to its child — the right shape for a limb, and the reason a
        // skirt now slides down a thigh instead of through it.
        void AddLimb(string role, string childRole, float radiusFactor, int region)
        {
            int idx = BoneOf(role);
            int childIdx = BoneOf(childRole);
            if (idx < 0 || childIdx < 0) return;

            // Tail must be in the parent bone's local space; if the child isn't a direct
            // descendant the delta of the global rests still gives the right direction.
            Vector3 tail = Skeleton.GetBoneParent(childIdx) == idx
                ? Skeleton.GetBoneRest(childIdx).Origin
                : Skeleton.GetBoneGlobalRest(idx).AffineInverse() * Skeleton.GetBoneGlobalRest(childIdx).Origin;

            result.Add(new PhysBoneColliderMeta
            {
                Name = role,
                RootTransform = Skeleton.GetBoneName(idx),
                Radius = measured.TryGetValue(idx, out float mr) ? mr : span * radiusFactor,
                ShapeType = 1,
                Offset = new[] { 0f, 0f, 0f },
                Tail = new[] { tail.X, tail.Y, tail.Z },
                Region = region,
            });
        }

        void AddSphere(string role, float radiusFactor, Vector3 offset, int region)
        {
            int idx = BoneOf(role);
            if (idx < 0) return;
            result.Add(new PhysBoneColliderMeta
            {
                Name = role,
                RootTransform = Skeleton.GetBoneName(idx),
                Radius = measured.TryGetValue(idx, out float mr) ? mr : span * radiusFactor,
                ShapeType = 0,
                Offset = new[] { offset.X, offset.Y, offset.Z },
                Region = region,
            });
        }

        // Region tags (1 = upper, 2 = lower, 0 = both) keep the backstop from testing hair
        // against shins. Same answer either way, but paid for per joint per frame.
        const int Upper = 1, Lower = 2, Both = 0;

        // Legs — the ones that were missing, and the whole reason skirts clipped.
        AddLimb("leftUpperLeg", "leftLowerLeg", 0.30f, Lower);
        AddLimb("rightUpperLeg", "rightLowerLeg", 0.30f, Lower);
        AddLimb("leftLowerLeg", "leftFoot", 0.22f, Lower);
        AddLimb("rightLowerLeg", "rightFoot", 0.22f, Lower);

        // Torso, as one capsule from the hips to the neck rather than a stack of spheres.
        // Both halves: a skirt rides against the hips and back hair against the shoulder blades.
        AddLimb("spine", "neck", 0.42f, Both);
        AddLimb("hips", "spine", 0.44f, Both);

        // Arms, so hair and capes don't pass through them.
        AddLimb("leftUpperArm", "leftLowerArm", 0.16f, Upper);
        AddLimb("rightUpperArm", "rightLowerArm", 0.16f, Upper);
        AddLimb("leftLowerArm", "leftHand", 0.13f, Upper);
        AddLimb("rightLowerArm", "rightHand", 0.13f, Upper);

        // Head, nudged up so the sphere covers the skull rather than the jaw.
        AddSphere("head", 0.36f, new Vector3(0, span * 0.28f, 0), Upper);

        return result;
    }

    /// Scan the skeleton for bones with secondary physics keywords (hair, skirt, ears, tail, breasts, ribbons) and auto-generate PhysBone chains.
    private System.Collections.Generic.List<PhysBoneMeta> AutoDetectPhysBones()
    {
        var result = new System.Collections.Generic.List<PhysBoneMeta>();
        if (Skeleton == null) return result;

        // Matched against name *tokens*, prefix-wise — see MatchesSecondaryKeyword. "hem" and
        // "skrt" are here because they are what rigs that don't spell out "skirt" tend to use.
        string[] keywords = {
            "hair", "kami", "skirt", "skrt", "hem", "ear", "tail", "bust", "breast", "titty",
            "mune", "oppai", "boob", "cleavage", "ribbon", "cape", "coat", "wing", "sleeve",
            "scarf", "muffler", "tie", "chain", "strap", "cloth",
            "胸", "乳", "髪", "耳", "尾", "スカート", "リボン", "袖", "マフラー",
        };

        string[] breastKeywords = { "bust", "breast", "titty", "mune", "oppai", "boob", "cleavage", "胸", "乳" };

        // Things that hang below the waist and should be tested against the legs, not the head.
        string[] lowerBodyKeywords = { "skirt", "skrt", "hem", "tail", "coat", "尾", "スカート" };

        // Restricting each chain to the colliders in its own region is both cheaper and more
        // correct: testing hair against the shins costs the same as testing it against the skull
        // and can only ever produce a wrong answer. Roles that a rig doesn't have simply fail to
        // resolve later and drop out.
        var lowerColliders = new System.Collections.Generic.List<string>
            { "hips", "spine", "leftUpperLeg", "rightUpperLeg", "leftLowerLeg", "rightLowerLeg" };
        var upperColliders = new System.Collections.Generic.List<string>
            { "head", "spine", "leftUpperArm", "rightUpperArm", "leftLowerArm", "rightLowerArm" };

        // Bones the humanoid map already claims are body, not secondary motion, whatever they
        // happen to be called. This is the hard guard; the tokenizer below is the soft one.
        var humanoid = new System.Collections.Generic.HashSet<int>(_roleToBone.Values);

        for (int i = 0; i < Skeleton.GetBoneCount(); i++)
        {
            if (humanoid.Contains(i)) continue;
            if (!MatchesSecondaryKeyword(Skeleton.GetBoneName(i), keywords)) continue;

            // Only pick top-level chain roots: if the parent also matches, this is mid-chain.
            int parent = Skeleton.GetBoneParent(i);
            if (parent >= 0 && MatchesSecondaryKeyword(Skeleton.GetBoneName(parent), keywords))
                continue;

            string bone = Skeleton.GetBoneName(i);
            bool isBreast = MatchesSecondaryKeyword(bone, breastKeywords);
            bool isLower = MatchesSecondaryKeyword(bone, lowerBodyKeywords);

            // Values are in the VRM spring model the solver implements: `stiffness` is the pull
            // back toward rest in metres per second, `damping` is per-step drag, `gravity` is a
            // downward pull in the same units as stiffness. Chest tissue is stiff, low-travel and
            // angle-limited; hair and skirts are softer and free to swing, because with real
            // colliders in place a tight clamp is what makes a rig look dead.
            result.Add(new PhysBoneMeta
            {
                Name = bone,
                RootTransform = bone,
                Stiffness = isBreast ? 1.60f : 0.90f,
                Gravity = isBreast ? 0.12f : 0.20f,
                GravityDir = new[] { 0f, -1f, 0f },
                Damping = isBreast ? 0.35f : 0.40f,
                Radius = isBreast ? 0.03f : 0.02f,
                MaxAngleDegrees = isBreast ? 22f : 0f,
                IsGrabbable = !isBreast,
                IsPosable = false,
                AllowCollision = !isBreast,
                Colliders = isBreast ? new System.Collections.Generic.List<string>()
                          : isLower ? lowerColliders : upperColliders,
            });
        }

        if (result.Count > 0)
            GD.Print($"AvatarInstance: auto-detected {result.Count} spring-bone chains for avatar");

        return result;
    }

    /// Does a bone name name a secondary-motion part?
    ///
    /// Matching on raw substrings is not safe here, and the failure is not theoretical: "ear"
    /// is a substring of "for<b>ear</b>m_stretch.l", a bone real rigs ship, and treating a
    /// forearm as a spring chain leaves the avatar's arms swinging like rope. So the name is
    /// split into tokens on separators, digits and camelCase humps, and a keyword has to start
    /// a token. That still catches the shapes rigs actually use — `Hair_01`, `hair.back.001.L`,
    /// `J_Sec_Hair1_01`, `SkirtFront` — and still lets "earring" through, which should jiggle,
    /// while "forearm" tokenizes to "forearm" and is correctly left alone.
    ///
    /// CJK keywords are matched as substrings: they don't tokenize and are unambiguous anyway.
    private static bool MatchesSecondaryKeyword(string boneName, string[] keywords)
    {
        if (string.IsNullOrEmpty(boneName)) return false;
        string lower = boneName.ToLowerInvariant();

        // Non-ASCII keywords (胸, 髪, スカート…) have no token structure and are unambiguous.
        foreach (var kw in keywords)
            if (kw.Length > 0 && kw[0] > 127 && lower.Contains(kw)) return true;

        foreach (string token in TokenizeBoneName(boneName))
            foreach (var kw in keywords)
                if (kw.Length > 0 && kw[0] <= 127 && token.StartsWith(kw, StringComparison.Ordinal))
                    return true;

        return false;
    }

    /// Split a bone name into lowercase word tokens, breaking on anything that isn't a letter
    /// and on camelCase humps: `J_Sec_Hair1_01` → j, sec, hair; `SkirtFront` → skirt, front.
    private static System.Collections.Generic.List<string> TokenizeBoneName(string name)
    {
        var tokens = new System.Collections.Generic.List<string>();
        var current = new System.Text.StringBuilder();

        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (!char.IsLetter(c))
            {
                if (current.Length > 0) { tokens.Add(current.ToString().ToLowerInvariant()); current.Clear(); }
                continue;
            }
            if (current.Length > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]))
            {
                tokens.Add(current.ToString().ToLowerInvariant());
                current.Clear();
            }
            current.Append(c);
        }
        if (current.Length > 0) tokens.Add(current.ToString().ToLowerInvariant());

        return tokens;
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
