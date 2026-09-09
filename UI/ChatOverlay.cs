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
    private ScrollContainer _scroll;
    private VBoxContainer _feed;
    private Panel _inputPanel;
    private LineEdit _input;
    private bool _stickToBottom = true;

    public override void _Ready()
    {
        Layer = 60;

        _scroll = new ScrollContainer
        {
            AnchorLeft = 0, AnchorTop = 1, AnchorRight = 0, AnchorBottom = 1,
            OffsetLeft = 16, OffsetTop = -360, OffsetRight = 640, OffsetBottom = -56,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.ShowNever,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        AddChild(_scroll);

        _feed = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _feed.AddThemeConstantOverride("separation", 2);
        _scroll.AddChild(_feed);

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
        _stickToBottom = true;
        _inputPanel.Visible = true;
        _scroll.MouseFilter = Control.MouseFilterEnum.Stop;
        _scroll.VerticalScrollMode = ScrollContainer.ScrollMode.Auto;
        _input.Text = "";
        _input.GrabFocus();
        ScrollToBottom();
    }

    public void CloseInput()
    {
        if (!IsTyping) return;
        IsTyping = false;
        _stickToBottom = true;
        _inputPanel.Visible = false;
        _scroll.MouseFilter = Control.MouseFilterEnum.Ignore;
        _scroll.VerticalScrollMode = ScrollContainer.ScrollMode.ShowNever;
        _input.ReleaseFocus();
        ScrollToBottom();
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
        if (!IsTyping) return;
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            CloseInput();
            GetViewport().SetInputAsHandled();
            return;
        }
        // Wheel anywhere while chat is open, like Minecraft — the log is a corner box, so
        // requiring the cursor to sit on it made history look frozen.
        if (@event is InputEventMouseButton { Pressed: true } mb)
        {
            int page = Math.Max(48, (int)(_scroll.Size.Y * 0.35f));
            if (mb.ButtonIndex == MouseButton.WheelUp)
            {
                _stickToBottom = false;
                _scroll.ScrollVertical = Math.Max(0, _scroll.ScrollVertical - page);
                GetViewport().SetInputAsHandled();
            }
            else if (mb.ButtonIndex == MouseButton.WheelDown)
            {
                _scroll.ScrollVertical += page;
                RememberIfAtBottom();
                GetViewport().SetInputAsHandled();
            }
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
        label.AddThemeFontSizeOverride("font_size", Brand.Fs(15));
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
        if (_stickToBottom || !IsTyping) ScrollToBottom();
    }

    private void ScrollToBottom()
    {
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(_scroll)) return;
            var bar = _scroll.GetVScrollBar();
            _scroll.ScrollVertical = (int)bar.MaxValue;
            _stickToBottom = true;
        }).CallDeferred();
    }

    private void RememberIfAtBottom()
    {
        if (!GodotObject.IsInstanceValid(_scroll)) return;
        var bar = _scroll.GetVScrollBar();
        _stickToBottom = bar.MaxValue - bar.Value - bar.Page < 32;
    }

    public override void _Process(double delta)
    {
        // In VR the layer's visibility is what decides whether the whole floating UI panel is
        // drawn (see Hud.HideAll), and this layer used to be visible from boot — so an empty
        // chat log alone was enough to keep a slab in front of the player forever. Show the
        // layer only while there is a message on screen or the input box is open.
        Visible = IsTyping || _lines.Count > 0;

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
