using System;
using Godot;
using SerikaSocial.Player;
using SerikaSocial.UI;

namespace SerikaSocial.World.Video;

/// A video surface in a world. Replaces the old `YouTubeScreen`, which only ever painted a
/// still thumbnail onto a mesh — this one actually plays, for the formats Godot can decode,
/// and reports every failure so the manager can log it, toast the room, and skip to the next.
///
/// **Honest decode limit.** Stock Godot 4 only ships an Ogg Theora decoder
/// (`VideoStreamTheora`). It has no mp4/webm/HLS path without a native GDExtension, and there
/// are no Quest/macOS builds of one in this project. So YouTube (which resolves to mp4/webm)
/// cannot decode in-engine today: those items fail fast, land in the error log, raise the
/// "failed to load, loading next" toast, and advance the queue. Direct `.ogv` links play now,
/// and the whole pipeline is ready the day a decoder GDExtension is added — only `CanDecode`
/// and the stream construction below would change.
public partial class VideoScreen : Node, IInteractable
{
    private readonly MeshInstance3D _mesh;
    private SubViewport _subViewport;
    private VideoStreamPlayer _player;
    private StandardMaterial3D _screenMat;
    private ImageTexture _thumbTex;

    // Segment playlist. A transcoded clip arrives as a sequence of short .ogv files rather
    // than one big one, so playback can start on segment 0 while ffmpeg is still encoding the
    // rest. `_segIndex` is the segment currently in the player; -1 means nothing started.
    private readonly System.Collections.Generic.List<string> _segments = new();
    private int _segIndex = -1;
    private bool _playlistComplete;
    private string _playlistUrl;
    /// True when we ran out of segments but the encoder is still producing them — the clip is
    /// not over, we are just waiting. Resumed by the next AppendSegment.
    private bool _starved;

    /// Raised when playback fails or the format can't be decoded. (url, humanReason, logDetail).
    public event Action<string, string, string> Failed;
    /// Raised when the current clip plays to its end, so the manager can advance.
    public event Action Finished;
    /// Raised when a player interacts with the screen in-world to open the queue.
    public static event Action InteractionRequested;

    /// Group every screen joins, so the per-world VideoManager can find them all after a world
    /// builds without the builders having to hand back a list.
    public const string Group = "serika_video_screen";

    /// Event watch-parties own the screen; the public queue and interact prompt stay off.
    public static bool AllowPlayerControls = true;

    public string PromptText => AllowPlayerControls ? "Video Queue" : "";
    public float Range => 10f;
    public Vector3 FocusPoint => _mesh?.GlobalPosition ?? Vector3.Zero;
    public bool CanInteract => AllowPlayerControls;

    public void Interact(in InteractionContext ctx)
    {
        InteractionRequested?.Invoke();
    }

    public VideoScreen(MeshInstance3D mesh) => _mesh = mesh;

    public override void _Ready()
    {
        AddToGroup(Group);
        AddToGroup(Interactable.Group);

        SerikaSocial.World.CinemaSpeakers.EnsureBus();

        // VideoStreamPlayer decodes video and exposes frames via GetVideoTexture().
        // It must live inside a viewport to process — a SubViewport keeps it off-screen.
        //
        // Update mode starts Disabled and is only flipped to Always while a clip is actually
        // playing. Leaving it on Always costs a full re-render of this target every frame for
        // the entire life of the world, even though a screen is idle almost all of the time.
        // Sized to the graphics-tier transcode: High 1080p, Medium 720p, Low 480p.
        _subViewport = new SubViewport
        {
            Name = "VideoViewport",
            Size = TargetSize,
            TransparentBg = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            RenderTargetClearMode = SubViewport.ClearMode.Once,
        };
        AddChild(_subViewport);

        _player = new VideoStreamPlayer
        {
            Name = "Player",
            AnchorsPreset = (int)Control.LayoutPreset.FullRect,
            Expand = true,
            Visible = true,
            Autoplay = false,
            Bus = SerikaSocial.World.CinemaSpeakers.Bus,
        };
        _subViewport.AddChild(_player);
        _player.Finished += OnPlayerFinished;

        // Pre-create the screen material; the video texture is plugged in during _Process
        // once the decoder starts producing frames.
        //
        // Unshaded, and albedo only. The previous version bound the frame to *both*
        // AlbedoTexture and EmissionTexture with the default additive emission operator, so
        // the lit albedo and the emission were summed: any frame brighter than about 0.5
        // exceeded 1.0 and clipped to flat white. A title card at 0.85 came out as a blank
        // white rectangle while the audio played fine. A screen should show exactly the
        // decoded image and not be relit by the room, which is what Unshaded gives.
        _screenMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };

        PaintIdle();
    }

    public override void _Process(double delta)
    {
        if (!Alive || !_player.IsPlaying()) return;
        if (_seekToSec > 0)
        {
            try { _player.StreamPosition = _seekToSec; } catch { }
            _seekToSec = -1;
        }
        var vidTex = _player.GetVideoTexture();
        if (vidTex == null) return;
        if (_screenMat.AlbedoTexture != vidTex)
        {
            // Albedo only — see the material setup in _Ready for why emission is not bound.
            _screenMat.AlbedoTexture = vidTex;
            if (_mesh != null && _mesh.MaterialOverride != _screenMat)
            {
                _mesh.MaterialOverride = _screenMat;
            }
        }
    }

    /// True if this container/codec is something the engine can actually decode right now.
    public static bool CanDecode(string container, string vcodec)
    {
        string c = (container ?? "").ToLowerInvariant();
        string v = (vcodec ?? "").ToLowerInvariant();
        return c is "ogv" or "ogg" or "ogx" || v.Contains("theora");
    }

    /// Show the resolved thumbnail while we wait, or when a clip can't play — a dark screen with
    /// a still is a better idle state than a void.
    public void ShowThumbnail(byte[] jpg)
    {
        if (!Alive || jpg == null || jpg.Length < 4 || _player.IsPlaying()) return;
        var img = LoadImageSafely(jpg);
        if (img == null) return;
        _thumbTex = ImageTexture.CreateFromImage(img);
        if (!_player.IsPlaying())
        {
            PaintIdle();
        }
    }

    private static Image LoadImageSafely(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 4) return null;
        var img = new Image();
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            if (img.LoadJpgFromBuffer(bytes) == Error.Ok) return img;
        }
        else if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            if (img.LoadPngFromBuffer(bytes) == Error.Ok) return img;
        }
        else if (bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
                 && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        {
            if (img.LoadWebpFromBuffer(bytes) == Error.Ok) return img;
        }
        else
        {
            if (img.LoadPngFromBuffer(bytes) == Error.Ok) return img;
            if (img.LoadWebpFromBuffer(bytes) == Error.Ok) return img;
            if (img.LoadJpgFromBuffer(bytes) == Error.Ok) return img;
        }
        return null;
    }

    /// Play a resolved track. `localPath` is a downloaded file under user://; `container`/`vcodec`
    /// come from the resolver. Fails (via the event) rather than throwing.
    public void Play(string url, string localPath, string container, string vcodec)
    {
        if (!CanDecode(container, vcodec))
        {
            Failed?.Invoke(url, "unsupported format",
                $"no in-engine decoder for container={container} vcodec={vcodec}");
            return;
        }

        // A single self-contained file is just a one-entry playlist that is already closed.
        BeginPlaylist(url);
        _playlistComplete = true;
        AppendSegment(localPath);
    }

    /// Begin a segmented clip. Segments are fed in afterwards by `AppendSegment` as the encoder
    /// produces them, and `CompletePlaylist` closes the sequence. Playback starts on the first
    /// appended segment, so the wait before the first frame is one segment's encode, not the
    /// whole clip's.
    /// True while this screen's nodes are alive. `_player` is a Godot object: once the screen
    /// is freed the managed wrapper survives but the native side is gone, so a plain null check
    /// is not enough — calling through it throws ObjectDisposedException.
    private bool Alive =>
        GodotObject.IsInstanceValid(this) && !IsQueuedForDeletion() &&
        GodotObject.IsInstanceValid(_player);

    /// Index of the segment currently in the player, or -1 before playback starts. The manager
    /// uses this to measure how far the encoder is ahead of playback and throttle accordingly.
    public int PlayingSegment => _segIndex;

    private static Vector2I TargetSize => DeviceProfile.Current switch
    {
        DeviceProfile.Tier.High => new Vector2I(1920, 1080),
        DeviceProfile.Tier.Medium => new Vector2I(1280, 720),
        _ => new Vector2I(854, 480),
    };

    public void BeginPlaylist(string url)
    {
        if (!Alive) return;
        if (_subViewport != null) _subViewport.Size = TargetSize;
        _segments.Clear();
        _segIndex = -1;
        _playlistComplete = false;
        _starved = false;
        _playlistUrl = url;
        _player.Stop();
    }

    /// Hand the screen the next ready segment. Starts playback if this is the first one, or
    /// resumes it if the encoder had fallen behind the player.
    public void AppendSegment(string path)
    {
        if (!Alive || string.IsNullOrEmpty(path)) return;
        _segments.Add(path);
        // Either we have not started yet, or we ran dry waiting for this.
        if (_segIndex < 0 || _starved) PlayNextSegment();
    }

    /// No more segments are coming. If the player already drained the list, the clip is over.
    public void CompletePlaylist()
    {
        if (!Alive) return;
        _playlistComplete = true;
        if (_starved) { _starved = false; Finished?.Invoke(); }
    }

    private void OnPlayerFinished()
    {
        if (_segIndex >= 0 && _segIndex + 1 < _segments.Count) { PlayNextSegment(); return; }
        // Drained. Either the clip really ended, or the encoder has not caught up yet.
        if (_playlistComplete)
        {
            SetRendering(false);
            Finished?.Invoke();
        }
        else
        {
            // Hold the last frame rather than flashing idle — this is a stall, not an end.
            _starved = true;
        }
    }

    private void PlayNextSegment()
    {
        _starved = false;
        _segIndex++;
        if (_segIndex >= _segments.Count) { _segIndex--; _starved = true; return; }
        PlayFile(_playlistUrl, _segments[_segIndex]);
    }

    /// Load one concrete .ogv file into the player. Shared by the playlist path and by direct
    /// single-file playback of an already-Theora source.
    private void PlayFile(string url, string localPath)
    {
        if (!Alive) return;
        try
        {
            string path = localPath;
            if (!FileAccess.FileExists(path))
            {
                string global = ProjectSettings.GlobalizePath(path);
                if (System.IO.File.Exists(global)) path = global;
                else
                {
                    Failed?.Invoke(url, "file missing", $"Segment not found: {localPath}");
                    return;
                }
            }

            var stream = new VideoStreamTheora();
            stream.File = path;
            _player.Stream = stream;
            _player.Play();

            if (!_player.IsPlaying())
            {
                Failed?.Invoke(url, "decoder rejected the file", $"VideoStreamPlayer did not start: {localPath}");
                return;
            }

            SetRendering(true);
            if (_mesh != null) _mesh.MaterialOverride = _screenMat;
        }
        catch (Exception e)
        {
            Failed?.Invoke(url, "playback error", e.Message);
        }
    }

    /// Only spend GPU time on the offscreen target while frames are actually being produced.
    private void SetRendering(bool on)
    {
        if (_subViewport == null) return;
        _subViewport.RenderTargetUpdateMode = on
            ? SubViewport.UpdateMode.Always
            : SubViewport.UpdateMode.Disabled;
        _rendering = on;
    }

    private bool _rendering;

    /// True while frames are actually being decoded onto this screen — not merely "a clip is
    /// queued". This is the flag the house lights watch, so it has to mean "there is a picture
    /// on the wall right now": it goes true on the first segment and stays true across segment
    /// boundaries and encoder stalls, so the room does not strobe between segments.
    public bool IsRendering => _rendering && Alive;

    /// The quad the picture is painted on, so a light can be placed relative to it.
    public MeshInstance3D Surface => _mesh;

    /// The decoder's current frame, or null when nothing is playing. Read by the house lights
    /// to tint the screen bounce; nothing else should need it.
    public Texture2D FrameTexture => Alive && _rendering ? _player.GetVideoTexture() : null;

    private double _seekToSec = -1;

    /// Seek once the decoder is actually running. Theora ignores StreamPosition set before
    /// the first decoded frame, so this is applied from `_Process` on the first playing tick.
    public void SeekWhenReady(double seconds)
    {
        if (seconds < 0.5) return;
        _seekToSec = seconds;
        if (Alive && _player.IsPlaying())
        {
            try { _player.StreamPosition = seconds; _seekToSec = -1; } catch { }
        }
    }

    public void Stop()
    {
        if (!Alive) return;
        _player.Stop();
        _segments.Clear();
        _segIndex = -1;
        _playlistComplete = false;
        _starved = false;
        SetRendering(false);
        PaintIdle();
    }

    private void PaintIdle()
    {
        if (_mesh == null) return;
        if (_thumbTex != null)
        {
            // Dimmed a little so a still reads as "paused/idle" rather than live playback,
            // but unshaded like the video itself so it cannot blow out to white.
            _mesh.MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoTexture = _thumbTex,
                AlbedoColor = new Color(0.65f, 0.65f, 0.70f),
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
            return;
        }
        _mesh.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.03f, 0.025f, 0.05f),
            EmissionEnabled = true,
            Emission = new Color(0.06f, 0.05f, 0.10f),
            Roughness = 0.95f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
    }

    /// Idle state used while a clip is being fetched or transcoded, so the room can tell the
    /// difference between "nothing queued" and "your video is coming". Without this the screen
    /// looks identical during the transcode wait as it does when idle, which reads as broken.
    public void ShowPreparing()
    {
        if (!Alive || _mesh == null || _thumbTex != null) return;
        _mesh.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.10f, 0.07f, 0.18f),
            EmissionEnabled = true,
            Emission = new Color(0.22f, 0.16f, 0.42f),
            EmissionEnergyMultiplier = 1.2f,
            Roughness = 0.9f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
    }
}
