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

    /// The gesture a controller is making, from the three signals it actually has.
    ///
    /// **A controller cannot be classified by measuring finger curls, and trying to was a bug.**
    /// The curl route synthesised thumb/index/grip into five floats and ran them through
    /// `Classify` below. That works for the shapes whose finger pattern the hardware happens to
    /// reproduce — a fist really is all five closed — and is *unable in principle* to produce the
    /// ones whose pattern it does not. A peace sign is index and middle extended with ring and
    /// little closed; one grip axis moves all three of those together, so no combination of
    /// trigger and grip can ever express it. Peace and Rock'n'Roll were therefore unreachable on
    /// a controller, and the classifier quietly returned whatever the previous gesture had been.
    ///
    /// So controllers get a lookup table instead, keyed on the three bits VRChat keys on, which is
    /// the layout the hardware affords and the one players already have in their hands. The seven
    /// shapes plus the resting state fill the eight combinations exactly.
    ///
    /// The one judgement call is which combination is the resting state. VRChat's own
    /// documentation describes the shapes in prose rather than as a table, and a literal reading
    /// puts the peace sign on "thumb resting, nothing pressed" — which is precisely how a hand
    /// sits when it is doing nothing at all, so every idle player would stand there flashing a V.
    /// Neutral takes that slot and the peace sign moves to the combination the literal reading
    /// leaves unused.
    public static HandGesture FromController(bool thumbDown, bool trigger, bool grip) =>
        (thumbDown, trigger, grip) switch
        {
            (true,  false, false) => HandGesture.Neutral,   // resting on the stick, nothing pulled
            (false, false, false) => HandGesture.Open,
            (true,  true,  true)  => HandGesture.Fist,
            (true,  false, true)  => HandGesture.Point,
            (false, true,  true)  => HandGesture.ThumbsUp,
            (false, false, true)  => HandGesture.Gun,
            (true,  true,  false) => HandGesture.RockNRoll,
            (false, true,  false) => HandGesture.Peace,
        };

    /// The finger shape each gesture actually is, so the avatar's hand forms the pose rather than
    /// whatever the trigger and grip happened to describe.
    ///
    /// This is what makes the table above worth having: once the gesture is known by name, the
    /// hand can be posed from the name. A peace sign closes ring and little while the grip axis
    /// that nominally drives them is released, which is exactly the shape the old curl route could
    /// not reach.
    public static void CurlsForGesture(HandGesture gesture, float[] dst)
    {
        if (dst == null || dst.Length < 5) return;
        // thumb, index, middle, ring, little.
        var shape = gesture switch
        {
            HandGesture.Fist      => new[] { 1f, 1f, 1f, 1f, 1f },
            HandGesture.Open      => new[] { 0f, 0f, 0f, 0f, 0f },
            HandGesture.Point     => new[] { 1f, 0f, 1f, 1f, 1f },
            HandGesture.ThumbsUp  => new[] { 0f, 1f, 1f, 1f, 1f },
            HandGesture.Peace     => new[] { 1f, 0f, 0f, 1f, 1f },
            HandGesture.RockNRoll => new[] { 1f, 0f, 1f, 1f, 0f },
            HandGesture.Gun       => new[] { 0f, 0f, 1f, 1f, 1f },
            // A relaxed hand is not a flat hand: fingers rest slightly curled, and posing them
            // dead straight is what makes an idle avatar's hands look like mannequin parts.
            _                     => new[] { 0.18f, 0.22f, 0.25f, 0.28f, 0.30f },
        };
        System.Array.Copy(shape, dst, 5);
    }

    /// Blend `current` toward `target` at a fixed rate, in place.
    ///
    /// Curls used to be assigned outright, so a finger crossed its whole range in one frame. That
    /// reads as the hand *teleporting* between shapes — the pose is right in every still frame and
    /// wrong in motion, which is the failure mode nobody catches in a screenshot. A real finger
    /// takes roughly 80–120 ms to close, and matching that is most of what makes a synthetic hand
    /// look like it belongs to someone.
    ///
    /// Exponential, so the rate is frame-rate independent: a Quest at 72 Hz and a desktop at 144
    /// must not close their fingers at different speeds.
    public static void Approach(float[] current, float[] target, float dt, float seconds = 0.09f)
    {
        if (current == null || target == null) return;
        float k = 1f - Mathf.Exp(-dt / Mathf.Max(0.001f, seconds));
        for (int i = 0; i < current.Length && i < target.Length; i++)
            current[i] = Mathf.Lerp(current[i], target[i], k);
    }

    /// The curl a *relaxed* hand should show, following the analogue trigger and grip directly.
    ///
    /// Only used while the gesture is `Neutral`. There the player is not asking for a shape, so
    /// the most useful thing the hand can do is track what their fingers are really doing — a half
    /// squeeze shows as a half-closed hand. Every other gesture is a deliberate pose and is driven
    /// from `CurlsForGesture`, or the analogue value would fight the shape.
    public static void RelaxedCurls(float trigger, float grip, bool thumbDown, float[] dst)
    {
        if (dst == null || dst.Length < 5) return;
        float g = Mathf.Clamp(grip, 0f, 1f);
        dst[(int)HandPoser.Finger.Thumb] = thumbDown ? 0.75f : 0.18f;
        dst[(int)HandPoser.Finger.Index] = Mathf.Max(0.22f, Mathf.Clamp(trigger, 0f, 1f));
        dst[(int)HandPoser.Finger.Middle] = Mathf.Max(0.25f, g);
        dst[(int)HandPoser.Finger.Ring] = Mathf.Max(0.28f, g);
        dst[(int)HandPoser.Finger.Little] = Mathf.Max(0.30f, g);
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
