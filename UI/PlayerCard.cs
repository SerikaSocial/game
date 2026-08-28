using System;
using Godot;
using Serika.Net;

namespace SerikaSocial.UI;

/// Per-player actions, opened by tapping a remote row in the Quick Menu's instance
/// roster: friend / block / report. State is fetched fresh on each open — the panel is
/// opened rarely, so two cheap GETs beat keeping a live cache.
public partial class PlayerCard : CanvasLayer
{
    public event Action Closed;
    /// Fired after a block/unblock so Main can re-apply or lift beans in-world.
    public event Action BlocksChanged;
    /// Set by Main: opens the shared report dialog pre-targeted at this player.
    public Func<string, string, bool> ReportRequested;

    private ApiClient _api;
    private ColorRect _scrim;
    private PanelContainer _card;
    private Label _title;
    private Label _subtitle;
    private VBoxContainer _actions;
    private Label _status;

    private ApiClient.SocialUser _user;

    public PlayerCard()
    {
        Layer = 107; // above the quick menu it opens from, below the report dialog (110)
    }

    public override void _Ready()
    {
        Visible = false;

        _scrim = Brand.Scrim(0.7f);
        AddChild(_scrim);

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(center);

        _card = new PanelContainer();
        _card.AddThemeStyleboxOverride("panel", Brand.Panel(Brand.Bg1, 16));
        _card.CustomMinimumSize = Brand.Card(440, 380);
        center.AddChild(_card);

        var margin = new MarginContainer();
        foreach (var s in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(s, 20);
        _card.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 12);
        margin.AddChild(vbox);

        _title = new Label { Text = "Player" };
        _title.AddThemeFontSizeOverride("font_size", Brand.Fs(18));
        _title.AddThemeColorOverride("font_color", Brand.TextHi);
        vbox.AddChild(_title);

        _subtitle = new Label { Text = "" };
        _subtitle.AddThemeFontSizeOverride("font_size", Brand.Fs(12));
        _subtitle.AddThemeColorOverride("font_color", Brand.TextDim);
        vbox.AddChild(_subtitle);

        vbox.AddChild(new HSeparator());

        _actions = new VBoxContainer();
        _actions.AddThemeConstantOverride("separation", 10);
        vbox.AddChild(_actions);

        _status = new Label
        {
            Text = "",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _status.AddThemeFontSizeOverride("font_size", Brand.Fs(12));
        _status.AddThemeColorOverride("font_color", Brand.Warning);
        vbox.AddChild(_status);

        var closeBtn = Brand.Ghost_(new Button { Text = "Close", CustomMinimumSize = new Vector2(120, 42) });
        closeBtn.Pressed += Hide;
        var closeWrap = new CenterContainer();
        closeWrap.AddChild(closeBtn);
        vbox.AddChild(closeWrap);
    }

    public void Configure(ApiClient api) => _api = api;

    public async void OpenFor(ApiClient.SocialUser user)
    {
        _user = user;
        _title.Text = user.Name;
        _subtitle.Text = $"@{user.Username}";
        _status.Text = "";
        _scrim.Visible = _card.Visible = Visible = true;
        await LoadStateAsync();
    }

    public new void Hide()
    {
        _scrim.Visible = false;
        _card.Visible = false;
        Visible = false;
        Closed?.Invoke();
    }

    public bool IsOpen => Visible;

    private async System.Threading.Tasks.Task LoadStateAsync()
    {
        if (_api == null) return;
        foreach (var c in _actions.GetChildren()) c.QueueFree();
        AddAction("Loading…", null, Icons.Kind.Question, null);

        ApiClient.SocialGraph graph = null;
        bool blocked = false;
        try
        {
            graph = await _api.GetSocialGraphAsync();
            blocked = (await _api.GetBlockedUsersAsync()).Contains(_user.Id);
        }
        catch (Exception e)
        {
            GD.PrintErr($"player card state fetch failed: {e.Message}");
        }

        foreach (var c in _actions.GetChildren()) c.QueueFree();
        if (graph == null)
        {
            _status.Text = "Couldn't load relationship — check your connection.";
            // Report and block still work blind; friend actions need the graph.
            AddAction("Report player", () => RequestReport(), Icons.Kind.Flag, Brand.Danger);
            return;
        }

        bool isFriend = graph.Friends.Exists(f => f.Id == _user.Id);
        bool incoming = graph.Incoming.Exists(f => f.Id == _user.Id);
        bool outgoing = graph.Outgoing.Exists(f => f.Id == _user.Id);

        if (blocked)
        {
            _status.Text = "You've blocked this player.";
            AddAction("Unblock", () => _ = UnblockAsync(), Icons.Kind.Ban, null);
        }
        else
        {
            if (isFriend)
                AddAction("Friends ✓ — remove", () => _ = RemoveFriendAsync(), Icons.Kind.Person, null);
            else if (incoming)
            {
                AddAction("Accept friend request", () => _ = AcceptAsync(), Icons.Kind.PersonAdd, Brand.Accent);
                AddAction("Decline friend request", () => _ = RemoveFriendAsync(), Icons.Kind.Close, null);
            }
            else if (outgoing)
                AddAction("Cancel friend request", () => _ = RemoveFriendAsync(), Icons.Kind.Close, null);
            else
                AddAction("Add friend", () => _ = AddFriendAsync(), Icons.Kind.PersonAdd, Brand.Accent);

            AddAction("Block", () => _ = BlockAsync(), Icons.Kind.Ban, Brand.Warning);
        }
        AddAction("Report player", () => RequestReport(), Icons.Kind.Flag, Brand.Danger);
    }

    private void RequestReport()
    {
        bool handled = ReportRequested?.Invoke(_user.Id, _user.Name) ?? false;
        if (handled) Hide();
    }

    private void AddAction(string text, Action onClick, Icons.Kind icon, Color? tint)
    {
        var btn = Brand.Ghost_(new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(0, 46),
            Icon = Icons.Get(icon, 18, tint ?? Brand.TextMid),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        });
        btn.AddThemeFontSizeOverride("font_size", Brand.Fs(14));
        btn.AddThemeConstantOverride("h_separation", 10);
        if (tint != null) btn.AddThemeColorOverride("font_color", tint.Value);
        if (onClick != null) btn.Pressed += onClick;
        else btn.Disabled = true;
        _actions.AddChild(btn);
    }

    private async System.Threading.Tasks.Task AddFriendAsync()
    {
        try
        {
            string state = await _api.SendFriendRequestAsync(_user.Id);
            _status.Text = state == "accepted" ? "You are now friends!" :
                           state == "already_friends" ? "Already friends." :
                           state == "blocked" ? "Can't send — one of you has blocked the other." :
                           "Friend request sent.";
            await LoadStateAsync();
        }
        catch (Exception e) { ShowError(e); }
    }

    private async System.Threading.Tasks.Task AcceptAsync()
    {
        try
        {
            await _api.AcceptFriendRequestAsync(_user.Id);
            _status.Text = "You are now friends!";
            await LoadStateAsync();
        }
        catch (Exception e) { ShowError(e); }
    }

    private async System.Threading.Tasks.Task RemoveFriendAsync()
    {
        try
        {
            await _api.RemoveFriendAsync(_user.Id);
            _status.Text = "Removed.";
            await LoadStateAsync();
        }
        catch (Exception e) { ShowError(e); }
    }

    private async System.Threading.Tasks.Task BlockAsync()
    {
        try
        {
            await _api.BlockUserAsync(_user.Id);
            _status.Text = "Blocked. They now appear as a bean to you.";
            BlocksChanged?.Invoke();
            await LoadStateAsync();
        }
        catch (Exception e) { ShowError(e); }
    }

    private async System.Threading.Tasks.Task UnblockAsync()
    {
        try
        {
            await _api.UnblockUserAsync(_user.Id);
            _status.Text = "Unblocked.";
            BlocksChanged?.Invoke();
            await LoadStateAsync();
        }
        catch (Exception e) { ShowError(e); }
    }

    private void ShowError(Exception e)
    {
        GD.PrintErr($"player card action failed: {e.Message}");
        _status.Text = "Action failed — check your connection.";
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape } && IsOpen)
        {
            Hide();
            GetViewport().SetInputAsHandled();
        }
    }
}
