using System;
using Godot;
using SerikaSocial.Avatar;

namespace SerikaSocial;

/// VRChat-style Launch Pad & Pause Menu: wide rectangular layout (800x520),
/// dark purple glassmorphism styling, header stats (FPS/Clock), tabbed views (Launch Pad, Settings, Social),
/// quick action grids with icons, and zero container overflow.
public partial class PauseMenu : CanvasLayer
{
    public event Action Closed;
    public event Action HomePressed;
    public event Action WorldsPressed;
    public event Action QuitPressed;
    public event Action RespawnPressed;
    public event Action CameraTogglePressed;
    public event Action CopyInvitePressed;
    public event Action AvatarsPressed;
    public event Action<AvatarInstance.Emote> EmotePressed;

    private ColorRect _scrim;
    private PanelContainer _card;

    // Header elements
    private Label _clockLabel;
    private Label _fpsLabel;
    private Label _locationLabel;

    // Content container & tabs
    private VBoxContainer _tabLaunchPad;
    private VBoxContainer _tabSettings;
    private VBoxContainer _tabSocial;
    private Button _tabBtnLaunch;
    private Button _tabBtnSettings;
    private Button _tabBtnSocial;
    private Button _copyInviteButton;
    private Button _homeButton;

    // Settings
    private HSlider _sensitivitySlider;
    private HSlider _volumeSlider;
    private CheckButton _nameTagsToggle;
    private Label _sensitivityValue;
    private Label _volumeValue;

    public float MouseSensitivity { get; private set; } = 0.003f;
    public float MasterVolume { get; private set; } = 1.0f;
    public bool NameTagsVisible { get; private set; } = true;

    private double _clockTimer;

    public override void _Ready()
    {
        Layer = 90;

        _scrim = new ColorRect
        {
            Color = new Color(0.02f, 0.03f, 0.05f, 0.82f),
            AnchorRight = 1,
            AnchorBottom = 1,
            Visible = false,
        };
        AddChild(_scrim);

        var centerContainer = new CenterContainer();
        centerContainer.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(centerContainer);

        // Main rectangular container (820x520) — fixed wide format like VRChat Launch Pad
        _card = new PanelContainer
        {
            CustomMinimumSize = new Vector2(820, 520),
            Visible = false,
        };
        _card.AddThemeStyleboxOverride("panel", Brand.Panel(Brand.Bg1, 16, 1, Brand.Border));
        centerContainer.AddChild(_card);

        var margin = new MarginContainer();
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 20);
        _card.AddChild(margin);

        var mainVBox = new VBoxContainer();
        mainVBox.AddThemeConstantOverride("separation", 12);
        margin.AddChild(mainVBox);

        // ── TOP HEADER (User info, Clock, FPS) ─────────────────────────────────────────
        var header = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        header.AddThemeConstantOverride("separation", 12);
        mainVBox.AddChild(header);

        var userBadge = new HBoxContainer();
        userBadge.AddThemeConstantOverride("separation", 8);
        var dot = new ColorRect
        {
            CustomMinimumSize = new Vector2(10, 10),
            Color = Brand.Success,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        userBadge.AddChild(dot);
        _locationLabel = new Label { Text = "The Commons" };
        _locationLabel.AddThemeFontSizeOverride("font_size", 15);
        _locationLabel.AddThemeColorOverride("font_color", Brand.TextHi);
        userBadge.AddChild(_locationLabel);
        header.AddChild(userBadge);

        header.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        _clockLabel = new Label { Text = DateTime.Now.ToString("HH:mm") };
        _clockLabel.AddThemeFontSizeOverride("font_size", 16);
        _clockLabel.AddThemeColorOverride("font_color", Brand.Accent);
        header.AddChild(_clockLabel);

        header.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        _fpsLabel = new Label { Text = $"FPS: {Engine.GetFramesPerSecond()}" };
        _fpsLabel.AddThemeFontSizeOverride("font_size", 13);
        _fpsLabel.AddThemeColorOverride("font_color", Brand.TextDim);
        header.AddChild(_fpsLabel);

        mainVBox.AddChild(new HSeparator());

        // ── CENTER SCROLLABLE CONTENT ──────────────────────────────────────────────────
        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        scroll.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        mainVBox.AddChild(scroll);

        var contentStack = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(contentStack);

        // Build Tabs
        _tabLaunchPad = BuildLaunchPadTab();
        _tabSettings = BuildSettingsTab();
        _tabSocial = BuildSocialTab();

        contentStack.AddChild(_tabLaunchPad);
        contentStack.AddChild(_tabSettings);
        contentStack.AddChild(_tabSocial);

        mainVBox.AddChild(new HSeparator());

        // ── BOTTOM NAVIGATION & ACTIONS BAR ───────────────────────────────────────────
        var bottomBar = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        bottomBar.AddThemeConstantOverride("separation", 10);
        mainVBox.AddChild(bottomBar);

        // Tab selection buttons
        _tabBtnLaunch = MakeNavTab("🚀 Launch Pad", true);
        _tabBtnLaunch.Pressed += () => SwitchTab(0);
        bottomBar.AddChild(_tabBtnLaunch);

        _tabBtnSettings = MakeNavTab("⚙️ Settings", false);
        _tabBtnSettings.Pressed += () => SwitchTab(1);
        bottomBar.AddChild(_tabBtnSettings);

        _tabBtnSocial = MakeNavTab("👥 Social", false);
        _tabBtnSocial.Pressed += () => SwitchTab(2);
        bottomBar.AddChild(_tabBtnSocial);

        bottomBar.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        var quitBtn = Brand.Ghost_(new Button { Text = "🚪 Quit", CustomMinimumSize = new Vector2(90, 42) });
        quitBtn.AddThemeColorOverride("font_color", Brand.Danger);
        quitBtn.Pressed += () => QuitPressed?.Invoke();
        bottomBar.AddChild(quitBtn);

        var resumeBtn = Brand.Primary_(new Button { Text = "⚡ Resume (Esc)", CustomMinimumSize = new Vector2(140, 42) });
        resumeBtn.Pressed += Hide;
        bottomBar.AddChild(resumeBtn);

        SwitchTab(0);
    }

    private Button MakeNavTab(string label, bool active)
    {
        var btn = new Button
        {
            Text = label,
            CustomMinimumSize = new Vector2(130, 42),
        };
        return active ? Brand.Primary_(btn) : Brand.Ghost_(btn);
    }

    private void SwitchTab(int index)
    {
        _tabLaunchPad.Visible = index == 0;
        _tabSettings.Visible = index == 1;
        _tabSocial.Visible = index == 2;

        Brand.Primary_(_tabBtnLaunch); if (index != 0) Brand.Ghost_(_tabBtnLaunch);
        Brand.Primary_(_tabBtnSettings); if (index != 1) Brand.Ghost_(_tabBtnSettings);
        Brand.Primary_(_tabBtnSocial); if (index != 2) Brand.Ghost_(_tabBtnSocial);
    }

    // ── TAB 1: LAUNCH PAD ─────────────────────────────────────────────────────────────
    private VBoxContainer BuildLaunchPadTab()
    {
        var vbox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        vbox.AddThemeConstantOverride("separation", 14);

        var actionsTitle = new Label { Text = "Quick Actions" };
        actionsTitle.AddThemeFontSizeOverride("font_size", 14);
        actionsTitle.AddThemeColorOverride("font_color", Brand.TextDim);
        vbox.AddChild(actionsTitle);

        // 3-Column Grid for Quick Actions
        var grid = new GridContainer
        {
            Columns = 3,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 12);
        vbox.AddChild(grid);

        _homeButton = ActionCard("🏠 Go Home", "Return to your private home", () => { Hide(); HomePressed?.Invoke(); });
        grid.AddChild(_homeButton);

        grid.AddChild(ActionCard("⟲ Respawn", "Reset position to world spawn", () => { Hide(); RespawnPressed?.Invoke(); }));
        grid.AddChild(ActionCard("📷 Camera (V)", "Toggle 1st / 3rd person view", () => CameraTogglePressed?.Invoke()));

        _copyInviteButton = ActionCard("🔗 Copy Invite", "Copy world link to clipboard", () => CopyInvitePressed?.Invoke());
        grid.AddChild(_copyInviteButton);

        grid.AddChild(ActionCard("🧍 Sit", "Trigger sitting pose", () => { Hide(); EmotePressed?.Invoke(AvatarInstance.Emote.Sit); }));
        grid.AddChild(ActionCard("💃 Dance", "Trigger dance animation", () => { Hide(); EmotePressed?.Invoke(AvatarInstance.Emote.Dance); }));

        var linksTitle = new Label { Text = "Shortcuts" };
        linksTitle.AddThemeFontSizeOverride("font_size", 14);
        linksTitle.AddThemeColorOverride("font_color", Brand.TextDim);
        vbox.AddChild(linksTitle);

        var linksRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        linksRow.AddThemeConstantOverride("separation", 12);
        vbox.AddChild(linksRow);

        var worldsBtn = Brand.Ghost_(new Button
        {
            Text = "🌐 Browse Worlds…",
            CustomMinimumSize = new Vector2(240, 44),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        });
        worldsBtn.Pressed += () => WorldsPressed?.Invoke();
        linksRow.AddChild(worldsBtn);

        var avatarBtn = Brand.Ghost_(new Button
        {
            Text = "👕 Change Avatar…",
            CustomMinimumSize = new Vector2(240, 44),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        });
        avatarBtn.Pressed += () => AvatarsPressed?.Invoke();
        linksRow.AddChild(avatarBtn);

        return vbox;
    }

    private static Button ActionCard(string title, string subtitle, Action onClick)
    {
        var btn = new Button
        {
            Text = $"{title}\n{subtitle}",
            CustomMinimumSize = new Vector2(240, 64),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        Brand.Ghost_(btn);
        btn.AddThemeFontSizeOverride("font_size", 14);
        btn.Pressed += onClick;
        return btn;
    }

    // ── TAB 2: SETTINGS ───────────────────────────────────────────────────────────────
    private VBoxContainer BuildSettingsTab()
    {
        var vbox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        vbox.AddThemeConstantOverride("separation", 16);

        var title = new Label { Text = "Controls & Audio Settings" };
        title.AddThemeFontSizeOverride("font_size", 14);
        title.AddThemeColorOverride("font_color", Brand.TextDim);
        vbox.AddChild(title);

        // Mouse Sensitivity
        var sensRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        sensRow.AddThemeConstantOverride("separation", 14);
        vbox.AddChild(sensRow);

        var sensLbl = new Label { Text = "Mouse Sensitivity", CustomMinimumSize = new Vector2(160, 0) };
        sensLbl.AddThemeColorOverride("font_color", Brand.TextHi);
        sensRow.AddChild(sensLbl);

        _sensitivitySlider = new HSlider
        {
            MinValue = 0.5, MaxValue = 5.0, Step = 0.1, Value = 3.0,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(200, 0),
        };
        _sensitivitySlider.ValueChanged += v =>
        {
            MouseSensitivity = 0.001f * (float)v;
            _sensitivityValue.Text = $"{v:F1}";
        };
        sensRow.AddChild(_sensitivitySlider);
        _sensitivityValue = new Label { Text = "3.0", CustomMinimumSize = new Vector2(40, 0) };
        _sensitivityValue.AddThemeColorOverride("font_color", Brand.Accent);
        sensRow.AddChild(_sensitivityValue);

        // Master Volume
        var volRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        volRow.AddThemeConstantOverride("separation", 14);
        vbox.AddChild(volRow);

        var volLbl = new Label { Text = "Master Volume", CustomMinimumSize = new Vector2(160, 0) };
        volLbl.AddThemeColorOverride("font_color", Brand.TextHi);
        volRow.AddChild(volLbl);

        _volumeSlider = new HSlider
        {
            MinValue = 0, MaxValue = 100, Step = 1, Value = 100,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(200, 0),
        };
        _volumeSlider.ValueChanged += v =>
        {
            MasterVolume = (float)v / 100f;
            _volumeValue.Text = $"{(int)v}%";
            ApplyVolume();
        };
        volRow.AddChild(_volumeSlider);
        _volumeValue = new Label { Text = "100%", CustomMinimumSize = new Vector2(40, 0) };
        _volumeValue.AddThemeColorOverride("font_color", Brand.Accent);
        volRow.AddChild(_volumeValue);

        // Name tags toggle
        var tagRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        tagRow.AddThemeConstantOverride("separation", 14);
        vbox.AddChild(tagRow);

        var tagLbl = new Label { Text = "Show Name Tags", CustomMinimumSize = new Vector2(160, 0) };
        tagLbl.AddThemeColorOverride("font_color", Brand.TextHi);
        tagRow.AddChild(tagLbl);

        _nameTagsToggle = new CheckButton { ButtonPressed = true };
        _nameTagsToggle.Toggled += on => NameTagsVisible = on;
        tagRow.AddChild(_nameTagsToggle);

        // Controls Cheat Sheet
        vbox.AddChild(new HSeparator());
        var hint = new Label
        {
            Text = "Desktop Controls: WASD to move · Shift to sprint · Space to jump · Ctrl to crouch\n" +
                   "Press V to toggle 1st/3rd person · Mouse Wheel to zoom · T to Chat · Esc to Pause",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", 12);
        hint.AddThemeColorOverride("font_color", Brand.TextDim);
        vbox.AddChild(hint);

        return vbox;
    }

    // ── TAB 3: SOCIAL ─────────────────────────────────────────────────────────────────
    private VBoxContainer BuildSocialTab()
    {
        var vbox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        vbox.AddThemeConstantOverride("separation", 12);

        var title = new Label { Text = "Players & Moderation" };
        title.AddThemeFontSizeOverride("font_size", 14);
        title.AddThemeColorOverride("font_color", Brand.TextDim);
        vbox.AddChild(title);

        var info = new Label
        {
            Text = "Manage social connections and blocked users on social.serika.dev.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        info.AddThemeFontSizeOverride("font_size", 13);
        info.AddThemeColorOverride("font_color", Brand.TextHi);
        vbox.AddChild(info);

        return vbox;
    }

    public void ShowMenu(string worldName, bool alreadyHome)
    {
        _locationLabel.Text = string.IsNullOrEmpty(worldName) ? "Home" : worldName;
        _homeButton.Visible = !alreadyHome;
        _copyInviteButton.Visible = !alreadyHome;
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

    public override void _Process(double delta)
    {
        if (!IsOpen) return;
        _clockTimer += delta;
        if (_clockTimer >= 1.0)
        {
            _clockTimer = 0;
            _clockLabel.Text = DateTime.Now.ToString("HH:mm");
            _fpsLabel.Text = $"FPS: {Engine.GetFramesPerSecond()}";
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
}
