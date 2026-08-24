using Godot;

namespace SerikaSocial;

/// A lightweight in-world HUD overlay: world name, player count, and a connection indicator.
/// Shown in the top-right corner while connected to a relay. Does not capture input.
public partial class InWorldHud : CanvasLayer
{
    private Label _worldName;
    private Label _playerCount;
    private Label _ping;
    private double _pingTimer;

    private Label _toast;
    private double _toastTimer;

    private MicIndicator _mic;

    public override void _Ready()
    {
        Layer = 50;

        // Mic indicator, bottom-left. Dim when muted, lit when the mic is on, and it pulses
        // with your voice level while you're actually speaking.
        _mic = new MicIndicator
        {
            AnchorLeft = 0, AnchorTop = 1, AnchorRight = 0, AnchorBottom = 1,
            OffsetLeft = 20, OffsetTop = -60, OffsetRight = 64, OffsetBottom = -16,
        };
        AddChild(_mic);

        var container = new VBoxContainer
        {
            AnchorLeft = 1, AnchorTop = 0, AnchorRight = 1, AnchorBottom = 0,
            OffsetLeft = -220, OffsetTop = 16, OffsetRight = -16, OffsetBottom = 80,
            Alignment = BoxContainer.AlignmentMode.End,
        };
        container.AddThemeConstantOverride("separation", 4);
        AddChild(container);

        _worldName = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _worldName.AddThemeFontSizeOverride("font_size", 16);
        _worldName.AddThemeColorOverride("font_color", new Color(0.9f, 0.92f, 0.95f));
        container.AddChild(_worldName);

        _playerCount = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _playerCount.AddThemeFontSizeOverride("font_size", 13);
        _playerCount.AddThemeColorOverride("font_color", new Color(0.6f, 0.64f, 0.72f));
        container.AddChild(_playerCount);

        _ping = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Visible = false,
        };
        _ping.AddThemeFontSizeOverride("font_size", 11);
        _ping.AddThemeColorOverride("font_color", new Color(0.5f, 0.54f, 0.6f));
        container.AddChild(_ping);

        // Transient centre-bottom toast (e.g. "First-person view").
        _toast = new Label
        {
            AnchorLeft = 0, AnchorTop = 1, AnchorRight = 1, AnchorBottom = 1,
            OffsetTop = -96, OffsetBottom = -64,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visible = false,
        };
        _toast.AddThemeFontSizeOverride("font_size", 15);
        _toast.AddThemeColorOverride("font_color", new Color(0.95f, 0.96f, 1f));
        _toast.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.7f));
        _toast.AddThemeConstantOverride("outline_size", 4);
        AddChild(_toast);
    }

    /// Show a short-lived status message centred near the bottom of the screen.
    public void Toast(string message, double seconds = 2.0)
    {
        _toast.Text = message;
        _toast.Visible = true;
        _toastTimer = seconds;
    }

    public void SetWorld(string name)
    {
        _worldName.Text = name;
    }

    public void SetPlayerCount(int count)
    {
        _playerCount.Text = $"{count} player{(count == 1 ? "" : "s")}";
    }

    public void SetPing(int ms)
    {
        _ping.Visible = ms > 0;
        _ping.Text = $"{ms} ms";
    }

    /// Set whether the mic is enabled (unmuted). Shown as a lit vs slashed mic bottom-left.
    public void SetMicEnabled(bool enabled) => _mic.SetEnabled(enabled);

    /// Feed the live capture level (raw RMS ~0..1) so the icon pulses while you speak.
    public void SetMicLevel(float rms) => _mic.SetLevel(rms);

    public override void _Process(double delta)
    {
        _pingTimer += delta;
        if (_pingTimer >= 2.0)
        {
            _pingTimer = 0;
        }

        if (_toast.Visible)
        {
            _toastTimer -= delta;
            if (_toastTimer <= 0) _toast.Visible = false;
        }
    }
}

/// A small, hand-drawn microphone icon for the bottom-left corner. Three states:
/// muted (dim, with a slash), on (violet), and speaking (glows green and swells with your
/// live voice level). Drawn in code so it needs no texture asset and stays on-brand.
public partial class MicIndicator : Control
{
    private bool _enabled;
    private float _level;       // smoothed 0..1 speaking level
    private float _targetLevel; // latest raw level, decays toward 0

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(44, 44);
        MouseFilter = MouseFilterEnum.Ignore;
        SetProcess(true);
    }

    public void SetEnabled(bool enabled) { _enabled = enabled; QueueRedraw(); }

    /// Feed raw capture RMS (~0..1). Amplified a little since speech RMS is usually small.
    public void SetLevel(float rms) => _targetLevel = Mathf.Clamp(rms * 6f, 0f, 1f);

    public override void _Process(double delta)
    {
        // Decay the target so the icon settles when you stop talking, and smooth toward it.
        _targetLevel = Mathf.Max(0f, _targetLevel - (float)delta * 1.5f);
        float next = Mathf.Lerp(_level, _targetLevel, (float)delta * 12f);
        if (Mathf.Abs(next - _level) > 0.002f) { _level = next; QueueRedraw(); }
    }

    public override void _Draw()
    {
        Vector2 c = Size * 0.5f;
        bool speaking = _enabled && _level > 0.06f;

        // Two tints: a dim base the whole mic is drawn in, and a bright fill that rises from the
        // bottom of the capsule with the speech level — the mic literally fills up as you talk.
        Color baseTint = !_enabled ? new Color(0.55f, 0.58f, 0.66f) : new Color(Brand.Accent.R, Brand.Accent.G, Brand.Accent.B, 0.45f);
        Color fillTint = Brand.Success;

        // Speaking halo that swells with level (kept — reads at a glance from across the HUD).
        if (speaking)
            DrawCircle(c, 15f + _level * 9f, new Color(Brand.Success.R, Brand.Success.G, Brand.Success.B, 0.16f + _level * 0.22f));

        // Backing disc so it reads on any world.
        DrawCircle(c, 15f, new Color(0.05f, 0.04f, 0.10f, 0.72f));

        // Capsule geometry (body + rounded caps).
        var capsule = new Rect2(c.X - 4.5f, c.Y - 10f, 9f, 13f);
        float capTop = capsule.Position.Y;
        float capBottom = capsule.Position.Y + capsule.Size.Y;

        // 1) Draw the whole mic in the dim base tint.
        DrawRect(capsule, baseTint, true);
        DrawCircle(new Vector2(c.X, capTop), 4.5f, baseTint);
        DrawCircle(new Vector2(c.X, capBottom), 4.5f, baseTint);

        // 2) Overlay the bright fill from the bottom up to the current level, clipped to the
        //    capsule. Only when live — a muted mic never fills.
        if (_enabled && _level > 0.01f)
        {
            float total = capBottom - capTop + 9f;             // include both rounded caps
            float fillH = Mathf.Clamp(_level, 0f, 1f) * total;
            float fillTopY = capBottom + 4.5f - fillH;          // bottom of lower cap upward

            // Lower cap fills first.
            DrawCircle(new Vector2(c.X, capBottom), 4.5f, fillTint);
            // Then the body up to fillTopY.
            float bodyTop = Mathf.Max(capTop, fillTopY);
            if (bodyTop < capBottom)
                DrawRect(new Rect2(capsule.Position.X, bodyTop, capsule.Size.X, capBottom - bodyTop), fillTint, true);
            // Top cap only once the fill reaches it.
            if (fillTopY <= capTop)
                DrawCircle(new Vector2(c.X, capTop), 4.5f, fillTint);
        }

        // Outline the capsule so it stays crisp over the fill.
        Color outline = !_enabled ? new Color(0.6f, 0.62f, 0.7f) : (speaking ? Brand.Success : Brand.Accent);
        DrawArc(new Vector2(c.X, capTop), 4.5f, Mathf.Pi, Mathf.Tau, 10, outline, 1.4f);
        DrawArc(new Vector2(c.X, capBottom), 4.5f, 0, Mathf.Pi, 10, outline, 1.4f);
        DrawLine(new Vector2(capsule.Position.X, capTop), new Vector2(capsule.Position.X, capBottom), outline, 1.4f);
        DrawLine(new Vector2(capsule.Position.X + capsule.Size.X, capTop), new Vector2(capsule.Position.X + capsule.Size.X, capBottom), outline, 1.4f);

        // Stand: arc + stem + base.
        DrawArc(c + new Vector2(0, -1), 8.5f, Mathf.Pi * 0.15f, Mathf.Pi * 0.85f, 16, outline, 2f);
        DrawLine(new Vector2(c.X, c.Y + 7.5f), new Vector2(c.X, c.Y + 11f), outline, 2f);
        DrawLine(new Vector2(c.X - 5f, c.Y + 11f), new Vector2(c.X + 5f, c.Y + 11f), outline, 2f);

        // Muted slash.
        if (!_enabled)
            DrawLine(new Vector2(c.X - 11f, c.Y - 11f), new Vector2(c.X + 11f, c.Y + 11f),
                     new Color(0.95f, 0.35f, 0.35f), 2.5f);
    }
}
