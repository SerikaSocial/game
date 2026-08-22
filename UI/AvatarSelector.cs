using System;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Serika.Net;

namespace SerikaSocial;

/// In-world avatar picker — a VRChat-style grid of avatar cards (thumbnail, name, author)
/// with tabs for the public catalogue vs the player's own uploads. Opened from the pause
/// menu; equipping fires `AvatarChosen(id, downloadUrl)` for Main to download the `.ska`,
/// swap the live avatar, and persist the choice server-side.
///
/// Built entirely in code, themed through Brand, matching the rest of the client (no .tscn).
public partial class AvatarSelector : CanvasLayer
{
    /// (avatarId, skaDownloadUrl, displayName) of the avatar the player equipped.
    public event Action<string, string, string> AvatarChosen;
    public event Action Closed;

    private ApiClient _api;
    private string _currentAvatarId;

    private ColorRect _scrim;
    private PanelContainer _card;
    private GridContainer _grid;
    private Label _status;
    private Button _allTab;
    private Button _mineTab;
    private bool _showingMine;
    private bool _loading;

    public void Configure(ApiClient api, string currentAvatarId)
    {
        _api = api;
        _currentAvatarId = currentAvatarId;
    }

    public override void _Ready()
    {
        Layer = 95; // above the pause menu (90) so it stacks on top when opened from it
        Visible = false;

        _scrim = new ColorRect { Color = new Color(0.02f, 0.03f, 0.05f, 0.82f) };
        _scrim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_scrim);

        _card = new PanelContainer();
        _card.AddThemeStyleboxOverride("panel", Brand.Panel(Brand.Bg1, 16));
        _card.SetAnchorsPreset(Control.LayoutPreset.Center);
        _card.CustomMinimumSize = new Vector2(860, 620);
        AddChild(_card);

        var pad = new MarginContainer();
        foreach (var s in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            pad.AddThemeConstantOverride(s, 24);
        _card.AddChild(pad);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 14);
        pad.AddChild(col);

        // Header row: title + tabs + close.
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 12);
        col.AddChild(header);

        var title = new Label { Text = "Choose your avatar" };
        title.AddThemeFontSizeOverride("font_size", 22);
        title.AddThemeColorOverride("font_color", Brand.TextHi);
        header.AddChild(title);

        header.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        _allTab = Brand.Ghost_(new Button { Text = "Catalogue" });
        _allTab.Pressed += () => SwitchTab(false);
        header.AddChild(_allTab);

        _mineTab = Brand.Ghost_(new Button { Text = "My uploads" });
        _mineTab.Pressed += () => SwitchTab(true);
        header.AddChild(_mineTab);

        var close = Brand.Ghost_(new Button { Text = "✕" });
        close.Pressed += Hide;
        header.AddChild(close);

        _status = new Label { Text = "" };
        _status.AddThemeColorOverride("font_color", Brand.TextDim);
        col.AddChild(_status);

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        col.AddChild(scroll);

        _grid = new GridContainer
        {
            Columns = 4,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _grid.AddThemeConstantOverride("h_separation", 14);
        _grid.AddThemeConstantOverride("v_separation", 14);
        scroll.AddChild(_grid);
    }

    public void Open()
    {
        _scrim.Visible = true;
        _card.Visible = true;
        Visible = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        SwitchTab(false);
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

    private void SwitchTab(bool mine)
    {
        _showingMine = mine;
        HighlightTabs();
        _ = LoadAsync();
    }

    private void HighlightTabs()
    {
        // Re-skin the active tab as the filled primary so the current view is obvious.
        (_showingMine ? _mineTab : _allTab).AddThemeStyleboxOverride("normal",
            Brand.Panel(Brand.Primary, 10, 1, Brand.PrimaryHi));
        (_showingMine ? _allTab : _mineTab).AddThemeStyleboxOverride("normal",
            Brand.Panel(Brand.Bg2, 10, 1, Brand.BorderSoft));
    }

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        _status.Text = "Loading avatars…";
        foreach (var c in _grid.GetChildren()) c.QueueFree();

        JsonElement avatars = await _api.GetAvatarsAsync(_showingMine);
        int count = avatars.GetArrayLength();
        _status.Text = count == 0
            ? (_showingMine ? "You haven't uploaded any avatars yet. Upload on social.serika.dev."
                            : "No avatars available.")
            : $"{count} avatar{(count == 1 ? "" : "s")}";

        foreach (var a in avatars.EnumerateArray())
            AddCard(a);

        _loading = false;
    }

    private void AddCard(JsonElement a)
    {
        string id = a.GetProperty("id").GetString() ?? "";
        string name = Prop(a, "name") ?? "Untitled";
        string author = Prop(a, "author") ?? "unknown";
        string downloadUrl = Prop(a, "downloadUrl");
        string thumbUrl = Prop(a, "thumbnailUrl");
        bool isCurrent = id == _currentAvatarId;

        var card = new PanelContainer { CustomMinimumSize = new Vector2(184, 250) };
        card.AddThemeStyleboxOverride("panel",
            Brand.Panel(isCurrent ? Brand.Bg3 : Brand.Bg2, 12, 1, isCurrent ? Brand.Accent : Brand.BorderSoft));
        _grid.AddChild(card);

        var pad = new MarginContainer();
        foreach (var s in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            pad.AddThemeConstantOverride(s, 10);
        card.AddChild(pad);

        var inner = new VBoxContainer();
        inner.AddThemeConstantOverride("separation", 8);
        pad.AddChild(inner);

        // Thumbnail (square-ish). Placeholder tint until the image streams in.
        var thumb = new TextureRect
        {
            CustomMinimumSize = new Vector2(0, 150),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        var ph = new StyleBoxFlat { BgColor = Brand.Bg0 };
        ph.CornerRadiusTopLeft = ph.CornerRadiusTopRight = 8;
        var thumbFrame = new PanelContainer();
        thumbFrame.AddThemeStyleboxOverride("panel", ph);
        thumbFrame.CustomMinimumSize = new Vector2(0, 150);
        thumbFrame.AddChild(thumb);
        inner.AddChild(thumbFrame);
        if (!string.IsNullOrEmpty(thumbUrl))
            _ = LoadThumbAsync(thumb, thumbUrl);

        var nameLabel = new Label
        {
            Text = name,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            CustomMinimumSize = new Vector2(160, 0),
        };
        nameLabel.AddThemeFontSizeOverride("font_size", 14);
        nameLabel.AddThemeColorOverride("font_color", Brand.TextHi);
        inner.AddChild(nameLabel);

        var authorLabel = new Label { Text = $"by {author}" };
        authorLabel.AddThemeFontSizeOverride("font_size", 11);
        authorLabel.AddThemeColorOverride("font_color", Brand.TextDim);
        inner.AddChild(authorLabel);

        var equip = isCurrent
            ? Brand.Ghost_(new Button { Text = "Equipped ✓" })
            : Brand.Primary_(new Button { Text = "Equip" });
        equip.Disabled = isCurrent || string.IsNullOrEmpty(downloadUrl);
        equip.Pressed += () =>
        {
            if (string.IsNullOrEmpty(downloadUrl)) return;
            _currentAvatarId = id;
            AvatarChosen?.Invoke(id, downloadUrl, name);
            Hide();
        };
        inner.AddChild(equip);
    }

    private async Task LoadThumbAsync(TextureRect target, string url)
    {
        byte[] bytes = await _api.GetImageBytesAsync(url);
        if (bytes == null || !IsInstanceValid(target)) return;

        var img = new Image();
        Error err = url.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? img.LoadPngFromBuffer(bytes)
            : url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
                ? img.LoadWebpFromBuffer(bytes)
                : img.LoadJpgFromBuffer(bytes);
        // Fall back across decoders — thumbnails come in mixed formats.
        if (err != Error.Ok) err = img.LoadPngFromBuffer(bytes);
        if (err != Error.Ok) err = img.LoadJpgFromBuffer(bytes);
        if (err != Error.Ok || !IsInstanceValid(target)) return;

        target.Texture = ImageTexture.CreateFromImage(img);
    }

    private static string Prop(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
