using System;
using Godot;

namespace SerikaSocial;

/// A pause/settings overlay shown when the player presses Escape in-world. Provides mouse
/// sensitivity, master volume, name tag visibility, and disconnect/quit actions.
/// Built in code to match the rest of the client's programmatic UI style.
public partial class PauseMenu : CanvasLayer
{
    public event Action Closed;
    public event Action HomePressed;
    public event Action WorldsPressed;
    public event Action QuitPressed;

    private ColorRect _scrim;
    private Panel _card;
    private Label _title;
    private Label _worldLabel;
    private Button _resumeButton;
    private Button _homeButton;
    private Button _quitButton;
    private HSlider _sensitivitySlider;
    private HSlider _volumeSlider;
    private CheckButton _nameTagsToggle;
    private Label _sensitivityValue;
    private Label _volumeValue;

    public float MouseSensitivity { get; private set; } = 0.003f;
    public float MasterVolume { get; private set; } = 1.0f;
    public bool NameTagsVisible { get; private set; } = true;

    public override void _Ready()
    {
        Layer = 90;

        _scrim = new ColorRect
        {
            Color = new Color(0.02f, 0.03f, 0.05f, 0.8f),
            AnchorRight = 1,
            AnchorBottom = 1,
            Visible = false,
        };
        AddChild(_scrim);

        _card = new Panel
        {
            CustomMinimumSize = new Vector2(500, 600),
            AnchorLeft = 0.5f,
            AnchorTop = 0.5f,
            AnchorRight = 0.5f,
            AnchorBottom = 0.5f,
            OffsetLeft = -250,
            OffsetTop = -300,
            OffsetRight = 250,
            OffsetBottom = 300,
            Visible = false,
        };
        _card.AddThemeStyleboxOverride("panel", Brand.Panel(Brand.Bg1, 16));
        AddChild(_card);

        var vbox = new VBoxContainer
        {
            AnchorRight = 1, AnchorBottom = 1,
            OffsetLeft = 28, OffsetTop = 28, OffsetRight = -28, OffsetBottom = -28,
        };
        vbox.AddThemeConstantOverride("separation", 14);
        _card.AddChild(vbox);

        _title = new Label { Text = "Paused", HorizontalAlignment = HorizontalAlignment.Center };
        _title.AddThemeFontSizeOverride("font_size", 24);
        _title.AddThemeColorOverride("font_color", new Color(0.95f, 0.96f, 0.98f));
        vbox.AddChild(_title);

        _worldLabel = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center };
        _worldLabel.AddThemeFontSizeOverride("font_size", 13);
        _worldLabel.AddThemeColorOverride("font_color", Brand.TextDim);
        vbox.AddChild(_worldLabel);

        // Controls cheat-sheet so the menu is also where players (re)learn the keys.
        var hints = new Label
        {
            Text = "WASD move · Shift sprint · Space jump · Ctrl crouch\n" +
                   "V camera · scroll to zoom (3rd person) · T chat · M mic · Esc resume",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hints.AddThemeFontSizeOverride("font_size", 12);
        hints.AddThemeColorOverride("font_color", new Color(0.55f, 0.6f, 0.7f));
        vbox.AddChild(hints);

        vbox.AddChild(new HSeparator());

        var settingsLabel = new Label { Text = "Settings" };
        settingsLabel.AddThemeFontSizeOverride("font_size", 13);
        settingsLabel.AddThemeColorOverride("font_color", Brand.TextDim);
        vbox.AddChild(settingsLabel);

        // Mouse sensitivity
        var sensRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        sensRow.AddThemeConstantOverride("separation", 12);
        vbox.AddChild(sensRow);
        var sensLabel = new Label { Text = "Mouse sensitivity", CustomMinimumSize = new Vector2(140, 0) };
        sensLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.74f, 0.82f));
        sensRow.AddChild(sensLabel);
        _sensitivitySlider = new HSlider
        {
            MinValue = 0.5, MaxValue = 5.0, Step = 0.1, Value = 3.0,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(120, 0),
        };
        _sensitivitySlider.ValueChanged += v =>
        {
            MouseSensitivity = 0.001f * (float)v;
            _sensitivityValue.Text = $"{v:F1}";
        };
        sensRow.AddChild(_sensitivitySlider);
        _sensitivityValue = new Label { Text = "3.0", CustomMinimumSize = new Vector2(36, 0) };
        _sensitivityValue.AddThemeColorOverride("font_color", new Color(0.6f, 0.64f, 0.72f));
        sensRow.AddChild(_sensitivityValue);

        // Volume
        var volRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        volRow.AddThemeConstantOverride("separation", 12);
        vbox.AddChild(volRow);
        var volLabel = new Label { Text = "Master volume", CustomMinimumSize = new Vector2(140, 0) };
        volLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.74f, 0.82f));
        volRow.AddChild(volLabel);
        _volumeSlider = new HSlider
        {
            MinValue = 0, MaxValue = 100, Step = 1, Value = 100,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(120, 0),
        };
        _volumeSlider.ValueChanged += v =>
        {
            MasterVolume = (float)v / 100f;
            _volumeValue.Text = $"{(int)v}%";
            ApplyVolume();
        };
        volRow.AddChild(_volumeSlider);
        _volumeValue = new Label { Text = "100%", CustomMinimumSize = new Vector2(36, 0) };
        _volumeValue.AddThemeColorOverride("font_color", new Color(0.6f, 0.64f, 0.72f));
        volRow.AddChild(_volumeValue);

        // Name tags toggle
        var tagRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        tagRow.AddThemeConstantOverride("separation", 12);
        vbox.AddChild(tagRow);
        var tagLabel = new Label { Text = "Show name tags", CustomMinimumSize = new Vector2(140, 0) };
        tagLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.74f, 0.82f));
        tagRow.AddChild(tagLabel);
        _nameTagsToggle = new CheckButton { ButtonPressed = true };
        _nameTagsToggle.Toggled += on => NameTagsVisible = on;
        tagRow.AddChild(_nameTagsToggle);

        vbox.AddChild(new HSeparator());

        // Buttons
        _resumeButton = MakeButton("Resume (Esc)", true);
        _resumeButton.Pressed += Hide;
        vbox.AddChild(_resumeButton);

        _homeButton = MakeButton("Return to Home", false);
        _homeButton.Pressed += () => { Hide(); HomePressed?.Invoke(); };
        vbox.AddChild(_homeButton);

        var worldsButton = MakeButton("Worlds…", false);
        worldsButton.Pressed += () => WorldsPressed?.Invoke();
        vbox.AddChild(worldsButton);

        _quitButton = MakeButton("Quit to desktop", false);
        _quitButton.Pressed += () => QuitPressed?.Invoke();
        vbox.AddChild(_quitButton);
    }

    /// Open the menu. `worldName` shows under the title; when the player is already Home the
    /// "Return to Home" button is pointless and hidden.
    public void ShowMenu(string worldName, bool alreadyHome)
    {
        _worldLabel.Text = string.IsNullOrEmpty(worldName) ? "" : $"in {worldName}";
        _homeButton.Visible = !alreadyHome;
        Show();
    }

    public new void Show()
    {
        _scrim.Visible = true;
        _card.Visible = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    public new void Hide()
    {
        _scrim.Visible = false;
        _card.Visible = false;
        Input.MouseMode = Input.MouseModeEnum.Captured;
        Closed?.Invoke();
    }

    public bool IsOpen => _card.Visible;

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape } && IsOpen)
        {
            Hide();
            GetViewport().SetInputAsHandled();
        }
    }

    private void ApplyVolume()
    {
        var bus = AudioServer.GetBusIndex("Master");
        if (bus >= 0)
        {
            AudioServer.SetBusVolumeDb(bus, Mathf.LinearToDb(MasterVolume));
            AudioServer.SetBusMute(bus, MasterVolume < 0.001f);
        }
    }

    private static Button MakeButton(string text, bool primary)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(0, 44) };
        return primary ? Brand.Primary_(b) : Brand.Ghost_(b);
    }
}
