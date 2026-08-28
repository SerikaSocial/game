using System;
using Godot;

namespace SerikaSocial.UI;

/// A floating on-screen keyboard for VR users. Godot's LineEdit relies on the OS virtual
/// keyboard, which never appears inside a SubViewport render target — so in the headset, focusing
/// a text field does nothing. This Control watches for LineEdit focus changes and pops a
/// compact QWERTY panel that injects key events into the focused field via the viewport's
/// PushInput. It only activates in VR mode (when VrUiSurface.Active is true).
public partial class VrKeyboard : CanvasLayer
{
    private static readonly string[][] Layout =
    [
        ["q", "w", "e", "r", "t", "y", "u", "i", "o", "p"],
        ["a", "s", "d", "f", "g", "h", "j", "k", "l"],
        ["⇧", "z", "x", "c", "v", "b", "n", "m", "⌫"],
        ["123", " ", "↵"],
    ];

    private static readonly string[][] LayoutShift =
    [
        ["Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P"],
        ["A", "S", "D", "F", "G", "H", "J", "K", "L"],
        ["⇧", "Z", "X", "C", "V", "B", "N", "M", "⌫"],
        ["123", " ", "↵"],
    ];

    private static readonly string[][] LayoutNum =
    [
        ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"],
        ["-", "/", ":", ";", "(", ")", "$", "@", "\""],
        ["#+=", ".", ",", "?", "!", "'", "⌫"],
        ["ABC", " ", "↵"],
    ];

    private static readonly string[][] LayoutSym =
    [
        ["[", "]", "{", "}", "#", "%", "^", "*", "+", "="],
        ["_", "\\", "|", "~", "<", ">", "€", "£", "¥"],
        ["#+=", ".", ",", "?", "!", "'", "⌫"],
        ["ABC", " ", "↵"],
    ];

    private Panel _panel;
    private VBoxContainer _rows;
    private Label _preview;
    private LineEdit _target;
    private bool _shifted;
    private bool _numbers;
    private bool _symbols;

    private const float KeyW = 56f;
    private const float KeyH = 56f;
    private const int KeyGap = 4;

    public override void _Ready()
    {
        // Above EVERY screen that can hold a text field, which is the whole point of it.
        // At 70 it sat *under* the login screen (Hud, layer 100) — and that screen paints a
        // full-viewport 0.96-alpha backdrop, so the keyboard rendered perfectly and was covered
        // completely. Same for the chat input under the pause hub (105) and the main menu (106).
        // 150 clears Tutorial (120) and stays below the Updater (200), which is a hard modal with
        // no text entry of its own.
        Layer = 150;

        // The *layer*, not only the inner panel.
        //
        // A `CanvasLayer` is created visible, and `_Process` only calls `Hide()` when it has a
        // target to drop — at boot there is none, so nothing ever hid this layer and it stayed
        // visible for the entire session with an invisible panel inside it.
        // `VrUiSurface.HasInteractiveUi` asks whether any layer on the panel is visible, and it
        // gates both the panel render and the laser pointer, so this pinned a 2 m slab and a lit
        // laser in front of the player from the moment VR started. That is the fourth layer to do
        // this (mic indicator, InteractionPrompt, ChatOverlay); it is caught here by
        // `--serika-uivr`, which reports a layer that is visible while drawing nothing as BLANK.
        Visible = false;

        _panel = new Panel
        {
            // Lifted off the very bottom edge. The panel's logical space is only 640 px tall, and
            // a 252 px keyboard hard against the bottom put its space/enter row on the panel's
            // border, where the curve makes it the hardest row to hit with a ray.
            AnchorLeft = 0.5f, AnchorTop = 1f, AnchorRight = 0.5f, AnchorBottom = 1f,
            OffsetLeft = -340, OffsetTop = -272, OffsetRight = 340, OffsetBottom = -20,
            Visible = false,
        };
        // On-brand and opaque. The old backing was a hand-mixed blue-grey at 0.92 alpha, which is
        // the one piece of chrome in the client that is not violet — and on the VR panel, which
        // is itself translucent, a 0.92 backing let the room show through the gaps between keys.
        // Keys are the smallest targets in the whole UI; they need the most contrast, not the
        // least.
        var style = Brand.Panel(Brand.Bg0, 12, 1.5f, Brand.Border);
        style.ContentMarginLeft = 10; style.ContentMarginRight = 10;
        style.ContentMarginTop = 10; style.ContentMarginBottom = 10;
        _panel.AddThemeStyleboxOverride("panel", style);
        AddChild(_panel);

        // The preview label and the key grid are siblings, not parent-and-child.
        //
        // `Rebuild` clears the key grid every time the layout changes (shift, 123, symbols), and
        // the preview used to live inside that grid — so the rebuild queued the preview itself
        // for deletion and then re-added the very object it had just freed. The second rebuild
        // therefore threw ObjectDisposedException on every frame, from both `Show()` and
        // `_Process`, and the keyboard died as soon as anyone pressed shift.
        var stack = new VBoxContainer
        {
            AnchorLeft = 0, AnchorTop = 0, AnchorRight = 1, AnchorBottom = 1,
            Alignment = BoxContainer.AlignmentMode.Center,
        };
        stack.AddThemeConstantOverride("separation", KeyGap);
        _panel.AddChild(stack);

        _rows = new VBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _rows.AddThemeConstantOverride("separation", KeyGap);

        // Preview of what's typed so the user sees text without looking at the LineEdit.
        _preview = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _preview.AddThemeFontSizeOverride("font_size", Brand.Fs(16));
        _preview.AddThemeColorOverride("font_color", Brand.TextMid);
        stack.AddChild(_preview);
        stack.AddChild(_rows);
    }

    public new bool IsVisible => _panel.Visible;

    /// Additional viewports to watch for a focused text field.
    ///
    /// Focus is per-viewport, and this keyboard lives on the menu panel — so `GetViewport()` only
    /// ever sees fields on the menu panel. The chat input does not live there: `ChatOverlay` is
    /// mounted as chrome, which routes it to `VrWristHud`'s own SubViewport, so focusing it could
    /// never raise this keyboard and there was no other way to type in VR at all. Delivering the
    /// keystroke is not the problem — `OnKey` writes straight into the target `LineEdit` and works
    /// across viewports — only *finding* the target was.
    private readonly System.Collections.Generic.List<Viewport> _watched = new();

    /// Also watch `vp` for a focused `LineEdit`. Call once per viewport that can hold a text
    /// field; the keyboard's own viewport is always watched and does not need registering.
    public void Attach(SubViewport vp)
    {
        // Poll for focus changes — Godot doesn't expose a focus-changed signal on SubViewport.
        // The poll runs only while the keyboard layer is added (VR mode), and is cheap.
        SetProcess(true);
        if (vp != null && !_watched.Contains(vp)) _watched.Add(vp);
    }

    public override void _Process(double delta)
    {
        if (!VrUiSurface.Active) return;

        // Our own viewport first, then any registered elsewhere.
        LineEdit focusedField = FocusedField(GetViewport());
        for (int i = 0; focusedField == null && i < _watched.Count; i++)
            focusedField = FocusedField(_watched[i]);

        if (focusedField is { } le && le.IsVisibleInTree())
        {
            if (_target != le)
            {
                _target = le;
                Show();
            }
            _preview.Text = le.Text;
        }
        else if (_target != null)
        {
            _target = null;
            Hide();
        }
    }

    private static LineEdit FocusedField(Viewport vp) =>
        vp != null && GodotObject.IsInstanceValid(vp) && vp.GuiGetFocusOwner() is LineEdit le ? le : null;

    public new void Show()
    {
        Visible = true; // the layer, not just the panel — see the note in Hud.HideAll
        _panel.Visible = true;
        Rebuild();
    }

    public new void Hide()
    {
        Visible = false;
        _panel.Visible = false;
        _target = null;
    }

    private void Rebuild()
    {
        foreach (var child in _rows.GetChildren())
        {
            _rows.RemoveChild(child);
            child.QueueFree();
        }

        string[][] layout = _symbols ? LayoutSym : _numbers ? LayoutNum : (_shifted ? LayoutShift : Layout);

        foreach (var row in layout)
        {
            var hbox = new HBoxContainer
            {
                Alignment = BoxContainer.AlignmentMode.Center,
            };
            hbox.AddThemeConstantOverride("separation", KeyGap);

            foreach (var key in row)
            {
                var btn = MakeKey(key);
                hbox.AddChild(btn);
            }

            _rows.AddChild(hbox);
        }
    }

    private Button MakeKey(string label)
    {
        bool isSpace = label == " ";
        bool isEnter = label == "↵";
        bool isBack = label == "⌫";
        bool isShift = label == "⇧";
        bool is123 = label == "123" || label == "ABC" || label == "#+=";

        float w = KeyW;
        if (isSpace) w = KeyW * 5;
        else if (is123) w = KeyW * 1.8f;

        var btn = new Button
        {
            Text = label,
            CustomMinimumSize = new Vector2(w, KeyH),
        };
        btn.AddThemeFontSizeOverride("font_size", Brand.Fs(18));
        btn.AddThemeConstantOverride("h_separation", 0);

        // Style keys
        if (isEnter)
        {
            btn.AddThemeStyleboxOverride("normal", KeyStyle(Brand.Primary, Brand.PrimaryHi));
            btn.AddThemeColorOverride("font_color", Brand.TextHi);
        }
        else if (isShift && _shifted)
        {
            btn.AddThemeStyleboxOverride("normal", KeyStyle(Brand.Accent, Brand.AccentSoft));
            btn.AddThemeColorOverride("font_color", Brand.Bg0);
        }
        else if (isBack)
        {
            btn.AddThemeStyleboxOverride("normal", KeyStyle(new Color(0.3f, 0.1f, 0.1f), new Color(0.4f, 0.15f, 0.15f)));
            btn.AddThemeColorOverride("font_color", Brand.TextHi);
        }

        btn.Pressed += () => OnKey(label);
        return btn;
    }

    private static StyleBoxFlat KeyStyle(Color bg, Color hover)
    {
        var s = new StyleBoxFlat
        {
            BgColor = bg,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = 4, ContentMarginTop = 2,
            ContentMarginRight = 4, ContentMarginBottom = 2,
        };
        return s;
    }

    private void OnKey(string key)
    {
        if (_target == null) return;

        switch (key)
        {
            case "⇧":
                _shifted = !_shifted;
                Rebuild();
                return;
            case "⌫":
                if (_target.Text.Length > 0)
                    _target.Text = _target.Text[..^1];
                _target.CaretColumn = _target.Text.Length;
                break;
            case "↵":
                _target.EmitSignal(LineEdit.SignalName.TextSubmitted, _target.Text);
                Hide();
                return;
            case "123":
                _numbers = true;
                _symbols = false;
                _shifted = false;
                Rebuild();
                return;
            case "ABC":
                _numbers = false;
                _symbols = false;
                Rebuild();
                return;
            case "#+=":
                _symbols = true;
                _numbers = false;
                Rebuild();
                return;
            case " ":
                _target.Text += " ";
                _target.CaretColumn = _target.Text.Length;
                break;
            default:
                _target.Text += key;
                _target.CaretColumn = _target.Text.Length;
                // Auto-unshift after one capital letter
                if (_shifted && !_numbers && !_symbols)
                {
                    _shifted = false;
                    Rebuild();
                }
                break;
        }

        _preview.Text = _target.Text;
    }
}
