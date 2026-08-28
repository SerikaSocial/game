using Godot;

namespace SerikaSocial.UI;

/// Anything that can advertise the current interaction.
///
/// Exists because this one is a `CanvasLayer`, and in VR the whole 2D canvas is composited onto a
/// world-space panel that floats in front of you — so a prompt drawn here is either invisible or
/// stuck to a menu surface metres from the thing it describes. VR gets `VrInteractLabel` instead.
public interface IInteractPrompt
{
    void Show(string verb);
    void Clear();
}

/// The "[E] Sit" hint that appears just under the crosshair when the player is near something
/// interactable. Purely informational — it never takes input, so it can't steal focus from a
/// menu or swallow the very key it's advertising.
public partial class InteractionPrompt : CanvasLayer, IInteractPrompt
{
    private PanelContainer _panel;
    private Label _label;
    private string _current;

    /// Shown before the verb. "E" on desktop; on a touchscreen there is no key to press, so the
    /// button itself is the affordance and the hint is just the verb.
    public string KeyHint { get; set; } = "E";

    /// Below the HUD (50) so a toast or menu always wins, above the world.
    private const int HudLayer = 45;

    public override void _Ready()
    {
        Layer = HudLayer;

        _panel = new PanelContainer
        {
            AnchorLeft = 1.0f, AnchorRight = 1.0f, AnchorTop = 1.0f, AnchorBottom = 1.0f,
            // Positioned cleanly in the bottom right corner
            OffsetLeft = -240, OffsetRight = -28, OffsetTop = -72, OffsetBottom = -28,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        _panel.AddThemeStyleboxOverride("panel", Brand.Panel(new Color(Brand.Bg1, 0.88f), 10));
        AddChild(_panel);

        _label = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _label.AddThemeFontSizeOverride("font_size", Brand.Fs(16));
        _label.AddThemeColorOverride("font_color", Brand.TextHi);
        _panel.AddChild(_label);
    }

    /// Show the hint for `verb`, e.g. "Sit" → "[E]  Sit". No-op if it's already showing that
    /// verb, so the 10 Hz scan doesn't rebuild the label every tick.
    public void Show(string verb)
    {
        if (_panel == null) return; // never initialised — see the note in Main.AddUi
        if (_current == verb && _panel.Visible) return;
        _current = verb;
        _label.Text = string.IsNullOrEmpty(KeyHint) ? verb : $"[{KeyHint}]   {verb}";
        _panel.Visible = true;
    }

    public void Clear()
    {
        if (_panel == null) return; // never initialised — see the note in Main.AddUi
        if (!_panel.Visible) return;
        _current = null;
        _panel.Visible = false;
    }
}
