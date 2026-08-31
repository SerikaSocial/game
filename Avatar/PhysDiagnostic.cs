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
    /// Mirrors LocalPlayer.WalkSpeed / SprintSpeed — the speeds the rig is actually driven at.
    private const float WalkSpeed = 4.0f;
    private const float SprintSpeed = 7.0f;


    /// `async void` swallows exceptions: the method simply stops, `Quit` is never reached, and
    /// headless Godot spins forever looking like a hang rather than a failure. Wrap it.
    public static async void Run(Node host, string skaPath)
    {
        try { await RunInner(host, skaPath); }
        catch (Exception e)
        {
            GD.Print($"PHYSTEST FAIL exception: {e}");
            host.GetTree().Quit(1);
        }
    }

    private static async System.Threading.Tasks.Task RunInner(Node host, string skaPath)
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

        // First-person viewpoint: the camera rides the head bone plus this offset. If it lands
        // below the head bone or implausibly far above it, first person sits inside the chest.
        int headIdx = skel.FindBone("head");
        if (headIdx < 0 && avatar.RoleToBoneForDiagnostics().TryGetValue("head", out int hr)) headIdx = hr;
        if (headIdx >= 0)
        {
            float headY = skel.GetBoneGlobalRest(headIdx).Origin.Y;
            GD.Print($"PHYSTEST EYE headBoneY={headY:F3} offset={avatar.EyeOffsetY:F3} " +
                     $"viewY={headY + avatar.EyeOffsetY:F3} avatarHeight={avatar.Height:F3}");
        }

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
        // 1 mm of slack: the floor each joint is held to is calibrated from a settled pose, and
        // settling converges to within a fraction of a millimetre rather than exactly.
        bool penetrationOk = CheckPenetration("rest", rest, -0.001f);

        // Standing still, the rig should hold the shape its author built. Drift here means the
        // body colliders are pushing it off that shape — a coat shoved out into a balloon, hair
        // held off the scalp — which no clearance measurement can reveal, since clearance is
        // measured against the very colliders doing the pushing.
        var (drift, driftBone) = spring.DebugRestDrift();
        GD.Print($"PHYSTEST DRIFT settled pose is {drift:F4}m off the authored rest at {driftBone}");
        bool driftOk = drift < 0.03f;
        if (!driftOk)
            GD.Print("PHYSTEST FAIL drift: body colliders are displacing the rig at rest");

        // ── response to motion ────────────────────────────────────────────────────────────
        // Move at the game's real speeds, not a token one: LocalPlayer walks at 4 m/s and
        // sprints at 7. Testing at 2 understates how hard the rig is actually driven.
        int deadChains = 0;
        var before = Snapshot(spring);
        float peak = 0f, walkAngle = 0f;
        string worstJoint = "(none)";
        for (int i = 0; i < 45; i++)
        {
            avatar.Position += new Vector3(WalkSpeed / 60f, 0, 0);
            avatar.ForceUpdateTransform();
            spring.DebugStep(1f / 60f);
            peak = Mathf.Max(peak, MaxDisplacement(before, Snapshot(spring), avatar.Position));
            if (i < 30) continue;
            var (deg, bone) = spring.DebugMaxAngleDegrees();
            if (deg > walkAngle) { walkAngle = deg; worstJoint = bone; }
        }
        GD.Print($"PHYSTEST RESPONSE walk {WalkSpeed}m/s: peak displacement={peak:F4}m " +
                 $"steady max deflection={walkAngle:F1}deg at {worstJoint}");

        // A rig that barely moves isn't simulating in any way a person would notice; one pinned
        // near its cone limit is being thrown flat, which reads as gale-force wind rather than
        // motion. Both are failures, in opposite directions.
        bool responseOk = peak > 0.01f;
        if (!responseOk) GD.Print("PHYSTEST FAIL response: rig is effectively static");
        if (walkAngle > 68f)
            GD.Print($"PHYSTEST WARN deflection {walkAngle:F1}deg at walking pace — chains are " +
                     "saturating against their limit, secondary motion will look wind-blown");

        // Per-chain motion. An avatar-wide peak hides a whole subsystem being frozen: Suisei's
        // 36 coat-skirt chains can be completely dead while her 9 hair chains carry the number.
        // Settle to rest first, capture that as the baseline, then start from a standstill — a
        // baseline taken mid-deflection would show near-zero *additional* motion at steady lag
        // and call every chain dead, which is a bug in the measurement, not the rig.
        {
            for (int i = 0; i < 150; i++) spring.DebugStep(1f / 60f);
            var origin = avatar.Position;
            var start = spring.DebugChainTails().Select(c => c.Tails.ToArray()).ToList();
            var moved = new float[start.Count];
            for (int i = 0; i < 45; i++)
            {
                avatar.Position += new Vector3(WalkSpeed / 60f, 0, 0);
                avatar.ForceUpdateTransform();
                spring.DebugStep(1f / 60f);
                Vector3 body = avatar.Position - origin;
                var now = spring.DebugChainTails();
                for (int c = 0; c < now.Count && c < start.Count; c++)
                    for (int j = 0; j < now[c].Tails.Length; j++)
                        moved[c] = Mathf.Max(moved[c], (now[c].Tails[j] - body - start[c][j]).Length());
            }

            var chains = spring.DebugChainTails();
            int dead = moved.Count(m => m < 0.001f);
            var byName = chains.Select((c, i) => (c.Chain, moved[i]))
                               .OrderBy(t => t.Item2).ToList();
            GD.Print($"PHYSTEST CHAINS {chains.Count} total, {dead} moved <1mm at walking pace; " +
                     $"quietest={byName[0].Chain}@{byName[0].Item2:F4}m " +
                     $"liveliest={byName[^1].Chain}@{byName[^1].Item2:F4}m");
            if (dead > 0)
                GD.Print($"PHYSTEST FAIL dead chains: {dead} of {chains.Count} do not move");
            deadChains = dead;

            for (int i = 0; i < 120; i++) spring.DebugStep(1f / 60f);
        }

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

        bool ok = penetrationOk && responseOk && movingOk && settleOk && skinOk && liveOk && driftOk && deadChains == 0;
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

        // First-person head culling depends on `IsHeadMesh` resolving each skin bind to a real
        // bone. Godot's glTF importer uses *named* binds, so this silently classified nothing at
        // all when it read the bind index directly. Report the split so a regression is visible as
        // numbers rather than as "my own hair is in my face".
        int headMeshes = 0, namedBinds = 0, unresolvedBinds = 0;
        foreach (var mi in meshes)
        {
            if (mi.Skin == null) continue;
            for (int i = 0; i < mi.Skin.GetBindCount(); i++)
            {
                if (mi.Skin.GetBindBone(i) >= 0) continue;
                var bn = mi.Skin.GetBindName(i);
                if (!bn.IsEmpty && avatar.Skeleton?.FindBone(bn) >= 0) namedBinds++;
                else unresolvedBinds++;
            }
            if (avatar.IsHeadMesh(mi)) headMeshes++;
        }
        GD.Print($"PHYSTEST HEADMESH meshes={meshes.Count} classifiedHead={headMeshes} " +
                 $"namedBinds={namedBinds} unresolvedBinds={unresolvedBinds}");

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
            avatar.Position += new Vector3(WalkSpeed / 60f, 0, 0);
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
