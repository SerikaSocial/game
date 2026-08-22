using System;
using System.Collections.Generic;
using Godot;

namespace SerikaSocial;

/// Minecraft-style world chat: a bottom-left feed of lines that fade after a few seconds when
/// idle, and a text input opened with T. System lines (joins/leaves) are tinted; player lines
/// show `<name> message`. While the input is open the feed stays fully visible and the caller
/// suppresses player movement.
///
/// Networking lives in Main — this overlay just raises MessageSubmitted with the typed text.
public partial class ChatOverlay : CanvasLayer
{
    private const int MaxLines = 60;
    private const double FadeAfterSeconds = 8.0;
    private const double FadeDuration = 1.0;

    public event Action<string> MessageSubmitted;
    /// Fired whenever the input closes (submit or Escape), so the caller can restore mouse/controls.
    public event Action Closed;
    public bool IsTyping { get; private set; }

    private readonly List<(Label label, double age)> _lines = new();
    private VBoxContainer _feed;
    private Panel _inputPanel;
    private LineEdit _input;

    public override void _Ready()
    {
        Layer = 60;

        _feed = new VBoxContainer
        {
            AnchorLeft = 0, AnchorTop = 1, AnchorRight = 0, AnchorBottom = 1,
            OffsetLeft = 16, OffsetTop = -320, OffsetRight = 640, OffsetBottom = -56,
            Alignment = BoxContainer.AlignmentMode.End,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _feed.AddThemeConstantOverride("separation", 2);
        AddChild(_feed);

        // Chat input bar (hidden until T).
        _inputPanel = new Panel
        {
            AnchorLeft = 0, AnchorTop = 1, AnchorRight = 1, AnchorBottom = 1,
            OffsetLeft = 12, OffsetTop = -48, OffsetRight = -12, OffsetBottom = -14,
            Visible = false,
        };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.06f, 0.09f, 0.85f),
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
        };
        _inputPanel.AddThemeStyleboxOverride("panel", style);
        AddChild(_inputPanel);

        _input = new LineEdit
        {
            AnchorLeft = 0, AnchorTop = 0, AnchorRight = 1, AnchorBottom = 1,
            OffsetLeft = 8, OffsetRight = -8,
            PlaceholderText = "Say something…  (Enter to send, Esc to close)",
            MaxLength = 200,
        };
        _input.AddThemeColorOverride("font_color", new Color(0.95f, 0.96f, 1f));
        _input.TextSubmitted += OnSubmit;
        _inputPanel.AddChild(_input);
    }

    public void OpenInput()
    {
        if (IsTyping) return;
        IsTyping = true;
        _inputPanel.Visible = true;
        _input.Text = "";
        _input.GrabFocus();
    }

    public void CloseInput()
    {
        if (!IsTyping) return;
        IsTyping = false;
        _inputPanel.Visible = false;
        _input.ReleaseFocus();
        Closed?.Invoke();
    }

    private void OnSubmit(string text)
    {
        text = text.Trim();
        if (text.Length > 0) MessageSubmitted?.Invoke(text);
        CloseInput();
    }

    public override void _Input(InputEvent @event)
    {
        if (IsTyping && @event is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            CloseInput();
            GetViewport().SetInputAsHandled();
        }
    }

    /// A player-authored line: `<name> message`.
    public void AddChat(string name, string text) =>
        AddLine($"<{name}> {text}", new Color(0.95f, 0.96f, 1f));

    /// A system/event line (joins, leaves, view changes) — tinted amber, Minecraft-style.
    public void AddSystem(string text) =>
        AddLine(text, new Color(1f, 0.86f, 0.4f));

    private void AddLine(string text, Color color)
    {
        var label = new Label
        {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", 15);
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
        label.AddThemeConstantOverride("outline_size", 4);
        _feed.AddChild(label);
        _lines.Add((label, 0));

        while (_lines.Count > MaxLines)
        {
            _lines[0].label.QueueFree();
            _lines.RemoveAt(0);
        }
    }

    public override void _Process(double delta)
    {
        for (int i = 0; i < _lines.Count; i++)
        {
            var (label, age) = _lines[i];
            age += delta;
            _lines[i] = (label, age);

            if (IsTyping)
            {
                label.Modulate = new Color(1, 1, 1, 1); // full history while typing
            }
            else
            {
                double over = age - FadeAfterSeconds;
                float a = over <= 0 ? 1f : (float)Math.Clamp(1.0 - over / FadeDuration, 0, 1);
                label.Modulate = new Color(1, 1, 1, a);
            }
        }
    }
}
