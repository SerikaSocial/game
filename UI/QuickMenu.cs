using System;
using System.Collections.Generic;
using Godot;

using SerikaSocial.UI;

namespace SerikaSocial;

/// The pause hub — what Esc opens. One clean card: who you are, who else is in the instance,
/// and a grid of the things you actually do (worlds, avatars, camera, video, settings, invite),
/// plus quick pills (home, respawn, emotes, quit).
///
/// This replaced a crowded mock-up: a non-functional banner carousel, a fake trust-token
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
    public event Action OpenSocial;
    public event Action ReportWorldPressed;
    /// A remote roster row was tapped — carries the account id + display name.
    public event Action<string, string> PlayerSelected;
    // Emotes are reached through the radial menu (the "Emotes" pill → OpenRadialMenu), so the
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
    private Button _reportWorldBtn;

    private double _clockTimer;

    public override void _Ready()
    {
        Layer = 105;
        Visible = false;

        _scrim = Brand.Scrim(0.72f);
        AddChild(_scrim);

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(center);

        _card = new PanelContainer { CustomMinimumSize = Brand.Card(560, 600) };
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
        _usernameLabel.AddThemeFontSizeOverride("font_size", Brand.Fs(16));
        _usernameLabel.AddThemeColorOverride("font_color", Brand.TextHi);
        header.AddChild(_usernameLabel);

        // Trust chip — the player's standing, which gates publishing worlds/avatars.
        var chipWrap = new PanelContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        chipWrap.AddThemeStyleboxOverride("panel", Brand.Panel(Brand.Bg2, 8, 1, Brand.BorderSoft));
        _trustChip = new Label { Text = "Visitor" };
        _trustChip.AddThemeFontSizeOverride("font_size", Brand.Fs(11));
        _trustChip.AddThemeColorOverride("font_color", Brand.AccentSoft);
        chipWrap.AddChild(_trustChip);
        header.AddChild(chipWrap);

        header.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        _clockLabel = new Label { Text = DateTime.Now.ToString("HH:mm") };
        _clockLabel.AddThemeFontSizeOverride("font_size", Brand.Fs(18));
        _clockLabel.AddThemeColorOverride("font_color", Brand.Accent);
        header.AddChild(_clockLabel);

        // Location + player count row.
        var subHeader = new HBoxContainer();
        subHeader.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(subHeader);
        _locationLabel = new Label { Text = "Home", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _locationLabel.AddThemeFontSizeOverride("font_size", Brand.Fs(13));
        _locationLabel.AddThemeColorOverride("font_color", Brand.TextMid);
        subHeader.AddChild(_locationLabel);
        subHeader.AddChild(new TextureRect
        {
            Texture = Icons.Get(Icons.Kind.Users, 14, Brand.TextDim),
            StretchMode = TextureRect.StretchModeEnum.KeepCentered,
        });
        _playerCountLabel = new Label { Text = "1" };
        _playerCountLabel.AddThemeFontSizeOverride("font_size", Brand.Fs(13));
        _playerCountLabel.AddThemeColorOverride("font_color", Brand.TextDim);
        subHeader.AddChild(_playerCountLabel);

        // Report the current world — the one moderation action that belongs on the hub
        // itself. Disabled in Home (no world to report).
        _reportWorldBtn = Brand.Ghost_(new Button
        {
            CustomMinimumSize = new Vector2(30, 26),
            TooltipText = "Report this world",
            Icon = Icons.Get(Icons.Kind.Flag, 14, Brand.TextDim),
        });
        _reportWorldBtn.Pressed += () => ReportWorldPressed?.Invoke();
        subHeader.AddChild(_reportWorldBtn);

        vbox.AddChild(new HSeparator());

        // ── Live player list ("more things" — who is actually in this instance) ──────────
        var listLbl = new Label { Text = "In this instance" };
        listLbl.AddThemeFontSizeOverride("font_size", Brand.Fs(12));
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

        grid.AddChild(GridCard(Icons.Kind.Globe, "Worlds", "Browse & travel", () => { Hide(); OpenMainMenuWorlds?.Invoke(); }));
        grid.AddChild(GridCard(Icons.Kind.Shirt, "Avatars", "Change your look", () => { Hide(); OpenMainMenuAvatars?.Invoke(); }));
        grid.AddChild(GridCard(Icons.Kind.Camera, "Camera", "Photo viewfinder", () => { Hide(); OpenCameraMenu?.Invoke(); }));
        grid.AddChild(GridCard(Icons.Kind.Screen, "Video", "Queue & watch", () => { Hide(); OpenVideoQueue?.Invoke(); }));
        grid.AddChild(GridCard(Icons.Kind.Gear, "Settings", "Graphics & controls", () => { Hide(); OpenSettings?.Invoke(); }));
        _inviteCard = GridCard(Icons.Kind.Link, "Invite", "Copy world link", () => CopyInvitePressed?.Invoke());
        grid.AddChild(_inviteCard);

        // ── Quick pills ─────────────────────────────────────────────────────────────────
        var pillRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        pillRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(pillRow);
        pillRow.AddChild(ActionPill(Icons.Kind.Home, "Home", () => { Hide(); HomePressed?.Invoke(); }));
        pillRow.AddChild(ActionPill(Icons.Kind.Refresh, "Respawn", () => { Hide(); RespawnPressed?.Invoke(); }));
        pillRow.AddChild(ActionPill(Icons.Kind.Smile, "Emotes", () => { Hide(); OpenRadialMenu?.Invoke(); }));
        pillRow.AddChild(ActionPill(Icons.Kind.Users, "Social", () => { Hide(); OpenSocial?.Invoke(); }));

        vbox.AddChild(new HSeparator());

        // ── Bottom bar: mic · quit · close ──────────────────────────────────────────────
        var bottom = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        bottom.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(bottom);

        _micBtn = Brand.Ghost_(new Button
        {
            Text = "Mic: off",
            CustomMinimumSize = new Vector2(140, 40),
            Icon = Icons.Get(Icons.Kind.Mic, 18, Brand.TextDim),
        });
        _micBtn.AddThemeConstantOverride("h_separation", 8);
        _micBtn.Pressed += () => MicTogglePressed?.Invoke();
        bottom.AddChild(_micBtn);

        bottom.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        var quitBtn = Brand.Ghost_(new Button
        {
            Text = "Quit",
            CustomMinimumSize = new Vector2(90, 40),
            Icon = Icons.Get(Icons.Kind.Power, 18, Brand.Danger),
        });
        quitBtn.AddThemeConstantOverride("h_separation", 8);
        quitBtn.AddThemeColorOverride("font_color", Brand.Danger);
        quitBtn.Pressed += () => QuitPressed?.Invoke();
        bottom.AddChild(quitBtn);

        var resumeBtn = Brand.Primary_(new Button { Text = "Resume (Esc)", CustomMinimumSize = new Vector2(140, 40) });
        resumeBtn.Pressed += Hide;
        bottom.AddChild(resumeBtn);
    }

    private static Button GridCard(Icons.Kind icon, string title, string subtitle, Action onClick)
    {
        var btn = new Button
        {
            Text = $"{title}\n{subtitle}",
            CustomMinimumSize = new Vector2(165, 66),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Icon = Icons.Get(icon, 22, Brand.Accent),
        };
        Brand.Ghost_(btn);
        btn.AddThemeFontSizeOverride("font_size", Brand.Fs(14));
        btn.AddThemeConstantOverride("h_separation", 10);
        btn.Pressed += onClick;
        return btn;
    }

    private static Button ActionPill(Icons.Kind icon, string label, Action onClick)
    {
        var btn = new Button
        {
            Text = label,
            CustomMinimumSize = new Vector2(120, 38),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Icon = Icons.Get(icon, 18, Brand.Accent),
        };
        Brand.Ghost_(btn);
        btn.AddThemeFontSizeOverride("font_size", Brand.Fs(13));
        btn.AddThemeConstantOverride("h_separation", 8);
        btn.Pressed += onClick;
        return btn;
    }

    // ── Data fed from Main ──────────────────────────────────────────────────────────────

    /// Set the world name + whether we're in a joinable world (controls the Invite and
    /// Report-world controls).
    public void SetLocation(string worldName, bool invitable)
    {
        _locationLabel.Text = string.IsNullOrEmpty(worldName) ? "Home" : worldName;
        if (_inviteCard != null) _inviteCard.Disabled = !invitable;
        if (_reportWorldBtn != null) _reportWorldBtn.Disabled = !invitable;
    }

    /// Repopulate the instance roster. `you` is highlighted; remote rows are buttons that
    /// fire `PlayerSelected` — a remote with no account id (relay edge case) renders as a
    /// plain, non-clickable row rather than a dead button.
    public void SetPlayers(string you, IReadOnlyList<(string userId, string name)> others)
    {
        if (_playerList == null) return;
        foreach (var c in _playerList.GetChildren()) c.QueueFree();

        int total = 1 + (others?.Count ?? 0);
        _playerCountLabel.Text = $"{total}";

        _playerList.AddChild(SelfRow(string.IsNullOrEmpty(you) ? "You" : $"{you}  (you)"));
        if (others == null) return;
        foreach (var (userId, name) in others)
        {
            if (string.IsNullOrEmpty(userId))
            {
                _playerList.AddChild(SelfRow(name));
                continue;
            }
            var btn = Brand.Ghost_(new Button
            {
                Text = $"  {name}",
                Alignment = HorizontalAlignment.Left,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 34),
                TooltipText = "Friend, block or report",
                Icon = Icons.Get(Icons.Kind.Person, 14, Brand.Success),
            });
            btn.AddThemeFontSizeOverride("font_size", Brand.Fs(13));
            btn.AddThemeColorOverride("font_color", Brand.TextMid);
            string id = userId, n = name;
            btn.Pressed += () => PlayerSelected?.Invoke(id, n);
            _playerList.AddChild(btn);
        }
    }

    private static HBoxContainer SelfRow(string name)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        var dot = new ColorRect
        {
            CustomMinimumSize = new Vector2(8, 8),
            Color = Brand.Accent,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        row.AddChild(dot);
        var lbl = new Label { Text = name };
        lbl.AddThemeFontSizeOverride("font_size", Brand.Fs(13));
        lbl.AddThemeColorOverride("font_color", Brand.TextHi);
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
        if (_micBtn == null) return;
        // Live mic is signalled by tinting the icon, not by swapping in a red dot emoji —
        // the tint carries at a glance and stays on-brand.
        _micBtn.Text = active ? "Mic: on" : "Mic: off";
        _micBtn.Icon = Icons.Get(Icons.Kind.Mic, 18, active ? Brand.Success : Brand.TextDim);
        _micBtn.AddThemeColorOverride("font_color", active ? Brand.TextHi : Brand.TextMid);
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
