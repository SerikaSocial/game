using Godot;
using System;
using System.Text.Json;
using SerikaSocial.UI;

namespace SerikaSocial;

public partial class Hud
{
    public event Action<string> SetHomePressed;
    public event Action ResetHomePressed;
    public event Action<string> NewPrivateInstancePressed;
    public event Action<string, string> JoinInstancePressed;
    public event Action BrowseWorldsPressed;
    public event Action<string> RefreshWorldPressed;
    public bool WorldDetailVisible => _worldDetailPanel?.Visible ?? false;
    public string DetailWorldId => _detailWorldId;
    private Button _detailHomeButton;
    private Button _detailResetHomeButton;
    private Button _detailPrivateButton;
    private Label _detailActionStatus;
    private string _selectedHomeId;
    private bool _detailHomeBusy;
    private bool _detailCanSaveHome;
    private bool _detailHasThumbnail;
    public Func<string, System.Threading.Tasks.Task<byte[]>> ImageLoader { get; set; }

    private static Label DetailLabel(string text, int size, Color color)
    {
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", Brand.Fs(size));
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    private static Button DetailButton(string text, Action action, bool primary = false)
    {
        var button = new Button { Text = text, CustomMinimumSize = new Vector2(0, 46), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        if (primary) Brand.Primary_(button); else Brand.Ghost_(button);
        button.AddThemeFontSizeOverride("font_size", Brand.Fs(15));
        button.Pressed += action;
        return button;
    }

    private void BuildWorldDetailPanel()
    {
        var size = Brand.FitCard(GetViewport(), 900, 680) * 0.5f;
        _worldDetailPanel = new Panel
        {
            Name = "WorldDetail", Visible = false, Theme = Brand.Theme,
            AnchorLeft = .5f, AnchorTop = .5f, AnchorRight = .5f, AnchorBottom = .5f,
            OffsetLeft = -size.X, OffsetTop = -size.Y, OffsetRight = size.X, OffsetBottom = size.Y,
        };
        _worldDetailPanel.AddThemeStyleboxOverride("panel", Brand.Panel(Brand.Bg1, 18, 1, Brand.Border));
        AddChild(_worldDetailPanel);
        GetViewport().SizeChanged += FitWorldDetail;
        var content = new VBoxContainer { AnchorRight = 1, AnchorBottom = 1, OffsetLeft = 24, OffsetTop = 20, OffsetRight = -24, OffsetBottom = -20 };
        content.AddThemeConstantOverride("separation", 12);
        _worldDetailPanel.AddChild(content);

        var navigation = new HBoxContainer();
        navigation.AddThemeConstantOverride("separation", 10);
        content.AddChild(navigation);
        navigation.AddChild(DetailButton("← Worlds", () => BrowseWorldsPressed?.Invoke()));
        navigation.AddChild(DetailButton("Refresh", () => { if (_detailWorldId != null) RefreshWorldPressed?.Invoke(_detailWorldId); }));
        navigation.AddChild(DetailButton("Close", () => WorldListClosed?.Invoke()));
        _detailName = DetailLabel("World", 28, Brand.TextHi);
        _detailName.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        content.AddChild(_detailName);
        _detailStats = DetailLabel("", 13, Brand.TextMid);
        _detailStats.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        content.AddChild(_detailStats);

        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        content.AddChild(scroll);
        var body = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 12);
        scroll.AddChild(body);
        _detailBanner = new TextureRect { CustomMinimumSize = new Vector2(0, 70), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale };
        body.AddChild(_detailBanner);
        _detailTagsRow = new HFlowContainer();
        _detailTagsRow.AddThemeConstantOverride("h_separation", 8);
        body.AddChild(_detailTagsRow);
        _detailDesc = DetailLabel("", 15, Brand.TextMid);
        _detailDesc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(_detailDesc);
        body.AddChild(new HSeparator());
        body.AddChild(DetailLabel("Public instances", 18, Brand.TextHi));
        _detailInstanceList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _detailInstanceList.AddThemeConstantOverride("separation", 8);
        body.AddChild(_detailInstanceList);

        content.AddChild(new HSeparator());
        _detailActionStatus = DetailLabel("Private instances are invite only. Their owner chooses who can join.", 13, Brand.TextMid);
        _detailActionStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        content.AddChild(_detailActionStatus);
        var homeActions = new HBoxContainer();
        homeActions.AddThemeConstantOverride("separation", 10);
        content.AddChild(homeActions);
        _detailHomeButton = DetailButton("Set as Home", () => SetHomePressed?.Invoke(_detailWorldId));
        _detailHomeButton.Name = "SetHome";
        _detailResetHomeButton = DetailButton("Use default Home", () => ResetHomePressed?.Invoke());
        _detailResetHomeButton.Name = "ResetHome";
        homeActions.AddChild(_detailHomeButton);
        homeActions.AddChild(_detailResetHomeButton);
        var travelActions = new HBoxContainer();
        travelActions.AddThemeConstantOverride("separation", 10);
        content.AddChild(travelActions);
        _detailPrivateButton = DetailButton("New private instance", () => NewPrivateInstancePressed?.Invoke(_detailWorldId));
        _detailPrivateButton.Name = "NewPrivate";
        _detailJoinButton = DetailButton("Join public", () => JoinWorldFromDetailPressed?.Invoke(_detailWorldId), true);
        _detailJoinButton.Name = "JoinPublic";
        travelActions.AddChild(_detailPrivateButton);
        travelActions.AddChild(_detailJoinButton);
    }

    private void FitWorldDetail()
    {
        var half = Brand.FitCard(GetViewport(), 900, 680) * .5f;
        _worldDetailPanel.OffsetLeft = -half.X;
        _worldDetailPanel.OffsetRight = half.X;
        _worldDetailPanel.OffsetTop = -half.Y;
        _worldDetailPanel.OffsetBottom = half.Y;
        if (_detailBanner != null)
        {
            _detailBanner.Visible = _detailHasThumbnail && half.Y >= 270;
            _detailBanner.CustomMinimumSize = new Vector2(0, half.Y >= 300 ? 110 : 70);
        }
    }

    public override void _ExitTree() => GetViewport().SizeChanged -= FitWorldDetail;

    public void ShowWorldDetail(JsonElement world)
    {
        _detailWorldId = world.GetProperty("id").GetString();
        string name = JsonText(world, "name", "Untitled world");
        string author = JsonText(world, "author", "Community");
        int capacity = JsonInt(world, "capacity", 16);
        _detailName.Text = name;
        _detailStats.Text = $"By {author}  ·  Up to {capacity} people";
        _detailDesc.Text = JsonText(world, "description", "Explore this community world.");
        _detailCanSaveHome = JsonInt(world, "releaseStatus", 2) >= 1 && !string.IsNullOrEmpty(JsonText(world, "downloadUrl"));
        _detailPrivateButton.Disabled = !_detailCanSaveHome;
        _detailJoinButton.Disabled = !_detailCanSaveHome;
        string thumbnail = JsonText(world, "thumbnailUrl");
        _detailHasThumbnail = !string.IsNullOrEmpty(thumbnail);
        float hue = WorldHue(name) / 360f;
        var gradient = new Gradient();
        gradient.SetColor(0, Color.FromHsv(hue, .24f, .28f));
        gradient.SetColor(1, Brand.Bg2);
        _detailBanner.Texture = new GradientTexture2D { Gradient = gradient, FillFrom = Vector2.Zero, FillTo = Vector2.One, Width = 128, Height = 64 };
        _detailBanner.StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered;
        FitWorldDetail();
        if (_detailHasThumbnail && ImageLoader != null) _ = LoadWorldThumbnail(_detailWorldId, thumbnail);
        foreach (Node child in _detailTagsRow.GetChildren()) { _detailTagsRow.RemoveChild(child); child.QueueFree(); }
        if (world.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            int count = 0;
            foreach (var tag in tags.EnumerateArray())
            {
                if (++count > 8) break;
                if (tag.ValueKind != JsonValueKind.String) continue;
                string text = tag.GetString();
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (text.Length > 36) text = text[..33] + "…";
                var pill = new PanelContainer();
                pill.AddThemeStyleboxOverride("panel", Brand.Panel(Brand.Bg2, 8));
                pill.AddChild(DetailLabel("  " + text + "  ", 12, Brand.AccentSoft));
                _detailTagsRow.AddChild(pill);
            }
        }
        foreach (Node child in _detailInstanceList.GetChildren()) { _detailInstanceList.RemoveChild(child); child.QueueFree(); }
        int visibleInstances = 0;
        if (world.TryGetProperty("instances", out var instances) && instances.ValueKind == JsonValueKind.Array)
        {
            foreach (var instance in instances.EnumerateArray())
            {
                // Public detail never exposes private rooms, even when talking to an older API.
                if (JsonInt(instance, "access") != 0) continue;
                string instanceId = JsonText(instance, "id");
                if (string.IsNullOrEmpty(instanceId)) continue;
                visibleInstances++;
                int players = JsonInt(instance, "playerCount"), cap = JsonInt(instance, "capacity", capacity);
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 12);
                var label = DetailLabel($"{JsonText(instance, "region", "Public")}  ·  {players}/{cap} people", 14, Brand.TextHi);
                label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
                row.AddChild(label);
                string worldId = _detailWorldId;
                var join = DetailButton(players >= cap ? "Full" : "Join", () => JoinInstancePressed?.Invoke(instanceId, worldId));
                join.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
                join.CustomMinimumSize = new Vector2(92, 44);
                join.Disabled = players >= cap;
                row.AddChild(join);
                _detailInstanceList.AddChild(row);
            }
        }
        if (visibleInstances == 0)
        {
            var empty = DetailLabel("No public instances yet. Join public to start one, or invite friends to a private instance.", 14, Brand.TextMid);
            empty.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _detailInstanceList.AddChild(empty);
        }
        SetHomeSelection(_selectedHomeId, _detailHomeBusy);
        SetWorldActionStatus("Private instances are invite only. Their owner chooses who can join.");
        Visible = true;
        _scrim.Visible = false;
        _loginScreen.Visible = false;
        if (_homeButton != null) _homeButton.Visible = false;
        _homePanel.Visible = false;
        _worldDetailPanel.Visible = true;
        _detailJoinButton.GrabFocus();
    }

    private async System.Threading.Tasks.Task LoadWorldThumbnail(string worldId, string url)
    {
        try
        {
            byte[] bytes = await ImageLoader(url);
            if (bytes == null || !IsInstanceValid(this) || IsQueuedForDeletion() || _detailWorldId != worldId) return;
            using var image = new Image();
            var error = image.LoadWebpFromBuffer(bytes);
            if (error != Error.Ok) error = image.LoadPngFromBuffer(bytes);
            if (error != Error.Ok) error = image.LoadJpgFromBuffer(bytes);
            if (error == Error.Ok) _detailBanner.Texture = ImageTexture.CreateFromImage(image);
        }
        catch (Exception) { /* A missing preview does not block the world or its actions. */ }
    }

    public void SetHomeSelection(string worldId, bool busy = false)
    {
        _selectedHomeId = worldId;
        _detailHomeBusy = busy;
        if (_detailHomeButton == null) return;
        bool chosen = worldId != null && worldId == _detailWorldId;
        _detailHomeButton.Text = chosen ? "Your Home" : "Set as Home";
        _detailHomeButton.Disabled = busy || chosen || !_detailCanSaveHome;
        _detailResetHomeButton.Disabled = busy || worldId == null;
    }

    public void SetWorldActionStatus(string message, bool error = false)
    {
        if (_detailActionStatus == null) return;
        _detailActionStatus.Text = message;
        _detailActionStatus.AddThemeColorOverride("font_color", error ? Brand.Danger : Brand.TextMid);
    }

    private static string JsonText(JsonElement item, string key, string fallback = null) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : fallback;
    private static int JsonInt(JsonElement item, string key, int fallback = 0) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : fallback;
}
