using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

using SerikaSocial.UI;

namespace SerikaSocial;

/// The client's front-of-house UI: a polished login screen, a loading/connecting overlay with an
/// animated spinner and status line, an error card with Retry, and a Home panel with world list.
/// Built entirely in code so it has no scene/asset dependencies.
///
/// Main drives it: ShowLogin → SetStatus(…) as login/join progresses → Hide() once in-world,
/// or ShowError(…) on failure. ShowHome(…) for the personal Home with world browsing.
public partial class Hud : CanvasLayer
{
    public event Action LoginPressed;
    public event Action<string, string> EmailLoginPressed;
    public event Action RetryPressed;
    public event Action HomePressed;
    public event Action JoinCommonsPressed;
    public event Action<string> JoinWorldPressed;
    /// Fired when the player clicks Join in the world detail panel.
    public event Action<string> JoinWorldFromDetailPressed;

    private const string WorldsUrl = "https://social.serika.dev/worlds";
    public const string ClientVersion = "1.10.0";

    private ColorRect _scrim;
    private Control _loginScreen;
    private Label _title;
    private Label _subtitle;
    private Label _status;
    private LineEdit _emailInput;
    private LineEdit _passwordInput;
    private Button _emailLoginButton;
    private Button _browserLoginButton;
    private Button _retryButton;
    private Button _quitButton;
    private Control _spinner;
    private Label _toast;
    private Label _versionLabel;

    private Panel _homePanel;
    private Label _homeLabel;
    private GridContainer _worldListContainer;
    private Button _homeButton;

    // World detail panel
    private Panel _worldDetailPanel;
    private Label _detailName;
    private Label _detailDesc;
    private Label _detailStats;
    private VBoxContainer _detailInstanceList;
    private HFlowContainer _detailTagsRow;
    private TextureRect _detailBanner;
    private Button _detailJoinButton;
    private string _detailWorldId;

    private bool _spinning;

    // Store world data so we can show detail without re-fetching
    private readonly Dictionary<string, (string name, string description, int capacity, string author, string downloadUrl)> _worldCache = new();

    public override void _Ready()
    {
        Layer = 100;

        // Fullscreen gradient backdrop — no small card, the whole screen IS the login.
        // On a monitor the login screen IS the whole window, so a near-opaque full-bleed backdrop
        // is right. On the VR panel it is a two-metre slab hanging in the room, and painting 96%
        // of it flat dark leaves the form floating in the middle of a void with nothing framing
        // it. Let the world show through instead, and put the form on a real card below.
        bool vr = VrUiSurface.Active;
        _scrim = new ColorRect
        {
            // Fully transparent in VR: the card below is the entire login screen, floating in the
            // room. Any backdrop at all just re-adds the dark slab this is meant to remove.
            Color = new Color(Brand.Bg0.R, Brand.Bg0.G, Brand.Bg0.B, vr ? 0f : 0.96f),
            AnchorRight = 1,
            AnchorBottom = 1,
        };
        AddChild(_scrim);

        // Login screen container — fullscreen, content centered via anchors.
        _loginScreen = new Control
        {
            AnchorRight = 1,
            AnchorBottom = 1,
            Visible = true,
        };
        AddChild(_loginScreen);

        // Centered content panel (scales with viewport via anchor centering).
        var content = new VBoxContainer();
        // Tighter row spacing in VR. The login form is eleven rows tall — two fields, three
        // buttons, a title, a subtitle, a status line, a version stamp and two spacers — and at
        // 14 px of separation plus 34 px of card padding it came to 639 px against the panel's
        // 640, so the card's own rounded corners were cut off top and bottom by the panel edge.
        // Rows, not type: the font sizes are what make this readable at 1.7 m and none of them
        // change.
        content.AddThemeConstantOverride("separation", vr ? 8 : 14);

        if (vr)
        {
            // A bordered, rounded card, centred on the panel — the same shape every other screen
            // in this project uses (`Brand.Panel`). Anchoring the form directly to the middle of
            // the viewport, as the desktop layout does, gives it no edges at all, and on a
            // floating panel that reads as text dumped onto a dark rectangle.
            var centre = new CenterContainer();
            centre.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _loginScreen.AddChild(centre);

            // Large on purpose. The panel's logical space is 1000x640 (see `VrUiSurface`), and a
            // 620-wide card used barely a quarter of its width — legible on a monitor, tiny
            // through a headset lens. `Brand.Card` clamps this to what the panel can actually
            // contain, border and shadow included.
            var card = new PanelContainer { CustomMinimumSize = Brand.Card(940, 700) };
            card.AddThemeStyleboxOverride("panel", Brand.Panel(Brand.Bg1, 18, 1.5f, Brand.Border));
            centre.AddChild(card);

            var pad = new MarginContainer();
            foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
                pad.AddThemeConstantOverride(side, 24);
            card.AddChild(pad);

            content.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            pad.AddChild(content);
        }
        else
        {
            content.AnchorLeft = 0.5f;
            content.AnchorTop = 0.5f;
            content.AnchorRight = 0.5f;
            content.AnchorBottom = 0.5f;
            content.OffsetLeft = -260;
            content.OffsetTop = -280;
            content.OffsetRight = 260;
            content.OffsetBottom = 280;
            _loginScreen.AddChild(content);
        }

        _title = new Label { Text = "Serika Social", HorizontalAlignment = HorizontalAlignment.Center };
        _title.AddThemeFontSizeOverride("font_size", Brand.Fs(36));
        _title.AddThemeColorOverride("font_color", Brand.TextHi);
        content.AddChild(_title);

        _subtitle = new Label
        {
            Text = "Social VR for everyone",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _subtitle.AddThemeFontSizeOverride("font_size", Brand.Fs(16));
        _subtitle.AddThemeColorOverride("font_color", Brand.TextDim);
        content.AddChild(_subtitle);

        content.AddChild(new Control { CustomMinimumSize = new Vector2(0, 16) });

        // Email field
        _emailInput = new LineEdit
        {
            PlaceholderText = "Email",
            CustomMinimumSize = new Vector2(0, 44),
        };
        _emailInput.AddThemeFontSizeOverride("font_size", Brand.Fs(16));
        content.AddChild(_emailInput);

        // Password field
        _passwordInput = new LineEdit
        {
            PlaceholderText = "Password",
            Secret = true,
            CustomMinimumSize = new Vector2(0, 44),
        };
        _passwordInput.AddThemeFontSizeOverride("font_size", Brand.Fs(16));
        content.AddChild(_passwordInput);

        content.AddChild(new Control { CustomMinimumSize = new Vector2(0, 4) });

        // Email login button
        _emailLoginButton = MakeButton("Sign in", true);
        _emailLoginButton.Pressed += () =>
        {
            var email = _emailInput.Text.Trim();
            var pass = _passwordInput.Text;
            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(pass)) return;
            EmailLoginPressed?.Invoke(email, pass);
        };
        content.AddChild(_emailLoginButton);

        // Divider
        var dividerRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        dividerRow.AddThemeConstantOverride("separation", 8);
        content.AddChild(dividerRow);
        var dividerLabel = new Label { Text = "— or —" };
        dividerLabel.AddThemeFontSizeOverride("font_size", Brand.Fs(13));
        dividerLabel.AddThemeColorOverride("font_color", Brand.TextDim);
        dividerRow.AddChild(dividerLabel);

        // Browser login button
        _browserLoginButton = MakeButton("Sign in via browser", false);
        _browserLoginButton.Pressed += () => LoginPressed?.Invoke();
        content.AddChild(_browserLoginButton);

        // On desktop this pushes the status/quit block to the bottom of a full-screen layout. In
        // a fixed-height card it just opens a dead gap in the middle of the form.
        content.AddChild(vr
            ? new Control { CustomMinimumSize = new Vector2(0, 18) }
            : new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });

        _spinner = new Control { CustomMinimumSize = new Vector2(0, 48), Visible = false };
        _spinner.Draw += DrawSpinner;
        content.AddChild(_spinner);

        _status = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _status.AddThemeFontSizeOverride("font_size", Brand.Fs(15));
        _status.AddThemeColorOverride("font_color", new Color(0.7f, 0.74f, 0.82f));
        content.AddChild(_status);

        _retryButton = MakeButton("Retry", true);
        _retryButton.Visible = false;
        _retryButton.Pressed += () => RetryPressed?.Invoke();
        content.AddChild(_retryButton);

        _quitButton = MakeButton("Quit", false);
        _quitButton.Visible = false;
        _quitButton.Pressed += () => GetTree().Quit();
        content.AddChild(_quitButton);

        _versionLabel = new Label
        {
            Text = $"v{ClientVersion}  ·  © 2026 Serika.dev",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _versionLabel.AddThemeFontSizeOverride("font_size", Brand.Fs(12));
        _versionLabel.AddThemeColorOverride("font_color", Brand.TextDim);
        content.AddChild(_versionLabel);

        _toast = new Label
        {
            Visible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            AnchorLeft = 0,
            AnchorTop = 1,
            AnchorRight = 1,
            AnchorBottom = 1,
            OffsetTop = -52,
            OffsetBottom = -16,
        };
        _toast.AddThemeFontSizeOverride("font_size", Brand.Fs(15));
        _toast.AddThemeColorOverride("font_color", new Color(0.75f, 0.8f, 0.9f));
        AddChild(_toast);

        BuildHomePanel();
        BuildWorldDetailPanel();
        BuildHomeButton();
    }

    // A non-modal panel shown while in the personal Home: welcome + world list.
    private void BuildHomePanel()
    {
        // Sized through `Brand.Card` rather than as raw offsets, because this panel is the world
        // list — the screen the player spends the join flow looking at — and at 880x640 it was
        // taller than the VR panel's whole logical space, so its bottom row of actions ("Join The
        // Commons") sat under the panel edge.
        var homeSize = Brand.Card(880, 640) * 0.5f;
        _homePanel = new Panel
        {
            Visible = false,
            AnchorLeft = 0.5f,
            AnchorTop = 0.5f,
            AnchorRight = 0.5f,
            AnchorBottom = 0.5f,
            OffsetLeft = -homeSize.X,
            OffsetTop = -homeSize.Y,
            OffsetRight = homeSize.X,
            OffsetBottom = homeSize.Y,
        };
        _homePanel.AddThemeStyleboxOverride("panel", Brand.Panel(Brand.Bg1, 16));
        AddChild(_homePanel);

        var vbox = new VBoxContainer
        {
            AnchorRight = 1, AnchorBottom = 1,
            OffsetLeft = 24, OffsetTop = 24, OffsetRight = -24, OffsetBottom = -24,
        };
        vbox.AddThemeConstantOverride("separation", 12);
        _homePanel.AddChild(vbox);

        _homeLabel = new Label { Text = "Home", HorizontalAlignment = HorizontalAlignment.Center };
        _homeLabel.AddThemeFontSizeOverride("font_size", Brand.Fs(24));
        _homeLabel.AddThemeColorOverride("font_color", new Color(0.95f, 0.96f, 0.98f));
        vbox.AddChild(_homeLabel);

        var hint = new Label
        {
            Text = "You're in your private Home. Join a world below when you're ready.",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", Brand.Fs(13));
        hint.AddThemeColorOverride("font_color", new Color(0.5f, 0.55f, 0.62f));
        vbox.AddChild(hint);

        vbox.AddChild(new Control { CustomMinimumSize = new Vector2(0, 4) });

        // Scrollable world grid
        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, 240),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        scroll.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0) });
        vbox.AddChild(scroll);

        // Two columns on the VR panel, three on a monitor.
        //
        // The grid stretches its columns to the container width, so the column count is what sets
        // a world card's size — and a card is also the *click target*, aimed at from 1.7 m away
        // with a controller ray whose angular jitter is a degree or so. Three columns inside the
        // VR card give ~270 px each, which is 16° wide and carries a 60-character description in
        // 13 px type; two give ~410 px, and the same card is then a comfortable target and a
        // readable one. There is room for it: the panel is 61° across, wider than a desktop
        // monitor at arm's length, and this list previously left a third of its own height empty.
        _worldListContainer = new GridContainer
        {
            Columns = VrUiSurface.Active ? 2 : 3,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _worldListContainer.AddThemeConstantOverride("h_separation", 12);
        _worldListContainer.AddThemeConstantOverride("v_separation", 12);
        scroll.AddChild(_worldListContainer);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 10);
        vbox.AddChild(row);

        var join = MakeButton("Join The Commons", true);
        join.CustomMinimumSize = new Vector2(200, 42);
        join.Pressed += () => JoinCommonsPressed?.Invoke();
        row.AddChild(join);

        var browse = new Button { Text = "Browse worlds ↗", CustomMinimumSize = new Vector2(160, 42) };
        browse.Flat = true;
        browse.AddThemeColorOverride("font_color", new Color(0.65f, 0.7f, 0.82f));
        browse.AddThemeFontSizeOverride("font_size", Brand.Fs(14));
        browse.Pressed += () => OS.ShellOpen(WorldsUrl);
        row.AddChild(browse);

        // Close the overlay and return to walking around Home.
        var back = MakeButton("Stay home", false);
        back.CustomMinimumSize = new Vector2(140, 42);
        back.Pressed += () => WorldListClosed?.Invoke();
        row.AddChild(back);
    }

    // ── World Detail panel ──────────────────────────────────────────────────────────
    /// Fired when the world-list overlay is dismissed ("Stay home"), so Main can recapture input.
    public event Action WorldListClosed;

    /// Hide every HUD panel — the playable state (walking around Home or a world).
    public void HideAll()
    {
        // `false`, not `true`. On a monitor a visible layer with every child hidden draws
        // nothing, so this read as harmless — but in VR `VrUiSurface` decides whether to show
        // the whole floating panel (and light the laser pointer) from exactly this flag, so a
        // permanently-visible layer pinned a 2 m slab in front of the player for the entire
        // session. A layer that is showing nothing must say so.
        Visible = false;
        _scrim.Visible = false;
        _loginScreen.Visible = false;
        _homePanel.Visible = false;
        if (_homeButton != null) _homeButton.Visible = false;
        _worldDetailPanel.Visible = false;
        SetSpinning(false);
    }

    /// Show the world-list overlay on demand (from the pause menu). Non-forced — the player can
    /// dismiss it with "Stay home".
    public void ShowWorldList(string username)
    {
        Visible = true;
        _scrim.Visible = false;
        _loginScreen.Visible = false;
        if (_homeButton != null) _homeButton.Visible = false;
        _worldDetailPanel.Visible = false;
        _homeLabel.Text = $"Worlds — hi, {username}";
        _homePanel.Visible = true;
    }

    // A small button, top-left, to return to Home from a world.
    //
    // Desktop only. In VR it sat in the corner of the floating menu panel, which is not a screen
    // corner — it is a slab in the middle of the room — so it read as a stray button hanging in
    // space above whatever menu was open. Going home in VR is a pill in the pause hub
    // (`QuickMenu`), reachable from the menu button, which is where every other world action is.
    private void BuildHomeButton()
    {
        if (VrUiSurface.Active) return;

        _homeButton = new Button
        {
            Text = "⌂ Home",
            Visible = false,
            CustomMinimumSize = new Vector2(90, 34),
            OffsetLeft = 16, OffsetTop = 16, OffsetRight = 106, OffsetBottom = 50,
        };
        _homeButton.Pressed += () => HomePressed?.Invoke();
        AddChild(_homeButton);
    }

    private static Button MakeButton(string text, bool primary)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(0, 46) };
        return primary ? Brand.Primary_(b) : Brand.Ghost_(b);
    }

    // ── Public state transitions ─────────────────────────────────────────────────────

    /// The initial screen: a single Log in button.
    public void ShowLogin()
    {
        Visible = true;
        _scrim.Visible = true;
        _loginScreen.Visible = true;
        _emailInput.Visible = true;
        _passwordInput.Visible = true;
        _emailLoginButton.Visible = true;
        _browserLoginButton.Visible = true;
        _retryButton.Visible = false;
        _quitButton.Visible = true;
        _homePanel.Visible = false;
        if (_homeButton != null) _homeButton.Visible = false;
        _worldDetailPanel.Visible = false;
        SetSpinning(false);
        _subtitle.Visible = true;
        _status.Text = "";
    }

    /// Busy state: hide the button, show the spinner and a status line.
    public void SetStatus(string message)
    {
        Visible = true;
        _loginScreen.Visible = true;
        _emailInput.Visible = false;
        _passwordInput.Visible = false;
        _emailLoginButton.Visible = false;
        _browserLoginButton.Visible = false;
        _retryButton.Visible = false;
        _quitButton.Visible = false;
        _subtitle.Visible = false;
        _homePanel.Visible = false;
        if (_homeButton != null) _homeButton.Visible = false;
        _worldDetailPanel.Visible = false;
        SetSpinning(true);
        _status.AddThemeColorOverride("font_color", new Color(0.7f, 0.74f, 0.82f));
        _status.Text = message;
    }

    /// Failure state: red status + a Retry button.
    public void ShowError(string message)
    {
        Visible = true;
        _loginScreen.Visible = true;
        SetSpinning(false);
        _emailInput.Visible = true;
        _passwordInput.Visible = true;
        _emailLoginButton.Visible = true;
        _browserLoginButton.Visible = false;
        _retryButton.Visible = true;
        _quitButton.Visible = true;
        _subtitle.Visible = false;
        _status.AddThemeColorOverride("font_color", new Color(1f, 0.45f, 0.45f));
        _status.Text = message;
    }

    /// In-world: tear down the overlay, show the small Home button, leave a transient toast.
    public void HideWithToast(string toast)
    {
        _scrim.Visible = false;
        _loginScreen.Visible = false;
        _homePanel.Visible = false;
        if (_homeButton != null) _homeButton.Visible = true;
        _worldDetailPanel.Visible = false;
        SetSpinning(false);
        if (!string.IsNullOrEmpty(toast))
        {
            _toast.Text = toast;
            _toast.Visible = true;
            var t = GetTree().CreateTimer(4.0);
            t.Timeout += () => _toast.Visible = false;
        }
    }

    /// The personal Home: no modal scrim, a friendly panel with world list.
    public void ShowHome(string username)
    {
        Visible = true;
        _scrim.Visible = false;
        _loginScreen.Visible = false;
        if (_homeButton != null) _homeButton.Visible = false;
        _worldDetailPanel.Visible = false;
        _homeLabel.Text = $"Welcome, {username}";
        _homePanel.Visible = true;
    }

    /// Populate the world grid in the Home panel. Each entry is a card that opens world detail.
    public void SetWorlds(List<(string id, string name, string description, int capacity, string author, string downloadUrl)> worlds)
    {
        if (_worldListContainer == null) return;
        foreach (var child in _worldListContainer.GetChildren())
            child.QueueFree();
        _worldCache.Clear();

        if (worlds.Count == 0)
        {
            var empty = new Label
            {
                Text = "No worlds available yet. Check back later or visit social.serika.dev.",
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            empty.AddThemeColorOverride("font_color", new Color(0.5f, 0.55f, 0.62f));
            empty.AddThemeFontSizeOverride("font_size", Brand.Fs(13));
            _worldListContainer.AddChild(empty);
            return;
        }

        foreach (var w in worlds)
        {
            _worldCache[w.id] = (w.name, w.description, w.capacity, w.author, w.downloadUrl);
            var card = MakeWorldCard(w.id, w.name, w.description, w.capacity, w.author);
            _worldListContainer.AddChild(card);
        }
    }

    /// Deterministic hue from a seed string (same algorithm as the web frontend).
    private static float WorldHue(string seed)
    {
        float h = 262f;
        for (int i = 0; i < seed.Length; i++)
            h = (h + seed[i] * 7f) % 360f;
        return h;
    }

    private Button MakeWorldCard(string id, string name, string desc, int capacity, string author = null)
    {
        var btn = new Button
        {
            CustomMinimumSize = new Vector2(250, 130),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipContents = true,
        };

        // Gradient background using the world name as seed (matches web)
        float hue = WorldHue(name ?? id);
        float hue2 = (hue + 40f) % 360f;
        var col1 = Color.FromHsv(hue / 360f, 0.45f, 0.28f);
        var col2 = Color.FromHsv(hue2 / 360f, 0.4f, 0.22f);

        var normal = new StyleBoxFlat
        {
            BgColor = col1,
            CornerRadiusTopLeft = 12, CornerRadiusTopRight = 12,
            CornerRadiusBottomLeft = 12, CornerRadiusBottomRight = 12,
            BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
            BorderColor = new Color(1, 1, 1, 0.08f),
            ContentMarginLeft = 16, ContentMarginRight = 16,
            ContentMarginTop = 14, ContentMarginBottom = 14,
        };
        var hover = (StyleBoxFlat)normal.Duplicate();
        hover.BgColor = col1.Lerp(new Color(1, 1, 1), 0.08f);
        hover.BorderColor = new Color(0.55f, 0.35f, 0.8f, 0.6f);
        hover.BorderWidthTop = 2; hover.BorderWidthBottom = 2;
        hover.BorderWidthLeft = 2; hover.BorderWidthRight = 2;
        btn.AddThemeStyleboxOverride("normal", normal);
        btn.AddThemeStyleboxOverride("hover", hover);
        btn.AddThemeStyleboxOverride("pressed", normal);
        btn.AddThemeStyleboxOverride("focus", normal);

        // Use a VBoxContainer for the card content (name, desc, capacity)
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 6);
        vbox.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        vbox.OffsetLeft = 16; vbox.OffsetTop = 14;
        vbox.OffsetRight = -16; vbox.OffsetBottom = -14;
        btn.AddChild(vbox);

        var nameLabel = new Label
        {
            Text = name,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        };
        nameLabel.AddThemeFontSizeOverride("font_size", Brand.Fs(18));
        nameLabel.AddThemeColorOverride("font_color", new Color(0.95f, 0.95f, 0.98f));
        vbox.AddChild(nameLabel);

        var descLabel = new Label
        {
            Text = string.IsNullOrEmpty(desc) ? "" : (desc.Length > 60 ? desc[..60] + "…" : desc),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(0, 30),
        };
        descLabel.AddThemeFontSizeOverride("font_size", Brand.Fs(11));
        descLabel.AddThemeColorOverride("font_color", new Color(0.8f, 0.82f, 0.88f, 0.7f));
        vbox.AddChild(descLabel);

        vbox.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });

        var footer = new HBoxContainer();
        footer.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(footer);

        if (!string.IsNullOrEmpty(author))
        {
            var authorLabel = new Label { Text = $"by {author}" };
            authorLabel.AddThemeFontSizeOverride("font_size", Brand.Fs(10));
            authorLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.72f, 0.8f, 0.6f));
            footer.AddChild(authorLabel);
        }

        footer.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        var capLabel = new Label { Text = $"{capacity}" };
        capLabel.AddThemeFontSizeOverride("font_size", Brand.Fs(11));
        capLabel.AddThemeColorOverride("font_color", new Color(0.8f, 0.82f, 0.88f, 0.6f));
        footer.AddChild(capLabel);

        // Clicking opens detail panel instead of immediately joining
        btn.Pressed += () => JoinWorldPressed?.Invoke(id);

        // Clear the button's own text — content is rendered by children
        btn.Text = "";

        return btn;
    }

    private void SetSpinning(bool on)
    {
        _spinning = on;
        _spinner.Visible = on;
    }

    public override void _Process(double delta)
    {
        if (_spinning)
            _spinner.QueueRedraw();
    }

    private void DrawSpinner()
    {
        float w = _spinner.Size.X;
        var center = new Vector2(w * 0.5f, 24);
        float radius = 14f;
        int segments = 10;
        float t = (float)Time.GetTicksMsec() / 1000f;
        for (int i = 0; i < segments; i++)
        {
            float a = Mathf.Tau * i / segments + t * 3f;
            float alpha = (float)i / segments;
            var p = center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
            _spinner.DrawCircle(p, 2.2f, new Color(0.6f, 0.7f, 0.9f, alpha));
        }
    }
}
