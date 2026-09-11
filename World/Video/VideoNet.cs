using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace SerikaSocial.World.Video;

/// Compact control messages for the instance video queue, riding the existing Chat
/// datagram (400-byte UTF-8 cap, whole-instance fan-out, no proto change).
///
/// A leading U+0001 keeps these out of the chat overlay. Old clients that do not
/// recognise the prefix will still not print a normal line — the control char is
/// invisible — and new clients consume the payload without displaying it.
internal static class VideoNet
{
    public const string Prefix = "\u0001SV";
    public const int MaxBytes = 400;

    public enum Op { Enqueue, Skip, Clear, Play, Want, State }

    public sealed class Msg
    {
        public Op Kind;
        public string Url;
        public string By;
        public long StartedUnixMs;
        public string[] Queue;
    }

    public static bool IsNet(string text) =>
        !string.IsNullOrEmpty(text) && text.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Enqueue(string url, string by) =>
        Pack("q", url, by, 0, null);

    public static string Skip() => Pack("s", null, null, 0, null);

    public static string Clear() => Pack("c", null, null, 0, null);

    public static string Play(string url, long startedUnixMs) =>
        Pack("p", url, null, startedUnixMs, null);

    public static string Want() => Pack("?", null, null, 0, null);

    public static string State(string nowUrl, long startedUnixMs, IReadOnlyList<string> queue)
    {
        // Fit as many upcoming URLs as the 400-byte cap allows.
        var kept = new List<string>();
        if (queue != null)
        {
            foreach (var u in queue)
            {
                if (string.IsNullOrEmpty(u)) continue;
                kept.Add(u);
                if (Utf8Len(Pack("st", nowUrl, null, startedUnixMs, kept.ToArray())) > MaxBytes - 8)
                {
                    kept.RemoveAt(kept.Count - 1);
                    break;
                }
            }
        }
        return Pack("st", nowUrl, null, startedUnixMs, kept.Count > 0 ? kept.ToArray() : null);
    }

    public static bool TryParse(string text, out Msg msg)
    {
        msg = null;
        if (!IsNet(text) || text.Length <= Prefix.Length) return false;
        try
        {
            using var doc = JsonDocument.Parse(text.Substring(Prefix.Length));
            var root = doc.RootElement;
            if (!root.TryGetProperty("op", out var opEl) || opEl.ValueKind != JsonValueKind.String)
                return false;
            string op = opEl.GetString();
            var parsed = new Msg
            {
                Url = Str(root, "u"),
                By = Str(root, "by"),
                StartedUnixMs = root.TryGetProperty("t", out var tEl) && tEl.TryGetInt64(out long t) ? t : 0,
            };
            parsed.Kind = op switch
            {
                "q" => Op.Enqueue,
                "s" => Op.Skip,
                "c" => Op.Clear,
                "p" => Op.Play,
                "?" => Op.Want,
                "st" => Op.State,
                _ => (Op)(-1),
            };
            if ((int)parsed.Kind < 0) return false;
            if (root.TryGetProperty("q", out var qEl) && qEl.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var e in qEl.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String) list.Add(e.GetString());
                parsed.Queue = list.ToArray();
            }
            msg = parsed;
            return true;
        }
        catch { return false; }
    }

    private static string Pack(string op, string url, string by, long t, string[] queue)
    {
        var sb = new StringBuilder(Prefix, 256);
        sb.Append("{\"op\":");
        sb.Append(AotJson.JStr(op));
        if (!string.IsNullOrEmpty(url))
        {
            sb.Append(",\"u\":");
            sb.Append(AotJson.JStr(url));
        }
        if (!string.IsNullOrEmpty(by))
        {
            sb.Append(",\"by\":");
            sb.Append(AotJson.JStr(by));
        }
        if (t > 0)
        {
            sb.Append(",\"t\":");
            sb.Append(t);
        }
        if (queue != null && queue.Length > 0)
        {
            sb.Append(",\"q\":[");
            for (int i = 0; i < queue.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(AotJson.JStr(queue[i]));
            }
            sb.Append(']');
        }
        sb.Append('}');
        string s = sb.ToString();
        if (Utf8Len(s) > MaxBytes)
        {
            // Last-ditch: drop the URL rather than send a truncated JSON that cannot parse.
            if (url != null && url.Length > 80)
                return Pack(op, url[..80], by, t, null);
        }
        return s;
    }

    private static string Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static int Utf8Len(string s) => Encoding.UTF8.GetByteCount(s);
}
