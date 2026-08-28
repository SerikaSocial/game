using Godot;
using SerikaSocial.Avatar;

namespace SerikaSocial.Player;

/// A simulated OpenXR device: a headset, two controllers and two optical hand trackers,
/// registered with `XRServer` under the exact names the real OpenXR interface uses.
///
/// **Why this and not the old pose injection.** `VrDiagnostic` poses `VrPlayer`'s camera and
/// controller nodes by writing their transforms directly, and routes button reads through
/// `VrTestInput`. That tests the maths downstream of tracking, but it steps *around* every piece
/// of Godot's XR plumbing: `XRCamera3D` never binds to the head tracker, `XRController3D` never
/// reads an input, `GetHasTrackingData` is always false, and `XRHandTracker` is never involved at
/// all. Bugs that live in that plumbing — a controller whose pose is fine but whose stick reads
/// zero, a hand tracker that never goes `Active`, a camera that never moves because the reference
/// space is wrong — are invisible to it, and those are exactly the bugs VR shipped with.
///
/// This drives the real thing instead. `XRCamera3D` binds to whatever tracker is named `head`
/// and follows its pose; `XRController3D` binds to `left_hand`/`right_hand` and reads inputs by
/// name; `VrHandTracking` looks up `/user/hand_tracker/{left,right}`. None of that code cares
/// whether the poses came from a headset or from here, so with this installed the whole VR client
/// runs on a desktop with no HMD, and `VrPlayer` itself contains no test-only branch.
///
/// What it still cannot tell you: whether a binding lands on the button you expect on real
/// hardware (that is the runtime's interaction profile, not ours), how the image feels, or
/// anything about tracking quality. Those need a headset. This is a regression net for
/// everything else — which, on the evidence of this port's history, is where the bugs are.
///
/// Poses are in **play-space local** coordinates, i.e. relative to `XROrigin3D`, which is what
/// the XR runtime reports and what `XRNode3D` expects. `xr/openxr/reference_space` is Local
/// Floor, so y = 0 is the physical floor and a standing headset sits at ~1.65.
public sealed class VrSimDevice
{
    // The tracker names Godot's OpenXR interface registers. `XRCamera3D` hardcodes "head";
    // `XRController3D.Tracker` is set to these hand names in `VrPlayer._Ready`.
    private const string HeadName = "head";
    private const string LeftName = "left_hand";
    private const string RightName = "right_hand";
    private const string LeftHandTrackerName = "/user/hand_tracker/left";
    private const string RightHandTrackerName = "/user/hand_tracker/right";

    private XRPositionalTracker _head;
    private readonly XRControllerTracker[] _controller = new XRControllerTracker[2];
    private readonly XRHandTracker[] _handTracker = new XRHandTracker[2];

    /// Headset pose in play space. Defaults to a person of average height standing at the origin
    /// looking down -Z, which is Godot's forward.
    public Transform3D Head = new(Basis.Identity, new Vector3(0, 1.65f, 0));

    /// Controller poses in play space, index 0 = left, 1 = right.
    public readonly Transform3D[] Hand =
    {
        new(Basis.Identity, new Vector3(-0.22f, 1.05f, -0.30f)),
        new(Basis.Identity, new Vector3(0.22f, 1.05f, -0.30f)),
    };

    /// Whether each controller reports tracking. Setting this false is how you simulate a
    /// controller being put down — which is the only way to reach the bare-hands code paths.
    public readonly bool[] HandTracked = { true, true };

    // ── Controller inputs, one set per hand ──────────────────────────────────────────
    public readonly Vector2[] Stick = new Vector2[2];
    public readonly float[] Trigger = new float[2];
    public readonly float[] Grip = new float[2];
    public readonly bool[] PrimaryButton = new bool[2];   // A / X
    public readonly bool[] SecondaryButton = new bool[2]; // B / Y
    public readonly bool[] MenuButton = new bool[2];
    public readonly bool[] StickClick = new bool[2];
    /// Capacitive thumb rest, which real Touch controllers report and the built-in action map
    /// does not expose. Simulated so the gesture table can be exercised end to end.
    public readonly bool[] ThumbTouch = new bool[2];

    // ── Optical hand tracking ────────────────────────────────────────────────────────

    /// Whether each hand is being optically tracked. Mutually exclusive with a held controller in
    /// practice, and `VrHandTracking` only accepts `Unobstructed`, so this is what makes bare
    /// hands reachable at all.
    public readonly bool[] HandsVisible = new bool[2];

    /// Per-finger curl driving the synthesised joints, same order as `HandPoser.Finger`.
    public readonly float[][] HandCurl = { new float[5], new float[5] };

    /// When set, the thumb tip is placed to produce exactly this pinch strength against the index
    /// tip, overriding whatever the thumb's curl would have given. Pinch is the bare-hands click
    /// and teleport commit, so it needs to be drivable on its own.
    public readonly float?[] PinchOverride = new float?[2];

    /// Register the device. Safe to call once per process; `Remove` undoes it.
    public void Install()
    {
        _head = new XRPositionalTracker
        {
            Name = HeadName,
            Type = XRServer.TrackerType.Head,
            Description = "Simulated HMD",
        };
        XRServer.AddTracker(_head);

        for (int i = 0; i < 2; i++)
        {
            _controller[i] = new XRControllerTracker
            {
                Name = i == 0 ? LeftName : RightName,
                Type = XRServer.TrackerType.Controller,
                Hand = i == 0 ? XRPositionalTracker.TrackerHand.Left : XRPositionalTracker.TrackerHand.Right,
                Description = i == 0 ? "Simulated left controller" : "Simulated right controller",
                // The interaction profile a real Touch controller reports. Nothing in this project
                // branches on it yet, but reporting a plausible one keeps the simulation honest.
                Profile = "/interaction_profiles/oculus/touch_controller",
            };
            XRServer.AddTracker(_controller[i]);

            _handTracker[i] = new XRHandTracker
            {
                Name = i == 0 ? LeftHandTrackerName : RightHandTrackerName,
                Type = XRServer.TrackerType.Hand,
                Hand = i == 0 ? XRPositionalTracker.TrackerHand.Left : XRPositionalTracker.TrackerHand.Right,
                Description = i == 0 ? "Simulated left hand" : "Simulated right hand",
                HasTrackingData = false,
                // `VrHandTracking` deliberately rejects `Controller`-derived hands, so anything
                // other than `Unobstructed` here would make the bare-hands paths untestable.
                HandTrackingSource = XRHandTracker.HandTrackingSourceEnum.Unobstructed,
            };
            XRServer.AddTracker(_handTracker[i]);
        }

        Commit();
    }

    public void Remove()
    {
        if (_head != null) { XRServer.RemoveTracker(_head); _head = null; }
        for (int i = 0; i < 2; i++)
        {
            if (_controller[i] != null) { XRServer.RemoveTracker(_controller[i]); _controller[i] = null; }
            if (_handTracker[i] != null) { XRServer.RemoveTracker(_handTracker[i]); _handTracker[i] = null; }
        }
    }

    /// Push the current state into the trackers. Call once per frame, before the physics tick that
    /// should see it — this is the simulated equivalent of the runtime delivering a frame.
    public void Commit()
    {
        _head?.SetPose("default", Head, Vector3.Zero, Vector3.Zero, XRPose.TrackingConfidenceEnum.High);

        for (int i = 0; i < 2; i++)
        {
            var t = _controller[i];
            if (t == null) continue;

            if (HandTracked[i])
                t.SetPose("default", Hand[i], Vector3.Zero, Vector3.Zero, XRPose.TrackingConfidenceEnum.High);
            else
                t.InvalidatePose("default");

            // `aim` is the pose Godot's action map binds the pointing ray to on real hardware. It
            // is not read by this project (the controller's own basis is used), but publishing it
            // keeps the tracker shaped like the real one.
            t.SetPose("aim", Hand[i], Vector3.Zero, Vector3.Zero,
                      HandTracked[i] ? XRPose.TrackingConfidenceEnum.High : XRPose.TrackingConfidenceEnum.None);

            t.SetInput("primary", Stick[i]);
            t.SetInput("trigger", Trigger[i]);
            t.SetInput("grip", Grip[i]);
            t.SetInput("ax_button", PrimaryButton[i]);
            t.SetInput("by_button", SecondaryButton[i]);
            t.SetInput("menu_button", MenuButton[i]);
            t.SetInput("primary_click", StickClick[i]);
            t.SetInput("primary_touch", ThumbTouch[i]);
            // Click actions Godot's built-in map also publishes, so a binding change that reaches
            // for one finds it here rather than silently reading zero.
            t.SetInput("trigger_click", Trigger[i] > 0.8f);
            t.SetInput("grip_click", Grip[i] > 0.8f);

            CommitHand(i);
        }
    }

    // ---------------------------------------------------------------- hand synthesis

    /// Wrist-to-middle-knuckle distance. `VrHandTracking` scales every measurement by this rather
    /// than by absolute metres, so it is the one number that has to be self-consistent here.
    private const float Span = 0.09f;

    /// Fill in the 26-joint hand from the curl vector, or mark it untracked.
    ///
    /// The joints are placed so that `VrHandTracking`'s own measurements invert exactly back to
    /// the curls that went in — tip distance from knuckle is `span * (0.25 + 0.55 * (1 - curl))`,
    /// which is the inverse of `VrHandTracking.CurlOf`. That makes the simulator a true round
    /// trip: a test can assert the classifier saw the shape it was given, and any drift between
    /// the two formulas shows up as a failing round-trip rather than as a mysterious gesture.
    ///
    /// Direction is interpolated from "along the finger" to "folded into the palm" so the render
    /// looks like a closing hand rather than a fingertip sliding down a line.
    private void CommitHand(int i)
    {
        var tracker = _handTracker[i];
        if (tracker == null) return;

        tracker.HasTrackingData = HandsVisible[i];
        if (!HandsVisible[i]) return;

        // The hand frame rides on the controller pose: -Z is where the hand points, +Y is the back
        // of the hand, and +X runs thumb-ward on the right hand (mirrored on the left).
        var w = Hand[i];
        var basis = w.Basis;
        var along = -basis.Z;          // toward the fingertips
        var palmward = -basis.Y;       // into the palm, the direction a finger folds
        var across = basis.X * (i == 0 ? -1f : 1f); // thumb side

        var wrist = w.Origin;
        SetJoint(tracker, XRHandTracker.HandJoint.Wrist, wrist, basis);
        SetJoint(tracker, XRHandTracker.HandJoint.Palm, wrist + along * (Span * 0.55f), basis);

        // Knuckles spread across the palm, index nearest the thumb. Ring and little both scale
        // against the *pinky* knuckle, because that is the only one `VrHandTracking` caches — it
        // skips the ring knuckle deliberately, the two being ~2 cm apart. Placing them against
        // anything else here would make the round trip disagree with the real measurement.
        var knuckle = wrist + along * (Span * 0.95f);
        var pinkyKnuckle = knuckle + across * -0.038f;
        PlaceFinger(tracker, knuckle + across * 0.022f,
                    XRHandTracker.HandJoint.IndexFingerPhalanxProximal, XRHandTracker.HandJoint.IndexFingerTip,
                    HandCurl[i][(int)HandPoser.Finger.Index], along, palmward, basis);
        PlaceFinger(tracker, knuckle,
                    XRHandTracker.HandJoint.MiddleFingerPhalanxProximal, XRHandTracker.HandJoint.MiddleFingerTip,
                    HandCurl[i][(int)HandPoser.Finger.Middle], along, palmward, basis);
        PlaceFinger(tracker, pinkyKnuckle,
                    XRHandTracker.HandJoint.PinkyFingerPhalanxProximal, XRHandTracker.HandJoint.RingFingerTip,
                    HandCurl[i][(int)HandPoser.Finger.Ring], along, palmward, basis);
        SetTip(tracker, XRHandTracker.HandJoint.PinkyFingerTip, pinkyKnuckle, along, palmward,
               HandCurl[i][(int)HandPoser.Finger.Little], basis);

        // The thumb sits low on the palm and folds across it rather than into it.
        var thumbKnuckle = wrist + along * (Span * 0.35f) + across * 0.035f;
        SetJoint(tracker, XRHandTracker.HandJoint.ThumbPhalanxProximal, thumbKnuckle, basis);

        var indexTip = tracker.GetHandJointTransform(XRHandTracker.HandJoint.IndexFingerTip).Origin;
        Vector3 thumbTip;
        if (PinchOverride[i] is float pinch)
        {
            // Invert `VrHandTracking`'s pinch curve: it maps tip separation / span from 0.02
            // (touching) to 0.45 (clearly apart) onto 1..0.
            float sep = Span * (0.02f + 0.43f * (1f - Mathf.Clamp(pinch, 0f, 1f)));
            var away = (thumbKnuckle - indexTip);
            away = away.LengthSquared() > 1e-8f ? away.Normalized() : across;
            thumbTip = indexTip + away * sep;
        }
        else
        {
            float curl = HandCurl[i][(int)HandPoser.Finger.Thumb];
            var dir = (along * 0.6f + across * 0.4f).Normalized().Slerp(
                (palmward + across * -0.5f).Normalized(), Mathf.Clamp(curl, 0f, 1f));
            thumbTip = thumbKnuckle + dir * (Span * (0.25f + 0.55f * (1f - Mathf.Clamp(curl, 0f, 1f))));
        }
        SetJoint(tracker, XRHandTracker.HandJoint.ThumbTip, thumbTip, basis);

        // Everything else the runtime would publish. Nothing in this project reads these, but a
        // tracker that reports half a hand is a tracker that will surprise the next reader of it.
        FillUnusedJoints(tracker, wrist, basis);
    }

    private static void PlaceFinger(XRHandTracker tracker, Vector3 knuckle,
                                    XRHandTracker.HandJoint knuckleJoint, XRHandTracker.HandJoint tipJoint,
                                    float curl, Vector3 along, Vector3 palmward, Basis basis)
    {
        SetJoint(tracker, knuckleJoint, knuckle, basis);
        SetTip(tracker, tipJoint, knuckle, along, palmward, curl, basis);
    }

    /// Place a fingertip at exactly the distance `VrHandTracking.CurlOf` will read back as `curl`.
    private static void SetTip(XRHandTracker tracker, XRHandTracker.HandJoint tip, Vector3 knuckle,
                               Vector3 along, Vector3 palmward, float curl, Basis basis)
    {
        float c = Mathf.Clamp(curl, 0f, 1f);
        var dir = along.Slerp(palmward, c);
        SetJoint(tracker, tip, knuckle + dir * (Span * (0.25f + 0.55f * (1f - c))), basis);
    }

    private static void SetJoint(XRHandTracker tracker, XRHandTracker.HandJoint joint,
                                 Vector3 origin, Basis basis)
    {
        tracker.SetHandJointTransform(joint, new Transform3D(basis, origin));
        tracker.SetHandJointFlags(joint,
            XRHandTracker.HandJointFlags.PositionValid | XRHandTracker.HandJointFlags.PositionTracked |
            XRHandTracker.HandJointFlags.OrientationValid | XRHandTracker.HandJointFlags.OrientationTracked);
        tracker.SetHandJointRadius(joint, 0.008f);
    }

    /// The joints this project never reads, parked between the wrist and their fingertip so the
    /// published hand is at least geometrically plausible.
    private static void FillUnusedJoints(XRHandTracker tracker, Vector3 wrist, Basis basis)
    {
        foreach (var (joint, parent, tip, t) in Intermediates)
        {
            var a = tracker.GetHandJointTransform(parent).Origin;
            var b = tracker.GetHandJointTransform(tip).Origin;
            if (a == Vector3.Zero && b == Vector3.Zero) { a = wrist; b = wrist; }
            SetJoint(tracker, joint, a.Lerp(b, t), basis);
        }
    }

    /// (joint to fill, joint before it, joint after it, fraction between them).
    private static readonly (XRHandTracker.HandJoint, XRHandTracker.HandJoint, XRHandTracker.HandJoint, float)[]
        Intermediates =
    {
        (XRHandTracker.HandJoint.ThumbMetacarpal, XRHandTracker.HandJoint.Wrist, XRHandTracker.HandJoint.ThumbPhalanxProximal, 0.5f),
        (XRHandTracker.HandJoint.ThumbPhalanxDistal, XRHandTracker.HandJoint.ThumbPhalanxProximal, XRHandTracker.HandJoint.ThumbTip, 0.6f),
        (XRHandTracker.HandJoint.IndexFingerMetacarpal, XRHandTracker.HandJoint.Wrist, XRHandTracker.HandJoint.IndexFingerPhalanxProximal, 0.5f),
        (XRHandTracker.HandJoint.IndexFingerPhalanxIntermediate, XRHandTracker.HandJoint.IndexFingerPhalanxProximal, XRHandTracker.HandJoint.IndexFingerTip, 0.45f),
        (XRHandTracker.HandJoint.IndexFingerPhalanxDistal, XRHandTracker.HandJoint.IndexFingerPhalanxProximal, XRHandTracker.HandJoint.IndexFingerTip, 0.75f),
        (XRHandTracker.HandJoint.MiddleFingerMetacarpal, XRHandTracker.HandJoint.Wrist, XRHandTracker.HandJoint.MiddleFingerPhalanxProximal, 0.5f),
        (XRHandTracker.HandJoint.MiddleFingerPhalanxIntermediate, XRHandTracker.HandJoint.MiddleFingerPhalanxProximal, XRHandTracker.HandJoint.MiddleFingerTip, 0.45f),
        (XRHandTracker.HandJoint.MiddleFingerPhalanxDistal, XRHandTracker.HandJoint.MiddleFingerPhalanxProximal, XRHandTracker.HandJoint.MiddleFingerTip, 0.75f),
        (XRHandTracker.HandJoint.RingFingerMetacarpal, XRHandTracker.HandJoint.Wrist, XRHandTracker.HandJoint.PinkyFingerPhalanxProximal, 0.5f),
        (XRHandTracker.HandJoint.RingFingerPhalanxProximal, XRHandTracker.HandJoint.Wrist, XRHandTracker.HandJoint.RingFingerTip, 0.55f),
        (XRHandTracker.HandJoint.RingFingerPhalanxIntermediate, XRHandTracker.HandJoint.PinkyFingerPhalanxProximal, XRHandTracker.HandJoint.RingFingerTip, 0.45f),
        (XRHandTracker.HandJoint.RingFingerPhalanxDistal, XRHandTracker.HandJoint.PinkyFingerPhalanxProximal, XRHandTracker.HandJoint.RingFingerTip, 0.75f),
        (XRHandTracker.HandJoint.PinkyFingerMetacarpal, XRHandTracker.HandJoint.Wrist, XRHandTracker.HandJoint.PinkyFingerPhalanxProximal, 0.5f),
        (XRHandTracker.HandJoint.PinkyFingerPhalanxIntermediate, XRHandTracker.HandJoint.PinkyFingerPhalanxProximal, XRHandTracker.HandJoint.PinkyFingerTip, 0.45f),
        (XRHandTracker.HandJoint.PinkyFingerPhalanxDistal, XRHandTracker.HandJoint.PinkyFingerPhalanxProximal, XRHandTracker.HandJoint.PinkyFingerTip, 0.75f),
    };

    // ---------------------------------------------------------------- convenience

    /// Point the headset at a yaw/pitch in degrees, keeping its position.
    public void LookAt(float yawDeg, float pitchDeg = 0f)
    {
        Head.Basis = Basis.FromEuler(new Vector3(Mathf.DegToRad(pitchDeg), Mathf.DegToRad(yawDeg), 0));
    }

    /// Park both controllers in a natural ready position relative to the current head pose, so a
    /// test does not have to re-derive hand placement every time it moves the head.
    public void RestHands()
    {
        var yaw = Basis.FromEuler(new Vector3(0, Head.Basis.GetEuler().Y, 0));
        for (int i = 0; i < 2; i++)
        {
            float x = i == 0 ? -0.22f : 0.22f;
            Hand[i] = new Transform3D(yaw, Head.Origin + yaw * new Vector3(x, -0.55f, -0.32f));
        }
    }

    /// Set one hand's curls in one call, in `HandPoser.Finger` order.
    public void SetCurls(int hand, float thumb, float index, float middle, float ring, float little)
    {
        var c = HandCurl[hand];
        c[(int)HandPoser.Finger.Thumb] = thumb;
        c[(int)HandPoser.Finger.Index] = index;
        c[(int)HandPoser.Finger.Middle] = middle;
        c[(int)HandPoser.Finger.Ring] = ring;
        c[(int)HandPoser.Finger.Little] = little;
    }

    /// Release every button and centre both sticks. Tests share one device across phases, so a
    /// phase that forgets to clean up would otherwise poison the next one.
    public void ClearInputs()
    {
        for (int i = 0; i < 2; i++)
        {
            Stick[i] = Vector2.Zero;
            Trigger[i] = 0f;
            Grip[i] = 0f;
            PrimaryButton[i] = false;
            SecondaryButton[i] = false;
            MenuButton[i] = false;
            StickClick[i] = false;
            ThumbTouch[i] = false;
            PinchOverride[i] = null;
        }
    }
}
