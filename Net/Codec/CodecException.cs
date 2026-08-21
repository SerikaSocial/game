namespace Serika.Net.Codec;

/// Thrown when a frame off the wire fails to decode. Decoding is a trust boundary: a bad
/// frame is always a rejection, never a fixup. Mirrors Rust's DecodeError.
public sealed class CodecException : System.Exception
{
    public CodecException(string message) : base(message) { }
}
