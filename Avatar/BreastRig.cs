using System;
using System.Collections.Generic;
using Godot;

namespace SerikaSocial.Avatar;

/// Gives an avatar chest physics when its skeleton has no bones for it.
///
/// This exists because detection cannot fix the problem. Most avatars in circulation — Shiroko
/// included — simply have no breast bones in the rig at all, so scanning bone names for "bust"
/// or "mune" will keep finding nothing no matter how many keywords the list grows. The only way
/// to get motion out of geometry that isn't bound to a movable bone is to bind it: add the bones
/// and move some skin weights onto them.
///
/// What it does, once, at avatar load:
///   1. Locates the chest region from the mesh itself — no per-avatar tuning, no magic numbers
///      tied to one model's proportions. The forward axis comes from the arm bones, the lobes
///      from the most forward-protruding torso-weighted vertices on each side.
///   2. Adds `Serika_Breast_L/R` under the chest bone, plus an end bone that defines the
///      direction the spring solver swings along.
///   3. Re-skins the surrounding vertices onto the new bones, *taking* the weight from the torso
///      bones that already held them, so the influences still sum to 1 and nothing tears.
///
/// It never writes to the `.ska` — the mesh is modified in memory, for this session only. An
/// author who rigged their own chest bones, or who set `disableAutoPhysBones`, is left alone.
public static class BreastRig
{
    /// Minimum share of a vertex's weight that must already belong to the torso before any of
    /// it is moved to the chest bones. Vertices below this are arm, shoulder or neck geometry
    /// that merely sits near the chest, and rebinding them makes limbs move with the chest.
    private const float TorsoWeightGate = 0.5f;

    public const string LeftBone = "Serika_Breast_L";
    public const string RightBone = "Serika_Breast_R";

    /// Bone names that already imply an authored chest rig — if any exist, do nothing.
    private static readonly string[] ExistingBreastKeywords =
    {
        "bust", "breast", "titty", "mune", "oppai", "boob", "cleavage", "胸", "乳",
    };

    public sealed class Result
    {
        public bool Added;
        public List<PhysBoneMeta> Chains = new();
        public List<PhysBoneColliderMeta> Colliders = new();
    }

    /// Returns the spring chains to register, or an empty result if nothing was (or should be)
    /// added. `roleToBone` is the avatar's resolved humanoid map.
    public static Result Build(Node3D model, Skeleton3D skeleton, IReadOnlyDictionary<string, int> roleToBone)
    {
        var result = new Result();
        if (model == null || skeleton == null || roleToBone == null) return result;

        if (HasAuthoredBreastBones(skeleton))
        {
            // The author rigged it. Auto-detection will pick those bones up on its own.
            return result;
        }

        // Chest to hang from — prefer upperChest, which is where breast bones sit on a real rig.
        int chest = Role(roleToBone, "upperChest");
        if (chest < 0) chest = Role(roleToBone, "chest");
        if (chest < 0) chest = Role(roleToBone, "spine");
        if (chest < 0) return result;

        int leftArm = Role(roleToBone, "leftUpperArm");
        int rightArm = Role(roleToBone, "rightUpperArm");
        if (leftArm < 0 || rightArm < 0) return result;

        // Model axes, derived from the rig rather than assumed. Avatars arrive facing +Z or -Z
        // depending on the exporter, so hardcoding a forward vector silently mirrors half of
        // them; the shoulder line plus world up pins it down for every rig.
        Vector3 leftPos = skeleton.GetBoneGlobalRest(leftArm).Origin;
        Vector3 rightPos = skeleton.GetBoneGlobalRest(rightArm).Origin;
        Vector3 shoulderSpan = leftPos - rightPos;
        float shoulderWidth = shoulderSpan.Length();
        if (shoulderWidth < 1e-3f) return result;

        Vector3 rightDir = (-shoulderSpan).Normalized();          // model's right
        Vector3 forward = Vector3.Up.Cross(rightDir).Normalized(); // model's front
        if (forward.LengthSquared() < 1e-6f) return result;

        Vector3 chestPos = skeleton.GetBoneGlobalRest(chest).Origin;

        // The torso bones we are allowed to steal weight from. Anything not held by these
        // (arms, neck, clothing bones) is left exactly as the author skinned it.
        var torsoBones = new HashSet<int>();
        foreach (var role in new[] { "chest", "upperChest", "spine", "hips" })
        {
            int b = Role(roleToBone, role);
            if (b >= 0) torsoBones.Add(b);
        }
        if (torsoBones.Count == 0) return result;

        var meshes = new List<MeshInstance3D>();
        CollectSkinnedMeshes(model, skeleton, meshes);
        if (meshes.Count == 0) return result;

        // Search band: from the chest bone up to a shoulder-width above it, in front of the
        // chest. Generous enough for both a tall and a short torso, tight enough to exclude the
        // neck and the belly.
        float bandLow = chestPos.Y - shoulderWidth * 0.25f;
        float bandHigh = chestPos.Y + shoulderWidth * 0.75f;

        var leftCandidates = new List<Vector3>();
        var rightCandidates = new List<Vector3>();

        foreach (var mi in meshes)
        {
            if (mi.Mesh is not ArrayMesh am) continue;
            for (int s = 0; s < am.GetSurfaceCount(); s++)
            {
                var arrays = am.SurfaceGetArrays(s);
                var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
                var weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
                if (verts.Length == 0 || bones.Length == 0 || weights.Length != bones.Length) continue;

                int inf = bones.Length / verts.Length;
                if (inf != 4 && inf != 8) continue;

                var bindMap = BuildBindMap(mi.Skin, skeleton);
                if (bindMap == null) continue;

                for (int v = 0; v < verts.Length; v++)
                {
                    Vector3 p = verts[v];
                    if (p.Y < bandLow || p.Y > bandHigh) continue;
                    if ((p - chestPos).Dot(forward) <= 0f) continue;   // back and sides only

                    if (TorsoWeight(bones, weights, v, inf, bindMap, torsoBones) < TorsoWeightGate) continue;

                    float side = (p - chestPos).Dot(rightDir);
                    if (side > 0f) rightCandidates.Add(p);
                    else leftCandidates.Add(p);
                }
            }
        }

        // A flat-chested or heavily clothed model produces no usable lobe. Bail rather than
        // inventing one — a bone in the wrong place is worse than no bone.
        float lobeRadius = shoulderWidth * 0.28f;
        if (!FindApex(leftCandidates, chestPos, forward, out Vector3 leftApex) ||
            !FindApex(rightCandidates, chestPos, forward, out Vector3 rightApex))
            return result;

        // Reject a "lobe" that barely protrudes — that is a chest wall, not a breast, and
        // rigging it would just make the torso wobble.
        float leftProtrusion = (leftApex - chestPos).Dot(forward);
        float rightProtrusion = (rightApex - chestPos).Dot(forward);
        if (leftProtrusion < shoulderWidth * 0.12f || rightProtrusion < shoulderWidth * 0.12f)
            return result;

        // Sit the bone behind the apex, inside the lobe, so the skin rotates about the chest
        // wall the way real tissue pivots rather than swinging around its own tip.
        Vector3 leftOrigin = leftApex - forward * (lobeRadius * 0.7f);
        Vector3 rightOrigin = rightApex - forward * (lobeRadius * 0.7f);

        int leftIdx = AddBonePair(skeleton, chest, LeftBone, leftOrigin, leftApex);
        int rightIdx = AddBonePair(skeleton, chest, RightBone, rightOrigin, rightApex);
        if (leftIdx < 0 || rightIdx < 0) return result;

        int reskinned = 0;
        foreach (var mi in meshes)
            reskinned += Reskin(mi, skeleton, torsoBones,
                (leftIdx, leftOrigin, LeftBone), (rightIdx, rightOrigin, RightBone),
                forward, lobeRadius);

        if (reskinned == 0)
        {
            // Bones went in but nothing bound to them; they would animate empty space.
            GD.Print("BreastRig: no vertices could be re-skinned, skipping chest physics");
            return result;
        }

        // Tuned to read as soft tissue: a firm return to rest, low drag so it settles rather
        // than wobbling, and a real cone limit — unlike hair, a chest that swings 90° is a bug.
        foreach (var (name, _) in new[] { (LeftBone, leftIdx), (RightBone, rightIdx) })
        {
            result.Chains.Add(new PhysBoneMeta
            {
                Name = name,
                RootTransform = name,
                Stiffness = 1.6f,
                Gravity = 0.12f,
                GravityDir = new[] { 0f, -1f, 0f },
                Damping = 0.35f,
                Radius = lobeRadius * 0.5f,
                IsGrabbable = false,
                MaxAngleDegrees = 22f,
                // These bones sit inside the torso by construction, so body collision would
                // fight them every frame. The 22° cone is what bounds their travel instead.
                AllowCollision = false,
            });
        }

        result.Added = true;
        GD.Print($"BreastRig: added chest physics ({reskinned} vertices re-skinned, lobe r={lobeRadius:F3}m)");
        return result;
    }

    // ── placement ─────────────────────────────────────────────────────────────────────────

    /// The apex is the centroid of the most forward-protruding tenth of the candidates, not the
    /// single furthest vertex — one stray vertex (a button, a seam) should not decide where a
    /// bone goes.
    private static bool FindApex(List<Vector3> candidates, Vector3 chestPos, Vector3 forward, out Vector3 apex)
    {
        apex = Vector3.Zero;
        if (candidates.Count < 20) return false;

        candidates.Sort((a, b) => (b - chestPos).Dot(forward).CompareTo((a - chestPos).Dot(forward)));

        int take = Mathf.Max(candidates.Count / 10, 8);
        Vector3 sum = Vector3.Zero;
        for (int i = 0; i < take; i++) sum += candidates[i];
        apex = sum / take;
        return true;
    }

    /// Add the physics bone plus the end bone that gives the spring solver a direction to swing
    /// along. Returns the physics bone's index.
    private static int AddBonePair(Skeleton3D skeleton, int parent, string name, Vector3 origin, Vector3 tip)
    {
        if (skeleton.FindBone(name) >= 0) return -1;

        Transform3D parentGlobal = skeleton.GetBoneGlobalRest(parent);
        Transform3D parentInv = parentGlobal.AffineInverse();

        skeleton.AddBone(name);
        int idx = skeleton.GetBoneCount() - 1;
        skeleton.SetBoneParent(idx, parent);

        // Keep the parent's orientation and only offset the origin: the solver derives its swing
        // axis from the child bone's position, so the rotation here is free, and inheriting the
        // chest's basis keeps the numbers readable in a debugger.
        var rest = new Transform3D(Basis.Identity, parentInv * origin);
        skeleton.SetBoneRest(idx, rest);
        skeleton.SetBonePose(idx, rest);

        skeleton.AddBone(name + "_End");
        int endIdx = skeleton.GetBoneCount() - 1;
        skeleton.SetBoneParent(endIdx, idx);

        // The end bone's offset is the apex expressed in the new bone's space. Since the new
        // bone inherits the parent's basis, that is the parent-space delta.
        var endRest = new Transform3D(Basis.Identity, parentInv * tip - parentInv * origin);
        skeleton.SetBoneRest(endIdx, endRest);
        skeleton.SetBonePose(endIdx, endRest);

        return idx;
    }

    // ── re-skinning ───────────────────────────────────────────────────────────────────────

    /// Move weight from the torso bones onto the two new bones for every vertex inside a lobe.
    /// Returns how many vertices changed.
    private static int Reskin(MeshInstance3D mi, Skeleton3D skeleton, HashSet<int> torsoBones,
        (int Bone, Vector3 Origin, string Name) left, (int Bone, Vector3 Origin, string Name) right,
        Vector3 forward, float lobeRadius)
    {
        if (mi.Mesh is not ArrayMesh src) return 0;
        var skin = mi.Skin;
        if (skin == null) return 0;

        var bindMap = BuildBindMap(skin, skeleton);
        if (bindMap == null) return 0;

        int leftBind = EnsureBind(skin, skeleton, left.Bone, left.Name);
        int rightBind = EnsureBind(skin, skeleton, right.Bone, right.Name);
        if (leftBind < 0 || rightBind < 0) return 0;

        int changed = 0;
        var rebuilt = new ArrayMesh { BlendShapeMode = src.BlendShapeMode };

        // Blend shape names must exist on the target before any surface is added, or the
        // per-surface blend arrays are rejected and every facial expression on the avatar dies.
        for (int b = 0; b < src.GetBlendShapeCount(); b++)
            rebuilt.AddBlendShape(src.GetBlendShapeName(b));

        for (int s = 0; s < src.GetSurfaceCount(); s++)
        {
            var arrays = src.SurfaceGetArrays(s);
            var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
            var weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();

            int inf = verts.Length > 0 && bones.Length > 0 ? bones.Length / verts.Length : 0;
            if ((inf == 4 || inf == 8) && weights.Length == bones.Length)
            {
                for (int v = 0; v < verts.Length; v++)
                {
                    float wl = Falloff(verts[v], left.Origin, forward, lobeRadius);
                    float wr = Falloff(verts[v], right.Origin, forward, lobeRadius);
                    if (wl <= 0.001f && wr <= 0.001f) continue;

                    // A vertex between the lobes must not receive more than it can give.
                    float total = wl + wr;
                    if (total > 1f) { wl /= total; wr /= total; }

                    if (Transfer(bones, weights, v, inf, bindMap, torsoBones,
                            (leftBind, wl), (rightBind, wr)))
                        changed++;
                }

                arrays[(int)Mesh.ArrayType.Bones] = bones;
                arrays[(int)Mesh.ArrayType.Weights] = weights;
            }

            var blendArrays = src.SurfaceGetBlendShapeArrays(s);
            var flags = src.SurfaceGetFormat(s) & Mesh.ArrayFormat.FlagUse8BoneWeights;

            rebuilt.AddSurfaceFromArrays(src.SurfaceGetPrimitiveType(s), arrays, blendArrays,
                null, flags);
            rebuilt.SurfaceSetMaterial(rebuilt.GetSurfaceCount() - 1, src.SurfaceGetMaterial(s));
            rebuilt.SurfaceSetName(rebuilt.GetSurfaceCount() - 1, src.SurfaceGetName(s));
        }

        if (changed > 0)
        {
            mi.Mesh = rebuilt;
            // Re-assigning the skin forces Godot to rebuild the skin reference against the
            // now-larger bind list; without it the new binds are never uploaded.
            mi.Skin = skin;
        }
        return changed;
    }

    /// Weight for a vertex, as a function of where it sits relative to the breast bone.
    /// Zero at and behind the chest wall so the seam cannot tear, rising to 1 near the apex,
    /// falling off radially around the bone's forward axis.
    private static float Falloff(Vector3 p, Vector3 boneOrigin, Vector3 forward, float radius)
    {
        Vector3 d = p - boneOrigin;
        float fwd = d.Dot(forward);
        if (fwd <= 0f) return 0f;

        float radial = (d - forward * fwd).Length();
        if (radial >= radius) return 0f;

        float lateral = 1f - Smoothstep(radial / radius);
        float depth = Smoothstep(Mathf.Min(fwd / (radius * 0.5f), 1f));
        return lateral * depth;
    }

    private static float Smoothstep(float t)
    {
        t = Mathf.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// Move part of a vertex's torso weight onto the new bones, keeping the total at 1.
    /// Returns true if anything moved.
    ///
    /// Transactional: the vertex is only written if the whole transfer succeeds. An earlier
    /// version scaled the torso influences down first and bailed out if a slot could not be
    /// found afterwards, which left that vertex's weights summing to less than 1 — and a vertex
    /// whose weights don't sum to 1 collapses toward the model origin.
    private static bool Transfer(int[] bones, float[] weights, int v, int inf, int[] bindMap,
        HashSet<int> torsoBones, (int Bind, float Amount) a, (int Bind, float Amount) b)
    {
        int baseIdx = v * inf;

        // Only ever redistribute torso weight. A vertex mostly held by a shoulder or an upper
        // arm is arm geometry that happens to sit near the chest, and rebinding it is what makes
        // an avatar's arm twitch when its chest moves.
        float available = 0f;
        for (int i = 0; i < inf; i++)
        {
            int bind = bones[baseIdx + i];
            if (bind >= 0 && bind < bindMap.Length && torsoBones.Contains(bindMap[bind]))
                available += weights[baseIdx + i];
        }
        if (available < TorsoWeightGate) return false;

        float takeA = available * a.Amount;
        float takeB = available * b.Amount;
        if (takeA + takeB <= 0.001f) return false;

        // Work on a copy so a failed placement leaves the vertex untouched.
        Span<int> newBones = stackalloc int[8];
        Span<float> newWeights = stackalloc float[8];
        for (int i = 0; i < inf; i++)
        {
            int bind = bones[baseIdx + i];
            newBones[i] = bind;
            bool isTorso = bind >= 0 && bind < bindMap.Length && torsoBones.Contains(bindMap[bind]);
            newWeights[i] = weights[baseIdx + i] * (isTorso ? 1f - (a.Amount + b.Amount) : 1f);
        }

        if (takeA > 0.001f && !Place(newBones, newWeights, inf, a.Bind, takeA)) return false;
        if (takeB > 0.001f && !Place(newBones, newWeights, inf, b.Bind, takeB)) return false;

        float sum = 0f;
        for (int i = 0; i < inf; i++) sum += newWeights[i];
        if (sum <= 1e-6f) return false;

        for (int i = 0; i < inf; i++)
        {
            bones[baseIdx + i] = newBones[i];
            weights[baseIdx + i] = newWeights[i] / sum;
        }
        return true;
    }

    /// Write an influence into a vertex's slots: merge into an existing slot for the same bone,
    /// or take one that the torso scale-down has emptied. Never evicts a live influence — the
    /// one it would evict is the vertex's weakest, which on chest-edge geometry is typically the
    /// shoulder or arm, and stealing that slot drags arm vertices along with the chest. A vertex
    /// with no room is simply left alone; with a smooth falloff those are rare and read as a
    /// slightly stiffer patch, not a seam.
    private static bool Place(Span<int> bones, Span<float> weights, int inf, int bind, float weight)
    {
        for (int i = 0; i < inf; i++)
            if (bones[i] == bind && weights[i] > 0f)
            {
                weights[i] += weight;
                return true;
            }

        for (int i = 0; i < inf; i++)
            if (weights[i] <= 0.0001f)
            {
                bones[i] = bind;
                weights[i] = weight;
                return true;
            }

        return false;
    }


    // ── skin plumbing ─────────────────────────────────────────────────────────────────────

    /// Vertex `BONES` values index the skin's *bind* list, not the skeleton. Build the
    /// translation table once per skin.
    private static int[] BuildBindMap(Skin skin, Skeleton3D skeleton)
    {
        if (skin == null) return null;
        int count = skin.GetBindCount();
        if (count == 0) return null;

        var map = new int[count];
        for (int i = 0; i < count; i++)
        {
            int bone = skin.GetBindBone(i);
            if (bone < 0)
            {
                string name = skin.GetBindName(i);
                bone = string.IsNullOrEmpty(name) ? -1 : skeleton.FindBone(name);
            }
            map[i] = bone;
        }
        return map;
    }

    /// Return the bind index for `bone`, appending a bind if the skin doesn't have one yet.
    /// Skins are frequently shared between a model's meshes, so an existing bind is reused
    /// rather than duplicated — otherwise each mesh would append its own copy.
    private static int EnsureBind(Skin skin, Skeleton3D skeleton, int bone, string name)
    {
        for (int i = 0; i < skin.GetBindCount(); i++)
        {
            if (skin.GetBindBone(i) == bone) return i;
            if (skin.GetBindName(i) == name) return i;
        }

        // The bind pose maps mesh space into the bone's space, which is the inverse of where the
        // bone rests. Get this wrong and the bound vertices fly off to the origin.
        skin.AddNamedBind(name, skeleton.GetBoneGlobalRest(bone).AffineInverse());
        return skin.GetBindCount() - 1;
    }

    // ── misc ──────────────────────────────────────────────────────────────────────────────

    private static float TorsoWeight(int[] bones, float[] weights, int v, int inf, int[] bindMap,
        HashSet<int> torsoBones)
    {
        int baseIdx = v * inf;
        float sum = 0f;
        for (int i = 0; i < inf; i++)
        {
            int bind = bones[baseIdx + i];
            if (bind >= 0 && bind < bindMap.Length && torsoBones.Contains(bindMap[bind]))
                sum += weights[baseIdx + i];
        }
        return sum;
    }

    private static bool HasAuthoredBreastBones(Skeleton3D skeleton)
    {
        for (int i = 0; i < skeleton.GetBoneCount(); i++)
        {
            string n = skeleton.GetBoneName(i).ToLowerInvariant();
            foreach (var kw in ExistingBreastKeywords)
                if (n.Contains(kw)) return true;
        }
        return false;
    }

    /// Every skinned mesh driven by this skeleton. A VRM splits the character across a dozen
    /// mesh nodes (body, jacket, skirt, hair…), and the chest can span several of them — the
    /// body and whatever clothing covers it — so all of them have to be considered.
    private static void CollectSkinnedMeshes(Node node, Skeleton3D skeleton, List<MeshInstance3D> into)
    {
        // A skinned MeshInstance3D binds to the Skeleton3D it is parented under, so anything
        // skinned inside this model's subtree belongs to this rig. A VRM only ever has one.
        if (node is MeshInstance3D mi && mi.Mesh is ArrayMesh && mi.Skin != null)
            into.Add(mi);

        foreach (var child in node.GetChildren())
            CollectSkinnedMeshes(child, skeleton, into);
    }

    private static int Role(IReadOnlyDictionary<string, int> map, string role)
        => map.TryGetValue(role, out int v) ? v : -1;
}
