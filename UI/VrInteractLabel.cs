using Godot;

namespace SerikaSocial.UI;

/// The VR equivalent of `InteractionPrompt`: a small billboarded label floating just above the
/// controller, showing what the trigger would do right now.
///
/// A `CanvasLayer` prompt is no use here. In VR the 2D canvas is rendered into `VrUiSurface`'s
/// SubViewport and drawn on a panel floating in front of the player, so a hint written there would
/// appear on the menu surface — often metres away from the object it refers to, and hidden entirely
/// while no menu is open. Putting the text on the hand keeps the affordance where the player's
/// attention already is.
public partial class VrInteractLabel : Node3D, IInteractPrompt
{
    private Label3D _label;
    private string _current;

    public override void _Ready()
    {
        _label = new Label3D
        {
            Name = "InteractHint",
            // Sits above the controller so the hand and whatever it is reaching for stay unobscured.
            Position = new Vector3(0, 0.07f, 0),
            FontSize = 96,
            PixelSize = 0.0007f,
            Modulate = Brand.TextHi,
            OutlineModulate = new Color(0, 0, 0, 0.85f),
            OutlineSize = 24,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            // Readable against dark scenery and bright scenery alike, and never lit by the world.
            Shaded = false,
            // Small text at arm's length loses to any geometry it clips; keep it legible.
            NoDepthTest = true,
            RenderPriority = 50,
            Visible = false,
        };
        AddChild(_label);
    }

    public void Show(string verb)
    {
        if (_label == null) return;
        if (_current == verb && _label.Visible) return;
        _current = verb;
        _label.Text = verb;
        _label.Visible = true;
    }

    public void Clear()
    {
        if (_label == null || !_label.Visible) return;
        _current = null;
        _label.Visible = false;
    }
}
