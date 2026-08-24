using System;
using Godot;
using SerikaSocial.Player;

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

    /// Raised when playback fails or the format can't be decoded. (url, humanReason, logDetail).
    public event Action<string, string, string> Failed;
    /// Raised when the current clip plays to its end, so the manager can advance.
    public event Action Finished;
    /// Raised when a player interacts with the screen in-world to open the queue.
    public static event Action InteractionRequested;

    /// Group every screen joins, so the per-world VideoManager can find them all after a world
    /// builds without the builders having to hand back a list.
    public const string Group = "serika_video_screen";

    public string PromptText => "Video Queue";
    public float Range => 10f;
    public Vector3 FocusPoint => _mesh?.GlobalPosition ?? Vector3.Zero;
    public bool CanInteract => true;

    public void Interact(LocalPlayer player)
    {
        InteractionRequested?.Invoke();
    }

    public VideoScreen(MeshInstance3D mesh) => _mesh = mesh;

    public override void _Ready()
    {
        AddToGroup(Group);
        AddToGroup(Interactable.Group);

        SerikaSocial.World.CinemaSpeakers.EnsureBus();

        // SubViewport renders the VideoStreamPlayer directly into a ViewportTexture
        _subViewport = new SubViewport
        {
            Name = "VideoViewport",
            Size = new Vector2I(1280, 720),
            TransparentBg = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
        };
        AddChild(_subViewport);

        _player = new VideoStreamPlayer
        {
            Name = "Player",
            Position = Vector2.Zero,
            Size = new Vector2(1280, 720),
            CustomMinimumSize = new Vector2(1280, 720),
            Expand = true,
            Visible = true,
            Autoplay = false,
            // Route audio to the Cinema bus, which CinemaSpeakers captures and re-plays through
            // positional 3D speakers for surround.
            Bus = SerikaSocial.World.CinemaSpeakers.Bus,
        };
        _subViewport.AddChild(_player);
        _player.Finished += () => Finished?.Invoke();

        var vpTex = _subViewport.GetTexture();
        _screenMat = new StandardMaterial3D
        {
            AlbedoTexture = vpTex,
            EmissionEnabled = true,
            EmissionTexture = vpTex,
            Emission = new Color(1, 1, 1),
            EmissionEnergyMultiplier = 1.8f,
            Roughness = 0.08f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };

        PaintIdle();
    }

    public override void _Process(double delta)
    {
        if (_player != null && _player.IsPlaying())
        {
            var vidTex = _player.GetVideoTexture();
            if (vidTex != null && _screenMat != null && _screenMat.AlbedoTexture != vidTex)
            {
                _screenMat.AlbedoTexture = vidTex;
                _screenMat.EmissionTexture = vidTex;
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
        if (jpg == null || jpg.Length < 4 || (_player != null && _player.IsPlaying())) return;
        var img = LoadImageSafely(jpg);
        if (img == null) return;
        _thumbTex = ImageTexture.CreateFromImage(img);
        if (_player == null || !_player.IsPlaying())
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

        try
        {
            string path = localPath;
            if (!FileAccess.FileExists(path))
            {
                string global = ProjectSettings.GlobalizePath(path);
                if (System.IO.File.Exists(global)) path = global;
                else
                {
                    Failed?.Invoke(url, "file missing", $"Downloaded file not found: {localPath}");
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

            if (_mesh != null)
            {
                _mesh.MaterialOverride = _screenMat;
            }
        }
        catch (Exception e)
        {
            Failed?.Invoke(url, "playback error", e.Message);
        }
    }

    public void Stop()
    {
        _player?.Stop();
        PaintIdle();
    }

    private void PaintIdle()
    {
        if (_mesh == null) return;
        if (_thumbTex != null)
        {
            _mesh.MaterialOverride = new StandardMaterial3D
            {
                AlbedoTexture = _thumbTex,
                EmissionEnabled = true,
                EmissionTexture = _thumbTex,
                Emission = new Color(1, 1, 1),
                EmissionEnergyMultiplier = 1.2f,
                Roughness = 0.08f,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
            return;
        }
        _mesh.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.02f, 0.02f, 0.03f),
            EmissionEnabled = true,
            Emission = new Color(0.05f, 0.05f, 0.08f),
            Roughness = 0.2f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
    }
}
