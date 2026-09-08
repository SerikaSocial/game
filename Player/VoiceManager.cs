using System;
using System.Collections.Generic;
using Godot;

namespace SerikaSocial.Player;

/// How the microphone is gated.
public enum MicMode
{
    /// Mic is off entirely. No capture, no frames.
    Muted,
    /// Transmit whenever VAD says the user is speaking. The default, and what most people mean
    /// by "open mic".
    Open,
    /// Transmit only while the push-to-talk key is held (still gated by VAD's hangover).
    PushToTalk,
    /// Transmit unconditionally — no voice gate at all. For people whose VAD keeps clipping the
    /// start of their words, and for anyone who simply does not want a gate deciding for them.
    /// Costs a constant ~32 KB/s even in silence, which is why it is not the default.
    Always,
}

/// Spatial voice chat. Captures the mic via AudioStreamMicrophone + AudioEffectCapture on the
/// "Record" bus, **resamples to 16 kHz**, applies Voice Activity Detection (VAD) / noise gating
/// with hangover, ships PCM16 frames over the transport, and plays received frames back through
/// an AudioStreamGenerator positioned at each remote avatar's mouth.
///
/// The resampling is not optional and is easy to miss. `AudioEffectCapture` hands back frames at
/// the **AudioServer mix rate** — 44100 Hz unless `audio/driver/mix_rate` says otherwise — not at
/// whatever rate the consumer wants. This class used to take 320 of those frames, call them "20 ms
/// of 16 kHz audio", and push them into a generator declared at 16 kHz. Three things broke at once:
/// playback ran at 16000/44100 = 0.36x speed (a deep unintelligible rumble), frames were emitted
/// 2.76x faster than intended (~137/s, not 50/s, which at 644 B/frame is ~88 KB/s — over the
/// relay's 64 KB/s per-peer budget), and the VAD hangover arithmetic was off by the same factor.
/// `CinemaSpeakers` already got this right with `AudioServer.GetMixRate()`; voice did not.
public partial class VoiceManager : Node
{
    /// One generator playback per speaker node, created lazily the first time we hear them.
    private readonly Dictionary<ulong, AudioStreamGeneratorPlayback> _playbacks = new();
    /// Called when we have a voice frame ready to send over the wire.
    public event System.Action<byte[]> VoiceFrameReady;
    /// Current measured input RMS loudness (0.0 to 1.0) for live UI/HUD meter.
    public float InputRms { get; private set; }
    /// Whether the microphone is actively picking up speech above the VAD gate.
    public bool IsSpeaking { get; private set; }

    /// Estimated vowel viseme weights (0.0 to 1.0) for avatar mouth shapes.
    public float LocalVisemeAa { get; private set; }
    public float LocalVisemeIh { get; private set; }
    public float LocalVisemeOu { get; private set; }
    public float LocalVisemeEe { get; private set; }
    public float LocalVisemeOh { get; private set; }

    /// Gate mode. Setting it starts/stops capture as needed.
    public MicMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            if (_mode == MicMode.Muted) StopRecording();
            else StartRecording();
        }
    }

    /// Held state of the push-to-talk key. Only consulted in `MicMode.PushToTalk`.
    public bool PushToTalkHeld { get; set; }

    /// Input sensitivity multiplier applied to the VAD threshold. >1 makes the gate harder to
    /// open (noisy rooms), <1 easier. Surfaced in Settings.
    public float VadSensitivity { get; set; } = 1f;

    /// Linear gain applied to captured audio before transmit.
    public float MicGain { get; set; } = 1f;

    private MicMode _mode = MicMode.Muted;
    private AudioEffectCapture _capture;
    private AudioStreamPlayer _micPlayer;
    private bool _recording;
    private float _vadHangoverTimer;
    private float _noiseFloor = 0.015f;

    /// The rate `_capture` actually produces. Read from AudioServer at setup, not assumed.
    private int _captureRate = 44100;

    // Resampler state. `_monoIn` holds captured mono samples at `_captureRate`; `_resamplePos` is
    // a fractional read cursor into it that persists across calls so frame boundaries don't drift.
    private readonly List<float> _monoIn = new();
    private double _resamplePos;
    private readonly List<short> _out = new();

    /// Voice wire rate. The relay forwards opaquely, so this only has to agree with itself and
    /// with the receiving generator's MixRate.
    public const int SampleRate = 16000;
    private const int FrameMs = 20; // 20ms frames
    private const int FrameSamples = SampleRate * FrameMs / 1000; // 320 samples @ 16 kHz
    private const float VadHangoverSeconds = 0.25f; // 250ms hangover keeps syllable ends from cutting
    private const float MinVadThreshold = 0.025f;
    /// Hard cap on the capture backlog (~1 s) so a stalled consumer can't grow the list forever.
    private const int MaxBacklogSeconds = 1;

    public override void _Ready()
    {
        // On Android / Meta Quest, request runtime microphone permission
        RequestMicrophonePermission();

        SetupCaptureBus();
        SetupMicrophonePlayer();
    }

    private void RequestMicrophonePermission()
    {
        if (OS.GetName() == "Android")
        {
            try
            {
                var granted = OS.GetGrantedPermissions();
                bool hasPermission = false;
                if (granted != null)
                {
                    foreach (var p in granted)
                    {
                        if (p.Contains("RECORD_AUDIO")) { hasPermission = true; break; }
                    }
                }
                if (!hasPermission)
                {
                    OS.RequestPermission("RECORD_AUDIO");
                    GD.Print("VoiceManager: requested Android RECORD_AUDIO permission");
                }
            }
            catch (System.Exception ex)
            {
                GD.PrintErr($"VoiceManager: failed to query Android permission: {ex.Message}");
            }
        }
    }

    // ── Input devices ─────────────────────────────────────────────────────────────────

    /// Available microphone names, for the Settings picker.
    public static string[] InputDevices()
    {
        try
        {
            return AudioServer.GetInputDeviceList() ?? System.Array.Empty<string>();
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"VoiceManager: could not enumerate input devices: {ex.Message}");
            return System.Array.Empty<string>();
        }
    }

    /// Currently selected microphone ("Default" when untouched).
    public static string CurrentInputDevice
    {
        get { try { return AudioServer.InputDevice; } catch { return "Default"; } }
    }

    /// Switch microphone. Capture is bounced so the new device is actually picked up.
    public void SetInputDevice(string device)
    {
        if (string.IsNullOrEmpty(device)) return;
        bool wasRecording = _recording;
        if (wasRecording) StopRecording();
        try { AudioServer.InputDevice = device; }
        catch (System.Exception ex) { GD.PrintErr($"VoiceManager: could not select input '{device}': {ex.Message}"); }
        if (wasRecording) StartRecording();
    }

    private void SetupCaptureBus()
    {
        int busIndex = AudioServer.GetBusIndex("Record");
        if (busIndex < 0)
        {
            busIndex = AudioServer.BusCount;
            AudioServer.AddBus(busIndex);
            AudioServer.SetBusName(busIndex, "Record");
        }

        // Mute the Record bus in the audio graph so the speaker doesn't hear their own raw voice.
        // Mute is applied when the bus is mixed into its send target, downstream of the effect
        // chain, so AudioEffectCapture still sees the signal.
        AudioServer.SetBusMute(busIndex, true);

        // Find or add AudioEffectCapture
        _capture = null;
        int effectCount = AudioServer.GetBusEffectCount(busIndex);
        for (int i = 0; i < effectCount; i++)
        {
            if (AudioServer.GetBusEffect(busIndex, i) is AudioEffectCapture cap)
            {
                _capture = cap;
                break;
            }
        }

        if (_capture == null)
        {
            _capture = new AudioEffectCapture { BufferLength = 0.5f };
            AudioServer.AddBusEffect(busIndex, _capture);
        }

        _captureRate = Mathf.RoundToInt(AudioServer.GetMixRate());
        if (_captureRate <= 0) _captureRate = 44100;
        GD.Print($"VoiceManager: capture at {_captureRate} Hz, transmitting at {SampleRate} Hz");
    }

    private void SetupMicrophonePlayer()
    {
        if (_micPlayer != null && GodotObject.IsInstanceValid(_micPlayer)) return;

        _micPlayer = new AudioStreamPlayer
        {
            Name = "MicrophoneCapture",
            Stream = new AudioStreamMicrophone(),
            Bus = "Record",
            Autoplay = false
        };
        AddChild(_micPlayer);
    }

    public void StartRecording()
    {
        RequestMicrophonePermission();
        _recording = true;
        _vadHangoverTimer = 0f;

        // The mix rate can change when the output device changes underneath us.
        int rate = Mathf.RoundToInt(AudioServer.GetMixRate());
        if (rate > 0) _captureRate = rate;

        ResetResampler();
        if (_capture != null) _capture.ClearBuffer();

        if (_micPlayer != null && !_micPlayer.Playing)
        {
            try { _micPlayer.Play(); }
            catch (System.Exception ex) { GD.PrintErr($"VoiceManager: mic play failed: {ex.Message}"); }
        }
    }

    public void StopRecording()
    {
        _recording = false;
        _vadHangoverTimer = 0f;
        IsSpeaking = false;
        InputRms = 0f;

        if (_micPlayer != null && _micPlayer.Playing)
        {
            _micPlayer.Stop();
        }
        ResetResampler();
        if (_capture != null) _capture.ClearBuffer();
    }

    private void ResetResampler()
    {
        _monoIn.Clear();
        _out.Clear();
        _resamplePos = 0;
    }

    public override void _Process(double delta)
    {
        DecaySpeakerVolumes(delta);

        if (!_recording || _capture == null) return;

        // Ensure capture player stays active
        if (_micPlayer != null && !_micPlayer.Playing)
        {
            try { _micPlayer.Play(); } catch { }
        }

        DrainCapture();
        Resample();
        EmitFrames();
    }

    /// Pull everything the capture effect has, downmix to mono, and append at the capture rate.
    private void DrainCapture()
    {
        int available = _capture.GetFramesAvailable();
        if (available <= 0) return;

        var frames = _capture.GetBuffer(available);
        for (int i = 0; i < frames.Length; i++)
            _monoIn.Add((frames[i].X + frames[i].Y) * 0.5f);

        // Don't let a backlog build without bound if something downstream stalls.
        int cap = _captureRate * MaxBacklogSeconds;
        if (_monoIn.Count > cap)
        {
            int drop = _monoIn.Count - cap;
            _monoIn.RemoveRange(0, drop);
            _resamplePos = Math.Max(0, _resamplePos - drop);
        }
    }

    /// Linear-interpolate the capture-rate mono stream down to `SampleRate`. The cursor is kept
    /// fractional across calls; resetting it per call would drop or duplicate a sample each time
    /// and put a periodic click in the stream.
    private void Resample()
    {
        if (_monoIn.Count < 2) return;
        double ratio = (double)_captureRate / SampleRate;

        while (_resamplePos + 1 < _monoIn.Count)
        {
            int i = (int)_resamplePos;
            float frac = (float)(_resamplePos - i);
            float sample = Mathf.Lerp(_monoIn[i], _monoIn[i + 1], frac) * MicGain;
            _out.Add((short)(Mathf.Clamp(sample, -1f, 1f) * 32767));
            _resamplePos += ratio;
        }

        // Retire the fully-consumed prefix, keeping the cursor's fractional part intact.
        int consumed = (int)Math.Floor(_resamplePos);
        if (consumed > 0)
        {
            if (consumed > _monoIn.Count) consumed = _monoIn.Count;
            _monoIn.RemoveRange(0, consumed);
            _resamplePos -= consumed;
        }
    }

    /// Slice the 16 kHz stream into true 20 ms frames, run VAD, and emit.
    private void EmitFrames()
    {
        while (_out.Count >= FrameSamples)
        {
            var pcm = new byte[FrameSamples * 2];
            long sumSquare = 0;

            for (int i = 0; i < FrameSamples; i++)
            {
                short s = _out[i];
                pcm[i * 2] = (byte)(s & 0xFF);
                pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
                sumSquare += (long)s * s;
            }
            _out.RemoveRange(0, FrameSamples);

            float rms = Mathf.Sqrt((float)sumSquare / FrameSamples) / 32767f;
            InputRms = Mathf.Lerp(InputRms, rms, 0.35f);

            UpdateVisemes(pcm);

            // Always-on bypasses the gate entirely — that is the whole point of the mode. The
            // noise floor still adapts so a later switch back to Open starts calibrated.
            if (_mode == MicMode.Always)
            {
                if (rms <= Mathf.Max(MinVadThreshold, _noiseFloor * 2.2f))
                    _noiseFloor = Mathf.Lerp(_noiseFloor, rms, 0.02f);
                IsSpeaking = true;
                VoiceFrameReady?.Invoke(pcm);
                continue;
            }

            // Voice Activity Detection (VAD)
            float threshold = Mathf.Max(MinVadThreshold, _noiseFloor * 2.2f) * VadSensitivity;
            bool gateOpen = _mode != MicMode.PushToTalk || PushToTalkHeld;
            // The hangover tail runs after key-up too: PTT exists to gate what you START
            // sending, and clipping the 250 ms decay the instant the key lifts cut the end
            // off every word said while releasing — audible as chopped final syllables.
            bool tailFlowing = gateOpen || _mode == MicMode.PushToTalk;

            if (rms > threshold && gateOpen)
            {
                _vadHangoverTimer = VadHangoverSeconds;
                IsSpeaking = true;
            }
            else
            {
                if (rms <= threshold)
                    _noiseFloor = Mathf.Lerp(_noiseFloor, rms, 0.02f); // slow adapt to background noise
                if (_vadHangoverTimer > 0f && tailFlowing)
                {
                    _vadHangoverTimer -= FrameMs / 1000f;
                    IsSpeaking = true;
                }
                else
                {
                    _vadHangoverTimer = 0f;
                    IsSpeaking = false;
                }
            }

            if (IsSpeaking)
            {
                VoiceFrameReady?.Invoke(pcm);
            }
        }
    }

    // ── Diagnostic seam ───────────────────────────────────────────────────────────────
    //
    /// Feed synthetic mono audio at `rate` through the real resample + framing path, exactly as
    /// `_Process` would. Used by `--serika-voicetest`; there is no test-only branch inside the
    /// resampler itself, so what the diagnostic measures is what ships.
    public void PumpForDiagnostics(float[] monoAtCaptureRate, int rate)
    {
        _captureRate = rate;
        _monoIn.AddRange(monoAtCaptureRate);
        Resample();
        EmitFrames();
    }

    /// Reset resampler + VAD state between diagnostic phases.
    public void ResetForDiagnostics()
    {
        ResetResampler();
        _noiseFloor = 0.015f;
        _vadHangoverTimer = 0f;
        InputRms = 0f;
        IsSpeaking = false;
    }

    /// Rough formant estimation for viseme vowels from zero-crossing rate.
    private void UpdateVisemes(byte[] pcm)
    {
        int samples = pcm.Length / 2;
        int zeroCrossings = 0;
        for (int i = 1; i < samples; i++)
        {
            short s1 = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            short s0 = (short)(pcm[(i - 1) * 2] | (pcm[(i - 1) * 2 + 1] << 8));
            if ((s1 > 0 && s0 <= 0) || (s1 <= 0 && s0 > 0)) zeroCrossings++;
        }
        float zcr = (float)zeroCrossings / samples;
        float speechVol = Mathf.Clamp(InputRms / 0.12f, 0f, 1f);

        LocalVisemeAa = Mathf.Lerp(LocalVisemeAa, speechVol * (zcr is >= 0.06f and <= 0.22f ? 0.9f : 0.4f), 0.35f);
        LocalVisemeIh = Mathf.Lerp(LocalVisemeIh, speechVol * (zcr > 0.25f ? 0.85f : 0.05f), 0.35f);
        LocalVisemeOu = Mathf.Lerp(LocalVisemeOu, speechVol * (zcr < 0.07f ? 0.85f : 0.05f), 0.35f);
        LocalVisemeEe = Mathf.Lerp(LocalVisemeEe, speechVol * (zcr is >= 0.18f and <= 0.32f ? 0.8f : 0.1f), 0.35f);
        LocalVisemeOh = Mathf.Lerp(LocalVisemeOh, speechVol * (zcr is >= 0.04f and <= 0.14f ? 0.75f : 0.1f), 0.35f);
    }

    // ── Playback ──────────────────────────────────────────────────────────────────────

    private readonly Dictionary<ulong, float> _speakerVolume = new();
    private readonly Dictionary<uint, float> _peerVolume = new();
    /// Per-peer local mute. Purely client-side — the frames still arrive, we just drop them.
    private readonly HashSet<uint> _mutedPeers = new();
    /// Per-peer local gain, 0..2.
    private readonly Dictionary<uint, float> _peerGain = new();

    /// Locally mute/unmute a peer. Client-side only; nothing is told to the server.
    public void SetPeerMuted(uint peerId, bool muted)
    {
        if (muted) _mutedPeers.Add(peerId);
        else _mutedPeers.Remove(peerId);
        if (muted) _peerVolume[peerId] = 0f;
    }

    public bool IsPeerMuted(uint peerId) => _mutedPeers.Contains(peerId);

    /// Per-peer playback gain (1.0 = unchanged).
    public void SetPeerGain(uint peerId, float gain) => _peerGain[peerId] = Mathf.Clamp(gain, 0f, 2f);

    public float GetPeerGain(uint peerId) => _peerGain.GetValueOrDefault(peerId, 1f);

    /// Live loudness of a peer, for the speaking ring on their nametag.
    public float GetPeerVolume(uint peerId) => _peerVolume.GetValueOrDefault(peerId, 0f);

    /// Whether a peer is audibly speaking right now.
    public bool IsPeerSpeaking(uint peerId) => GetPeerVolume(peerId) > 0.01f;

    /// Get current audio volume of a remote speaker node for driving mouth visemes.
    public float GetSpeakerVolume(AudioStreamPlayer3D player)
    {
        if (player == null) return 0f;
        return _speakerVolume.GetValueOrDefault(player.GetInstanceId(), 0f);
    }

    /// Remote loudness decays when frames stop arriving, otherwise a peer who stops talking keeps
    /// a lit speaking-ring and an open mouth at whatever their last frame measured.
    private void DecaySpeakerVolumes(double delta)
    {
        float k = Mathf.Exp(-(float)delta * 6f);
        foreach (var id in new List<ulong>(_speakerVolume.Keys))
        {
            float v = _speakerVolume[id] * k;
            if (v < 0.0005f) _speakerVolume.Remove(id); else _speakerVolume[id] = v;
        }
        foreach (var id in new List<uint>(_peerVolume.Keys))
        {
            float v = _peerVolume[id] * k;
            if (v < 0.0005f) _peerVolume.Remove(id); else _peerVolume[id] = v;
        }
    }

    /// Play back a received voice frame through a spatial audio node. The caller parents the
    /// `AudioStreamPlayer3D` to the remote avatar; this owns its stream and playback handle.
    public void PlayFrame(uint peerId, AudioStreamPlayer3D player, byte[] pcmData)
    {
        if (player == null || pcmData == null || pcmData.Length < 2) return;
        if (_mutedPeers.Contains(peerId)) return;

        var playback = GetOrCreatePlayback(player);
        if (playback == null) return;

        int sampleCount = pcmData.Length / 2;
        int room = playback.GetFramesAvailable();
        int toPush = Mathf.Min(sampleCount, room);
        if (toPush <= 0) return;

        float gain = _peerGain.GetValueOrDefault(peerId, 1f);
        var frames = new Vector2[toPush];
        float sumSquare = 0f;
        for (int i = 0; i < toPush; i++)
        {
            short s = (short)(pcmData[i * 2] | (pcmData[i * 2 + 1] << 8));
            float f = Mathf.Clamp(s / 32767f * gain, -1f, 1f);
            sumSquare += f * f;
            frames[i] = new Vector2(f, f); // mono -> both channels; 3D node handles spatial pan
        }
        float remoteRms = Mathf.Sqrt(sumSquare / toPush);
        _speakerVolume[player.GetInstanceId()] = remoteRms;
        _peerVolume[peerId] = remoteRms;
        playback.PushBuffer(frames);
    }

    /// Ensure the speaker node has a 16 kHz generator stream that's playing, and return its
    /// live playback handle. Keyed by instance id so each remote gets its own stream.
    private AudioStreamGeneratorPlayback GetOrCreatePlayback(AudioStreamPlayer3D player)
    {
        ulong id = player.GetInstanceId();
        if (_playbacks.TryGetValue(id, out var existing) && player.Playing)
            return existing;

        var gen = new AudioStreamGenerator
        {
            MixRate = SampleRate,
            BufferLength = 0.35f, // 350 ms covers UDP jitter without mouth-to-ear lag
        };
        player.Stream = gen;
        player.Play();

        var pb = player.GetStreamPlayback() as AudioStreamGeneratorPlayback;
        if (pb != null) _playbacks[id] = pb;
        return pb;
    }

    /// Drop a speaker's playback handle when its avatar leaves, so the map doesn't leak.
    public void ForgetSpeaker(AudioStreamPlayer3D player)
    {
        if (player != null)
        {
            _playbacks.Remove(player.GetInstanceId());
            _speakerVolume.Remove(player.GetInstanceId());
        }
    }

    /// Drop all per-peer state when a peer leaves the instance.
    public void ForgetPeer(uint peerId)
    {
        _peerVolume.Remove(peerId);
        _peerGain.Remove(peerId);
        _mutedPeers.Remove(peerId);
    }
}
