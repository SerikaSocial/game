using Godot;

namespace SerikaSocial.Audio;

/// The client's audio bus graph, built in code like every other piece of UI here.
///
/// Before this there was no graph at all: every sound in the game — world ambience, other players'
/// voices, UI clicks, video playback — went straight to Master, so the only volume control that
/// could exist was one master slider. Turning down a loud world also turned down the people you
/// were talking to, which is the one thing a social app must not do.
///
/// Each category is its own bus routed to Master, so a per-category slider is just that bus's
/// volume and "mute everything except voices" is a real, one-click state.
///
///   Master
///   ├── Voice     other players' speech
///   ├── World     ambience, world-authored sound, footsteps
///   ├── SFX       UI-triggered and gameplay one-shots
///   ├── Music     background music
///   ├── Media     video screens / cinema (the pre-existing "Cinema" bus routes here)
///   └── UI        menu clicks and notification chimes
///   Record        mic capture — deliberately NOT under Master (see below)
///
/// `Record` stays a sibling and stays muted: it is a capture tap, not something anyone should
/// hear. Routing it under Master would put the player's own microphone into their own speakers.
public static class AudioBuses
{
    public const string Master = "Master";
    public const string Voice = "Voice";
    public const string World = "World";
    public const string Sfx = "SFX";
    public const string Music = "Music";
    public const string Media = "Media";
    public const string Ui = "UI";

    /// Every category bus, in the order the settings screen shows them.
    public static readonly string[] Categories = { Voice, World, Sfx, Music, Media, Ui };

    private static bool _built;

    /// Create any missing buses and route them to Master. Idempotent, and safe to call before or
    /// after `VoiceManager` has made its own `Record` bus.
    public static void Ensure()
    {
        if (_built) return;
        _built = true;

        foreach (var name in Categories) EnsureBus(name, Master);

        // The cinema bus predates this graph and used to sit on Master. Re-parent it under Media
        // so the video-volume slider actually governs it.
        int cinema = AudioServer.GetBusIndex("Cinema");
        if (cinema > 0) AudioServer.SetBusSend(cinema, Media);
    }

    /// Find or create `name`, sending to `sendTo`.
    private static int EnsureBus(string name, string sendTo)
    {
        int idx = AudioServer.GetBusIndex(name);
        if (idx < 0)
        {
            idx = AudioServer.BusCount;
            AudioServer.AddBus(idx);
            AudioServer.SetBusName(idx, name);
        }
        // Bus 0 is Master and has no send; setting one on it errors.
        if (idx > 0) AudioServer.SetBusSend(idx, sendTo);
        return idx;
    }

    /// Set a bus's volume from a 0..1 (or higher) linear scalar. Zero mutes outright rather than
    /// converting to -inf dB, which some drivers render as a click.
    public static void SetVolume(string bus, float linear)
    {
        int idx = AudioServer.GetBusIndex(bus);
        if (idx < 0) return;
        bool silent = linear < 0.001f;
        AudioServer.SetBusMute(idx, silent);
        if (!silent) AudioServer.SetBusVolumeDb(idx, Mathf.LinearToDb(Mathf.Clamp(linear, 0.001f, 4f)));
    }

    public static void SetMuted(string bus, bool muted)
    {
        int idx = AudioServer.GetBusIndex(bus);
        if (idx >= 0) AudioServer.SetBusMute(idx, muted);
    }

    public static bool IsMuted(string bus)
    {
        int idx = AudioServer.GetBusIndex(bus);
        return idx >= 0 && AudioServer.IsBusMute(idx);
    }

    // ── Output shaping ────────────────────────────────────────────────────────────────

    /// What the player is listening on. This does NOT change the channel count — that is the OS's
    /// business and Godot reports it via `AudioServer.GetSpeakerMode()`. What it changes is how
    /// aggressively positional audio is panned, which is the part that actually sounds wrong on
    /// the wrong device: headphone panning on speakers is disorienting, and speaker-width panning
    /// on headphones collapses the stereo image.
    public enum Output { Auto = 0, Headphones = 1, Speakers = 2, Surround = 3 }

    /// Apply an output profile. Panning strength is a ProjectSettings value read live by the audio
    /// server, so this takes effect immediately with no restart.
    public static void ApplyOutput(Output mode)
    {
        // Godot's default is 1.0. Headphones carry a wider image comfortably; speakers need it
        // pulled in or everything hard-pans as you turn.
        float panning3d = mode switch
        {
            Output.Headphones => 1.35f,
            Output.Speakers => 0.75f,
            Output.Surround => 1.0f,
            _ => 1.0f,
        };
        ProjectSettings.SetSetting("audio/general/3d_panning_strength", panning3d);
        ProjectSettings.SetSetting("audio/general/2d_panning_strength", panning3d * 0.75f);
    }

    /// Human-readable description of the output the audio driver actually negotiated. Worth
    /// surfacing because "surround" is a property of the player's system, not something the game
    /// can switch on, and saying so avoids a setting that appears to do nothing.
    public static string DetectedOutput()
    {
        try
        {
            return AudioServer.GetSpeakerMode() switch
            {
                AudioServer.SpeakerMode.ModeStereo => "Stereo",
                AudioServer.SpeakerMode.Surround31 => "3.1 surround",
                AudioServer.SpeakerMode.Surround51 => "5.1 surround",
                AudioServer.SpeakerMode.Surround71 => "7.1 surround",
                _ => "Unknown",
            };
        }
        catch { return "Unknown"; }
    }

    // ── Night mode ────────────────────────────────────────────────────────────────────

    /// Compress the master output so a sudden loud world or a shouting player cannot spike over
    /// everything else. Aimed at headphone use late at night, hence the name people expect.
    public static void SetNightMode(bool on)
    {
        int master = AudioServer.GetBusIndex(Master);
        if (master < 0) return;

        int existing = -1;
        for (int i = 0; i < AudioServer.GetBusEffectCount(master); i++)
            if (AudioServer.GetBusEffect(master, i) is AudioEffectCompressor) { existing = i; break; }

        if (on && existing < 0)
        {
            AudioServer.AddBusEffect(master, new AudioEffectCompressor
            {
                Threshold = -22f,
                Ratio = 6f,
                AttackUs = 15f,
                ReleaseMs = 180f,
                Gain = 5f,
            });
        }
        else if (!on && existing >= 0)
        {
            AudioServer.RemoveBusEffect(master, existing);
        }
    }
}
