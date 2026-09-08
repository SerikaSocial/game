using System;
using System.Collections.Generic;
using Godot;
using SerikaSocial.UI;

namespace SerikaSocial;

/// In-world controls, current location and the actual instance roster.
public partial class QuickMenu : CanvasLayer
{
    public UI.EventBanner EventsBanner { get; private set; }
    public event Action AdminEventsPressed;
    private Button _eventAdminButton;
    public void SetEventAdmin(bool enabled) { if (_eventAdminButton != null) _eventAdminButton.Visible = enabled; }
    public event Action Closed;
    public event Action HomePressed;
    public event Action SetHomePressed;
    public event Action ResetHomePressed;
    public event Action NewPrivateInstancePressed;
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
    public event Action<string, string> PlayerSelected;

    private PanelContainer _card;
    private Label _clockLabel, _usernameLabel, _trustChip, _playerCountLabel;
    private Label _locationLabel, _privacyLabel, _worldStatus;
    private VBoxContainer _playerList;
    private GridContainer _actions;
    private Button _micBtn, _inviteCard, _reportWorldBtn, _socialPill, _resumeBtn;
    private Button _setHomeBtn, _resetHomeBtn, _privateBtn;
    private GridContainer _worldActions;
    private int _notificationCount;
    private bool _homeResetAvailable, _worldBusy;
    private double _clockTimer;

    public override void _Ready()
    {
        Layer = 105;
        Visible = false;
        AddChild(Brand.Scrim(0.64f));
        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(center);
        _card = new PanelContainer { Name = "QuickMenuCard", Theme = Brand.Theme };
        _card.AddThemeStyleboxOverride("panel", Brand.Panel(Brand.Bg1, 18, 1, Brand.Border));
        center.AddChild(_card);
        var margin = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (var side in new[] { "left", "right", "top", "bottom" })
            margin.AddThemeConstantOverride("margin_" + side, 20);
        _card.AddChild(margin);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        margin.AddChild(column);

        EventsBanner = new UI.EventBanner { Name = "EventsBanner", Visible = false };
        column.AddChild(EventsBanner);
        _eventAdminButton = Brand.Ghost_(new Button { Text = "Admin · Events", Visible = false, CustomMinimumSize = new Vector2(0, 40) });
        _eventAdminButton.Pressed += () => AdminEventsPressed?.Invoke();
        column.AddChild(_eventAdminButton);

        var header = Row();
        column.AddChild(header);
        var identity = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        identity.AddThemeConstantOverride("separation", 2);
        identity.AddChild(Label("QUICK MENU", 11, Brand.Accent));
        _usernameLabel = Label("Serika User", 19, Brand.TextHi);
        _usernameLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        identity.AddChild(_usernameLabel);
        header.AddChild(identity);
        _trustChip = Label("Visitor", 12, Brand.TextDim);
        header.AddChild(_trustChip);
        _clockLabel = Label(DateTime.Now.ToString("HH:mm"), 15, Brand.TextMid);
        header.AddChild(_clockLabel);

        // Keep identity and Resume visible while long rosters or a small window scroll.
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            FollowFocus = true,
        };
        column.AddChild(scroll);
        var body = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(body);

        var location = new PanelContainer();
        var locationStyle = Brand.Panel(Brand.Bg0, 12, 1, Brand.BorderSoft);
        locationStyle.ShadowSize = 0;
        locationStyle.ContentMarginLeft = locationStyle.ContentMarginRight = 14;
        locationStyle.ContentMarginTop = locationStyle.ContentMarginBottom = 12;
        location.AddThemeStyleboxOverride("panel", locationStyle);
        body.AddChild(location);
        var locationColumn = new VBoxContainer();
        locationColumn.AddThemeConstantOverride("separation", 8);
        location.AddChild(locationColumn);
        var locationRow = Row();
        locationColumn.AddChild(locationRow);
        var place = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _locationLabel = Label("Home", 18, Brand.TextHi);
        _locationLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        place.AddChild(_locationLabel);
        _privacyLabel = Label("Personal Home · only you", 12, Brand.TextDim);
        place.AddChild(_privacyLabel);
        locationRow.AddChild(place);
        _reportWorldBtn = Button(Icons.Kind.Flag, "", ReportWorldPressedNow, 44);
        _reportWorldBtn.TooltipText = "Report this world";
        _reportWorldBtn.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        locationRow.AddChild(_reportWorldBtn);
        _worldActions = new GridContainer { Columns = 2 };
        _worldActions.AddThemeConstantOverride("h_separation", 8);
        _worldActions.AddThemeConstantOverride("v_separation", 8);
        locationColumn.AddChild(_worldActions);
        _setHomeBtn = Button(Icons.Kind.Home, "Set as Home", () => SetHomePressed?.Invoke());
        _setHomeBtn.TooltipText = "Use this world as your personal arrival Home";
        _worldActions.AddChild(_setHomeBtn);
        _privateBtn = Button(Icons.Kind.Users, "New private instance", () => NewPrivateInstancePressed?.Invoke());
        _privateBtn.TooltipText = "Create an invite-only instance; only you can invite people";
        _worldActions.AddChild(_privateBtn);
        _resetHomeBtn = Button(Icons.Kind.Refresh, "Use default Home", () => ResetHomePressed?.Invoke());
        _resetHomeBtn.TooltipText = "Restore the community's default arrival Home";
        locationColumn.AddChild(_resetHomeBtn);
        _worldStatus = Label("", 12, Brand.TextMid);
        _worldStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _worldStatus.Visible = false;
        locationColumn.AddChild(_worldStatus);

        _actions = new GridContainer { Columns = 3, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _actions.AddThemeConstantOverride("h_separation", 8);
        _actions.AddThemeConstantOverride("v_separation", 8);
        body.AddChild(_actions);
        _actions.AddChild(Tile(Icons.Kind.Globe, "Worlds", "Find your next place", () => LeaveTo(OpenMainMenuWorlds)));
        _actions.AddChild(Tile(Icons.Kind.Shirt, "Avatars", "Change your look", () => LeaveTo(OpenMainMenuAvatars)));
        _socialPill = Tile(Icons.Kind.Users, "Social", "Friends & notifications", () => LeaveTo(OpenSocial));
        _actions.AddChild(_socialPill);
        _actions.AddChild(Tile(Icons.Kind.Camera, "Camera", "Take a photo", () => LeaveTo(OpenCameraMenu)));
        _actions.AddChild(Tile(Icons.Kind.Screen, "Video", "Watch together", () => LeaveTo(OpenVideoQueue)));
        _actions.AddChild(Tile(Icons.Kind.Gear, "Settings", "Comfort & controls", () => LeaveTo(OpenSettings)));
        _actions.AddChild(Tile(Icons.Kind.Home, "Go Home", "Return to your space", () => LeaveTo(HomePressed)));
        _actions.AddChild(Tile(Icons.Kind.Refresh, "Respawn", "Back to the entrance", () => LeaveTo(RespawnPressed)));
        _actions.AddChild(Tile(Icons.Kind.Smile, "Actions", "Emotes & gestures", () => LeaveTo(OpenRadialMenu)));
        ApplyNotificationCount();

        var peopleHeader = Row();
        body.AddChild(peopleHeader);
        peopleHeader.AddChild(Label("HERE WITH YOU", 11, Brand.TextDim));
        _playerCountLabel = Label("1 person", 12, Brand.TextMid);
        _playerCountLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        peopleHeader.AddChild(_playerCountLabel);
        _inviteCard = Button(Icons.Kind.Link, "Copy invite", () => CopyInvitePressed?.Invoke());
        _inviteCard.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        _inviteCard.CustomMinimumSize = new Vector2(150, 44);
        peopleHeader.AddChild(_inviteCard);
        var peopleScroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, 68),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            FollowFocus = true,
        };
        body.AddChild(peopleScroll);
        _playerList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _playerList.AddThemeConstantOverride("separation", 4);
        peopleScroll.AddChild(_playerList);
        column.AddChild(new HSeparator());
        var bottom = Row();
        column.AddChild(bottom);
        _micBtn = Button(Icons.Kind.Mic, "Mic muted", () => MicTogglePressed?.Invoke());
        bottom.AddChild(_micBtn);
        var quit = Button(Icons.Kind.Power, "Quit", () => QuitPressed?.Invoke());
        quit.AddThemeColorOverride("font_color", Brand.Danger);
        bottom.AddChild(quit);
        _resumeBtn = Brand.Primary_(new Button { Text = "Resume", CustomMinimumSize = new Vector2(120, 46), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        _resumeBtn.TooltipText = VrUiSurface.Active ? "Close the menu and return to the world" : "Return to the world · Esc";
        _resumeBtn.Pressed += Hide;
        bottom.AddChild(_resumeBtn);
        GetViewport().SizeChanged += Fit;
        Fit();
        SetWorldActions(false, false, false, "Personal Home · only you");
    }

    public override void _ExitTree() => GetViewport().SizeChanged -= Fit;

    private void Fit()
    {
        _card.CustomMinimumSize = Brand.FitCard(GetViewport(), 700, 690);
        _actions.Columns = _card.CustomMinimumSize.X < 600 ? 2 : 3;
        _worldActions.Columns = _card.CustomMinimumSize.X < 580 ? 1 : 2;
    }

    private static HBoxContainer Row()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        return row;
    }

    private static Label Label(string text, int size, Color color)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", Brand.Fs(size));
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    private static Button Button(Icons.Kind icon, string title, Action action, int width = 0)
    {
        var button = Brand.Ghost_(new Button
        {
            Text = title, CustomMinimumSize = new Vector2(width, 44),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Icon = Icons.Get(icon, 18, Brand.Accent), ClipText = true,
        });
        button.AddThemeFontSizeOverride("font_size", Brand.Fs(13));
        button.AddThemeConstantOverride("h_separation", 8);
        button.Pressed += action;
        return button;
    }

    private static Button Tile(Icons.Kind icon, string title, string subtitle, Action action)
    {
        var button = Button(icon, title, action);
        button.CustomMinimumSize = new Vector2(150, 56);
        button.AddThemeFontSizeOverride("font_size", Brand.Fs(15));
        button.TooltipText = subtitle;
        return button;
    }

    private void LeaveTo(Action action) { Hide(); action?.Invoke(); }
    private void ReportWorldPressedNow() => ReportWorldPressed?.Invoke();

    public void SetNotificationCount(int unread) { _notificationCount = Math.Max(0, unread); ApplyNotificationCount(); }
    private void ApplyNotificationCount()
    {
        if (_socialPill == null) return;
        _socialPill.Text = _notificationCount > 0 ? $"Social ({_notificationCount})" : "Social";
        _socialPill.AddThemeColorOverride("font_color", _notificationCount > 0 ? Brand.AccentSoft : Brand.TextMid);
    }

    public void SetLocation(string worldName, bool invitable)
    {
        _locationLabel.Text = string.IsNullOrEmpty(worldName) ? "Home" : worldName;
        _locationLabel.TooltipText = _locationLabel.Text;
        _inviteCard.Disabled = !invitable;
        _reportWorldBtn.Disabled = !invitable;
    }

    /// Privacy comes from the server's instance state, never inferred from its player count.
    public void SetWorldActions(bool canSetHome, bool isHome, bool canCreatePrivate,
        string privacyLabel = "Public instance", bool busy = false)
    {
        if (_setHomeBtn == null) return;
        _setHomeBtn.Text = isHome ? "Your Home" : "Set as Home";
        _setHomeBtn.Disabled = busy || !canSetHome || isHome;
        _worldBusy = busy;
        _resetHomeBtn.Disabled = busy || !_homeResetAvailable;
        _resetHomeBtn.Visible = _homeResetAvailable;
        _privateBtn.Disabled = busy || !canCreatePrivate;
        _privacyLabel.Text = privacyLabel;
        _inviteCard.Text = privacyLabel.StartsWith("Private", StringComparison.OrdinalIgnoreCase) ? "Invite friends" : "Copy invite";
    }

    public void SetHomeResetAvailable(bool available)
    {
        _homeResetAvailable = available;
        if (_resetHomeBtn == null) return;
        _resetHomeBtn.Visible = available;
        _resetHomeBtn.Disabled = !available || _worldBusy;
    }

    public void SetWorldActionStatus(string message, bool error = false)
    {
        if (_worldStatus == null) return;
        _worldStatus.Text = message ?? "";
        _worldStatus.Visible = !string.IsNullOrEmpty(message);
        _worldStatus.AddThemeColorOverride("font_color", error ? Brand.Danger : Brand.Success);
    }

    public void SetPlayers(string you, IReadOnlyList<(string userId, string name)> others)
    {
        foreach (var child in _playerList.GetChildren()) { _playerList.RemoveChild(child); child.QueueFree(); }
        int total = 1 + (others?.Count ?? 0);
        _playerCountLabel.Text = total == 1 ? "1 person" : $"{total} people";
        var self = Label(string.IsNullOrEmpty(you) ? "You" : $"{you}  ·  you", 13, Brand.TextHi);
        self.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _playerList.AddChild(self);
        if (others == null) return;
        foreach (var (userId, name) in others)
        {
            if (string.IsNullOrEmpty(userId)) { _playerList.AddChild(Label(name, 13, Brand.TextMid)); continue; }
            string id = userId, displayName = name;
            var button = Button(Icons.Kind.Person, name, () => PlayerSelected?.Invoke(id, displayName));
            button.Alignment = HorizontalAlignment.Left;
            button.TooltipText = $"View {name} · friend, block or report";
            _playerList.AddChild(button);
        }
    }

    public void SetTrust(string label) { if (_trustChip != null) _trustChip.Text = string.IsNullOrEmpty(label) ? "Visitor" : label; }
    public void SetMic(bool active)
    {
        if (_micBtn == null) return;
        _micBtn.Text = active ? "Mic live" : "Mic muted";
        _micBtn.Icon = Icons.Get(Icons.Kind.Mic, 18, active ? Brand.Success : Brand.TextDim);
        _micBtn.AddThemeColorOverride("font_color", active ? Brand.Success : Brand.TextMid);
    }

    public void Open(string username)
    {
        if (!string.IsNullOrEmpty(username)) _usernameLabel.Text = username;
        Visible = true;
        Fit();
        _resumeBtn.CallDeferred(Control.MethodName.GrabFocus);
    }

    public new void Hide()
    {
        if (!Visible) return;
        var focus = GetViewport().GuiGetFocusOwner();
        if (focus != null && IsAncestorOf(focus)) focus.ReleaseFocus();
        Visible = false;
        Closed?.Invoke();
    }
    public bool IsOpen => Visible;
    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape } && IsOpen)
        { Hide(); GetViewport().SetInputAsHandled(); }
    }
    public override void _Process(double delta)
    {
        if (!IsOpen) return;
        _clockTimer += delta;
        if (_clockTimer < 1) return;
        _clockTimer = 0;
        _clockLabel.Text = DateTime.Now.ToString("HH:mm");
    }
}
