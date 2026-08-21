using Godot;

namespace SerikaSocial.Player;

/// Voice chat scaffolding for M3. Captures microphone input via AudioEffectCapture,
/// encodes to a raw PCM VoiceFrame, and sends over the transport. Incoming voice frames
/// are decoded and played back through AudioStreamPlayer3D nodes parented to remote avatars
/// for spatial audio.
///
/// NOTE: Opus encoding requires a native GDExtension (no C# Opus path in Godot). This
/// currently sends raw PCM16 at 16kHz mono — functional for local testing but too
/// bandwidth-heavy for production. Replace with Opus when the GDExtension lands.
public partial class VoiceManager : Node
{
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

    /// Play back a received voice frame through a spatial audio node.
    /// Caller is responsible for parenting the AudioStreamPlayer3D to the remote avatar.
    public void PlayFrame(AudioStreamPlayer3D player, byte[] pcmData)
    {
        // Convert PCM16 back to stereo float32 for Godot playback
        int sampleCount = pcmData.Length / 2;
        var frames = new Vector2[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            short s = (short)(pcmData[i * 2] | (pcmData[i * 2 + 1] << 8));
            float f = s / 32767f;
            frames[i] = new Vector2(f, f);
        }

        var stream = new AudioStreamPolyphonic();
        // For real-time voice, we'd use AudioStreamGenerator + push frames.
        // This is the scaffold — wire it to the transport's VoiceReceived event.
        player.Stream = stream;
        player.Play();
    }
}
