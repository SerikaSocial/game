using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SerikaSocial.Avatar;

/// Headless secondary-physics diagnostic.
///
///   Godot --headless --path game -- --serika-phystest --ska /path/to/avatar.ska
///
/// Secondary motion is exactly the kind of thing you can convince yourself works by looking at
/// it. A skirt resting on a thigh and a skirt hanging straight through it are a few pixels apart
/// in a still frame, and "the physics feels weak" is not a number. So this measures three things
/// that the eye is bad at and arithmetic is good at:
///
///   PENETRATION — signed clearance from every simulated joint to the body colliders it is meant
///     to respect. Any negative value at rest is the skirt-through-legs bug, full stop.
///
///   RESPONSE — how far the rig travels when the avatar moves. Near-zero means the springs are
///     nominally running but too weak to see, which is what "phys barely works" looked like.
///
///   SETTLING — that the rig comes back to rest and stays there rather than oscillating, which
///     is the failure mode a too-stiff or under-damped solver produces.
public static class PhysDiagnostic
{

    public static async void Run(Node host, string skaPath)
    {
        GD.Print($"PHYSTEST ska={skaPath ?? "(bean)"}");

        var avatar = AvatarLibrary.InstantiateOrDefault(skaPath);
        if (avatar == null) { Fail(host, "no avatar instance"); return; }
        host.AddChild(avatar);

        var skel = avatar.Skeleton;
        if (skel == null) { Fail(host, "avatar has no Skeleton3D"); return; }

        var spring = avatar.SpringBones;
        if (spring == null) { Fail(host, "avatar has no SpringBoneSystem — no physics bones were found"); return; }

        GD.Print($"PHYSTEST rig bones={skel.GetBoneCount()} chains={spring.ChainCount} " +
                 $"joints={spring.JointCount} colliders={spring.ColliderCount}");

        int lb = skel.FindBone(BreastRig.LeftBone);
        int rb = skel.FindBone(BreastRig.RightBone);
        bool hasBreast = lb >= 0 && rb >= 0;
        GD.Print($"PHYSTEST breastRig={(hasBreast ? "PRESENT" : "absent")}");

        // Re-skinning rewrites vertex bone indices and weights in place. Get that wrong and the
        // avatar doesn't wobble oddly, it detonates — vertices collapse to the origin or fly off
        // to infinity. That is invisible to a clearance measurement, so check the skin's
        // invariants directly: weights sum to 1, and every index points at a real bind.
        bool skinOk = CheckSkin(avatar);
        if (hasBreast)
        {
            Vector3 l = skel.GetBoneGlobalRest(lb).Origin;
            Vector3 r = skel.GetBoneGlobalRest(rb).Origin;
            GD.Print($"PHYSTEST breast bones L=({l.X:F3},{l.Y:F3},{l.Z:F3}) R=({r.X:F3},{r.Y:F3},{r.Z:F3})");

            // They must be mirrored across the body's midline and sit at the same height —
            // a placement search that latched onto an arm or a collar would show up here.
            if (Mathf.Abs(l.Y - r.Y) > 0.02f || Mathf.Abs(Mathf.Abs(l.X) - Mathf.Abs(r.X)) > 0.02f)
            {
                GD.Print("PHYSTEST FAIL breast bones are not symmetric — placement search misfired");
                skinOk = false;
            }
        }

        // ── settle at rest ────────────────────────────────────────────────────────────────
        // Let the rig come to rest from its spawn pose before measuring anything.
        for (int i = 0; i < 120; i++) spring.DebugStep(1f / 60f);

        var rest = spring.DebugClearances();
        Report("REST", rest);
        bool penetrationOk = CheckPenetration("rest", rest, 0f);

        // ── response to motion ────────────────────────────────────────────────────────────
        // Walk the avatar sideways at 2 m/s for half a second and see whether the rig notices.
        var before = Snapshot(spring);
        float peak = 0f;
        for (int i = 0; i < 30; i++)
        {
            avatar.Position += new Vector3(2f / 60f, 0, 0);
            avatar.ForceUpdateTransform();
            spring.DebugStep(1f / 60f);
            peak = Mathf.Max(peak, MaxDisplacement(before, Snapshot(spring), avatar.Position));
        }
        GD.Print($"PHYSTEST RESPONSE peak displacement from rest = {peak:F4}m");

        // A rig that moves less than a centimetre under a 2 m/s sidestep is not simulating in
        // any way a person would notice.
        bool responseOk = peak > 0.01f;
        if (!responseOk) GD.Print("PHYSTEST FAIL response: rig is effectively static");

        // Penetration must also hold *during* motion, not just at rest — that is the case that
        // actually put a skirt through a leg while walking.
        var moving = spring.DebugClearances();
        Report("MOVING", moving);
        // A 5 mm tolerance under motion: collision is resolved at discrete 60 Hz steps, so a
        // fast-moving tail can end a step marginally inside and be pushed out on the next one.
        bool movingOk = CheckPenetration("motion", moving, -0.005f);

        // ── settling ──────────────────────────────────────────────────────────────────────
        // Stop dead and confirm the rig converges instead of ringing forever.
        for (int i = 0; i < 180; i++) spring.DebugStep(1f / 60f);
        var settledA = Snapshot(spring);
        for (int i = 0; i < 30; i++) spring.DebugStep(1f / 60f);
        var settledB = Snapshot(spring);

        float residual = MaxDisplacement(settledA, settledB, Vector3.Zero);
        GD.Print($"PHYSTEST SETTLE residual motion over 0.5s after stopping = {residual:F5}m");
        bool settleOk = residual < 0.002f;
        if (!settleOk) GD.Print("PHYSTEST FAIL settle: rig is still oscillating after 3s at rest");

        // Chest motion is reported on its own. It shares the response budget with hair and
        // skirts, so a healthy overall number can still hide a chest that does nothing — and
        // equally, a chest that swings like hair is a bug, not a feature.
        if (hasBreast)
        {
            float travel = BoneTravel(avatar, spring, new[] { BreastRig.LeftBone, BreastRig.RightBone });
            GD.Print($"PHYSTEST BREAST travel under motion = {travel:F4}m");
            if (travel < 0.002f) GD.Print("PHYSTEST WARN breast rig is bound but barely moves");
            if (travel > 0.08f) GD.Print("PHYSTEST WARN breast rig travel is implausibly large");
        }

        // ── cost ──────────────────────────────────────────────────────────────────────────
        // The budget that matters is Quest's: at 72 Hz a frame is 13.9 ms for everything, and a
        // social instance can hold a dozen avatars. Measure one rig's steady-state solve so the
        // per-avatar cost is a number rather than a hope.
        const int timedFrames = 600;

        // Worst case: the avatar is moving, so nothing sleeps and every joint is solved and
        // written every frame.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < timedFrames; i++)
        {
            avatar.Position += new Vector3(0.01f, 0, 0);
            avatar.ForceUpdateTransform();
            spring.DebugStep(1f / 60f);
        }
        clock.Stop();
        double activeUs = clock.Elapsed.TotalMilliseconds * 1000.0 / timedFrames;

        // Steady state for a social app: standing still. Let it settle, then time it asleep.
        for (int i = 0; i < 120; i++) spring.DebugStep(1f / 60f);
        clock.Restart();
        for (int i = 0; i < timedFrames; i++) spring.DebugStep(1f / 60f);
        clock.Stop();
        double idleUs = clock.Elapsed.TotalMilliseconds * 1000.0 / timedFrames;

        GD.Print($"PHYSTEST COST active={activeUs:F1}us/frame idle={idleUs:F1}us/frame " +
                 $"({spring.JointCount} joints, {activeUs / Mathf.Max(spring.JointCount, 1):F2}us/joint active)");

        // ── live loop ─────────────────────────────────────────────────────────────────────
        // Everything above drives the solver by hand. That proves the maths, not the wiring:
        // if the node were never ticked by the engine — wrong process mode, wrong priority,
        // never added to the tree — every check above would still pass and nothing would move
        // in the actual game. So let the engine run it, and confirm it did.
        int framesBefore = spring.SolvedFrames;
        for (int i = 0; i < 20; i++)
        {
            avatar.Position += new Vector3(0.02f, 0, 0);
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        int solved = spring.SolvedFrames - framesBefore;
        GD.Print($"PHYSTEST LIVE engine solved {solved} of 20 frames via _Process");

        bool liveOk = solved > 0;
        if (!liveOk) GD.Print("PHYSTEST FAIL live: the solver never ran from the engine's own loop");

        bool ok = penetrationOk && responseOk && movingOk && settleOk && skinOk && liveOk;
        GD.Print($"PHYSTEST {(ok ? "PASS" : "FAIL")}");
        host.GetTree().Quit(ok ? 0 : 1);
    }

    private static void Report(string label, IReadOnlyList<(string Chain, string Bone, float Clearance, bool Anchored)> data)
    {
        if (data.Count == 0) { GD.Print($"PHYSTEST {label}: no joints have colliders assigned"); return; }

        var free = data.Where(d => !d.Anchored).ToList();
        int anchored = data.Count - free.Count;
        if (free.Count == 0)
        {
            GD.Print($"PHYSTEST {label}: all {data.Count} joints are anchored inside colliders");
            return;
        }

        var worst = free.OrderBy(d => d.Clearance).First();
        GD.Print($"PHYSTEST {label} clearance min={free.Min(d => d.Clearance):F4}m " +
                 $"avg={free.Average(d => d.Clearance):F4}m " +
                 $"worst={worst.Chain}/{worst.Bone} n={free.Count} anchored={anchored}");
    }

    /// Fail on joints that are inside the body and could have been outside it. Anchored joints
    /// are excluded — see `SpringBoneSystem.DebugClearances` for why they cannot be resolved.
    private static bool CheckPenetration(string phase, IReadOnlyList<(string Chain, string Bone, float Clearance, bool Anchored)> data, float tolerance)
    {
        var bad = data.Where(c => !c.Anchored && c.Clearance < tolerance).ToList();
        if (bad.Count == 0) return true;

        GD.Print($"PHYSTEST FAIL penetration at {phase}: {bad.Count} joints inside the body");
        foreach (var p in bad.OrderBy(p => p.Clearance).Take(10))
            GD.Print($"PHYSTEST   {p.Chain}/{p.Bone} clearance={p.Clearance:F4}m");
        return false;
    }

    /// Walk every skinned surface and assert the two invariants that re-skinning could break.
    private static bool CheckSkin(AvatarInstance avatar)
    {
        var meshes = new List<MeshInstance3D>();
        Collect(avatar, meshes);

        int surfaces = 0, verts = 0, badWeight = 0, badIndex = 0;

        foreach (var mi in meshes)
        {
            if (mi.Mesh is not ArrayMesh am || mi.Skin == null) continue;
            int binds = mi.Skin.GetBindCount();

            for (int s = 0; s < am.GetSurfaceCount(); s++)
            {
                var arrays = am.SurfaceGetArrays(s);
                var v = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
                var weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
                if (v.Length == 0 || bones.Length == 0) continue;

                int inf = bones.Length / v.Length;
                if (inf != 4 && inf != 8) continue;
                surfaces++;
                verts += v.Length;

                for (int i = 0; i < v.Length; i++)
                {
                    float sum = 0f;
                    for (int k = 0; k < inf; k++)
                    {
                        int b = bones[i * inf + k];
                        if (b < 0 || b >= binds) badIndex++;
                        sum += weights[i * inf + k];
                    }
                    if (Mathf.Abs(sum - 1f) > 0.01f) badWeight++;
                }
            }
        }

        GD.Print($"PHYSTEST SKIN surfaces={surfaces} verts={verts} " +
                 $"badWeightSum={badWeight} badBoneIndex={badIndex}");

        if (badWeight > 0 || badIndex > 0)
        {
            GD.Print("PHYSTEST FAIL skin: re-skinning corrupted the mesh");
            return false;
        }
        return true;
    }

    private static void Collect(Node node, List<MeshInstance3D> into)
    {
        if (node is MeshInstance3D mi) into.Add(mi);
        foreach (var c in node.GetChildren()) Collect(c, into);
    }

    /// How far the named bones' tails move, relative to the body, over a sidestep.
    private static float BoneTravel(AvatarInstance avatar, SpringBoneSystem spring, string[] bones)
    {
        var want = new HashSet<string>(bones);
        Dictionary<string, Vector3> Sample() => spring.DebugTails()
            .Where(t => want.Contains(t.Bone))
            .ToDictionary(t => t.Bone, t => t.Tail);

        var start = Sample();
        Vector3 origin = avatar.Position;
        float peak = 0f;
        for (int i = 0; i < 30; i++)
        {
            avatar.Position += new Vector3(2f / 60f, 0, 0);
            avatar.ForceUpdateTransform();
            spring.DebugStep(1f / 60f);
            peak = Mathf.Max(peak, MaxDisplacement(start, Sample(), avatar.Position - origin));
        }

        // Leave the rig settled so later measurements aren't polluted.
        for (int i = 0; i < 120; i++) spring.DebugStep(1f / 60f);
        return peak;
    }

    private static Dictionary<string, Vector3> Snapshot(SpringBoneSystem spring)
    {
        var map = new Dictionary<string, Vector3>();
        foreach (var (bone, tail) in spring.DebugTails()) map[bone] = tail;
        return map;
    }

    /// Largest movement of any joint between two snapshots, with the avatar's own translation
    /// subtracted out — otherwise walking 1 m would register as 1 m of "physics".
    private static float MaxDisplacement(Dictionary<string, Vector3> a, Dictionary<string, Vector3> b,
        Vector3 bodyOffset)
    {
        float max = 0f;
        foreach (var (bone, tail) in b)
            if (a.TryGetValue(bone, out var prev))
                max = Mathf.Max(max, (tail - bodyOffset - prev).Length());
        return max;
    }

    private static void Fail(Node host, string why)
    {
        GD.Print($"PHYSTEST FAIL: {why}");
        host.GetTree().Quit(1);
    }
}
