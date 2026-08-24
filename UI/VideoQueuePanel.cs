using System;
using Godot;
using SerikaSocial.World.Video;

namespace SerikaSocial.UI;

/// The video queue, docked to the right side of the screen. Opened from the pause menu, but
/// only in worlds that actually have a screen — the pause menu hides its "Video" button
/// otherwise. Lets anyone paste a URL to queue, see what's playing and what's next, skip, and
/// remove items.
///
/// It never captures gameplay input on its own; it takes an InputMode hold while visible like
/// every other overlay, so the cursor is free to use it.
public partial class VideoQueuePanel : CanvasLayer
{
    private VideoManager _manager;
    private PanelContainer _card;
    private LineEdit _urlInput;
    private VBoxContainer _list;
    private Label _nowPlaying;

    public event Action Closed;

    public override void _Ready()
    {
        Layer = 88; // just under the pause menu (90) so Esc-menu wins if both are somehow up

        _card = new PanelContainer
        {
            AnchorLeft = 1, AnchorRight = 1, AnchorTop = 0, AnchorBottom = 1,
            OffsetLeft = -360, OffsetRight = -16, OffsetTop = 60, OffsetBottom = -60,
            Visible = false,
        };
        _card.AddThemeStyleboxOverride("panel", Brand.Panel(Brand.Bg1, 16, 1, Brand.Border));
        AddChild(_card);

        var margin = new MarginContainer();
        foreach (var s in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(s, 16);
        _card.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 12);
        margin.AddChild(vbox);

        var header = new HBoxContainer();
        var title = new Label { Text = "📺 Video Queue", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", 18);
        title.AddThemeColorOverride("font_color", Brand.TextHi);
        header.AddChild(title);
        var closeBtn = Brand.Ghost_(new Button { Text = "✕", CustomMinimumSize = new Vector2(36, 32) });
        closeBtn.Pressed += Hide;
        header.AddChild(closeBtn);
        vbox.AddChild(header);

        _nowPlaying = new Label { Text = "Nothing playing", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _nowPlaying.AddThemeFontSizeOverride("font_size", 13);
        _nowPlaying.AddThemeColorOverride("font_color", Brand.Accent);
        vbox.AddChild(_nowPlaying);

        // Add-by-URL row.
        var addRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        addRow.AddThemeConstantOverride("separation", 8);
        _urlInput = new LineEdit
        {
            PlaceholderText = "Paste a video URL…",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _urlInput.TextSubmitted += _ => AddCurrent();
        addRow.AddChild(_urlInput);
        var addBtn = Brand.Primary_(new Button { Text = "Queue" });
        addBtn.Pressed += AddCurrent;
        addRow.AddChild(addBtn);
        vbox.AddChild(addRow);

        var controls = new HBoxContainer();
        controls.AddThemeConstantOverride("separation", 8);
        var skipBtn = Brand.Ghost_(new Button { Text = "⏭ Skip" });
        skipBtn.Pressed += () => _manager?.Skip();
        controls.AddChild(skipBtn);
        var clearBtn = Brand.Ghost_(new Button { Text = "🗑 Clear" });
        clearBtn.Pressed += () => _manager?.Clear();
        controls.AddChild(clearBtn);
        vbox.AddChild(controls);

        vbox.AddChild(new HSeparator());

        var upNext = new Label { Text = "Up next" };
        upNext.AddThemeFontSizeOverride("font_size", 12);
        upNext.AddThemeColorOverride("font_color", Brand.TextDim);
        vbox.AddChild(upNext);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        vbox.AddChild(scroll);
        _list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_list);

        // A quiet note that YouTube etc. won't decode yet — better than a user staring at a
        // black screen wondering if they did something wrong.
        var note = new Label
        {
            Text = "Plays direct Ogg Theora (.ogv) links. YouTube/mp4 can't decode in-engine yet — " +
                   "they'll skip with a note in the error log.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        note.AddThemeFontSizeOverride("font_size", 11);
        note.AddThemeColorOverride("font_color", Brand.TextDim);
        vbox.AddChild(note);
    }

    /// Point the panel at the current world's manager (or null when the world has no screens).
    public void Bind(VideoManager manager)
    {
        if (_manager != null) _manager.QueueChanged -= Redraw;
        _manager = manager;
        if (_manager != null) _manager.QueueChanged += Redraw;
        Redraw();
    }

    public bool HasVideo => _manager is { HasScreens: true };
    public bool IsOpen => _card.Visible;

    public void Open()
    {
        if (_manager == null) return;
        _card.Visible = true;
        Redraw();
        InputMode.Hold(InputMode.Settings); // reuse a cursor-freeing hold; distinct from the pause menu
    }

    public new void Hide()
    {
        if (!_card.Visible) return;
        _card.Visible = false;
        InputMode.Release(InputMode.Settings);
        Closed?.Invoke();
    }

    private void AddCurrent()
    {
        string url = _urlInput.Text.Trim();
        if (string.IsNullOrEmpty(url)) return;
        _manager?.Enqueue(url, "you");
        _urlInput.Text = "";
    }

    private void Redraw()
    {
        if (_nowPlaying == null) return;
        _nowPlaying.Text = _manager?.NowPlaying != null
            ? $"▶ {_manager.NowPlaying.Title}"
            : "Nothing playing";

        foreach (var c in _list.GetChildren()) c.QueueFree();
        if (_manager == null) return;

        for (int i = 0; i < _manager.Queue.Count; i++)
        {
            var item = _manager.Queue[i];
            int index = i;
            var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            row.AddThemeConstantOverride("separation", 8);

            var label = new Label
            {
                Text = $"{i + 1}. {item.Title}",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            label.AddThemeFontSizeOverride("font_size", 12);
            label.AddThemeColorOverride("font_color", Brand.TextMid);
            row.AddChild(label);

            var rm = Brand.Ghost_(new Button { Text = "✕", CustomMinimumSize = new Vector2(30, 26) });
            rm.Pressed += () => _manager?.Remove(index);
            row.AddChild(rm);

            _list.AddChild(row);
        }
    }
}
