using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SerikaSocial.Avatar;

/// Parsed metadata header of a `.ska` (SerikaAvatar) file. See tools/ska/ska_format.md.
public sealed class SkaMeta
{
    [JsonPropertyName("name")] public string Name { get; set; } = "Avatar";
    [JsonPropertyName("author")] public string Author { get; set; } = "unknown";
    [JsonPropertyName("sourceFormat")] public string SourceFormat { get; set; } = "glb";
    [JsonPropertyName("faceYawDegrees")] public float FaceYawDegrees { get; set; }
    [JsonPropertyName("heightMeters")] public float HeightMeters { get; set; } = 1.7f;
    [JsonPropertyName("eyeHeightMeters")] public float EyeHeightMeters { get; set; } = 1.6f;
    [JsonPropertyName("humanoid")] public Dictionary<string, string> Humanoid { get; set; } = new();
}

/// A loaded `.ska` container: the humanoid metadata plus the raw GLB payload bytes.
///
/// The container is deliberately trivial to parse (fixed header, JSON meta, GLB blob) so the
/// same format works in the Godot client, the Bun API, and the Rust asset validator without a
/// shared codegen step. The GLB is a normal glTF binary — for VRM sources it is the original
/// VRM bytes, which Godot's GltfDocument imports directly.
public sealed class SkaFile
{
    private static readonly byte[] Magic = { (byte)'S', (byte)'K', (byte)'A', (byte)'1' };

    public SkaMeta Meta { get; }
    public byte[] Glb { get; }

    private SkaFile(SkaMeta meta, byte[] glb)
    {
        Meta = meta;
        Glb = glb;
    }

    public static SkaFile Parse(byte[] data)
    {
        if (data.Length < 16 || data[0] != Magic[0] || data[1] != Magic[1] ||
            data[2] != Magic[2] || data[3] != Magic[3])
            throw new FormatException("not a .ska file (bad magic)");

        int off = 4;
        uint version = ReadU32(data, ref off);
        if (version != 1)
            throw new FormatException($"unsupported .ska version {version}");

        uint metaLen = ReadU32(data, ref off);
        if (off + metaLen > data.Length)
            throw new FormatException(".ska meta length overflows file");
        string json = Encoding.UTF8.GetString(data, off, (int)metaLen);
        off += (int)metaLen;

        uint glbLen = ReadU32(data, ref off);
        if (off + glbLen > data.Length)
            throw new FormatException(".ska glb length overflows file");
        var glb = new byte[glbLen];
        Array.Copy(data, off, glb, 0, (int)glbLen);

        var meta = JsonSerializer.Deserialize<SkaMeta>(json) ?? new SkaMeta();
        return new SkaFile(meta, glb);
    }

    private static uint ReadU32(byte[] d, ref int off)
    {
        uint v = (uint)(d[off] | (d[off + 1] << 8) | (d[off + 2] << 16) | (d[off + 3] << 24));
        off += 4;
        return v;
    }
}
