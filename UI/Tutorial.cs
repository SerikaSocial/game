using System;
using Godot;

namespace SerikaSocial;

/// A one-time welcome tutorial shown the first time a player reaches Home. Walks through the
/// controls with Next/Skip buttons, then raises Completed. "Seen" is persisted to a marker file
/// under user:// so it never shows again (deletable to replay).
public partial class Tutorial : CanvasLayer
{
    private const string SeenPath = "user://tutorial_seen.flag";

    public event Action Completed;

    private static readonly (string title, string body)[] Steps =
    {
        ("Welcome to Serika Social", "A cosy corner of the metaverse. Let's cover the basics — it'll only take a moment."),
        ("Move around", "Walk with W A S D. Look with the mouse. Hold Shift to sprint, Space to jump, Ctrl to crouch."),
        ("Switch your view", "Press V any time to toggle between first-person and third-person. Third-person lets you admire your avatar."),
        ("Say hello", "Press T to open chat. Type a message and hit Enter — everyone in the world sees it, Minecraft-style."),
        ("See yourself", "There's a full-length mirror on the wall. Stand in front of it to check out your look."),
        ("Travel", "Walk into the glowing portal to visit the Commons — the shared space where you'll meet other people."),
    };

    private int _step;
    private Label _title;
    private Label _body;
    private Label _progress;
    private Button _next;

    public static bool AlreadySeen() => FileAccess.FileExists(SeenPath);

    public override void _Ready()
    {
        Layer = 120; // above every other overlay so its buttons are always clickable

        var dim = new ColorRect
        {
            Color = new Color(0, 0, 0, 0.55f),
            AnchorRight = 1, AnchorBottom = 1,
        };
        AddChild(dim);

        var panel = new PanelContainer
        {
            AnchorLeft = 0.5f, AnchorTop = 1, AnchorRight = 0.5f, AnchorBottom = 1,
            OffsetLeft = -320, OffsetRight = 320, OffsetTop = -260, OffsetBottom = -40,
        };
        var pstyle = new StyleBoxFlat
        {
            BgColor = new Color(0.09f, 0.10f, 0.14f, 0.97f),
            CornerRadiusTopLeft = 14, CornerRadiusTopRight = 14,
            CornerRadiusBottomLeft = 14, CornerRadiusBottomRight = 14,
            ContentMarginLeft = 28, ContentMarginRight = 28, ContentMarginTop = 22, ContentMarginBottom = 22,
        };
        panel.AddThemeStyleboxOverride("panel", pstyle);
        AddChild(panel);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 14);
        panel.AddChild(vbox);

        _title = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _title.AddThemeFontSizeOverride("font_size", 26);
        _title.AddThemeColorOverride("font_color", new Color(0.95f, 0.85f, 0.6f));
        vbox.AddChild(_title);

        _body = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(560, 90),
        };
        _body.AddThemeFontSizeOverride("font_size", 16);
        _body.AddThemeColorOverride("font_color", new Color(0.85f, 0.88f, 0.94f));
        vbox.AddChild(_body);

        _progress = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _progress.AddThemeColorOverride("font_color", new Color(0.5f, 0.55f, 0.62f));
        vbox.AddChild(_progress);

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 16);
        vbox.AddChild(buttons);

        var skip = new Button { Text = "Skip", CustomMinimumSize = new Vector2(120, 40) };
        skip.Pressed += Finish;
        buttons.AddChild(skip);

        _next = new Button { Text = "Next", CustomMinimumSize = new Vector2(160, 40) };
        _next.Pressed += Advance;
        buttons.AddChild(_next);

        Render();
    }

    private void Advance()
    {
        _step++;
        if (_step >= Steps.Length) { Finish(); return; }
        Render();
    }

    private void Render()
    {
        _title.Text = Steps[_step].title;
        _body.Text = Steps[_step].body;
        _progress.Text = $"{_step + 1} / {Steps.Length}";
        _next.Text = _step == Steps.Length - 1 ? "Let's go!" : "Next";
    }

    private void Finish()
    {
        using (var f = FileAccess.Open(SeenPath, FileAccess.ModeFlags.Write))
            f?.StoreString("seen");
        Completed?.Invoke();
        QueueFree();
    }
}
