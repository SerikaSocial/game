using System;
using Godot;

namespace SerikaSocial.UI;

/// The full settings screen — Graphics, Audio, Controls, Interface — backed by DeviceProfile and
/// persisted to user://settings.cfg. Every control applies live and saves on change, so there's
/// no "Apply" button to forget. Opened from the pause menu's ⚙ dock icon.
///
/// Built in code like every other screen (no .tscn), styled through Brand, and it takes a
/// cursor-freeing InputMode hold while open.
public partial class SettingsMenu : CanvasLayer
{
    public event Action Closed;
    /// Raised when a value changes that Main must relay to live objects (sensitivity → player,
    /// name-tag/pfp visibility → avatars). The string names what changed.
    public event Action<string> SettingChanged;

    private ColorRect _scrim;
    private PanelContainer _card;
    private VBoxContainer _graphics, _audio, _controls, _iface;
    private Button _tabG, _tabA, _tabC, _tabI;

    public override void _Ready()
    {
        Layer = 92; // above the pause menu so ⚙ from it stacks correctly

        _scrim = new ColorRect { Color = new Color(0.02f, 0.03f, 0.05f, 0.82f), AnchorRight = 1, AnchorBottom = 1, Visible = false };
        AddChild(_scrim);

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(center);

        _card = new PanelContainer { CustomMinimumSize = new Vector2(760, 560), Visible = false };
        _card.AddThemeStyleboxOverride("panel", Brand.Panel(Brand.Bg1, 16, 1, Brand.Border));
        center.AddChild(_card);

        var margin = new MarginContainer();
        foreach (var s in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(s, 22);
        _card.AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        var header = new HBoxContainer();
        var title = new Label { Text = "⚙  Settings", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", 22);
        title.AddThemeColorOverride("font_color", Brand.TextHi);
        header.AddChild(title);
        var detected = new Label { Text = $"Detected: {DeviceProfile.Current} tier" };
        detected.AddThemeFontSizeOverride("font_size", 12);
        detected.AddThemeColorOverride("font_color", Brand.TextDim);
        header.AddChild(detected);
        root.AddChild(header);

        var tabs = new HBoxContainer();
        tabs.AddThemeConstantOverride("separation", 8);
        _tabG = Tab("🖥 Graphics", () => Switch(0));
        _tabA = Tab("🔊 Audio", () => Switch(1));
        _tabC = Tab("🎮 Controls", () => Switch(2));
        _tabI = Tab("🖼 Interface", () => Switch(3));
        tabs.AddChild(_tabG); tabs.AddChild(_tabA); tabs.AddChild(_tabC); tabs.AddChild(_tabI);
        root.AddChild(tabs);
        root.AddChild(new HSeparator());

        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        root.AddChild(scroll);
        var stack = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(stack);

        _graphics = BuildGraphics();
        _audio = BuildAudio();
        _controls = BuildControls();
        _iface = BuildInterface();
        stack.AddChild(_graphics); stack.AddChild(_audio); stack.AddChild(_controls); stack.AddChild(_iface);

        root.AddChild(new HSeparator());
        var footer = new HBoxContainer();
        footer.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var resetBtn = Brand.Ghost_(new Button { Text = "Reset to detected defaults", CustomMinimumSize = new Vector2(200, 40) });
        resetBtn.Pressed += () => { DeviceProfile.Detect(); RebuildValues(); };
        footer.AddChild(resetBtn);
        var closeBtn = Brand.Primary_(new Button { Text = "Done (Esc)", CustomMinimumSize = new Vector2(130, 40) });
        closeBtn.Pressed += Hide;
        footer.AddChild(closeBtn);
        root.AddChild(footer);

        Switch(0);
    }

    // ── Graphics tab ────────────────────────────────────────────────────────────────────
    private OptionButton _tierOpt;
    private HSlider _renderScale; private Label _renderScaleVal;
    private OptionButton _msaaOpt;
    private CheckButton _shadowsChk, _bloomChk, _vsyncChk;
    private OptionButton _fpsOpt;

    private VBoxContainer BuildGraphics()
    {
        var v = Section();

        _tierOpt = new OptionButton();
        _tierOpt.AddItem("Low"); _tierOpt.AddItem("Medium"); _tierOpt.AddItem("High");
        _tierOpt.ItemSelected += i => { DeviceProfile.SetTier((DeviceProfile.Tier)(int)i); DeviceProfile.Apply(); RebuildValues(); };
        v.AddChild(Row("Quality preset", _tierOpt));

        _renderScale = new HSlider { MinValue = 0.5, MaxValue = 1.0, Step = 0.05, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _renderScaleVal = ValLabel();
        _renderScale.ValueChanged += x => { DeviceProfile.RenderScale = (float)x; _renderScaleVal.Text = $"{x:P0}"; DeviceProfile.Apply(); };
        v.AddChild(Row("Render scale", _renderScale, _renderScaleVal));

        _msaaOpt = new OptionButton();
        foreach (var s in new[] { "Off", "2×", "4×", "8×" }) _msaaOpt.AddItem(s);
        _msaaOpt.ItemSelected += i => { DeviceProfile.MsaaLevel = (int)i; DeviceProfile.Apply(); };
        v.AddChild(Row("Anti-aliasing (MSAA)", _msaaOpt));

        _fpsOpt = new OptionButton();
        foreach (var s in new[] { "Uncapped", "60", "72", "90", "120", "144" }) _fpsOpt.AddItem(s);
        _fpsOpt.ItemSelected += i => { DeviceProfile.MaxFps = i switch { 0 => 0, 1 => 60, 2 => 72, 3 => 90, 4 => 120, _ => 144 }; DeviceProfile.Apply(); };
        v.AddChild(Row("Max FPS", _fpsOpt));

        _shadowsChk = new CheckButton();
        _shadowsChk.Toggled += on => { DeviceProfile.Shadows = on; ApplyLightShadows(on); DeviceProfile.Apply(); };
        v.AddChild(Row("Shadows", _shadowsChk));

        _bloomChk = new CheckButton();
        _bloomChk.Toggled += on => { DeviceProfile.BloomEnabled = on; DeviceProfile.Apply(); };
        v.AddChild(Row("Bloom / glow", _bloomChk));

        _vsyncChk = new CheckButton();
        _vsyncChk.Toggled += on => { DeviceProfile.VSync = on; DeviceProfile.Apply(); };
        v.AddChild(Row("V-Sync", _vsyncChk));

        return v;
    }

    // ── Audio tab ───────────────────────────────────────────────────────────────────────
    private HSlider _master; private Label _masterVal;
    private VBoxContainer BuildAudio()
    {
        var v = Section();
        _master = new HSlider { MinValue = 0, MaxValue = 100, Step = 1, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _masterVal = ValLabel();
        _master.ValueChanged += x =>
        {
            DeviceProfile.Settings.MasterVolume = (float)x / 100f;
            _masterVal.Text = $"{(int)x}%";
            var bus = AudioServer.GetBusIndex("Master");
            if (bus >= 0)
            {
                AudioServer.SetBusVolumeDb(bus, Mathf.LinearToDb(DeviceProfile.Settings.MasterVolume));
                AudioServer.SetBusMute(bus, DeviceProfile.Settings.MasterVolume < 0.001f);
            }
            DeviceProfile.Settings.Save();
        };
        v.AddChild(Row("Master volume", _master, _masterVal));
        return v;
    }

    // ── Controls tab ────────────────────────────────────────────────────────────────────
    private HSlider _sens; private Label _sensVal; private CheckButton _thirdPerson;
    private VBoxContainer BuildControls()
    {
        var v = Section();
        _sens = new HSlider { MinValue = 0.5, MaxValue = 5.0, Step = 0.1, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _sensVal = ValLabel();
        _sens.ValueChanged += x =>
        {
            DeviceProfile.Settings.MouseSensitivity = 0.001f * (float)x;
            _sensVal.Text = $"{x:F1}";
            SettingChanged?.Invoke("sensitivity");
            DeviceProfile.Settings.Save();
        };
        v.AddChild(Row("Mouse sensitivity", _sens, _sensVal));

        _thirdPerson = new CheckButton();
        _thirdPerson.Toggled += on => { DeviceProfile.Settings.StartThirdPerson = on; DeviceProfile.Settings.Save(); };
        v.AddChild(Row("Start in third person", _thirdPerson));

        var hint = new Label
        {
            Text = "WASD move · Shift sprint · Space jump · Ctrl crouch · V camera · E interact\n" +
                   "Tab free the mouse · T chat · P video queue · Esc menu",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", 12);
        hint.AddThemeColorOverride("font_color", Brand.TextDim);
        v.AddChild(hint);
        return v;
    }

    // ── Interface tab ───────────────────────────────────────────────────────────────────
    private CheckButton _nameTags, _pfp;
    private VBoxContainer BuildInterface()
    {
        var v = Section();
        _nameTags = new CheckButton();
        _nameTags.Toggled += on => { DeviceProfile.Settings.NameTags = on; SettingChanged?.Invoke("name_tags"); DeviceProfile.Settings.Save(); };
        v.AddChild(Row("Show name tags", _nameTags));

        _pfp = new CheckButton();
        _pfp.Toggled += on => { DeviceProfile.Settings.ProfilePictures = on; SettingChanged?.Invoke("pfp"); DeviceProfile.Settings.Save(); };
        v.AddChild(Row("Show profile pictures on tags", _pfp));
        return v;
    }

    // ── Open / close ────────────────────────────────────────────────────────────────────
    public bool IsOpen => _card.Visible;

    public void Open()
    {
        RebuildValues();
        _scrim.Visible = true;
        _card.Visible = true;
        InputMode.Hold(InputMode.Settings);
    }

    public new void Hide()
    {
        if (!_card.Visible) return;
        _scrim.Visible = false;
        _card.Visible = false;
        InputMode.Release(InputMode.Settings);
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

    /// Reflect current DeviceProfile values into every control (without firing their handlers,
    /// which would re-save mid-populate).
    private void RebuildValues()
    {
        _tierOpt.Selected = (int)DeviceProfile.Current;
        _renderScale.SetValueNoSignal(DeviceProfile.RenderScale);
        _renderScaleVal.Text = $"{DeviceProfile.RenderScale:P0}";
        _msaaOpt.Selected = DeviceProfile.MsaaLevel;
        _fpsOpt.Selected = DeviceProfile.MaxFps switch { 0 => 0, 60 => 1, 72 => 2, 90 => 3, 120 => 4, _ => 5 };
        _shadowsChk.SetPressedNoSignal(DeviceProfile.Shadows);
        _bloomChk.SetPressedNoSignal(DeviceProfile.BloomEnabled);
        _vsyncChk.SetPressedNoSignal(DeviceProfile.VSync);

        _master.SetValueNoSignal(DeviceProfile.Settings.MasterVolume * 100f);
        _masterVal.Text = $"{(int)(DeviceProfile.Settings.MasterVolume * 100)}%";
        _sens.SetValueNoSignal(DeviceProfile.Settings.MouseSensitivity / 0.001f);
        _sensVal.Text = $"{DeviceProfile.Settings.MouseSensitivity / 0.001f:F1}";
        _thirdPerson.SetPressedNoSignal(DeviceProfile.Settings.StartThirdPerson);
        _nameTags.SetPressedNoSignal(DeviceProfile.Settings.NameTags);
        _pfp.SetPressedNoSignal(DeviceProfile.Settings.ProfilePictures);
    }

    /// Toggle shadow casting on every light currently in the tree — the render-scale/MSAA knobs
    /// are global, but shadows are per-light, so this walks what's built.
    private void ApplyLightShadows(bool on)
    {
        foreach (var node in GetTree().Root.FindChildren("*", "Light3D", true, false))
            if (node is Light3D l) l.ShadowEnabled = on;
    }

    private void Switch(int i)
    {
        _graphics.Visible = i == 0; _audio.Visible = i == 1; _controls.Visible = i == 2; _iface.Visible = i == 3;
        Style(_tabG, i == 0); Style(_tabA, i == 1); Style(_tabC, i == 2); Style(_tabI, i == 3);
    }

    // ── small builders ──────────────────────────────────────────────────────────────────
    private static void Style(Button b, bool active) { if (active) Brand.Primary_(b); else Brand.Ghost_(b); }
    private static Button Tab(string label, Action onClick)
    {
        var b = Brand.Ghost_(new Button { Text = label, CustomMinimumSize = new Vector2(150, 40) });
        b.Pressed += onClick;
        return b;
    }
    private static VBoxContainer Section()
    {
        var v = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        v.AddThemeConstantOverride("separation", 14);
        return v;
    }
    private static Label ValLabel()
    {
        var l = new Label { CustomMinimumSize = new Vector2(56, 0), HorizontalAlignment = HorizontalAlignment.Right };
        l.AddThemeColorOverride("font_color", Brand.Accent);
        return l;
    }
    private static HBoxContainer Row(string label, Control control, Control trailing = null)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 14);
        var l = new Label { Text = label, CustomMinimumSize = new Vector2(220, 0) };
        l.AddThemeColorOverride("font_color", Brand.TextHi);
        row.AddChild(l);
        control.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(control);
        if (trailing != null) row.AddChild(trailing);
        return row;
    }
}
