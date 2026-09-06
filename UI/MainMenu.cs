using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SerikaSocial.UI;

namespace SerikaSocial;

/// Searchable catalogue. Cards open real world details or equip a real avatar.
public partial class MainMenu : CanvasLayer
{
    public event Action Closed;
    public event Action<string> JoinWorldPressed;
    public event Action<string, string, string> AvatarChosen;
    public event Action<int> RefreshRequested;
    public Func<string, System.Threading.Tasks.Task<byte[]>> ImageLoader;

    private PanelContainer _card;
    private Label _usernameLabel, _categoryTitle, _statusLabel, _emptyTitle, _emptyBody, _brandLabel, _footer;
    private MarginContainer _margin;
    private VBoxContainer _column;
    private bool _compact;
    private GridContainer _contentGrid;
    private VBoxContainer _emptyState;
    private ScrollContainer _scroll;
    private LineEdit _search;
    private Button _worldTab, _avatarTab, _refreshBtn, _clearBtn;
    private int _activeBottomTab = 1;
    private readonly bool[] _loading = new bool[3];
    private readonly bool[] _loaded = new bool[3];
    private readonly string[] _errors = new string[3];
    private readonly Dictionary<string, string> _worldThumbnails = new();
    private readonly Dictionary<string, Texture2D> _thumbnails = new();
    private readonly Dictionary<string, System.Threading.Tasks.Task<Texture2D>> _thumbnailLoads = new();
    private readonly List<(string id, string name, string desc, int cap, string author, string dlUrl)> _worldsCache = new();
    private readonly List<(string id, string name, string author, string thumbUrl, string dlUrl)> _avatarsCache = new();

    public override void _Ready()
    {
        Layer = 106;
        Visible = false;
        AddChild(Brand.Scrim(0.72f));
        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(center);
        _card = new PanelContainer { Name = "CatalogueCard", Theme = Brand.Theme };
        _card.AddThemeStyleboxOverride("panel", Brand.Panel(Brand.Bg1, 18, 1, Brand.Border));
        center.AddChild(_card);
        var margin = _margin = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top", "bottom" })
            margin.AddThemeConstantOverride("margin_" + side, 24);
        _card.AddChild(margin);
        var column = _column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 14);
        margin.AddChild(column);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 12);
        column.AddChild(header);
        var title = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        title.AddThemeConstantOverride("separation", 3);
        _brandLabel = Label("SERIKA SOCIAL", 11, Brand.Accent);
        title.AddChild(_brandLabel);
        _categoryTitle = Label("Find your next place", 26, Brand.TextHi);
        _categoryTitle.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        title.AddChild(_categoryTitle);
        header.AddChild(title);
        _usernameLabel = Label("", 13, Brand.TextMid);
        _usernameLabel.CustomMinimumSize = new Vector2(130, 0);
        _usernameLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        header.AddChild(_usernameLabel);
        var close = Brand.Ghost_(new Button
        {
            Icon = Icons.Get(Icons.Kind.Close, 18, Brand.TextMid),
            CustomMinimumSize = new Vector2(46, 46), TooltipText = "Close catalogue",
        });
        close.Pressed += Hide;
        header.AddChild(close);

        var navigation = new HBoxContainer();
        navigation.AddThemeConstantOverride("separation", 8);
        column.AddChild(navigation);
        _worldTab = Tab("Worlds", Icons.Kind.Globe, 1);
        _avatarTab = Tab("Avatars", Icons.Kind.Shirt, 2);
        navigation.AddChild(_worldTab);
        navigation.AddChild(_avatarTab);
        navigation.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        _refreshBtn = Brand.Ghost_(new Button
        {
            Text = "Refresh", Icon = Icons.Get(Icons.Kind.Refresh, 16, Brand.TextMid),
            CustomMinimumSize = new Vector2(110, 46), TooltipText = "Fetch the latest catalogue",
        });
        _refreshBtn.Pressed += () => RefreshRequested?.Invoke(_activeBottomTab);
        navigation.AddChild(_refreshBtn);

        _search = new LineEdit
        {
            PlaceholderText = "Search worlds, descriptions or creators…",
            CustomMinimumSize = new Vector2(0, 48), ClearButtonEnabled = true,
            RightIcon = Icons.Get(Icons.Kind.Search, 18, Brand.TextDim),
        };
        _search.TextChanged += _ => PopulateGrid();
        column.AddChild(_search);
        _statusLabel = Label("Loading worlds…", 13, Brand.TextDim);
        _statusLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        column.AddChild(_statusLabel);

        _scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            FollowFocus = true,
        };
        column.AddChild(_scroll);
        _contentGrid = new GridContainer { Columns = 3, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _contentGrid.AddThemeConstantOverride("h_separation", 12);
        _contentGrid.AddThemeConstantOverride("v_separation", 12);
        _scroll.AddChild(_contentGrid);

        _emptyState = new VBoxContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            Alignment = BoxContainer.AlignmentMode.Center,
        };
        _emptyState.AddThemeConstantOverride("separation", 12);
        column.AddChild(_emptyState);
        _emptyTitle = Label("Loading worlds…", 21, Brand.TextHi);
        _emptyTitle.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyState.AddChild(_emptyTitle);
        _emptyBody = Label("Finding places to spend time together.", 14, Brand.TextDim);
        _emptyBody.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyBody.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _emptyState.AddChild(_emptyBody);
        _clearBtn = Brand.Ghost_(new Button
        {
            Text = "Clear search", CustomMinimumSize = new Vector2(150, 46),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
        });
        _clearBtn.Pressed += () => { _search.Text = ""; PopulateGrid(); _search.GrabFocus(); };
        _emptyState.AddChild(_clearBtn);
        column.AddChild(new HSeparator());
        var footer = _footer = Label("Choose a world to view details, find an instance or make it your Home.", 12, Brand.TextDim);
        footer.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        footer.Name = "CatalogueFooter";
        column.AddChild(footer);
        GetViewport().SizeChanged += Fit;
        _scroll.Resized += FitColumns;
        Fit();
        SwitchBottomTab(1);
    }

    public override void _ExitTree() => GetViewport().SizeChanged -= Fit;
    private void Fit()
    {
        _card.CustomMinimumSize = Brand.FitCard(GetViewport(), 1080, 700);
        _usernameLabel.Visible = _card.CustomMinimumSize.X >= 700;
        bool compact = _card.CustomMinimumSize.Y < 540;
        bool changed = compact != _compact;
        _compact = compact;
        _column.AddThemeConstantOverride("separation", compact ? 8 : 14);
        foreach (var side in new[] { "left", "right", "top", "bottom" })
            _margin.AddThemeConstantOverride("margin_" + side, compact ? 16 : 24);
        _brandLabel.Visible = !compact;
        _footer.Visible = !compact;
        _categoryTitle.AddThemeFontSizeOverride("font_size", Brand.Fs(compact ? 22 : 26));
        if (changed && _contentGrid != null) PopulateGrid();
        FitColumns();
    }
    private void FitColumns()
    {
        float width = _scroll.Size.X > 0 ? _scroll.Size.X : _card.CustomMinimumSize.X - 48;
        _contentGrid.Columns = Mathf.Clamp((int)((width + 12) / 222), 1, 4);
    }
    private static Label Label(string text, int size, Color color)
    {
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", Brand.Fs(size));
        label.AddThemeColorOverride("font_color", color);
        return label;
    }
    private Button Tab(string text, Icons.Kind icon, int index)
    {
        var button = new Button
        {
            Text = text, CustomMinimumSize = new Vector2(136, 46),
            Icon = Icons.Get(icon, 18, Brand.AccentSoft),
        };
        button.AddThemeConstantOverride("h_separation", 8);
        button.Pressed += () => SwitchBottomTab(index);
        return button;
    }
    private void SwitchBottomTab(int index)
    {
        _activeBottomTab = index == 2 ? 2 : 1;
        if (_activeBottomTab == 1) { Brand.Primary_(_worldTab); Brand.Ghost_(_avatarTab); }
        else { Brand.Ghost_(_worldTab); Brand.Primary_(_avatarTab); }
        _categoryTitle.Text = _activeBottomTab == 1 ? "Find your next place" : "Make yourself at home";
        _search.PlaceholderText = _activeBottomTab == 1 ? "Search worlds, descriptions or creators…" : "Search avatars or creators…";
        _search.Text = "";
        _card.FindChild("CatalogueFooter", true, false).Set("text", _activeBottomTab == 1
            ? "Choose a world to view details, find an instance or make it your Home."
            : "Choose an avatar to wear it. Your selection is saved to your account.");
        PopulateGrid();
        _scroll.ScrollVertical = 0;
    }

    public void SetWorlds(List<(string id, string name, string desc, int cap, string author, string dlUrl)> worlds)
    {
        _worldsCache.Clear();
        if (worlds != null) _worldsCache.AddRange(worlds);
        _loaded[1] = true; _loading[1] = false; _errors[1] = null;
        if (_activeBottomTab == 1 && _contentGrid != null) PopulateGrid();
    }
    /// Optional artwork keyed by world ID, separate from the existing catalogue data contract.
    public void SetWorldThumbnails(Dictionary<string, string> thumbnails)
    {
        _worldThumbnails.Clear();
        if (thumbnails != null)
            foreach (var (id, url) in thumbnails)
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(url)) _worldThumbnails[id] = url;
        if (_activeBottomTab == 1 && _contentGrid != null && IsOpen) PopulateGrid();
    }

    public void SetAvatars(List<(string id, string name, string author, string thumbUrl, string dlUrl)> avatars)
    {
        _avatarsCache.Clear();
        if (avatars != null) _avatarsCache.AddRange(avatars);
        _loaded[2] = true; _loading[2] = false; _errors[2] = null;
        if (_activeBottomTab == 2 && _contentGrid != null) PopulateGrid();
    }
    public void SetLoading(int tab, bool loading)
    {
        tab = tab == 2 ? 2 : 1;
        _loading[tab] = loading;
        if (loading) _errors[tab] = null;
        if (_contentGrid != null && _activeBottomTab == tab) PopulateGrid();
    }
    public void SetLoadError(int tab, string message)
    {
        tab = tab == 2 ? 2 : 1;
        _loading[tab] = false;
        _errors[tab] = string.IsNullOrEmpty(message) ? "Please try again." : message;
        if (_contentGrid != null && _activeBottomTab == tab) PopulateGrid();
    }

    private bool Matches(params string[] values)
    {
        string query = _search.Text.Trim();
        return query.Length == 0 || values.Any(value => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);
    }
    private void PopulateGrid()
    {
        foreach (var child in _contentGrid.GetChildren()) { _contentGrid.RemoveChild(child); child.QueueFree(); }
        int tab = _activeBottomTab;
        bool avatars = tab == 2;
        string noun = avatars ? "avatars" : "worlds";
        int count = 0;
        if (avatars)
        {
            foreach (var avatar in _avatarsCache.Where(a => Matches(a.name, a.author)).OrderBy(a => a.name, StringComparer.OrdinalIgnoreCase))
            {
                var card = MakeCard(avatar.name, "by " + Creator(avatar.author), "Wear avatar", Icons.Kind.Shirt, avatar.thumbUrl);
                card.Pressed += () => { Hide(); AvatarChosen?.Invoke(avatar.id, avatar.dlUrl, avatar.name); };
                _contentGrid.AddChild(card); count++;
            }
        }
        else
        {
            foreach (var world in _worldsCache.Where(w => Matches(w.name, w.desc, w.author)).OrderBy(w => w.name, StringComparer.OrdinalIgnoreCase))
            {
                _worldThumbnails.TryGetValue(world.id ?? "", out var thumbnailUrl);
                var card = MakeCard(world.name, string.IsNullOrWhiteSpace(world.desc) ? "A place to spend time together." : world.desc,
                    $"by {Creator(world.author)} · up to {world.cap}", Icons.Kind.Globe, thumbnailUrl);
                card.TooltipText = $"{world.name}\n{world.desc}\nView world details and available instances";
                card.Pressed += () => { Hide(); JoinWorldPressed?.Invoke(world.id); };
                _contentGrid.AddChild(card); count++;
            }
        }
        _scroll.Visible = count > 0;
        _emptyState.Visible = count == 0;
        _refreshBtn.Disabled = _loading[tab];
        _clearBtn.Visible = !string.IsNullOrWhiteSpace(_search.Text);
        _statusLabel.AddThemeColorOverride("font_color", string.IsNullOrEmpty(_errors[tab]) ? Brand.TextDim : Brand.Warning);
        if (_loading[tab] || (!_loaded[tab] && string.IsNullOrEmpty(_errors[tab])))
        {
            _statusLabel.Text = count > 0 ? $"Refreshing {noun}… · {count} available" : $"Loading {noun}…";
            _emptyTitle.Text = $"Loading {noun}…";
            _emptyBody.Text = "Your catalogue will appear here in a moment.";
        }
        else if (!string.IsNullOrEmpty(_errors[tab]))
        {
            _statusLabel.Text = count > 0 ? "Could not refresh. Showing the saved catalogue." : $"Could not load {noun}.";
            _emptyTitle.Text = "Couldn’t reach the catalogue";
            _emptyBody.Text = _errors[tab] + "\nUse Refresh to try again.";
        }
        else
        {
            _statusLabel.Text = count == 1 ? $"1 {(avatars ? "avatar" : "world")}" : $"{count} {noun}";
            _emptyTitle.Text = _clearBtn.Visible ? "No matches yet" : $"No {noun} available";
            _emptyBody.Text = _clearBtn.Visible ? "Try another name or creator, or clear your search." : "Refresh to check for newly published content.";
        }
        FitColumns();
    }
    private static string Creator(string author) => string.IsNullOrWhiteSpace(author) ? "Community" : author;

    private Button MakeCard(string name, string description, string footer, Icons.Kind icon, string thumbUrl = null)
    {
        var button = new Button
        {
            Name = "CatalogueItem", CustomMinimumSize = new Vector2(210, _compact ? 214 : 230),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = name,
        };
        var normal = Brand.Panel(Brand.Bg2, 12, 1, Brand.BorderSoft); normal.ShadowSize = 0;
        var hover = Brand.Panel(Brand.Bg3, 12, 1, Brand.Accent); hover.ShadowSize = 0;
        button.AddThemeStyleboxOverride("normal", normal);
        button.AddThemeStyleboxOverride("hover", hover);
        button.AddThemeStyleboxOverride("pressed", hover);
        button.AddThemeStyleboxOverride("focus", Brand.FocusRing(12));
        var column = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        column.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        column.OffsetLeft = column.OffsetTop = 12;
        column.OffsetRight = column.OffsetBottom = -12;
        column.AddThemeConstantOverride("separation", 7);
        button.AddChild(column);
        var banner = new PanelContainer { CustomMinimumSize = new Vector2(0, _compact ? 64 : 80), MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true };
        uint hash = 2166136261;
        foreach (char character in name ?? "") hash = unchecked((hash ^ character) * 16777619);
        var tint = Brand.Bg3.Lerp(Brand.PrimaryLo, 0.10f + (hash % 5) * 0.055f);
        var bannerStyle = Brand.Panel(tint, 8, 0); bannerStyle.ShadowSize = 0;
        banner.AddThemeStyleboxOverride("panel", bannerStyle);
        column.AddChild(banner);
        var mark = new TextureRect
        {
            Texture = Icons.Get(icon, 32, Brand.AccentSoft),
            StretchMode = TextureRect.StretchModeEnum.KeepCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        banner.AddChild(mark);
        if (!string.IsNullOrEmpty(thumbUrl) && ImageLoader != null) _ = LoadThumb(banner, thumbUrl);
        var title = Label(name, 16, Brand.TextHi);
        title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        column.AddChild(title);
        var detail = Label(description, 13, Brand.TextMid);
        detail.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        detail.MaxLinesVisible = 2;
        detail.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        detail.CustomMinimumSize = new Vector2(0, 40);
        column.AddChild(detail);
        column.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
        var foot = Label(footer, 12, Brand.AccentSoft);
        foot.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        column.AddChild(foot);
        return button;
    }
    private async System.Threading.Tasks.Task LoadThumb(PanelContainer banner, string url)
    {
        try
        {
            if (!_thumbnails.TryGetValue(url, out var texture))
            {
                if (!_thumbnailLoads.TryGetValue(url, out var pending))
                {
                    pending = FetchThumbnail(url);
                    _thumbnailLoads[url] = pending;
                }
                texture = await pending;
                _thumbnailLoads.Remove(url);
                if (texture != null) _thumbnails[url] = texture;
            }
            if (texture == null || !IsInstanceValid(banner) || banner.IsQueuedForDeletion()) return;
            banner.AddChild(new TextureRect
            {
                Texture = texture, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, MouseFilter = Control.MouseFilterEnum.Ignore,
            });
        }
        catch (Exception exception) { _thumbnailLoads.Remove(url); GD.PrintErr($"catalogue thumbnail: {exception.Message}"); }
    }
    private async System.Threading.Tasks.Task<Texture2D> FetchThumbnail(string url)
    {
        byte[] bytes = await ImageLoader(url);
        if (bytes == null || bytes.Length == 0) return null;
        var image = new Image();
        Error error = image.LoadWebpFromBuffer(bytes);
        if (error != Error.Ok) error = image.LoadPngFromBuffer(bytes);
        if (error != Error.Ok) error = image.LoadJpgFromBuffer(bytes);
        return error == Error.Ok ? ImageTexture.CreateFromImage(image) : null;
    }
    public void Open(string username, int tab = 1)
    {
        _usernameLabel.Text = username ?? "";
        Visible = true;
        Fit();
        SwitchBottomTab(tab);
        // Start on a button in VR, so opening the catalogue does not summon a keyboard.
        (_activeBottomTab == 2 ? _avatarTab : _worldTab).CallDeferred(Control.MethodName.GrabFocus);
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
}
