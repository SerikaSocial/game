using Godot;

namespace SerikaSocial.World;

/// Turns a cinema's flat video audio into positional, surround-style sound.
///
/// A `VideoStreamPlayer` mixes its audio straight to a 2D bus — there's no built-in way to make
/// film audio come from the screen, let alone wrap it around the room. So this taps the video's
/// audio off a dedicated "Cinema" bus with an `AudioEffectCapture` and re-plays it through a ring
/// of `AudioStreamPlayer3D` speakers (front L/R by the screen, rear L/R behind the seats). The
/// left bus channel feeds the left speakers and the right feeds the right, so as you walk the
/// room the mix pans for real — the same capture→generator technique the voice chat uses.
///
/// Honest limits: this only makes sound when a clip actually has decodable audio (Ogg Theora
/// today — see VideoScreen), and true hardware 5.1 depends on the OS output device. What it
/// delivers regardless is genuinely *placed* audio the listener moves through, not a flat mix.
public partial class CinemaSpeakers : Node
{
    /// Dedicated bus the video audio is routed to, so it can be captured and re-spatialised.
    public const string Bus = "Cinema";

    private AudioEffectCapture _capture;
    private readonly System.Collections.Generic.List<Speaker> _speakers = new();

    private sealed class Speaker
    {
        public AudioStreamPlayer3D Node;
        public AudioStreamGeneratorPlayback Playback;
        public bool Right; // which bus channel feeds it
    }

    /// Ensure the Cinema bus exists with a capture effect. Left **audible** here: a video screen
    /// that isn't in a cinema (no CinemaSpeakers) must still play its sound as a normal flat mix.
    /// Only `Setup` mutes the bus, because only then is something re-playing it in 3D. Calling
    /// this per world also resets the mute, so leaving a cinema doesn't silence the next world's
    /// screen. Returns the bus index; safe to call repeatedly.
    public static int EnsureBus()
    {
        int idx = AudioServer.GetBusIndex(Bus);
        if (idx < 0)
        {
            idx = AudioServer.BusCount;
            AudioServer.AddBus(idx);
            AudioServer.SetBusName(idx, Bus);
        }
        // Capture effect must be present exactly once (it taps a copy — it doesn't mute the signal).
        bool hasCapture = false;
        for (int e = 0; e < AudioServer.GetBusEffectCount(idx); e++)
            if (AudioServer.GetBusEffect(idx, e) is AudioEffectCapture) { hasCapture = true; break; }
        if (!hasCapture) AudioServer.AddBusEffect(idx, new AudioEffectCapture());

        AudioServer.SetBusMute(idx, false);
        return idx;
    }

    /// Place the speakers. `screenZ` is the screen wall (−z), `backZ` the rear wall; `halfWidth`
    /// spreads the L/R pair. Called by the cinema builder.
    public void Setup(float screenZ, float backZ, float halfWidth, float height)
    {
        int idx = EnsureBus();
        for (int e = 0; e < AudioServer.GetBusEffectCount(idx); e++)
            if (AudioServer.GetBusEffect(idx, e) is AudioEffectCapture cap) { _capture = cap; break; }

        // Now that we're re-playing the audio through 3D speakers, silence the bus's flat 2D
        // output so the room doesn't hear both. EnsureBus (per world) unmutes it again elsewhere.
        AudioServer.SetBusMute(idx, true);

        // Front pair flanking the screen, rear pair behind the seats — four corners of the room.
        AddSpeaker(new Vector3(-halfWidth, height, screenZ + 0.4f), right: false);
        AddSpeaker(new Vector3(halfWidth, height, screenZ + 0.4f), right: true);
        AddSpeaker(new Vector3(-halfWidth, height, backZ - 0.4f), right: false);
        AddSpeaker(new Vector3(halfWidth, height, backZ - 0.4f), right: true);
    }

    private void AddSpeaker(Vector3 pos, bool right)
    {
        var node = new AudioStreamPlayer3D
        {
            Position = pos,
            UnitSize = 12f,                 // audible across the room
            MaxDistance = 40f,
            AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance,
            PanningStrength = 1.0f,
            Stream = new AudioStreamGenerator { MixRate = AudioServer.GetMixRate(), BufferLength = 0.2f },
        };
        AddChild(node);
        node.Play();
        _speakers.Add(new Speaker
        {
            Node = node,
            Playback = node.GetStreamPlayback() as AudioStreamGeneratorPlayback,
            Right = right,
        });
    }

    public override void _Process(double delta)
    {
        if (_capture == null) return;
        int avail = _capture.GetFramesAvailable();
        if (avail <= 0) return;

        // Drain the captured stereo bus once, then fan each channel out to its side's speakers.
        var stereo = _capture.GetBuffer(avail);
        if (stereo.Length == 0) return;

        foreach (var sp in _speakers)
        {
            if (sp.Playback == null) continue;
            int room = sp.Playback.GetFramesAvailable();
            int n = Mathf.Min(stereo.Length, room);
            if (n <= 0) continue;

            var mono = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                float s = sp.Right ? stereo[i].Y : stereo[i].X;
                mono[i] = new Vector2(s, s);
            }
            sp.Playback.PushBuffer(mono);
        }
    }
}
