using System;
using System.Collections.Generic;
using Godot;

namespace SerikaSocial.Avatar;

/// Headless animation diagnostic. Loads the bundled avatar, drives the retargeter through a
/// clip, and prints key bone positions in hips-local space so retargeting can be checked
/// numerically instead of by eyeballing a screenshot.
///
///   Godot --headless --path game -- --serika-animtest --clip Walk
///
/// Expected for a sane humanoid (metres, relative to hips, +X right, +Y up, -Z forward):
///   hands  |x| ≈ 0.15–0.35, y ≈ -0.10..-0.35  (arms hang at the sides)
///   A flung-out T-pose/broken retarget shows |x| ≳ 0.55 with y ≈ 0 (arms horizontal).
public static class AnimDiagnostic
{
    private static readonly string[] Probes =
    {
        "head", "leftHand", "rightHand", "leftFoot", "rightFoot",
        "leftUpperArm", "rightUpperArm", "leftLowerLeg", "rightLowerLeg",
    };

    public static void Run(Node host, string clip, string skaPath = null)
    {
        GD.Print($"ANIMTEST clip={clip} ska={skaPath ?? "(bean)"}");

        var avatar = AvatarLibrary.InstantiateOrDefault(skaPath);
        if (avatar == null) { GD.Print("ANIMTEST FAIL: no avatar instance"); host.GetTree().Quit(1); return; }
        host.AddChild(avatar);

        var skel = avatar.Skeleton;
        if (skel == null) { GD.Print("ANIMTEST FAIL: avatar has no Skeleton3D"); host.GetTree().Quit(1); return; }
        GD.Print($"ANIMTEST skeleton bones={skel.GetBoneCount()} height={avatar.Height:F2}");

        var roles = avatar.RoleToBoneForDiagnostics();
        GD.Print($"ANIMTEST mapped roles={roles.Count}");

        // Baseline (bind pose) before any retargeting.
        DumpFrame("REST", skel, roles);

        if (!Enum.TryParse(clip, ignoreCase: true, out AnimRetargeter.State state))
        {
            GD.Print($"ANIMTEST FAIL: '{clip}' is not a State. Valid: {string.Join(", ", Enum.GetNames<AnimRetargeter.State>())}");
            host.GetTree().Quit(1);
            return;
        }

        var rt = AnimRetargeter.Create("res://Assets/Animations/locomotion.glb", skel, roles);
        if (rt == null) { GD.Print("ANIMTEST FAIL: retargeter did not build"); host.GetTree().Quit(1); return; }
        host.AddChild(rt);

        GD.Print($"ANIMTEST SRCREST {rt.DebugSourceRest()}");
        if (!rt.HasClip(state)) GD.Print($"ANIMTEST WARN: no clip for state {state}");
        rt.SetState(state);

        // Advance a fixed number of steps and sample a few points through the cycle.
        for (int i = 1; i <= 30; i++)
        {
            rt.Update(1.0 / 30.0);
            if (i is 10 or 20 or 30)
            {
                GD.Print($"ANIMTEST SRC {rt.DebugSourceState()}");
                DumpFrame($"t={i / 30.0:F2}s", skel, roles);
            }
        }

        GD.Print("ANIMTEST DONE");
        host.GetTree().Quit(0);
    }

    private static void DumpFrame(string label, Skeleton3D skel, Dictionary<string, int> roles)
    {
        if (!roles.TryGetValue("hips", out int hipsIdx)) { GD.Print($"{label}: no hips"); return; }
        Vector3 hips = skel.GetBoneGlobalPose(hipsIdx).Origin;

        // Absolute foot height matters for the ground clamp: feet should sit near y=0 in
        // skeleton space, and hipsY should drop during a crouch rather than the feet rising.
        string ground = "";
        if (roles.TryGetValue("leftFoot", out int lfi) && roles.TryGetValue("rightFoot", out int rfi))
        {
            float lfy = skel.GetBoneGlobalPose(lfi).Origin.Y;
            float rfy = skel.GetBoneGlobalPose(rfi).Origin.Y;
            ground = $" |hipsY={hips.Y:F2} footY={Mathf.Min(lfy, rfy):F2}";
        }

        var parts = new List<string>();
        foreach (string role in Probes)
        {
            if (!roles.TryGetValue(role, out int idx)) continue;
            Vector3 p = skel.GetBoneGlobalPose(idx).Origin - hips;
            parts.Add($"{role}=({p.X:F2},{p.Y:F2},{p.Z:F2})");
        }
        GD.Print($"ANIMTEST {label} {string.Join(" ", parts)}{ground}");
    }
}
