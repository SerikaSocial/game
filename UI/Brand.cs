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
    public static readonly Color Bg0 = Hex(0x101019);       // deepest background
    public static readonly Color Bg1 = Hex(0x191723);       // panel
    public static readonly Color Bg2 = Hex(0x24212F);       // elevated / row
    public static readonly Color Bg3 = Hex(0x302B40);       // hover row
    public static readonly Color Border = new(0.42f, 0.32f, 0.72f, 0.45f);
    public static readonly Color BorderSoft = new(0.42f, 0.32f, 0.72f, 0.22f);

    public static readonly Color Primary = Hex(0x7C3AED);   // violet-600
    public static readonly Color PrimaryHi = Hex(0x8B5CF6); // hover
    public static readonly Color PrimaryLo = Hex(0x6D28D9); // pressed
    public static readonly Color Accent = Hex(0xA78BFA);    // lavender-400
    public static readonly Color AccentSoft = Hex(0xC4B5FD);

    public static readonly Color TextHi = Hex(0xF5F3FF);
    public static readonly Color TextMid = Hex(0xD0CADF);
    public static readonly Color TextDim = Hex(0xAAA1BD);

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

    /// Visible keyboard and controller focus, drawn over a control's normal appearance.
    public static StyleBoxFlat FocusRing(float radius = 10)
    {
        var focus = Panel(Colors.Transparent, radius, 2, AccentSoft);
        focus.DrawCenter = false;
        focus.ShadowSize = 0;
        return focus;
    }

    /// Give a new menu a card size that fits its actual viewport, including desktop windows.
    public static Vector2 FitCard(Viewport viewport, float width, float height)
    {
        var available = viewport.GetVisibleRect().Size - new Vector2(40, 40);
        var preferred = Card(width, height);
        return new Vector2(Mathf.Min(preferred.X, Mathf.Max(280, available.X)),
            Mathf.Min(preferred.Y, Mathf.Max(260, available.Y)));
    }

    /// Turn a Button into the emphasised, filled violet primary action.
    public static Button Primary_(Button b)
    {
        b.AddThemeStyleboxOverride("normal", Btn(Primary, PrimaryHi));
        b.AddThemeStyleboxOverride("hover", Btn(PrimaryHi, Accent));
        b.AddThemeStyleboxOverride("pressed", Btn(PrimaryLo, PrimaryLo));
        b.AddThemeStyleboxOverride("focus", FocusRing());
        b.AddThemeColorOverride("font_color", TextHi);
        b.AddThemeColorOverride("font_hover_color", Colors.White);
        b.AddThemeFontSizeOverride("font_size", Brand.Fs(15));
        return b;
    }

    /// A quiet, bordered secondary button.
    public static Button Ghost_(Button b)
    {
        b.AddThemeStyleboxOverride("normal", Btn(Bg2, BorderSoft));
        b.AddThemeStyleboxOverride("hover", Btn(Bg3, Border));
        b.AddThemeStyleboxOverride("pressed", Btn(Bg2, Border));
        b.AddThemeStyleboxOverride("focus", FocusRing());
        b.AddThemeColorOverride("font_color", TextMid);
        b.AddThemeColorOverride("font_hover_color", TextHi);
        b.AddThemeFontSizeOverride("font_size", Brand.Fs(15));
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
        t.SetColor("font_color", "RichTextLabel", TextMid);
        var separator = new StyleBoxLine { Color = BorderSoft, Thickness = 1 };
        t.SetStylebox("separator", "HSeparator", separator);
        t.SetConstant("separation", "HSeparator", 10);

        // Button (base = ghost look; call Brand.Primary_ for the filled action)
        t.SetStylebox("normal", "Button", Btn(Bg2, BorderSoft));
        t.SetStylebox("hover", "Button", Btn(Bg3, Border));
        t.SetStylebox("pressed", "Button", Btn(Bg2, Border));
        t.SetStylebox("focus", "Button", FocusRing());
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
        t.SetStylebox("grabber", "VScrollBar", ScrollBarStyle(Border));
        t.SetStylebox("grabber_highlight", "VScrollBar", ScrollBarStyle(Accent));
        t.SetStylebox("grabber_pressed", "VScrollBar", ScrollBarStyle(AccentSoft));
        t.SetStylebox("scroll", "VScrollBar", ScrollBarStyle(Bg0));

        return t;
    }

    private static StyleBoxFlat ScrollBarStyle(Color color)
    {
        var style = Panel(color, 4, 0);
        style.ShadowSize = 0;
        style.ContentMarginLeft = style.ContentMarginRight = 6;
        style.ContentMarginTop = style.ContentMarginBottom = 8;
        return style;
    }

    /// The dimming layer behind a modal screen, anchored to fill its parent.
    ///
    /// On a monitor a scrim darkens the world so the dialog reads as the only live thing. On the
    /// VR panel there is no world behind it to darken — the panel *is* a floating rectangle, so
    /// every pixel of scrim is a pixel of translucent sheet hanging in the room.
    ///
    /// This used to scale the alpha down to ~0.20 rather than removing it, on the theory that a
    /// faint tint still "seats the card against something". Measured on the panel
    /// (`--serika-uivr`), it does not: every menu painted ink over **100%** of the panel, and in
    /// a lit room a 0.2-alpha sheet 2 m wide and 1.3 m tall is plainly an object — a grey
    /// rectangle floating in front of the wall with a small menu inside it. The card already
    /// carries its own border and drop shadow (`Panel`), which is what actually seats it.
    ///
    /// The rect is still created in VR, at zero alpha, because callers also use it to swallow
    /// clicks that miss the card. Invisible, not absent.
    public static ColorRect Scrim(float alpha = 0.72f)
    {
        if (UI.VrUiSurface.Active) alpha = 0f;
        var r = new ColorRect { Color = new Color(0.02f, 0.03f, 0.05f, alpha) };
        r.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        return r;
    }

    /// The angular floor for text on the VR panel, expressed as a `font_size`.
    ///
    /// Measured with `--serika-uivr`: the panel is 2.0 m wide at 1.7 m, so it subtends 60.9°, and
    /// the Controls lay out across `VrUiSurface.LogicalSize.X` = 1000 px of that — **16.4 logical
    /// pixels per degree**. Godot's `font_size` is the em box and a Latin cap height is about 0.72
    /// of it, so a `font_size` of *n* reads as `n * 0.72 / 16.4` degrees. The comfortable floor
    /// for reading in a headset is ~0.5°, which is `n >= 11.4`; 13 is the first size that clears
    /// it with margin (0.57°) and it is already the most common small size in `UI/`, so rounding
    /// the stragglers up to it costs no layout.
    ///
    /// The eleven `font_size: 11` and one `font_size: 10` overrides across `UI/` were all below
    /// the floor — tag pills, instance rows, per-world author/capacity lines, the version stamp.
    /// Those are exactly the labels a player squints at, and squinting is not available in a
    /// headset.
    private const int VrMinFontSize = 13;

    /// Clamp a `font_size` to the VR floor. On desktop it is the identity, so the monitor
    /// typography is unchanged.
    ///
    /// Every `AddThemeFontSizeOverride("font_size", n)` in `UI/` routes through this rather than
    /// each screen branching on `VrUiSurface.Active`: a global type floor that any one screen can
    /// forget to apply is not a floor.
    public static int Fs(int size) =>
        UI.VrUiSurface.Active && size < VrMinFontSize ? VrMinFontSize : size;

    /// Clamp a card's design size to what actually fits the surface it is drawn on.
    ///
    /// The desktop sizes are returned untouched — a 1100x700 big menu is right on a 1080p
    /// monitor. On the panel the whole layout happens inside `VrUiSurface.LogicalSize`, and a
    /// card that meets or exceeds it is not "full-bleed", it is **clipped**: the SubViewport
    /// simply cuts it off at the panel edge, which looks deliberate and is not. `MainMenu`'s
    /// 1100x700 card lost its top edge and both side margins that way, and it filled 100% of a
    /// 61° x 39° panel with an opaque sheet, so opening the menu blacked out the room.
    ///
    /// The cap leaves a margin on all four sides so the card's border and shadow are inside the
    /// panel and the panel visibly *contains* the screen.
    public static Vector2 Card(float width, float height)
    {
        if (!UI.VrUiSurface.Active) return new Vector2(width, height);
        var max = (Vector2)UI.VrUiSurface.UsableLogicalSize;
        return new Vector2(Mathf.Min(width, max.X), Mathf.Min(height, max.Y));
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
