using System;

namespace Serika.Net.Codec;

/// Opus frame framing. Mirrors proto/rust/src/voice.rs. The relay never decodes audio — it
/// reads the header to route/rank and forwards the payload untouched.
public sealed class VoiceFrame
{
    public byte Sequence;
    /// Client-reported loudness for speaker ranking. UNTRUSTED on the server side.
    public byte Rms;
    public byte[] Payload = Array.Empty<byte>();

    private const int Header = 4;

    public byte[] Encode()
    {
        var buf = new byte[Header + Payload.Length];
        buf[0] = Sequence;
        buf[1] = Rms;
        buf[2] = (byte)(Payload.Length & 0xFF);
        buf[3] = (byte)((Payload.Length >> 8) & 0xFF);
        Array.Copy(Payload, 0, buf, Header, Payload.Length);
        return buf;
    }

    public static VoiceFrame Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < Header)
            throw new CodecException($"frame too short: need {Header}, got {buf.Length}");
        int len = buf[2] | (buf[3] << 8);
        int want = Header + len;
        if (buf.Length < want) throw new CodecException($"frame too short: need {want}, got {buf.Length}");
        if (buf.Length > want) throw new CodecException($"trailing bytes: {buf.Length - want}");
        return new VoiceFrame
        {
            Sequence = buf[0],
            Rms = buf[1],
            Payload = buf.Slice(Header, len).ToArray(),
        };
    }
}
