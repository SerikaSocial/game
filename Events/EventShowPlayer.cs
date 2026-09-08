using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using Godot;
using Serika.Net;
using SerikaSocial.Avatar;
namespace SerikaSocial.Events;

/// One deterministic show per client. Screens share a single render, including in VR.
public partial class EventShowPlayer : Node3D
{
    public event Action<string> Failed;
    /// The automatic quality drop flipped. False means the show is now running reduced: half the
    /// stage-screen refresh, thinned cinematic effects. Surfaced because it is otherwise
    /// indistinguishable from the show being broken.
    public event Action<bool> QualityChanged;
    public string EventId => _state?.Id;
    public bool ReadyToPlay { get; private set; }
    public Camera3D BroadcastCamera => _camera;
    private LiveEvent _state;
    private long _serverTime;
    private ulong _receivedAt;
    private AvatarInstance _artist;
    private AnimRetargeter _animation;
    private AudioStreamPlayer _audio;
    private SubViewport _feed;
    private Camera3D _camera;
    private int _generation;
    private int _playingRevision = -1;
    private readonly List<(MeshInstance3D Mesh, Material Material, uint Layers)> _screens = new();
    /// Quality thresholds are a FRACTION of the frame rate this machine is actually trying to
    /// hit, not absolute numbers. The old pair was a flat 42 down / 48 up, which on a mid-range
    /// GPU at a heavy venue is unreachable: the show dropped to reduced quality within seconds
    /// and could never climb back, so the stage screens ran at half rate for the whole event
    /// with nothing on screen explaining why. Reported as "the side displays don't do shit".
    private const float FpsDownFraction = 0.62f;
    private const float FpsUpFraction = 0.72f;
    /// What "full speed" means here: an explicit cap if the player set one, otherwise the
    /// display's own refresh rate. Clamped because a headless or unknown display reports -1.
    private static float TargetFps => Engine.MaxFps > 0
        ? Engine.MaxFps
        : Mathf.Clamp(DisplayServer.ScreenGetRefreshRate(), 30f, 144f);
    private const int FpsDownFrames = 24;
    private const int FpsUpFrames = 60;
    private float _fpsSmoothing = 60;
    private int _fpsDropFrames;
    private int _fpsRecoverFrames;
    private bool _highQuality = true;
    /// Pin full quality and ignore the frame rate entirely.
    ///
    /// The automatic drop is one-way in practice on mid-range hardware: a GPU that cannot hold
    /// the down threshold at a heavy venue will never reach the up threshold either, so the show
    /// runs reduced for the whole event and the player has no say in it. Some people would
    /// rather have the stage screens at full rate and take the frame rate hit. This is that
    /// choice, and it is theirs to make, not the hysteresis's.
    public bool ForceFullQuality { get; set; }
    private double _broadcastAccumulator;
    private const double LowQualityFxCadence = 1.0 / 18.0;
    private double _fxAccumulator;
    public bool Preview { get; set; }
    public bool HighQuality => _highQuality;
    private Node3D _world;
    private bool _effectsEnabled = true;
    /// Player-facing escape hatch for a venue their machine cannot carry. Drops the pyro/laser
    /// surfaces, the audience penlights and the held light sticks — the expensive per-frame
    /// shader work — while leaving the performer, the stage lighting, the screens and the audio
    /// alone, so the show is still the show. Toggling it back on rebuilds those rigs from the
    /// world, which is why the venue root is kept.
    public bool EffectsEnabled
    {
        get => _effectsEnabled;
        set
        {
            if (_effectsEnabled == value) return;
            _effectsEnabled = value;
            if (_world == null || !IsInstanceValid(_world)) return;
            RestoreShowEffects(); RestoreShowLasers(); RestoreConcertAudience(); RestorePlayerLightSticks();
            if (value) { SetupShowEffects(_world); SetupShowLasers(_world); SetupConcertAudience(_world); SetupPlayerLightSticks(_world); }
        }
    }
    public double PreviewPosition { get; set; }
    public bool PreviewPlaying { get; set; }
    public AvatarInstance Performer => _artist;
    public double AudioPosition => StageAudioActive ? _stageSpeakers[0].GetPlaybackPosition() : _audio?.GetPlaybackPosition() ?? 0;
    public string[] Clips => _animation?.ShowClips ?? Array.Empty<string>();

    public void ApplyState(LiveEvent state, double roundTripMs = 0)
    {
        if (_state != null && state.Revision < _state.Revision) return;
        _state = state;
        _serverTime = state.ServerTime + (long)(roundTripMs / 2);
        _receivedAt = Time.GetTicksMsec();
        if (state.Status != "live") { StopShowAudio(); _playingRevision = -1; }
    }
    public Task Prepare(ApiClient api, LiveEvent state, Node3D world) => PrepareFiles(state, world,
        Task.WhenAll(api.DownloadShowAssetAsync(state.Config.ArtistUrl), api.DownloadShowAssetAsync(state.Config.AnimationUrl),
            api.DownloadShowAssetAsync(state.Config.AudioUrl), string.IsNullOrEmpty(state.Config.IntroUrl)
                ? Task.FromResult<string>(null) : api.DownloadShowAssetAsync(state.Config.IntroUrl),
            OptionalAudio(api,state.Config.StageAudioLeftUrl), OptionalAudio(api,state.Config.StageAudioRightUrl), OptionalAudio(api,state.Config.IntroAudioUrl)));
    private static Task<string> OptionalAudio(ApiClient api,string url) => string.IsNullOrEmpty(url)?Task.FromResult<string>(null):api.DownloadShowAssetAsync(url);

    public async Task PrepareFiles(LiveEvent state, Node3D world, Task<string[]> sourceFiles)
    {
        ApplyState(state); int generation = ++_generation;
        try {
            var files = await sourceFiles;
            if (!IsInstanceValid(this) || !IsInsideTree() || generation != _generation) return;
            var c = state.Config;
            byte[] artistBytes = File.ReadAllBytes(files[0]);
            _artist = AvatarInstance.FromBytes(artistBytes);
            if (_artist?.Skeleton == null) throw new InvalidOperationException("The performer model has no usable humanoid skeleton.");
            ToonShading.ApplyToConcertAvatar(_artist, artistBytes);
            // Reserve a show-only layer so scenic fixtures cannot evict the singer's
            // four dedicated lights from the Mobile/Compatibility per-mesh light budget.
            foreach (Node node in _artist.FindChildren("*", "MeshInstance3D", true, false))
                ((MeshInstance3D)node).Layers = 1u << 18;
            AddChild(_artist); _artist.GlobalPosition = ShowTimeline.Vector(c.Performer);
            _artist.RotationDegrees = new Vector3(0, c.Yaw, 0); _artist.Scale = Vector3.One * c.Scale;
            var doc = new GltfDocument(); var gltf = new GltfState();
            if (doc.AppendFromBuffer(File.ReadAllBytes(files[1]), "", gltf) != Error.Ok) throw new InvalidOperationException("Animation GLB could not be loaded.");
            var source = doc.GenerateScene(gltf);
            _animation = AnimRetargeter.CreateFromScene(source, _artist.Skeleton, _artist.RoleToBoneForDiagnostics());
            if (_animation == null || _animation.MappedShowBones < 10) throw new InvalidOperationException("Animation skeleton does not match enough humanoid bones. Use the artist's rig or a Mixamo humanoid GLB.");
            _artist.AddChild(_animation);
            if (_animation.ShowClipLength(c.Clip) <= 0) throw new InvalidOperationException($"Clip '{c.Clip}' is not in the GLB. Choose an imported clip.");
            _audio = new AudioStreamPlayer { Name = "ShowAudio", Bus = "Music", Stream = LoadAudio(files[2]) }; AddChild(_audio);
            if (_audio.Stream == null || _audio.Stream.GetLength() <= 0) throw new InvalidOperationException("Audio could not be decoded.");
            // Protect against invalid API/old client configurations before allocating a viewport.
            if (c.Cameras == null || c.Cameras.Count == 0) throw new InvalidOperationException("Show needs at least one camera point.");
            _feed = new SubViewport { Name = "ShowBroadcast", Msaa3D = Viewport.Msaa.Msaa2X, Size = new Vector2I(OS.HasFeature("mobile") ? 256 : 512, OS.HasFeature("mobile") ? 384 : 768),
                World3D = GetViewport().World3D, AudioListenerEnable3D = false, TransparentBg = false, RenderTargetUpdateMode = SubViewport.UpdateMode.Always, HandleInputLocally = false };
            AddChild(_feed);
            _camera = new Camera3D { Name = "BroadcastCamera", Current = true, Far = 2500, CullMask = 0xfffff & ~((1u << 19) | Player.LocalPlayer.NonFpCullLayers) }; _feed.AddChild(_camera);
            BindScreens(world);
            _world = world;
            if (_screens.Count == 0) throw new InvalidOperationException("This venue has no SERIKA_EVENT_SCREEN display meshes.");
            SetupPresentation(world, files[3]);
            SetupBroadcastCamera(world);
            SetupStageAudio(world, files);
            if (_effectsEnabled) SetupPlayerLightSticks(world);
            ReadyToPlay = true;
            _animation.SampleShowClip(c.Clip, 0);
            // Direction-retargeted clips have no finger tracks. Give those unmapped
            // joints a relaxed resting shape instead of leaving every finger rigid.
            var curls = new float[] { .18f,.16f,.22f,.28f,.32f };
            var splay = new float[5];
            new HandPoser(_artist, true).Apply(curls,splay,0);
            new HandPoser(_artist, false).Apply(curls,splay,0);
        } catch (Exception e) { GD.PrintErr($"event show: {e}"); if (IsInstanceValid(this)) {
                ReadyToPlay=false;StopShowAudio();RestoreStageAudio();RestorePlayerLightSticks();RestoreBroadcastCamera();RestorePresentation();RestoreScreens();
                foreach(Node child in GetChildren())child.QueueFree();
                _audio=null;_artist=null;_animation=null;_feed=null;_camera=null;_introPlayer=null;_introViewport=null;
                Failed?.Invoke(e.Message);
            } }
    }
    public static AudioStream LoadAudio(string path) => Path.GetExtension(path).ToLowerInvariant() switch {
        ".ogg" => AudioStreamOggVorbis.LoadFromFile(path), ".mp3" => new AudioStreamMP3 { Data = File.ReadAllBytes(path) },
        ".wav" => AudioStreamWav.LoadFromFile(path), _ => null,
    };
    private void BindScreens(Node node)
    {
        foreach (var screen in FindPortraitScreens(node)) {
            if (_screens.Any(s => s.Mesh == screen)) continue;
            if (screen.MaterialOverride is not null) _screens.Add((screen, screen.MaterialOverride, screen.Layers));
            else _screens.Add((screen, null, screen.Layers));
            screen.Layers = 1u << 19;
            screen.MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoTexture = _feed.GetTexture(), CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        }
    }
    internal static bool IsPortraitScreenNode(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string lowered = name.ToLowerInvariant();
        if (lowered.StartsWith("serika_event_screen", StringComparison.Ordinal) || lowered.Contains("serika_event_screen")) return true;
        if (!lowered.Contains("event") || !lowered.Contains("screen")) return false;
        bool hasSideHint = lowered.Contains("left") || lowered.Contains("right") || lowered.Contains("portra") || lowered.Contains("side");
        if (!hasSideHint) return false;
        if (lowered.Contains("enclosure") || lowered.Contains("housing") || lowered.Contains("graphic")) return false;
        return true;
    }
    internal static List<MeshInstance3D> FindPortraitScreens(Node root)
    {
        var screens = new List<MeshInstance3D>();
        var seen = new HashSet<MeshInstance3D>();

        void Collect(Node node)
        {
            if (node is MeshInstance3D mesh && IsPortraitScreenNode(mesh.Name.ToString())) {
                if (seen.Add(mesh)) screens.Add(mesh);
            }
            foreach (Node child in node.GetChildren()) if (child != null) Collect(child);
        }

        Collect(root);

        if (screens.Count < 2) {
            void Fallback(Node node)
            {
                if (node is MeshInstance3D mesh) {
                    string lowered = mesh.Name.ToString().ToLowerInvariant();
                    bool candidate = lowered.Contains("screen") &&
                        (lowered.Contains("left") || lowered.Contains("right") || lowered.Contains("portra") || lowered.Contains("side")) &&
                        !lowered.Contains("enclosure") && !lowered.Contains("housing") && !lowered.Contains("graphic");
                    if (candidate && seen.Add(mesh)) screens.Add(mesh);
                }
                foreach (Node child in node.GetChildren()) if (child != null) Fallback(child);
            }
            Fallback(root);
        }

        if (screens.Count >= 2) {
            screens.Sort((a, b) => Math.Sign(a.GlobalPosition.X - b.GlobalPosition.X));
        }
        return screens;
    }
    public override void _Process(double delta)
    {
        if (!ReadyToPlay || _state == null) return;
        if (delta > 0) {
            // Measure the real frame duration; never clamp the show clock after a hitch.
            float fps = Mathf.Min(Mathf.Max(1f / (float)delta, 1), 500);
            _fpsSmoothing = Mathf.Lerp(_fpsSmoothing, fps, 0.15f);
            float target = TargetFps;
            if (_fpsSmoothing < target * FpsDownFraction) {
                _fpsDropFrames = Mathf.Min(_fpsDropFrames + 1, FpsDownFrames);
                _fpsRecoverFrames = 0;
            } else if (_fpsSmoothing > target * FpsUpFraction) {
                _fpsRecoverFrames = Mathf.Min(_fpsRecoverFrames + 1, FpsUpFrames);
                _fpsDropFrames = 0;
            } else {
                _fpsDropFrames = 0;
                _fpsRecoverFrames = 0;
            }
            bool wasHighQuality = _highQuality;
            if (ForceFullQuality) _highQuality = true;
            else {
                if (_highQuality && _fpsDropFrames >= FpsDownFrames) _highQuality = false;
                if (!_highQuality && _fpsRecoverFrames >= FpsUpFrames) _highQuality = true;
            }
            if (wasHighQuality != _highQuality) {
                GD.Print("CONCERT_QUALITY high=" + _highQuality + " fps=" + _fpsSmoothing + " event=" + (_state?.Title ?? "local"));
                // Say it out loud. This halves the stage-screen refresh and thins the cinematic
                // effects, and it did so silently — so a show that quietly degraded read as "the
                // displays stopped doing anything" with nothing to connect it to frame rate.
                QualityChanged?.Invoke(_highQuality);
            }
        }
        long now = _serverTime + (long)(Time.GetTicksMsec() - _receivedAt);
        if (Preview && PreviewPlaying) PreviewPosition = Math.Min(PreviewPosition + delta, _state.Config.Duration);
        bool playing = Preview ? PreviewPlaying : _state.Status == "live" && _state.StartedAt.HasValue && now >= _state.StartedAt;
        double seconds = Preview ? PreviewPosition : ShowTimeline.Position(now, _state.StartedAt, _state.Config.Duration);
        UpdateShowAudio(seconds,playing);
        ShowTimeline.PerformerAt(_state.Config, seconds, out var artistPosition, out float artistYaw);
        _artist.GlobalPosition = artistPosition;
        _artist.RotationDegrees = new Vector3(0, artistYaw, 0);
        bool running = playing || Preview;
        bool highQuality = _highQuality;
        _fxAccumulator += delta;
        bool runCinematicFx = highQuality || _fxAccumulator >= LowQualityFxCadence;
        if (runCinematicFx) _fxAccumulator = 0;
        UpdatePresentation(seconds, running, highQuality, delta, runCinematicFx);
        UpdateIntroStageAudio(running);
        if (_effectsEnabled) UpdatePlayerLightSticks(seconds, running, delta);
        // Both portrait screens share one camera render. Cap its refresh independently
        // of the player view, especially when the main frame budget is already exceeded.
        _broadcastAccumulator += delta;
        // Reduced quality drops the stage-screen refresh, but 15 Hz reads as broken rather than
        // as a saving. 20 Hz still cuts a third of the broadcast renders and stays motion.
        double feedInterval = highQuality ? 1.0 / 30.0 : 1.0 / 20.0;
        if (running && _broadcastAccumulator >= feedInterval) {
            _broadcastAccumulator %= feedInterval;
            _feed.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        } else _feed.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        _animation.SampleShowClip(_state.Config.Clip, playing || Preview ? seconds : 0);
        UpdateBroadcastCamera(seconds, playing || Preview);
    }
    public void DrawCameraPath(bool visible)
    {
        GetNodeOrNull("CameraPath")?.QueueFree(); if (!visible || _state == null) return;
        var lines = new ImmediateMesh(); lines.SurfaceBegin(Mesh.PrimitiveType.Lines);
        var keys = _state.Config.Cameras;
        for (int i = 0; i < keys.Count; i++) {
            lines.SurfaceAddVertex(ShowTimeline.Vector(keys[i].Position)); lines.SurfaceAddVertex(ShowTimeline.Vector(keys[i].Target));
            if (i > 0) { lines.SurfaceAddVertex(ShowTimeline.Vector(keys[i-1].Position)); lines.SurfaceAddVertex(ShowTimeline.Vector(keys[i].Position)); }
        }
        lines.SurfaceEnd(); AddChild(new MeshInstance3D { Name = "CameraPath", Mesh = lines,
            MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = Brand.Accent } });
    }
    private void RestoreScreens() { foreach (var entry in _screens) if (IsInstanceValid(entry.Mesh)) { entry.Mesh.MaterialOverride = entry.Material; entry.Mesh.Layers = entry.Layers; } _screens.Clear(); }
    public override void _ExitTree() { ++_generation; StopShowAudio(); RestoreStageAudio(); RestorePlayerLightSticks(); RestoreBroadcastCamera(); RestorePresentation(); RestoreScreens(); }
}
