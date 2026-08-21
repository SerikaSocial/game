using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Serika.Net.Codec;
using Xunit;

namespace Serika.Net.Codec.Tests;

/// The C# half of the cross-language guarantee. These assert the C# codec against the same
/// proto/golden/vectors.json that proto/rust/tests/golden.rs uses. When both suites pass,
/// the client and relay speak the same wire format — byte for byte.
public class GoldenTests
{
    private static JsonElement Corpus()
    {
        // Locate vectors.json relative to this source file, so the test works regardless of
        // the working directory. This file is game/Net/Codec/Tests/GoldenTests.cs; the
        // corpus is game/proto/golden/vectors.json (proto is a submodule).
        string dir = SourceDir();
        string path = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "proto", "golden", "vectors.json"));
        Assert.True(File.Exists(path), $"golden corpus not found at {path} — run `git submodule update --init`");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }

    private static string SourceDir([System.Runtime.CompilerServices.CallerFilePath] string p = "")
        => Path.GetDirectoryName(p)!;

    private static string Hex(byte[] b)
    {
        var sb = new StringBuilder(b.Length * 2);
        foreach (var x in b) sb.Append(x.ToString("x2"));
        return sb.ToString();
    }

    private static Quat ReadQuat(JsonElement a) =>
        new(a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle(), a[3].GetSingle());

    [Fact]
    public void CorpusIsExpectedVersion()
    {
        Assert.Equal(1, Corpus().GetProperty("version").GetInt32());
    }

    [Fact]
    public void PositionQuantizationMatchesCorpus()
    {
        foreach (var c in Corpus().GetProperty("positions").EnumerateArray())
        {
            float input = c.GetProperty("input").GetSingle();
            ushort expected = c.GetProperty("quantized").GetUInt16();
            Assert.Equal(expected, PoseFrame.QuantizePos(input));
        }
    }

    [Fact]
    public void QuaternionPackingMatchesCorpus()
    {
        foreach (var c in Corpus().GetProperty("quaternions").EnumerateArray())
        {
            var input = ReadQuat(c.GetProperty("input"));
            uint expected = c.GetProperty("packed").GetUInt32();
            Assert.Equal(expected, PoseFrame.PackQuat(input));
        }
    }

    [Fact]
    public void FramesEncodeToCorpusBytes()
    {
        foreach (var c in Corpus().GetProperty("frames").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            var lod = (Lod)(byte)c.GetProperty("lod").GetInt32();

            var pos = c.GetProperty("root_pos");
            var hands = c.GetProperty("hands");
            var bones = new List<Quat>();
            foreach (var b in c.GetProperty("bones").EnumerateArray()) bones.Add(ReadQuat(b));

            var frame = new PoseFrame
            {
                Lod = lod,
                Sequence = (byte)c.GetProperty("sequence").GetInt32(),
                RootPos = new[] { pos[0].GetSingle(), pos[1].GetSingle(), pos[2].GetSingle() },
                RootRot = ReadQuat(c.GetProperty("root_rot")),
                Bones = bones,
                Hands = new[]
                {
                    new[] { hands[0][0].GetSingle(), hands[0][1].GetSingle(), hands[0][2].GetSingle() },
                    new[] { hands[1][0].GetSingle(), hands[1][1].GetSingle(), hands[1][2].GetSingle() },
                },
            };

            Assert.Equal(c.GetProperty("encoded_hex").GetString(), Hex(frame.Encode()));
        }
    }

    [Fact]
    public void CorpusFramesDecodeBackAndReEncodeIdentically()
    {
        foreach (var c in Corpus().GetProperty("frames").EnumerateArray())
        {
            string hex = c.GetProperty("encoded_hex").GetString()!;
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);

            var frame = PoseFrame.Decode(bytes);
            Assert.Equal((byte)c.GetProperty("lod").GetInt32(), (byte)frame.Lod);
            Assert.Equal(frame.Lod.BoneCount(), frame.Bones.Count);
            // Re-encoding a decoded frame must be a fixed point.
            Assert.Equal(hex, Hex(frame.Encode()));
        }
    }

    [Fact]
    public void FrameSizesMatchSpecTable()
    {
        Assert.Equal(232, Lod.Full.FrameLen());
        Assert.Equal(100, Lod.Body.FrameLen());
        Assert.Equal(28, Lod.Distant.FrameLen());
    }

    [Fact]
    public void HostileFramesAreRejected()
    {
        var good = new PoseFrame
        {
            Lod = Lod.Body,
            Sequence = 1,
            Bones = new List<Quat>(new Quat[LodExt.Lod1BoneCount]),
        }.Encode();

        Assert.Throws<CodecException>(() => PoseFrame.Decode(good.AsSpan(0, 4)));

        var trailing = new byte[good.Length + 1];
        good.CopyTo(trailing, 0);
        Assert.Throws<CodecException>(() => PoseFrame.Decode(trailing));

        var reserved = (byte[])good.Clone();
        reserved[0] |= 0x80;
        Assert.Throws<CodecException>(() => PoseFrame.Decode(reserved));
    }

    [Fact]
    public void VoiceFrameRoundTrips()
    {
        var f = new VoiceFrame { Sequence = 42, Rms = 200, Payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF } };
        var back = VoiceFrame.Decode(f.Encode());
        Assert.Equal(f.Sequence, back.Sequence);
        Assert.Equal(f.Rms, back.Rms);
        Assert.Equal(f.Payload, back.Payload);
    }
}
