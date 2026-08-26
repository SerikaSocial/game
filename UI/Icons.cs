using System;
using System.Collections.Generic;
using Godot;

namespace SerikaSocial.UI;

/// Serika's line-art icon set, drawn in code.
///
/// The client used colour emoji as iconography — `🌐 Worlds`, `📷 Camera`, `🧘 Poses`. At the
/// 16–20 px these are rendered at, an emoji font's colour bitmaps turn into muddy multicolour
/// blobs: the camera glyph was genuinely unreadable, and none of them take the brand tint, so
/// the menus read as a pile of unrelated stickers rather than one product. Emoji also render
/// differently on every platform, which for a client shipping on Windows, macOS, Linux and
/// Quest means the UI is never twice the same.
///
/// So: monochrome strokes, defined as polylines in a 0..1 unit box and rasterised to an
/// `ImageTexture` at whatever size and tint the caller wants. That makes them `Button.Icon`
/// material, so the existing buttons keep their layout and just gain a proper icon.
///
/// Rasterising rather than drawing into a `Control` is deliberate: `Button` positions and
/// aligns an `Icon` texture against its text for free, whereas a child `Control` would need
/// every call site to do its own layout maths.
public static class Icons
{
    public enum Kind
    {
        Globe, Shirt, Camera, Screen, Gear, Link, Home, Refresh, Smile, Mic, Power,
        Users, Search, Bell, Calendar, Box, Close, Check, Trash, Doc, Person, Sit,
        Wave, Star, Flame, Gamepad, Flask, Pin, Cart, Bolt, Group, Timer, Trophy,
        Question, Film, Focus, Moon, Snowflake, Music, ThumbUp, ThumbDown,
        Speaker, Display, Image,
    }

    /// Stroke width, as a fraction of the icon box. Tuned so a 20 px icon lands near 2 px,
    /// which is the weight the surrounding text is drawn at.
    private const float Stroke = 0.085f;

    private static readonly Dictionary<(Kind, int, Color), ImageTexture> Cache = new();

    /// A tinted icon texture. Cached — these are rasterised on the CPU, and menu rebuilds
    /// would otherwise redraw the same handful of icons on every open.
    public static ImageTexture Get(Kind kind, int size = 20, Color? tint = null)
    {
        var color = tint ?? Brand.TextMid;
        var key = (kind, size, color);
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var tex = ImageTexture.CreateFromImage(Raster(Paths(kind), size, color));
        Cache[key] = tex;
        return tex;
    }

    // ── Rasteriser ────────────────────────────────────────────────────────────────────

    /// Signed distance to a line segment. Every shape below is a polyline, including the
    /// circles and arcs (they are tessellated), so this is the only primitive needed.
    private static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-9f) return p.DistanceTo(a);
        float t = Mathf.Clamp((p - a).Dot(ab) / len2, 0f, 1f);
        return p.DistanceTo(a + ab * t);
    }

    private static Image Raster(List<Vector2[]> paths, int size, Color color)
    {
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        float half = Stroke * 0.5f;

        // One pixel of feathering, expressed in unit space, gives clean edges at any size
        // without a separate supersampling pass.
        float feather = 1f / size;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                var p = new Vector2((x + 0.5f) / size, (y + 0.5f) / size);

                float d = float.MaxValue;
                foreach (var path in paths)
                    for (int i = 0; i + 1 < path.Length; i++)
                        d = Mathf.Min(d, DistToSegment(p, path[i], path[i + 1]));

                // d is distance to the centreline; subtract half the stroke to get distance
                // to the stroke's edge, then fade across one pixel.
                float alpha = Mathf.Clamp((half - d) / feather, 0f, 1f);
                img.SetPixel(x, y, new Color(color.R, color.G, color.B, color.A * alpha));
            }
        }
        return img;
    }

    // ── Shape helpers (all in a 0..1 box) ─────────────────────────────────────────────

    private static Vector2 V(float x, float y) => new(x, y);

    private static Vector2[] Line(params float[] xy)
    {
        var pts = new Vector2[xy.Length / 2];
        for (int i = 0; i < pts.Length; i++) pts[i] = new Vector2(xy[i * 2], xy[i * 2 + 1]);
        return pts;
    }

    /// An arc from `a0` to `a1` radians, tessellated finely enough that the segments are
    /// invisible at icon sizes.
    private static Vector2[] Arc(float cx, float cy, float rx, float ry, float a0, float a1, int steps = 24)
    {
        var pts = new Vector2[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            float t = a0 + (a1 - a0) * i / steps;
            pts[i] = new Vector2(cx + Mathf.Cos(t) * rx, cy + Mathf.Sin(t) * ry);
        }
        return pts;
    }

    private static Vector2[] Circle(float cx, float cy, float r) => Arc(cx, cy, r, r, 0, Mathf.Tau);

    private static Vector2[] Ellipse(float cx, float cy, float rx, float ry) => Arc(cx, cy, rx, ry, 0, Mathf.Tau);

    /// A closed rectangle, optionally with rounded corners approximated by clipping the
    /// corners — at 20 px a chamfer and a fillet are indistinguishable.
    private static Vector2[] Rect(float x0, float y0, float x1, float y1, float r = 0f)
    {
        if (r <= 0f) return Line(x0, y0, x1, y0, x1, y1, x0, y1, x0, y0);
        return Line(x0 + r, y0, x1 - r, y0, x1, y0 + r, x1, y1 - r, x1 - r, y1,
                    x0 + r, y1, x0, y1 - r, x0, y0 + r, x0 + r, y0);
    }

    // ── The icons ─────────────────────────────────────────────────────────────────────

    private static List<Vector2[]> Paths(Kind k) => k switch
    {
        Kind.Globe => new() {
            Circle(0.5f, 0.5f, 0.38f),
            Ellipse(0.5f, 0.5f, 0.17f, 0.38f),
            Line(0.12f, 0.5f, 0.88f, 0.5f),
        },

        Kind.Shirt => new() {
            // Collar, shoulders, sleeves, then straight down to the hem.
            Line(0.38f, 0.14f, 0.20f, 0.24f, 0.10f, 0.42f, 0.24f, 0.50f, 0.24f, 0.86f,
                 0.76f, 0.86f, 0.76f, 0.50f, 0.90f, 0.42f, 0.80f, 0.24f, 0.62f, 0.14f),
            // Neckline.
            Arc(0.5f, 0.14f, 0.12f, 0.10f, 0f, Mathf.Pi),
        },

        Kind.Camera => new() {
            Rect(0.08f, 0.28f, 0.92f, 0.84f, 0.08f),
            Line(0.34f, 0.28f, 0.42f, 0.16f, 0.58f, 0.16f, 0.66f, 0.28f),
            Circle(0.5f, 0.56f, 0.16f),
        },

        Kind.Screen => new() {
            Rect(0.08f, 0.20f, 0.92f, 0.70f, 0.06f),
            Line(0.36f, 0.84f, 0.64f, 0.84f),
            Line(0.5f, 0.70f, 0.5f, 0.84f),
            Line(0.42f, 0.36f, 0.62f, 0.45f, 0.42f, 0.54f, 0.42f, 0.36f),
        },

        Kind.Gear => new() {
            Circle(0.5f, 0.5f, 0.18f),
            Circle(0.5f, 0.5f, 0.34f),
            Line(0.5f, 0.06f, 0.5f, 0.20f), Line(0.5f, 0.80f, 0.5f, 0.94f),
            Line(0.06f, 0.5f, 0.20f, 0.5f), Line(0.80f, 0.5f, 0.94f, 0.5f),
            Line(0.19f, 0.19f, 0.29f, 0.29f), Line(0.71f, 0.71f, 0.81f, 0.81f),
            Line(0.81f, 0.19f, 0.71f, 0.29f), Line(0.29f, 0.71f, 0.19f, 0.81f),
        },

        Kind.Link => new() {
            // A horizontal chain: two C-shaped half-links facing each other across a bar.
            // Drawing them on a diagonal instead (the obvious first try) reads as a stray
            // "S" at icon size, because the bar between them disappears into the curves.
            Arc(0.34f, 0.5f, 0.22f, 0.24f, Mathf.Pi * 0.38f, Mathf.Pi * 1.62f),
            Arc(0.66f, 0.5f, 0.22f, 0.24f, -Mathf.Pi * 0.62f, Mathf.Pi * 0.62f),
            Line(0.30f, 0.5f, 0.70f, 0.5f),
        },

        Kind.Home => new() {
            Line(0.08f, 0.48f, 0.5f, 0.12f, 0.92f, 0.48f),
            Line(0.20f, 0.42f, 0.20f, 0.88f, 0.80f, 0.88f, 0.80f, 0.42f),
            Rect(0.41f, 0.60f, 0.59f, 0.88f),
        },

        Kind.Refresh => new() {
            Arc(0.5f, 0.5f, 0.34f, 0.34f, Mathf.Pi * 0.35f, Mathf.Pi * 1.85f),
            // Arrowhead at the open end of the arc.
            Line(0.66f, 0.06f, 0.62f, 0.26f, 0.82f, 0.24f),
        },

        Kind.Smile => new() {
            Circle(0.5f, 0.5f, 0.40f),
            Line(0.36f, 0.40f, 0.36f, 0.46f),
            Line(0.64f, 0.40f, 0.64f, 0.46f),
            Arc(0.5f, 0.54f, 0.20f, 0.16f, Mathf.Pi * 0.15f, Mathf.Pi * 0.85f),
        },

        Kind.Mic => new() {
            Rect(0.37f, 0.08f, 0.63f, 0.56f, 0.13f),
            Arc(0.5f, 0.50f, 0.30f, 0.26f, 0f, Mathf.Pi),
            Line(0.5f, 0.76f, 0.5f, 0.92f),
            Line(0.32f, 0.92f, 0.68f, 0.92f),
        },

        Kind.Power => new() {
            Arc(0.5f, 0.56f, 0.34f, 0.34f, -Mathf.Pi * 0.35f, Mathf.Pi * 1.35f),
            Line(0.5f, 0.08f, 0.5f, 0.44f),
        },

        Kind.Users => new() {
            Circle(0.35f, 0.32f, 0.16f),
            Arc(0.35f, 0.86f, 0.30f, 0.26f, Mathf.Pi, Mathf.Tau),
            Arc(0.68f, 0.32f, 0.14f, 0.14f, -Mathf.Pi * 0.5f, Mathf.Pi * 0.5f),
            Arc(0.68f, 0.86f, 0.24f, 0.24f, Mathf.Pi * 1.5f, Mathf.Tau),
        },

        Kind.Search => new() {
            Circle(0.42f, 0.42f, 0.28f),
            Line(0.63f, 0.63f, 0.90f, 0.90f),
        },

        Kind.Bell => new() {
            Line(0.20f, 0.70f, 0.20f, 0.44f),
            Arc(0.5f, 0.44f, 0.30f, 0.30f, Mathf.Pi, Mathf.Tau),
            Line(0.80f, 0.44f, 0.80f, 0.70f),
            Line(0.12f, 0.70f, 0.88f, 0.70f),
            Arc(0.5f, 0.70f, 0.11f, 0.13f, 0f, Mathf.Pi),
            Line(0.5f, 0.08f, 0.5f, 0.15f),
        },

        Kind.Calendar => new() {
            Rect(0.10f, 0.20f, 0.90f, 0.90f, 0.06f),
            Line(0.10f, 0.40f, 0.90f, 0.40f),
            Line(0.30f, 0.08f, 0.30f, 0.28f),
            Line(0.70f, 0.08f, 0.70f, 0.28f),
        },

        Kind.Box => new() {
            Line(0.5f, 0.08f, 0.92f, 0.30f, 0.92f, 0.72f, 0.5f, 0.94f, 0.08f, 0.72f, 0.08f, 0.30f, 0.5f, 0.08f),
            Line(0.08f, 0.30f, 0.5f, 0.52f, 0.92f, 0.30f),
            Line(0.5f, 0.52f, 0.5f, 0.94f),
        },

        Kind.Close => new() {
            Line(0.20f, 0.20f, 0.80f, 0.80f),
            Line(0.80f, 0.20f, 0.20f, 0.80f),
        },

        Kind.Check => new() { Line(0.16f, 0.52f, 0.40f, 0.76f, 0.84f, 0.24f) },

        Kind.Trash => new() {
            Line(0.12f, 0.26f, 0.88f, 0.26f),
            Line(0.38f, 0.26f, 0.38f, 0.14f, 0.62f, 0.14f, 0.62f, 0.26f),
            Line(0.22f, 0.26f, 0.28f, 0.90f, 0.72f, 0.90f, 0.78f, 0.26f),
            Line(0.42f, 0.40f, 0.44f, 0.76f),
            Line(0.58f, 0.40f, 0.56f, 0.76f),
        },

        Kind.Doc => new() {
            Line(0.20f, 0.08f, 0.62f, 0.08f, 0.80f, 0.28f, 0.80f, 0.92f, 0.20f, 0.92f, 0.20f, 0.08f),
            Line(0.62f, 0.08f, 0.62f, 0.28f, 0.80f, 0.28f),
            Line(0.33f, 0.50f, 0.67f, 0.50f),
            Line(0.33f, 0.68f, 0.67f, 0.68f),
        },

        Kind.Person => new() {
            Circle(0.5f, 0.18f, 0.13f),
            Line(0.5f, 0.32f, 0.5f, 0.62f),
            Line(0.22f, 0.44f, 0.78f, 0.44f),
            Line(0.5f, 0.62f, 0.30f, 0.92f),
            Line(0.5f, 0.62f, 0.70f, 0.92f),
        },

        Kind.Sit => new() {
            Circle(0.34f, 0.20f, 0.13f),
            Line(0.34f, 0.34f, 0.34f, 0.62f, 0.72f, 0.62f),
            Line(0.72f, 0.62f, 0.72f, 0.90f),
            Line(0.20f, 0.90f, 0.34f, 0.62f),
        },

        Kind.Wave => new() {
            // An open hand, drawn as one closed outline rather than separate finger strokes —
            // parallel strokes with gaps between them read as a comb, not a hand.
            Line(0.30f, 0.92f, 0.28f, 0.60f),
            Arc(0.335f, 0.60f, 0.055f, 0.07f, Mathf.Pi, Mathf.Tau),
            Line(0.39f, 0.60f, 0.39f, 0.34f),
            Arc(0.445f, 0.34f, 0.055f, 0.07f, Mathf.Pi, Mathf.Tau),
            Line(0.50f, 0.34f, 0.50f, 0.28f),
            Arc(0.555f, 0.28f, 0.055f, 0.07f, Mathf.Pi, Mathf.Tau),
            Line(0.61f, 0.28f, 0.61f, 0.38f),
            Arc(0.665f, 0.38f, 0.055f, 0.07f, Mathf.Pi, Mathf.Tau),
            Line(0.72f, 0.38f, 0.72f, 0.66f),
            // Thumb, then the wrist closes the shape.
            Line(0.72f, 0.66f, 0.78f, 0.78f),
            Line(0.30f, 0.92f, 0.66f, 0.92f, 0.78f, 0.78f),
        },

        Kind.Star => new() {
            Line(0.5f, 0.06f, 0.62f, 0.38f, 0.96f, 0.40f, 0.70f, 0.60f,
                 0.79f, 0.94f, 0.5f, 0.74f, 0.21f, 0.94f, 0.30f, 0.60f,
                 0.04f, 0.40f, 0.38f, 0.38f, 0.5f, 0.06f),
        },

        Kind.Flame => new() {
            // Asymmetric, with a kink on the left. A symmetric teardrop is a water drop —
            // the lean and the notch are the whole difference between fire and rain.
            Line(0.52f, 0.04f, 0.72f, 0.30f, 0.78f, 0.52f),
            Arc(0.5f, 0.62f, 0.28f, 0.32f, 0f, Mathf.Pi),
            Line(0.22f, 0.60f, 0.30f, 0.40f, 0.44f, 0.34f, 0.40f, 0.18f, 0.52f, 0.04f),
            // Inner core flame.
            Arc(0.52f, 0.70f, 0.13f, 0.15f, Mathf.Pi, Mathf.Tau),
            Line(0.39f, 0.70f, 0.52f, 0.46f, 0.65f, 0.70f),
        },

        Kind.Gamepad => new() {
            Rect(0.06f, 0.32f, 0.94f, 0.76f, 0.16f),
            Line(0.20f, 0.54f, 0.36f, 0.54f),
            Line(0.28f, 0.46f, 0.28f, 0.62f),
            Circle(0.68f, 0.48f, 0.06f),
            Circle(0.80f, 0.60f, 0.06f),
        },

        Kind.Flask => new() {
            Line(0.36f, 0.08f, 0.36f, 0.40f, 0.14f, 0.84f),
            Line(0.64f, 0.08f, 0.64f, 0.40f, 0.86f, 0.84f),
            Arc(0.5f, 0.84f, 0.36f, 0.12f, 0f, Mathf.Pi),
            Line(0.30f, 0.08f, 0.70f, 0.08f),
            Line(0.26f, 0.62f, 0.74f, 0.62f),
        },

        Kind.Pin => new() {
            Arc(0.5f, 0.38f, 0.30f, 0.30f, Mathf.Pi * 0.85f, Mathf.Pi * 2.15f),
            Line(0.27f, 0.57f, 0.5f, 0.94f, 0.73f, 0.57f),
            Circle(0.5f, 0.36f, 0.11f),
        },

        Kind.Cart => new() {
            Line(0.04f, 0.14f, 0.20f, 0.14f, 0.32f, 0.66f, 0.82f, 0.66f, 0.92f, 0.30f, 0.24f, 0.30f),
            Circle(0.38f, 0.84f, 0.09f),
            Circle(0.76f, 0.84f, 0.09f),
        },

        Kind.Bolt => new() {
            Line(0.58f, 0.06f, 0.24f, 0.54f, 0.48f, 0.54f, 0.42f, 0.94f, 0.76f, 0.44f, 0.52f, 0.44f, 0.58f, 0.06f),
        },

        Kind.Group => new() {
            Circle(0.5f, 0.22f, 0.13f),
            Circle(0.20f, 0.62f, 0.13f),
            Circle(0.80f, 0.62f, 0.13f),
            Line(0.40f, 0.33f, 0.29f, 0.51f),
            Line(0.60f, 0.33f, 0.71f, 0.51f),
            Line(0.33f, 0.66f, 0.67f, 0.66f),
        },

        Kind.Timer => new() {
            Circle(0.5f, 0.56f, 0.34f),
            Line(0.5f, 0.36f, 0.5f, 0.56f, 0.66f, 0.62f),
            Line(0.36f, 0.08f, 0.64f, 0.08f),
            Line(0.5f, 0.08f, 0.5f, 0.22f),
        },

        Kind.Trophy => new() {
            Line(0.26f, 0.10f, 0.74f, 0.10f, 0.72f, 0.42f),
            Arc(0.5f, 0.42f, 0.22f, 0.22f, 0f, Mathf.Pi),
            Line(0.28f, 0.42f, 0.26f, 0.10f),
            Arc(0.20f, 0.24f, 0.12f, 0.12f, Mathf.Pi * 0.5f, Mathf.Pi * 1.5f),
            Arc(0.80f, 0.24f, 0.12f, 0.12f, Mathf.Pi * 1.5f, Mathf.Pi * 2.5f),
            Line(0.5f, 0.64f, 0.5f, 0.80f),
            Line(0.30f, 0.90f, 0.70f, 0.90f),
            Line(0.36f, 0.80f, 0.64f, 0.80f),
        },

        Kind.Question => new() {
            Arc(0.5f, 0.30f, 0.20f, 0.20f, Mathf.Pi, Mathf.Tau + 0.5f),
            Line(0.5f, 0.50f, 0.5f, 0.66f),
            Circle(0.5f, 0.86f, 0.035f),
        },

        Kind.Film => new() {
            Rect(0.06f, 0.22f, 0.94f, 0.78f, 0.05f),
            Line(0.26f, 0.22f, 0.26f, 0.78f),
            Line(0.74f, 0.22f, 0.74f, 0.78f),
            Line(0.06f, 0.50f, 0.94f, 0.50f),
        },

        Kind.Focus => new() {
            Circle(0.5f, 0.5f, 0.20f),
            Line(0.5f, 0.04f, 0.5f, 0.20f), Line(0.5f, 0.80f, 0.5f, 0.96f),
            Line(0.04f, 0.5f, 0.20f, 0.5f), Line(0.80f, 0.5f, 0.96f, 0.5f),
        },

        Kind.Moon => new() {
            // Crescent: the outer disc, then an inner arc biting into it.
            Arc(0.5f, 0.5f, 0.38f, 0.38f, Mathf.Pi * 0.30f, Mathf.Pi * 1.70f),
            Arc(0.30f, 0.5f, 0.34f, 0.34f, -Mathf.Pi * 0.42f, Mathf.Pi * 0.42f),
        },

        Kind.Snowflake => new() {
            Line(0.5f, 0.06f, 0.5f, 0.94f),
            Line(0.12f, 0.28f, 0.88f, 0.72f),
            Line(0.12f, 0.72f, 0.88f, 0.28f),
            Line(0.36f, 0.18f, 0.5f, 0.28f, 0.64f, 0.18f),
            Line(0.36f, 0.82f, 0.5f, 0.72f, 0.64f, 0.82f),
        },

        Kind.Music => new() {
            Line(0.38f, 0.78f, 0.38f, 0.16f, 0.82f, 0.08f, 0.82f, 0.66f),
            Ellipse(0.27f, 0.78f, 0.13f, 0.11f),
            Ellipse(0.71f, 0.66f, 0.13f, 0.11f),
        },

        Kind.ThumbUp => new() {
            Rect(0.06f, 0.44f, 0.28f, 0.92f, 0.04f),
            Line(0.36f, 0.92f, 0.36f, 0.44f, 0.50f, 0.16f, 0.56f, 0.08f,
                 0.66f, 0.12f, 0.62f, 0.40f, 0.88f, 0.40f, 0.84f, 0.92f, 0.36f, 0.92f),
        },

        Kind.ThumbDown => new() {
            Rect(0.06f, 0.08f, 0.28f, 0.56f, 0.04f),
            Line(0.36f, 0.08f, 0.36f, 0.56f, 0.50f, 0.84f, 0.56f, 0.92f,
                 0.66f, 0.88f, 0.62f, 0.60f, 0.88f, 0.60f, 0.84f, 0.08f, 0.36f, 0.08f),
        },

        Kind.Speaker => new() {
            Line(0.10f, 0.36f, 0.26f, 0.36f, 0.46f, 0.16f, 0.46f, 0.84f, 0.26f, 0.64f, 0.10f, 0.64f, 0.10f, 0.36f),
            Arc(0.46f, 0.5f, 0.18f, 0.20f, -Mathf.Pi * 0.45f, Mathf.Pi * 0.45f),
            Arc(0.46f, 0.5f, 0.32f, 0.36f, -Mathf.Pi * 0.42f, Mathf.Pi * 0.42f),
        },

        Kind.Display => new() {
            Rect(0.08f, 0.16f, 0.92f, 0.68f, 0.06f),
            Line(0.32f, 0.90f, 0.68f, 0.90f),
            Line(0.5f, 0.68f, 0.5f, 0.90f),
        },

        Kind.Image => new() {
            Rect(0.08f, 0.18f, 0.92f, 0.82f, 0.06f),
            Circle(0.32f, 0.36f, 0.07f),
            // The classic "mountains inside a frame" — two peaks meeting the frame's floor.
            Line(0.08f, 0.72f, 0.36f, 0.46f, 0.55f, 0.64f, 0.68f, 0.52f, 0.92f, 0.74f),
        },

        _ => new() { Circle(0.5f, 0.5f, 0.36f) },
    };
}
