using Godot;
using SerikaSocial.Avatar;

namespace SerikaSocial.Player;

/// The hand shapes a social VR player expects to be able to make. Deliberately the same set
/// VRChat settled on, because it is what people already know how to perform and what they read
/// on other people's avatars without being told.
public enum HandGesture
{
    /// Hand relaxed — no deliberate shape. The resting state, not a pose anyone performs.
    Neutral = 0,
    Fist,
    Open,
    Point,
    ThumbsUp,
    Peace,
    RockNRoll,
    Gun,
}

/// Turns raw finger curls into a `HandGesture`, from either input source.
///
/// Both optical hand tracking and controllers funnel through the same five-float curl vector, so
/// there is exactly one classifier and a gesture means the same thing however it was made. The
/// alternative — a joint-geometry classifier for hands and a button-combo table for controllers —
/// drifts, and then the same shape reads as two different gestures depending on what the player
/// happens to be holding.
public static class HandGestures
{
    /// Curl above this counts as "closed", below `Open` as "extended". The gap between the two is
    /// dead space: a finger caught mid-way matches nothing and the gesture holds its last value
    /// rather than flickering between two neighbours as a curl drifts across a single threshold.
    private const float Closed = 0.62f;
    private const float Open = 0.38f;

    /// Synthesise a curl vector from controller inputs.
    ///
    /// The mapping is the one every OpenXR title uses because it is the one the hardware affords:
    /// the trigger sits under the index finger, the grip under the remaining three, and the thumb
    /// rests on the face buttons. It is a coarse three-degrees-of-freedom approximation of a
    /// twenty-joint hand, which is precisely why the gesture set above is small.
    ///
    /// `thumbDown` has no analogue input on any common controller — the built-in OpenXR action map
    /// exposes no touch-capacitance actions — so it is driven by whether the thumb is doing
    /// anything at all: resting on a face button or on the stick.
    public static void CurlsFromController(float trigger, float grip, bool thumbDown, float[] dst)
    {
        if (dst == null || dst.Length < 5) return;
        dst[(int)HandPoser.Finger.Thumb] = thumbDown ? 1f : 0f;
        dst[(int)HandPoser.Finger.Index] = Mathf.Clamp(trigger, 0f, 1f);
        float g = Mathf.Clamp(grip, 0f, 1f);
        dst[(int)HandPoser.Finger.Middle] = g;
        dst[(int)HandPoser.Finger.Ring] = g;
        dst[(int)HandPoser.Finger.Little] = g;
    }

    /// Classify a curl vector. `previous` is returned whenever the hand is in the dead space
    /// between shapes, which is what stops a gesture strobing while a finger hovers on a
    /// threshold — a held pose is the whole point of a gesture in a social space, and a gesture
    /// that flickers is worse than no gesture at all.
    public static HandGesture Classify(float[] curl, HandGesture previous = HandGesture.Neutral)
    {
        if (curl == null || curl.Length < 5) return HandGesture.Neutral;

        bool thumbIn = curl[(int)HandPoser.Finger.Thumb] > Closed;
        bool thumbOut = curl[(int)HandPoser.Finger.Thumb] < Open;
        bool indexIn = curl[(int)HandPoser.Finger.Index] > Closed;
        bool indexOut = curl[(int)HandPoser.Finger.Index] < Open;
        bool midIn = curl[(int)HandPoser.Finger.Middle] > Closed;
        bool midOut = curl[(int)HandPoser.Finger.Middle] < Open;
        bool ringIn = curl[(int)HandPoser.Finger.Ring] > Closed;
        bool littleIn = curl[(int)HandPoser.Finger.Little] > Closed;
        bool littleOut = curl[(int)HandPoser.Finger.Little] < Open;

        // Ordered most specific first: a gun is also "index extended, middle/ring/little closed",
        // so testing Point before Gun would swallow it.
        if (thumbIn && indexIn && midIn && ringIn && littleIn) return HandGesture.Fist;
        if (thumbOut && indexOut && midOut && littleOut) return HandGesture.Open;
        if (thumbOut && indexOut && midIn && ringIn && littleIn) return HandGesture.Gun;
        if (thumbIn && indexOut && midOut && ringIn && littleIn) return HandGesture.Peace;
        if (thumbOut && indexIn && midIn && ringIn && littleIn) return HandGesture.ThumbsUp;
        if (indexOut && midIn && ringIn && littleOut) return HandGesture.RockNRoll;
        if (thumbIn && indexOut && midIn && ringIn && littleIn) return HandGesture.Point;

        return previous;
    }

    /// A short human-readable name, for the wrist HUD and the settings preview.
    public static string Label(HandGesture g) => g switch
    {
        HandGesture.Fist => "Fist",
        HandGesture.Open => "Open",
        HandGesture.Point => "Point",
        HandGesture.ThumbsUp => "Thumbs up",
        HandGesture.Peace => "Peace",
        HandGesture.RockNRoll => "Rock",
        HandGesture.Gun => "Gun",
        _ => "",
    };
}
