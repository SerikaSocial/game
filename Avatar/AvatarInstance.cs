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

    /// Height of the hip bone from the feet in metres, measured from the skeleton's rest pose.
    /// Used by the seat system to place the avatar so its hips land on the seat surface.
    public float HipHeight { get; private set; } = 0.45f;

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
        if (inst.Skeleton != null) { inst.ResolveHumanoid(); inst.ResolveEyeOffset(); inst.ResolveHipHeight(); }
        else GD.PrintErr("avatar: no Skeleton3D found in imported scene");

        // Everything below is an *enhancement* to an avatar that already exists and renders. Each
        // stage is isolated so one unusual rig cannot cost the player their whole avatar — and,
        // before `AvatarLibrary.Instantiate` grew a handler, could not cost them the session by
        // escaping into `EnterHome` and leaving the loading screen up forever.
        //
        // These are the stages most likely to throw on real user content: the spring import reads
        // author-supplied VRM extension JSON, the toggle setup walks arbitrary node trees, and
        // BreastRig re-skins live vertex weights. A rig that trips one of them is usually still
        // perfectly wearable without it.
        Stage(inst, "animation", () => inst.SetupAnimation());
        // The glTF state is passed through so the avatar's own VRM spring rig can be read out of
        // it — the author's chains and, crucially, their collider ladder.
        Stage(inst, "secondary physics", () => inst.SetupPhysBones(state));
        Stage(inst, "toggles", () => inst.SetupToggles());
        // Cel-shade the flat PBR the VRM/PMX imported as. Done last so it sees the final mesh,
        // including any chest geometry BreastRig re-skinned.
        Stage(inst, "toon shading", () => ToonShading.ApplyToAvatar(model));

        return inst;
    }

    /// Run one optional post-import stage, logging and continuing if it throws.
    ///
    /// Named rather than anonymous so the log says which stage failed. "avatar failed to load" is
    /// almost useless on a user-supplied rig; "avatar: 'secondary physics' stage failed" points
    /// straight at `VrmSpringImport` and the author's `VRMC_springBone` data.
    private static void Stage(AvatarInstance inst, string what, Action body)
    {
        try { body(); }
        catch (Exception e)
        {
            GD.PrintErr($"avatar: '{what}' stage failed on '{inst.Meta?.Name ?? "?"}' " +
                        $"({e.GetType().Name}: {e.Message}) — continuing without it");
        }
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

        // The bean is a full first-class citizen of the eye-tracking first-person camera, which
        // needs the head→eyes probe resolved just like an imported rig.
        inst.ResolveEyeOffset();

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
        // Folded in here rather than called alongside at the one call site, so a second
        // construction path cannot forget it and silently leave every rig fingerless on the wire.
        ResolveFingerBones();
        ResolveFaceAndJaw();
        ResolveFootBones();
    }

    public int BoneOf(string role) => _roleToBone.GetValueOrDefault(role, -1);

    /// Read this rig's current local bone rotations into wire order, for streaming to peers.
    /// Roles this avatar doesn't have are written as identity. Nothing is allocated here
    /// (hot path, 20 Hz).
    ///
    /// **`dst.Length` selects the LOD**: pass 22 entries for a LOD1 body frame, 55 for a LOD0
    /// frame carrying eyes, jaw and fingers. Both are prefixes of the same `HumanoidBones.Full`
    /// table, so there is one loop rather than one per LOD — which is what stops the two drifting
    /// apart on the bones they share.
    public void CaptureBonePose(Serika.Net.Codec.Quat[] dst)
    {
        if (Skeleton == null) return;
        int n = System.Math.Min(dst.Length, HumanoidBones.Full.Length);
        for (int i = 0; i < n; i++)
        {
            int b = BoneOf(HumanoidBones.Full[i]);
            if (b < 0) { dst[i] = Serika.Net.Codec.Quat.Identity; continue; }
            var q = Skeleton.GetBonePoseRotation(b);
            dst[i] = new Serika.Net.Codec.Quat(q.X, q.Y, q.Z, q.W);
        }
    }

    /// Drive this rig from bone rotations received off the wire. This is what makes a remote
    /// player's crouch, emote and locomotion match what the sender actually sees, rather than
    /// being re-guessed locally from their observed velocity.
    ///
    /// `src.Count` is whatever LOD arrived — 22 or 55 — clamped to the table, so a peer sending a
    /// 22-bone body frame simply leaves this rig's fingers where they were rather than snapping
    /// them to identity every frame.
    public void ApplyBonePose(System.Collections.Generic.IReadOnlyList<Serika.Net.Codec.Quat> src)
    {
        if (Skeleton == null || src == null) return;
        int n = System.Math.Min(src.Count, HumanoidBones.Full.Length);
        for (int i = 0; i < n; i++)
        {
            int b = BoneOf(HumanoidBones.Full[i]);
            if (b < 0) continue;
            var q = src[i];
            var rot = new Quaternion(q.X, q.Y, q.Z, q.W);
            if (!rot.IsNormalized()) rot = rot.Normalized();
            Skeleton.SetBonePoseRotation(b, rot);
        }
    }

    /// Whether this rig has any finger bones worth sending at LOD0. Resolved once at load rather
    /// than probed per frame.
    ///
    /// Without this the sender would pay 232 bytes a frame to transmit 30 identity quaternions
    /// for a rig that has no fingers at all — which is most PMX conversions and a fair number of
    /// VRMs (several avatars in the local cache carry only the 22 body roles).
    public bool HasFingerBones { get; private set; }

    private void ResolveFingerBones()
    {
        for (int i = HumanoidBones.FirstFingerIndex; i < HumanoidBones.Full.Length; i++)
        {
            if (BoneOf(HumanoidBones.Full[i]) < 0) continue;
            HasFingerBones = true;
            return;
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

    /// The rest-pose offset from the head BONE to the eye midpoint, in skeleton space.
    ///
    /// Exposed because a VR body solve needs it in the opposite direction to the first-person
    /// camera: the camera asks "given this head bone, where are the eyes", while `VrAvatarIk`
    /// knows where the eyes are — the headset is there — and has to work back to where the head
    /// bone belongs. Targeting the bone at the headset instead stands the whole skeleton up by
    /// this offset, which is 8–15 cm of extra height on every avatar, and fights the play-space
    /// calibration that had already aligned the eyes correctly.
    public Vector3 EyeRestOffset => _eyeRestOffsetSkeleton;

    // The rest-pose offset from the head bone's origin to the eye midpoint, measured in
    // *skeleton* space, plus the head bone's index for per-frame queries.
    private Vector3 _eyeRestOffsetSkeleton = new(0f, 0.08f, 0f);
    private int _headBoneIdx = -1;
    private int _leftEyeBone = -1;
    private int _rightEyeBone = -1;
    private bool _eyeProbeReady;

    /// Whether this rig carries real eye bones, so the first-person viewpoint is sampled from
    /// them rather than reconstructed from the head bone. Reported by the eye diagnostics.
    public bool HasEyeBones => _leftEyeBone >= 0 || _rightEyeBone >= 0;

    /// World-space position of this rig's eye midpoint, sampled from the ANIMATED pose.
    ///
    /// When the rig has eye bones — nearly every VRM does — this is literally the midpoint of the
    /// two of them in their animated pose: exactly where the avatar is looking from, and what
    /// anyone watching the avatar would point at as its viewpoint. No offset, no reconstruction.
    ///
    /// The fallback below is for rigs with no eye bones, and for the one way the direct sample can
    /// go wrong. The head bone ORIGIN rides the animation in full — that's what makes the camera
    /// follow a run cycle's bob and a crouch's drop — but a retargeted clip can drive the head
    /// somewhere the rest pose never anticipated, and eye bones parented under it go along for the
    /// ride. `EyesPlausible` catches that; the reconstruction it falls back to rotates the measured
    /// rest offset by the head's animated YAW only, which keeps the viewpoint inside the skull no
    /// matter how hard a clip leans it (a pitch-rotated offset swings out the BACK of the skull and
    /// leaves the camera staring at the avatar's own scalp).
    public bool TryGetEyeGlobal(out Transform3D xf)
    {
        xf = Transform3D.Identity;
        if (Skeleton == null || !_eyeProbeReady || _headBoneIdx < 0) return false;
        if (!TryGetHeadGlobal(out var head)) return false;

        var sInv = Skeleton.GlobalTransform.AffineInverse();
        Vector3 headSkel = sInv * head.Origin;

        Vector3 eyeSkel = TryGetAnimatedEyeMidpoint(out var measured) && EyesPlausible(measured, headSkel)
            ? measured
            : headSkel + CurrentEyeOffsetSkeleton();

        xf = new Transform3D(Basis.Identity, Skeleton.GlobalTransform * eyeSkel);
        return true;
    }

    /// Midpoint of the animated left and right eye bones, in skeleton space. One eye is enough —
    /// a cyclops rig is still better sampled than reconstructed.
    private bool TryGetAnimatedEyeMidpoint(out Vector3 midpoint)
    {
        midpoint = Vector3.Zero;
        var sum = Vector3.Zero;
        int n = 0;
        if (_leftEyeBone >= 0) { sum += Skeleton.GetBoneGlobalPose(_leftEyeBone).Origin; n++; }
        if (_rightEyeBone >= 0) { sum += Skeleton.GetBoneGlobalPose(_rightEyeBone).Origin; n++; }
        if (n == 0) return false;
        midpoint = sum / n;
        return true;
    }

    /// Guard on the direct sample: the eyes must sit roughly where the rest pose says they do,
    /// relative to the head. Generous — this exists to catch a rig or clip driving the eye bones
    /// somewhere absurd, not to second-guess an animator.
    private bool EyesPlausible(Vector3 eyeSkel, Vector3 headSkel)
    {
        float rest = _eyeRestOffsetSkeleton.Length();
        return eyeSkel.DistanceTo(headSkel) <= rest * 2.5f + 0.05f;
    }

    /// The rest offset with only the head's animated YAW (relative to rest) applied.
    private Vector3 CurrentEyeOffsetSkeleton()
    {
        var headRest = Skeleton.GetBoneGlobalRest(_headBoneIdx);
        var headAnim = Skeleton.GetBoneGlobalPose(_headBoneIdx);

        // Rest-forward of the head, carried through the animated rotation, flattened to the
        // horizontal plane: its angle against the flat rest-forward is the lean's yaw.
        Vector3 restFwd = headRest.Basis * Vector3.Forward; restFwd.Y = 0;
        if (restFwd.LengthSquared() < 1e-6f) return _eyeRestOffsetSkeleton;
        restFwd = restFwd.Normalized();

        Quaternion qAnim = headAnim.Basis.GetRotationQuaternion();
        Quaternion qRest = headRest.Basis.GetRotationQuaternion();
        Vector3 animFwd = qAnim * qRest.Inverse() * restFwd; animFwd.Y = 0;
        if (animFwd.LengthSquared() < 1e-6f) return _eyeRestOffsetSkeleton;

        float yaw = restFwd.SignedAngleTo(animFwd.Normalized(), Vector3.Up);
        return new Quaternion(Vector3.Up, yaw) * _eyeRestOffsetSkeleton;
    }

    private void ResolveEyeOffset()
    {
        int head = BoneOf("head");
        if (Skeleton == null || head < 0) return;
        _headBoneIdx = head;

        var headRest = Skeleton.GetBoneGlobalRest(head);
        float headY = headRest.Origin.Y;

        int le = BoneOf("leftEye"), re = BoneOf("rightEye");
        _leftEyeBone = le;
        _rightEyeBone = re;
        Vector3 offset;
        if (le >= 0 || re >= 0)
        {
            var sum = Vector3.Zero;
            int n = 0;
            if (le >= 0) { sum += Skeleton.GetBoneGlobalRest(le).Origin; n++; }
            if (re >= 0) { sum += Skeleton.GetBoneGlobalRest(re).Origin; n++; }
            offset = sum / n - headRest.Origin;

            // The eye bones give all three axes; keep them as long as the result looks like a
            // skull-sized displacement and not like mis-authored bones on the other end of town.
            // Bones that fail this are disowned outright — if their REST pose is nonsense there is
            // no reason to trust their animated pose as a viewpoint either.
            if (offset.Length() > 0.5f)
            {
                GD.PrintErr($"avatar: eye bones sit {offset.Length():F2} m from the head bone — " +
                            "ignoring them and reconstructing the viewpoint from the head.");
                offset = new Vector3(0, 0.08f, 0);
                _leftEyeBone = _rightEyeBone = -1;
            }

            EyeOffsetY = Mathf.Clamp(offset.Y, 0.02f, 0.25f);
        }
        else
        {
            // No eye bones — fall back to the metadata's eye height. It's the avatar's height
            // less a constant rather than a measurement, so clamp it to a plausible skull's
            // worth of offset. Vertical only: guessing a forward axis too risks poking the
            // camera through faces whose head bones aren't oriented like ours.
            float y = Mathf.Clamp(EyeHeight - headY, 0.03f, 0.18f);
            offset = new Vector3(0, y, 0);
            EyeOffsetY = y;
        }

        _eyeRestOffsetSkeleton = offset;
        _eyeProbeReady = true;
        GD.Print($"avatar: first-person viewpoint = {(HasEyeBones ? "eye-bone midpoint (sampled)" : "head bone + reconstructed offset")}" +
                 $", rest offset {offset} ({offset.Length() * 100f:F1} cm)");
    }

    /// Measure the hip bone height from the skeleton's rest pose so seats can place the
    /// avatar's hips exactly on the seat surface regardless of avatar proportions.
    private void ResolveHipHeight()
    {
        int hips = BoneOf("hips");
        if (Skeleton == null || hips < 0) return;
        HipHeight = Mathf.Clamp(Skeleton.GetBoneGlobalRest(hips).Origin.Y, 0.2f, 1.2f);
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
        var perBone = new Dictionary<int, double>();
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
                perBone[bone] = (perBone.TryGetValue(bone, out var cur) ? cur : 0) + w;
            }
        }

        if (totalWeight <= 0) return false;

        double headFrac = headWeight / totalWeight;
        int dominant = -1;
        double dominantW = 0;
        foreach (var kv in perBone)
            if (kv.Value > dominantW) { dominantW = kv.Value; dominant = kv.Key; }
        double domFrac = dominantW / totalWeight;

        // Mesh-classification telemetry: SERIKA_DEBUG_MESHES=1 dumps each mesh's head-weight
        // numbers once, so the hide thresholds below can be tuned against real rigs instead of
        // guessed at (a wrong threshold either engulfs the camera in hair or deletes the body).
        if (System.Environment.GetEnvironmentVariable("SERIKA_DEBUG_MESHES") == "1")
        {
            string boneName = dominant >= 0 ? Skeleton.GetBoneName(dominant) : "(none)";
            GD.Print($"MESHCLASS {mesh.Name}: headFrac={headFrac:F3} dominant='{boneName}' " +
                     $"domFrac={domFrac:F3} surfaces={am.GetSurfaceCount()} " +
                     $"aabbY={mesh.GetAabb().Size.Y:F2}");
        }

        // ANY meaningful head weight ⇒ head-attached. This is the rule that actually clears
        // first person on real rigs: strand planes draped over the chest commonly hold 70-85%
        // of their weight on chest/shoulder bones with the rest on the skull, so strict tests
        // leave them visible — and they engulf the camera exactly when a crouch tucks the head
        // or a sprint leans it back toward the viewpoint. Hiding them costs only your own view
        // of your back hair; everyone else, mirrors and the shadow map still see every strand
        // (render layers + light masks).
        if (headFrac > 0.95) return true;

        // Dominant-bone rule: a mesh LED by a head bone is head geometry wearing a
        // body-shaped hem, even when head weight is a minority overall.
        return headBones.Contains(dominant) && domFrac > 0.4;
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
    ///
    /// `Yes` and `Reject` are routed to the additive head gesture instead of their authored
    /// full-body clips (see `PlayHeadGesture`): a nod that cancels your walk and drags your gaze
    /// off whoever you are nodding at is the wrong shape for a social space, and the clip path
    /// suppresses itself above 0.5 m/s anyway. The enum members and the retargeter's `Yes`/
    /// `Reject` clips are left in place — they remain reachable through `AnimRetargeter.State`.
    public void PlayEmote(Emote e)
    {
        if (e == Emote.Yes) { PlayHeadGesture(HeadGesture.Nod); return; }
        if (e == Emote.Reject) { PlayHeadGesture(HeadGesture.Shake); return; }

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
        // Head: deliberately STEADY. The old while-idle sine wander (nod + turn) streamed to
        // every peer, mirrored in every reflection and baked into every shadow as a continuously
        // wobbling skull — a social app reads that as a glitchy avatar, not life. Head motion
        // now comes solely from authored locomotion clips (run bounce etc.), which reads as real
        // movement instead of shaking.
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

        // ── Head aim ────────────────────────────────────────────────────────────────
        // LAST, because the retargeter owns `neck` and would overwrite an earlier write. See
        // Avatar/HeadAim.cs. Nothing happens here unless the player is looking around or has
        // asked for a gesture — this is not the free-running head sway that was removed above.
        ApplyHeadAim(dt);

        // ── Facial dynamics & Gaze ──────────────────────────────────────────────────
        UpdateFacialDynamics(dt);

        // ── Terrain Slope & Ground Foot Conformance ─────────────────────────────────
        ApplyGroundSlopeIk(dt, onFloor);
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

    // ── Gaze, Blinking & Audio-Driven Visemes ──────────────────────────────────────

    private readonly struct FaceMeshBlendShapes
    {
        public readonly MeshInstance3D Mesh;
        public readonly int Blink;
        public readonly int BlinkL;
        public readonly int BlinkR;
        public readonly int LookUp;
        public readonly int LookDown;
        public readonly int LookLeft;
        public readonly int LookRight;
        public readonly int VisemeAa;
        public readonly int VisemeIh;
        public readonly int VisemeOu;
        public readonly int VisemeEe;
        public readonly int VisemeOh;

        public FaceMeshBlendShapes(MeshInstance3D mesh)
        {
            Mesh = mesh;
            Blink = FindShape(mesh, "blink", "eyeblink", "fcl_eye_close", "blendshape1.blink");
            BlinkL = FindShape(mesh, "blink_l", "blinkleft", "eyeblink_l", "fcl_eye_close_l", "blendshape1.blink_l");
            BlinkR = FindShape(mesh, "blink_r", "blinkright", "eyeblink_r", "fcl_eye_close_r", "blendshape1.blink_r");
            LookUp = FindShape(mesh, "lookup", "look_up", "eye_up", "fcl_eye_up");
            LookDown = FindShape(mesh, "lookdown", "look_down", "eye_down", "fcl_eye_down");
            LookLeft = FindShape(mesh, "lookleft", "look_left", "eye_left", "fcl_eye_left");
            LookRight = FindShape(mesh, "lookright", "look_right", "eye_right", "fcl_eye_right");
            VisemeAa = FindShape(mesh, "aa", "a", "vrm.aa", "fcl_mth_a", "mouth_open", "jaw_open");
            VisemeIh = FindShape(mesh, "ih", "i", "vrm.ih", "fcl_mth_i");
            VisemeOu = FindShape(mesh, "ou", "u", "vrm.ou", "fcl_mth_u");
            VisemeEe = FindShape(mesh, "ee", "e", "vrm.ee", "fcl_mth_e");
            VisemeOh = FindShape(mesh, "oh", "o", "vrm.oh", "fcl_mth_o");
        }

        private static int FindShape(MeshInstance3D mesh, params string[] candidates)
        {
            if (mesh?.Mesh is not ArrayMesh am) return -1;
            int count = am.GetBlendShapeCount();
            for (int i = 0; i < count; i++)
            {
                string name = am.GetBlendShapeName(i).ToString().ToLowerInvariant();
                foreach (var c in candidates)
                {
                    if (name == c || name.EndsWith("." + c) || name.EndsWith("_" + c))
                        return i;
                }
            }
            return -1;
        }
    }

    private readonly List<FaceMeshBlendShapes> _faceMeshes = new();
    private int _jawBone = -1;
    private Quaternion _restJawRot;

    // Gaze and Blink state
    private float _blinkTimer = 3.0f;
    private float _blinkProgress = 1.0f;
    private bool _isBlinking;
    private float _saccadeTimer = 1.2f;
    private Vector2 _saccadeOffset;
    private Vector2 _gazeDirection;

    // Live Viseme state
    private float _voiceVolume;
    private float _visemeAa, _visemeIh, _visemeOu, _visemeEe, _visemeOh;

    private void ResolveFaceAndJaw()
    {
        _jawBone = BoneOf("jaw");
        if (_jawBone >= 0 && Skeleton != null)
            _restJawRot = Skeleton.GetBoneGlobalRest(_jawBone).Basis.GetRotationQuaternion();

        _faceMeshes.Clear();
        var allMeshes = new List<MeshInstance3D>();
        CollectMeshInstances(this, allMeshes);
        foreach (var m in allMeshes)
        {
            if (m?.Mesh is ArrayMesh am && am.GetBlendShapeCount() > 0)
            {
                _faceMeshes.Add(new FaceMeshBlendShapes(m));
            }
        }
    }

    /// Live facial dynamics: natural spontaneous blinking, micro-saccades / ocular gaze,
    /// and audio-driven viseme mouth shapes.
    public void UpdateFacialDynamics(float dt)
    {
        if (_faceMeshes.Count == 0 && !HasEyeBones && _jawBone < 0) return;

        // 1. Spontaneous Blinking (Poisson distribution ~2.8 to 4.8s)
        _blinkTimer -= dt;
        if (_blinkTimer <= 0f)
        {
            _isBlinking = true;
            _blinkProgress = 0f;
            _blinkTimer = (float)GD.RandRange(2.8, 4.8);
        }

        float blinkWeight = 0f;
        if (_isBlinking)
        {
            _blinkProgress += dt / 0.16f; // full blink ~160ms
            if (_blinkProgress >= 1f)
            {
                _isBlinking = false;
                _blinkProgress = 1f;
                blinkWeight = 0f;
            }
            else
            {
                blinkWeight = _blinkProgress < 0.35f
                    ? _blinkProgress / 0.35f
                    : 1f - (_blinkProgress - 0.35f) / 0.65f;
                blinkWeight = Mathf.Clamp(blinkWeight, 0f, 1f);
            }
        }

        // 2. Micro-Saccadic Gaze Exploration
        _saccadeTimer -= dt;
        if (_saccadeTimer <= 0f)
        {
            float yawRange = Mathf.DegToRad(3.0f);
            float pitchRange = Mathf.DegToRad(2.0f);
            _saccadeOffset = new Vector2(
                (float)GD.RandRange(-yawRange, yawRange),
                (float)GD.RandRange(-pitchRange, pitchRange)
            );
            _saccadeTimer = (float)GD.RandRange(0.8, 2.2);
        }

        Vector2 effectiveGaze = _gazeDirection + _saccadeOffset;
        float lookUp = Mathf.Clamp(effectiveGaze.Y, 0f, 1f);
        float lookDown = Mathf.Clamp(-effectiveGaze.Y, 0f, 1f);
        float lookLeft = Mathf.Clamp(-effectiveGaze.X, 0f, 1f);
        float lookRight = Mathf.Clamp(effectiveGaze.X, 0f, 1f);

        // Apply to eye bones if present
        if (Skeleton != null && (_leftEyeBone >= 0 || _rightEyeBone >= 0))
        {
            var eyeRot = Basis.FromEuler(new Vector3(effectiveGaze.Y * 0.45f, effectiveGaze.X * 0.45f, 0f)).GetRotationQuaternion();
            if (_leftEyeBone >= 0)
            {
                var rest = Skeleton.GetBoneGlobalRest(_leftEyeBone).Basis.GetRotationQuaternion();
                Skeleton.SetBonePoseRotation(_leftEyeBone, rest * eyeRot);
            }
            if (_rightEyeBone >= 0)
            {
                var rest = Skeleton.GetBoneGlobalRest(_rightEyeBone).Basis.GetRotationQuaternion();
                Skeleton.SetBonePoseRotation(_rightEyeBone, rest * eyeRot);
            }
        }

        // Apply to Jaw bone if present
        if (_jawBone >= 0 && Skeleton != null)
        {
            float jawAngle = (_voiceVolume * 0.35f + _visemeAa * 0.25f);
            var jawPitch = new Quaternion(Vector3.Right, jawAngle);
            Skeleton.SetBonePoseRotation(_jawBone, _restJawRot * jawPitch);
        }

        // 3. Apply Blend Shapes to all face meshes
        foreach (var fm in _faceMeshes)
        {
            if (fm.Mesh == null || !GodotObject.IsInstanceValid(fm.Mesh)) continue;

            if (fm.Blink >= 0) fm.Mesh.SetBlendShapeValue(fm.Blink, blinkWeight);
            if (fm.BlinkL >= 0) fm.Mesh.SetBlendShapeValue(fm.BlinkL, blinkWeight);
            if (fm.BlinkR >= 0) fm.Mesh.SetBlendShapeValue(fm.BlinkR, blinkWeight);

            if (fm.LookUp >= 0) fm.Mesh.SetBlendShapeValue(fm.LookUp, lookUp);
            if (fm.LookDown >= 0) fm.Mesh.SetBlendShapeValue(fm.LookDown, lookDown);
            if (fm.LookLeft >= 0) fm.Mesh.SetBlendShapeValue(fm.LookLeft, lookLeft);
            if (fm.LookRight >= 0) fm.Mesh.SetBlendShapeValue(fm.LookRight, lookRight);

            if (fm.VisemeAa >= 0) fm.Mesh.SetBlendShapeValue(fm.VisemeAa, _visemeAa);
            if (fm.VisemeIh >= 0) fm.Mesh.SetBlendShapeValue(fm.VisemeIh, _visemeIh);
            if (fm.VisemeOu >= 0) fm.Mesh.SetBlendShapeValue(fm.VisemeOu, _visemeOu);
            if (fm.VisemeEe >= 0) fm.Mesh.SetBlendShapeValue(fm.VisemeEe, _visemeEe);
            if (fm.VisemeOh >= 0) fm.Mesh.SetBlendShapeValue(fm.VisemeOh, _visemeOh);
        }
    }

    /// Set live audio lip-sync parameters from VoiceManager (local or remote).
    public void SetVoiceLipSync(float volume, float aa = 0f, float ih = 0f, float ou = 0f, float ee = 0f, float oh = 0f)
    {
        _voiceVolume = volume;
        _visemeAa = Mathf.Clamp(aa > 0f ? aa : volume * 0.7f, 0f, 1f);
        _visemeIh = Mathf.Clamp(ih, 0f, 1f);
        _visemeOu = Mathf.Clamp(ou, 0f, 1f);
        _visemeEe = Mathf.Clamp(ee, 0f, 1f);
        _visemeOh = Mathf.Clamp(oh, 0f, 1f);
    }

    /// Set commanded ocular gaze direction (yaw and pitch in radians relative to head forward).
    public void SetGazeDirection(Vector2 gazeRadians)
    {
        _gazeDirection = gazeRadians;
    }

    // ── Terrain Ground & Slope Foot Conformance (Genshin / AAA Style) ──────────────

    private int _hipsBone = -1;
    private Vector3 _restHipsPos = Vector3.Zero;
    private int _lUpLegBone = -1, _lLowLegBone = -1, _lFootBone = -1, _lToesBone = -1;
    private int _rUpLegBone = -1, _rLowLegBone = -1, _rFootBone = -1, _rToesBone = -1;
    private float _lLegL1, _lLegL2, _rLegL1, _rLegL2;
    private Vector3 _restDirLUp, _restDirLLow, _restDirRUp, _restDirRLow;
    private Quaternion _restRotLUp, _restRotLLow, _restRotLFoot, _restRotLToes;
    private Quaternion _restRotRUp, _restRotRLow, _restRotRFoot, _restRotRToes;
    private float _ankleHeightLeft = 0.08f, _ankleHeightRight = 0.08f;
    private float _smoothedHipDrop = 0f;
    private float _smoothedLFootDrop = 0f;
    private float _smoothedRFootDrop = 0f;
    private bool _feetBonesResolved;

    private void ResolveFootBones()
    {
        _hipsBone = BoneOf("hips");
        _lUpLegBone = BoneOf("leftUpperLeg");
        _lLowLegBone = BoneOf("leftLowerLeg");
        _lFootBone = BoneOf("leftFoot");
        _lToesBone = BoneOf("leftToes");

        _rUpLegBone = BoneOf("rightUpperLeg");
        _rLowLegBone = BoneOf("rightLowerLeg");
        _rFootBone = BoneOf("rightFoot");
        _rToesBone = BoneOf("rightToes");

        if (Skeleton != null)
        {
            if (_hipsBone >= 0) _restHipsPos = Skeleton.GetBonePosePosition(_hipsBone);
            if (_lUpLegBone >= 0 && _lLowLegBone >= 0 && _lFootBone >= 0)
            {
                var ru = Skeleton.GetBoneGlobalRest(_lUpLegBone);
                var rl = Skeleton.GetBoneGlobalRest(_lLowLegBone);
                var rf = Skeleton.GetBoneGlobalRest(_lFootBone);
                var du = rl.Origin - ru.Origin;
                var dl = rf.Origin - rl.Origin;
                _lLegL1 = du.Length();
                _lLegL2 = dl.Length();
                _restDirLUp = _lLegL1 > 1e-4f ? du / _lLegL1 : Vector3.Down;
                _restDirLLow = _lLegL2 > 1e-4f ? dl / _lLegL2 : Vector3.Down;
                _restRotLUp = ru.Basis.GetRotationQuaternion();
                _restRotLLow = rl.Basis.GetRotationQuaternion();
                _restRotLFoot = rf.Basis.GetRotationQuaternion();
                _ankleHeightLeft = Mathf.Max(0.04f, rf.Origin.Y);
            }
            if (_rUpLegBone >= 0 && _rLowLegBone >= 0 && _rFootBone >= 0)
            {
                var ru = Skeleton.GetBoneGlobalRest(_rUpLegBone);
                var rl = Skeleton.GetBoneGlobalRest(_rLowLegBone);
                var rf = Skeleton.GetBoneGlobalRest(_rFootBone);
                var du = rl.Origin - ru.Origin;
                var dl = rf.Origin - rl.Origin;
                _rLegL1 = du.Length();
                _rLegL2 = dl.Length();
                _restDirRUp = _rLegL1 > 1e-4f ? du / _rLegL1 : Vector3.Down;
                _restDirRLow = _rLegL2 > 1e-4f ? dl / _rLegL2 : Vector3.Down;
                _restRotRUp = ru.Basis.GetRotationQuaternion();
                _restRotRLow = rl.Basis.GetRotationQuaternion();
                _restRotRFoot = rf.Basis.GetRotationQuaternion();
                _ankleHeightRight = Mathf.Max(0.04f, rf.Origin.Y);
            }
            if (_lToesBone >= 0) _restRotLToes = Skeleton.GetBoneGlobalRest(_lToesBone).Basis.GetRotationQuaternion();
            if (_rToesBone >= 0) _restRotRToes = Skeleton.GetBoneGlobalRest(_rToesBone).Basis.GetRotationQuaternion();
        }
        _feetBonesResolved = true;
    }

    /// Adapt feet, legs, and pelvis to terrain slopes, ledges, and steps (Genshin / AAA ground conformance).
    public void ApplyGroundSlopeIk(float dt, bool onFloor)
    {
        if (Skeleton == null) return;
        if (!_feetBonesResolved) ResolveFootBones();
        if (_lFootBone < 0 || _rFootBone < 0) return;

        var space = Skeleton.GetWorld3D()?.DirectSpaceState;
        if (space == null) return;

        var skelXform = Skeleton.GlobalTransform;
        var skelInv = skelXform.AffineInverse();

        if (!onFloor)
        {
            // Smoothly recover rest height when in mid-air
            _smoothedHipDrop = Mathf.Lerp(_smoothedHipDrop, 0f, dt * 10f);
            _smoothedLFootDrop = Mathf.Lerp(_smoothedLFootDrop, 0f, dt * 10f);
            _smoothedRFootDrop = Mathf.Lerp(_smoothedRFootDrop, 0f, dt * 10f);
            return;
        }

        var lFootPoseWorld = skelXform * Skeleton.GetBoneGlobalPose(_lFootBone).Origin;
        var rFootPoseWorld = skelXform * Skeleton.GetBoneGlobalPose(_rFootBone).Origin;

        // Cast rays down under each foot to find true terrain / prop surface
        uint mask = 1 | (1 << 2); // World + Props/Remote
        bool hitL = ProbeTerrain(space, lFootPoseWorld, mask, out var hitLPos, out var hitLNormal);
        bool hitR = ProbeTerrain(space, rFootPoseWorld, mask, out var hitRPos, out var hitRNormal);

        float targetLDrop = hitL ? (hitLPos.Y + _ankleHeightLeft - lFootPoseWorld.Y) : 0f;
        float targetRDrop = hitR ? (hitRPos.Y + _ankleHeightRight - rFootPoseWorld.Y) : 0f;

        // Limit maximum extension and drop
        targetLDrop = Mathf.Clamp(targetLDrop, -1.0f, 0.45f);
        targetRDrop = Mathf.Clamp(targetRDrop, -1.0f, 0.45f);

        // Calculate hips drop when one foot is on a lower surface / ledge
        float targetHipDrop = Mathf.Min(0f, Mathf.Min(targetLDrop, targetRDrop)) * 0.40f;
        targetHipDrop = Mathf.Clamp(targetHipDrop, -0.22f, 0f);

        // Smooth damping
        float smoothRate = dt * 16f;
        _smoothedHipDrop = Mathf.Lerp(_smoothedHipDrop, targetHipDrop, smoothRate);
        _smoothedLFootDrop = Mathf.Lerp(_smoothedLFootDrop, targetLDrop, smoothRate);
        _smoothedRFootDrop = Mathf.Lerp(_smoothedRFootDrop, targetRDrop, smoothRate);

        // Apply Hip offset relative to bind rest position (no compounding)
        if (_hipsBone >= 0)
        {
            Skeleton.SetBonePosePosition(_hipsBone, _restHipsPos + Vector3.Up * _smoothedHipDrop);
        }

        // Solve Two-Bone IK for left leg
        if (_lUpLegBone >= 0 && _lLowLegBone >= 0 && _lLegL1 > 1e-4f)
        {
            var targetWorldL = new Vector3(lFootPoseWorld.X, lFootPoseWorld.Y + _smoothedLFootDrop, lFootPoseWorld.Z);
            var targetSkelL = skelInv * targetWorldL;
            var footRotL = FootSoleRotation(hitL ? hitLNormal : Vector3.Up, skelXform, _restRotLFoot);
            SolveTwoBoneLeg(Skeleton, _lUpLegBone, _lLowLegBone, _lFootBone, _lToesBone,
                            targetSkelL, footRotL, _lLegL1, _lLegL2,
                            _restDirLUp, _restDirLLow, _restRotLUp, _restRotLLow, _restRotLFoot, _restRotLToes);
        }

        // Solve Two-Bone IK for right leg
        if (_rUpLegBone >= 0 && _rLowLegBone >= 0 && _rLegL1 > 1e-4f)
        {
            var targetWorldR = new Vector3(rFootPoseWorld.X, rFootPoseWorld.Y + _smoothedRFootDrop, rFootPoseWorld.Z);
            var targetSkelR = skelInv * targetWorldR;
            var footRotR = FootSoleRotation(hitR ? hitRNormal : Vector3.Up, skelXform, _restRotRFoot);
            SolveTwoBoneLeg(Skeleton, _rUpLegBone, _rLowLegBone, _rFootBone, _rToesBone,
                            targetSkelR, footRotR, _rLegL1, _rLegL2,
                            _restDirRUp, _restDirRLow, _restRotRUp, _restRotRLow, _restRotRFoot, _restRotRToes);
        }
    }

    private static bool ProbeTerrain(PhysicsDirectSpaceState3D space, Vector3 footPos, uint mask,
                                     out Vector3 hitPos, out Vector3 hitNormal)
    {
        hitPos = footPos;
        hitNormal = Vector3.Up;
        var q = PhysicsRayQueryParameters3D.Create(
            footPos + Vector3.Up * 0.45f, footPos + Vector3.Down * 1.5f, mask);
        var hit = space.IntersectRay(q);
        if (hit.Count == 0) return false;
        hitPos = hit["position"].AsVector3();
        hitNormal = hit["normal"].AsVector3();
        return true;
    }

    private static Quaternion FootSoleRotation(Vector3 worldNormal, Transform3D skelXform, Quaternion restFoot)
    {
        var n = (skelXform.Basis.Inverse() * worldNormal).Normalized();
        if (n.LengthSquared() < 1e-4f || n.Dot(Vector3.Up) < 0.2f) return restFoot;

        float maxTilt = Mathf.DegToRad(45f);
        float tiltAngle = Mathf.Acos(Mathf.Clamp(Vector3.Up.Dot(n), -1f, 1f));
        if (tiltAngle > maxTilt)
        {
            var axis = Vector3.Up.Cross(n).Normalized();
            n = Vector3.Up.Rotated(axis, maxTilt);
        }

        var tilt = SwingTo(Vector3.Up, n);
        return tilt * restFoot;
    }

    private static void SolveTwoBoneLeg(Skeleton3D skel, int upperBone, int lowerBone, int footBone, int toesBone,
                                        Vector3 targetSkel, Quaternion footRot,
                                        float l1, float l2,
                                        Vector3 restDirUpper, Vector3 restDirLower,
                                        Quaternion restRotUpper, Quaternion restRotLower,
                                        Quaternion restRotFoot, Quaternion restRotToes)
    {
        var hip = skel.GetBoneGlobalPose(upperBone).Origin;
        var toTarget = targetSkel - hip;
        float dist = toTarget.Length();
        if (dist < 1e-4f) return;

        float maxExt = (l1 + l2) * 0.999f;
        float minExt = Mathf.Abs(l1 - l2) + 1e-3f;
        float d = Mathf.Clamp(dist, minExt, maxExt);
        var aim = toTarget / dist;

        float cosA = (l1 * l1 + d * d - l2 * l2) / (2f * l1 * d);
        float a = Mathf.Acos(Mathf.Clamp(cosA, -1f, 1f));

        // Lateral bend axis (+X). Knee must ALWAYS bend forward (-Z).
        var axis = Vector3.Right;
        var upperDir = aim.Rotated(axis, a).Normalized();
        if (upperDir.Z > aim.Z)
        {
            upperDir = aim.Rotated(axis, -a).Normalized();
        }

        var knee = hip + upperDir * l1;
        var lowerDir = targetSkel - knee;
        if (lowerDir.LengthSquared() < 1e-8f) return;
        lowerDir = lowerDir.Normalized();

        var upperRot = SwingTo(restDirUpper, upperDir) * restRotUpper;
        var lowerRot = SwingTo(restDirLower, lowerDir) * restRotLower;

        SetBoneGlobalRotation(skel, upperBone, upperRot);
        skel.SetBonePoseRotation(lowerBone, upperRot.Inverse() * lowerRot);

        if (footBone >= 0)
        {
            skel.SetBonePoseRotation(footBone, lowerRot.Inverse() * footRot);
        }

        if (toesBone >= 0)
        {
            var currentToes = restRotToes;
            skel.SetBonePoseRotation(toesBone, footRot.Inverse() * (footRot * currentToes));
        }
    }

    private static void SetBoneGlobalRotation(Skeleton3D skel, int bone, Quaternion globalRot)
    {
        int parent = skel.GetBoneParent(bone);
        var parentRot = parent >= 0
            ? skel.GetBoneGlobalPose(parent).Basis.GetRotationQuaternion()
            : Quaternion.Identity;
        skel.SetBonePoseRotation(bone, (parentRot.Inverse() * globalRot).Normalized());
    }

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
}
