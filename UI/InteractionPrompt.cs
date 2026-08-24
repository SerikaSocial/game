using Godot;

namespace SerikaSocial.UI;

/// The "[E] Sit" hint that appears just under the crosshair when the player is near something
/// interactable. Purely informational — it never takes input, so it can't steal focus from a
/// menu or swallow the very key it's advertising.
public partial class InteractionPrompt : CanvasLayer
{
    private PanelContainer _panel;
    private Label _label;
    private string _current;

    /// Below the HUD (50) so a toast or menu always wins, above the world.
    private const int HudLayer = 45;

    public override void _Ready()
    {
        Layer = HudLayer;

        _panel = new PanelContainer
        {
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0.5f, AnchorBottom = 0.5f,
            // Centred horizontally, a little below the middle of the screen so it sits under
            // the crosshair rather than over the thing being looked at.
            OffsetLeft = -140, OffsetRight = 140, OffsetTop = 64, OffsetBottom = 108,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        _panel.AddThemeStyleboxOverride("panel", Brand.Panel(new Color(Brand.Bg1, 0.82f), 10));
        AddChild(_panel);

        _label = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _label.AddThemeFontSizeOverride("font_size", 16);
        _label.AddThemeColorOverride("font_color", Brand.TextHi);
        _panel.AddChild(_label);
    }

    /// Show the hint for `verb`, e.g. "Sit" → "[E]  Sit". No-op if it's already showing that
    /// verb, so the 10 Hz scan doesn't rebuild the label every tick.
    public void Show(string verb)
    {
        if (_current == verb && _panel.Visible) return;
        _current = verb;
        _label.Text = $"[E]   {verb}";
        _panel.Visible = true;
    }

    public void Clear()
    {
        if (!_panel.Visible) return;
        _current = null;
        _panel.Visible = false;
    }
}
