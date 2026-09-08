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
    /// The mirror camera spends this budget on the whole glass unless the glass fills the view.
    /// In that close-up case it crops to the visible physical patch and remaps local UVs, so the
    /// same target reaches screen density without allocating a giant full-glass render texture.
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
    public static bool IsStandaloneXr => _forceStandaloneXr ?? OS.HasFeature("android");

    private static bool? _forceStandaloneXr;

    /// Pretend to be (or not be) a standalone headset, for diagnostics only.
    ///
    /// A large amount of behaviour hangs off `IsStandaloneXr` — the whole Quest lighting path in
    /// `WorldLoader`, the LOD pass, occlusion culling, the HouseLights dim floors — and none of
    /// it could be rendered on a dev box, because the flag is a hard platform test. So the Quest
    /// Cinema was only ever verifiable by building an APK, sideloading it and putting a headset
    /// on, and it consequently shipped broken twice: once black, once flat and over-bright.
    ///
    /// This does NOT make a desktop run equivalent to a Quest — the Compatibility renderer's
    /// per-mesh light cap, its lack of HDR, and the tile GPU are all still absent. It makes the
    /// *scene* the Quest would be given inspectable and renderable, which is where both bugs
    /// actually lived.
    public static void ForceStandaloneXrForDiagnostics(bool on) => _forceStandaloneXr = on;

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

        // Occlusion culling is off on standalone. The software rasteriser culls the
        // interior of authored rooms (Cinema) and the headset reads that as no lights.
        if (IsStandaloneXr)
            SerikaSocial.World.WorldLod.DisableLiveOcclusionCulling();
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

            if (IsStandaloneXr)
                vp.UseOcclusionCulling = false;

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

        /// Schema version of the settings file. Bump this whenever a DEFAULT changes in a way that
        /// existing users must pick up, and guard that key's read with `storedVersion >= N` in
        /// `Load`. A settings file records what the value *was*, not whether the user chose it, so
        /// a changed default is invisible to everyone who has already run the app — which is
        /// exactly the people whose habits the change is meant to correct.
        private const int SettingsVersion = 1;

        // Non-graphics settings that the settings menu also owns, kept here so there's one file.
        public static float MouseSensitivity = 0.003f;
        public static float MasterVolume = 1.0f;
        public static string OutputDevice = "Default";
        public static string InputDevice = "Default";
        public static bool NameTags = true;
        public static bool ProfilePictures = true;
        public static bool StartThirdPerson = false;

        /// Live-event show effects: pyro/laser surfaces, the audience penlights and the held
        /// light sticks. Persisted rather than session-scoped because the machines that need it
        /// off need it off at every event, and re-finding the toggle each time is the kind of
        /// friction that makes people stop attending.
        public static bool EventEffects = true;

        /// Pin live-event shows to full quality instead of letting the frame-rate watchdog
        /// reduce them. Off by default: the watchdog is right for most machines. It is wrong for
        /// one that can never reach the recovery threshold, which is exactly the machine whose
        /// owner will want this.
        public static bool EventFullQuality = false;

        /// Discord Social SDK rich presence. Off skips the Authorize popup entirely.
        public static bool DiscordPresence = true;

        // ── Voice ─────────────────────────────────────────────────────────────────────
        //
        /// How the mic gates when it is on. Open-mic (voice-activated) is the default because it
        /// is what a social space is for; push-to-talk is the setting people in shared rooms
        /// reach for, and Always transmits with no gate at all.
        public static Player.MicMode VoiceMicMode = Player.MicMode.Open;

        // ── Audio mix ─────────────────────────────────────────────────────────────────
        //
        // Per-category volumes, 0..2 linear. These map onto the buses in `Audio.AudioBuses`, so
        // "mute the world but keep voices" is a real state rather than a compromise on one slider.
        public static float VolVoice = 1f;
        public static float VolWorld = 1f;
        public static float VolSfx = 1f;
        public static float VolMusic = 0.7f;
        public static float VolMedia = 1f;
        public static float VolUi = 0.8f;

        /// Headphones / speakers / surround. Governs positional panning width, not channel count —
        /// see `AudioBuses.ApplyOutput`.
        public static Audio.AudioBuses.Output AudioOutputMode = Audio.AudioBuses.Output.Auto;

        /// Master compression so nothing can spike over everything else.
        public static bool NightMode;

        /// Multiplier on the VAD gate threshold. Above 1 needs a louder voice to open (noisy
        /// room), below 1 opens more readily.
        public static float VoiceSensitivity = 1f;

        /// Linear gain applied to captured audio before transmit.
        public static float VoiceMicGain = 1f;

        /// Playback volume for everyone else's voice, 0..2.
        public static float VoiceOutputGain = 1f;

        /// Start each session with the mic live rather than muted. Off by default: joining a
        /// world with a hot mic you did not know was hot is the failure people actually mind.
        public static bool VoiceStartUnmuted;

        /// Last answer to Discord's in-client Authorize prompt. `Declined` is sticky — we do
        /// not pop the overlay again until the player turns presence back on in Settings.
        public enum DiscordConsentKind { Unknown = 0, Granted = 1, Declined = 2 }
        public static DiscordConsentKind DiscordConsent = DiscordConsentKind.Unknown;

        // VR comfort. Defaults are the conservative end of each axis — snap turning and a
        // movement vignette are what a first-time headset user tolerates; smooth turning with
        // no vignette is the setting experienced players opt into.
        public static bool VrSnapTurn = true;
        public static float VrSnapTurnAngle = 30f;    // degrees per snap
        public static float VrSmoothTurnSpeed = 120f; // degrees/sec when snap turn is off
        public static bool VrVignette = true;
        public static float VrVignetteStrength = 0.7f; // 0..1, how far the aperture closes
        public static bool VrHaptics = true;
        public static float VrHeightOffset = 0f;       // manual calibration, metres

        /// How the left stick moves you. The two modes social VR has standardised on, and the
        /// reason `VrTeleport` above is now derived rather than authoritative: a bool cannot grow
        /// a third mode, and it could not express "teleport is available *while* smooth is the
        /// default", which is how the dash below works.
        public enum Locomotion { Smooth = 0, Teleport = 1 }

        /// Which way "forward" is. Head-relative walks where you look; hand-relative walks where
        /// the *left controller* points, which lets you strafe around something while keeping your
        /// eyes on it. Hand-relative is what experienced players switch to and what desktop
        /// players find baffling, so head stays the default.
        public enum MoveOrientation { Head = 0, Hand = 1 }

        public static Locomotion VrLocomotion = Locomotion.Smooth;
        public static MoveOrientation VrMoveOrientation = MoveOrientation.Head;

        /// Teleport on the *right* stick even in smooth mode. Costs nothing when unused and means
        /// a player who is fine with smooth locomotion can still blink across a room.
        public static bool VrDashTeleport = true;

        /// Which hand walks. Default is the RIGHT stick, with turning on the left.
        ///
        /// The opposite of the VRChat/Godot convention on purpose — this is what the project owner
        /// asked for after using it. Both layouts are here because stick-hand preference is close
        /// to religious and neither is wrong.
        /// Locomotion on the right stick instead of the left.
        ///
        /// Default is LEFT, which is VRChat's layout and therefore the one social VR players
        /// already have in muscle memory. This defaulted to the right, which is a defensible
        /// choice in isolation and the wrong one here: a player whose first minute is spent
        /// turning when they meant to walk does not conclude that the sticks are swapped, they
        /// conclude the port is broken.
        public static bool VrMoveOnRightStick = false;

        /// Push forward on the stick, walk forward. Off by default because that is what the
        /// hardware reports here; kept as a setting because thumbstick Y sign is a genuine
        /// per-runtime difference, not something to hardcode and hope.
        public static bool VrInvertForward;

        /// Optical hand tracking: use bare hands when the runtime sees them, drive the avatar's
        /// fingers from the joints, and put the UI ray on the index fingertip.
        public static bool VrHandTracking = true;

        /// Curl the avatar's fingers from the trigger/grip (controllers) or the tracked joints
        /// (bare hands). Separate from `VrHandTracking` because it applies to controllers too.
        public static bool VrFingerPosing = true;

        /// Scale the reach of the avatar's arms to the player's own.
        ///
        /// **Off by default, and the reason is worth stating because the trade-off is not obvious.**
        /// Stylised avatars have short limbs — a 1.57 m rig reaches ~43 cm shoulder to wrist against
        /// an adult's ~60 cm — so unscaled, the avatar's hands sit ~15 cm behind the player's on any
        /// real reach, with the elbows locked straight through the whole outer half of the working
        /// volume. Scaling fixes that, and it makes the avatar look right to everyone else.
        ///
        /// But it fixes the wrong view. Scaling preserves *direction* and compresses *distance*
        /// about the shoulder, which necessarily moves the avatar's hands closer to its body than
        /// the player's hands are to theirs. In third person and on every peer's screen that reads
        /// as correct proportion. In first person — the view the player is actually in — it breaks
        /// co-location: you look down and your hands are not where your hands are. That is a worse
        /// failure than short arms, because hand co-location is most of what presence in VR *is*,
        /// and it was reported from a headset as the tracking feeling broken.
        ///
        /// The real fix for a mismatched body is to scale the play space so the player *is* the
        /// avatar's size, which preserves co-location and reach together at the cost of changing
        /// perceived world scale. Until that exists and can be tested on a headset, the default is
        /// literal 1:1 tracking, which is at worst stiff rather than wrong.
        public static bool VrArmScaling = false;

        /// The wrist-anchored info panel (world, players, clock). Off puts nothing at all in the
        /// player's view while they are just standing in a world, which some people want.
        public static bool VrWristHud = true;

        /// Force the seated-reference-space compensation on, regardless of what the runtime
        /// reports.
        ///
        /// `VrPlayer.DetectReferenceSpace` works this out on its own and is right on every runtime
        /// tested, but "my view is at my feet" is severe enough — and the detection depends on the
        /// runtime honestly reporting user presence — that a player hitting it needs a switch they
        /// can find, not a bug report and a wait. Off means auto-detect.
        public static bool VrForceSeatedSpace;

        /// Plant the avatar's feet in the world and step them, instead of playing a walk cycle
        /// underneath a body that slides to follow the headset.
        ///
        /// On by default because the alternative is visibly wrong to everyone *except* the player
        /// wearing it: without it the feet skate whenever their owner leans, and the legs keep
        /// walking on the spot while the body stands still. It costs two downward raycasts per
        /// frame, so it is a setting rather than an invariant.
        public static bool VrFootIk = true;

        /// Let the avatar's wrists take the controllers' rotation.
        ///
        /// Only a setting because the neutral alignment depends on the runtime publishing grip
        /// poses in Godot's −Z-forward convention; a runtime that does not would leave the hands
        /// rotated off-square, and turning this off falls back to wrists that follow the forearm.
        /// Relative wrist motion is correct either way — see `VrAvatarIk.BuildHandFromController`.
        public static bool VrWristTracking = true;

        /// Bend the avatar — and shrink the player's collider — when the player physically
        /// crouches. Off pins the body upright and keeps a fixed-height capsule.
        public static bool VrPhysicalCrouch = true;

        /// How far the head may turn before the body follows it, in degrees.
        ///
        /// Zero makes the body a slave to the gaze, which is what this used to be: every glance
        /// over your shoulder slowly swung your whole avatar round, and every peer watched you
        /// pirouette. A real person turns their head first and their shoulders only if they keep
        /// looking. Past this angle the body catches up.
        public static float VrBodyTurnDeadzone = 40f;

        /// Back-compat shim for the old `VrTeleport` bool. Reading and writing the enum through
        /// this keeps every existing call site working while there is one source of truth.
        public static bool VrTeleport
        {
            get => VrLocomotion == Locomotion.Teleport;
            set => VrLocomotion = value ? Locomotion.Teleport : Locomotion.Smooth;
        }

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

            // Which schema this file was written by. Anything older than `SettingsVersion` predates
            // a deliberate change of default, and the keys listed in `MigrateFrom` below are reset
            // rather than read.
            int storedVersion = (int)cfg.GetValue("meta", "settings_version", 0);

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
            EventEffects = (bool)cfg.GetValue("events", "effects", EventEffects);
            EventFullQuality = (bool)cfg.GetValue("events", "full_quality", EventFullQuality);
            DiscordPresence = (bool)cfg.GetValue("discord", "presence", DiscordPresence);
            DiscordConsent = (DiscordConsentKind)(int)cfg.GetValue("discord", "consent", (int)DiscordConsent);

            VoiceMicMode = (Player.MicMode)(int)cfg.GetValue("voice", "mic_mode", (int)VoiceMicMode);
            KeyBindings.Load(cfg);
            VolVoice = (float)cfg.GetValue("mix", "voice", VolVoice);
            VolWorld = (float)cfg.GetValue("mix", "world", VolWorld);
            VolSfx = (float)cfg.GetValue("mix", "sfx", VolSfx);
            VolMusic = (float)cfg.GetValue("mix", "music", VolMusic);
            VolMedia = (float)cfg.GetValue("mix", "media", VolMedia);
            VolUi = (float)cfg.GetValue("mix", "ui", VolUi);
            AudioOutputMode = (Audio.AudioBuses.Output)(int)cfg.GetValue("mix", "output_mode", (int)AudioOutputMode);
            NightMode = (bool)cfg.GetValue("mix", "night_mode", NightMode);
            VoiceSensitivity = (float)cfg.GetValue("voice", "sensitivity", VoiceSensitivity);
            VoiceMicGain = (float)cfg.GetValue("voice", "mic_gain", VoiceMicGain);
            VoiceOutputGain = (float)cfg.GetValue("voice", "output_gain", VoiceOutputGain);
            VoiceStartUnmuted = (bool)cfg.GetValue("voice", "start_unmuted", VoiceStartUnmuted);

            VrSnapTurn = (bool)cfg.GetValue("vr", "snap_turn", VrSnapTurn);
            VrSnapTurnAngle = (float)cfg.GetValue("vr", "snap_turn_angle", VrSnapTurnAngle);
            VrSmoothTurnSpeed = (float)cfg.GetValue("vr", "smooth_turn_speed", VrSmoothTurnSpeed);
            VrVignette = (bool)cfg.GetValue("vr", "vignette", VrVignette);
            VrVignetteStrength = (float)cfg.GetValue("vr", "vignette_strength", VrVignetteStrength);
            // `teleport` is the pre-1.6.4 key. Read it first so an existing config carries its
            // choice forward, then let the newer `locomotion` key win if it is present.
            VrTeleport = (bool)cfg.GetValue("vr", "teleport", VrTeleport);
            VrLocomotion = (Locomotion)(int)cfg.GetValue("vr", "locomotion", (int)VrLocomotion);
            VrMoveOrientation = (MoveOrientation)(int)cfg.GetValue("vr", "move_orientation", (int)VrMoveOrientation);
            VrDashTeleport = (bool)cfg.GetValue("vr", "dash_teleport", VrDashTeleport);
            VrInvertForward = (bool)cfg.GetValue("vr", "invert_forward", VrInvertForward);
            // Migrated in v1: the default moved from the right stick to the left to match VRChat.
            // A stored `true` is indistinguishable from "the user chose this" and from "this was
            // simply the old default", and on every existing install it is the latter — so before
            // v1 the value is discarded and the new default stands. Without this the changed
            // default reaches nobody who has ever launched the app, which is the whole audience
            // that had already learned the wrong layout.
            if (storedVersion >= 1)
                VrMoveOnRightStick = (bool)cfg.GetValue("vr", "move_on_right_stick", VrMoveOnRightStick);
            VrHandTracking = (bool)cfg.GetValue("vr", "hand_tracking", VrHandTracking);
            VrFingerPosing = (bool)cfg.GetValue("vr", "finger_posing", VrFingerPosing);
            // Migrated in v1 for the same reason: arm scaling shipped on for one build, broke
            // first-person hand co-location, and now defaults off.
            if (storedVersion >= 1)
                VrArmScaling = (bool)cfg.GetValue("vr", "arm_scaling", VrArmScaling);
            VrWristHud = (bool)cfg.GetValue("vr", "wrist_hud", VrWristHud);
            VrForceSeatedSpace = (bool)cfg.GetValue("vr", "force_seated_space", VrForceSeatedSpace);
            VrFootIk = (bool)cfg.GetValue("vr", "foot_ik", VrFootIk);
            VrWristTracking = (bool)cfg.GetValue("vr", "wrist_tracking", VrWristTracking);
            VrPhysicalCrouch = (bool)cfg.GetValue("vr", "physical_crouch", VrPhysicalCrouch);
            VrBodyTurnDeadzone = (float)cfg.GetValue("vr", "body_turn_deadzone", VrBodyTurnDeadzone);
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
            cfg.SetValue("events", "effects", EventEffects);
            cfg.SetValue("events", "full_quality", EventFullQuality);
            cfg.SetValue("discord", "presence", DiscordPresence);
            cfg.SetValue("discord", "consent", (int)DiscordConsent);

            cfg.SetValue("voice", "mic_mode", (int)VoiceMicMode);
            KeyBindings.Save(cfg);
            cfg.SetValue("mix", "voice", VolVoice);
            cfg.SetValue("mix", "world", VolWorld);
            cfg.SetValue("mix", "sfx", VolSfx);
            cfg.SetValue("mix", "music", VolMusic);
            cfg.SetValue("mix", "media", VolMedia);
            cfg.SetValue("mix", "ui", VolUi);
            cfg.SetValue("mix", "output_mode", (int)AudioOutputMode);
            cfg.SetValue("mix", "night_mode", NightMode);
            cfg.SetValue("voice", "sensitivity", VoiceSensitivity);
            cfg.SetValue("voice", "mic_gain", VoiceMicGain);
            cfg.SetValue("voice", "output_gain", VoiceOutputGain);
            cfg.SetValue("voice", "start_unmuted", VoiceStartUnmuted);
            cfg.SetValue("vr", "snap_turn", VrSnapTurn);
            cfg.SetValue("vr", "snap_turn_angle", VrSnapTurnAngle);
            cfg.SetValue("vr", "smooth_turn_speed", VrSmoothTurnSpeed);
            cfg.SetValue("vr", "vignette", VrVignette);
            cfg.SetValue("vr", "vignette_strength", VrVignetteStrength);
            cfg.SetValue("vr", "locomotion", (int)VrLocomotion);
            cfg.SetValue("vr", "move_orientation", (int)VrMoveOrientation);
            cfg.SetValue("vr", "dash_teleport", VrDashTeleport);
            cfg.SetValue("vr", "invert_forward", VrInvertForward);
            cfg.SetValue("vr", "move_on_right_stick", VrMoveOnRightStick);
            cfg.SetValue("vr", "hand_tracking", VrHandTracking);
            cfg.SetValue("vr", "finger_posing", VrFingerPosing);
            cfg.SetValue("vr", "arm_scaling", VrArmScaling);
            cfg.SetValue("vr", "wrist_hud", VrWristHud);
            cfg.SetValue("vr", "force_seated_space", VrForceSeatedSpace);
            cfg.SetValue("vr", "foot_ik", VrFootIk);
            cfg.SetValue("vr", "wrist_tracking", VrWristTracking);
            cfg.SetValue("vr", "physical_crouch", VrPhysicalCrouch);
            cfg.SetValue("vr", "body_turn_deadzone", VrBodyTurnDeadzone);
            cfg.SetValue("vr", "haptics", VrHaptics);
            cfg.SetValue("vr", "height_offset", VrHeightOffset);
            cfg.SetValue("meta", "settings_version", SettingsVersion);
            cfg.Save(Path);
        }
    }
}
