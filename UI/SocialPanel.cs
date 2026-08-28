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

    private enum SocialPanelTab { Friends, Requests, Blocked, Add }

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
        _ = ReloadAsync();
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

        switch (_tab)
        {
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
            AddRow(u.Name, $"@{u.Username}", "Unfriend", () => _ = RemoveAsync(u));
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
