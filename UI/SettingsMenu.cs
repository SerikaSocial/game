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
    private VBoxContainer _graphics, _audio, _controls, _iface, _keys;
    // The VR tab and its section are null outside a headset — see the guard in _Ready.
    private VBoxContainer _vr;
    private Button _tabG, _tabA, _tabC, _tabI, _tabV, _tabK;
    private LineEdit _search;
    private HBoxContainer _tabsRow;
    private int _tabBeforeSearch;
    private Label _searchEmpty;

    public override void _Ready()
    {
        Layer = 92; // above the pause menu so Settings opened from it stacks correctly

        // Hidden at the layer, not only at the card. `Open()` sets both and `Hide()` bails out
        // when the card is already hidden, so from `_Ready` until the player first opened *and*
        // closed Settings this layer was visible with nothing in it — which on the VR panel is
        // exactly what `VrUiSurface.HasInteractiveUi` reads to keep the panel drawn and the laser
        // pointer lit.
        Visible = false;

        // Through `Brand.Scrim`, not a hand-rolled ColorRect. Built inline at 0.82 alpha this one
        // never saw the VR branch, so on the panel it was a near-opaque sheet over the whole
        // 61° x 39° surface with the settings card floating in the middle of it.
        _scrim = Brand.Scrim(0.82f);
        _scrim.Visible = false;
        AddChild(_scrim);

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(center);

        _card = new PanelContainer { CustomMinimumSize = Brand.Card(760, 560), Visible = false };
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
        title.AddThemeFontSizeOverride("font_size", Brand.Fs(22));
        title.AddThemeColorOverride("font_color", Brand.TextHi);
        header.AddChild(title);
        _search = new LineEdit
        {
            PlaceholderText = "Search settings…",
            CustomMinimumSize = new Vector2(200, 32),
        };
        _search.TextChanged += ApplySearch;
        header.AddChild(_search);

        var detected = new Label { Text = $"Detected: {DeviceProfile.Current} tier" };
        detected.AddThemeFontSizeOverride("font_size", Brand.Fs(12));
        detected.AddThemeColorOverride("font_color", Brand.TextDim);
        header.AddChild(detected);
        root.AddChild(header);

        var tabs = new HBoxContainer();
        _tabsRow = tabs;
        tabs.AddThemeConstantOverride("separation", 8);
        _tabG = Tab(Icons.Kind.Display, "Graphics", () => Switch(0));
        _tabA = Tab(Icons.Kind.Speaker, "Audio", () => Switch(1));
        _tabC = Tab(Icons.Kind.Gamepad, "Controls", () => Switch(2));
        _tabI = Tab(Icons.Kind.Image, "Interface", () => Switch(3));
        _tabK = Tab(Icons.Kind.Gamepad, "Keybinds", () => Switch(5));
        tabs.AddChild(_tabG); tabs.AddChild(_tabA); tabs.AddChild(_tabC); tabs.AddChild(_tabK); tabs.AddChild(_tabI);
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
        _keys = BuildKeybinds();
        stack.AddChild(_graphics); stack.AddChild(_audio); stack.AddChild(_controls);
        stack.AddChild(_keys); stack.AddChild(_iface);
        if (_tabV != null) { _vr = BuildVr(); stack.AddChild(_vr); }

        _searchEmpty = Caption("No settings match that.");
        _searchEmpty.Visible = false;
        stack.AddChild(_searchEmpty);

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
    private OptionButton _micMode; private Label _micModeHint;
    private HSlider _micGain; private Label _micGainVal;
    private HSlider _micSens; private Label _micSensVal;
    private OptionButton _outputMode;
    private CheckButton _nightMode;
    private HSlider _volVoice; private Label _volVoiceVal;
    private HSlider _volWorld; private Label _volWorldVal;
    private HSlider _volSfx; private Label _volSfxVal;
    private HSlider _volMusic; private Label _volMusicVal;
    private HSlider _volMedia; private Label _volMediaVal;
    private HSlider _volUi; private Label _volUiVal;

    private VBoxContainer BuildAudio()
    {
        Audio.AudioBuses.Ensure();

        var v = Section();

        v.AddChild(Heading("Output"));
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

        // Speaker layout is negotiated by the OS and the driver — the game cannot switch a stereo
        // output into 5.1. Saying what was detected keeps the mode below from looking inert.
        _outputMode = new OptionButton();
        _outputMode.AddItem("Auto", 0);
        _outputMode.AddItem("Headphones", 1);
        _outputMode.AddItem("Stereo speakers", 2);
        _outputMode.AddItem("Surround", 3);
        _outputMode.ItemSelected += idx =>
        {
            DeviceProfile.Settings.AudioOutputMode = (Audio.AudioBuses.Output)(int)idx;
            Audio.AudioBuses.ApplyOutput(DeviceProfile.Settings.AudioOutputMode);
            DeviceProfile.Settings.Save();
        };
        v.AddChild(Row("Listening on", _outputMode));
        v.AddChild(Caption(
            $"Detected: {Audio.AudioBuses.DetectedOutput()}. This tunes how wide positional audio " +
            "is panned — the channel count itself comes from your system's sound settings."));

        _nightMode = new CheckButton();
        _nightMode.Toggled += on =>
        {
            DeviceProfile.Settings.NightMode = on;
            Audio.AudioBuses.SetNightMode(on);
            DeviceProfile.Settings.Save();
        };
        v.AddChild(Row("Night mode", _nightMode));
        v.AddChild(Caption("Evens out loud worlds and loud players so nothing spikes. Good on headphones."));

        // ── Mixer ───────────────────────────────────────────────────────────────────────
        // Each of these is its own audio bus, so turning a world down genuinely does not turn the
        // people you're talking to down with it.
        v.AddChild(Heading("Mixer"));
        v.AddChild(MixRow("Player voices", Audio.AudioBuses.Voice,
            () => DeviceProfile.Settings.VolVoice, x => DeviceProfile.Settings.VolVoice = x,
            out _volVoice, out _volVoiceVal));
        v.AddChild(MixRow("World & ambience", Audio.AudioBuses.World,
            () => DeviceProfile.Settings.VolWorld, x => DeviceProfile.Settings.VolWorld = x,
            out _volWorld, out _volWorldVal));
        v.AddChild(MixRow("Sound effects", Audio.AudioBuses.Sfx,
            () => DeviceProfile.Settings.VolSfx, x => DeviceProfile.Settings.VolSfx = x,
            out _volSfx, out _volSfxVal));
        v.AddChild(MixRow("Music", Audio.AudioBuses.Music,
            () => DeviceProfile.Settings.VolMusic, x => DeviceProfile.Settings.VolMusic = x,
            out _volMusic, out _volMusicVal));
        v.AddChild(MixRow("Video screens", Audio.AudioBuses.Media,
            () => DeviceProfile.Settings.VolMedia, x => DeviceProfile.Settings.VolMedia = x,
            out _volMedia, out _volMediaVal));
        v.AddChild(MixRow("Menus & alerts", Audio.AudioBuses.Ui,
            () => DeviceProfile.Settings.VolUi, x => DeviceProfile.Settings.VolUi = x,
            out _volUi, out _volUiVal));

        var soloVoice = Brand.Ghost_(new Button { Text = "Voices only", CustomMinimumSize = new Vector2(150, 34) });
        soloVoice.AddThemeFontSizeOverride("font_size", Brand.Fs(12));
        soloVoice.Pressed += () =>
        {
            // The state people actually want mid-conversation, and tedious to reach by dragging
            // five sliders to zero.
            DeviceProfile.Settings.VolWorld = 0f;
            DeviceProfile.Settings.VolSfx = 0f;
            DeviceProfile.Settings.VolMusic = 0f;
            DeviceProfile.Settings.VolMedia = 0f;
            DeviceProfile.Settings.VolVoice = 1f;
            ApplyMix();
            RebuildValues();
            DeviceProfile.Settings.Save();
        };
        var resetMix = Brand.Ghost_(new Button { Text = "Reset mix", CustomMinimumSize = new Vector2(130, 34) });
        resetMix.AddThemeFontSizeOverride("font_size", Brand.Fs(12));
        resetMix.Pressed += () =>
        {
            DeviceProfile.Settings.VolVoice = 1f;
            DeviceProfile.Settings.VolWorld = 1f;
            DeviceProfile.Settings.VolSfx = 1f;
            DeviceProfile.Settings.VolMusic = 0.7f;
            DeviceProfile.Settings.VolMedia = 1f;
            DeviceProfile.Settings.VolUi = 0.8f;
            ApplyMix();
            RebuildValues();
            DeviceProfile.Settings.Save();
        };
        var mixBtns = new HBoxContainer();
        mixBtns.AddThemeConstantOverride("separation", 8);
        mixBtns.AddChild(soloVoice);
        mixBtns.AddChild(resetMix);
        v.AddChild(mixBtns);

        // ── Voice chat ──────────────────────────────────────────────────────────────────
        v.AddChild(Heading("Microphone"));
        _micMode = new OptionButton();
        _micMode.AddItem("Voice activated (open mic)", 0);
        _micMode.AddItem("Push to talk (hold B)", 1);
        _micMode.AddItem("Always on (no gate)", 2);
        _micMode.ItemSelected += idx =>
        {
            // The enum's Muted member is the "mic off" state, not a mode the user picks here —
            // that is what the V toggle does. This chooses what ON means.
            DeviceProfile.Settings.VoiceMicMode = idx switch
            {
                1 => Player.MicMode.PushToTalk,
                2 => Player.MicMode.Always,
                _ => Player.MicMode.Open,
            };
            DeviceProfile.Settings.Save();
            VoiceSettingsChanged?.Invoke();
            RefreshVoiceRowVisibility();
        };
        v.AddChild(Row("Voice mode", _micMode));
        _micModeHint = Caption("");
        v.AddChild(_micModeHint);

        _micGain = new HSlider { MinValue = 25, MaxValue = 300, Step = 5, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _micGainVal = ValLabel();
        _micGain.ValueChanged += x =>
        {
            DeviceProfile.Settings.VoiceMicGain = (float)x / 100f;
            _micGainVal.Text = $"{(int)x}%";
            DeviceProfile.Settings.Save();
            VoiceSettingsChanged?.Invoke();
        };
        v.AddChild(Row("Mic gain", _micGain, _micGainVal));

        _micSens = new HSlider { MinValue = 25, MaxValue = 300, Step = 5, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _micSensVal = ValLabel();
        _micSens.ValueChanged += x =>
        {
            // Higher = a louder voice is needed to open the gate. Phrased as "gate threshold"
            // in the label because "sensitivity" is ambiguous about which way it runs.
            DeviceProfile.Settings.VoiceSensitivity = (float)x / 100f;
            _micSensVal.Text = $"{(int)x}%";
            DeviceProfile.Settings.Save();
            VoiceSettingsChanged?.Invoke();
        };
        v.AddChild(Row("Voice gate threshold", _micSens, _micSensVal));

        // Playback volume for other people lives in the mixer's "Player voices" channel above —
        // it is a bus, not a per-node gain, so it belongs with the rest of the mix.

        RefreshVoiceRowVisibility();
        return v;
    }

    private static void SyncMix(HSlider s, Label l, float linear)
    {
        s?.SetValueNoSignal(linear * 100f);
        if (l != null) l.Text = $"{(int)(linear * 100)}%";
    }

    /// Push the whole saved mix onto the buses. Called on boot and by the mix presets.
    public static void ApplyMix()
    {
        Audio.AudioBuses.Ensure();
        Audio.AudioBuses.SetVolume(Audio.AudioBuses.Voice, DeviceProfile.Settings.VolVoice);
        Audio.AudioBuses.SetVolume(Audio.AudioBuses.World, DeviceProfile.Settings.VolWorld);
        Audio.AudioBuses.SetVolume(Audio.AudioBuses.Sfx, DeviceProfile.Settings.VolSfx);
        Audio.AudioBuses.SetVolume(Audio.AudioBuses.Music, DeviceProfile.Settings.VolMusic);
        Audio.AudioBuses.SetVolume(Audio.AudioBuses.Media, DeviceProfile.Settings.VolMedia);
        Audio.AudioBuses.SetVolume(Audio.AudioBuses.Ui, DeviceProfile.Settings.VolUi);
        Audio.AudioBuses.ApplyOutput(DeviceProfile.Settings.AudioOutputMode);
        Audio.AudioBuses.SetNightMode(DeviceProfile.Settings.NightMode);
    }

    /// The gate sliders only mean something when a gate is running, so hide them in Always-on
    /// rather than leaving two controls that visibly do nothing.
    private void RefreshVoiceRowVisibility()
    {
        var mode = DeviceProfile.Settings.VoiceMicMode;
        bool gated = mode != Player.MicMode.Always;
        if (_micSens != null) _micSens.GetParent<Control>().Visible = gated;
        if (_micModeHint != null)
            _micModeHint.Text = mode switch
            {
                Player.MicMode.PushToTalk => "Hold B to talk. Nothing is transmitted otherwise.",
                Player.MicMode.Always => "Your mic transmits constantly while unmuted (~32 KB/s). " +
                                          "Use this if voice activation keeps clipping your first word.",
                _ => "Transmits when you speak. Raise the gate threshold in a noisy room.",
            };
    }

    /// Raised when any voice setting changes so Main can push it into the live VoiceManager —
    /// a setting that only takes effect on restart is a setting people conclude is broken.
    public event System.Action VoiceSettingsChanged;

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
        hint.AddThemeFontSizeOverride("font_size", Brand.Fs(12));
        hint.AddThemeColorOverride("font_color", Brand.TextDim);
        v.AddChild(hint);
        return v;
    }

    // ── Interface tab ───────────────────────────────────────────────────────────────────
    private CheckButton _nameTags, _pfp, _discordPresence;
    private VBoxContainer BuildInterface()
    {
        var v = Section();
        _nameTags = new CheckButton();
        _nameTags.Toggled += on => { DeviceProfile.Settings.NameTags = on; SettingChanged?.Invoke("name_tags"); DeviceProfile.Settings.Save(); };
        v.AddChild(Row("Show name tags", _nameTags));

        _pfp = new CheckButton();
        _pfp.Toggled += on => { DeviceProfile.Settings.ProfilePictures = on; SettingChanged?.Invoke("pfp"); DeviceProfile.Settings.Save(); };
        v.AddChild(Row("Show profile pictures on tags", _pfp));

        // Desktop only — the Social SDK is not wired on either mobile platform.
        if (!OS.HasFeature("android") && !OS.HasFeature("ios"))
        {
            _discordPresence = new CheckButton();
            _discordPresence.Toggled += on => Discord.DiscordRichPresence.SetEnabled(on);
            v.AddChild(Row("Discord Rich Presence", _discordPresence));
            var discordHint = new Label
            {
                Text = "Shows what you're playing and lets friends join from Discord. Discord asks once; Cancel is remembered. Re-enable here to connect again.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            discordHint.AddThemeFontSizeOverride("font_size", Brand.Fs(12));
            discordHint.AddThemeColorOverride("font_color", Brand.TextDim);
            v.AddChild(discordHint);
        }
        return v;
    }

    // ── VR tab ──────────────────────────────────────────────────────────────────────────
    private OptionButton _vrLocoOpt, _vrOrientOpt, _vrTurnOpt;
    private HSlider _vrSnapAngle; private Label _vrSnapAngleVal;
    private HSlider _vrSmoothTurn; private Label _vrSmoothTurnVal;
    private HSlider _vrVignetteAmt; private Label _vrVignetteAmtVal;
    private HSlider _vrHeight; private Label _vrHeightVal;
    private CheckButton _vrVignetteChk, _vrDashChk, _vrHandsChk, _vrFingersChk, _vrWristChk, _vrHapticsChk;

    private CheckButton _vrForceSeated;
    private Label _vrSpaceStatus;

    /// Supplied by Main so the tab can report what the tracking space is actually doing right now.
    /// A callback rather than a stored rig because the VR player is created and destroyed around
    /// world changes, so a captured reference goes stale.
    public Func<(bool seated, bool calibrated, float eyeHeight)?> VrSpaceProbe;

    private void RefreshVrSpaceStatus()
    {
        if (_vrSpaceStatus == null) return;
        var p = VrSpaceProbe?.Invoke();
        if (p == null) { _vrSpaceStatus.Text = "VR rig not active."; return; }

        var (seated, calibrated, eye) = p.Value;
        _vrSpaceStatus.Text = seated
            ? "Tracking space: SEATED — compensating, your view is placed at your avatar's eye height."
            : calibrated
                ? $"Tracking space: floor-relative. Measured eye height {eye:0.00} m."
                : "Tracking space: floor-relative. Measuring your height — put the headset on and stand up.";
    }

    private VBoxContainer BuildVr()
    {
        var v = Section();

        // ── Play space ──────────────────────────────────────────────────────────────────
        // First, because when this is wrong nothing else on the tab matters: the player is
        // looking at their avatar's feet and no amount of turn-speed tuning helps.
        v.AddChild(Heading("Play space & height"));

        _vrSpaceStatus = Caption("");
        v.AddChild(_vrSpaceStatus);

        _vrForceSeated = new CheckButton();
        _vrForceSeated.Toggled += on =>
        {
            DeviceProfile.Settings.VrForceSeatedSpace = on;
            DeviceProfile.Settings.Save();
            SettingChanged?.Invoke("vr_space");
            RefreshVrSpaceStatus();
        };
        v.AddChild(Row("My view is at my feet (fix)", _vrForceSeated));
        v.AddChild(Caption(
            "Some headsets do not give the game a floor-relative tracking space, which puts your " +
            "viewpoint on the ground. This is detected automatically — tick it only if it was not."));

        v.AddChild(Heading("Movement"));

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
        hint.AddThemeFontSizeOverride("font_size", Brand.Fs(12));
        hint.AddThemeColorOverride("font_color", Brand.TextDim);
        v.AddChild(hint);

        return v;
    }

    // ── Open / close ────────────────────────────────────────────────────────────────────
    public bool IsOpen => _card.Visible;

    public void Open()
    {
        RebuildValues();
        Visible = true; // the layer itself — see the note in Hud.HideAll
        _scrim.Visible = true;
        _card.Visible = true;
        InputMode.Hold(InputMode.Settings);
    }

    public new void Hide()
    {
        if (!_card.Visible) return;
        _scrim.Visible = false;
        _card.Visible = false;
        Visible = false;
        InputMode.Release(InputMode.Settings);
        Closed?.Invoke();
    }

    /// Key capture for the rebind UI.
    ///
    /// This must be `_Input`, not `_UnhandledInput`. The key being bound is by definition a key
    /// something else wants: the game's own handler, or the focused Button's "activate on Space".
    /// `_UnhandledInput` runs after both, so a player rebinding to Space would toggle the button
    /// they just clicked instead of binding it, and rebinding to `M` would open the world menu
    /// behind the settings screen.
    public override void _Input(InputEvent e)
    {
        if (_rebinding == null || !IsOpen) return;
        if (e is not InputEventKey { Pressed: true, Echo: false } k) return;

        GetViewport().SetInputAsHandled();

        if (k.Keycode == Key.Escape) { CancelRebind(); return; }

        KeyBindings.Rebind(_rebinding, k.Keycode);
        DeviceProfile.Settings.Save();
        // Movement bindings go through Godot's InputMap; the rest are read back by `Main`. Both
        // are already live at this point — `Rebind` applied them — so there is nothing to restart.
        CancelRebind();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        // While capturing, Escape belongs to the rebind (handled in `_Input` above) — closing the
        // whole settings screen on it would be a surprising way to cancel one key.
        if (_rebinding != null) return;
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

        _micMode?.Select(DeviceProfile.Settings.VoiceMicMode switch
        {
            Player.MicMode.PushToTalk => 1,
            Player.MicMode.Always => 2,
            _ => 0,
        });
        _micGain?.SetValueNoSignal(DeviceProfile.Settings.VoiceMicGain * 100f);
        if (_micGainVal != null) _micGainVal.Text = $"{(int)(DeviceProfile.Settings.VoiceMicGain * 100)}%";
        _micSens?.SetValueNoSignal(DeviceProfile.Settings.VoiceSensitivity * 100f);
        if (_micSensVal != null) _micSensVal.Text = $"{(int)(DeviceProfile.Settings.VoiceSensitivity * 100)}%";

        _vrForceSeated?.SetPressedNoSignal(DeviceProfile.Settings.VrForceSeatedSpace);
        RefreshVrSpaceStatus();

        _outputMode?.Select((int)DeviceProfile.Settings.AudioOutputMode);
        _nightMode?.SetPressedNoSignal(DeviceProfile.Settings.NightMode);

        SyncMix(_volVoice, _volVoiceVal, DeviceProfile.Settings.VolVoice);
        SyncMix(_volWorld, _volWorldVal, DeviceProfile.Settings.VolWorld);
        SyncMix(_volSfx, _volSfxVal, DeviceProfile.Settings.VolSfx);
        SyncMix(_volMusic, _volMusicVal, DeviceProfile.Settings.VolMusic);
        SyncMix(_volMedia, _volMediaVal, DeviceProfile.Settings.VolMedia);
        SyncMix(_volUi, _volUiVal, DeviceProfile.Settings.VolUi);
        RefreshVoiceRowVisibility();

        PopulateDevices();

        _sens.SetValueNoSignal(DeviceProfile.Settings.MouseSensitivity / 0.001f);
        _sensVal.Text = $"{DeviceProfile.Settings.MouseSensitivity / 0.001f:F1}";
        _thirdPerson.SetPressedNoSignal(DeviceProfile.Settings.StartThirdPerson);
        _nameTags.SetPressedNoSignal(DeviceProfile.Settings.NameTags);
        _pfp.SetPressedNoSignal(DeviceProfile.Settings.ProfilePictures);
        _discordPresence?.SetPressedNoSignal(DeviceProfile.Settings.DiscordPresence);

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

    /// Filter every tab at once by row label.
    ///
    /// Deliberately cross-tab. A settings screen with six tabs has the same problem every settings
    /// screen has: you know what the thing is called and not which tab someone filed it under.
    /// So a search shows all tabs' matching rows together, and the tab strip disappears while it
    /// is active — leaving it visible would imply the results are scoped to the current tab.
    ///
    /// It filters in place rather than building a results list, because the controls are live and
    /// stateful; cloning them would produce a second set of sliders that do not reflect (or write
    /// back) the real values.
    /// Drive the search from a diagnostic. There is no test-only logic behind it — this calls the
    /// same method the LineEdit does.
    public void SearchForDiagnostics(string query) => ApplySearch(query);

    /// Visible, searchable rows across every tab, for the diagnostic to count.
    public int VisibleRowCountForDiagnostics()
    {
        int n = 0;
        foreach (var section in Sections())
        {
            if (!section.Visible) continue;
            foreach (var child in section.GetChildren())
                if (child is Control c && c.Visible && c.HasMeta("search")) n++;
        }
        return n;
    }

    private void ApplySearch(string query)
    {
        string q = query.Trim().ToLower();
        bool searching = q.Length > 0;

        if (searching && _tabsRow.Visible) _tabBeforeSearch = CurrentTab();
        _tabsRow.Visible = !searching;

        if (!searching)
        {
            // Restore everything, then re-show whichever single tab was in front.
            foreach (var section in Sections())
                foreach (var child in section.GetChildren())
                    if (child is Control c) c.Visible = true;
            _searchEmpty.Visible = false;
            Switch(_tabBeforeSearch);
            return;
        }

        int hits = 0;
        foreach (var section in Sections())
        {
            int sectionHits = 0;
            foreach (var child in section.GetChildren())
            {
                if (child is not Control c) continue;
                // Only tagged rows are searchable. Headings, captions and loose buttons are
                // chrome for the unfiltered view and would be meaningless floating in results.
                if (c.HasMeta("search"))
                {
                    bool match = ((string)c.GetMeta("search")).Contains(q);
                    c.Visible = match;
                    if (match) sectionHits++;
                }
                else c.Visible = false;
            }
            section.Visible = sectionHits > 0;
            hits += sectionHits;
        }

        _searchEmpty.Visible = hits == 0;
    }

    private System.Collections.Generic.IEnumerable<VBoxContainer> Sections()
    {
        yield return _graphics;
        yield return _audio;
        yield return _controls;
        yield return _keys;
        yield return _iface;
        if (_vr != null) yield return _vr;
    }

    private int CurrentTab()
    {
        if (_audio.Visible) return 1;
        if (_controls.Visible) return 2;
        if (_iface.Visible) return 3;
        if (_vr != null && _vr.Visible) return 4;
        if (_keys.Visible) return 5;
        return 0;
    }

    private void Switch(int i)
    {
        _graphics.Visible = i == 0; _audio.Visible = i == 1; _controls.Visible = i == 2; _iface.Visible = i == 3;
        Style(_tabG, i == 0); Style(_tabA, i == 1); Style(_tabC, i == 2); Style(_tabI, i == 3);
        if (_vr != null) _vr.Visible = i == 4;
        if (_tabV != null) Style(_tabV, i == 4);
        _keys.Visible = i == 5;
        Style(_tabK, i == 5);
        // Leaving the tab must never strand the UI waiting for a keypress that will now be
        // interpreted as a game input.
        if (i != 5) CancelRebind();
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
    /// A section heading inside a settings tab. The audio tab grew four distinct groups (devices,
    /// the mixer, voice, output shaping) and without headings it reads as one undifferentiated
    /// column of sliders.
    private static Control Heading(string text)
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 4);
        var l = new Label { Text = text.ToUpperInvariant() };
        l.AddThemeFontSizeOverride("font_size", Brand.Fs(11));
        l.AddThemeColorOverride("font_color", Brand.Accent);
        v.AddChild(l);
        v.AddChild(new HSeparator());
        return v;
    }

    /// Small muted caption under a control, for the things that genuinely need a sentence.
    private static Label Caption(string text)
    {
        var l = new Label
        {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        l.AddThemeFontSizeOverride("font_size", Brand.Fs(11));
        l.AddThemeColorOverride("font_color", Brand.TextDim);
        return l;
    }

    /// One channel of the mixer: a slider, a live percentage, and a mute toggle. Returns the row
    /// and hands back the slider/label so `SyncFromSettings` can drive them.
    private HBoxContainer MixRow(string label, string bus, Func<float> get, Action<float> set,
                                 out HSlider slider, out Label value)
    {
        var s = new HSlider { MinValue = 0, MaxValue = 200, Step = 5, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var v = ValLabel();
        var mute = Brand.Ghost_(new Button { Text = "Mute", ToggleMode = true, CustomMinimumSize = new Vector2(70, 32) });
        mute.AddThemeFontSizeOverride("font_size", Brand.Fs(11));

        s.ValueChanged += x =>
        {
            set((float)x / 100f);
            v.Text = $"{(int)x}%";
            Audio.AudioBuses.SetVolume(bus, (float)x / 100f);
            // A slider moved off zero is an unmute; leaving the toggle stuck on would make the
            // slider look broken.
            if (x > 0 && mute.ButtonPressed) mute.SetPressedNoSignal(false);
            DeviceProfile.Settings.Save();
        };
        mute.Toggled += on =>
        {
            Audio.AudioBuses.SetMuted(bus, on);
            if (!on) Audio.AudioBuses.SetVolume(bus, get());
        };

        slider = s;
        value = v;
        var row = Row(label, s, v);
        row.AddChild(mute);
        return row;
    }

    // ── Keybinds tab ────────────────────────────────────────────────────────────────────
    //
    // Capture happens in `_Input` while `_rebinding` is set, so the next key the player presses is
    // swallowed and assigned rather than acted on. That is the whole subtlety of a rebind UI: the
    // key you are trying to bind is, by definition, a key the game also wants to handle.

    private string _rebinding;
    private Button _rebindButton;
    private Label _keyHint;
    private readonly System.Collections.Generic.Dictionary<string, Button> _keyButtons = new();

    private VBoxContainer BuildKeybinds()
    {
        var v = Section();

        _keyHint = Caption("Click a key to change it. Escape cancels. A key already in use is " +
                           "taken from whatever held it, so you never have to clear one first.");
        v.AddChild(_keyHint);

        string category = null;
        foreach (var b in KeyBindings.All)
        {
            if (b.Category != category)
            {
                category = b.Category;
                v.AddChild(Heading(category));
            }

            var btn = Brand.Ghost_(new Button
            {
                Text = KeyBindings.Name(b.Current),
                CustomMinimumSize = new Vector2(160, 34),
                Disabled = b.Locked,
            });
            btn.AddThemeFontSizeOverride("font_size", Brand.Fs(12));
            string id = b.Id;
            btn.Pressed += () => BeginRebind(id);
            _keyButtons[b.Id] = btn;

            var row = Row(b.Label + (b.Locked ? "  (fixed)" : ""), new Control(), btn);
            v.AddChild(row);
        }

        var reset = Brand.Ghost_(new Button { Text = "Reset all keys", CustomMinimumSize = new Vector2(150, 34) });
        reset.AddThemeFontSizeOverride("font_size", Brand.Fs(12));
        reset.Pressed += () =>
        {
            KeyBindings.ResetAll();
            DeviceProfile.Settings.Save();
            RefreshKeyButtons();
        };
        v.AddChild(reset);

        return v;
    }

    private void BeginRebind(string id)
    {
        CancelRebind();
        _rebinding = id;
        _keyButtons.TryGetValue(id, out _rebindButton);
        if (_rebindButton != null) _rebindButton.Text = "press a key…";
        if (_keyHint != null) _keyHint.Text = "Press the key you want. Escape cancels.";
    }

    private void CancelRebind()
    {
        _rebinding = null;
        _rebindButton = null;
        if (_keyHint != null)
            _keyHint.Text = "Click a key to change it. Escape cancels. A key already in use is " +
                            "taken from whatever held it, so you never have to clear one first.";
        RefreshKeyButtons();
    }

    private void RefreshKeyButtons()
    {
        foreach (var b in KeyBindings.All)
            if (_keyButtons.TryGetValue(b.Id, out var btn))
                btn.Text = KeyBindings.Name(b.Current);
    }

    private static HBoxContainer Row(string label, Control control, Control trailing = null)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 14);
        // Tagged so the search box can match a row without re-deriving its label from the
        // first child, which breaks the moment a row's layout changes.
        row.SetMeta("search", label.ToLower());
        var l = new Label { Text = label, CustomMinimumSize = new Vector2(220, 0) };
        l.AddThemeColorOverride("font_color", Brand.TextHi);
        row.AddChild(l);
        control.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(control);
        if (trailing != null) row.AddChild(trailing);
        return row;
    }
}
