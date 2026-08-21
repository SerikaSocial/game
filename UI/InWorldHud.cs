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

    public override void _Ready()
    {
        Layer = 50;

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

    public override void _Process(double delta)
    {
        _pingTimer += delta;
        if (_pingTimer >= 2.0)
        {
            _pingTimer = 0;
        }
    }
}
