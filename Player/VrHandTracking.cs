using Godot;
using SerikaSocial.Avatar;

namespace SerikaSocial.Player;

/// Optical hand tracking for one hand, read from the `XRHandTracker` the OpenXR interface
/// registers with `XRServer`.
///
/// **Why this is not `XRHandModifier3D`.** That node exists to pose a *hand mesh's* skeleton, and
/// this project has no hand meshes — the player's hands are their avatar's hands, which are part
/// of a full humanoid `.ska` rig driven by `VrAvatarIk`. What is needed here is the raw joint
/// data (to place the wrist target, curl the avatar's fingers and classify gestures), so the
/// tracker is read directly.
///
/// **Controller-derived hands are not hand tracking.** `XRHandTracker.HandTrackingSource`
/// distinguishes `Unobstructed` (a camera really sees the hand) from `Controller` (the runtime
/// synthesised a hand pose from a held controller). Meta's runtime publishes a hand tracker in
/// *both* cases, so a bare `tracker != null` check is true whenever a controller is on — the same
/// trap that made the old `IsOpticalHandTrackingActive()` a no-op (see the note in
/// `VrPlayer.HandleLocomotion`). Only `Unobstructed` counts as bare hands here.
public sealed class VrHandTracking
{
    /// Godot's OpenXR hand trackers register under these names.
    private const string LeftTracker = "/user/hand_tracker/left";
    private const string RightTracker = "/user/hand_tracker/right";

    private readonly bool _isLeft;
    private XRHandTracker _tracker;
    private double _rescanTimer;

    /// The joints whose transforms are cached each frame. The full set is 26 per hand; these are
    /// the ones gesture classification and finger posing actually read, which keeps the per-frame
    /// marshalling cost to 11 interop calls per hand instead of 26.
    private static readonly XRHandTracker.HandJoint[] Cached =
    {
        XRHandTracker.HandJoint.Palm,
        XRHandTracker.HandJoint.Wrist,
        XRHandTracker.HandJoint.ThumbTip,
        XRHandTracker.HandJoint.ThumbPhalanxProximal,
        XRHandTracker.HandJoint.IndexFingerTip,
        XRHandTracker.HandJoint.IndexFingerPhalanxProximal,
        XRHandTracker.HandJoint.MiddleFingerTip,
        XRHandTracker.HandJoint.MiddleFingerPhalanxProximal,
        XRHandTracker.HandJoint.RingFingerTip,
        XRHandTracker.HandJoint.PinkyFingerTip,
        XRHandTracker.HandJoint.PinkyFingerPhalanxProximal,
    };

    private readonly System.Collections.Generic.Dictionary<XRHandTracker.HandJoint, Transform3D> _joints = new();

    public VrHandTracking(bool isLeft) => _isLeft = isLeft;

    /// True only while a camera is genuinely seeing this hand. Controller-derived hand poses
    /// report `Controller` and are deliberately excluded — see the class note.
    public bool Active { get; private set; }

    /// Per-finger curl, 0 = straight, 1 = fully closed. Index order matches
    /// `HandPoser.Finger`: thumb, index, middle, ring, little.
    public readonly float[] Curl = new float[5];

    /// Thumb-tip to index-tip distance normalised into a 0–1 pinch strength. 1 means the tips are
    /// touching. This is the hand-tracking equivalent of a trigger pull and is what drives UI
    /// clicks when there is no controller to pull.
    public float Pinch { get; private set; }

    /// The pose the UI ray is cast from when bare hands are driving the menus: the index
    /// fingertip, aimed along the finger. Pointing at a thing with your finger is the gesture
    /// every hand-tracking platform has converged on, and it is nothing like a controller's grip
    /// axis — reusing the controller ray here aims roughly at the player's own elbow.
    public bool TryGetPointerRay(out Vector3 origin, out Vector3 direction)
    {
        origin = default;
        direction = default;
        if (!Active) return false;
        if (!_joints.TryGetValue(XRHandTracker.HandJoint.IndexFingerTip, out var tip)) return false;
        if (!_joints.TryGetValue(XRHandTracker.HandJoint.IndexFingerPhalanxProximal, out var knuckle)) return false;

        var along = tip.Origin - knuckle.Origin;
        if (along.LengthSquared() < 1e-8f) return false;

        origin = tip.Origin;
        direction = along.Normalized();
        return true;
    }

    /// The wrist pose, used as the avatar's hand IK target while bare hands are tracked. The
    /// wrist rather than the palm because that is what the humanoid `leftHand`/`rightHand` bone
    /// actually is; targeting the palm plants the bone half a hand too far forward.
    public bool TryGetWrist(out Transform3D wrist)
        => _joints.TryGetValue(XRHandTracker.HandJoint.Wrist, out wrist);

    public bool TryGetJoint(XRHandTracker.HandJoint joint, out Transform3D xform)
        => _joints.TryGetValue(joint, out xform);

    /// Refresh from the tracker. `originXform` converts tracker-space poses into world space —
    /// joint transforms are reported relative to the `XROrigin3D`, exactly like `XRNode3D`
    /// positions, so they must be lifted through the origin's global transform or every joint
    /// lands near the world origin.
    public void Poll(double delta, Transform3D originXform)
    {
        // Trackers appear and vanish as the player picks controllers up and puts them down, so
        // the lookup is re-tried periodically rather than resolved once. Polled at 4 Hz because
        // XRServer.GetTracker allocates, and a hand does not materialise mid-frame.
        _rescanTimer -= delta;
        if (_tracker == null || !GodotObject.IsInstanceValid(_tracker) || _rescanTimer <= 0)
        {
            _rescanTimer = 0.25;
            _tracker = XRServer.GetTracker(_isLeft ? LeftTracker : RightTracker) as XRHandTracker;
        }

        if (_tracker == null || !_tracker.HasTrackingData
            || _tracker.HandTrackingSource != XRHandTracker.HandTrackingSourceEnum.Unobstructed)
        {
            Reset();
            return;
        }

        _joints.Clear();
        bool allValid = true;
        foreach (var joint in Cached)
        {
            // A joint the runtime cannot see reports a stale or zeroed transform, and a zeroed
            // fingertip reads as a fully clenched fist — a hand half out of frame would otherwise
            // fire a "fist" gesture every time it drifted to the edge of the tracking volume.
            var flags = _tracker.GetHandJointFlags(joint);
            if ((flags & XRHandTracker.HandJointFlags.PositionValid) == 0) { allValid = false; break; }
            _joints[joint] = originXform * _tracker.GetHandJointTransform(joint);
        }

        if (!allValid) { Reset(); return; }

        Active = true;
        Measure();
    }

    private void Reset()
    {
        Active = false;
        Pinch = 0f;
        _joints.Clear();
        for (int i = 0; i < Curl.Length; i++) Curl[i] = 0f;
    }

    /// Derive curls and pinch from the cached joints.
    ///
    /// Curl is measured as how far a fingertip has closed toward its own knuckle, scaled by the
    /// hand's own size rather than by an absolute distance in metres — hands vary by a factor of
    /// well over 1.5 between a child and a large adult, and a fixed threshold classifies one of
    /// them wrong. `span` (wrist to middle knuckle) is the scale reference because it is the one
    /// measurement that does not change as the fingers move.
    private void Measure()
    {
        var wrist = _joints[XRHandTracker.HandJoint.Wrist].Origin;
        var midKnuckle = _joints[XRHandTracker.HandJoint.MiddleFingerPhalanxProximal].Origin;
        float span = wrist.DistanceTo(midKnuckle);
        if (span < 1e-4f) { Reset(); return; }

        Curl[(int)HandPoser.Finger.Thumb] = CurlOf(
            XRHandTracker.HandJoint.ThumbTip, XRHandTracker.HandJoint.ThumbPhalanxProximal, span);
        Curl[(int)HandPoser.Finger.Index] = CurlOf(
            XRHandTracker.HandJoint.IndexFingerTip, XRHandTracker.HandJoint.IndexFingerPhalanxProximal, span);
        Curl[(int)HandPoser.Finger.Middle] = CurlOf(
            XRHandTracker.HandJoint.MiddleFingerTip, XRHandTracker.HandJoint.MiddleFingerPhalanxProximal, span);
        // Ring and little share the pinky knuckle as their reference: the ring knuckle is not in
        // the cached set (it buys nothing else) and the two knuckles are ~2 cm apart, well inside
        // the noise of this measurement.
        Curl[(int)HandPoser.Finger.Ring] = CurlOf(
            XRHandTracker.HandJoint.RingFingerTip, XRHandTracker.HandJoint.PinkyFingerPhalanxProximal, span);
        Curl[(int)HandPoser.Finger.Little] = CurlOf(
            XRHandTracker.HandJoint.PinkyFingerTip, XRHandTracker.HandJoint.PinkyFingerPhalanxProximal, span);

        float pinchDist = _joints[XRHandTracker.HandJoint.ThumbTip].Origin
            .DistanceTo(_joints[XRHandTracker.HandJoint.IndexFingerTip].Origin);
        // Touching is ~2% of span, clearly apart is ~45%.
        Pinch = 1f - Mathf.Clamp((pinchDist / span - 0.02f) / 0.43f, 0f, 1f);
    }

    /// An extended finger puts its tip roughly `span` away from its knuckle; a closed one folds
    /// the tip back to within about a quarter of that.
    private float CurlOf(XRHandTracker.HandJoint tip, XRHandTracker.HandJoint knuckle, float span)
    {
        if (!_joints.TryGetValue(tip, out var t) || !_joints.TryGetValue(knuckle, out var k)) return 0f;
        float extension = t.Origin.DistanceTo(k.Origin) / span;
        return 1f - Mathf.Clamp((extension - 0.25f) / 0.55f, 0f, 1f);
    }
}
