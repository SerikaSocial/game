using System;
using System.Collections.Generic;
using Godot;
using Serika.Net;

namespace SerikaSocial.UI;

/// The social panel — friends, requests, blocked users, and user search, reached from
/// the Quick Menu's "Social" pill. Works in Home and in-world, on desktop, touch and VR
/// (ordinary Controls on the VR panel, so the laser pointer drives it; the search field
/// is a LineEdit so VrKeyboard covers it).
public partial class SocialPanel : CanvasLayer
{
    public event Action Closed;
    /// Fired after any block/unblock so Main can re-apply beans in-world.
    public event Action BlocksChanged;

    private ApiClient _api;
    private ColorRect _scrim;
    private PanelContainer _card;
    private Label _status;
    private VBoxContainer _content;
    private readonly Dictionary<string, Button> _tabButtons = new();

    private SocialPanelTab _tab = SocialPanelTab.Friends;
    private ApiClient.SocialGraph _graph = new();
    private List<ApiClient.SocialUser> _blocks = new();
    private List<ApiClient.SocialUser> _searchResults = new();
    private HBoxContainer _searchRow;
    private LineEdit _searchBox;
    private VBoxContainer _listBody;
    private bool _loading;

    private enum SocialPanelTab { Notifications, Friends, Requests, Blocked, Add }

    /// Inbox state. Owned here but fed from two directions — a REST fetch on open, and live
    /// gateway pushes via `PushNotification` — so the list is correct whether or not the socket
    /// was up when something happened.
    private List<SerikaNotification> _notifications = new();
    private int _unread;

    /// Raised when the user acts on an invite. Main joins the world; the panel has no business
    /// knowing how joining works.
    public event Action<SerikaNotification> InviteAccepted;
    public Func<string> InviteInstanceId;
    private bool _sendingInvite;

    /// Raised whenever the unread count changes, so the quick-menu badge can follow it.
    public event Action<int> UnreadChanged;

    /// Current unread count, for a freshly-built badge.
    public int Unread => _unread;

    public SocialPanel()
    {
        Layer = 96; // above the avatar selector (95), below the quick menu (105)
    }

    public override void _Ready()
    {
        Visible = false;

        _scrim = Brand.Scrim(0.82f);
        AddChild(_scrim);

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(center);

        _card = new PanelContainer();
        _card.AddThemeStyleboxOverride("panel", Brand.Panel(Brand.Bg1, 16));
        _card.CustomMinimumSize = Brand.Card(680, 560);
        center.AddChild(_card);

        var margin = new MarginContainer();
        foreach (var s in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(s, 18);
        _card.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 12);
        margin.AddChild(vbox);

        // ── Header: title · tabs · close ────────────────────────────────────────────
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(header);

        var title = new Label { Text = "Social" };
        title.AddThemeFontSizeOverride("font_size", Brand.Fs(18));
        title.AddThemeColorOverride("font_color", Brand.TextHi);
        header.AddChild(title);

        header.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        AddTab(header, SocialPanelTab.Notifications, "Notifications");
        AddTab(header, SocialPanelTab.Friends, "Friends");
        AddTab(header, SocialPanelTab.Requests, "Requests");
        AddTab(header, SocialPanelTab.Blocked, "Blocked");
        AddTab(header, SocialPanelTab.Add, "Add friend");

        header.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        var close = Brand.Ghost_(new Button
        {
            Text = "Close",
            CustomMinimumSize = new Vector2(86, 36),
        });
        close.Pressed += Hide;
        header.AddChild(close);

        // ── Content ─────────────────────────────────────────────────────────────────
        // The search row is built once and re-shown on the Add tab; only `_listBody` is
        // rebuilt per render, so the LineEdit keeps its text (and VR keyboard focus).
        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, 380),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        scroll.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        vbox.AddChild(scroll);

        _content = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _content.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_content);

        _searchRow = new HBoxContainer();
        _searchRow.AddThemeConstantOverride("separation", 8);
        _content.AddChild(_searchRow);

        _searchBox = new LineEdit
        {
            PlaceholderText = "Search usernames (at least 2 characters)…",
            CustomMinimumSize = new Vector2(0, 40 * (Brand.Fs(14) / 14f)),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _searchBox.TextSubmitted += text => { _ = SearchAsync(); };
        _searchRow.AddChild(_searchBox);

        var searchBtn = Brand.Primary_(new Button { Text = "Search", CustomMinimumSize = new Vector2(96, 40) });
        searchBtn.Pressed += () => _ = SearchAsync();
        _searchRow.AddChild(searchBtn);

        _listBody = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _listBody.AddThemeConstantOverride("separation", 6);
        _content.AddChild(_listBody);

        _status = new Label { Text = "" };
        _status.AddThemeFontSizeOverride("font_size", Brand.Fs(12));
        _status.AddThemeColorOverride("font_color", Brand.TextDim);
        vbox.AddChild(_status);
    }

    private void AddTab(HBoxContainer header, SocialPanelTab tab, string label)
    {
        var btn = Brand.Ghost_(new Button { Text = label, ToggleMode = true });
        btn.AddThemeFontSizeOverride("font_size", Brand.Fs(13));
        btn.Pressed += () => SwitchTab(tab);
        header.AddChild(btn);
        _tabButtons[tab.ToString()] = btn;
    }

    public void Configure(ApiClient api) => _api = api;

    public void Open()
    {
        _scrim.Visible = _card.Visible = Visible = true;
        // Land on whatever the user opened this for: unread notifications if there are any,
        // otherwise the friends list.
        _tab = _unread > 0 ? SocialPanelTab.Notifications : SocialPanelTab.Friends;
        _ = ReloadAsync();
        _ = LoadNotificationsAsync();
    }

    public new void Hide()
    {
        _scrim.Visible = false;
        _card.Visible = false;
        Visible = false;
        _searchBox?.ReleaseFocus();
        Closed?.Invoke();
    }

    public bool IsOpen => Visible;

    private void SwitchTab(SocialPanelTab tab)
    {
        _tab = tab;
        Render();
        if (tab == SocialPanelTab.Friends) _ = ReloadAsync();
        if (tab == SocialPanelTab.Notifications) _ = LoadNotificationsAsync();
    }

    // ── Notifications ─────────────────────────────────────────────────────────────────

    /// Fetch the inbox. Called when the tab opens and after a reconnect, because pushes that
    /// happened while the socket was down were never delivered anywhere but the database.
    public async System.Threading.Tasks.Task LoadNotificationsAsync()
    {
        if (_api == null) return;
        try
        {
            var (items, unread) = await _api.GetNotificationsAsync();
            _notifications = new List<SerikaNotification>(items);
            SetUnread(unread);
            if (_tab == SocialPanelTab.Notifications) Render();
        }
        catch (Exception e)
        {
            GD.PrintErr($"notifications load failed: {e.Message}");
        }
    }

    /// A notification arrived over the gateway. Merge it in without a round trip.
    public void PushNotification(SerikaNotification n, int unread)
    {
        if (n == null) return;
        // The REST fetch and the push can race on the same row; keep one copy.
        _notifications.RemoveAll(x => x.Id == n.Id);
        _notifications.Insert(0, n);
        SetUnread(unread);
        if (IsOpen && _tab == SocialPanelTab.Notifications) Render();
        else RefreshTabLabels();
    }

    private void SetUnread(int unread)
    {
        if (_unread == unread) { RefreshTabLabels(); return; }
        _unread = unread;
        RefreshTabLabels();
        UnreadChanged?.Invoke(_unread);
    }

    /// The tab itself carries the count — a badge somewhere else is easy to miss when the panel
    /// is already open.
    private void RefreshTabLabels()
    {
        if (_tabButtons.TryGetValue(SocialPanelTab.Notifications.ToString(), out var btn))
            btn.Text = _unread > 0 ? $"Notifications ({_unread})" : "Notifications";
    }

    private void RenderNotifications()
    {
        if (_notifications.Count == 0)
        {
            AddHint("Nothing here yet. Friend requests and world invites will show up");
            AddHint("on this tab, and stay here if you were offline when they arrived.");
            return;
        }

        if (_unread > 0)
        {
            var clear = Brand.Ghost_(new Button { Text = "Mark all read", CustomMinimumSize = new Vector2(140, 32) });
            clear.AddThemeFontSizeOverride("font_size", Brand.Fs(12));
            clear.Pressed += () => _ = MarkAllReadAsync();
            _listBody.AddChild(clear);
        }

        foreach (var n in _notifications)
            AddNotificationRow(n);
    }

    private void AddNotificationRow(SerikaNotification n)
    {
        var row = new PanelContainer();
        // Unread rows get the accent border; read ones recede. Without that the list is a wall
        // of identical cards and "what's new" has to be remembered rather than seen.
        row.AddThemeStyleboxOverride("panel", n.Read
            ? Brand.Panel(Brand.Bg2, 10, 1, Brand.BorderSoft)
            : Brand.Panel(Brand.Bg2, 10, 1, Brand.Accent));
        _listBody.AddChild(row);

        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 10);
        row.AddChild(hbox);

        var names = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        names.AddThemeConstantOverride("separation", 2);
        hbox.AddChild(names);

        var title = new Label { Text = n.Title, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        title.AddThemeFontSizeOverride("font_size", Brand.Fs(14));
        title.AddThemeColorOverride("font_color", n.Read ? Brand.TextMid : Brand.TextHi);
        names.AddChild(title);

        string sub = n.Body;
        string age = Ago(n.CreatedAt);
        if (!string.IsNullOrEmpty(age)) sub = string.IsNullOrEmpty(sub) ? age : $"{sub} · {age}";
        if (n.Kind == "invite" && n.IsExpired) sub += " · expired";

        var subLabel = new Label { Text = sub, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        subLabel.AddThemeFontSizeOverride("font_size", Brand.Fs(12));
        subLabel.AddThemeColorOverride("font_color", Brand.TextDim);
        names.AddChild(subLabel);

        // An expired invite keeps its row as history but loses its button — the instance it
        // points at has very likely emptied, and a Join that fails is worse than no Join.
        if (n.Kind == "invite" && !n.IsExpired && !string.IsNullOrEmpty(n.WorldId))
        {
            var join = Brand.Primary_(new Button { Text = "Join", CustomMinimumSize = new Vector2(88, 36) });
            join.AddThemeFontSizeOverride("font_size", Brand.Fs(13));
            join.Pressed += () =>
            {
                _ = MarkReadAsync(n);
                Hide();
                InviteAccepted?.Invoke(n);
            };
            hbox.AddChild(join);
        }
        else if (n.Kind == "friend_request" && !string.IsNullOrEmpty(n.ActorId))
        {
            var accept = Brand.Primary_(new Button { Text = "Accept", CustomMinimumSize = new Vector2(88, 36) });
            accept.AddThemeFontSizeOverride("font_size", Brand.Fs(13));
            accept.Pressed += () => _ = AcceptFromNotificationAsync(n);
            hbox.AddChild(accept);
        }

        if (!n.Read)
        {
            var read = Brand.Ghost_(new Button { Text = "Mark read", CustomMinimumSize = new Vector2(96, 36) });
            read.AddThemeFontSizeOverride("font_size", Brand.Fs(12));
            read.Pressed += () => _ = MarkReadAsync(n);
            hbox.AddChild(read);
        }

        var dismiss = Brand.Ghost_(new Button { Text = "×", CustomMinimumSize = new Vector2(36, 36) });
        dismiss.AddThemeFontSizeOverride("font_size", Brand.Fs(14));
        dismiss.Pressed += () => _ = DismissAsync(n);
        hbox.AddChild(dismiss);
    }

    private async System.Threading.Tasks.Task MarkReadAsync(SerikaNotification n)
    {
        if (_api == null || n.Read) return;
        n.Read = true;             // optimistic: the row should dim on click, not on round trip
        SetUnread(Math.Max(0, _unread - 1));
        if (_tab == SocialPanelTab.Notifications) Render();
        try { SetUnread(await _api.MarkNotificationReadAsync(n.Id)); }
        catch (Exception e) { GD.PrintErr($"mark read failed: {e.Message}"); }
    }

    private async System.Threading.Tasks.Task MarkAllReadAsync()
    {
        if (_api == null) return;
        foreach (var n in _notifications) n.Read = true;
        SetUnread(0);
        Render();
        try { await _api.MarkAllNotificationsReadAsync(); }
        catch (Exception e) { GD.PrintErr($"mark all read failed: {e.Message}"); }
    }

    private async System.Threading.Tasks.Task DismissAsync(SerikaNotification n)
    {
        if (_api == null) return;
        _notifications.RemoveAll(x => x.Id == n.Id);
        if (!n.Read) SetUnread(Math.Max(0, _unread - 1));
        Render();
        try { await _api.DeleteNotificationAsync(n.Id); }
        catch (Exception e) { GD.PrintErr($"dismiss failed: {e.Message}"); }
    }

    /// Accept a friend request straight from its notification, so the common case never needs
    /// the Requests tab.
    private async System.Threading.Tasks.Task AcceptFromNotificationAsync(SerikaNotification n)
    {
        if (_api == null || string.IsNullOrEmpty(n.ActorId)) return;
        try
        {
            await _api.AcceptFriendRequestAsync(n.ActorId);
            await MarkReadAsync(n);
            _status.Text = $"Accepted {n.ActorName ?? "friend request"}.";
            await ReloadAsync();
        }
        catch (Exception e) { ShowError(e); }
    }

    /// Compact relative age. Absolute timestamps in a notification list are noise.
    private static string Ago(DateTimeOffset when)
    {
        if (when == default) return "";
        var d = DateTimeOffset.UtcNow - when;
        if (d.TotalSeconds < 60) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays}d ago";
        return when.LocalDateTime.ToString("d MMM");
    }

    private async System.Threading.Tasks.Task ReloadAsync()
    {
        if (_api == null) return;
        _loading = true;
        _status.Text = "Loading…";
        Render();
        try
        {
            _graph = await _api.GetSocialGraphAsync();
            _blocks = await _api.GetBlockedUsersDetailedAsync();
        }
        catch (Exception e)
        {
            GD.PrintErr($"social panel reload failed: {e.Message}");
        }
        finally
        {
            _loading = false;
            _status.Text = "";
            Render();
        }
    }

    private void Render()
    {
        foreach (var b in _tabButtons.Values) b.ButtonPressed = false;
        if (_tabButtons.TryGetValue(_tab.ToString(), out var active)) active.ButtonPressed = true;

        _searchRow.Visible = _tab == SocialPanelTab.Add;
        foreach (var c in _listBody.GetChildren()) c.QueueFree();

        RefreshTabLabels();

        switch (_tab)
        {
            case SocialPanelTab.Notifications: RenderNotifications(); break;
            case SocialPanelTab.Friends: RenderFriends(); break;
            case SocialPanelTab.Requests: RenderRequests(); break;
            case SocialPanelTab.Blocked: RenderBlocked(); break;
            case SocialPanelTab.Add: RenderAdd(); break;
        }
    }

    private void RenderFriends()
    {
        if (_loading) { AddHint("Loading friends…"); return; }
        if (_graph.Friends.Count == 0)
        {
            AddHint("No friends yet. Use “Add friend” to search for people, or open");
            AddHint("a player's card from the pause menu's instance roster.");
            return;
        }
        foreach (var u in _graph.Friends)
        {
            if (!string.IsNullOrEmpty(InviteInstanceId?.Invoke()))
                AddRow(u.Name, $"@{u.Username}", _sendingInvite ? "Sending…" : "Invite",
                    _sendingInvite ? null : () => _ = InviteFriendAsync(u), "Unfriend", () => _ = RemoveAsync(u));
            else AddRow(u.Name, $"@{u.Username}", "Unfriend", () => _ = RemoveAsync(u));
        }
    }

    private async System.Threading.Tasks.Task InviteFriendAsync(ApiClient.SocialUser friend)
    {
        string instanceId = InviteInstanceId?.Invoke();
        if (_api == null || _sendingInvite || string.IsNullOrEmpty(instanceId)) return;
        _sendingInvite = true;
        Render();
        try { await _api.InviteToInstanceAsync(friend.Id, instanceId); _status.Text = $"Invited {friend.Name}."; }
        catch (Exception e) { ShowError(e); }
        finally { _sendingInvite = false; Render(); }
    }

    private void RenderRequests()
    {
        if (_loading) { AddHint("Loading requests…"); return; }
        AddSection("Incoming");
        if (_graph.Incoming.Count == 0) AddHint("No incoming requests.");
        foreach (var u in _graph.Incoming)
            AddRow(u.Name, $"@{u.Username} wants to be friends", "Accept", () => _ = AcceptAsync(u), "Decline", () => _ = RemoveAsync(u));
        AddSection("Sent");
        if (_graph.Outgoing.Count == 0) AddHint("No pending sent requests.");
        foreach (var u in _graph.Outgoing)
            AddRow(u.Name, $"@{u.Username} · waiting", "Cancel", () => _ = RemoveAsync(u));
    }

    private void RenderBlocked()
    {
        if (_loading) { AddHint("Loading blocked users…"); return; }
        if (_blocks.Count == 0)
        {
            AddHint("You haven't blocked anyone. Blocked users can't friend or");
            AddHint("follow you, and appear as beans to you in-world.");
            return;
        }
        foreach (var u in _blocks)
            AddRow(u.Name, $"@{u.Username}", "Unblock", () => _ = UnblockAsync(u));
    }

    private void RenderAdd()
    {
        // The search row lives above `_listBody` and is only shown on this tab.
        if (_searchResults.Count == 0 && !string.IsNullOrEmpty(_searchBox?.Text))
            AddHint("No users matched.");
        foreach (var u in _searchResults)
        {
            bool isFriend = _graph.Friends.Exists(f => f.Id == u.Id);
            bool isOutgoing = _graph.Outgoing.Exists(f => f.Id == u.Id);
            string action = isFriend ? "Friends" : isOutgoing ? "Requested" : "Add";
            AddRow(u.Name, $"@{u.Username}", action, isFriend || isOutgoing ? null : () => _ = AddAsync(u));
        }
    }

    // ── Row helpers ────────────────────────────────────────────────────────────────

    private void AddSection(string title)
    {
        var lbl = new Label { Text = title };
        lbl.AddThemeFontSizeOverride("font_size", Brand.Fs(12));
        lbl.AddThemeColorOverride("font_color", Brand.TextDim);
        _listBody.AddChild(lbl);
    }

    private void AddHint(string text)
    {
        var lbl = new Label
        {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        lbl.AddThemeFontSizeOverride("font_size", Brand.Fs(13));
        lbl.AddThemeColorOverride("font_color", Brand.TextMid);
        _listBody.AddChild(lbl);
    }

    private void AddRow(string name, string sub, string action, Action onAction,
                        string secondAction = null, Action onSecond = null)
    {
        var row = new PanelContainer();
        row.AddThemeStyleboxOverride("panel", Brand.Panel(Brand.Bg2, 10, 1, Brand.BorderSoft));
        _listBody.AddChild(row);

        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 10);
        row.AddChild(hbox);

        var names = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        names.AddThemeConstantOverride("separation", 2);
        hbox.AddChild(names);

        var nameLabel = new Label { Text = name };
        nameLabel.AddThemeFontSizeOverride("font_size", Brand.Fs(14));
        nameLabel.AddThemeColorOverride("font_color", Brand.TextHi);
        names.AddChild(nameLabel);

        var subLabel = new Label { Text = sub };
        subLabel.AddThemeFontSizeOverride("font_size", Brand.Fs(12));
        subLabel.AddThemeColorOverride("font_color", Brand.TextDim);
        names.AddChild(subLabel);

        if (action != null)
        {
            var btn = Brand.Ghost_(new Button { Text = action, CustomMinimumSize = new Vector2(100, 36) });
            btn.AddThemeFontSizeOverride("font_size", Brand.Fs(13));
            if (onAction != null) btn.Pressed += onAction;
            else btn.Disabled = true;
            hbox.AddChild(btn);
        }
        if (secondAction != null && onSecond != null)
        {
            var btn2 = Brand.Ghost_(new Button { Text = secondAction, CustomMinimumSize = new Vector2(96, 36) });
            btn2.AddThemeFontSizeOverride("font_size", Brand.Fs(13));
            btn2.Pressed += onSecond;
            hbox.AddChild(btn2);
        }
    }

    // ── Mutations (each reloads the affected lists) ────────────────────────────────

    private async System.Threading.Tasks.Task AcceptAsync(ApiClient.SocialUser u)
    {
        try
        {
            await _api.AcceptFriendRequestAsync(u.Id);
            _graph.Incoming.RemoveAll(x => x.Id == u.Id);
            _graph.Friends.Insert(0, u);
            Render();
        }
        catch (Exception e) { ShowError(e); }
    }

    private async System.Threading.Tasks.Task RemoveAsync(ApiClient.SocialUser u)
    {
        try
        {
            await _api.RemoveFriendAsync(u.Id);
            _graph.Friends.RemoveAll(x => x.Id == u.Id);
            _graph.Incoming.RemoveAll(x => x.Id == u.Id);
            _graph.Outgoing.RemoveAll(x => x.Id == u.Id);
            Render();
        }
        catch (Exception e) { ShowError(e); }
    }

    private async System.Threading.Tasks.Task AddAsync(ApiClient.SocialUser u)
    {
        try
        {
            string state = await _api.SendFriendRequestAsync(u.Id);
            if (state == "accepted")
            {
                _graph.Incoming.RemoveAll(x => x.Id == u.Id);
                _graph.Friends.Insert(0, u);
            }
            else
            {
                _graph.Outgoing.Insert(0, u);
            }
            Render();
        }
        catch (Exception e) { ShowError(e); }
    }

    private async System.Threading.Tasks.Task UnblockAsync(ApiClient.SocialUser u)
    {
        try
        {
            await _api.UnblockUserAsync(u.Id);
            _blocks.RemoveAll(x => x.Id == u.Id);
            Render();
            BlocksChanged?.Invoke();
        }
        catch (Exception e) { ShowError(e); }
    }

    private async System.Threading.Tasks.Task SearchAsync()
    {
        if (_api == null || _searchBox == null) return;
        string q = _searchBox.Text;
        _status.Text = "Searching…";
        try
        {
            _searchResults = await _api.SearchUsersAsync(q);
            _status.Text = _searchResults.Count == 0 ? "No users matched." : "";
        }
        catch (Exception e) { ShowError(e); }
        Render();
    }

    private void ShowError(Exception e)
    {
        GD.PrintErr($"social panel action failed: {e.Message}");
        _status.Text = $"Action failed ({Friendly(e)})";
    }

    private static string Friendly(Exception e) =>
        e.InnerException?.Message ?? e.Message;

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape } && IsOpen)
        {
            Hide();
            GetViewport().SetInputAsHandled();
        }
    }
}
