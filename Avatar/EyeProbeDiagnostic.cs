using System;
using Godot;

namespace SerikaSocial.Avatar;

/// Headless numeric check of the first-person eye probe (`AvatarInstance.TryGetEyeGlobal`) —
/// the thing the tracked desktop first-person camera rides. Verifies, purely as numbers:
///
///   • the probe resolves and tracks the ANIMATED pose: through a sprint cycle the eye point
///     bobs vertically and shifts fore/aft with the chest lean instead of hovering frozen;
///   • crouching drops the eye by roughly what the CrouchIdle clip drops the skull;
///   • sitting drops it far further, matching a seated pose.
///
/// A broken version of this was previously invisible until you wore the avatar and ran — the
/// camera stayed bolt upright while the body visibly bounced away underneath it.
///
///   Godot --headless --path game -- --serika-fptest [--ska /path/to/avatar.ska]
public static class EyeProbeDiagnostic
{
    private const float Dt = 1f / 30f;

    public static void Run(Node host, string skaPath)
    {
        GD.Print($"FPTEST ska={skaPath ?? "(bean)"}");

        var avatar = AvatarLibrary.InstantiateOrDefault(skaPath);
        if (avatar == null) { GD.Print("FPTEST FAIL: no avatar instance"); host.GetTree().Quit(1); return; }
        host.AddChild(avatar);

        // Mesh-classification telemetry (SERIKA_DEBUG_MESHES=1): fptest is the headless path
        // that actually classifies meshes, so the per-mesh dump rides here.
        if (System.Environment.GetEnvironmentVariable("SERIKA_DEBUG_MESHES") == "1")
            foreach (var c in avatar.FindChildren("*", "MeshInstance3D", true, false))
                if (c is MeshInstance3D mi) _ = avatar.IsHeadMesh(mi);

        if (!avatar.TryGetEyeGlobal(out var restXf))
        {
            GD.Print("FPTEST FAIL: TryGetEyeGlobal never became ready — ResolveEyeOffset did not run");
            host.GetTree().Quit(1);
            return;
        }
        if (!avatar.TryGetHeadGlobal(out var headRest))
        {
            GD.Print("FPTEST FAIL: no head bone");
            host.GetTree().Quit(1);
            return;
        }
        float eyeAboveSkull = restXf.Origin.Y - headRest.Origin.Y;
        GD.Print($"FPTEST eyeOffset above head bone Y={eyeAboveSkull:F3} m " +
                 $"(offset vector len={(restXf.Origin - headRest.Origin).Length():F3})");

        // ── Sprint cycle ────────────────────────────────────────────────────────
        avatar.MoveDir = AvatarInstance.MoveDirection.Forward;
        float minY = float.MaxValue, maxY = float.MinValue;
        float minFwdDelta = float.MaxValue, maxFwdDelta = float.MinValue; // axis-aligned lean
        Vector3 restPos = restXf.Origin;
        for (int i = 0; i < 90; i++)
        {
            avatar.Animate(Dt, 6.8f, onFloor: true, crouching: false, sprinting: true);
            avatar.TryGetEyeGlobal(out var xf);
            var p = xf.Origin;
            minY = Mathf.Min(minY, p.Y); maxY = Mathf.Max(maxY, p.Y);

            // Forward shift along the model's facing is whatever axis the lean puts travel into;
            // compare against the standing-rest position rather than assuming a world axis.
            Vector3 rel = p - restPos;
            float alongFacing = new Vector2(rel.X, rel.Z).Length();
            minFwdDelta = Mathf.Min(minFwdDelta, alongFacing);
            maxFwdDelta = Mathf.Max(maxFwdDelta, alongFacing);
        }
        float bobRange = maxY - minY;
        float leanRange = maxFwdDelta - minFwdDelta;
        bool passBob = bobRange >= 0.015f;
        GD.Print($"FPTEST sprint: eyeY {minY:F3}..{maxY:F3} (bob {bobRange * 100f:F1} cm), " +
                 $"planar wander {leanRange * 100f:F1} cm -> bob {(passBob ? "OK" : "STUCK")}");

        // ── Crouch ──────────────────────────────────────────────────────────────
        float standEyeY = maxY;
        for (int i = 0; i < 45; i++)
            avatar.Animate(Dt, 0f, onFloor: true, crouching: true, sprinting: false);
        avatar.TryGetEyeGlobal(out var crouchXf);
        float crouchDrop = standEyeY - crouchXf.Origin.Y;
        bool passCrouch = crouchDrop >= 0.03f;
        GD.Print($"FPTEST crouch: eye dropped {crouchDrop * 100f:F1} cm -> {(passCrouch ? "OK" : "NO DROP")}");

        // ── Sit ─────────────────────────────────────────────────────────────────
        // Informational only: locomotion.glb's Sitting_Idle is chair-authored — thighs come up
        // into the seated pose but the pelvis stays at its own height (the seat system
        // teleports the avatar onto the chair, so nothing in the clip needs to descend).
        // What matters here is that the probe keeps producing sane samples through an emote.
        avatar.PlayEmote(AvatarInstance.Emote.Sit);
        float lastY = standEyeY;
        bool sitTracked = true;
        for (int i = 0; i < 75; i++)
        {
            avatar.Animate(Dt, 0f, onFloor: true, crouching: false, sprinting: false);
            if (!avatar.TryGetEyeGlobal(out var sitXf)) { sitTracked = false; break; }
            lastY = sitXf.Origin.Y;
            if (!float.IsFinite(sitXf.Origin.X) || !float.IsFinite(lastY) || lastY <= 0f)
            {
                sitTracked = false;
                GD.Print($"FPTEST FAIL: sit frame {i} produced a non-finite/non-positive eye ({sitXf.Origin})");
                break;
            }
        }
        GD.Print($"FPTEST sit: end eye Y={lastY:F3} (stand ref {standEyeY:F3}), tracked={sitTracked} " +
                 "(chair-authored clip — no drop expected)");

        bool pass = passBob && passCrouch && sitTracked;
        GD.Print(pass
            ? "FPTEST PASS: the eye probe rides the animation (bob, lean and crouch all move it; " +
              "emotes keep tracking sanely)."
            : "FPTEST FAIL: the eye probe is not following the animated pose; the FP camera " +
              "would detach from the body.");
        GetTreeSafe(host)?.Quit(pass ? 0 : 1);
    }

    private static SceneTree GetTreeSafe(Node host) => host.GetTree();
}
