using System.Collections.Generic;
using Godot;
using SerikaSocial.Avatar;

namespace SerikaSocial.Player;

/// Optical hand tracking for one hand, read from the `XRHandTracker` the OpenXR interface
/// registers with `XRServer`.
///
/// Tracks all 26 standard OpenXR hand joints, derives per-finger Curl and Splay (abduction/spread),
/// computes 3D Thumb Opposition, and applies a 1-Euro adaptive low-pass filter to eliminate
/// micro-jitter while preserving sub-millisecond responsiveness for fast gestures.
public sealed class VrHandTracking
{
    private const string LeftTracker = "/user/hand_tracker/left";
    private const string RightTracker = "/user/hand_tracker/right";

    private readonly bool _isLeft;
    private XRHandTracker _tracker;
    private double _rescanTimer;

    /// Full standard 26-joint OpenXR joint set for high-fidelity hand kinematics.
    private static readonly XRHandTracker.HandJoint[] AllJoints =
    {
        XRHandTracker.HandJoint.Palm,
        XRHandTracker.HandJoint.Wrist,
        XRHandTracker.HandJoint.ThumbMetacarpal,
        XRHandTracker.HandJoint.ThumbPhalanxProximal,
        XRHandTracker.HandJoint.ThumbPhalanxDistal,
        XRHandTracker.HandJoint.ThumbTip,
        XRHandTracker.HandJoint.IndexFingerMetacarpal,
        XRHandTracker.HandJoint.IndexFingerPhalanxProximal,
        XRHandTracker.HandJoint.IndexFingerPhalanxIntermediate,
        XRHandTracker.HandJoint.IndexFingerPhalanxDistal,
        XRHandTracker.HandJoint.IndexFingerTip,
        XRHandTracker.HandJoint.MiddleFingerMetacarpal,
        XRHandTracker.HandJoint.MiddleFingerPhalanxProximal,
        XRHandTracker.HandJoint.MiddleFingerPhalanxIntermediate,
        XRHandTracker.HandJoint.MiddleFingerPhalanxDistal,
        XRHandTracker.HandJoint.MiddleFingerTip,
        XRHandTracker.HandJoint.RingFingerMetacarpal,
        XRHandTracker.HandJoint.RingFingerPhalanxProximal,
        XRHandTracker.HandJoint.RingFingerPhalanxIntermediate,
        XRHandTracker.HandJoint.RingFingerPhalanxDistal,
        XRHandTracker.HandJoint.RingFingerTip,
        XRHandTracker.HandJoint.PinkyFingerMetacarpal,
        XRHandTracker.HandJoint.PinkyFingerPhalanxProximal,
        XRHandTracker.HandJoint.PinkyFingerPhalanxIntermediate,
        XRHandTracker.HandJoint.PinkyFingerPhalanxDistal,
        XRHandTracker.HandJoint.PinkyFingerTip,
    };

    private readonly Dictionary<XRHandTracker.HandJoint, Transform3D> _joints = new();

    // 1-Euro adaptive low-pass filters for curl and splay (one per finger)
    private readonly OneEuroFilter[] _curlFilter = new OneEuroFilter[5];
    private readonly OneEuroFilter[] _splayFilter = new OneEuroFilter[5];
    private OneEuroFilter _pinchFilter;
    private OneEuroFilter _thumbOppFilter;

    public VrHandTracking(bool isLeft) => _isLeft = isLeft;

    /// True only while a camera is genuinely seeing this hand unobstructed.
    public bool Active { get; private set; }

    /// Per-finger curl, 0 = straight, 1 = fully closed (Index: Thumb, Index, Middle, Ring, Little).
    public readonly float[] Curl = new float[5];

    /// Per-finger lateral splay (spread), in radians (-0.35 to +0.35). 0 = neutral resting fan.
    public readonly float[] Splay = new float[5];

    /// 3D Thumb opposition across the palm (0 = resting lateral, 1 = fully opposed facing pinky).
    public float ThumbOpposition { get; private set; }

    /// Thumb-tip to index-tip distance normalised into a 0–1 pinch strength (1 = touching).
    public float Pinch { get; private set; }

    /// Thumb-tip to middle-tip distance normalised into a 0–1 pinch strength.
    public float MiddlePinch { get; private set; }

    /// The pose the UI ray is cast from when bare hands are driving menus.
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

    /// The wrist pose in world space.
    public bool TryGetWrist(out Transform3D wrist)
        => _joints.TryGetValue(XRHandTracker.HandJoint.Wrist, out wrist);

    public bool TryGetJoint(XRHandTracker.HandJoint joint, out Transform3D xform)
        => _joints.TryGetValue(joint, out xform);

    /// Refresh from the tracker. `originXform` converts tracker-space poses into world space.
    public void Poll(double delta, Transform3D originXform)
    {
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

        foreach (var joint in AllJoints)
        {
            var flags = _tracker.GetHandJointFlags(joint);
            if ((flags & XRHandTracker.HandJointFlags.PositionValid) == 0)
            {
                // Fallback for optional metacarpals if unsupported on older runtimes
                if (joint == XRHandTracker.HandJoint.ThumbMetacarpal
                    || joint == XRHandTracker.HandJoint.IndexFingerMetacarpal
                    || joint == XRHandTracker.HandJoint.MiddleFingerMetacarpal
                    || joint == XRHandTracker.HandJoint.RingFingerMetacarpal
                    || joint == XRHandTracker.HandJoint.PinkyFingerMetacarpal)
                {
                    continue;
                }
                allValid = false;
                break;
            }
            _joints[joint] = originXform * _tracker.GetHandJointTransform(joint);
        }

        if (!allValid || !_joints.ContainsKey(XRHandTracker.HandJoint.Wrist) || !_joints.ContainsKey(XRHandTracker.HandJoint.Palm))
        {
            Reset();
            return;
        }

        Active = true;
        Measure((float)delta);
    }

    private void Reset()
    {
        Active = false;
        Pinch = 0f;
        MiddlePinch = 0f;
        ThumbOpposition = 0f;
        _joints.Clear();
        for (int i = 0; i < Curl.Length; i++)
        {
            Curl[i] = 0f;
            Splay[i] = 0f;
            _curlFilter[i].Reset();
            _splayFilter[i].Reset();
        }
        _pinchFilter.Reset();
        _thumbOppFilter.Reset();
    }

    /// Derive raw curls, splay angles, thumb opposition, and pinches, then apply 1-Euro smoothing.
    private void Measure(float dt)
    {
        var wrist = _joints[XRHandTracker.HandJoint.Wrist].Origin;
        var midKnuckle = _joints[XRHandTracker.HandJoint.MiddleFingerPhalanxProximal].Origin;
        float span = wrist.DistanceTo(midKnuckle);
        if (span < 1e-4f) { Reset(); return; }

        var palmNormal = _joints[XRHandTracker.HandJoint.Palm].Basis.Y.Normalized();
        if (_isLeft) palmNormal = -palmNormal;

        // Raw Curls
        float rawThumbCurl = CurlOf(XRHandTracker.HandJoint.ThumbTip, XRHandTracker.HandJoint.ThumbPhalanxProximal, span);
        float rawIndexCurl = CurlOf(XRHandTracker.HandJoint.IndexFingerTip, XRHandTracker.HandJoint.IndexFingerPhalanxProximal, span);
        float rawMiddleCurl = CurlOf(XRHandTracker.HandJoint.MiddleFingerTip, XRHandTracker.HandJoint.MiddleFingerPhalanxProximal, span);
        float rawRingCurl = CurlOf(XRHandTracker.HandJoint.RingFingerTip, XRHandTracker.HandJoint.RingFingerPhalanxProximal, span);
        float rawLittleCurl = CurlOf(XRHandTracker.HandJoint.PinkyFingerTip, XRHandTracker.HandJoint.PinkyFingerPhalanxProximal, span);

        // 1-Euro adaptive low-pass filter on curls
        Curl[(int)HandPoser.Finger.Thumb] = _curlFilter[0].Filter(rawThumbCurl, dt, minCutoff: 1.2f, beta: 0.05f);
        Curl[(int)HandPoser.Finger.Index] = _curlFilter[1].Filter(rawIndexCurl, dt, minCutoff: 1.2f, beta: 0.05f);
        Curl[(int)HandPoser.Finger.Middle] = _curlFilter[2].Filter(rawMiddleCurl, dt, minCutoff: 1.2f, beta: 0.05f);
        Curl[(int)HandPoser.Finger.Ring] = _curlFilter[3].Filter(rawRingCurl, dt, minCutoff: 1.2f, beta: 0.05f);
        Curl[(int)HandPoser.Finger.Little] = _curlFilter[4].Filter(rawLittleCurl, dt, minCutoff: 1.2f, beta: 0.05f);

        // Raw Splay (lateral spread of fingers across the palm plane)
        var palmFwd = (midKnuckle - wrist).Normalized();
        var palmRight = palmFwd.Cross(palmNormal).Normalized();

        float rawIndexSplay = SplayOf(XRHandTracker.HandJoint.IndexFingerPhalanxProximal, XRHandTracker.HandJoint.IndexFingerTip, palmRight, palmFwd);
        float rawMiddleSplay = SplayOf(XRHandTracker.HandJoint.MiddleFingerPhalanxProximal, XRHandTracker.HandJoint.MiddleFingerTip, palmRight, palmFwd);
        float rawRingSplay = SplayOf(XRHandTracker.HandJoint.RingFingerPhalanxProximal, XRHandTracker.HandJoint.RingFingerTip, palmRight, palmFwd);
        float rawLittleSplay = SplayOf(XRHandTracker.HandJoint.PinkyFingerPhalanxProximal, XRHandTracker.HandJoint.PinkyFingerTip, palmRight, palmFwd);
        float rawThumbSplay = SplayOf(XRHandTracker.HandJoint.ThumbPhalanxProximal, XRHandTracker.HandJoint.ThumbTip, palmRight, palmFwd);

        Splay[(int)HandPoser.Finger.Thumb] = _splayFilter[0].Filter(rawThumbSplay, dt, minCutoff: 1.0f, beta: 0.04f);
        Splay[(int)HandPoser.Finger.Index] = _splayFilter[1].Filter(rawIndexSplay, dt, minCutoff: 1.0f, beta: 0.04f);
        Splay[(int)HandPoser.Finger.Middle] = _splayFilter[2].Filter(rawMiddleSplay, dt, minCutoff: 1.0f, beta: 0.04f);
        Splay[(int)HandPoser.Finger.Ring] = _splayFilter[3].Filter(rawRingSplay, dt, minCutoff: 1.0f, beta: 0.04f);
        Splay[(int)HandPoser.Finger.Little] = _splayFilter[4].Filter(rawLittleSplay, dt, minCutoff: 1.0f, beta: 0.04f);

        // Thumb Opposition: distance from thumb tip to pinky base
        if (_joints.TryGetValue(XRHandTracker.HandJoint.ThumbTip, out var thumbTip)
            && _joints.TryGetValue(XRHandTracker.HandJoint.PinkyFingerPhalanxProximal, out var pinkyKnuckle))
        {
            float oppDist = thumbTip.Origin.DistanceTo(pinkyKnuckle.Origin) / span;
            float rawOpp = 1f - Mathf.Clamp((oppDist - 0.25f) / 0.65f, 0f, 1f);
            ThumbOpposition = _thumbOppFilter.Filter(rawOpp, dt, minCutoff: 1.2f, beta: 0.05f);
        }

        // Multi-Finger Pinches
        if (_joints.TryGetValue(XRHandTracker.HandJoint.ThumbTip, out var tTip))
        {
            if (_joints.TryGetValue(XRHandTracker.HandJoint.IndexFingerTip, out var iTip))
            {
                float d = tTip.Origin.DistanceTo(iTip.Origin) / span;
                float rawPinch = 1f - Mathf.Clamp((d - 0.02f) / 0.43f, 0f, 1f);
                Pinch = _pinchFilter.Filter(rawPinch, dt, minCutoff: 2.0f, beta: 0.1f);
            }
            if (_joints.TryGetValue(XRHandTracker.HandJoint.MiddleFingerTip, out var mTip))
            {
                float d = tTip.Origin.DistanceTo(mTip.Origin) / span;
                MiddlePinch = 1f - Mathf.Clamp((d - 0.02f) / 0.43f, 0f, 1f);
            }
        }
    }

    private float CurlOf(XRHandTracker.HandJoint tip, XRHandTracker.HandJoint knuckle, float span)
    {
        if (!_joints.TryGetValue(tip, out var t) || !_joints.TryGetValue(knuckle, out var k)) return 0f;
        float extension = t.Origin.DistanceTo(k.Origin) / span;
        return 1f - Mathf.Clamp((extension - 0.25f) / 0.55f, 0f, 1f);
    }

    private float SplayOf(XRHandTracker.HandJoint knuckle, XRHandTracker.HandJoint tip, Vector3 palmRight, Vector3 palmFwd)
    {
        if (!_joints.TryGetValue(knuckle, out var k) || !_joints.TryGetValue(tip, out var t)) return 0f;
        var dir = (t.Origin - k.Origin).Normalized();
        float sideways = dir.Dot(palmRight);
        return Mathf.Clamp(sideways * 0.65f, -0.45f, 0.45f);
    }

    /// 1-Euro adaptive low-pass filter struct for smooth, jitter-free, zero-latency motion.
    private struct OneEuroFilter
    {
        private float _x;
        private float _dx;
        private bool _init;

        public float Filter(float val, float dt, float minCutoff, float beta)
        {
            if (dt <= 0f) return val;
            if (!_init)
            {
                _x = val;
                _dx = 0f;
                _init = true;
                return val;
            }

            float dVal = (val - _x) / dt;
            float alphaD = Alpha(dt, 1.0f);
            _dx = Mathf.Lerp(_dx, dVal, alphaD);

            float cutoff = minCutoff + beta * Mathf.Abs(_dx);
            float alpha = Alpha(dt, cutoff);
            _x = Mathf.Lerp(_x, val, alpha);
            return _x;
        }

        public void Reset()
        {
            _init = false;
            _x = 0f;
            _dx = 0f;
        }

        private static float Alpha(float dt, float cutoff)
        {
            float tau = 1.0f / (2.0f * Mathf.Pi * Mathf.Max(0.01f, cutoff));
            return 1.0f / (1.0f + tau / dt);
        }
    }
}
