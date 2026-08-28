using System;
using Godot;
using SerikaSocial.World.Video;

using SerikaSocial.UI;

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
        Layer = 94; // above the pause menu and settings so it is fully clickable

        // The layer starts hidden, not just the card.
        //
        // `Open()` sets both, but it bails out when there is no `VideoManager` — which is every
        // world without a video screen — and `Hide()` bails out when the card is already hidden,
        // so nothing ever turned this layer off. A `CanvasLayer` is created visible, so on the VR
        // panel it reported "something is visible on me" from `_Ready` onward, which is what
        // `VrUiSurface.HasInteractiveUi` uses to decide whether to render the panel and light the
        // laser pointer. Result: an empty slab and a laser in the player's face in every world.
        Visible = false;

        _card = new PanelContainer
        {
            AnchorLeft = 1, AnchorRight = 1, AnchorTop = 0, AnchorBottom = 1,
            OffsetLeft = -380, OffsetRight = -16, OffsetTop = 50, OffsetBottom = -50,
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop,
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
        var title = new Label { Text = "Video Queue", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", Brand.Fs(18));
        title.AddThemeColorOverride("font_color", Brand.TextHi);
        header.AddChild(title);
        var closeBtn = Brand.Ghost_(new Button { CustomMinimumSize = new Vector2(36, 32), Icon = Icons.Get(Icons.Kind.Close, 14, Brand.TextMid) });
        closeBtn.Pressed += Hide;
        header.AddChild(closeBtn);
        vbox.AddChild(header);

        _nowPlaying = new Label { Text = "Nothing playing", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _nowPlaying.AddThemeFontSizeOverride("font_size", Brand.Fs(13));
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
        var clearBtn = Brand.Ghost_(new Button { Text = "Clear", Icon = Icons.Get(Icons.Kind.Trash, 16, Brand.TextMid) });
        clearBtn.Pressed += () => _manager?.Clear();
        controls.AddChild(clearBtn);

        var logBtn = Brand.Ghost_(new Button { Text = "Error Log", Icon = Icons.Get(Icons.Kind.Doc, 16, Brand.TextMid) });
        logBtn.Pressed += ToggleLog;
        controls.AddChild(logBtn);

        vbox.AddChild(controls);

        _logContainer = new VBoxContainer { Visible = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _logText = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _logText.AddThemeFontSizeOverride("font_size", Brand.Fs(11));
        _logText.AddThemeColorOverride("font_color", new Color(0.95f, 0.45f, 0.45f));
        _logContainer.AddChild(_logText);
        vbox.AddChild(_logContainer);

        vbox.AddChild(new HSeparator());

        var upNext = new Label { Text = "Up next" };
        upNext.AddThemeFontSizeOverride("font_size", Brand.Fs(12));
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
        note.AddThemeFontSizeOverride("font_size", Brand.Fs(11));
        note.AddThemeColorOverride("font_color", Brand.TextDim);
        vbox.AddChild(note);
    }

    private VBoxContainer _logContainer;
    private Label _logText;

    private void ToggleLog()
    {
        if (_logContainer == null) return;
        _logContainer.Visible = !_logContainer.Visible;
        if (_logContainer.Visible)
        {
            var logs = VideoErrorLog.ReadRecent(5);
            _logText.Text = logs.Count > 0
                ? string.Join("\n", logs)
                : "No errors logged yet.\nLog file: " + VideoErrorLog.AbsolutePath;
        }
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
        Visible = true; // the layer itself — see the note in Hud.HideAll
        _card.Visible = true;
        Redraw();
        InputMode.Hold(InputMode.Video);
    }

    public new void Hide()
    {
        if (!_card.Visible) return;
        _card.Visible = false;
        Visible = false;
        InputMode.Release(InputMode.Video);
        Closed?.Invoke();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (IsOpen && e is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            Hide();
            GetViewport().SetInputAsHandled();
        }
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
            label.AddThemeFontSizeOverride("font_size", Brand.Fs(12));
            label.AddThemeColorOverride("font_color", Brand.TextMid);
            row.AddChild(label);

            var rm = Brand.Ghost_(new Button { CustomMinimumSize = new Vector2(30, 26), Icon = Icons.Get(Icons.Kind.Close, 12, Brand.TextMid) });
            rm.Pressed += () => _manager?.Remove(index);
            row.AddChild(rm);

            _list.AddChild(row);
        }
    }
}
