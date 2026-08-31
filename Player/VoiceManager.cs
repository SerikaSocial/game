using System.Collections.Generic;
using Godot;

namespace SerikaSocial.Player;

/// Spatial voice chat. Captures the mic via AudioStreamMicrophone + AudioEffectCapture on the
/// "Record" bus, applies Voice Activity Detection (VAD) / noise gating with hangover, ships
/// PCM16 frames over the transport, and plays received frames back through an AudioStreamGenerator
/// positioned at each remote avatar's mouth.
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

    private AudioEffectCapture _capture;
    private AudioStreamPlayer _micPlayer;
    private bool _recording;
    private float _vadHangoverTimer;
    private float _noiseFloor = 0.015f;

    private const int SampleRate = 16000;
    private const int FrameMs = 20; // 20ms frames
    private const int FrameSamples = SampleRate * FrameMs / 1000; // 320 samples
    private const float VadHangoverSeconds = 0.25f; // 250ms hangover keeps syllable ends from cutting
    private const float MinVadThreshold = 0.025f;

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

    private void SetupCaptureBus()
    {
        int busIndex = AudioServer.GetBusIndex("Record");
        if (busIndex < 0)
        {
            busIndex = AudioServer.BusCount;
            AudioServer.AddBus(busIndex);
            AudioServer.SetBusName(busIndex, "Record");
        }

        // Mute the Record bus in the audio graph so the speaker doesn't hear their own raw voice
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
        if (_capture != null) _capture.ClearBuffer();
    }

    public override void _Process(double delta)
    {
        if (!_recording || _capture == null) return;

        // Ensure capture player stays active
        if (_micPlayer != null && !_micPlayer.Playing)
        {
            try { _micPlayer.Play(); } catch { }
        }

        int available = _capture.GetFramesAvailable();
        if (available < FrameSamples) return;

        // Process in fixed 20ms chunks
        while (_capture.GetFramesAvailable() >= FrameSamples)
        {
            var frames = _capture.GetBuffer(FrameSamples);
            if (frames.Length < FrameSamples) break;

            // Convert stereo float32 to mono PCM16 and measure RMS
            var pcm = new byte[FrameSamples * 2];
            long sumSquare = 0;

            for (int i = 0; i < FrameSamples; i++)
            {
                float sample = (frames[i].X + frames[i].Y) * 0.5f; // mix L+R
                short s = (short)(Mathf.Clamp(sample, -1f, 1f) * 32767);
                pcm[i * 2] = (byte)(s & 0xFF);
                pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
                sumSquare += (long)s * s;
            }

            float rms = Mathf.Sqrt((float)sumSquare / FrameSamples) / 32767f;
            InputRms = Mathf.Lerp(InputRms, rms, 0.35f);

            // Rough formant estimation for viseme vowels from zero-crossing rate and slope
            int zeroCrossings = 0;
            for (int i = 1; i < FrameSamples; i++)
            {
                short s1 = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
                short s0 = (short)(pcm[(i - 1) * 2] | (pcm[(i - 1) * 2 + 1] << 8));
                if ((s1 > 0 && s0 <= 0) || (s1 <= 0 && s0 > 0)) zeroCrossings++;
            }
            float zcr = (float)zeroCrossings / FrameSamples;
            float speechVol = Mathf.Clamp(InputRms / 0.12f, 0f, 1f);

            LocalVisemeAa = Mathf.Lerp(LocalVisemeAa, speechVol * (zcr is >= 0.06f and <= 0.22f ? 0.9f : 0.4f), 0.35f);
            LocalVisemeIh = Mathf.Lerp(LocalVisemeIh, speechVol * (zcr > 0.25f ? 0.85f : 0.05f), 0.35f);
            LocalVisemeOu = Mathf.Lerp(LocalVisemeOu, speechVol * (zcr < 0.07f ? 0.85f : 0.05f), 0.35f);
            LocalVisemeEe = Mathf.Lerp(LocalVisemeEe, speechVol * (zcr is >= 0.18f and <= 0.32f ? 0.8f : 0.1f), 0.35f);
            LocalVisemeOh = Mathf.Lerp(LocalVisemeOh, speechVol * (zcr is >= 0.04f and <= 0.14f ? 0.75f : 0.1f), 0.35f);

            // Voice Activity Detection (VAD)
            float threshold = Mathf.Max(MinVadThreshold, _noiseFloor * 2.2f);
            if (rms > threshold)
            {
                _vadHangoverTimer = VadHangoverSeconds;
                IsSpeaking = true;
            }
            else
            {
                _noiseFloor = Mathf.Lerp(_noiseFloor, rms, 0.02f); // slow adapt to background noise
                if (_vadHangoverTimer > 0f)
                {
                    _vadHangoverTimer -= FrameMs / 1000f;
                    IsSpeaking = true;
                }
                else
                {
                    IsSpeaking = false;
                }
            }

            if (IsSpeaking)
            {
                VoiceFrameReady?.Invoke(pcm);
            }
        }
    }

    private readonly Dictionary<ulong, float> _speakerVolume = new();

    /// Get current audio volume of a remote speaker node for driving mouth visemes.
    public float GetSpeakerVolume(AudioStreamPlayer3D player)
    {
        if (player == null) return 0f;
        return _speakerVolume.GetValueOrDefault(player.GetInstanceId(), 0f);
    }

    /// Play back a received voice frame through a spatial audio node. The caller parents the
    /// `AudioStreamPlayer3D` to the remote avatar; this owns its stream and playback handle.
    public void PlayFrame(AudioStreamPlayer3D player, byte[] pcmData)
    {
        if (player == null || pcmData == null || pcmData.Length < 2) return;

        var playback = GetOrCreatePlayback(player);
        if (playback == null) return;

        int sampleCount = pcmData.Length / 2;
        int room = playback.GetFramesAvailable();
        int toPush = Mathf.Min(sampleCount, room);
        if (toPush <= 0) return;

        var frames = new Vector2[toPush];
        float sumSquare = 0f;
        for (int i = 0; i < toPush; i++)
        {
            short s = (short)(pcmData[i * 2] | (pcmData[i * 2 + 1] << 8));
            float f = s / 32767f;
            sumSquare += f * f;
            frames[i] = new Vector2(f, f); // mono -> both channels; 3D node handles spatial pan
        }
        float remoteRms = Mathf.Sqrt(sumSquare / toPush);
        _speakerVolume[player.GetInstanceId()] = remoteRms;
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
        if (player != null) _playbacks.Remove(player.GetInstanceId());
    }
}
