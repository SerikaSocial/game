using System;
using Godot;
using SerikaSocial.World;

using SerikaSocial.UI;

namespace SerikaSocial;

/// The in-game photo camera, held like a phone: the panel is the handset and the viewfinder
/// shows the live feed from a detached free-flying `PhotoCamera` rather than a still of the
/// player's own view. WASD/QE fly the camera, the mouse aims it, Shift is boost.
///
/// Because the feed is a real `SubViewport`, this is also what an OBS "Window Capture" of the
/// client sees while the camera is up. Triggered with `C`, or the camera wedge on the R menu.
public partial class CameraMenu : CanvasLayer
{
    public event Action Closed;
    public event Action PhotoTaken;

    private ColorRect _scrim;
    private PanelContainer _card;
    private Label _statusLabel;
    private Label _countdownLabel;
    private TextureRect _feed;
    private PhotoCamera _photoCam;

    private int _countdown = 0;
    private double _timerAcc;
    private string _currentMode = "Photo";
    private string _currentAnchor = "World";

    /// The free-flying camera this viewfinder displays. Set by Main once the world exists.
    public void Bind(PhotoCamera cam)
    {
        _photoCam = cam;
        if (_feed != null && cam != null) _feed.Texture = cam.ViewTexture;
    }

    public override void _Ready()
    {
        Layer = 96;
        Visible = false;

        _scrim = Brand.Scrim(0.40f);
        AddChild(_scrim);

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(center);

        var mainVBox = new VBoxContainer();
        mainVBox.AddThemeConstantOverride("separation", 12);
        center.AddChild(mainVBox);

        // ── VIEWFINDER FRAME (640x360 16:9 Aspect Ratio) ──────────────────────────────
        _card = new PanelContainer
        {
            CustomMinimumSize = new Vector2(640, 360),
        };
        var frameStyle = Brand.Panel(new Color(0, 0, 0, 0.2f), 12, 2f, Brand.Accent);
        _card.AddThemeStyleboxOverride("panel", frameStyle);
        mainVBox.AddChild(_card);

        var vfMargin = new MarginContainer();
        foreach (var s in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            vfMargin.AddThemeConstantOverride(s, 12);
        _card.AddChild(vfMargin);

        var vfStack = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        vfMargin.AddChild(vfStack);

        // Live feed from the detached camera — this is the phone's screen.
        _feed = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _feed.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        vfStack.AddChild(_feed);

        _countdownLabel = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
            AnchorLeft = 0.5f, AnchorTop = 0.5f, AnchorRight = 0.5f, AnchorBottom = 0.5f,
            OffsetLeft = -50, OffsetTop = -40, OffsetRight = 50, OffsetBottom = 40,
        };
        _countdownLabel.AddThemeFontSizeOverride("font_size", Brand.Fs(64));
        _countdownLabel.AddThemeColorOverride("font_color", Brand.Warning);
        vfStack.AddChild(_countdownLabel);

        _statusLabel = new Label
        {
            Text = "CAMERA VIEW FINDER",
            AnchorLeft = 0, AnchorTop = 0,
        };
        _statusLabel.AddThemeFontSizeOverride("font_size", Brand.Fs(12));
        _statusLabel.AddThemeColorOverride("font_color", Brand.TextDim);
        vfStack.AddChild(_statusLabel);

        var closeBtn = Brand.Ghost_(new Button { CustomMinimumSize = new Vector2(32, 32), Icon = Icons.Get(Icons.Kind.Close, 14, Brand.TextMid) });
        closeBtn.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        closeBtn.Pressed += Hide;
        vfStack.AddChild(closeBtn);

        // ── BOTTOM CAMERA CONTROLS BAR ────────────────────────────────────────────────
        var ctrlBar = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        ctrlBar.AddThemeConstantOverride("separation", 10);
        mainVBox.AddChild(ctrlBar);

        ctrlBar.AddChild(ControlTile(Icons.Kind.Camera, "Take Photo", SnapPhoto));
        ctrlBar.AddChild(ControlTile(Icons.Kind.Timer, "Timed (5s)", StartTimedPhoto));
        ctrlBar.AddChild(ControlTile(Icons.Kind.Film, $"Mode: {_currentMode}", ToggleMode));
        ctrlBar.AddChild(ControlTile(Icons.Kind.Globe, $"Anchor: {_currentAnchor}", ToggleAnchor));
        ctrlBar.AddChild(ControlTile(Icons.Kind.Focus, "Focus: Auto", () => { }));
    }

    private Button ControlTile(Icons.Kind icon, string label, Action onClick)
    {
        var btn = new Button
        {
            Text = label,
            CustomMinimumSize = new Vector2(110, 60),
            Icon = Icons.Get(icon, 20, Brand.Accent),
            // Icon above the label rather than beside it — these tiles are square-ish and a
            // side-by-side icon leaves the text off-centre.
            VerticalIconAlignment = VerticalAlignment.Top,
            IconAlignment = HorizontalAlignment.Center,
            ExpandIcon = false,
        };
        Brand.Ghost_(btn);
        btn.AddThemeFontSizeOverride("font_size", Brand.Fs(12));
        btn.Pressed += onClick;
        return btn;
    }

    private void SnapPhoto()
    {
        // Capture what the phantom camera sees, not the player's own view.
        string saved = _photoCam?.TakePhoto();
        _statusLabel.Text = saved != null ? $"Saved {saved}" : "Couldn't save the photo";
        if (saved != null) PhotoTaken?.Invoke();
    }

    private void StartTimedPhoto()
    {
        _countdown = 5;
        _timerAcc = 0;
        _countdownLabel.Text = "5";
    }

    private void ToggleMode()
    {
        _currentMode = _currentMode == "Photo" ? "Stream" : "Photo";
        _statusLabel.Text = $"Camera Mode: {_currentMode}";
    }

    private void ToggleAnchor()
    {
        _currentAnchor = _currentAnchor == "World" ? "User" : "World";
        _statusLabel.Text = $"Camera Anchor: {_currentAnchor}";
    }

    /// Open the viewfinder and drop the phantom camera in front of `from` (the player's eye).
    public void Open(Transform3D? from = null)
    {
        _scrim.Visible = true;
        _card.Visible = true;
        Visible = true;
        _countdown = 0;
        _countdownLabel.Text = "";
        _statusLabel.Text = "WASD/QE fly · mouse aims · Shift boost";

        if (_photoCam != null)
        {
            _feed.Texture = _photoCam.ViewTexture;
            // Start a little ahead of the player so the first frame isn't inside their head.
            var origin = from ?? Transform3D.Identity;
            origin.Origin += origin.Basis.Z * -1.5f;
            _photoCam.Deploy(origin);
        }

        // Mouse stays captured while the viewfinder is up: here it aims the phantom camera
        // rather than driving a cursor. That exception is encoded in Main.SyncMenuHold, which
        // takes a non-cursor-freeing hold for this screen — nothing to set directly.
    }

    public new void Hide()
    {
        _scrim.Visible = false;
        _card.Visible = false;
        Visible = false;
        _photoCam?.Stow();
        Closed?.Invoke();
    }

    public bool IsOpen => Visible;

    // Escape/C are owned by Main so the menu can't close here and be reopened by the toggle.
    public override void _Input(InputEvent @event)
    {
        if (!IsOpen) return;

        if (@event is InputEventMouseMotion mm)
        {
            _photoCam?.AddLook(mm.Relative);
            GetViewport().SetInputAsHandled();
            return;
        }
        // Space is the shutter, so it doesn't also make the player jump behind the viewfinder.
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Space })
        {
            SnapPhoto();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        if (!IsOpen || _countdown <= 0) return;

        _timerAcc += delta;
        if (_timerAcc >= 1.0)
        {
            _timerAcc = 0;
            _countdown--;
            if (_countdown > 0)
            {
                _countdownLabel.Text = _countdown.ToString();
            }
            else
            {
                _countdownLabel.Text = "";
                SnapPhoto();
            }
        }
    }
}
