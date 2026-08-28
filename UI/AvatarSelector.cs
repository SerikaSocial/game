using System;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Serika.Net;

using SerikaSocial.UI;

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

    // Detail panel
    private PanelContainer _detailCard;
    private Label _detailName;
    private Label _detailAuthor;
    private Label _detailSource;
    private Label _detailPerf;
    private Label _detailHeight;
    private Label _detailAdded;
    private Button _detailEquipBtn;
    private string _detailId;
    private string _detailDownloadUrl;
    private string _detailDisplayName;

    private static readonly string[] SourceLabels = { "Built-in", "VRM", "glTF", "FBX", "PMX" };
    private static readonly string[] PerfLabels = { "Excellent", "Good", "Medium", "Poor", "Very Poor" };

    public void Configure(ApiClient api, string currentAvatarId)
    {
        _api = api;
        _currentAvatarId = currentAvatarId;
    }

    public override void _Ready()
    {
        Layer = 95; // above the pause menu (90) so it stacks on top when opened from it
        Visible = false;

        _scrim = Brand.Scrim(0.82f);
        AddChild(_scrim);

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(center);

        _card = new PanelContainer();
        _card.AddThemeStyleboxOverride("panel", Brand.Panel(Brand.Bg1, 16));
        _card.CustomMinimumSize = Brand.Card(920, 620);
        center.AddChild(_card);

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
        title.AddThemeFontSizeOverride("font_size", Brand.Fs(22));
        title.AddThemeColorOverride("font_color", Brand.TextHi);
        header.AddChild(title);

        header.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        _allTab = Brand.Ghost_(new Button { Text = "Catalogue" });
        _allTab.Pressed += () => SwitchTab(false);
        header.AddChild(_allTab);

        _mineTab = Brand.Ghost_(new Button { Text = "My uploads" });
        _mineTab.Pressed += () => SwitchTab(true);
        header.AddChild(_mineTab);

        var close = Brand.Ghost_(new Button { Icon = Icons.Get(Icons.Kind.Close, 14, Brand.TextMid) });
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

        BuildDetailPanel();
    }

    private void BuildDetailPanel()
    {
        _detailCard = new PanelContainer { Visible = false };
        _detailCard.AddThemeStyleboxOverride("panel", Brand.Panel(Brand.Bg1, 16));
        _detailCard.CustomMinimumSize = new Vector2(500, 400);

        // We can't add to the same CenterContainer, so add as a direct child on the layer.
        // Position it centered.
        var detailCenter = new CenterContainer { Visible = false };
        detailCenter.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(detailCenter);
        detailCenter.AddChild(_detailCard);

        var pad = new MarginContainer();
        foreach (var s in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            pad.AddThemeConstantOverride(s, 28);
        _detailCard.AddChild(pad);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 14);
        pad.AddChild(col);

        // Header with back button
        var headerRow = new HBoxContainer();
        headerRow.AddThemeConstantOverride("separation", 12);
        col.AddChild(headerRow);

        var backBtn = Brand.Ghost_(new Button { Text = "← Back" });
        backBtn.Pressed += () =>
        {
            _detailCard.Visible = false;
            _detailCard.GetParent<CenterContainer>().Visible = false;
            _card.Visible = true;
        };
        headerRow.AddChild(backBtn);

        _detailName = new Label { Text = "" };
        _detailName.AddThemeFontSizeOverride("font_size", Brand.Fs(24));
        _detailName.AddThemeColorOverride("font_color", Brand.TextHi);
        headerRow.AddChild(_detailName);

        _detailAuthor = new Label { Text = "" };
        _detailAuthor.AddThemeFontSizeOverride("font_size", Brand.Fs(14));
        _detailAuthor.AddThemeColorOverride("font_color", Brand.TextDim);
        col.AddChild(_detailAuthor);

        col.AddChild(new HSeparator());

        // Stats grid (2 columns)
        var statsGrid = new GridContainer { Columns = 2 };
        statsGrid.AddThemeConstantOverride("h_separation", 24);
        statsGrid.AddThemeConstantOverride("v_separation", 10);
        col.AddChild(statsGrid);

        void AddStat(string label, out Label valueLabel)
        {
            var lbl = new Label { Text = label };
            lbl.AddThemeFontSizeOverride("font_size", Brand.Fs(12));
            lbl.AddThemeColorOverride("font_color", Brand.TextDim);
            statsGrid.AddChild(lbl);
            valueLabel = new Label { Text = "—" };
            valueLabel.AddThemeFontSizeOverride("font_size", Brand.Fs(14));
            valueLabel.AddThemeColorOverride("font_color", Brand.TextHi);
            statsGrid.AddChild(valueLabel);
        }

        AddStat("Source", out _detailSource);
        AddStat("Performance", out _detailPerf);
        AddStat("Height", out _detailHeight);
        AddStat("Added", out _detailAdded);

        col.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });

        // Equip button
        _detailEquipBtn = Brand.Primary_(new Button { Text = "Equip", CustomMinimumSize = new Vector2(0, 48) });
        _detailEquipBtn.Pressed += () =>
        {
            if (string.IsNullOrEmpty(_detailDownloadUrl)) return;
            _currentAvatarId = _detailId;
            AvatarChosen?.Invoke(_detailId, _detailDownloadUrl, _detailDisplayName);
            Hide();
        };
        col.AddChild(_detailEquipBtn);
    }

    private void ShowDetail(JsonElement a)
    {
        _detailId = a.GetProperty("id").GetString() ?? "";
        _detailDisplayName = Prop(a, "name") ?? "Untitled";
        _detailDownloadUrl = Prop(a, "downloadUrl");
        string author = Prop(a, "author") ?? "unknown";
        bool isCurrent = _detailId == _currentAvatarId;

        _detailName.Text = _detailDisplayName;
        _detailAuthor.Text = $"by {author}";

        int srcFmt = a.TryGetProperty("sourceFormat", out var sf) ? sf.GetInt32() : 0;
        _detailSource.Text = srcFmt < SourceLabels.Length ? SourceLabels[srcFmt] : "Unknown";

        int perf = a.TryGetProperty("perfRank", out var pr) ? pr.GetInt32() : 0;
        _detailPerf.Text = perf < PerfLabels.Length ? PerfLabels[perf] : "—";

        if (a.TryGetProperty("heightMeters", out var hm) && hm.ValueKind == JsonValueKind.Number)
            _detailHeight.Text = $"{hm.GetDouble():F2} m";
        else
            _detailHeight.Text = "—";

        if (a.TryGetProperty("createdAt", out var ca) && ca.ValueKind == JsonValueKind.String)
        {
            if (DateTime.TryParse(ca.GetString(), out var dt))
                _detailAdded.Text = dt.ToString("yyyy-MM-dd");
            else
                _detailAdded.Text = ca.GetString() ?? "—";
        }
        else
            _detailAdded.Text = "—";

        if (isCurrent)
        {
            _detailEquipBtn.Text = "Equipped";
            _detailEquipBtn.Icon = Icons.Get(Icons.Kind.Check, 15, Brand.TextHi);
            _detailEquipBtn.Disabled = true;
        }
        else
        {
            _detailEquipBtn.Text = "Equip";
            _detailEquipBtn.Disabled = string.IsNullOrEmpty(_detailDownloadUrl);
        }

        _card.Visible = false;
        _detailCard.Visible = true;
        _detailCard.GetParent<CenterContainer>().Visible = true;
    }

    public void Open()
    {
        _scrim.Visible = true;
        _card.Visible = true;
        _detailCard.Visible = false;
        _detailCard.GetParent<CenterContainer>().Visible = false;
        Visible = true;
        SwitchTab(false);
    }

    public new void Hide()
    {
        _scrim.Visible = false;
        _card.Visible = false;
        _detailCard.Visible = false;
        _detailCard.GetParent<CenterContainer>().Visible = false;
        Visible = false;
        Closed?.Invoke();
    }

    public bool IsOpen => Visible;

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape } && IsOpen)
        {
            // If detail is showing, go back to grid; otherwise close entirely
            if (_detailCard.Visible)
            {
                _detailCard.Visible = false;
                _detailCard.GetParent<CenterContainer>().Visible = false;
                _card.Visible = true;
                GetViewport().SetInputAsHandled();
                return;
            }
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
        nameLabel.AddThemeFontSizeOverride("font_size", Brand.Fs(14));
        nameLabel.AddThemeColorOverride("font_color", Brand.TextHi);
        inner.AddChild(nameLabel);

        var authorLabel = new Label { Text = $"by {author}" };
        authorLabel.AddThemeFontSizeOverride("font_size", Brand.Fs(11));
        authorLabel.AddThemeColorOverride("font_color", Brand.TextDim);
        inner.AddChild(authorLabel);

        // "View" button that opens the detail panel
        var viewBtn = Brand.Primary_(new Button { Text = isCurrent ? "Equipped" : "View", Icon = isCurrent ? Icons.Get(Icons.Kind.Check, 14, Brand.TextHi) : null });
        // Capture the JsonElement for this avatar
        var avatarData = a;
        viewBtn.Pressed += () => ShowDetail(avatarData);
        inner.AddChild(viewBtn);
    }

    private async Task LoadThumbAsync(TextureRect target, string url)
    {
        byte[] bytes = await _api.GetImageBytesAsync(url);
        if (bytes == null || !IsInstanceValid(target)) return;

        // The image proxy serves WebP by default (URL ends "&output=webp", not ".webp"), so
        // we can't pick a decoder by extension — try them in likelihood order until one works.
        var img = new Image();
        Error err = img.LoadWebpFromBuffer(bytes);
        if (err != Error.Ok) err = img.LoadPngFromBuffer(bytes);
        if (err != Error.Ok) err = img.LoadJpgFromBuffer(bytes);
        if (err != Error.Ok || !IsInstanceValid(target)) return;

        target.Texture = ImageTexture.CreateFromImage(img);
    }

    private static string Prop(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
