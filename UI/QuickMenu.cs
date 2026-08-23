using System;
using Godot;
using SerikaSocial.Avatar;

namespace SerikaSocial;

/// VRChat Quick Menu (Launch Pad) — exact match of VRChat's Quick Menu design (input_file_0.png).
/// Floating dark card (560x640) with user header, banner carousel, 2x3 quick grid (Live Now,
/// Worlds, Avatars, Social, Groups, Shop), bottom pill row (Home, Respawn, Select, Safety),
/// and persistent bottom icon dock (Mic, Launchpad, Notifications, Location, Camera, Audio, Add, Settings, Alert).
public partial class QuickMenu : CanvasLayer
{
    public event Action Closed;
    public event Action HomePressed;
    public event Action RespawnPressed;
    public event Action OpenMainMenuWorlds;
    public event Action OpenMainMenuAvatars;
    public event Action OpenCameraMenu;
    public event Action OpenRadialMenu;
    public event Action<AvatarInstance.Emote> EmotePressed;

    private ColorRect _scrim;
    private PanelContainer _card;
    private Label _clockLabel;
    private Label _usernameLabel;
    private Label _micStatusLabel;
    private Button _micDockBtn;
    private bool _micMuted;

    private double _clockTimer;

    public override void _Ready()
    {
        Layer = 105;
        Visible = false;

        _scrim = new ColorRect
        {
            Color = new Color(0.02f, 0.03f, 0.05f, 0.65f),
            AnchorRight = 1,
            AnchorBottom = 1,
        };
        AddChild(_scrim);

        var centerContainer = new CenterContainer();
        centerContainer.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(centerContainer);

        var rootStack = new VBoxContainer();
        rootStack.AddThemeConstantOverride("separation", 10);
        centerContainer.AddChild(rootStack);

        // ── MAIN LAUNCHPAD CARD (560x580) ──────────────────────────────────────────────
        _card = new PanelContainer
        {
            CustomMinimumSize = new Vector2(560, 580),
        };
        _card.AddThemeStyleboxOverride("panel", Brand.Panel(Brand.Bg1, 18, 1.5f, Brand.Border));
        rootStack.AddChild(_card);

        var margin = new MarginContainer();
        foreach (var s in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(s, 16);
        _card.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 12);
        margin.AddChild(vbox);

        // TOP HEADER: User info | Clock | Tokens
        var topHeader = new HBoxContainer();
        topHeader.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(topHeader);

        var userBadge = new HBoxContainer();
        userBadge.AddThemeConstantOverride("separation", 6);
        var onlineDot = new ColorRect
        {
            CustomMinimumSize = new Vector2(8, 8),
            Color = Brand.Success,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        userBadge.AddChild(onlineDot);
        _usernameLabel = new Label { Text = "Serika User" };
        _usernameLabel.AddThemeFontSizeOverride("font_size", 14);
        _usernameLabel.AddThemeColorOverride("font_color", Brand.TextHi);
        userBadge.AddChild(_usernameLabel);
        topHeader.AddChild(userBadge);

        topHeader.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        _clockLabel = new Label { Text = DateTime.Now.ToString("HH:mm") };
        _clockLabel.AddThemeFontSizeOverride("font_size", 18);
        _clockLabel.AddThemeColorOverride("font_color", Brand.Accent);
        topHeader.AddChild(_clockLabel);

        topHeader.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        var vrcBadge = new PanelContainer();
        vrcBadge.AddThemeStyleboxOverride("panel", Brand.Panel(Brand.Bg2, 8, 1, Brand.BorderSoft));
        var vrcLbl = new Label { Text = "V  ✦✦✦✦" };
        vrcLbl.AddThemeFontSizeOverride("font_size", 12);
        vrcLbl.AddThemeColorOverride("font_color", Brand.AccentSoft);
        vrcBadge.AddChild(vrcLbl);
        topHeader.AddChild(vrcBadge);

        // BANNER CAROUSEL
        var banner = new PanelContainer { CustomMinimumSize = new Vector2(0, 110) };
        var bannerStyle = Brand.Panel(Brand.Bg3, 12, 1, Brand.Border);
        banner.AddThemeStyleboxOverride("panel", bannerStyle);
        var bannerMargin = new MarginContainer();
        foreach (var s in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            bannerMargin.AddThemeConstantOverride(s, 12);
        banner.AddChild(bannerMargin);
        var bannerVBox = new VBoxContainer();
        bannerVBox.AddThemeConstantOverride("separation", 4);
        bannerMargin.AddChild(bannerVBox);
        var bTitle = new Label { Text = "Who you are in VR is up to you" };
        bTitle.AddThemeFontSizeOverride("font_size", 18);
        bTitle.AddThemeColorOverride("font_color", Brand.TextHi);
        bannerVBox.AddChild(bTitle);
        var bSub = new Label { Text = "Find Your New Perfect Avatar  ·  Explore Wardrobe" };
        bSub.AddThemeFontSizeOverride("font_size", 12);
        bSub.AddThemeColorOverride("font_color", Brand.Accent);
        bannerVBox.AddChild(bSub);
        vbox.AddChild(banner);

        // 2x3 QUICK GRID (Live Now, Worlds, Avatars, Social, Groups, Shop)
        var grid = new GridContainer { Columns = 3, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 10);
        vbox.AddChild(grid);

        grid.AddChild(GridCard("⚡ Live Now", "Active Rooms", () => { Hide(); OpenMainMenuWorlds?.Invoke(); }));
        grid.AddChild(GridCard("🌐 Worlds", "Explore Places", () => { Hide(); OpenMainMenuWorlds?.Invoke(); }));
        grid.AddChild(GridCard("👕 Avatars", "Change Look", () => { Hide(); OpenMainMenuAvatars?.Invoke(); }));
        grid.AddChild(GridCard("👥 Social", "Friends & Online", () => { Hide(); OpenMainMenuWorlds?.Invoke(); }));
        grid.AddChild(GridCard("🏰 Groups", "Community Hub", () => { Hide(); OpenMainMenuWorlds?.Invoke(); }));
        grid.AddChild(GridCard("🛒 Shop", "Marketplace", () => { Hide(); OpenMainMenuAvatars?.Invoke(); }));

        // QUICK ACTION PILLS (Home, Respawn, Select, Safety)
        var pillRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        pillRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(pillRow);

        pillRow.AddChild(ActionPill("🏠 Home", () => { Hide(); HomePressed?.Invoke(); }));
        pillRow.AddChild(ActionPill("⟲ Respawn", () => { Hide(); RespawnPressed?.Invoke(); }));
        pillRow.AddChild(ActionPill("🧍 Sit", () => { Hide(); EmotePressed?.Invoke(AvatarInstance.Emote.Sit); }));
        pillRow.AddChild(ActionPill("💃 Dance", () => { Hide(); EmotePressed?.Invoke(AvatarInstance.Emote.Dance); }));

        // ── BOTTOM DOCK BAR (Icon Navigation Bar matching VRChat bottom bar) ────────────
        var dockBar = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        dockBar.AddThemeConstantOverride("separation", 8);
        rootStack.AddChild(dockBar);

        _micDockBtn = DockIcon("🎙️", () =>
        {
            _micMuted = !_micMuted;
            _micDockBtn.Text = _micMuted ? "🔴" : "🎙️";
        });
        dockBar.AddChild(_micDockBtn);

        dockBar.AddChild(DockIcon("🚀", () => { })); // Launchpad (current)
        dockBar.AddChild(DockIcon("🔔", () => { })); // Notifications
        dockBar.AddChild(DockIcon("📍", () => { Hide(); OpenMainMenuWorlds?.Invoke(); })); // Location / Worlds
        dockBar.AddChild(DockIcon("📷", () => { Hide(); OpenCameraMenu?.Invoke(); })); // Camera
        dockBar.AddChild(DockIcon("⭕", () => { Hide(); OpenRadialMenu?.Invoke(); })); // Radial Menu
        dockBar.AddChild(DockIcon("⚙️", () => { Hide(); OpenMainMenuWorlds?.Invoke(); })); // Settings
        dockBar.AddChild(DockIcon("❌", Hide)); // Close
    }

    private static Button GridCard(string title, string subtitle, Action onClick)
    {
        var btn = new Button
        {
            Text = $"{title}\n{subtitle}",
            CustomMinimumSize = new Vector2(165, 75),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        Brand.Ghost_(btn);
        btn.AddThemeFontSizeOverride("font_size", 14);
        btn.Pressed += onClick;
        return btn;
    }

    private static Button ActionPill(string label, Action onClick)
    {
        var btn = new Button
        {
            Text = label,
            CustomMinimumSize = new Vector2(120, 38),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        Brand.Ghost_(btn);
        btn.AddThemeFontSizeOverride("font_size", 13);
        btn.Pressed += onClick;
        return btn;
    }

    private static Button DockIcon(string icon, Action onClick)
    {
        var btn = new Button
        {
            Text = icon,
            CustomMinimumSize = new Vector2(46, 42),
        };
        Brand.Ghost_(btn);
        btn.AddThemeFontSizeOverride("font_size", 16);
        btn.Pressed += onClick;
        return btn;
    }

    public void Open(string username)
    {
        if (!string.IsNullOrEmpty(username)) _usernameLabel.Text = username;
        _scrim.Visible = true;
        _card.Visible = true;
        Visible = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    public new void Hide()
    {
        _scrim.Visible = false;
        _card.Visible = false;
        Visible = false;
        Input.MouseMode = Input.MouseModeEnum.Captured;
        Closed?.Invoke();
    }

    public bool IsOpen => Visible;

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
        }
    }
}
