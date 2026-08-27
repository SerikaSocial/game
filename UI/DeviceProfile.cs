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

    /// How many mirrors may render a live reflection in the same frame.
    ///
    /// Each live mirror is a SECOND FULL RENDER of the scene, and worlds do not ration them —
    /// the Mirror Gallery hangs eight in one room, all comfortably inside `MirrorRange`, which
    /// uncapped is eight extra scene renders every frame. Mirrors past the budget fall back to
    /// dark glass, nearest kept first, so the one you are actually looking into is always live.
    public static int MirrorBudget => Current switch
    {
        Tier.Low => 1,
        Tier.Medium => 2,
        _ => 3,
    };

    /// A mirror's reflection resolution, as a fraction of the main viewport's pixel size.
    ///
    /// The mirror shader samples its texture by SCREEN_UV, so the reflection is a screen-space
    /// image: it is sharp only when the render target matches the screen's pixel dimensions.
    /// Sizing it from the mirror's physical metres instead — which is what it used to do — left
    /// a 2.2 m mirror rendering 660 px tall and then upscaled ~1.6× on a 1080p display.
    public static float MirrorResolutionScale => Current switch
    {
        Tier.Low => 0.6f,
        Tier.Medium => 0.8f,
        _ => 1.0f,
    };

    /// Cel/toon shading for avatars and worlds. The banded look is core to Serika's style, so
    /// it stays on across tiers; only the outline (a second draw of every avatar surface, which
    /// matters on a tile GPU) drops on the Quest-Low tier.
    public static bool ToonShading = true;
    public static bool AvatarOutline = true;

    /// True on the Quest / any Android build — the tightest budget.
    public static bool IsStandaloneXr => OS.HasFeature("android");

    /// Pick a starting tier from the platform and seed the knobs. Called once at boot before the
    /// first frame renders.
    public static void Detect()
    {
        if (IsStandaloneXr)
        {
            // Quest 3 has ~2× the GPU of Quest 2; treat it as Medium so it gets better visuals.
            bool isQuest3 = OS.GetModelName().Contains("Quest 3") || OS.GetModelName().Contains("Quest3");
            if (isQuest3)
            {
                Current = Tier.Medium;
                RenderScale = 0.92f;
                Shadows = true;
                MsaaLevel = 1;
                MaxFps = 90;     // Quest 3 supports 90/120 Hz
                VSync = true;
                MirrorRange = 8f;
                BloomEnabled = true;
            }
            else
            {
                // Quest 2 / Quest Pro / unknown standalone — everything conservative.
                Current = Tier.Low;
                RenderScale = 0.72f;  // was 0.85 — lower to give more headroom
                Shadows = false;
                MsaaLevel = 1;            // 2x MSAA is cheap on tile GPUs and worth it in VR
                MaxFps = 72;             // Quest 2 display cadence
                VSync = true;
                MirrorRange = 6f;
                BloomEnabled = false;
                AvatarOutline = false;   // the outline's extra draw pass isn't worth it here
            }

            // Lower physics tick rate on standalone to save CPU. The default 60 Hz is overkill
            // for a social app where collision precision doesn't matter much.
            Engine.PhysicsTicksPerSecond = isQuest3 ? 50 : 45;
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

        // Enable occlusion culling on standalone — the software rasteriser is essentially
        // free and saves significant draw calls in enclosed worlds.
        if (IsStandaloneXr)
            SerikaSocial.World.WorldLod.EnableOcclusionCulling();
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

            // Shadow atlas. The default 4096 map is a big, always-resident render target and a
            // sizeable share of a tile GPU's bandwidth budget; a Low tier that has shadows on at
            // all does not need more than 1024.
            vp.PositionalShadowAtlasSize = Current switch
            {
                Tier.Low => 1024,
                Tier.Medium => 2048,
                _ => 4096,
            };

            // Debanding: dither the final image to break up 8-bit quantisation.
            //
            // Not cosmetic here. A point light's falloff across a flat wall is a very shallow
            // gradient, and in a dim scene the whole gradient spans only a handful of the 256
            // available levels — so it renders as a set of hard concentric contour rings
            // centred on the light. The Cinema looked like a topographic map of itself. It went
            // unnoticed for as long as worlds were cel-shaded, because banding a gradient into
            // three flat steps is what that shader does on purpose; it only surfaced once
            // worlds went back to smooth PBR falloff.
            //
            // The cost is a single full-screen dither in the tonemap pass, so it stays on for
            // every tier — a mobile GPU is if anything more prone to this, not less.
            vp.UseDebanding = true;

            ApplyFoveation();
        }

        if (DisplayServer.GetName() != "headless")
        {
            DisplayServer.WindowSetVsyncMode(
                VSync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
        }

        // Shadows and glow live on scene nodes, not on the engine, so they only take effect if
        // something walks the tree. Without this the Quest detected `Shadows = false` at boot and
        // then rendered every shadow anyway — the setting was saved and never enforced.
        if (vp != null) ApplyToScene(vp);

        Settings.Save();
    }

    /// Enforce the node-level graphics settings across a subtree. Call after building or loading a
    /// world: freshly spawned lights and environments default to full quality regardless of tier.
    public static void ApplyToScene(Node root)
    {
        if (root == null) return;

        foreach (var node in root.FindChildren("*", "Light3D", true, false))
        {
            if (node is not Light3D light) continue;
            light.ShadowEnabled = Shadows;
            // Avatars reserve visual layers 3/4 for the first-person head/body split. A light
            // only casts shadows from objects whose layers intersect BOTH of its masks, and
            // that lookup is per-light — hiding the head from a camera never excuses it from
            // the shadow map, but only while lights keep these bits. Worlds import with their
            // own mask notions, so this is re-stamped on every load: without it your ground
            // silhouette loses its hair (and reads as headless) exactly when first person is on.
            light.LightCullMask |= Player.LocalPlayer.AvatarRenderLayers;
            light.ShadowCasterMask |= Player.LocalPlayer.AvatarRenderLayers;
            // Directional shadows are the expensive ones. Pulling the split distance in is a much
            // bigger win on a tile GPU than lowering resolution, and barely visible in a social
            // space where the interesting geometry is all within a few metres.
            if (light is DirectionalLight3D dir)
            {
                dir.DirectionalShadowMaxDistance = Current switch
                {
                    Tier.Low => 25f,
                    Tier.Medium => 50f,
                    _ => 100f,
                };
                dir.DirectionalShadowMode = Current == Tier.Low
                    ? DirectionalLight3D.ShadowMode.Orthogonal        // one split, one shadow pass
                    : DirectionalLight3D.ShadowMode.Parallel4Splits;
            }
        }

        foreach (var node in root.FindChildren("*", "WorldEnvironment", true, false))
        {
            if (node is not WorldEnvironment we || we.Environment is not { } env) continue;
            env.GlowEnabled = BloomEnabled;
            // Screen-space effects are full-resolution passes per eye; nothing survives Low tier.
            if (Current == Tier.Low)
            {
                env.SsaoEnabled = false;
                env.SsilEnabled = false;
                env.SsrEnabled = false;
                env.VolumetricFogEnabled = false;
            }
        }
    }

    /// Turn on OpenXR fixed foveated rendering. The periphery of each eye buffer is shaded at a
    /// fraction of the centre's rate, which is the single cheapest large win on a standalone
    /// headset and is invisible through the lenses at their edge resolution.
    private static void ApplyFoveation()
    {
        if (XRServer.FindInterface("OpenXR") is not OpenXRInterface xr || !xr.IsInitialized()) return;
        xr.FoveationLevel = Current switch
        {
            Tier.Low => 3,     // high
            Tier.Medium => 2,  // medium
            _ => 1,            // low
        };
        // Follow the gaze where the runtime supports it; a static pattern otherwise.
        xr.FoveationDynamic = true;
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

        // VR comfort. Defaults are the conservative end of each axis — snap turning and a
        // movement vignette are what a first-time headset user tolerates; smooth turning with
        // no vignette is the setting experienced players opt into.
        public static bool VrSnapTurn = true;
        public static float VrSnapTurnAngle = 30f;    // degrees per snap
        public static float VrSmoothTurnSpeed = 120f; // degrees/sec when snap turn is off
        public static bool VrVignette = true;
        public static float VrVignetteStrength = 0.7f; // 0..1, how far the aperture closes
        public static bool VrTeleport = false;         // teleport locomotion instead of smooth
        public static bool VrHaptics = true;
        public static float VrHeightOffset = 0f;       // manual calibration, metres

        private static bool _loading;

        /// Route audio to the saved devices. ONLY a real interactive boot may call this —
        /// never diagnostics, UI-shot runs or anything under a virtual display. Those share
        /// the developer's live PulseAudio server, and silently rerouting system-wide sound
        /// from a test run is exactly how "the build nuked my audio prefs" happens. The
        /// settings menu applies its own changes immediately, so boot was the one caller that
        /// mattered.
        public static void ApplyAudioDevices()
        {
            if (_loading) return;
            try
            {
                if (!string.IsNullOrEmpty(OutputDevice) && OutputDevice != "Default")
                    AudioServer.OutputDevice = OutputDevice;
                if (!string.IsNullOrEmpty(InputDevice) && InputDevice != "Default")
                    AudioServer.InputDevice = InputDevice;
            }
            catch { }
        }

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

            VrSnapTurn = (bool)cfg.GetValue("vr", "snap_turn", VrSnapTurn);
            VrSnapTurnAngle = (float)cfg.GetValue("vr", "snap_turn_angle", VrSnapTurnAngle);
            VrSmoothTurnSpeed = (float)cfg.GetValue("vr", "smooth_turn_speed", VrSmoothTurnSpeed);
            VrVignette = (bool)cfg.GetValue("vr", "vignette", VrVignette);
            VrVignetteStrength = (float)cfg.GetValue("vr", "vignette_strength", VrVignetteStrength);
            VrTeleport = (bool)cfg.GetValue("vr", "teleport", VrTeleport);
            VrHaptics = (bool)cfg.GetValue("vr", "haptics", VrHaptics);
            VrHeightOffset = (float)cfg.GetValue("vr", "height_offset", VrHeightOffset);

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
            cfg.SetValue("vr", "snap_turn", VrSnapTurn);
            cfg.SetValue("vr", "snap_turn_angle", VrSnapTurnAngle);
            cfg.SetValue("vr", "smooth_turn_speed", VrSmoothTurnSpeed);
            cfg.SetValue("vr", "vignette", VrVignette);
            cfg.SetValue("vr", "vignette_strength", VrVignetteStrength);
            cfg.SetValue("vr", "teleport", VrTeleport);
            cfg.SetValue("vr", "haptics", VrHaptics);
            cfg.SetValue("vr", "height_offset", VrHeightOffset);
            cfg.Save(Path);
        }
    }
}
