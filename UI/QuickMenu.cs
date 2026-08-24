using System;
using System.Collections.Generic;
using Godot;

namespace SerikaSocial;

/// The pause hub — what Esc opens. One clean card: who you are, who else is in the instance,
/// and a grid of the things you actually do (worlds, avatars, camera, video, settings, invite),
/// plus quick pills (home, respawn, emotes, quit).
///
/// This replaced a crowded mock-up: a non-functional banner carousel, a fake "V ✦✦✦✦" token
/// badge, and a 2×3 grid where four of six tiles ("Live Now", "Worlds", "Social", "Groups") all
/// routed to the same world list — visual noise pretending to be features. The redundant, never-
/// opened `PauseMenu` was deleted at the same time; this is now the only pause surface.
public partial class QuickMenu : CanvasLayer
{
    public event Action Closed;
    public event Action HomePressed;
    public event Action RespawnPressed;
    public event Action QuitPressed;
    public event Action OpenMainMenuWorlds;
    public event Action OpenMainMenuAvatars;
    public event Action OpenCameraMenu;
    public event Action OpenRadialMenu;
    public event Action OpenVideoQueue;
    public event Action OpenSettings;
    public event Action CopyInvitePressed;
    public event Action MicTogglePressed;
    // Emotes are reached through the radial menu (the "😀 Emotes" pill → OpenRadialMenu), so the
    // hub no longer carries its own emote shortcuts.

    private ColorRect _scrim;
    private PanelContainer _card;
    private Label _clockLabel;
    private Label _usernameLabel;
    private Label _trustChip;
    private Label _playerCountLabel;
    private Label _locationLabel;
    private VBoxContainer _playerList;
    private Button _micBtn;
    private Button _inviteCard;

    private double _clockTimer;

    public override void _Ready()
    {
        Layer = 105;
        Visible = false;

        _scrim = new ColorRect
        {
            Color = new Color(0.02f, 0.03f, 0.05f, 0.72f),
            AnchorRight = 1, AnchorBottom = 1,
        };
        AddChild(_scrim);

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(center);

        _card = new PanelContainer { CustomMinimumSize = new Vector2(560, 600) };
        _card.AddThemeStyleboxOverride("panel", Brand.Panel(Brand.Bg1, 18, 1.5f, Brand.Border));
        center.AddChild(_card);

        var margin = new MarginContainer();
        foreach (var s in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(s, 18);
        _card.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 12);
        margin.AddChild(vbox);

        // ── Header: identity · location · clock ─────────────────────────────────────────
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(header);

        var dot = new ColorRect { CustomMinimumSize = new Vector2(9, 9), Color = Brand.Success, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        header.AddChild(dot);
        _usernameLabel = new Label { Text = "Serika User" };
        _usernameLabel.AddThemeFontSizeOverride("font_size", 16);
        _usernameLabel.AddThemeColorOverride("font_color", Brand.TextHi);
        header.AddChild(_usernameLabel);

        // Trust chip — the player's standing, which gates publishing worlds/avatars.
        var chipWrap = new PanelContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        chipWrap.AddThemeStyleboxOverride("panel", Brand.Panel(Brand.Bg2, 8, 1, Brand.BorderSoft));
        _trustChip = new Label { Text = "Visitor" };
        _trustChip.AddThemeFontSizeOverride("font_size", 11);
        _trustChip.AddThemeColorOverride("font_color", Brand.AccentSoft);
        chipWrap.AddChild(_trustChip);
        header.AddChild(chipWrap);

        header.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        _clockLabel = new Label { Text = DateTime.Now.ToString("HH:mm") };
        _clockLabel.AddThemeFontSizeOverride("font_size", 18);
        _clockLabel.AddThemeColorOverride("font_color", Brand.Accent);
        header.AddChild(_clockLabel);

        // Location + player count row.
        var subHeader = new HBoxContainer();
        subHeader.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(subHeader);
        _locationLabel = new Label { Text = "Home", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _locationLabel.AddThemeFontSizeOverride("font_size", 13);
        _locationLabel.AddThemeColorOverride("font_color", Brand.TextMid);
        subHeader.AddChild(_locationLabel);
        _playerCountLabel = new Label { Text = "👥 1" };
        _playerCountLabel.AddThemeFontSizeOverride("font_size", 13);
        _playerCountLabel.AddThemeColorOverride("font_color", Brand.TextDim);
        subHeader.AddChild(_playerCountLabel);

        vbox.AddChild(new HSeparator());

        // ── Live player list ("more things" — who is actually in this instance) ──────────
        var listLbl = new Label { Text = "In this instance" };
        listLbl.AddThemeFontSizeOverride("font_size", 12);
        listLbl.AddThemeColorOverride("font_color", Brand.TextDim);
        vbox.AddChild(listLbl);

        var listScroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, 120),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        listScroll.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        vbox.AddChild(listScroll);
        _playerList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _playerList.AddThemeConstantOverride("separation", 4);
        listScroll.AddChild(_playerList);

        vbox.AddChild(new HSeparator());

        // ── Action grid — every tile does something distinct ────────────────────────────
        var grid = new GridContainer { Columns = 3, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 10);
        vbox.AddChild(grid);

        grid.AddChild(GridCard("🌐 Worlds", "Browse & travel", () => { Hide(); OpenMainMenuWorlds?.Invoke(); }));
        grid.AddChild(GridCard("👕 Avatars", "Change your look", () => { Hide(); OpenMainMenuAvatars?.Invoke(); }));
        grid.AddChild(GridCard("📷 Camera", "Photo viewfinder", () => { Hide(); OpenCameraMenu?.Invoke(); }));
        grid.AddChild(GridCard("📺 Video", "Queue & watch", () => { Hide(); OpenVideoQueue?.Invoke(); }));
        grid.AddChild(GridCard("⚙️ Settings", "Graphics & controls", () => { Hide(); OpenSettings?.Invoke(); }));
        _inviteCard = GridCard("🔗 Invite", "Copy world link", () => CopyInvitePressed?.Invoke());
        grid.AddChild(_inviteCard);

        // ── Quick pills ─────────────────────────────────────────────────────────────────
        var pillRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        pillRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(pillRow);
        pillRow.AddChild(ActionPill("🏠 Home", () => { Hide(); HomePressed?.Invoke(); }));
        pillRow.AddChild(ActionPill("⟲ Respawn", () => { Hide(); RespawnPressed?.Invoke(); }));
        pillRow.AddChild(ActionPill("😀 Emotes", () => { Hide(); OpenRadialMenu?.Invoke(); }));

        vbox.AddChild(new HSeparator());

        // ── Bottom bar: mic · quit · close ──────────────────────────────────────────────
        var bottom = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        bottom.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(bottom);

        _micBtn = Brand.Ghost_(new Button { Text = "🎙 Mic: off", CustomMinimumSize = new Vector2(140, 40) });
        _micBtn.Pressed += () => MicTogglePressed?.Invoke();
        bottom.AddChild(_micBtn);

        bottom.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        var quitBtn = Brand.Ghost_(new Button { Text = "🚪 Quit", CustomMinimumSize = new Vector2(90, 40) });
        quitBtn.AddThemeColorOverride("font_color", Brand.Danger);
        quitBtn.Pressed += () => QuitPressed?.Invoke();
        bottom.AddChild(quitBtn);

        var resumeBtn = Brand.Primary_(new Button { Text = "Resume (Esc)", CustomMinimumSize = new Vector2(140, 40) });
        resumeBtn.Pressed += Hide;
        bottom.AddChild(resumeBtn);
    }

    private static Button GridCard(string title, string subtitle, Action onClick)
    {
        var btn = new Button
        {
            Text = $"{title}\n{subtitle}",
            CustomMinimumSize = new Vector2(165, 66),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        Brand.Ghost_(btn);
        btn.AddThemeFontSizeOverride("font_size", 14);
        btn.Pressed += onClick;
        return btn;
    }

    private static Button ActionPill(string label, Action onClick)
    {
        var btn = new Button { Text = label, CustomMinimumSize = new Vector2(120, 38), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        Brand.Ghost_(btn);
        btn.AddThemeFontSizeOverride("font_size", 13);
        btn.Pressed += onClick;
        return btn;
    }

    // ── Data fed from Main ──────────────────────────────────────────────────────────────

    /// Set the world name + whether we're in a joinable world (controls the Invite tile).
    public void SetLocation(string worldName, bool invitable)
    {
        _locationLabel.Text = string.IsNullOrEmpty(worldName) ? "Home" : worldName;
        if (_inviteCard != null) _inviteCard.Disabled = !invitable;
    }

    /// Repopulate the instance roster. `you` is highlighted; the rest are remote peers.
    public void SetPlayers(string you, IReadOnlyList<string> others)
    {
        if (_playerList == null) return;
        foreach (var c in _playerList.GetChildren()) c.QueueFree();

        int total = 1 + (others?.Count ?? 0);
        _playerCountLabel.Text = $"👥 {total}";

        _playerList.AddChild(PlayerRow(string.IsNullOrEmpty(you) ? "You" : $"{you}  (you)", true));
        if (others != null)
            foreach (var name in others)
                _playerList.AddChild(PlayerRow(name, false));
    }

    private static HBoxContainer PlayerRow(string name, bool self)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        var dot = new ColorRect
        {
            CustomMinimumSize = new Vector2(8, 8),
            Color = self ? Brand.Accent : Brand.Success,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        row.AddChild(dot);
        var lbl = new Label { Text = name };
        lbl.AddThemeFontSizeOverride("font_size", 13);
        lbl.AddThemeColorOverride("font_color", self ? Brand.TextHi : Brand.TextMid);
        row.AddChild(lbl);
        return row;
    }

    /// Show the player's trust standing (e.g. "Trusted"). Gates publishing server-side.
    public void SetTrust(string label)
    {
        if (_trustChip != null) _trustChip.Text = string.IsNullOrEmpty(label) ? "Visitor" : label;
    }

    /// Reflect the real mic state (owned by Main), so the button never lies about it.
    public void SetMic(bool active)
    {
        if (_micBtn != null) _micBtn.Text = active ? "🔴 Mic: on" : "🎙 Mic: off";
    }

    public void Open(string username)
    {
        if (!string.IsNullOrEmpty(username)) _usernameLabel.Text = username;
        _scrim.Visible = true;
        _card.Visible = true;
        Visible = true;
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
