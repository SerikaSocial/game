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
    [JsonPropertyName("faceYawDegrees")] public float FaceYawDegrees { get; set; } = 0f;
    [JsonPropertyName("heightMeters")] public float HeightMeters { get; set; } = 1.7f;
    [JsonPropertyName("eyeHeightMeters")] public float EyeHeightMeters { get; set; } = 1.6f;
    [JsonPropertyName("humanoid")] public Dictionary<string, string> Humanoid { get; set; } = new();

    // ── v2 extended fields (VRC avatar imports) ──

    /// PhysBone spring bone chains. Each entry describes a bone root and its physics params.
    [JsonPropertyName("physBones")]
    public List<PhysBoneMeta> PhysBones { get; set; } = new();

    /// PhysBone colliders — sphere/capsule shapes that interact with PhysBones.
    [JsonPropertyName("physBoneColliders")]
    public List<PhysBoneColliderMeta> PhysBoneColliders { get; set; } = new();

    /// Avatar toggles — named on/off switches for mesh groups.
    [JsonPropertyName("toggles")]
    public List<ToggleMeta> Toggles { get; set; } = new();

    /// Material names from the source (for shader mapping).
    [JsonPropertyName("materials")]
    public List<MaterialMeta> Materials { get; set; } = new();
}

public sealed class PhysBoneMeta
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("rootTransform")] public string RootTransform { get; set; } = "";
    [JsonPropertyName("stiffness")] public float Stiffness { get; set; } = 0f;
    [JsonPropertyName("gravity")] public float Gravity { get; set; } = 0f;
    [JsonPropertyName("force")] public float Force { get; set; } = 0f;
    [JsonPropertyName("pull")] public float Pull { get; set; } = 0f;
    [JsonPropertyName("spring")] public float Spring { get; set; } = 0f;
    [JsonPropertyName("damping")] public float Damping { get; set; } = 0f;
    [JsonPropertyName("maxStretch")] public float MaxStretch { get; set; } = 0f;
    [JsonPropertyName("isGrabbable")] public bool IsGrabbable { get; set; } = false;
    [JsonPropertyName("isPosable")] public bool IsPosable { get; set; } = false;
    [JsonPropertyName("allowCollision")] public bool AllowCollision { get; set; } = true;
}

public sealed class PhysBoneColliderMeta
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("rootTransform")] public string RootTransform { get; set; } = "";
    [JsonPropertyName("radius")] public float Radius { get; set; } = 0f;
    [JsonPropertyName("height")] public float Height { get; set; } = 0f;
    [JsonPropertyName("shapeType")] public int ShapeType { get; set; } = 0;
}

public sealed class ToggleMeta
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("defaultOn")] public bool DefaultOn { get; set; } = true;
    [JsonPropertyName("saved")] public bool Saved { get; set; } = false;
}

public sealed class MaterialMeta
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("shader")] public string Shader { get; set; } = "";
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
        if (version != 1 && version != 2)
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
