using System;
using System.Collections.Generic;
using Godot;

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

    private const string WorldsUrl = "https://social.serika.dev/worlds";
    public const string ClientVersion = "0.3.3";

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
    private VBoxContainer _worldListContainer;
    private Button _homeButton;

    private bool _spinning;

    public override void _Ready()
    {
        Layer = 100;

        // Fullscreen gradient backdrop — no small card, the whole screen IS the login.
        _scrim = new ColorRect
        {
            Color = new Color(Brand.Bg0.R, Brand.Bg0.G, Brand.Bg0.B, 0.96f),
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
        var content = new VBoxContainer
        {
            AnchorLeft = 0.5f,
            AnchorTop = 0.5f,
            AnchorRight = 0.5f,
            AnchorBottom = 0.5f,
            OffsetLeft = -260,
            OffsetTop = -280,
            OffsetRight = 260,
            OffsetBottom = 280,
        };
        content.AddThemeConstantOverride("separation", 14);
        _loginScreen.AddChild(content);

        _title = new Label { Text = "Serika Social", HorizontalAlignment = HorizontalAlignment.Center };
        _title.AddThemeFontSizeOverride("font_size", 36);
        _title.AddThemeColorOverride("font_color", Brand.TextHi);
        content.AddChild(_title);

        _subtitle = new Label
        {
            Text = "Social VR for everyone",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _subtitle.AddThemeFontSizeOverride("font_size", 16);
        _subtitle.AddThemeColorOverride("font_color", Brand.TextDim);
        content.AddChild(_subtitle);

        content.AddChild(new Control { CustomMinimumSize = new Vector2(0, 16) });

        // Email field
        _emailInput = new LineEdit
        {
            PlaceholderText = "Email",
            CustomMinimumSize = new Vector2(0, 44),
        };
        _emailInput.AddThemeFontSizeOverride("font_size", 16);
        content.AddChild(_emailInput);

        // Password field
        _passwordInput = new LineEdit
        {
            PlaceholderText = "Password",
            Secret = true,
            CustomMinimumSize = new Vector2(0, 44),
        };
        _passwordInput.AddThemeFontSizeOverride("font_size", 16);
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
        dividerLabel.AddThemeFontSizeOverride("font_size", 13);
        dividerLabel.AddThemeColorOverride("font_color", Brand.TextDim);
        dividerRow.AddChild(dividerLabel);

        // Browser login button
        _browserLoginButton = MakeButton("Sign in via browser", false);
        _browserLoginButton.Pressed += () => LoginPressed?.Invoke();
        content.AddChild(_browserLoginButton);

        content.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });

        _spinner = new Control { CustomMinimumSize = new Vector2(0, 48), Visible = false };
        _spinner.Draw += DrawSpinner;
        content.AddChild(_spinner);

        _status = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _status.AddThemeFontSizeOverride("font_size", 15);
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
        _versionLabel.AddThemeFontSizeOverride("font_size", 12);
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
        _toast.AddThemeFontSizeOverride("font_size", 15);
        _toast.AddThemeColorOverride("font_color", new Color(0.75f, 0.8f, 0.9f));
        AddChild(_toast);

        BuildHomePanel();
        BuildHomeButton();
    }

    // A non-modal panel shown while in the personal Home: welcome + world list.
    private void BuildHomePanel()
    {
        _homePanel = new Panel
        {
            Visible = false,
            AnchorLeft = 0.5f,
            AnchorTop = 0.5f,
            AnchorRight = 0.5f,
            AnchorBottom = 0.5f,
            OffsetLeft = -340,
            OffsetTop = -260,
            OffsetRight = 340,
            OffsetBottom = 260,
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
        _homeLabel.AddThemeFontSizeOverride("font_size", 24);
        _homeLabel.AddThemeColorOverride("font_color", new Color(0.95f, 0.96f, 0.98f));
        vbox.AddChild(_homeLabel);

        var hint = new Label
        {
            Text = "You're in your private Home. Join a world below when you're ready.",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", 13);
        hint.AddThemeColorOverride("font_color", new Color(0.5f, 0.55f, 0.62f));
        vbox.AddChild(hint);

        vbox.AddChild(new Control { CustomMinimumSize = new Vector2(0, 4) });

        // Scrollable world list
        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, 200),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        scroll.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0) });
        vbox.AddChild(scroll);

        _worldListContainer = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _worldListContainer.AddThemeConstantOverride("separation", 8);
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
        browse.AddThemeFontSizeOverride("font_size", 14);
        browse.Pressed += () => OS.ShellOpen(WorldsUrl);
        row.AddChild(browse);

        // Close the overlay and return to walking around Home.
        var back = MakeButton("Stay home", false);
        back.CustomMinimumSize = new Vector2(140, 42);
        back.Pressed += () => WorldListClosed?.Invoke();
        row.AddChild(back);
    }

    /// Fired when the world-list overlay is dismissed ("Stay home"), so Main can recapture input.
    public event Action WorldListClosed;

    /// Hide every HUD panel — the playable state (walking around Home or a world).
    public void HideAll()
    {
        Visible = true;
        _scrim.Visible = false;
        _loginScreen.Visible = false;
        _homePanel.Visible = false;
        _homeButton.Visible = false;
        SetSpinning(false);
    }

    /// Show the world-list overlay on demand (from the pause menu). Non-forced — the player can
    /// dismiss it with "Stay home".
    public void ShowWorldList(string username)
    {
        Visible = true;
        _scrim.Visible = false;
        _loginScreen.Visible = false;
        _homeButton.Visible = false;
        _homeLabel.Text = $"Worlds — hi, {username}";
        _homePanel.Visible = true;
    }

    // A small button, top-left, to return to Home from a world.
    private void BuildHomeButton()
    {
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

    private static Button MakeWorldButton(string name, string desc, int capacity, string author = null)
    {
        var btn = new Button
        {
            CustomMinimumSize = new Vector2(0, 64),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        var normal = new StyleBoxFlat
        {
            BgColor = new Color(0.12f, 0.13f, 0.16f),
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
            BorderColor = new Color(1, 1, 1, 0.05f),
            ContentMarginLeft = 16, ContentMarginRight = 16,
            ContentMarginTop = 12, ContentMarginBottom = 12,
        };
        var hover = (StyleBoxFlat)normal.Duplicate();
        hover.BgColor = new Color(0.16f, 0.17f, 0.21f);
        hover.BorderColor = new Color(1, 1, 1, 0.12f);
        btn.AddThemeStyleboxOverride("normal", normal);
        btn.AddThemeStyleboxOverride("hover", hover);
        btn.AddThemeStyleboxOverride("pressed", normal);
        btn.AddThemeColorOverride("font_color", new Color(0.9f, 0.92f, 0.95f));
        btn.AddThemeFontSizeOverride("font_size", 15);
        btn.Text = author != null
            ? $"{name}  by {author}  [{capacity}]"
            : $"{name}    [capacity: {capacity}]";
        btn.TooltipText = desc;
        return btn;
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
        _homeButton.Visible = false;
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
        _homeButton.Visible = false;
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
        _homeButton.Visible = true;
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
        _homeButton.Visible = false;
        SetSpinning(false);
        _homeLabel.Text = $"Welcome home, {username}";
        _homePanel.Visible = true;
    }

    /// Populate the world list in the Home panel. Each entry is a button that fires JoinWorldPressed.
    public void SetWorlds(List<(string id, string name, string description, int capacity, string author, string downloadUrl)> worlds)
    {
        if (_worldListContainer == null) return;
        foreach (var child in _worldListContainer.GetChildren())
            child.QueueFree();

        if (worlds.Count == 0)
        {
            var empty = new Label
            {
                Text = "No worlds available right now.",
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            empty.AddThemeColorOverride("font_color", new Color(0.5f, 0.55f, 0.62f));
            empty.AddThemeFontSizeOverride("font_size", 13);
            _worldListContainer.AddChild(empty);
            return;
        }

        foreach (var w in worlds)
        {
            var btn = MakeWorldButton(w.name, w.description, w.capacity, w.author);
            var id = w.id;
            btn.Pressed += () => JoinWorldPressed?.Invoke(id);
            _worldListContainer.AddChild(btn);
        }
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
