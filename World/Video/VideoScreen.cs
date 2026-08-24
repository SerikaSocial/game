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
    private VideoStreamPlayer _player;
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

        _player = new VideoStreamPlayer
        {
            // Rendered off-screen: we pull frames as a texture rather than showing the Control.
            Visible = false,
            Autoplay = false,
            // Route audio to the Cinema bus, which CinemaSpeakers captures and re-plays through
            // positional 3D speakers for surround. The bus exists whenever a cinema is loaded; on
            // a world without one it just falls back to a normal audible bus.
            Bus = SerikaSocial.World.CinemaSpeakers.Bus,
        };
        SerikaSocial.World.CinemaSpeakers.EnsureBus();
        AddChild(_player);
        _player.Finished += () => Finished?.Invoke();

        PaintIdle();
    }

    /// True if this container/codec is something the engine can actually decode right now.
    /// Deliberately conservative: better to fail fast into the toast than to hand an mp4 to a
    /// Theora decoder and stall on a screen full of black.
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
        if (jpg == null || jpg.Length == 0) return;
        var img = new Image();
        if (img.LoadJpgFromBuffer(jpg) != Error.Ok && img.LoadWebpFromBuffer(jpg) != Error.Ok
            && img.LoadPngFromBuffer(jpg) != Error.Ok)
            return;
        _thumbTex = ImageTexture.CreateFromImage(img);
        _mesh.MaterialOverride = ScreenMaterial(_thumbTex, 1.6f);
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
            var stream = new VideoStreamTheora();
            // VideoStreamTheora reads from a file path; the manager has already downloaded it.
            stream.File = localPath;
            _player.Stream = stream;
            _player.Play();

            if (!_player.IsPlaying())
            {
                Failed?.Invoke(url, "decoder rejected the file", $"VideoStreamPlayer did not start: {localPath}");
                return;
            }
            _player.Visible = false; // stays off-screen; we blit its texture ourselves
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

    public override void _Process(double delta)
    {
        if (_player == null || !_player.IsPlaying()) return;
        var tex = _player.GetVideoTexture();
        if (tex != null) _mesh.MaterialOverride = ScreenMaterial(tex, 1.8f);
    }

    private void PaintIdle()
    {
        if (_thumbTex != null) { _mesh.MaterialOverride = ScreenMaterial(_thumbTex, 1.2f); return; }
        _mesh.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.02f, 0.02f, 0.03f),
            EmissionEnabled = true,
            Emission = new Color(0.05f, 0.05f, 0.08f),
            Roughness = 0.2f,
        };
    }

    private static StandardMaterial3D ScreenMaterial(Texture2D tex, float energy) => new()
    {
        AlbedoTexture = tex,
        EmissionEnabled = true,
        EmissionTexture = tex,
        Emission = new Color(1, 1, 1),
        EmissionEnergyMultiplier = energy,
        Roughness = 0.08f,
    };
}
