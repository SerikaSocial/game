using System;
using Godot;

namespace SerikaSocial;

/// The client's front-of-house UI: a login screen, a loading/connecting overlay with an
/// animated spinner and status line, and an error card with Retry. Built entirely in code so
/// it has no scene/asset dependencies and matches the rest of the client's programmatic style.
///
/// Main drives it: ShowLogin → SetStatus(…) as login/join progresses → Hide() once in-world,
/// or ShowError(…) on failure.
public partial class Hud : CanvasLayer
{
    public event Action LoginPressed;
    public event Action RetryPressed;
    public event Action HomePressed;
    public event Action JoinCommonsPressed;

    private const string WorldsUrl = "https://social.serika.dev/worlds";

    private ColorRect _scrim;
    private Panel _card;
    private Label _title;
    private Label _subtitle;
    private Label _status;
    private Button _loginButton;
    private Button _retryButton;
    private Control _spinner;
    private Label _toast;

    private Panel _homePanel;
    private Label _homeLabel;
    private Button _homeButton; // small "⌂ Home" while in a world

    private bool _spinning;

    public override void _Ready()
    {
        Layer = 100;

        _scrim = new ColorRect
        {
            Color = new Color(0.05f, 0.06f, 0.09f, 1f),
            AnchorRight = 1,
            AnchorBottom = 1,
        };
        AddChild(_scrim);

        _card = new Panel
        {
            CustomMinimumSize = new Vector2(420, 300),
            AnchorLeft = 0.5f,
            AnchorTop = 0.5f,
            AnchorRight = 0.5f,
            AnchorBottom = 0.5f,
            OffsetLeft = -210,
            OffsetTop = -150,
            OffsetRight = 210,
            OffsetBottom = 150,
        };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.10f, 0.11f, 0.15f, 1f),
            CornerRadiusTopLeft = 16,
            CornerRadiusTopRight = 16,
            CornerRadiusBottomLeft = 16,
            CornerRadiusBottomRight = 16,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderColor = new Color(1, 1, 1, 0.08f),
        };
        _card.AddThemeStyleboxOverride("panel", style);
        AddChild(_card);

        var vbox = new VBoxContainer
        {
            AnchorRight = 1,
            AnchorBottom = 1,
            OffsetLeft = 32,
            OffsetTop = 32,
            OffsetRight = -32,
            OffsetBottom = -32,
        };
        vbox.AddThemeConstantOverride("separation", 14);
        _card.AddChild(vbox);

        _title = new Label { Text = "Serika Social", HorizontalAlignment = HorizontalAlignment.Center };
        _title.AddThemeFontSizeOverride("font_size", 32);
        _title.AddThemeColorOverride("font_color", new Color(0.9f, 0.95f, 1f));
        vbox.AddChild(_title);

        _subtitle = new Label
        {
            Text = "A social VR world",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _subtitle.AddThemeColorOverride("font_color", new Color(0.6f, 0.65f, 0.75f));
        vbox.AddChild(_subtitle);

        vbox.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });

        _spinner = new Control { CustomMinimumSize = new Vector2(0, 48), Visible = false };
        _spinner.Draw += DrawSpinner;
        vbox.AddChild(_spinner);

        _status = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _status.AddThemeColorOverride("font_color", new Color(0.75f, 0.8f, 0.9f));
        vbox.AddChild(_status);

        vbox.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });

        _loginButton = MakeButton("Log in with Serika");
        _loginButton.Pressed += () => LoginPressed?.Invoke();
        vbox.AddChild(_loginButton);

        _retryButton = MakeButton("Retry");
        _retryButton.Visible = false;
        _retryButton.Pressed += () => RetryPressed?.Invoke();
        vbox.AddChild(_retryButton);

        _toast = new Label
        {
            Visible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            AnchorLeft = 0,
            AnchorTop = 1,
            AnchorRight = 1,
            AnchorBottom = 1,
            OffsetTop = -48,
            OffsetBottom = -16,
        };
        _toast.AddThemeColorOverride("font_color", new Color(0.8f, 0.85f, 0.95f));
        AddChild(_toast);

        BuildHomePanel();
        BuildHomeButton();
    }

    // A non-modal panel shown while in the personal Home: welcome + ways to go multiplayer.
    private void BuildHomePanel()
    {
        _homePanel = new Panel
        {
            Visible = false,
            AnchorLeft = 0.5f,
            AnchorTop = 1,
            AnchorRight = 0.5f,
            AnchorBottom = 1,
            OffsetLeft = -260,
            OffsetTop = -140,
            OffsetRight = 260,
            OffsetBottom = -24,
        };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.10f, 0.11f, 0.15f, 0.92f),
            CornerRadiusTopLeft = 14, CornerRadiusTopRight = 14,
            CornerRadiusBottomLeft = 14, CornerRadiusBottomRight = 14,
            BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
            BorderColor = new Color(1, 1, 1, 0.08f),
        };
        _homePanel.AddThemeStyleboxOverride("panel", style);
        AddChild(_homePanel);

        var vbox = new VBoxContainer
        {
            AnchorRight = 1, AnchorBottom = 1,
            OffsetLeft = 20, OffsetTop = 16, OffsetRight = -20, OffsetBottom = -16,
        };
        vbox.AddThemeConstantOverride("separation", 10);
        _homePanel.AddChild(vbox);

        _homeLabel = new Label { Text = "Welcome home", HorizontalAlignment = HorizontalAlignment.Center };
        _homeLabel.AddThemeFontSizeOverride("font_size", 18);
        _homeLabel.AddThemeColorOverride("font_color", new Color(0.9f, 0.95f, 1f));
        vbox.AddChild(_homeLabel);

        var hint = new Label
        {
            Text = "You're in your private Home. Jump into a world when you're ready.",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeColorOverride("font_color", new Color(0.6f, 0.65f, 0.75f));
        vbox.AddChild(hint);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 10);
        vbox.AddChild(row);

        var join = MakeButton("Join The Commons");
        join.CustomMinimumSize = new Vector2(190, 40);
        join.Pressed += () => JoinCommonsPressed?.Invoke();
        row.AddChild(join);

        var browse = new Button { Text = "Browse worlds ↗", CustomMinimumSize = new Vector2(150, 40) };
        browse.Flat = true;
        browse.AddThemeColorOverride("font_color", new Color(0.7f, 0.75f, 0.9f));
        browse.Pressed += () => OS.ShellOpen(WorldsUrl);
        row.AddChild(browse);
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

    private static Button MakeButton(string text)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(0, 44) };
        var normal = new StyleBoxFlat
        {
            BgColor = new Color(0.35f, 0.5f, 0.95f),
            CornerRadiusTopLeft = 10,
            CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10,
            CornerRadiusBottomRight = 10,
            ContentMarginTop = 10,
            ContentMarginBottom = 10,
        };
        var hover = (StyleBoxFlat)normal.Duplicate();
        hover.BgColor = new Color(0.42f, 0.57f, 1f);
        b.AddThemeStyleboxOverride("normal", normal);
        b.AddThemeStyleboxOverride("hover", hover);
        b.AddThemeStyleboxOverride("pressed", normal);
        b.AddThemeColorOverride("font_color", Colors.White);
        b.AddThemeFontSizeOverride("font_size", 16);
        return b;
    }

    // ── Public state transitions ─────────────────────────────────────────────────────

    /// The initial screen: a single Log in button.
    public void ShowLogin()
    {
        Visible = true;
        _scrim.Visible = true;
        _card.Visible = true;
        _loginButton.Visible = true;
        _retryButton.Visible = false;
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
        _card.Visible = true;
        _loginButton.Visible = false;
        _retryButton.Visible = false;
        _subtitle.Visible = false;
        _homePanel.Visible = false;
        _homeButton.Visible = false;
        SetSpinning(true);
        _status.AddThemeColorOverride("font_color", new Color(0.75f, 0.8f, 0.9f));
        _status.Text = message;
    }

    /// Failure state: red status + a Retry button.
    public void ShowError(string message)
    {
        Visible = true;
        _card.Visible = true;
        SetSpinning(false);
        _loginButton.Visible = false;
        _retryButton.Visible = true;
        _subtitle.Visible = false;
        _status.AddThemeColorOverride("font_color", new Color(1f, 0.5f, 0.5f));
        _status.Text = message;
    }

    /// In-world: tear down the overlay, show the small Home button, leave a transient toast.
    public void HideWithToast(string toast)
    {
        _scrim.Visible = false;
        _card.Visible = false;
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

    /// The personal Home: no modal scrim, a friendly non-modal panel with ways out.
    public void ShowHome(string username)
    {
        Visible = true;
        _scrim.Visible = false;
        _card.Visible = false;
        _homeButton.Visible = false;
        SetSpinning(false);
        _homeLabel.Text = $"Welcome home, {username}";
        _homePanel.Visible = true;
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
        float radius = 16f;
        int segments = 12;
        float t = (float)Time.GetTicksMsec() / 1000f;
        for (int i = 0; i < segments; i++)
        {
            float a = Mathf.Tau * i / segments + t * 4f;
            float alpha = (float)i / segments;
            var p = center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
            _spinner.DrawCircle(p, 2.5f, new Color(0.5f, 0.65f, 1f, alpha));
        }
    }
}
