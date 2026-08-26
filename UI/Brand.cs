using Godot;

namespace SerikaSocial;

/// Serika's visual identity, in one place. The brand is **purple** — a deep violet base with a
/// vivid violet primary and lavender accents. `BuildTheme()` returns a Godot Theme that, when set
/// on the root Window, restyles every Control (buttons, labels, panels, inputs, scrollbars,
/// progress bars) at once, so the whole client is on-brand without per-control styling.
///
/// The palette is also exposed as constants for bespoke drawing (loading screen, HUD, chat).
public static class Brand
{
    // ── Palette ──────────────────────────────────────────────────────────────────────
    public static readonly Color Bg0 = Hex(0x0E0A1A);       // deepest background
    public static readonly Color Bg1 = Hex(0x171130);       // panel
    public static readonly Color Bg2 = Hex(0x201743);       // elevated / row
    public static readonly Color Bg3 = Hex(0x2B1F5C);       // hover row
    public static readonly Color Border = new(0.42f, 0.32f, 0.72f, 0.45f);
    public static readonly Color BorderSoft = new(0.42f, 0.32f, 0.72f, 0.22f);

    public static readonly Color Primary = Hex(0x7C3AED);   // violet-600
    public static readonly Color PrimaryHi = Hex(0x8B5CF6); // hover
    public static readonly Color PrimaryLo = Hex(0x6D28D9); // pressed
    public static readonly Color Accent = Hex(0xA78BFA);    // lavender-400
    public static readonly Color AccentSoft = Hex(0xC4B5FD);

    public static readonly Color TextHi = Hex(0xF5F3FF);
    public static readonly Color TextMid = Hex(0xC7BCE6);
    public static readonly Color TextDim = Hex(0x8A7FB0);

    public static readonly Color Success = Hex(0x34D399);
    public static readonly Color Danger = Hex(0xF87171);
    public static readonly Color Warning = Hex(0xFBBF24);

    public static Color Hex(uint rgb) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);

    // ── Style helpers ─────────────────────────────────────────────────────────────────
    public static StyleBoxFlat Panel(Color bg, float radius = 14, float border = 1, Color? borderColor = null)
    {
        var s = new StyleBoxFlat
        {
            BgColor = bg,
            CornerRadiusTopLeft = (int)radius, CornerRadiusTopRight = (int)radius,
            CornerRadiusBottomLeft = (int)radius, CornerRadiusBottomRight = (int)radius,
            BorderWidthTop = (int)border, BorderWidthBottom = (int)border,
            BorderWidthLeft = (int)border, BorderWidthRight = (int)border,
            BorderColor = borderColor ?? Border,
            ShadowColor = new Color(0, 0, 0, 0.35f),
            ShadowSize = 10,
        };
        return s;
    }

    private static StyleBoxFlat Btn(Color bg, Color border, float radius = 10)
    {
        var s = new StyleBoxFlat
        {
            BgColor = bg,
            CornerRadiusTopLeft = (int)radius, CornerRadiusTopRight = (int)radius,
            CornerRadiusBottomLeft = (int)radius, CornerRadiusBottomRight = (int)radius,
            ContentMarginTop = 10, ContentMarginBottom = 10, ContentMarginLeft = 18, ContentMarginRight = 18,
            BorderColor = border, BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
        };
        return s;
    }

    /// Turn a Button into the emphasised, filled violet primary action.
    public static Button Primary_(Button b)
    {
        b.AddThemeStyleboxOverride("normal", Btn(Primary, PrimaryHi));
        b.AddThemeStyleboxOverride("hover", Btn(PrimaryHi, Accent));
        b.AddThemeStyleboxOverride("pressed", Btn(PrimaryLo, PrimaryLo));
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        b.AddThemeColorOverride("font_color", TextHi);
        b.AddThemeColorOverride("font_hover_color", Colors.White);
        b.AddThemeFontSizeOverride("font_size", 15);
        return b;
    }

    /// A quiet, bordered secondary button.
    public static Button Ghost_(Button b)
    {
        b.AddThemeStyleboxOverride("normal", Btn(Bg2, BorderSoft));
        b.AddThemeStyleboxOverride("hover", Btn(Bg3, Border));
        b.AddThemeStyleboxOverride("pressed", Btn(Bg2, Border));
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        b.AddThemeColorOverride("font_color", TextMid);
        b.AddThemeColorOverride("font_hover_color", TextHi);
        b.AddThemeFontSizeOverride("font_size", 15);
        return b;
    }

    // ── The global theme ────────────────────────────────────────────────────────────────
    private static Theme _theme;
    public static Theme Theme => _theme ??= BuildTheme();

    private static Theme BuildTheme()
    {
        var t = new Theme { DefaultFontSize = 15 };

        // Label
        t.SetColor("font_color", "Label", TextMid);

        // Button (base = ghost look; call Brand.Primary_ for the filled action)
        t.SetStylebox("normal", "Button", Btn(Bg2, BorderSoft));
        t.SetStylebox("hover", "Button", Btn(Bg3, Border));
        t.SetStylebox("pressed", "Button", Btn(Bg2, Border));
        t.SetStylebox("focus", "Button", new StyleBoxEmpty());
        t.SetStylebox("disabled", "Button", Btn(Bg1, BorderSoft));
        t.SetColor("font_color", "Button", TextMid);
        t.SetColor("font_hover_color", "Button", TextHi);
        t.SetColor("font_pressed_color", "Button", TextHi);
        t.SetColor("font_disabled_color", "Button", TextDim);
        t.SetFontSize("font_size", "Button", 15);

        // Panel
        t.SetStylebox("panel", "Panel", Panel(Bg1));
        t.SetStylebox("panel", "PanelContainer", Panel(Bg1));

        // LineEdit
        var input = Btn(Bg0, Border, 8);
        var inputFocus = Btn(Bg0, Accent, 8);
        t.SetStylebox("normal", "LineEdit", input);
        t.SetStylebox("focus", "LineEdit", inputFocus);
        t.SetColor("font_color", "LineEdit", TextHi);
        t.SetColor("font_placeholder_color", "LineEdit", TextDim);
        t.SetColor("caret_color", "LineEdit", Accent);
        t.SetColor("selection_color", "LineEdit", new Color(Primary.R, Primary.G, Primary.B, 0.4f));

        // ProgressBar
        t.SetStylebox("background", "ProgressBar", Panel(Bg0, 6, 0));
        t.SetStylebox("fill", "ProgressBar", Panel(Primary, 6, 0));
        t.SetColor("font_color", "ProgressBar", TextMid);

        // ScrollContainer / scrollbars
        t.SetStylebox("panel", "ScrollContainer", new StyleBoxEmpty());
        t.SetStylebox("grabber", "VScrollBar", Panel(Border, 4, 0));
        t.SetStylebox("grabber_highlight", "VScrollBar", Panel(Accent, 4, 0));
        t.SetStylebox("scroll", "VScrollBar", Panel(Bg0, 4, 0));

        return t;
    }

    /// The dimming layer behind a modal screen, anchored to fill its parent.
    ///
    /// On a monitor a scrim darkens the world so the dialog reads as the only live thing. On the
    /// VR panel there is no world behind it to darken — the panel *is* a floating rectangle, so a
    /// 0.7-alpha scrim turns it into a large dark slab hanging in front of the player, obscuring
    /// the room for no benefit. In VR the scrim is therefore kept faint: enough to seat the card
    /// against something, not enough to become an object in its own right.
    public static ColorRect Scrim(float alpha = 0.72f)
    {
        if (UI.VrUiSurface.Active) alpha *= 0.28f;
        var r = new ColorRect { Color = new Color(0.02f, 0.03f, 0.05f, alpha) };
        r.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        return r;
    }

    /// A vertical brand gradient (deep violet → near-black), for full-screen backdrops.
    public static Gradient BackdropGradient()
    {
        var g = new Gradient();
        g.SetColor(0, Hex(0x1A0F3A));
        g.SetColor(1, Bg0);
        return g;
    }
}
