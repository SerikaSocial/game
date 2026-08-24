using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Serika.Net;

namespace SerikaSocial.World.Video;

/// Owns the per-world video queue and drives whatever `VideoScreen`s the world exposes.
///
/// One manager per world (Main creates it when a world resolves at least one video screen and
/// frees it on world switch). It resolves each queued URL through the API, downloads the chosen
/// track, hands it to the screens, and — when a clip can't play — writes the failure to the
/// error log, raises a toast, and advances. All screens in a world play the same item in
/// lockstep, so a room shares one "channel".
///
/// **Not yet networked.** The queue is client-local: there is no wire message for it, and
/// adding one is a `proto` change (the sacred Rust↔C# contract), which is out of scope here.
/// So today each client has its own queue and its own toast. `QueueChanged`/`Toast` are already
/// shaped so a future relay-backed sync only has to feed them — see [[transport-chat-webrtc]].
public partial class VideoManager : Node
{
    public sealed class Item
    {
        public string Url;
        public string Title;
        public string AddedBy;
        public string ThumbnailUrl;
    }

    private readonly List<VideoScreen> _screens = new();
    private readonly List<Item> _queue = new();
    private ApiClient _api;
    private string _worldName = "world";
    private int _generation; // bumped on every skip/clear so a slow resolve can detect it's stale
    private bool _busy;

    public Item NowPlaying { get; private set; }

    /// Raised whenever the queue or now-playing changes, so the queue panel can redraw.
    public event Action QueueChanged;
    /// (message, seconds) — a corner toast. Main routes this to the in-world HUD.
    public event Action<string, int> Toast;

    public IReadOnlyList<Item> Queue => _queue;
    public bool HasScreens => _screens.Count > 0;

    public void Configure(ApiClient api, string worldName)
    {
        _api = api;
        _worldName = string.IsNullOrEmpty(worldName) ? "world" : worldName;
    }

    public void RegisterScreen(VideoScreen screen)
    {
        if (screen == null) return;
        _screens.Add(screen);
        screen.Failed += OnScreenFailed;
        // Only the first screen drives "finished" — otherwise N screens fire N advances.
        if (_screens.Count == 1) screen.Finished += OnFinished;
    }

    /// Queue a URL. If nothing is playing, it starts immediately.
    public void Enqueue(string url, string addedBy)
    {
        url = (url ?? "").Trim();
        if (string.IsNullOrEmpty(url)) return;
        _queue.Add(new Item { Url = url, Title = ShortLabel(url), AddedBy = addedBy });
        QueueChanged?.Invoke();
        if (NowPlaying == null && !_busy) _ = Advance();
    }

    /// Skip the current clip and play the next.
    public void Skip()
    {
        _generation++;
        foreach (var s in _screens) s.Stop();
        NowPlaying = null;
        _ = Advance();
    }

    public void Remove(int index)
    {
        if (index < 0 || index >= _queue.Count) return;
        _queue.RemoveAt(index);
        QueueChanged?.Invoke();
    }

    public void Clear()
    {
        _generation++;
        _queue.Clear();
        foreach (var s in _screens) s.Stop();
        NowPlaying = null;
        QueueChanged?.Invoke();
    }

    private void OnFinished() => Skip();

    private void OnScreenFailed(string url, string reason, string detail)
    {
        VideoErrorLog.Record(url, _worldName, reason, detail);
        // Exactly the message the design asked for, shown in the corner.
        Toast?.Invoke("Video failed to load, loading next", 3);
        // Guard against a double-advance if several screens fail on the same item.
        if (NowPlaying != null && NowPlaying.Url == url)
        {
            NowPlaying = null;
            _ = Advance();
        }
    }

    /// Pull the head of the queue, resolve it, download the best decodable track, and play.
    private async Task Advance()
    {
        if (_busy) return;
        if (_queue.Count == 0) { NowPlaying = null; QueueChanged?.Invoke(); return; }
        if (_api == null)
        {
            Toast?.Invoke("Not connected — can't resolve video", 3);
            return;
        }

        _busy = true;
        int gen = _generation;
        var item = _queue[0];
        _queue.RemoveAt(0);
        NowPlaying = item;
        QueueChanged?.Invoke();

        try
        {
            var resolved = await _api.ResolveVideoAsync(item.Url);
            if (gen != _generation) return; // skipped/cleared while we were resolving

            item.Title = resolved.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() : item.Title;
            item.ThumbnailUrl = resolved.TryGetProperty("thumbnail", out var th) && th.ValueKind == JsonValueKind.String
                ? th.GetString() : null;
            QueueChanged?.Invoke();

            // Fetch a thumbnail so the screen isn't black while resolving/if playback fails.
            if (!string.IsNullOrEmpty(item.ThumbnailUrl))
            {
                var jpg = await _api.GetImageBytesAsync(item.ThumbnailUrl);
                if (gen == _generation && jpg != null)
                    foreach (var s in _screens) s.ShowThumbnail(jpg);
            }

            var track = PickDecodableTrack(resolved);
            if (track == null)
            {
                // No natively decodable track (engine has Theora only; YouTube gives mp4/webm).
                // 1. Try server-side transcode proxy (pipes streams through ffmpeg to deliver ogv/Theora).
                string tcPath = await DownloadTranscode(item.Url, gen);
                if (gen != _generation) return;

                // 2. If server transcode is unavailable or returned no data, try local transcode fallback.
                if (string.IsNullOrEmpty(tcPath))
                {
                    tcPath = await TryLocalTranscodeAsync(item.Url, gen);
                    if (gen != _generation) return;
                }

                if (tcPath == null)
                {
                    OnScreenFailed(item.Url, "transcode failed",
                        "server transcode endpoint returned no data or ffmpeg missing");
                    return;
                }
                foreach (var s in _screens) s.Play(item.Url, tcPath, "ogv", "theora");
                Toast?.Invoke($"▶ {item.Title}", 3);
                return;
            }

            string localPath = await Download(track.Value.url, gen);
            if (gen != _generation) return;
            if (localPath == null)
            {
                OnScreenFailed(item.Url, "download failed", track.Value.url);
                return;
            }

            foreach (var s in _screens) s.Play(item.Url, localPath, track.Value.container, track.Value.vcodec);
            Toast?.Invoke($"▶ {item.Title}", 3);
        }
        catch (Exception e)
        {
            if (gen == _generation) OnScreenFailed(item.Url, "resolve failed", e.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    /// Choose the first track the engine can actually decode. Tracks come best-first, but "best"
    /// is highest resolution, not most-decodable — so we scan for a Theora/ogv one specifically.
    private static (string url, string container, string vcodec)? PickDecodableTrack(JsonElement resolved)
    {
        if (!resolved.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var tr in tracks.EnumerateArray())
        {
            string container = Str(tr, "container");
            string vcodec = Str(tr, "vcodec");
            if (VideoScreen.CanDecode(container, vcodec))
                return (Str(tr, "url"), container, vcodec);
        }
        return null;
    }

    private async Task<string> Download(string url, int gen)
    {
        try
        {
            DirAccess.MakeDirRecursiveAbsolute("user://video-cache");
            // One slot, overwritten each time — worlds stream one clip at a time and these files
            // are large; keeping a history would balloon user:// with no benefit.
            string abs = ProjectSettings.GlobalizePath("user://video-cache/current.ogv");
            bool ok = await _api.DownloadToAsync(url, abs);
            return ok && gen == _generation ? "user://video-cache/current.ogv" : null;
        }
        catch { return null; }
    }

    /// Download a transcoded ogv stream from the server's /v1/video/transcode endpoint.
    /// Used when no natively decodable track is available (e.g. YouTube mp4/webm).
    private async Task<string> DownloadTranscode(string url, int gen)
    {
        try
        {
            DirAccess.MakeDirRecursiveAbsolute("user://video-cache");
            string abs = ProjectSettings.GlobalizePath("user://video-cache/current.ogv");
            if (System.IO.File.Exists(abs)) System.IO.File.Delete(abs);

            string tcUrl = $"{_api.BaseUrl}/v1/video/transcode?url={Uri.EscapeDataString(url)}";
            bool ok = await _api.DownloadToAsync(tcUrl, abs);
            if (ok && System.IO.File.Exists(abs) && new System.IO.FileInfo(abs).Length > 1024)
                return gen == _generation ? "user://video-cache/current.ogv" : null;
        }
        catch { }
        return null;
    }

    /// Run a local client-side transcode fallback using yt-dlp + ffmpeg if available on desktop.
    private async Task<string> TryLocalTranscodeAsync(string url, int gen)
    {
        return await Task.Run(() =>
        {
            try
            {
                string ytdlpPath = FindBinary("yt-dlp");
                string ffmpegPath = FindBinary("ffmpeg");
                if (string.IsNullOrEmpty(ytdlpPath) || string.IsNullOrEmpty(ffmpegPath)) return null;

                DirAccess.MakeDirRecursiveAbsolute("user://video-cache");
                string absOgv = ProjectSettings.GlobalizePath("user://video-cache/current.ogv");
                string absTemp = ProjectSettings.GlobalizePath("user://video-cache/temp_raw.mp4");

                if (System.IO.File.Exists(absTemp)) System.IO.File.Delete(absTemp);
                if (System.IO.File.Exists(absOgv)) System.IO.File.Delete(absOgv);

                // Step 1: yt-dlp download up to 720p with forced mp4 merge
                var ytPsi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ytdlpPath,
                    Arguments = $"--no-warnings --no-playlist -f \"bestvideo[height<=720]+bestaudio/best[height<=720]/best\" --merge-output-format mp4 -o \"{absTemp}\" \"{url}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var pYt = System.Diagnostics.Process.Start(ytPsi))
                {
                    if (pYt == null) return null;
                    if (!pYt.WaitForExit(90_000)) { pYt.Kill(); return null; }
                }

                // Check for output file (handling possible container extensions)
                string inputToFf = absTemp;
                if (!System.IO.File.Exists(inputToFf))
                {
                    if (System.IO.File.Exists(absTemp + ".webm")) inputToFf = absTemp + ".webm";
                    else if (System.IO.File.Exists(absTemp + ".mkv")) inputToFf = absTemp + ".mkv";
                    else if (System.IO.File.Exists(absTemp + ".mp4")) inputToFf = absTemp + ".mp4";
                    else return null;
                }

                // Step 2: ffmpeg transcode to Theora/Vorbis OGV
                var ffPsi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-y -i \"{inputToFf}\" -c:v libtheora -q:v 5 -c:a libvorbis -q:a 3 -shortest \"{absOgv}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var pFf = System.Diagnostics.Process.Start(ffPsi))
                {
                    if (pFf == null) return null;
                    if (!pFf.WaitForExit(90_000)) { pFf.Kill(); return null; }
                    if (pFf.ExitCode != 0 || !System.IO.File.Exists(absOgv)) return null;
                }

                try { if (System.IO.File.Exists(inputToFf)) System.IO.File.Delete(inputToFf); } catch { }

                return gen == _generation && System.IO.File.Exists(absOgv) && new System.IO.FileInfo(absOgv).Length > 1024
                    ? "user://video-cache/current.ogv"
                    : null;
            }
            catch { return null; }
        });
    }

    private static string FindBinary(string name)
    {
        string home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        string[] candidates = {
            $"{home}/.local/bin/{name}",
            $"/usr/local/bin/{name}",
            $"/usr/bin/{name}",
            $"{home}/bin/{name}",
            name,
        };
        foreach (var c in candidates)
        {
            try
            {
                if (System.IO.File.Exists(c)) return c;
            }
            catch { }
        }
        return name;
    }

    private static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// A readable label from a URL before the resolver gives us a real title.
    private static string ShortLabel(string url)
    {
        try
        {
            var u = new Uri(url);
            string host = u.Host.Replace("www.", "");
            return $"{host}{u.AbsolutePath}".TrimEnd('/');
        }
        catch { return url; }
    }
}
