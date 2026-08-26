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
        Layer = 70; // above chat (60) and other UI layers

        _panel = new Panel
        {
            AnchorLeft = 0.5f, AnchorTop = 1f, AnchorRight = 0.5f, AnchorBottom = 1f,
            OffsetLeft = -340, OffsetTop = -260, OffsetRight = 340, OffsetBottom = -8,
            Visible = false,
        };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.06f, 0.09f, 0.92f),
            BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
            BorderColor = Brand.Border,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ContentMarginLeft = 8, ContentMarginTop = 8,
            ContentMarginRight = 8, ContentMarginBottom = 8,
        };
        _panel.AddThemeStyleboxOverride("panel", style);
        AddChild(_panel);

        _rows = new VBoxContainer
        {
            AnchorLeft = 0, AnchorTop = 0, AnchorRight = 1, AnchorBottom = 1,
            Alignment = BoxContainer.AlignmentMode.Center,
        };
        _rows.AddThemeConstantOverride("separation", KeyGap);
        _panel.AddChild(_rows);

        // Preview of what's typed so the user sees text without looking at the LineEdit.
        _preview = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _preview.AddThemeFontSizeOverride("font_size", 16);
        _preview.AddThemeColorOverride("font_color", Brand.TextMid);
    }

    public new bool IsVisible => _panel.Visible;

    /// Attach to a viewport's focus signals so the keyboard auto-shows when a LineEdit gains
    /// focus and auto-hides when it loses focus.
    public void Attach(SubViewport vp)
    {
        // Poll for focus changes — Godot doesn't expose a focus-changed signal on SubViewport.
        // The poll runs only while the keyboard layer is added (VR mode), and is cheap.
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        if (!VrUiSurface.Active) return;

        // Find the currently focused Control in our viewport.
        var vp = GetViewport();
        if (vp == null) return;

        var focused = vp.GuiGetFocusOwner();
        if (focused is LineEdit le && le.IsVisibleInTree())
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

    public new void Show()
    {
        _panel.Visible = true;
        Rebuild();
    }

    public new void Hide()
    {
        _panel.Visible = false;
        _target = null;
    }

    private void Rebuild()
    {
        foreach (var child in _rows.GetChildren())
            child.QueueFree();

        string[][] layout = _symbols ? LayoutSym : _numbers ? LayoutNum : (_shifted ? LayoutShift : Layout);

        // Preview row at top.
        _rows.AddChild(_preview);

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
        btn.AddThemeFontSizeOverride("font_size", 18);
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
