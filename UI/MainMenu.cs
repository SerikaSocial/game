using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

using SerikaSocial.UI;

namespace SerikaSocial;

/// VRChat Main Menu (Big Menu) — exact match of VRChat's Main Menu design (input_file_1.png).
/// Fullscreen wide layout (1100x700) with top header bar, left category sidebar, 3x4 main content grid
/// for Worlds & Avatars, and persistent bottom tab navigation bar (Live Now, Worlds, Avatars, Social, Groups, Shop).
public partial class MainMenu : CanvasLayer
{
    public event Action Closed;
    public event Action<string> JoinWorldPressed;
    public event Action<string, string, string> AvatarChosen;

    private ColorRect _scrim;
    private PanelContainer _card;
    private Label _clockLabel;
    private Label _usernameLabel;
    private GridContainer _contentGrid;
    private Label _categoryTitle;
    private Label _statusLabel;

    private int _activeBottomTab = 1; // 0=Live, 1=Worlds, 2=Avatars, 3=Social, 4=Groups, 5=Shop
    private double _clockTimer;

    // Set by Main so avatar/world cards can fetch their thumbnail bytes.
    public Func<string, System.Threading.Tasks.Task<byte[]>> ImageLoader;

    private readonly List<Button> _tabButtons = new();
    private readonly List<(string id, string name, string desc, int cap, string author, string dlUrl)> _worldsCache = new();
    private readonly List<(string id, string name, string author, string thumbUrl, string dlUrl)> _avatarsCache = new();

    public override void _Ready()
    {
        Layer = 106;
        Visible = false;

        _scrim = Brand.Scrim(0.85f);
        AddChild(_scrim);

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(center);

        _card = new PanelContainer
        {
            CustomMinimumSize = new Vector2(1100, 700),
        };
        _card.AddThemeStyleboxOverride("panel", Brand.Panel(Brand.Bg1, 16, 1.5f, Brand.Border));
        center.AddChild(_card);

        var margin = new MarginContainer();
        foreach (var s in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(s, 16);
        _card.AddChild(margin);

        var mainVBox = new VBoxContainer();
        mainVBox.AddThemeConstantOverride("separation", 10);
        margin.AddChild(mainVBox);

        // ── TOP HEADER BAR ─────────────────────────────────────────────────────────────
        var header = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        header.AddThemeConstantOverride("separation", 10);
        mainVBox.AddChild(header);

        var userBadge = new HBoxContainer();
        userBadge.AddThemeConstantOverride("separation", 6);
        var dot = new ColorRect
        {
            CustomMinimumSize = new Vector2(8, 8),
            Color = Brand.Success,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        userBadge.AddChild(dot);
        _usernameLabel = new Label { Text = "VRChat User" };
        _usernameLabel.AddThemeFontSizeOverride("font_size", 14);
        _usernameLabel.AddThemeColorOverride("font_color", Brand.TextHi);
        userBadge.AddChild(_usernameLabel);
        header.AddChild(userBadge);

        header.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        _clockLabel = new Label { Text = DateTime.Now.ToString("HH:mm") };
        _clockLabel.AddThemeFontSizeOverride("font_size", 18);
        _clockLabel.AddThemeColorOverride("font_color", Brand.Accent);
        header.AddChild(_clockLabel);

        header.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        // Header Icons
        header.AddChild(HeaderIcon(Icons.Kind.Search));
        header.AddChild(HeaderIcon(Icons.Kind.Bell));
        header.AddChild(HeaderIcon(Icons.Kind.Calendar));
        header.AddChild(HeaderIcon(Icons.Kind.Box));
        header.AddChild(HeaderIcon(Icons.Kind.Gear));

        var closeBtn = Brand.Ghost_(new Button
        {
            CustomMinimumSize = new Vector2(36, 36),
            Icon = Icons.Get(Icons.Kind.Close, 16, Brand.TextMid),
        });
        closeBtn.Pressed += Hide;
        header.AddChild(closeBtn);

        mainVBox.AddChild(new HSeparator());

        // ── MIDDLE BODY (Left Sidebar + Main Grid) ─────────────────────────────────────
        var bodyRow = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        bodyRow.AddThemeConstantOverride("separation", 12);
        mainVBox.AddChild(bodyRow);

        // LEFT SIDEBAR (Category links)
        var sidebar = new VBoxContainer { CustomMinimumSize = new Vector2(200, 0) };
        sidebar.AddThemeConstantOverride("separation", 6);
        bodyRow.AddChild(sidebar);

        var sideTitle = new Label { Text = "CATEGORIES" };
        sideTitle.AddThemeFontSizeOverride("font_size", 11);
        sideTitle.AddThemeColorOverride("font_color", Brand.TextDim);
        sidebar.AddChild(sideTitle);

        sidebar.AddChild(SideCategory(Icons.Kind.Pin, "Current World"));
        sidebar.AddChild(SideCategory(Icons.Kind.Flame, "Popular Worlds"));
        sidebar.AddChild(SideCategory(Icons.Kind.Star, "New & Noteworthy"));
        sidebar.AddChild(SideCategory(Icons.Kind.Shirt, "Avatar Worlds"));
        sidebar.AddChild(SideCategory(Icons.Kind.Gamepad, "Mini Games"));
        sidebar.AddChild(SideCategory(Icons.Kind.Flask, "Community Labs"));

        bodyRow.AddChild(new VSeparator());

        // MAIN CONTENT AREA (Grid)
        var contentVBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        contentVBox.AddThemeConstantOverride("separation", 8);
        bodyRow.AddChild(contentVBox);

        _categoryTitle = new Label { Text = "WORLDS" };
        _categoryTitle.AddThemeFontSizeOverride("font_size", 20);
        _categoryTitle.AddThemeColorOverride("font_color", Brand.TextHi);
        contentVBox.AddChild(_categoryTitle);

        _statusLabel = new Label { Text = "" };
        _statusLabel.AddThemeFontSizeOverride("font_size", 12);
        _statusLabel.AddThemeColorOverride("font_color", Brand.TextDim);
        contentVBox.AddChild(_statusLabel);

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        scroll.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        contentVBox.AddChild(scroll);

        _contentGrid = new GridContainer
        {
            Columns = 4,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _contentGrid.AddThemeConstantOverride("h_separation", 10);
        _contentGrid.AddThemeConstantOverride("v_separation", 10);
        scroll.AddChild(_contentGrid);

        mainVBox.AddChild(new HSeparator());

        // ── BOTTOM TAB NAVIGATION BAR (Live Now | Worlds | Avatars | Social | Groups | Shop) ──
        var bottomTabs = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        bottomTabs.AddThemeConstantOverride("separation", 10);
        mainVBox.AddChild(bottomTabs);

        bottomTabs.AddChild(BottomTabButton(Icons.Kind.Bolt, "Live Now", 0));
        bottomTabs.AddChild(BottomTabButton(Icons.Kind.Globe, "Worlds", 1));
        bottomTabs.AddChild(BottomTabButton(Icons.Kind.Shirt, "Avatars", 2));
        bottomTabs.AddChild(BottomTabButton(Icons.Kind.Users, "Social", 3));
        bottomTabs.AddChild(BottomTabButton(Icons.Kind.Group, "Groups", 4));
        bottomTabs.AddChild(BottomTabButton(Icons.Kind.Cart, "Shop", 5));
    }

    private Button HeaderIcon(Icons.Kind icon)
    {
        var b = new Button
        {
            CustomMinimumSize = new Vector2(36, 36),
            Icon = Icons.Get(icon, 17, Brand.TextMid),
        };
        Brand.Ghost_(b);
        return b;
    }

    private Button SideCategory(Icons.Kind icon, string text)
    {
        var b = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(0, 36),
            Icon = Icons.Get(icon, 16, Brand.Accent),
        };
        Brand.Ghost_(b);
        b.AddThemeFontSizeOverride("font_size", 13);
        b.AddThemeConstantOverride("h_separation", 9);
        return b;
    }

    private Button BottomTabButton(Icons.Kind icon, string label, int index)
    {
        var btn = new Button
        {
            Text = label,
            CustomMinimumSize = new Vector2(130, 42),
            Icon = Icons.Get(icon, 17, Brand.TextMid),
        };
        btn.AddThemeConstantOverride("h_separation", 8);
        btn.Pressed += () => SwitchBottomTab(index);
        _tabButtons.Add(btn);
        return btn;
    }

    private void SwitchBottomTab(int index)
    {
        _activeBottomTab = index;
        // Restyle every tab button so the active one is highlighted and the rest are ghosts —
        // without this the tab styled at build time (Worlds) stayed highlighted forever.
        for (int i = 0; i < _tabButtons.Count; i++)
        {
            if (i == index) Brand.Primary_(_tabButtons[i]);
            else Brand.Ghost_(_tabButtons[i]);
        }
        _categoryTitle.Text = index switch
        {
            0 => "LIVE NOW",
            1 => "WORLDS",
            2 => "AVATARS",
            3 => "SOCIAL & FRIENDS",
            4 => "COMMUNITY GROUPS",
            _ => "SHOP",
        };
        PopulateGrid();
    }

    public void SetWorlds(List<(string id, string name, string desc, int cap, string author, string dlUrl)> worlds)
    {
        _worldsCache.Clear();
        _worldsCache.AddRange(worlds);
        if (_activeBottomTab is 0 or 1) PopulateGrid();
    }

    public void SetAvatars(List<(string id, string name, string author, string thumbUrl, string dlUrl)> avatars)
    {
        _avatarsCache.Clear();
        _avatarsCache.AddRange(avatars);
        if (_activeBottomTab == 2) PopulateGrid();
    }

    private void PopulateGrid()
    {
        foreach (var child in _contentGrid.GetChildren()) child.QueueFree();

        switch (_activeBottomTab)
        {
            case 2: // Avatars
                if (_avatarsCache.Count == 0) { _statusLabel.Text = "No avatars available."; return; }
                _statusLabel.Text = $"{_avatarsCache.Count} avatar(s) available";
                foreach (var a in _avatarsCache)
                {
                    var card = MakeCard(a.name, string.IsNullOrEmpty(a.author) ? "Avatar" : $"by {a.author}", -1, a.thumbUrl, a.name);
                    card.Pressed += () => { Hide(); AvatarChosen?.Invoke(a.id, a.dlUrl, a.name); };
                    _contentGrid.AddChild(card);
                }
                return;

            case 0: // Live Now — active servers / popular worlds
                if (_worldsCache.Count == 0) { _statusLabel.Text = "No live servers right now."; return; }
                _statusLabel.Text = $"{_worldsCache.Count} live server(s)";
                foreach (var w in _worldsCache)
                {
                    var card = MakeCard($"{w.name}", $"{w.cap} slots · popular server", w.cap, null, w.name);
                    card.Pressed += () => { Hide(); JoinWorldPressed?.Invoke(w.id); };
                    _contentGrid.AddChild(card);
                }
                return;

            case 1: // Worlds
                if (_worldsCache.Count == 0) { _statusLabel.Text = "No worlds available."; return; }
                _statusLabel.Text = $"{_worldsCache.Count} world(s) available";
                foreach (var w in _worldsCache)
                {
                    var card = MakeCard(w.name, string.IsNullOrEmpty(w.desc) ? "Serika Social World" : w.desc, w.cap, null, w.name);
                    card.Pressed += () => { Hide(); JoinWorldPressed?.Invoke(w.id); };
                    _contentGrid.AddChild(card);
                }
                return;

            case 3: // Social — friends & online players + your groups
                _statusLabel.Text = "Friends, online players and your groups.";
                _contentGrid.AddChild(InfoCard("Online Players", "See who's in your current world. Full friends list is coming soon."));
                _contentGrid.AddChild(InfoCard("Create a Group", "Groups you make and join will appear here. Coming soon."));
                return;

            case 4: // Groups — user-created groups
                _statusLabel.Text = "Community groups you can create and join.";
                _contentGrid.AddChild(InfoCard("Your Groups", "Groups you belong to appear here."));
                _contentGrid.AddChild(InfoCard("New Group", "Create a user group anyone can join. Coming soon."));
                return;

            default: // 5: Shop
                _statusLabel.Text = "";
                _contentGrid.AddChild(InfoCard("Shop", "Coming soon."));
                return;
        }
    }

    // Simple non-interactive info tile used by the Social / Groups / Shop tabs.
    private Control InfoCard(string title, string body)
    {
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(195, 140) };
        panel.AddThemeStyleboxOverride("panel", Brand.Panel(Brand.Bg2, 10, 1, Brand.BorderSoft));
        var pad = new MarginContainer();
        foreach (var s in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            pad.AddThemeConstantOverride(s, 12);
        panel.AddChild(pad);
        var vb = new VBoxContainer();
        vb.AddThemeConstantOverride("separation", 6);
        pad.AddChild(vb);
        var t = new Label { Text = title };
        t.AddThemeFontSizeOverride("font_size", 15);
        t.AddThemeColorOverride("font_color", Brand.TextHi);
        vb.AddChild(t);
        var b = new Label { Text = body, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        b.AddThemeFontSizeOverride("font_size", 12);
        b.AddThemeColorOverride("font_color", Brand.TextDim);
        vb.AddChild(b);
        return panel;
    }

    // Generic content card. Caller wires the Pressed handler. capacity < 0 hides the footer count.
    // A thumbnail is loaded from thumbUrl when available; otherwise a deterministic gradient
    // banner (seeded from gradientSeed) stands in so cards are never blank.
    private Button MakeCard(string name, string desc, int capacity, string thumbUrl = null, string gradientSeed = null)
    {
        var btn = new Button
        {
            // Tall enough for everything the card actually contains: 10 px padding, a 64 px
            // banner, title, a 36 px two-line description, the footer, and the separations
            // between them. At the previous 150 px the content overflowed by ~16 px and the
            // capacity badge rendered *outside* the card's bottom edge — a VBoxContainer
            // anchored to a too-short parent overflows rather than clipping or shrinking.
            CustomMinimumSize = new Vector2(195, 178),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };

        var normal = Brand.Panel(Brand.Bg2, 10, 1, Brand.BorderSoft);
        var hover = Brand.Panel(Brand.Bg3, 10, 1.5f, Brand.Accent);
        btn.AddThemeStyleboxOverride("normal", normal);
        btn.AddThemeStyleboxOverride("hover", hover);
        btn.AddThemeStyleboxOverride("pressed", normal);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 4);
        vbox.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        vbox.OffsetLeft = 10; vbox.OffsetTop = 10;
        vbox.OffsetRight = -10; vbox.OffsetBottom = -10;
        btn.AddChild(vbox);

        // Image / gradient banner.
        var banner = new PanelContainer { CustomMinimumSize = new Vector2(0, 64) };
        banner.MouseFilter = Control.MouseFilterEnum.Ignore;
        banner.AddThemeStyleboxOverride("panel", GradientBanner(gradientSeed ?? name));
        vbox.AddChild(banner);
        if (!string.IsNullOrEmpty(thumbUrl) && ImageLoader != null)
            _ = LoadThumb(banner, thumbUrl);

        var titleLbl = new Label { Text = name, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        titleLbl.AddThemeFontSizeOverride("font_size", 14);
        titleLbl.AddThemeColorOverride("font_color", Brand.TextHi);
        vbox.AddChild(titleLbl);

        var descLbl = new Label
        {
            Text = string.IsNullOrEmpty(desc) ? "Serika Social World" : desc,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(0, 36),
        };
        descLbl.AddThemeFontSizeOverride("font_size", 11);
        descLbl.AddThemeColorOverride("font_color", Brand.TextDim);
        vbox.AddChild(descLbl);

        vbox.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });

        if (capacity >= 0)
        {
            var footer = new HBoxContainer();
            footer.AddThemeConstantOverride("separation", 5);
            footer.AddChild(new TextureRect
            {
                Texture = Icons.Get(Icons.Kind.Users, 13, Brand.Accent),
                StretchMode = TextureRect.StretchModeEnum.KeepCentered,
            });
            var capLbl = new Label { Text = $"{capacity}" };
            capLbl.AddThemeFontSizeOverride("font_size", 11);
            capLbl.AddThemeColorOverride("font_color", Brand.Accent);
            footer.AddChild(capLbl);
            vbox.AddChild(footer);
        }

        btn.Text = "";
        return btn;
    }

    // Deterministic purple-family gradient so each card banner has a stable, distinct look.
    private static StyleBoxFlat GradientBanner(string seed)
    {
        int h = 0;
        foreach (char c in seed ?? "") h = h * 31 + c;
        float hue = 0.72f + (Math.Abs(h) % 60) / 600f; // narrow band around the brand purple
        var box = new StyleBoxFlat
        {
            BgColor = Color.FromHsv(hue, 0.55f, 0.55f),
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
        };
        return box;
    }

    private async System.Threading.Tasks.Task LoadThumb(PanelContainer banner, string url)
    {
        try
        {
            byte[] bytes = await ImageLoader(url);
            if (bytes == null || bytes.Length == 0 || !IsInstanceValid(banner)) return;
            var img = new Image();
            // Decide by content, not by extension: the thumbnail CDN serves WebP from a URL
            // that ends in "&q=85", so extension sniffing picked the wrong decoder and every
            // avatar card fell back to its placeholder gradient.
            Error err = img.LoadWebpFromBuffer(bytes);
            if (err != Error.Ok) err = img.LoadPngFromBuffer(bytes);
            if (err != Error.Ok) err = img.LoadJpgFromBuffer(bytes);
            if (err != Error.Ok) { GD.PrintErr($"main-menu thumb decode failed: {url}"); return; }
            var tex = ImageTexture.CreateFromImage(img);
            var rect = new TextureRect
            {
                Texture = tex,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            banner.AddChild(rect);
        }
        catch (Exception e) { GD.PrintErr($"main-menu thumb load failed: {e.Message}"); }
    }

    public void Open(string username, int tab = 1)
    {
        if (!string.IsNullOrEmpty(username)) _usernameLabel.Text = username;
        _scrim.Visible = true;
        _card.Visible = true;
        Visible = true;
        SwitchBottomTab(tab);
    }

    public new void Hide()
    {
        _scrim.Visible = false;
        _card.Visible = false;
        Visible = false;
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
