using Godot;

namespace SerikaSocial;

/// The update surface, as a **screen** rather than a dialog.
///
/// This used to be a 460 px card floating over whatever happened to be on screen — login, Home,
/// or a world you were standing in. An update is a mode, not a notification: it ends with the
/// client quitting and relaunching, and the player cannot usefully do anything else while it
/// runs. So it owns the display. Layer 210 puts it above the HUD (100), the loading screen (105)
/// and the quick menu (105), so nothing can draw over a download in progress.
///
/// Purely presentational. <see cref="Updater"/> owns the version check, download, verification
/// and staging and drives this through the properties below — same split as
/// <see cref="LoadingScreen"/>, which Main also only ever calls Show/SetStatus/Hide on.
///
/// VR gets a card, not a full-bleed gradient. On a monitor the screen *is* the window and a
/// gradient is the right shape; on the VR panel a full-bleed gradient is a 2 m opaque sheet
/// hanging in the room, which is the exact mistake the loading screen documents at length.
/// Deliberately NOT <c>IVrPassiveLayer</c>: that marker tells <see cref="UI.VrUiSurface"/> a layer
/// has nothing to click, which suppresses the laser pointer. This screen's whole purpose is its
/// two buttons, so in VR it must count as interactive or the player can see it and not press it.
public partial class UpdateScreen : CanvasLayer
{
    private Control _root;
    private Label _title;
    private Label _body;
    private ProgressBar _progress;
    private Label _detail;
    private HBoxContainer _actions;

    public override void _Ready()
    {
        Layer = 210;
        Visible = false;
    }

    public bool IsBuilt => _root != null;

    // ── Content, driven by Updater ──────────────────────────────────────────────────

    public string Title { set { if (_title != null) _title.Text = value; } }
    public string Body { set { if (_body != null) _body.Text = value; } }
    public string Detail { set { if (_detail != null) _detail.Text = value; } }

    public bool ProgressVisible { set { if (_progress != null) _progress.Visible = value; } }
    public bool DetailVisible { set { if (_detail != null) _detail.Visible = value; } }
    public double ProgressValue { set { if (_progress != null) _progress.Value = value; } }

    public void ClearActions()
    {
        if (_actions == null) return;
        foreach (var c in _actions.GetChildren())
        {
            _actions.RemoveChild(c);
            c.QueueFree();
        }
    }

    public void AddAction(Button b) => _actions?.AddChild(b);

    // ── Lifecycle ───────────────────────────────────────────────────────────────────

    public void Present()
    {
        Build();
        Visible = true;
        // An update is modal by nature: it ends in a relaunch. Holding input keeps the player
        // from walking around behind an opaque screen they cannot see past.
        UI.InputMode.Hold("updater");
    }

    public void Close()
    {
        Visible = false;
        UI.InputMode.Release("updater");
        _root?.QueueFree();
        _root = null;
        _title = null; _body = null; _progress = null; _detail = null; _actions = null;
    }

    private void Build()
    {
        if (_root != null) return;
        bool vr = UI.VrUiSurface.Active;

        _root = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        if (!vr)
        {
            var grad = new GradientTexture2D
            {
                Gradient = Brand.BackdropGradient(),
                Fill = GradientTexture2D.FillEnum.Linear,
                FillFrom = new Vector2(0.5f, 0f),
                FillTo = new Vector2(0.5f, 1f),
                Width = 8, Height = 256,
            };
            _root.AddChild(new TextureRect
            {
                Texture = grad,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                AnchorRight = 1, AnchorBottom = 1,
            });
        }

        var centre = new CenterContainer();
        centre.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(centre);

        var card = new PanelContainer { CustomMinimumSize = Brand.Card(620, 0) };
        card.AddThemeStyleboxOverride("panel", Brand.Panel(Brand.Bg1, 18, 1.5f, Brand.Border));
        centre.AddChild(card);

        var pad = new MarginContainer();
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            pad.AddThemeConstantOverride(side, 32);
        card.AddChild(pad);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 16);
        pad.AddChild(col);

        var eyebrow = new Label { Text = "SERIKA SOCIAL" };
        eyebrow.AddThemeFontSizeOverride("font_size", Brand.Fs(12));
        eyebrow.AddThemeColorOverride("font_color", Brand.Accent);
        col.AddChild(eyebrow);

        _title = new Label { Text = "Update" };
        _title.AddThemeFontSizeOverride("font_size", Brand.Fs(28));
        _title.AddThemeColorOverride("font_color", Brand.TextHi);
        col.AddChild(_title);

        _body = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _body.AddThemeFontSizeOverride("font_size", Brand.Fs(15));
        _body.AddThemeColorOverride("font_color", Brand.TextMid);
        col.AddChild(_body);

        _progress = new ProgressBar
        {
            CustomMinimumSize = new Vector2(0, 12),
            MinValue = 0,
            MaxValue = 100,
            Value = 0,
            ShowPercentage = false,
        };
        col.AddChild(_progress);

        _detail = new Label();
        _detail.AddThemeFontSizeOverride("font_size", Brand.Fs(12));
        _detail.AddThemeColorOverride("font_color", Brand.TextDim);
        col.AddChild(_detail);

        _actions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        _actions.AddThemeConstantOverride("separation", 12);
        col.AddChild(_actions);
    }
}
