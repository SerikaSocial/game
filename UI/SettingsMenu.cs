using System;
using Godot;

using SerikaSocial.UI;

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
    // The VR tab and its section are null outside a headset — see the guard in _Ready.
    private VBoxContainer _vr;
    private Button _tabG, _tabA, _tabC, _tabI, _tabV;

    public override void _Ready()
    {
        Layer = 92; // above the pause menu so Settings opened from it stacks correctly

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
        var title = new Label { Text = "Settings", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
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
        _tabG = Tab(Icons.Kind.Display, "Graphics", () => Switch(0));
        _tabA = Tab(Icons.Kind.Speaker, "Audio", () => Switch(1));
        _tabC = Tab(Icons.Kind.Gamepad, "Controls", () => Switch(2));
        _tabI = Tab(Icons.Kind.Image, "Interface", () => Switch(3));
        tabs.AddChild(_tabG); tabs.AddChild(_tabA); tabs.AddChild(_tabC); tabs.AddChild(_tabI);
        // The VR tab only exists in a headset. On desktop every control on it is inert, and a tab
        // of settings that cannot do anything is worse than no tab.
        if (VrUiSurface.Active)
        {
            _tabV = Tab(Icons.Kind.Focus, "VR", () => Switch(4));
            tabs.AddChild(_tabV);
        }
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
        if (_tabV != null) { _vr = BuildVr(); stack.AddChild(_vr); }

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
    private OptionButton _outputOpt;
    private OptionButton _inputOpt;

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

        _outputOpt = new OptionButton();
        _outputOpt.ItemSelected += idx =>
        {
            string dev = _outputOpt.GetItemText((int)idx);
            AudioServer.OutputDevice = dev;
            DeviceProfile.Settings.OutputDevice = dev;
            DeviceProfile.Settings.Save();
        };
        v.AddChild(Row("Speaker / Output", _outputOpt));

        _inputOpt = new OptionButton();
        _inputOpt.ItemSelected += idx =>
        {
            string dev = _inputOpt.GetItemText((int)idx);
            AudioServer.InputDevice = dev;
            DeviceProfile.Settings.InputDevice = dev;
            DeviceProfile.Settings.Save();
        };
        v.AddChild(Row("Microphone / Input", _inputOpt));

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

    // ── VR tab ──────────────────────────────────────────────────────────────────────────
    private OptionButton _vrLocoOpt, _vrOrientOpt, _vrTurnOpt;
    private HSlider _vrSnapAngle; private Label _vrSnapAngleVal;
    private HSlider _vrSmoothTurn; private Label _vrSmoothTurnVal;
    private HSlider _vrVignetteAmt; private Label _vrVignetteAmtVal;
    private HSlider _vrHeight; private Label _vrHeightVal;
    private CheckButton _vrVignetteChk, _vrDashChk, _vrHandsChk, _vrFingersChk, _vrWristChk, _vrHapticsChk;

    private VBoxContainer BuildVr()
    {
        var v = Section();

        _vrLocoOpt = new OptionButton();
        _vrLocoOpt.AddItem("Smooth"); _vrLocoOpt.AddItem("Teleport");
        _vrLocoOpt.ItemSelected += i =>
        {
            DeviceProfile.Settings.VrLocomotion = (DeviceProfile.Settings.Locomotion)(int)i;
            DeviceProfile.Settings.Save();
        };
        v.AddChild(Row("Movement", _vrLocoOpt));

        _vrOrientOpt = new OptionButton();
        _vrOrientOpt.AddItem("Head (look to walk)"); _vrOrientOpt.AddItem("Hand (point to walk)");
        _vrOrientOpt.ItemSelected += i =>
        {
            DeviceProfile.Settings.VrMoveOrientation = (DeviceProfile.Settings.MoveOrientation)(int)i;
            DeviceProfile.Settings.Save();
        };
        v.AddChild(Row("Movement direction", _vrOrientOpt));

        _vrDashChk = new CheckButton();
        _vrDashChk.Toggled += on => { DeviceProfile.Settings.VrDashTeleport = on; DeviceProfile.Settings.Save(); };
        v.AddChild(Row("Dash teleport (right stick)", _vrDashChk));

        _vrTurnOpt = new OptionButton();
        _vrTurnOpt.AddItem("Snap"); _vrTurnOpt.AddItem("Smooth");
        _vrTurnOpt.ItemSelected += i => { DeviceProfile.Settings.VrSnapTurn = i == 0; DeviceProfile.Settings.Save(); };
        v.AddChild(Row("Turning", _vrTurnOpt));

        _vrSnapAngle = new HSlider { MinValue = 15, MaxValue = 90, Step = 5, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _vrSnapAngleVal = ValLabel();
        _vrSnapAngle.ValueChanged += x =>
        {
            DeviceProfile.Settings.VrSnapTurnAngle = (float)x;
            _vrSnapAngleVal.Text = $"{(int)x}°";
            DeviceProfile.Settings.Save();
        };
        v.AddChild(Row("Snap turn angle", _vrSnapAngle, _vrSnapAngleVal));

        _vrSmoothTurn = new HSlider { MinValue = 45, MaxValue = 240, Step = 5, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _vrSmoothTurnVal = ValLabel();
        _vrSmoothTurn.ValueChanged += x =>
        {
            DeviceProfile.Settings.VrSmoothTurnSpeed = (float)x;
            _vrSmoothTurnVal.Text = $"{(int)x}°/s";
            DeviceProfile.Settings.Save();
        };
        v.AddChild(Row("Smooth turn speed", _vrSmoothTurn, _vrSmoothTurnVal));

        _vrVignetteChk = new CheckButton();
        _vrVignetteChk.Toggled += on => { DeviceProfile.Settings.VrVignette = on; DeviceProfile.Settings.Save(); };
        v.AddChild(Row("Comfort vignette", _vrVignetteChk));

        _vrVignetteAmt = new HSlider { MinValue = 0.2, MaxValue = 1.0, Step = 0.05, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _vrVignetteAmtVal = ValLabel();
        _vrVignetteAmt.ValueChanged += x =>
        {
            DeviceProfile.Settings.VrVignetteStrength = (float)x;
            _vrVignetteAmtVal.Text = $"{x:P0}";
            DeviceProfile.Settings.Save();
        };
        v.AddChild(Row("Vignette strength", _vrVignetteAmt, _vrVignetteAmtVal));

        _vrHandsChk = new CheckButton();
        _vrHandsChk.Toggled += on => { DeviceProfile.Settings.VrHandTracking = on; DeviceProfile.Settings.Save(); };
        v.AddChild(Row("Hand tracking", _vrHandsChk));

        _vrFingersChk = new CheckButton();
        _vrFingersChk.Toggled += on => { DeviceProfile.Settings.VrFingerPosing = on; DeviceProfile.Settings.Save(); };
        v.AddChild(Row("Avatar finger gestures", _vrFingersChk));

        _vrWristChk = new CheckButton();
        _vrWristChk.Toggled += on => { DeviceProfile.Settings.VrWristHud = on; DeviceProfile.Settings.Save(); };
        v.AddChild(Row("Wrist info panel", _vrWristChk));

        _vrHapticsChk = new CheckButton();
        _vrHapticsChk.Toggled += on => { DeviceProfile.Settings.VrHaptics = on; DeviceProfile.Settings.Save(); };
        v.AddChild(Row("Haptics", _vrHapticsChk));

        _vrHeight = new HSlider { MinValue = -0.5, MaxValue = 0.5, Step = 0.01, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _vrHeightVal = ValLabel();
        _vrHeight.ValueChanged += x =>
        {
            DeviceProfile.Settings.VrHeightOffset = (float)x;
            _vrHeightVal.Text = $"{x:+0.00;-0.00;0.00} m";
            SettingChanged?.Invoke("vr_height");
            DeviceProfile.Settings.Save();
        };
        v.AddChild(Row("Height offset", _vrHeight, _vrHeightVal));

        var hint = new Label
        {
            Text = "Controllers: left stick move · right stick turn · push right stick forward to dash\n" +
                   "A/X jump · menu button opens this hub · grip to grab · trigger to interact\n" +
                   "Hands: point and pinch to teleport · pinch to click · tap your other wrist for the menu",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", 12);
        hint.AddThemeColorOverride("font_color", Brand.TextDim);
        v.AddChild(hint);

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

        PopulateDevices();

        _sens.SetValueNoSignal(DeviceProfile.Settings.MouseSensitivity / 0.001f);
        _sensVal.Text = $"{DeviceProfile.Settings.MouseSensitivity / 0.001f:F1}";
        _thirdPerson.SetPressedNoSignal(DeviceProfile.Settings.StartThirdPerson);
        _nameTags.SetPressedNoSignal(DeviceProfile.Settings.NameTags);
        _pfp.SetPressedNoSignal(DeviceProfile.Settings.ProfilePictures);

        RebuildVrValues();
    }

    /// The VR section only exists in a headset, so every control here may legitimately be null.
    private void RebuildVrValues()
    {
        if (_vr == null) return;

        _vrLocoOpt.Selected = (int)DeviceProfile.Settings.VrLocomotion;
        _vrOrientOpt.Selected = (int)DeviceProfile.Settings.VrMoveOrientation;
        _vrTurnOpt.Selected = DeviceProfile.Settings.VrSnapTurn ? 0 : 1;
        _vrDashChk.SetPressedNoSignal(DeviceProfile.Settings.VrDashTeleport);

        _vrSnapAngle.SetValueNoSignal(DeviceProfile.Settings.VrSnapTurnAngle);
        _vrSnapAngleVal.Text = $"{(int)DeviceProfile.Settings.VrSnapTurnAngle}°";
        _vrSmoothTurn.SetValueNoSignal(DeviceProfile.Settings.VrSmoothTurnSpeed);
        _vrSmoothTurnVal.Text = $"{(int)DeviceProfile.Settings.VrSmoothTurnSpeed}°/s";

        _vrVignetteChk.SetPressedNoSignal(DeviceProfile.Settings.VrVignette);
        _vrVignetteAmt.SetValueNoSignal(DeviceProfile.Settings.VrVignetteStrength);
        _vrVignetteAmtVal.Text = $"{DeviceProfile.Settings.VrVignetteStrength:P0}";

        _vrHandsChk.SetPressedNoSignal(DeviceProfile.Settings.VrHandTracking);
        _vrFingersChk.SetPressedNoSignal(DeviceProfile.Settings.VrFingerPosing);
        _vrWristChk.SetPressedNoSignal(DeviceProfile.Settings.VrWristHud);
        _vrHapticsChk.SetPressedNoSignal(DeviceProfile.Settings.VrHaptics);

        _vrHeight.SetValueNoSignal(DeviceProfile.Settings.VrHeightOffset);
        _vrHeightVal.Text = $"{DeviceProfile.Settings.VrHeightOffset:+0.00;-0.00;0.00} m";
    }

    private void PopulateDevices()
    {
        if (_outputOpt != null)
        {
            _outputOpt.Clear();
            var outputs = AudioServer.GetOutputDeviceList();
            string curOut = AudioServer.OutputDevice;
            int selOut = 0;
            for (int i = 0; i < outputs.Length; i++)
            {
                _outputOpt.AddItem(outputs[i]);
                if (outputs[i] == curOut || outputs[i] == DeviceProfile.Settings.OutputDevice) selOut = i;
            }
            if (outputs.Length > 0) _outputOpt.Selected = selOut;
        }

        if (_inputOpt != null)
        {
            _inputOpt.Clear();
            var inputs = AudioServer.GetInputDeviceList();
            string curIn = AudioServer.InputDevice;
            int selIn = 0;
            for (int i = 0; i < inputs.Length; i++)
            {
                _inputOpt.AddItem(inputs[i]);
                if (inputs[i] == curIn || inputs[i] == DeviceProfile.Settings.InputDevice) selIn = i;
            }
            if (inputs.Length > 0) _inputOpt.Selected = selIn;
        }
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
        if (_vr != null) _vr.Visible = i == 4;
        if (_tabV != null) Style(_tabV, i == 4);
    }

    // ── small builders ──────────────────────────────────────────────────────────────────
    private static void Style(Button b, bool active) { if (active) Brand.Primary_(b); else Brand.Ghost_(b); }
    private static Button Tab(Icons.Kind icon, string label, Action onClick)
    {
        var b = Brand.Ghost_(new Button
        {
            Text = label,
            CustomMinimumSize = new Vector2(150, 40),
            Icon = Icons.Get(icon, 16, Brand.Accent),
        });
        b.AddThemeConstantOverride("h_separation", 8);
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
