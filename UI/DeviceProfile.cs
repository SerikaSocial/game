using Godot;

namespace SerikaSocial.UI;

/// Per-device performance tiers, plus the persisted graphics settings that ride on top.
///
/// The client runs on three very different classes of hardware — a desktop GPU, a phone, and a
/// Quest standalone headset (an Android build) — and shipping one fixed quality level either
/// melts the Quest or wastes a desktop. `Detect()` picks a starting tier from the platform; the
/// settings menu can then override any individual knob, and `Apply()` pushes the result onto the
/// engine (render scale, MSAA, shadows, mirror budget, FPS cap, V-Sync).
public static class DeviceProfile
{
    public enum Tier { Low, Medium, High }

    /// The active tier. Starts auto-detected; the settings menu may change it.
    public static Tier Current { get; private set; } = Tier.High;

    // Individual overrides, defaulted from the tier on Detect() and mutable from settings.
    public static float RenderScale = 1.0f;
    public static bool Shadows = true;
    public static int MsaaLevel = 2;          // 0=off,1=2x,2=4x,3=8x — maps to Viewport.Msaa
    public static int MaxFps = 0;             // 0 = uncapped
    public static bool VSync = true;
    public static float MirrorRange = 12f;    // how close before a mirror re-renders
    public static bool BloomEnabled = true;

    /// True on the Quest / any Android build — the tightest budget.
    public static bool IsStandaloneXr => OS.HasFeature("android");

    /// Pick a starting tier from the platform and seed the knobs. Called once at boot before the
    /// first frame renders.
    public static void Detect()
    {
        if (IsStandaloneXr)
        {
            // Mobile-class GPU driving two eyes: everything conservative.
            Current = Tier.Low;
            RenderScale = 0.85f;
            Shadows = false;
            MsaaLevel = 1;            // 2x MSAA is cheap on tile GPUs and worth it in VR
            MaxFps = 72;             // Quest display cadence
            VSync = true;
            MirrorRange = 6f;
            BloomEnabled = false;
        }
        else if (DisplayServer.IsTouchscreenAvailable())
        {
            // Phone/tablet desktop-less build.
            Current = Tier.Medium;
            RenderScale = 0.9f;
            Shadows = true;
            MsaaLevel = 1;
            MaxFps = 60;
            VSync = true;
            MirrorRange = 8f;
            BloomEnabled = true;
        }
        else
        {
            Current = Tier.High;
            RenderScale = 1.0f;
            Shadows = true;
            MsaaLevel = 2;
            MaxFps = 0;
            VSync = true;
            MirrorRange = 12f;
            BloomEnabled = true;
        }

        Settings.Load();  // let a saved profile override the auto-detected one
        Apply();
    }

    /// Adopt a whole tier's presets (the settings menu's tier dropdown). Individual knobs can
    /// then still be tweaked afterward.
    public static void SetTier(Tier tier)
    {
        Current = tier;
        switch (tier)
        {
            case Tier.Low:
                RenderScale = 0.75f; Shadows = false; MsaaLevel = 0; MirrorRange = 6f; BloomEnabled = false; break;
            case Tier.Medium:
                RenderScale = 0.9f; Shadows = true; MsaaLevel = 1; MirrorRange = 9f; BloomEnabled = true; break;
            case Tier.High:
                RenderScale = 1.0f; Shadows = true; MsaaLevel = 2; MirrorRange = 12f; BloomEnabled = true; break;
        }
    }

    /// Push the current settings onto the engine. Safe to call repeatedly; a no-op under headless.
    public static void Apply()
    {
        Engine.MaxFps = MaxFps;

        var vp = (SceneTree)Engine.GetMainLoop() is { Root: { } root } ? root : null;
        if (vp != null)
        {
            vp.Scaling3DScale = RenderScale;
            vp.Msaa3D = MsaaLevel switch
            {
                0 => Viewport.Msaa.Disabled,
                1 => Viewport.Msaa.Msaa2X,
                2 => Viewport.Msaa.Msaa4X,
                _ => Viewport.Msaa.Msaa8X,
            };
        }

        if (DisplayServer.GetName() != "headless")
        {
            DisplayServer.WindowSetVsyncMode(
                VSync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
        }

        Settings.Save();
    }

    /// Persistence for the graphics + audio + control settings, in `user://settings.cfg`.
    public static class Settings
    {
        private const string Path = "user://settings.cfg";

        // Non-graphics settings that the settings menu also owns, kept here so there's one file.
        public static float MouseSensitivity = 0.003f;
        public static float MasterVolume = 1.0f;
        public static string OutputDevice = "Default";
        public static string InputDevice = "Default";
        public static bool NameTags = true;
        public static bool ProfilePictures = true;
        public static bool StartThirdPerson = false;

        private static bool _loading;

        public static void Load()
        {
            var cfg = new ConfigFile();
            if (cfg.Load(Path) != Error.Ok) return; // first run: keep detected defaults
            _loading = true;

            Current = (Tier)(int)cfg.GetValue("graphics", "tier", (int)Current);
            RenderScale = (float)cfg.GetValue("graphics", "render_scale", RenderScale);
            Shadows = (bool)cfg.GetValue("graphics", "shadows", Shadows);
            MsaaLevel = (int)cfg.GetValue("graphics", "msaa", MsaaLevel);
            MaxFps = (int)cfg.GetValue("graphics", "max_fps", MaxFps);
            VSync = (bool)cfg.GetValue("graphics", "vsync", VSync);
            MirrorRange = (float)cfg.GetValue("graphics", "mirror_range", MirrorRange);
            BloomEnabled = (bool)cfg.GetValue("graphics", "bloom", BloomEnabled);

            MouseSensitivity = (float)cfg.GetValue("controls", "sensitivity", MouseSensitivity);
            MasterVolume = (float)cfg.GetValue("audio", "master", MasterVolume);
            OutputDevice = (string)cfg.GetValue("audio", "output_device", OutputDevice);
            InputDevice = (string)cfg.GetValue("audio", "input_device", InputDevice);
            NameTags = (bool)cfg.GetValue("ui", "name_tags", NameTags);
            ProfilePictures = (bool)cfg.GetValue("ui", "pfp", ProfilePictures);
            StartThirdPerson = (bool)cfg.GetValue("controls", "third_person", StartThirdPerson);

            try
            {
                if (!string.IsNullOrEmpty(OutputDevice) && OutputDevice != "Default")
                    AudioServer.OutputDevice = OutputDevice;
                if (!string.IsNullOrEmpty(InputDevice) && InputDevice != "Default")
                    AudioServer.InputDevice = InputDevice;
            }
            catch { }

            _loading = false;
        }

        public static void Save()
        {
            if (_loading) return; // don't write back the values we're mid-load
            var cfg = new ConfigFile();
            cfg.SetValue("graphics", "tier", (int)Current);
            cfg.SetValue("graphics", "render_scale", RenderScale);
            cfg.SetValue("graphics", "shadows", Shadows);
            cfg.SetValue("graphics", "msaa", MsaaLevel);
            cfg.SetValue("graphics", "max_fps", MaxFps);
            cfg.SetValue("graphics", "vsync", VSync);
            cfg.SetValue("graphics", "mirror_range", MirrorRange);
            cfg.SetValue("graphics", "bloom", BloomEnabled);
            cfg.SetValue("controls", "sensitivity", MouseSensitivity);
            cfg.SetValue("controls", "third_person", StartThirdPerson);
            cfg.SetValue("audio", "master", MasterVolume);
            cfg.SetValue("audio", "output_device", OutputDevice);
            cfg.SetValue("audio", "input_device", InputDevice);
            cfg.SetValue("ui", "name_tags", NameTags);
            cfg.SetValue("ui", "pfp", ProfilePictures);
            cfg.Save(Path);
        }
    }
}
