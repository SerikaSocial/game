using System.Collections.Generic;
using Godot;

namespace SerikaSocial.Avatar;

/// Splits the LOCAL player's avatar into three render representations so first person can lose
/// every trace of its own head while every other view keeps the avatar whole.
///
/// Why three and not "hide the head":
///
///  • Godot 4 couples view visibility to shadow casting. Anything a camera cannot see, that
///    camera's shadow pass cannot see either — so hiding your head makes your own first-person
///    shadow bald. A separate ShadowsOnly copy is the only way out (see ShadowTwin).
///  • The head is not a mesh. On real rigs the face and hair ARE separate meshes, but the Body
///    mesh also bakes in hair planes, head ornaments and the jawline underside — which is why
///    mesh-level hiding still left a bow floating in view and a chin ceiling when pitching down.
///  • Filtering the real mesh in place is not an option either: an earlier attempt hid whole
///    SURFACES whenever any one triangle hugged the skull, which deleted entire clothing pieces
///    from mirrors and third person. Nothing here ever mutates the original meshes.
///
/// The three representations, all skinned to the same skeleton so they animate identically:
///
///   OTHERS  the original meshes, untouched apart from their render layer. Third person,
///           mirrors and every shadow pass render these. The local first-person camera culls
///           the whole layer.
///   FP      a per-triangle filtered COPY holding only body/clothing geometry — every triangle
///           that is head-weighted or sits inside the skull volume is left out. Rendered only
///           by the local first-person camera; casts no shadow (OTHERS/SHADOW already do).
///   SHADOW  a complete ShadowsOnly copy on a layer no camera culls, so the silhouette in the
///           shadow map keeps its head and hair even in the frame where the FP camera can see
///           neither. ShadowsOnly geometry never appears in a colour pass, so mirrors are
///           unaffected.
///
/// Local player only. RemoteAvatar must never call this — a remote avatar has no first-person
/// camera and would just pay for two extra copies.
public static class FirstPersonProxy
{
    /// Links an original mesh to its FP proxy, making Apply idempotent per avatar instance.
    private const string ProxyMeta = "serika_fp_proxy";

    /// SERIKA_DEBUG_MESHES=1 dumps per-mesh and per-surface filter counts. The thresholds below
    /// only mean anything against real rigs, and this is how they get checked.
    private static bool Debug => System.Environment.GetEnvironmentVariable("SERIKA_DEBUG_MESHES") == "1";

    // ── Triangle filter tuning ────────────────────────────────────────────────────
    // The skull sphere is grown a little past the measured scalp so the filter also takes the
    // few millimetres of fringe and ornament that sit just off the surface.
    private const float SkullGrow = 1.08f;
    private const float SkullPad = 0.04f;
    private const float SkullMaxRadius = 0.45f;
    /// Inside the skull volume, even a light head influence means head geometry (jaw underside,
    /// scalp-adjacent collar trim) — the body never reaches in there.
    private const float NearSkullHeadFrac = 0.10f;
    /// Anywhere at all: a triangle that is mostly head-driven is hair or an ornament, however far
    /// down the back it hangs. Long hair is the whole reason this rule is not distance-gated.
    private const float AnywhereHeadFrac = 0.55f;
    /// A vertex this strongly weighted to the head bone ITSELF (not its hair-bone descendants) is
    /// skull surface, and those vertices are what the skull sphere is measured from.
    private const float SkullVertexWeight = 0.5f;
    /// How far the measured skull centre may sit from the head bone's rest origin before we stop
    /// believing the measurement and fall back to the bone.
    private const float SkullCentreTolerance = 0.35f;

    /// Build the three representations for `av` and assign their layers. Safe to call twice.
    public static void Apply(AvatarInstance av, uint othersLayer, uint fpLayer, uint shadowLayer)
    {
        if (av == null) return;

        // Snapshot first: the walk below adds sibling nodes, and enumerating while adding would
        // hand us our own copies.
        var originals = new List<MeshInstance3D>();
        Collect(av, originals, othersLayer);

        var sk = av.Skeleton;
        int headBone = av.BoneOf("head");
        var headBones = av.GetHeadBoneSet();

        // Drop anything skinned to a DIFFERENT skeleton. The animation retargeter keeps
        // `locomotion.glb` instantiated inside the avatar for its clips — hidden, on its own rig.
        // It is not the player's body: its bind names resolve against nothing here, so every
        // head-weight number computed for it would come out zero and it would be copied whole.
        int foreign = 0;
        var mine = new List<MeshInstance3D>(originals.Count);
        foreach (var mi in originals)
        {
            if (sk != null && mi.Skin != null && mi.GetNodeOrNull<Skeleton3D>(mi.Skeleton) != sk)
            {
                mi.Layers = othersLayer;
                foreign++;
                continue;
            }
            mine.Add(mi);
        }

        // Rest geometry, in skeleton space, for every skinned mesh. Unskinned meshes (the
        // procedural bean's primitives) have no head weights to reason about and are copied
        // into the proxy whole.
        var rests = new Dictionary<MeshInstance3D, Rest>();
        if (sk != null && headBones.Count > 0)
            foreach (var mi in mine)
                if (TryBuildRest(mi, sk, headBones, headBone) is { } r)
                    rests[mi] = r;

        bool haveSkull = TryMeasureSkull(sk, headBone, rests, out var centre, out float radius);

        int proxied = 0, dropped = 0;
        foreach (var mi in mine)
        {
            // OTHERS + SHADOW. Casting is handed ENTIRELY to the twin: leaving the original a
            // caster as well put two copies of the same triangles in the shadow map, and the
            // depth fight between them speckled the avatar's own self-shadow edges — a visible
            // change to third person, which is exactly what must not change. One caster, on a
            // layer no camera culls, means every camera's shadow pass sees the same silhouette
            // it always did, including the first-person one that cannot see the original at all.
            mi.Layers = othersLayer;
            mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            ShadowTwin.Ensure(mi, shadowLayer).Layers = shadowLayer;

            if (mi.HasMeta(ProxyMeta)) continue;

            // A mesh that is nothing but head geometry needs no filtering — it is simply absent
            // from the proxy.
            if (av.IsHeadMesh(mi)) { dropped++; mi.SetMeta(ProxyMeta, true); continue; }

            var rest = rests.GetValueOrDefault(mi);
            if (Debug)
                GD.Print($"FpProxy   '{av.GetPathTo(mi)}' visible={mi.Visible} " +
                         $"skinned={rest != null} surfaces={mi.Mesh.GetSurfaceCount()} " +
                         $"aabb={mi.GetAabb()}");

            var proxy = BuildProxy(mi, rest, haveSkull, centre, radius, fpLayer);
            mi.SetMeta(ProxyMeta, true);
            if (proxy == null) continue;
            AvatarCopy.Attach(mi, proxy);
            proxied++;
        }

        GD.Print($"FpProxy: {originals.Count} mesh(es) → others=0x{othersLayer:X} fp=0x{fpLayer:X} " +
                 $"shadow=0x{shadowLayer:X}; {dropped} head mesh(es) omitted, {foreign} foreign-rig " +
                 $"mesh(es) left alone, {proxied} filtered copies, " +
                 (haveSkull ? $"skull r={radius:F3} m at {centre}" : "NO skull volume (head-weight rule only)"));
    }

    /// Depth-first walk collecting the avatar's own meshes. Anything we generated is skipped, and
    /// non-mesh visuals (name tags and the like) are parked on the always-visible layer.
    private static void Collect(Node node, List<MeshInstance3D> into, uint othersLayer)
    {
        if (AvatarCopy.IsGenerated(node)) return;

        if (node is MeshInstance3D mi && mi.Mesh != null) into.Add(mi);
        else if (node is VisualInstance3D vi) vi.Layers = othersLayer;

        foreach (var child in node.GetChildren()) Collect(child, into, othersLayer);
    }

    // ── Rest geometry ─────────────────────────────────────────────────────────────

    /// Per-surface rest data for one skinned mesh, all in SKELETON space.
    private sealed class Rest
    {
        public Vector3[][] Pos;      // linear-blend rest position per vertex
        public float[][] HeadFrac;   // fraction of weight on the head bone or its descendants
        public float[][] SkullFrac;  // fraction of weight on the head bone itself
    }

    /// Express a skinned mesh's vertices in skeleton space by running linear-blend skinning
    /// against the REST pose: `Σ w · (boneGlobalRest · bindPose) · v`.
    ///
    /// Doing it through the skin rather than reading the raw vertex array is what makes the
    /// filter rig-agnostic. Mesh-local space depends on where the importer parented the mesh and
    /// what bind matrices the exporter wrote; the skinned result is by definition the space the
    /// skeleton's own bone rests live in, which is what the skull sphere is measured in.
    private static Rest TryBuildRest(MeshInstance3D mi, Skeleton3D sk, HashSet<int> headBones, int headBone)
    {
        if (mi.Mesh is not ArrayMesh am || mi.Skin is not Skin skin) return null;
        int binds = skin.GetBindCount();
        if (binds == 0) return null;

        var skinXf = new Transform3D[binds];
        var boneOf = new int[binds];
        for (int i = 0; i < binds; i++)
        {
            int b = skin.GetBindBone(i);
            if (b < 0)
            {
                // Godot's glTF importer writes NAMED binds, so the index is -1 by design.
                var bn = skin.GetBindName(i);
                b = bn.IsEmpty ? -1 : sk.FindBone(bn);
            }
            boneOf[i] = b;
            skinXf[i] = b >= 0 ? sk.GetBoneGlobalRest(b) * skin.GetBindPose(i) : Transform3D.Identity;
        }

        if (Debug)
        {
            int resolved = 0;
            foreach (var b in boneOf) if (b >= 0) resolved++;
            GD.Print($"FpProxy   binds '{mi.Name}': {resolved}/{binds} resolved, " +
                     $"first name='{skin.GetBindName(0)}' firstBone={skin.GetBindBone(0)}");
        }

        int surfaces = am.GetSurfaceCount();
        var rest = new Rest
        {
            Pos = new Vector3[surfaces][],
            HeadFrac = new float[surfaces][],
            SkullFrac = new float[surfaces][],
        };
        bool any = false;

        for (int s = 0; s < surfaces; s++)
        {
            if (am.SurfaceGetPrimitiveType(s) != Mesh.PrimitiveType.Triangles) continue;
            var arrays = am.SurfaceGetArrays(s);
            var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
            var weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
            if (verts.Length == 0 || bones.Length == 0 || weights.Length != bones.Length) continue;

            int inf = bones.Length / verts.Length;
            if (inf != 4 && inf != 8) continue;

            var pos = new Vector3[verts.Length];
            var head = new float[verts.Length];
            var skull = new float[verts.Length];

            for (int v = 0; v < verts.Length; v++)
            {
                Vector3 acc = Vector3.Zero;
                float total = 0f, hw = 0f, sw = 0f;
                for (int k = 0; k < inf; k++)
                {
                    float w = weights[v * inf + k];
                    if (w <= 0f) continue;
                    int bind = bones[v * inf + k];
                    if (bind < 0 || bind >= binds) continue;
                    int bone = boneOf[bind];
                    if (bone < 0) continue;

                    acc += w * (skinXf[bind] * verts[v]);
                    total += w;
                    if (headBones.Contains(bone)) hw += w;
                    if (bone == headBone) sw += w;
                }
                if (total <= 1e-5f) { pos[v] = verts[v]; continue; }
                pos[v] = acc / total;
                head[v] = hw / total;
                skull[v] = sw / total;
            }

            rest.Pos[s] = pos;
            rest.HeadFrac[s] = head;
            rest.SkullFrac[s] = skull;
            any = true;

            if (Debug)
            {
                float maxHead = 0f, maxSkull = 0f;
                for (int v = 0; v < head.Length; v++)
                {
                    maxHead = Mathf.Max(maxHead, head[v]);
                    maxSkull = Mathf.Max(maxSkull, skull[v]);
                }
                GD.Print($"FpProxy   rest '{mi.Name}' s{s}: verts={verts.Length} inf={inf} " +
                         $"maxHeadFrac={maxHead:F2} maxSkullFrac={maxSkull:F2}");
            }
        }

        if (!any) return null;
        return InSkeletonSpace(rest, sk, mi) ? rest : null;
    }

    /// Fail-closed sanity check on the space assumption: the rest geometry must land somewhere
    /// near the bones that produced it. If a rig ever breaks the assumption we would rather do no
    /// filtering at all (today's behaviour, a visible head) than carve triangles out of a torso
    /// using coordinates that mean nothing.
    private static bool InSkeletonSpace(Rest rest, Skeleton3D sk, MeshInstance3D mi)
    {
        var bones = new Aabb();
        bool first = true;
        for (int b = 0; b < sk.GetBoneCount(); b++)
        {
            var o = sk.GetBoneGlobalRest(b).Origin;
            if (first) { bones = new Aabb(o, Vector3.Zero); first = false; }
            else bones = bones.Expand(o);
        }
        if (first) return false;
        bones = bones.Grow(1.0f);

        var mesh = new Aabb();
        first = true;
        foreach (var surf in rest.Pos)
        {
            if (surf == null) continue;
            foreach (var p in surf)
            {
                if (first) { mesh = new Aabb(p, Vector3.Zero); first = false; }
                else mesh = mesh.Expand(p);
            }
        }
        if (first) return false;

        if (bones.Intersects(mesh)) return true;
        GD.PrintErr($"FpProxy: '{mi.Name}' rest bounds {mesh} do not meet the skeleton's {bones} — " +
                    "skipping first-person filtering for this mesh (fail closed, head stays visible).");
        return false;
    }

    // ── Skull volume ──────────────────────────────────────────────────────────────

    /// Measure the skull as a sphere in skeleton space from the vertices actually weighted to the
    /// head bone — face and scalp.
    ///
    /// Deliberately NOT "all vertices of the pure-head meshes": on this rig the Hair mesh is
    /// entirely head-DESCENDANT weighted but hangs to the hips, so its extent would put the
    /// centre in the chest and peg the radius at the cap. Weight on the head bone itself is the
    /// skull surface and nothing else.
    private static bool TryMeasureSkull(Skeleton3D sk, int headBone, Dictionary<MeshInstance3D, Rest> rests,
                                        out Vector3 centre, out float radius)
    {
        centre = Vector3.Zero;
        radius = 0f;
        if (sk == null || headBone < 0) return false;

        var boneOrigin = sk.GetBoneGlobalRest(headBone).Origin;

        var sum = Vector3.Zero;
        int n = 0;
        foreach (var rest in rests.Values)
            for (int s = 0; s < rest.Pos.Length; s++)
            {
                if (rest.Pos[s] == null) continue;
                for (int v = 0; v < rest.Pos[s].Length; v++)
                    if (rest.SkullFrac[s][v] >= SkullVertexWeight) { sum += rest.Pos[s][v]; n++; }
            }

        if (n < 32)
        {
            GD.Print($"FpProxy: only {n} skull-weighted vertices — falling back to the head bone origin.");
            centre = boneOrigin;
        }
        else
        {
            centre = sum / n;
            float drift = centre.DistanceTo(boneOrigin);
            if (drift > SkullCentreTolerance)
            {
                GD.PrintErr($"FpProxy: measured skull centre is {drift:F2} m from the head bone — " +
                            "using the bone origin instead.");
                centre = boneOrigin;
            }
        }

        foreach (var rest in rests.Values)
            for (int s = 0; s < rest.Pos.Length; s++)
            {
                if (rest.Pos[s] == null) continue;
                for (int v = 0; v < rest.Pos[s].Length; v++)
                    if (rest.SkullFrac[s][v] >= SkullVertexWeight)
                        radius = Mathf.Max(radius, centre.DistanceTo(rest.Pos[s][v]));
            }

        if (radius <= 0f) radius = 0.12f; // bone-only fallback: a plausible skull half-width
        radius = Mathf.Min(SkullMaxRadius, radius * SkullGrow + SkullPad);
        return true;
    }

    // ── Proxy construction ────────────────────────────────────────────────────────

    /// Copy `mi` minus the head triangles. Returns null when nothing survives the filter.
    private static MeshInstance3D BuildProxy(MeshInstance3D mi, Rest rest, bool haveSkull,
                                             Vector3 centre, float radius, uint fpLayer)
    {
        if (mi.Mesh is not ArrayMesh am)
        {
            // Primitive meshes (the procedural bean) have no weights to filter on. Share the
            // resource so first person still shows a body.
            return Dress(new MeshInstance3D { Mesh = mi.Mesh }, mi, fpLayer, null);
        }

        var outMesh = new ArrayMesh();
        var surfaceMap = new List<int>();

        for (int s = 0; s < am.GetSurfaceCount(); s++)
        {
            var arrays = am.SurfaceGetArrays(s);
            var prim = am.SurfaceGetPrimitiveType(s);
            // Preserve the 8-influence flag; everything else in the format is re-derived from the
            // arrays we hand back.
            var flags = am.SurfaceGetFormat(s) & Mesh.ArrayFormat.FlagUse8BoneWeights;

            bool filterable = rest?.Pos[s] != null && prim == Mesh.PrimitiveType.Triangles;
            if (!filterable)
            {
                if (Debug) GD.Print($"FpProxy     surface {s}: UNFILTERED (no skin weights)");
                outMesh.AddSurfaceFromArrays(prim, arrays, null, null, flags);
                surfaceMap.Add(s);
                continue;
            }

            var kept = FilterTriangles(arrays, rest, s, haveSkull, centre, radius, out int total);
            if (Debug)
                GD.Print($"FpProxy     surface {s}: kept {kept.Count / 3}/{total} triangles " +
                         $"(mat='{am.SurfaceGetMaterial(s)?.ResourceName}')");
            if (kept.Count == 0) continue; // a wholly-head surface simply has no proxy

            arrays[(int)Mesh.ArrayType.Index] = Variant.From(kept.ToArray());
            outMesh.AddSurfaceFromArrays(prim, arrays, null, null, flags);
            surfaceMap.Add(s);
        }

        if (outMesh.GetSurfaceCount() == 0) return null;

        // Materials are remapped by ORIGINAL surface index — surfaces that filtered away
        // completely shift every later index, and getting this wrong once painted an avatar in
        // the wrong textures.
        for (int i = 0; i < surfaceMap.Count; i++)
            outMesh.SurfaceSetMaterial(i, am.SurfaceGetMaterial(surfaceMap[i]));

        return Dress(new MeshInstance3D { Mesh = outMesh }, mi, fpLayer, surfaceMap);
    }

    /// Copy the source instance's rigging and materials onto a proxy node. Parenting and the
    /// skeleton path are AvatarCopy.Attach's job.
    private static MeshInstance3D Dress(MeshInstance3D proxy, MeshInstance3D src, uint fpLayer,
                                        List<int> surfaceMap)
    {
        proxy.Name = src.Name + "FpProxy";
        proxy.Skin = src.Skin;
        proxy.Layers = fpLayer;
        // OTHERS casts for every other camera and SHADOW casts for all of them including this
        // one, so a third caster here would only cost fill rate.
        proxy.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;

        if (src.MaterialOverride != null) proxy.MaterialOverride = src.MaterialOverride;
        if (surfaceMap != null)
            for (int i = 0; i < surfaceMap.Count; i++)
                proxy.SetSurfaceOverrideMaterial(i, src.GetSurfaceOverrideMaterial(surfaceMap[i]));
        else
            for (int i = 0; i < src.GetSurfaceOverrideMaterialCount(); i++)
                proxy.SetSurfaceOverrideMaterial(i, src.GetSurfaceOverrideMaterial(i));

        return proxy;
    }

    /// The filter itself. Returns the indices of the triangles that stay in first person.
    private static List<int> FilterTriangles(Godot.Collections.Array arrays, Rest rest, int surface,
                                             bool haveSkull, Vector3 centre, float radius,
                                             out int triangleCount)
    {
        var pos = rest.Pos[surface];
        var head = rest.HeadFrac[surface];

        var index = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        bool indexed = index.Length >= 3;
        int triangles = indexed ? index.Length / 3 : pos.Length / 3;
        triangleCount = triangles;

        var kept = new List<int>(triangles * 3);
        float r2 = radius * radius;

        for (int t = 0; t < triangles; t++)
        {
            int a = indexed ? index[t * 3] : t * 3;
            int b = indexed ? index[t * 3 + 1] : t * 3 + 1;
            int c = indexed ? index[t * 3 + 2] : t * 3 + 2;
            if (a >= pos.Length || b >= pos.Length || c >= pos.Length) continue;

            float frac = (head[a] + head[b] + head[c]) / 3f;
            bool omit = frac >= AnywhereHeadFrac;
            if (!omit && haveSkull && frac >= NearSkullHeadFrac)
            {
                var centroid = (pos[a] + pos[b] + pos[c]) / 3f;
                omit = centroid.DistanceSquaredTo(centre) <= r2;
            }
            if (omit) continue;

            kept.Add(a); kept.Add(b); kept.Add(c);
        }
        return kept;
    }
}
