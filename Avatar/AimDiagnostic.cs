using System;
using Godot;

namespace SerikaSocial.Avatar;

/// Headless head-aim diagnostic. A head pitched 30° and a head pitched 8° are a few pixels apart
/// in a still frame and indistinguishable in a mirror; as numbers they are unmistakable.
///
///   Godot --headless --path game -- --serika-aimtest --ska &lt;path&gt; [--clip Walk]
///
/// **Everything is measured against a lockstep control avatar** — a second instance of the same
/// rig, fed the identical clip and timestep, with its aim left at zero. Subtracting it isolates
/// exactly what the aim layer contributed and nothing else.
///
/// That indirection is not ceremony; two more obvious measurements both give wrong answers:
///
///   - Skeleton-space head pitch includes everything the torso does. `Animate`'s idle breathing
///     swings the chest 0.028 rad and the spine half that, and the head rides along, so a
///     perfectly steady aim reads as ±1.6° of drift.
///   - Head pitch measured relative to the torso removes breathing but introduces the mirror
///     image of the same error. Holding a constant *skeleton-space* aim while the chest breathes
///     underneath requires the neck's local rotation to counter-rotate — correct behaviour, but
///     it shows up as the same ±1.6° wobble in the torso's frame.
///
/// Four properties are checked, each a way this can silently go wrong:
///
///   TRACK — sweep the commanded pitch and measure what the head bone actually does. A rig whose
///           neck and head bones are oriented unusually would show up here as a mis-scaled or
///           cross-axis response.
///   DRIFT — hold a fixed aim for 120 frames; the contribution must not move. This is the
///           regression test for the compounding failure `BasePose` exists to prevent: the head
///           is not rewritten by the retargeter, so a delta applied on top of last frame's own
///           result would wind up like a screw, a few degrees per frame.
///   CLAMP — command far past the neck's range and confirm it saturates at the cervical limit
///           rather than following the mouse into a 90° head snap.
///   NOD   — run a gesture to completion and confirm it oscillates and returns to exactly zero.
///           A gesture leaving a residual would permanently cock the head.
public static class AimDiagnostic
{
    // Same limits as Avatar/HeadAim.cs. Duplicated deliberately: a test that imports the constant
    // it is testing agrees with the code by construction and proves nothing.
    private const float PitchDown = 1.05f;
    private const float PitchUp = 0.96f;

    private const float TrackToleranceDeg = 2.0f;
    private const float DriftToleranceDeg = 0.5f;

    public static void Run(Node host, string skaPath, string clip)
    {
        GD.Print($"AIMTEST ska={skaPath ?? "(bean)"} clip={clip}");

        var aimed = AvatarLibrary.InstantiateOrDefault(skaPath);
        var control = AvatarLibrary.InstantiateOrDefault(skaPath);
        if (aimed?.Skeleton == null || control?.Skeleton == null)
        {
            GD.Print("AIMTEST FAIL: no avatar/skeleton");
            host.GetTree().Quit(1);
            return;
        }
        host.AddChild(aimed);
        host.AddChild(control);

        int head = aimed.BoneOf("head");
        int neck = aimed.BoneOf("neck");
        GD.Print($"AIMTEST bones head={head} neck={neck} " +
                 "(no neck is expected on some rigs — the head then carries the whole aim)");
        if (head < 0) { GD.Print("AIMTEST FAIL: rig has no head bone"); host.GetTree().Quit(1); return; }

        if (!Enum.TryParse(clip, ignoreCase: true, out AnimRetargeter.State state))
            state = AnimRetargeter.State.Idle;
        float speed = state == AnimRetargeter.State.Idle ? 0f : 4f;

        var rig = new Pair(aimed, control, head, speed);

        // Settle: the retargeter blends in over its first frames, and a measurement taken during
        // that ramp would be attributed to the aim layer.
        aimed.SetLookAim(0f, 0f);
        rig.Step(60);
        GD.Print($"AIMTEST settled, residual between the two rigs = {rig.Contribution():F3}° " +
                 "(must be ~0 — the pair are in lockstep before any aim is applied)");

        bool ok = true;

        // ── TRACK ────────────────────────────────────────────────────────────────────
        GD.Print("AIMTEST TRACK  commanded → measured (degrees)");
        foreach (float cmd in new[] { -55f, -40f, -20f, 0f, 20f, 40f, 55f })
        {
            aimed.SetLookAim(0f, Mathf.DegToRad(cmd));
            rig.Step(4);

            float measured = rig.Contribution();
            float err = Mathf.Abs(measured - cmd);
            bool pass = err <= TrackToleranceDeg;
            ok &= pass;
            GD.Print($"AIMTEST   {cmd,6:F1} → {measured,7:F2}   err {err,5:F2}  {(pass ? "ok" : "FAIL")}");
        }

        // ── DRIFT ────────────────────────────────────────────────────────────────────
        aimed.SetLookAim(0f, Mathf.DegToRad(30f));
        rig.Step(4);
        float first = rig.Contribution();
        float min = first, max = first;
        for (int i = 0; i < 120; i++)
        {
            rig.Step(1);
            float c = rig.Contribution();
            min = Mathf.Min(min, c);
            max = Mathf.Max(max, c);
        }
        float last = rig.Contribution();
        float drift = Mathf.Max(Mathf.Abs(last - first), max - min);
        bool driftOk = drift <= DriftToleranceDeg;
        ok &= driftOk;
        GD.Print($"AIMTEST DRIFT  held 30° for 120 frames: {first:F2}° → {last:F2}° " +
                 $"(range {min:F2}..{max:F2})  drift {drift:F3}°  " +
                 $"{(driftOk ? "ok" : "FAIL — the aim delta is compounding")}");

        // ── CLAMP ────────────────────────────────────────────────────────────────────
        aimed.SetLookAim(0f, Mathf.DegToRad(89f));
        rig.Step(4);
        float up = rig.Contribution();
        aimed.SetLookAim(0f, Mathf.DegToRad(-89f));
        rig.Step(4);
        float down = rig.Contribution();

        bool clampOk = up <= Mathf.RadToDeg(PitchUp) + TrackToleranceDeg
                    && down >= -Mathf.RadToDeg(PitchDown) - TrackToleranceDeg;
        ok &= clampOk;
        GD.Print($"AIMTEST CLAMP  ±89° commanded → up {up:F2}° (limit {Mathf.RadToDeg(PitchUp):F1}°), " +
                 $"down {down:F2}° (limit {-Mathf.RadToDeg(PitchDown):F1}°)  {(clampOk ? "ok" : "FAIL")}");

        // ── NOD ──────────────────────────────────────────────────────────────────────
        aimed.SetLookAim(0f, 0f);
        rig.Step(4);

        aimed.PlayEmote(AvatarInstance.Emote.Yes); // routed to the additive gesture
        float peak = 0f;
        int frames = 0;
        while (aimed.GestureActive && frames < 300)
        {
            rig.Step(1);
            peak = Mathf.Max(peak, Mathf.Abs(rig.Contribution()));
            frames++;
        }
        rig.Step(4);
        float residual = Mathf.Abs(rig.Contribution());

        bool nodOk = peak > 6f && residual <= DriftToleranceDeg && frames < 300;
        ok &= nodOk;
        GD.Print($"AIMTEST NOD    {frames} frames, peak {peak:F2}°, residual {residual:F3}°  " +
                 $"{(nodOk ? "ok" : "FAIL")}");

        // A shake must move the head in YAW and leave pitch alone, or the two gestures are
        // driving the same axis and "no" looks like "yes".
        aimed.PlayEmote(AvatarInstance.Emote.Reject);
        float yawPeak = 0f, pitchLeak = 0f;
        frames = 0;
        while (aimed.GestureActive && frames < 300)
        {
            rig.Step(1);
            yawPeak = Mathf.Max(yawPeak, Mathf.Abs(rig.ContributionYaw()));
            pitchLeak = Mathf.Max(pitchLeak, Mathf.Abs(rig.Contribution()));
            frames++;
        }
        bool shakeOk = yawPeak > 6f && pitchLeak < 6f;
        ok &= shakeOk;
        GD.Print($"AIMTEST SHAKE  peak yaw {yawPeak:F2}°, pitch leak {pitchLeak:F2}°  " +
                 $"{(shakeOk ? "ok" : "FAIL")}");

        ok &= WireCheck(aimed, control, head, speed);

        GD.Print(ok ? "AIMTEST PASS" : "AIMTEST FAIL");
        host.GetTree().Quit(ok ? 0 : 1);
    }

    /// The link that makes head aim visible to other players — asserted rather than assumed.
    ///
    /// No proto change was needed for any of this, and this is the proof: `HumanoidBones.Lod1`
    /// already carries `neck` at index 4 and `head` at index 5, `CaptureBonePose` samples the
    /// ANIMATED pose, and `AvatarPose` packs all 22 LOD1 bones into every frame. So the aim rides
    /// the existing wire format for free.
    ///
    /// Here the aimed rig's pose is captured, encoded with the production `PoseFrame.Encode`,
    /// decoded back, and applied to the control rig through `ApplyBonePose` — exactly the path a
    /// remote peer's client takes. The control rig's head must then match the aimed rig's, within
    /// the codec's own quantization (10-bit smallest-three quaternion components).
    private static bool WireCheck(AvatarInstance aimed, AvatarInstance control, int head, float speed)
    {
        const float commandedDeg = 42f;
        aimed.SetLookAim(0f, Mathf.DegToRad(commandedDeg));
        for (int i = 0; i < 4; i++)
        {
            aimed.Animate(1.0 / 60.0, speed, true);
            control.Animate(1.0 / 60.0, speed, true);
        }

        float sent = HeadPitchDeg(aimed, head) - HeadPitchDeg(control, head);

        var frame = Player.AvatarPose.FromTransform(Transform3D.Identity, 0, aimed);
        var decoded = Serika.Net.Codec.PoseFrame.Decode(frame.Encode());
        control.ApplyBonePose(decoded.Bones);

        // ApplyBonePose writes local bone rotations directly and the receiver does not re-run
        // Animate over them, so the head reads back as sent. Absolute pitch is the right
        // comparison here rather than a contribution: the frame carries all 22 LOD1 bones, so
        // the receiving rig's whole upper body — and therefore the head's parent chain — is the
        // sender's.
        float received = HeadPitchDeg(control, head);
        float aimedAbs = HeadPitchDeg(aimed, head);
        float err = Mathf.Abs(received - aimedAbs);

        // 10-bit components over the smallest-three range put the worst-case single-quaternion
        // error a little under a tenth of a degree; a whole degree of slack is generous and would
        // still catch a bone landing in the wrong wire slot.
        bool wireOk = err <= 1.0f && Mathf.Abs(sent - commandedDeg) <= TrackToleranceDeg;
        GD.Print($"AIMTEST WIRE   {commandedDeg:F0}° aim → encode → decode → peer rig: " +
                 $"sender head {aimedAbs:F2}°, receiver head {received:F2}°, err {err:F3}°  " +
                 $"{(wireOk ? "ok — peers see the aim, with no proto change" : "FAIL")}");
        return wireOk;
    }

    /// The aimed rig and its zero-aim control, advanced together.
    private readonly struct Pair
    {
        private readonly AvatarInstance _aimed, _control;
        private readonly int _head;
        private readonly float _speed;

        public Pair(AvatarInstance aimed, AvatarInstance control, int head, float speed)
        {
            _aimed = aimed; _control = control; _head = head; _speed = speed;
            aimed.MoveDir = AvatarInstance.MoveDirection.Forward;
            control.MoveDir = AvatarInstance.MoveDirection.Forward;
        }

        /// Advance both rigs by the same fixed timestep, so their clips stay in phase and the
        /// difference between them is attributable to the aim layer alone.
        public void Step(int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                _aimed.Animate(1.0 / 60.0, _speed, true);
                _control.Animate(1.0 / 60.0, _speed, true);
            }
        }

        /// The aim layer's pitch contribution, in degrees, positive looking up.
        public float Contribution() => HeadPitchDeg(_aimed, _head) - HeadPitchDeg(_control, _head);

        /// The aim layer's yaw contribution, in degrees.
        public float ContributionYaw() => HeadYawDeg(_aimed, _head) - HeadYawDeg(_control, _head);
    }

    private static float HeadPitchDeg(AvatarInstance avatar, int head)
    {
        var fwd = HeadForward(avatar, head);
        return Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(fwd.Y, -1f, 1f)));
    }

    private static float HeadYawDeg(AvatarInstance avatar, int head)
    {
        var fwd = HeadForward(avatar, head);
        return Mathf.RadToDeg(Mathf.Atan2(fwd.X, -fwd.Z));
    }

    /// The head's forward axis in skeleton space — the character-aligned, Y-up, -Z-forward frame
    /// `GetBoneGlobalPose` reports in, the convention `VrAvatarIk` uses too.
    ///
    /// "Forward" cannot be a fixed local axis: a humanoid head bone points UP the skull (its local
    /// +Y runs toward the crown) and which axis faces the front varies by rig. So it is taken as
    /// the rest pose's own -Z carried through the bone's rotation-since-rest — whatever the author
    /// called forward at rest is forward now. Reading the direction the bone actually points, not
    /// the quaternion we wrote, is what makes this catch a delta that landed on the wrong axis.
    private static Vector3 HeadForward(AvatarInstance avatar, int head)
    {
        var skel = avatar.Skeleton;
        var restBasis = skel.GetBoneGlobalRest(head).Basis;
        var sinceRest = skel.GetBoneGlobalPose(head).Basis.GetRotationQuaternion()
                        * restBasis.GetRotationQuaternion().Inverse();
        return (sinceRest * -restBasis.Z.Normalized()).Normalized();
    }
}
