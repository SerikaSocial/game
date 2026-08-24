using System.Collections.Generic;
using Godot;

namespace SerikaSocial.Player;

/// Spatial voice chat. Captures the mic via AudioEffectCapture, ships raw PCM16 frames over
/// the transport, and plays received frames back through an AudioStreamGenerator on each
/// remote avatar so voice comes from where the speaker is standing.
///
/// **Playback used to be a no-op.** `PlayFrame` created an empty `AudioStreamPolyphonic` and
/// called Play() without ever pushing samples — so remote voice was received but silent. It now
/// feeds an `AudioStreamGenerator` per speaker via `PushBuffer`, which is the real-time path.
///
/// NOTE: still raw PCM16 @ 16 kHz mono — audible and correct, just bandwidth-heavy. Opus is a
/// wire-size optimization that needs a native GDExtension (no C# Opus path in Godot); the
/// playback path here does not change when it lands, only the encode/decode around it.
public partial class VoiceManager : Node
{
    /// One generator playback per speaker node, created lazily the first time we hear them.
    private readonly Dictionary<ulong, AudioStreamGeneratorPlayback> _playbacks = new();
    /// Called when we have a voice frame ready to send over the wire.
    public event System.Action<byte[]> VoiceFrameReady;

    private AudioEffectCapture _capture;
    private AudioStreamPlayer3D _playback;
    private bool _recording;
    private int _captureIndex;
    private const int SampleRate = 16000;
    private const int FrameMs = 20; // 20ms frames
    private const int FrameSamples = SampleRate * FrameMs / 1000; // 320 samples

    public override void _Ready()
    {
        // Set up mic capture bus
        int busIndex = AudioServer.GetBusIndex("Record");
        if (busIndex < 0)
        {
            busIndex = AudioServer.BusCount;
            AudioServer.AddBus(busIndex);
            AudioServer.SetBusName(busIndex, "Record");
        }
        _capture = new AudioEffectCapture();
        AudioServer.AddBusEffect(busIndex, _capture);
        AudioServer.SetBusMute(busIndex, false); // muted so we don't hear ourselves
    }

    public void StartRecording()
    {
        _recording = true;
        _capture.ClearBuffer();
    }

    public void StopRecording()
    {
        _recording = false;
    }

    public override void _Process(double delta)
    {
        if (!_recording || _capture == null) return;

        // Grab available samples in chunks
        int available = _capture.GetFramesAvailable();
        if (available < FrameSamples) return;

        var frames = _capture.GetBuffer(FrameSamples);
        if (frames.Length < FrameSamples) return;

        // Convert stereo float32 to mono PCM16
        var pcm = new byte[FrameSamples * 2];
        for (int i = 0; i < FrameSamples; i++)
        {
            float sample = frames[i].X; // left channel
            short s = (short)(Mathf.Clamp(sample, -1f, 1f) * 32767);
            pcm[i * 2] = (byte)(s & 0xFF);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        VoiceFrameReady?.Invoke(pcm);
    }

    /// Play back a received voice frame through a spatial audio node. The caller parents the
    /// `AudioStreamPlayer3D` to the remote avatar; this owns its stream and playback handle.
    public void PlayFrame(AudioStreamPlayer3D player, byte[] pcmData)
    {
        if (player == null || pcmData == null || pcmData.Length < 2) return;

        var playback = GetOrCreatePlayback(player);
        if (playback == null) return;

        int sampleCount = pcmData.Length / 2;
        // Only push what the generator can take; dropping a little tail is better than blocking
        // or overflowing the ring buffer, which would crackle.
        int room = playback.GetFramesAvailable();
        int toPush = Mathf.Min(sampleCount, room);
        if (toPush <= 0) return;

        var frames = new Vector2[toPush];
        for (int i = 0; i < toPush; i++)
        {
            short s = (short)(pcmData[i * 2] | (pcmData[i * 2 + 1] << 8));
            float f = s / 32767f;
            frames[i] = new Vector2(f, f); // mono → both channels; 3D node handles spatial pan
        }
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
            BufferLength = 0.25f, // 250 ms — covers jitter without adding much mouth-to-ear lag
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
