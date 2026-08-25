using System.Collections.Generic;
using Godot;

namespace SerikaSocial.Avatar;

/// Reads the spring-bone rig the avatar's author already shipped inside the VRM.
///
/// Practically every VRM carries a hand-tuned secondary-physics setup — which bones swing,
/// how stiff they are, and (critically) the collider ladder that keeps a skirt off the legs.
/// Serika used to throw all of it away: `vrm2ska.py` never extracted it, so `SkaMeta.PhysBones`
/// was empty for every avatar and the client fell back to guessing chains from bone-name
/// keywords with a collider set that had no legs in it at all. That is precisely why Shiroko's
/// skirt hangs through her thighs while she stands still — the author's four-sphere-per-thigh
/// collider ladder was sitting unread in the file the whole time.
///
/// Reading it here, on the client, from the GLB payload means every avatar already uploaded
/// gets its real physics back with no re-conversion or re-upload. `.ska` metadata still wins
/// when present, so the offline pipeline can override this later.
///
/// Supports VRM 0.x (`extensions.VRM.secondaryAnimation`) and VRM 1.0
/// (`extensions.VRMC_springBone`). Both store offsets in the same right-handed, Y-up space as
/// the glTF node translations, which is also Godot's — verified against Shiroko's rig, whose
/// `shoulder.L` node sits at x=-0.038 and whose left-shoulder collider offsets a further
/// -0.03 outward. So no axis conversion is applied, deliberately.
public static class VrmSpringImport
{
    public sealed class Result
    {
        public List<PhysBoneMeta> Chains = new();
        public List<PhysBoneColliderMeta> Colliders = new();
        public bool Any => Chains.Count > 0;
    }

    /// Parse spring data out of an imported glTF. `skeleton` is used only to check that a named
    /// node actually became a bone — nodes that didn't (meshes, empties outside the rig) are
    /// dropped rather than silently producing dead chains.
    public static Result Extract(GltfState state, Skeleton3D skeleton)
    {
        var result = new Result();
        if (state == null || skeleton == null) return result;

        Godot.Collections.Dictionary json;
        try { json = state.Json; }
        catch { return result; }
        if (json == null || !json.ContainsKey("extensions")) return result;

        var extensions = json["extensions"].AsGodotDictionary();
        if (extensions == null) return result;

        var nodeNames = ReadNodeNames(json);
        if (nodeNames.Count == 0) return result;

        if (extensions.ContainsKey("VRM"))
        {
            var vrm = extensions["VRM"].AsGodotDictionary();
            if (vrm != null && vrm.ContainsKey("secondaryAnimation"))
                ExtractVrm0(vrm["secondaryAnimation"].AsGodotDictionary(), nodeNames, skeleton, result);
        }

        if (!result.Any && extensions.ContainsKey("VRMC_springBone"))
            ExtractVrm1(extensions["VRMC_springBone"].AsGodotDictionary(), nodeNames, skeleton, result);

        return result;
    }

    // ── VRM 0.x ───────────────────────────────────────────────────────────────────────────

    private static void ExtractVrm0(Godot.Collections.Dictionary sa, List<string> nodeNames,
        Skeleton3D skeleton, Result result)
    {
        if (sa == null) return;

        // Collider groups are referenced by index from the bone groups, so build them first and
        // remember the generated name of each collider per group.
        var groupColliderNames = new List<List<string>>();
        if (sa.ContainsKey("colliderGroups"))
        {
            var groups = sa["colliderGroups"].AsGodotArray();
            for (int g = 0; g < groups.Count; g++)
            {
                var names = new List<string>();
                groupColliderNames.Add(names);

                var grp = groups[g].AsGodotDictionary();
                if (grp == null) continue;

                string boneName = ResolveBone(grp, "node", nodeNames, skeleton);
                if (boneName == null) continue;

                var colliders = grp.ContainsKey("colliders") ? grp["colliders"].AsGodotArray() : new Godot.Collections.Array();
                for (int c = 0; c < colliders.Count; c++)
                {
                    var col = colliders[c].AsGodotDictionary();
                    if (col == null) continue;

                    float radius = col.ContainsKey("radius") ? (float)col["radius"] : 0f;
                    if (radius <= 0f) continue;

                    string name = $"{boneName}#c{g}_{c}";
                    result.Colliders.Add(new PhysBoneColliderMeta
                    {
                        Name = name,
                        RootTransform = boneName,
                        Radius = radius,
                        ShapeType = 0,
                        Offset = ReadXyz(col, "offset"),
                    });
                    names.Add(name);
                }
            }
        }

        if (!sa.ContainsKey("boneGroups")) return;
        var boneGroups = sa["boneGroups"].AsGodotArray();

        for (int i = 0; i < boneGroups.Count; i++)
        {
            var bg = boneGroups[i].AsGodotDictionary();
            if (bg == null || !bg.ContainsKey("bones")) continue;

            // Which colliders this group is allowed to hit. An empty list in the source means
            // "none" in VRM's model, but authors overwhelmingly leave it populated; we keep the
            // literal meaning and let the caller decide whether to widen it.
            var colliderNames = new List<string>();
            if (bg.ContainsKey("colliderGroups"))
            {
                var refs = bg["colliderGroups"].AsGodotArray();
                for (int r = 0; r < refs.Count; r++)
                {
                    int idx = (int)refs[r];
                    if (idx >= 0 && idx < groupColliderNames.Count)
                        colliderNames.AddRange(groupColliderNames[idx]);
                }
            }

            // VRM 0.x spells it "stiffiness" — the typo is in the shipped spec, not here.
            float stiffness = bg.ContainsKey("stiffiness") ? (float)bg["stiffiness"] : 1f;
            float drag = bg.ContainsKey("dragForce") ? (float)bg["dragForce"] : 0.4f;
            float gravityPower = bg.ContainsKey("gravityPower") ? (float)bg["gravityPower"] : 0f;
            float hitRadius = bg.ContainsKey("hitRadius") ? (float)bg["hitRadius"] : 0.02f;
            string comment = bg.ContainsKey("comment") ? bg["comment"].AsString() : null;

            var bones = bg["bones"].AsGodotArray();
            for (int b = 0; b < bones.Count; b++)
            {
                int nodeIdx = (int)bones[b];
                string boneName = BoneNameFor(nodeIdx, nodeNames, skeleton);
                if (boneName == null) continue;

                result.Chains.Add(new PhysBoneMeta
                {
                    Name = string.IsNullOrEmpty(comment) ? boneName : $"{comment}:{boneName}",
                    RootTransform = boneName,
                    Stiffness = stiffness,
                    Gravity = gravityPower,
                    GravityDir = ReadXyz(bg, "gravityDir") ?? new[] { 0f, -1f, 0f },
                    Damping = Mathf.Clamp(drag, 0f, 1f),
                    Radius = Mathf.Max(hitRadius, 0.001f),
                    Colliders = colliderNames,
                    IsGrabbable = true,
                    MaxAngleDegrees = 0f,
                });
            }
        }
    }

    // ── VRM 1.0 ───────────────────────────────────────────────────────────────────────────

    private static void ExtractVrm1(Godot.Collections.Dictionary sb, List<string> nodeNames,
        Skeleton3D skeleton, Result result)
    {
        if (sb == null) return;

        // Flat collider list, indexed directly and also grouped by `colliderGroups`.
        var colliderNameByIndex = new List<string>();
        if (sb.ContainsKey("colliders"))
        {
            var colliders = sb["colliders"].AsGodotArray();
            for (int c = 0; c < colliders.Count; c++)
            {
                colliderNameByIndex.Add(null);

                var col = colliders[c].AsGodotDictionary();
                if (col == null) continue;
                string boneName = ResolveBone(col, "node", nodeNames, skeleton);
                if (boneName == null || !col.ContainsKey("shape")) continue;

                var shape = col["shape"].AsGodotDictionary();
                if (shape == null) continue;

                string name = $"{boneName}#v1_{c}";
                if (shape.ContainsKey("sphere"))
                {
                    var s = shape["sphere"].AsGodotDictionary();
                    float radius = s.ContainsKey("radius") ? (float)s["radius"] : 0f;
                    if (radius <= 0f) continue;
                    bool inside = s.ContainsKey("inside") && s["inside"].AsBool();
                    result.Colliders.Add(new PhysBoneColliderMeta
                    {
                        Name = name,
                        RootTransform = boneName,
                        Radius = radius,
                        ShapeType = inside ? 2 : 0,
                        Offset = ReadArray3(s, "offset"),
                    });
                }
                else if (shape.ContainsKey("capsule"))
                {
                    var s = shape["capsule"].AsGodotDictionary();
                    float radius = s.ContainsKey("radius") ? (float)s["radius"] : 0f;
                    if (radius <= 0f) continue;
                    result.Colliders.Add(new PhysBoneColliderMeta
                    {
                        Name = name,
                        RootTransform = boneName,
                        Radius = radius,
                        ShapeType = 1,
                        Offset = ReadArray3(s, "offset"),
                        Tail = ReadArray3(s, "tail"),
                    });
                }
                else continue;

                colliderNameByIndex[c] = name;
            }
        }

        var groupColliderNames = new List<List<string>>();
        if (sb.ContainsKey("colliderGroups"))
        {
            var groups = sb["colliderGroups"].AsGodotArray();
            for (int g = 0; g < groups.Count; g++)
            {
                var names = new List<string>();
                groupColliderNames.Add(names);
                var grp = groups[g].AsGodotDictionary();
                if (grp == null || !grp.ContainsKey("colliders")) continue;
                var refs = grp["colliders"].AsGodotArray();
                for (int r = 0; r < refs.Count; r++)
                {
                    int idx = (int)refs[r];
                    if (idx >= 0 && idx < colliderNameByIndex.Count && colliderNameByIndex[idx] != null)
                        names.Add(colliderNameByIndex[idx]);
                }
            }
        }

        if (!sb.ContainsKey("springs")) return;
        var springs = sb["springs"].AsGodotArray();

        for (int i = 0; i < springs.Count; i++)
        {
            var spring = springs[i].AsGodotDictionary();
            if (spring == null || !spring.ContainsKey("joints")) continue;

            var colliderNames = new List<string>();
            if (spring.ContainsKey("colliderGroups"))
            {
                var refs = spring["colliderGroups"].AsGodotArray();
                for (int r = 0; r < refs.Count; r++)
                {
                    int idx = (int)refs[r];
                    if (idx >= 0 && idx < groupColliderNames.Count)
                        colliderNames.AddRange(groupColliderNames[idx]);
                }
            }

            // VRM 1.0 tunes every joint individually. Serika's chain model is per-chain, so take
            // the first joint's values as the chain's — it is the root and dominates the motion.
            var joints = spring["joints"].AsGodotArray();
            if (joints.Count == 0) continue;
            var first = joints[0].AsGodotDictionary();
            if (first == null) continue;

            string rootBone = ResolveBone(first, "node", nodeNames, skeleton);
            if (rootBone == null) continue;

            string name = spring.ContainsKey("name") ? spring["name"].AsString() : rootBone;

            result.Chains.Add(new PhysBoneMeta
            {
                Name = string.IsNullOrEmpty(name) ? rootBone : name,
                RootTransform = rootBone,
                Stiffness = first.ContainsKey("stiffness") ? (float)first["stiffness"] : 1f,
                Gravity = first.ContainsKey("gravityPower") ? (float)first["gravityPower"] : 0f,
                GravityDir = ReadArray3(first, "gravityDir") ?? new[] { 0f, -1f, 0f },
                Damping = first.ContainsKey("dragForce") ? Mathf.Clamp((float)first["dragForce"], 0f, 1f) : 0.4f,
                Radius = first.ContainsKey("hitRadius") ? Mathf.Max((float)first["hitRadius"], 0.001f) : 0.02f,
                Colliders = colliderNames,
                IsGrabbable = true,
                MaxAngleDegrees = 0f,
            });
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    private static List<string> ReadNodeNames(Godot.Collections.Dictionary json)
    {
        var names = new List<string>();
        if (!json.ContainsKey("nodes")) return names;
        var nodes = json["nodes"].AsGodotArray();
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i].AsGodotDictionary();
            names.Add(n != null && n.ContainsKey("name") ? n["name"].AsString() : "");
        }
        return names;
    }

    private static string ResolveBone(Godot.Collections.Dictionary dict, string key,
        List<string> nodeNames, Skeleton3D skeleton)
    {
        if (dict == null || !dict.ContainsKey(key)) return null;
        return BoneNameFor((int)dict[key], nodeNames, skeleton);
    }

    /// glTF node index → the name that node ended up with on the `Skeleton3D`. Godot sanitizes
    /// bone names on import (`:` and `/` become `_`), so try the raw name, then the sanitized
    /// form, then a case-insensitive sweep before giving up.
    private static string BoneNameFor(int nodeIdx, List<string> nodeNames, Skeleton3D skeleton)
    {
        if (nodeIdx < 0 || nodeIdx >= nodeNames.Count) return null;
        string raw = nodeNames[nodeIdx];
        if (string.IsNullOrEmpty(raw)) return null;

        if (skeleton.FindBone(raw) >= 0) return raw;

        string sanitized = raw.Replace(':', '_').Replace('/', '_');
        if (sanitized != raw && skeleton.FindBone(sanitized) >= 0) return sanitized;

        for (int i = 0; i < skeleton.GetBoneCount(); i++)
        {
            string bn = skeleton.GetBoneName(i);
            if (string.Equals(bn, raw, System.StringComparison.OrdinalIgnoreCase)) return bn;
        }
        return null;
    }

    /// VRM 0.x writes vectors as `{"x":..,"y":..,"z":..}`.
    private static float[] ReadXyz(Godot.Collections.Dictionary dict, string key)
    {
        if (dict == null || !dict.ContainsKey(key)) return null;
        var v = dict[key].AsGodotDictionary();
        if (v == null) return null;
        return new[]
        {
            v.ContainsKey("x") ? (float)v["x"] : 0f,
            v.ContainsKey("y") ? (float)v["y"] : 0f,
            v.ContainsKey("z") ? (float)v["z"] : 0f,
        };
    }

    /// VRM 1.0 writes vectors as plain `[x, y, z]` arrays.
    private static float[] ReadArray3(Godot.Collections.Dictionary dict, string key)
    {
        if (dict == null || !dict.ContainsKey(key)) return null;
        var a = dict[key].AsGodotArray();
        if (a.Count < 3) return null;
        return new[] { (float)a[0], (float)a[1], (float)a[2] };
    }
}
