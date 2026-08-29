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
/// Queue sync rides the existing Chat datagram (see <see cref="VideoNet"/>) so a proto
/// change is not required. Late joiners ask for a snapshot; everyone else applies enqueue /
/// skip / clear / play-clock the same way.
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
    private Action<string> _netSend;
    private bool _applyingNet;
    private long _startedUnixMs;
    private string _seekUrl;
    private double _seekToSec = -1;

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

    /// Send path is a lambda so it can read the live transport after a reconnect.
    public void BindNet(Action<string> send) => _netSend = send;

    /// Ask whoever is already in the room for the current queue. Safe to call before anyone
    /// else is connected — the want is dropped if the relay is not up yet.
    public void RequestSync() => Broadcast(VideoNet.Want());

    /// True if <paramref name="text"/> was a video control message (and has been applied).
    /// Main uses this to keep the payload out of the chat overlay.
    public bool TryHandleNet(string text)
    {
        if (!VideoNet.TryParse(text, out var msg)) return false;
        _applyingNet = true;
        try
        {
            switch (msg.Kind)
            {
                case VideoNet.Op.Enqueue:
                    Enqueue(msg.Url, string.IsNullOrEmpty(msg.By) ? "someone" : msg.By, fromNet: true);
                    break;
                case VideoNet.Op.Skip:
                    Skip(fromNet: true);
                    break;
                case VideoNet.Op.Clear:
                    Clear(fromNet: true);
                    break;
                case VideoNet.Op.Play:
                    RememberSeek(msg.Url, msg.StartedUnixMs);
                    TrySeekPlaying();
                    break;
                case VideoNet.Op.Want:
                    ReplyState();
                    break;
                case VideoNet.Op.State:
                    ApplySnapshot(msg);
                    break;
            }
        }
        finally { _applyingNet = false; }
        return true;
    }

    /// Drop screens whose nodes have gone away. A world switch frees the old world's screens,
    /// and any of the deferred callbacks below can land after that has happened — touching a
    /// freed screen throws ObjectDisposedException from deep inside an async path, where it
    /// surfaces as a bogus "resolve failed" against the user's URL.
    private void PruneScreens() =>
        _screens.RemoveAll(s => !GodotObject.IsInstanceValid(s) || s.IsQueuedForDeletion());

    public void RegisterScreen(VideoScreen screen)
    {
        if (screen == null) return;
        PruneScreens();
        _screens.Add(screen);
        screen.Failed += OnScreenFailed;
        // Only the first screen drives "finished" — otherwise N screens fire N advances.
        if (_screens.Count == 1) screen.Finished += OnFinished;
    }

    /// Queue a URL. If nothing is playing, it starts immediately.
    public void Enqueue(string url, string addedBy) => Enqueue(url, addedBy, fromNet: false);

    public void Enqueue(string url, string addedBy, bool fromNet)
    {
        url = CanonicalMediaUrl((url ?? "").Trim());
        if (string.IsNullOrEmpty(url)) return;
        if (fromNet && AlreadyHas(url)) return;
        _queue.Add(new Item { Url = url, Title = ShortLabel(url), AddedBy = addedBy });
        QueueChanged?.Invoke();
        if (!fromNet) Broadcast(VideoNet.Enqueue(url, addedBy));
        if (NowPlaying == null && !_busy) _ = Advance();
    }

    /// Skip the current clip and play the next.
    public void Skip() => Skip(fromNet: false);

    public void Skip(bool fromNet)
    {
        _generation++;
        KillTranscode();
        PruneScreens();
        foreach (var s in _screens) s.Stop();
        NowPlaying = null;
        _startedUnixMs = 0;
        if (!fromNet) Broadcast(VideoNet.Skip());
        _ = Advance();
    }

    public void Remove(int index)
    {
        if (index < 0 || index >= _queue.Count) return;
        _queue.RemoveAt(index);
        QueueChanged?.Invoke();
    }

    public void Clear() => Clear(fromNet: false);

    public void Clear(bool fromNet)
    {
        _generation++;
        KillTranscode();
        _queue.Clear();
        PruneScreens();
        foreach (var s in _screens) s.Stop();
        NowPlaying = null;
        _startedUnixMs = 0;
        QueueChanged?.Invoke();
        if (!fromNet) Broadcast(VideoNet.Clear());
    }

    private void OnFinished() => Skip(fromNet: false);

    private void OnScreenFailed(string url, string reason, string detail)
    {
        VideoErrorLog.Record(url, _worldName, reason, detail);
        // Also to stdout, not just the file. `VideoErrorLog` writes to user://logs, which on
        // Android lives inside the app's private data dir — and a release APK is not
        // debuggable, so `adb run-as` refuses and that file cannot be read off a Quest at all.
        // Every video failure on the headset was therefore completely invisible: the only
        // evidence was a toast the player had already dismissed. logcat is the one channel that
        // works on every platform, so failures go there too.
        GD.PrintErr($"[VideoManager] FAILED {url}: {reason} — {detail}");
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
        if (_api == null && OS.HasFeature("android"))
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
            // Desktop has yt-dlp + ffmpeg and they work from this machine. The production
            // API's datacenter IP is bot-gated by YouTube, so asking it first is how every
            // clip on this PC used to fail with "Sign in to confirm you're not a bot" and
            // never reach the local path. Quest has no local binaries — it still goes
            // through the API.
            bool localFirst = !OS.HasFeature("android");
            if (localFirst && await TryLocalTranscodeAsync(item, gen)) return;
            if (gen != _generation) return;

            JsonElement resolved = default;
            bool haveResolved = false;
            try
            {
                if (_api != null)
                {
                    resolved = await _api.ResolveVideoAsync(item.Url);
                    haveResolved = true;
                }
            }
            catch (Exception e)
            {
                GD.Print($"[VideoManager] API resolve failed ({e.Message}) — trying local transcode");
            }
            if (gen != _generation) return; // skipped/cleared while we were resolving

            if (haveResolved)
            {
                item.Title = resolved.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() : item.Title;
                item.ThumbnailUrl = resolved.TryGetProperty("thumbnail", out var th) && th.ValueKind == JsonValueKind.String
                    ? th.GetString() : null;
                QueueChanged?.Invoke();

                if (!string.IsNullOrEmpty(item.ThumbnailUrl))
                {
                    var jpg = await _api.GetImageBytesAsync(item.ThumbnailUrl);
                    if (gen == _generation && jpg != null)
                        foreach (var s in _screens) s.ShowThumbnail(jpg);
                }
            }

            var track = haveResolved ? PickDecodableTrack(resolved) : null;
            GD.Print($"[VideoManager] '{item.Url}' resolved={haveResolved} " +
                     $"decodableTrack={(track?.container ?? "none")} localFirst={localFirst}");
            if (track == null)
            {
                // No natively decodable track (engine has Theora only; YouTube gives mp4/webm).
                if (!localFirst && await TryLocalTranscodeAsync(item, gen)) return;
                if (gen != _generation) return;

                // Shared segmented transcode. Preferred over the whole-file fallback below on
                // every platform: it starts playing one segment in instead of one clip in, and
                // a clip someone else already queued costs no encode at all.
                Toast?.Invoke("Preparing video…", 3);
                if (await TryServerSegmentedAsync(item, gen)) return;
                if (gen != _generation) return;

                Toast?.Invoke("Transcoding on the server, this may take a minute…", 4);
                string tcPath = await DownloadTranscode(item.Url, gen);
                if (gen != _generation) return;

                if (tcPath == null)
                {
                    OnScreenFailed(item.Url, "transcode failed",
                        "both local and server transcode failed or tools missing");
                    return;
                }
                foreach (var s in _screens) s.Play(item.Url, tcPath, "ogv", "theora");
                OnPlaybackStarted(item);
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
            OnPlaybackStarted(item);
        }
        catch (Exception e)
        {
            if (gen == _generation) OnScreenFailed(item.Url, "resolve failed", e.Message);
        }
        finally
        {
            _busy = false;
            // A Skip/Clear/Enqueue that arrived while we were busy could not start, because
            // Advance early-returns on `_busy` and nothing re-triggered it — that was the
            // "queue stalls after you skip a still-loading video" bug. Now that we're free,
            // pick up any queued work.
            if (_queue.Count > 0 && NowPlaying == null)
                _ = Advance();
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

    /// Play through the server's *shared segmented* transcode (`/v1/video/session`).
    ///
    /// This is how Quest gets video at all, and it is the primary server path everywhere. The
    /// older whole-file `DownloadTranscode` below could not work on a headset for three reasons
    /// that this fixes together:
    ///
    ///   - Godot's VideoStreamPlayer opens a *path*, it does not stream, so a single-file
    ///     transcode means waiting for the entire clip to encode and download before the first
    ///     frame. Anything of real length also hit the server's 120 s encode watchdog first.
    ///   - The server's concurrency cap is per-ffmpeg, so the second person in the cinema got a
    ///     503. Here everyone attaches to one job, and the cap limits distinct *clips*.
    ///   - Nothing was cached, so N viewers meant N identical encodes. Now the first viewer pays
    ///     and everyone else reads the cache — which is the whole "only one person encodes" point.
    ///
    /// Segments are fed to the screens with the same `BeginPlaylist`/`AppendSegment` playlist the
    /// desktop local-ffmpeg path uses, so playback starts one segment in, not one clip in.
    /// Returns true once playback has begun; the pump keeps running in the background.
    private async Task<bool> TryServerSegmentedAsync(Item item, int gen)
    {
        if (_api == null) return false;

        JsonElement session;
        try
        {
            session = await _api.StartVideoSessionAsync(item.Url);
        }
        catch (Exception e)
        {
            GD.Print($"[VideoManager] server session failed: {e.Message}");
            return false;
        }
        if (gen != _generation) return false;

        string jobId = Str(session, "id");
        if (string.IsNullOrEmpty(jobId))
        {
            // Silence here is how the first on-headset test came back unreadable: this path,
            // and a couple below, returned false with no log, so "video did nothing" could not
            // be told apart from "video was never asked to do anything".
            GD.Print($"[VideoManager] server session returned no job id: {session}");
            return false;
        }
        GD.Print($"[VideoManager] server job accepted: {session}");

        string title = Str(session, "title");
        if (!string.IsNullOrEmpty(title)) { item.Title = title; QueueChanged?.Invoke(); }

        DirAccess.MakeDirRecursiveAbsolute("user://video-cache/segments");
        string absDir = ProjectSettings.GlobalizePath("user://video-cache/segments");
        foreach (var stale in System.IO.Directory.GetFiles(absDir, "seg_*.ogv"))
            try { System.IO.File.Delete(stale); } catch { /* in use; harmless */ }

        var firstSegment = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _ = PumpServerSegmentsAsync(jobId, absDir, gen, item, firstSegment);
        return await firstSegment.Task;
    }

    /// Poll the job and download segments as they become available, feeding each to the screens.
    private async Task PumpServerSegmentsAsync(string jobId, string absDir, int gen, Item item,
                                               TaskCompletionSource<bool> firstSegment)
    {
        int next = 0;
        bool startedAny = false;
        bool done = false;
        int idleTicks = 0;

        try
        {
            while (gen == _generation)
            {
                int available;
                bool jobDone;
                try
                {
                    var s = await _api.StartVideoSessionAsync(item.Url);
                    available = s.TryGetProperty("segments", out var sg) && sg.TryGetInt32(out int v) ? v : 0;
                    jobDone = s.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.True;
                }
                catch (Exception e)
                {
                    GD.Print($"[VideoManager] session poll failed: {e.Message}");
                    break;
                }
                if (gen != _generation) return;

                // Throttle the same way the local encoder is throttled: stay a few segments
                // ahead of the playhead and no more, so a long film does not download in full
                // onto a headset with a few gigabytes of free space.
                PruneScreens();
                var primary = _screens.Count > 0 ? _screens[0] : null;
                int playing = primary != null && GodotObject.IsInstanceValid(primary)
                    ? primary.PlayingSegment : -1;
                int buffered = next - 1 - playing;

                if (buffered < HighWaterSegments && next < available)
                {
                    string abs = System.IO.Path.Combine(absDir, $"seg_{next:D4}.ogv");
                    if (await _api.DownloadToAsync(_api.VideoSegmentUrl(jobId, next), abs))
                    {
                        if (gen != _generation) return;
                        string userPath = $"user://video-cache/segments/seg_{next:D4}.ogv";
                        Callable.From(() =>
                        {
                            PruneScreens();
                            foreach (var s in _screens) s.AppendSegment(userPath);
                        }).CallDeferred();
                        next++;
                        idleTicks = 0;
                        if (!startedAny)
                        {
                            startedAny = true;
                            GD.Print($"[VideoManager] server job {jobId}: playing from segment 0");
                            firstSegment.TrySetResult(true);
                            OnPlaybackStarted(item);
                        }
                        CleanupPlayedSegments(absDir, playing);
                        continue; // fetch the next one immediately rather than sleeping
                    }
                    GD.Print($"[VideoManager] segment {next} download failed");
                }

                if (jobDone && next >= available) { done = true; break; }

                // Nothing to do: either the buffer is full or the encoder has not caught up.
                await Task.Delay(1000);
                // A job that is neither finished nor producing is wedged. Give up rather than
                // poll a dead encode forever and leave the queue stuck on this item.
                if (!jobDone && next >= available)
                {
                    // Waiting on the encoder is the expected state for the first few seconds
                    // and a bug after a minute, and those must not look the same in a log.
                    if (++idleTicks % 10 == 0)
                        GD.Print($"[VideoManager] server job {jobId}: waiting on encoder " +
                                 $"({idleTicks}s, {available} segment(s) ready, fetched {next})");
                    if (idleTicks > 90)
                    {
                        GD.PrintErr($"[VideoManager] server job {jobId}: gave up after {idleTicks}s " +
                                    $"with {available} segment(s) available");
                        break;
                    }
                }
            }
        }
        finally
        {
            if (gen == _generation)
            {
                Callable.From(() =>
                {
                    PruneScreens();
                    foreach (var s in _screens) s.CompletePlaylist();
                }).CallDeferred();
                if (!startedAny)
                {
                    OnScreenFailed(item.Url, "server transcode produced nothing",
                                   $"job {jobId}, {next} segment(s), done={done}");
                }
            }
            firstSegment.TrySetResult(startedAny);
        }
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

    /// How long each transcoded chunk is. This is the dominant term in time-to-first-frame, so
    /// it is deliberately short — long enough that the per-segment encoder startup cost stays
    /// amortised, short enough that the screen lights up almost immediately.
    private const int SegmentSeconds = 6;
    /// Vertical cap for the transcode. Theora is a slow, single-threaded, dated encoder and this
    /// is a texture on a wall viewed from across a room — 480p roughly halves the encode cost
    /// versus 720p for detail nobody can see from a seat. Measured ~2.6x realtime at 480p vs
    /// ~1.1x at 720p on this box, i.e. the difference between "builds a buffer" and "can't keep
    /// up".
    private const int MaxHeight = 480;
    /// How many encoding threads ffmpeg may use. Capped so the encoder cannot claim every core
    /// and starve the game's render thread — the single most common cause of the "video makes
    /// the game lag" reports.
    private const int EncodeThreads = 2;
    /// Flow-control buffer, in segments. The encoder is allowed to run this far ahead of
    /// playback and is then paused until the buffer drains to `LowWaterSegments`. This bounds
    /// CPU to "keep a few seconds of buffer" regardless of clip length, instead of transcoding
    /// a 10-minute video flat-out the moment it is queued.
    private const int HighWaterSegments = 4;
    private const int LowWaterSegments = 2;

    private System.Diagnostics.Process _transcode;
    private LoopbackMediaProxy _proxy;

    /// Transcode locally with ffmpeg, emitting short .ogv segments and starting playback on the
    /// first one. Returns true once playback has begun (the encoder keeps running in the
    /// background and segments are streamed to the screens as they land); false if the local
    /// tools are missing or the encoder never produced anything, so the caller can fall back.
    ///
    /// The old implementation downloaded the entire clip with yt-dlp and *then* transcoded the
    /// whole thing before showing a single frame — two full serial passes over the media, which
    /// on any normal-length video is minutes of black screen. Here yt-dlp is only used to
    /// resolve direct CDN URLs (a sub-second metadata call) and ffmpeg reads and encodes them
    /// as a stream.
    private async Task<bool> TryLocalTranscodeAsync(Item item, int gen)
    {
        // Off-thread: the first call extracts a ~100 MB ffmpeg.exe out of the pck. `async` alone
        // would not have saved us — everything before the first `await` runs synchronously on
        // the caller, which is the main thread.
        string ffmpegPath = await Task.Run(() => FindBinary("ffmpeg"));
        if (ffmpegPath == null) return false;

        var (videoUrl, audioUrl) = await ResolveDirectStreamsAsync(item.Url);
        if (gen != _generation) return false;
        if (string.IsNullOrEmpty(videoUrl)) return false;

        // ffmpeg is static-linked and segfaults on any hostname (see LoopbackMediaProxy), so
        // it is only ever pointed at 127.0.0.1 and this relay fetches the real thing.
        var targets = new List<string> { videoUrl };
        if (!string.IsNullOrEmpty(audioUrl)) targets.Add(audioUrl);
        var proxy = LoopbackMediaProxy.Start(targets);
        if (proxy == null) return false;

        string absDir;
        try
        {
            DirAccess.MakeDirRecursiveAbsolute("user://video-cache/segments");
            absDir = ProjectSettings.GlobalizePath("user://video-cache/segments");
            foreach (var f in System.IO.Directory.GetFiles(absDir, "seg_*.ogv"))
            {
                try { System.IO.File.Delete(f); } catch { }
            }
            _cleanupCursor = 0; // fresh clip — segment numbering restarts at 0
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[VideoManager] segment dir prep failed: {ex.Message}");
            proxy.Dispose();
            return false;
        }

        // `-map` pins video from input 0 and audio from input 1 when yt-dlp gave us split
        // streams; with a single muxed input the second -i and the maps are simply omitted.
        // YouTube no longer offers progressive formats for most videos, so the two-input path
        // is the common one.
        string inputs = targets.Count > 1
            ? $"-i \"{proxy.UrlFor(0)}\" -i \"{proxy.UrlFor(1)}\" -map 0:v:0 -map 1:a:0"
            : $"-i \"{proxy.UrlFor(0)}\"";
        string pattern = System.IO.Path.Combine(absDir, "seg_%04d.ogv");
        string args =
            $"-y -loglevel error -threads {EncodeThreads} {inputs} " +
            // Even dimensions are required by yuv420p; the min() keeps portrait/short sources
            // from being upscaled.
            $"-vf \"scale=-2:min({MaxHeight}\\,ih)\" -pix_fmt yuv420p " +
            $"-c:v libtheora -q:v 5 -threads {EncodeThreads} -c:a libvorbis -q:a 4 " +
            $"-f segment -segment_time {SegmentSeconds} -segment_format ogg -reset_timestamps 1 " +
            $"\"{pattern}\"";

        KillTranscode();
        try
        {
            _transcode = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            });
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[VideoManager] ffmpeg start failed: {ex.Message}");
            proxy.Dispose();
            return false;
        }
        if (_transcode == null) { proxy.Dispose(); return false; }

        // Run the encoder below the game so it can never win a CPU fight against the render
        // thread. Lowering priority never needs privilege; raising it would, hence one-way.
        try { _transcode.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; }
        catch { /* not supported everywhere; the thread cap still applies */ }

        // The relay must outlive the encoder, which streams from it for the whole clip.
        _proxy = proxy;

        PruneScreens();
        foreach (var s in _screens) { s.BeginPlaylist(item.Url); s.ShowPreparing(); }
        Toast?.Invoke($"Preparing {item.Title}…", 3);

        // The pump runs for the whole length of the clip, so it must not be awaited here —
        // `Advance` holds `_busy` until this returns, and holding it for the clip's duration
        // would deadlock every later skip. Await only the first segment, then let it run.
        var firstSegment = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _ = PumpSegmentsAsync(absDir, gen, item, firstSegment);
        return await firstSegment.Task;
    }

    /// Watch the segment directory and hand each finished segment to the screens. A segment is
    /// only safe to play once the *next* one exists (ffmpeg is still appending to the newest
    /// file) or the encoder has exited. Resolves `firstSegment` as soon as playback can start,
    /// then keeps feeding segments until the encoder finishes.
    private async Task PumpSegmentsAsync(string absDir, int gen, Item item,
                                         TaskCompletionSource<bool> firstSegment)
    {
        var proc = _transcode;
        var proxy = _proxy;
        var primary = _screens.Count > 0 ? _screens[0] : null; // lockstep, so one tracks playback
        int next = 0;
        bool startedAny = false;

        while (true)
        {
            if (gen != _generation) // skipped/cleared — stop feeding screens
            {
                firstSegment.TrySetResult(startedAny);
                return;
            }

            bool exited;
            try { exited = proc == null || proc.HasExited; } catch { exited = true; }

            // Flow control: hold the encoder to a few segments ahead of playback. `playing` is
            // the segment on screen now; `next - 1 - playing` is how many are buffered ahead.
            // Pausing the proxy stalls ffmpeg's input, which is what actually stops the CPU
            // burn — see LoopbackMediaProxy. Deleting played segments keeps the cache bounded
            // no matter how long the clip is.
            int playing = primary != null && GodotObject.IsInstanceValid(primary)
                ? primary.PlayingSegment : -1;
            int buffered = next - 1 - Math.Max(0, playing);
            if (buffered >= HighWaterSegments) proxy?.Pause();
            else if (buffered <= LowWaterSegments) proxy?.Resume();
            CleanupPlayedSegments(absDir, playing);

            string cur = System.IO.Path.Combine(absDir, $"seg_{next:D4}.ogv");
            string following = System.IO.Path.Combine(absDir, $"seg_{next + 1:D4}.ogv");

            bool curReady = System.IO.File.Exists(cur) &&
                            (System.IO.File.Exists(following) || exited) &&
                            new System.IO.FileInfo(cur).Length > 1024;

            if (curReady)
            {
                string userPath = $"user://video-cache/segments/seg_{next:D4}.ogv";
                // Screens are Nodes — touch them on the main thread only.
                Callable.From(() => { PruneScreens(); foreach (var s in _screens) s.AppendSegment(userPath); })
                        .CallDeferred();
                startedAny = true;
                next++;
                // Playback has begun — release the caller so the queue stops being blocked.
                if (next == 1)
                {
                    Callable.From(() => OnPlaybackStarted(item)).CallDeferred();
                    firstSegment.TrySetResult(true);
                }
                // Yield rather than tight-looping when a burst of segments is already on disk.
                await Task.Delay(20);
                continue;
            }

            if (exited)
            {
                // Encoder is done and no further segment materialised: the clip is complete.
                if (startedAny)
                {
                    Callable.From(() => { PruneScreens(); foreach (var s in _screens) s.CompletePlaylist(); })
                            .CallDeferred();
                }
                else
                {
                    string err = "";
                    try { err = proc != null ? await proc.StandardError.ReadToEndAsync() : ""; } catch { }
                    GD.PrintErr($"[VideoManager] ffmpeg produced no segments: {err}");
                }
                // Encoding is over, so nothing needs the relay any more. `gen` still matches
                // here, so this is our own proxy and not a newer item's.
                var finished = _proxy;
                _proxy = null;
                finished?.Dispose();

                firstSegment.TrySetResult(startedAny);
                return;
            }

            await Task.Delay(250);
        }
    }

    private int _cleanupCursor;

    /// Delete segment files the player has already moved past, so a long clip does not fill
    /// user:// with dozens of .ogv files. Keeps one segment behind the play head as a safety
    /// margin (the decoder may briefly still reference the file it is leaving).
    private void CleanupPlayedSegments(string absDir, int playing)
    {
        int deleteBelow = playing - 1;
        for (; _cleanupCursor < deleteBelow; _cleanupCursor++)
        {
            try
            {
                string f = System.IO.Path.Combine(absDir, $"seg_{_cleanupCursor:D4}.ogv");
                if (System.IO.File.Exists(f)) System.IO.File.Delete(f);
            }
            catch { /* a locked file will be retried next clip via the dir wipe on start */ }
        }
    }

    /// Ask yt-dlp for direct CDN URLs without downloading. Returns (video, audio); audio is null
    /// when the chosen format is already muxed. A URL that is plainly a media file is passed
    /// straight through, so yt-dlp is not needed at all for direct links.
    private async Task<(string video, string audio)> ResolveDirectStreamsAsync(string url)
    {
        if (LooksLikeDirectMedia(url)) return (url, null);

        string ytdlpPath = await Task.Run(() => FindBinary("yt-dlp"));
        if (ytdlpPath == null) return (null, null);

        try
        {
            var first = await RunYtDlpGetUrls(ytdlpPath, url, cookiesBrowser: null);
            if (first.video != null) return first;

            string browser = FindCookieBrowser();
            if (browser != null)
            {
                GD.Print($"[VideoManager] yt-dlp retry with --cookies-from-browser {browser}");
                var second = await RunYtDlpGetUrls(ytdlpPath, url, browser);
                if (second.video != null) return second;
            }
            return (null, null);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[VideoManager] yt-dlp resolve failed: {ex.Message}");
            return (null, null);
        }
    }

    private static bool LooksLikeDirectMedia(string url)
    {
        string path;
        try { path = new Uri(url).AbsolutePath; } catch { return false; }
        foreach (var ext in new[] { ".mp4", ".webm", ".mkv", ".mov", ".ogv", ".m3u8" })
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// Stop any in-flight encode. Without this a skip leaves ffmpeg writing segments for a clip
    /// nobody is watching, which is both wasted CPU and a source of stale files.
    private void KillTranscode()
    {
        var p = _transcode;
        _transcode = null;
        if (p != null)
        {
            try { if (!p.HasExited) p.Kill(true); } catch { }
            try { p.Dispose(); } catch { }
        }

        var proxy = _proxy;
        _proxy = null;
        proxy?.Dispose();
    }

    public override void _ExitTree() => KillTranscode();

    private static bool _binsExtracted;

    /// Extract prepackaged binaries from res://bin/ to user://bin/ on first run.
    /// In exported builds with embed_pck, res:// files are inside the pck and can't be
    /// executed directly, so we copy them to the writable user:// directory.
    private static readonly object _binsLock = new object();

    private static void EnsureBundledBinsExtracted()
    {
        // Now reachable from several worker threads at once (ffmpeg and yt-dlp lookups race),
        // and a torn half-written binary is far worse than a moment of contention.
        lock (_binsLock)
        {
            if (_binsExtracted) return;
            _binsExtracted = true;
            ExtractBundledBins();
        }
    }

    private static void ExtractBundledBins()
    {
        try
        {
            string userBin = ProjectSettings.GlobalizePath("user://bin");
            System.IO.Directory.CreateDirectory(userBin);

            bool isWindows = OS.GetName() == "Windows";
            string[] names = isWindows
                ? new[] { "ffmpeg.exe", "yt-dlp.exe" }
                : new[] { "ffmpeg", "yt-dlp" };

            foreach (var n in names)
            {
                string resPath = $"res://bin/{n}";
                string userPath = System.IO.Path.Combine(userBin, n);
                if (System.IO.File.Exists(userPath)) continue;
                if (!FileAccess.FileExists(resPath)) continue;

                // Copy from res:// (which may be inside the embedded pck) to user:// in chunks.
                // `GetFileAsBytes` would materialise the whole binary in memory first — that is
                // a ~100 MB spike for ffmpeg.exe on Windows, on the first run, on the main
                // thread. Streaming it keeps the peak flat.
                using (var src = FileAccess.Open(resPath, FileAccess.ModeFlags.Read))
                using (var dst = FileAccess.Open($"user://bin/{n}", FileAccess.ModeFlags.Write))
                {
                    if (src == null || dst == null) continue;
                    const int chunk = 4 * 1024 * 1024;
                    while (!src.EofReached())
                    {
                        var buf = src.GetBuffer(chunk);
                        if (buf == null || buf.Length == 0) break;
                        dst.StoreBuffer(buf);
                    }
                }
                // Make executable on Unix
                if (!isWindows)
                {
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "chmod",
                            Arguments = $"+x \"{userPath}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        };
                        System.Diagnostics.Process.Start(psi)?.WaitForExit(3000);
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[VideoManager] Bin extraction failed: {ex.Message}");
        }
    }

    /// Strip playlist / radio junk so yt-dlp fetches one video, not a mix.
    private static string CanonicalMediaUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        try
        {
            var u = new Uri(url);
            string host = u.Host.ToLowerInvariant();
            if (host.Contains("youtu.be"))
            {
                string id = u.AbsolutePath.Trim('/');
                int slash = id.IndexOf('/');
                if (slash >= 0) id = id[..slash];
                if (id.Length > 0) return "https://www.youtube.com/watch?v=" + id;
            }
            if (host.Contains("youtube.com"))
            {
                string id = null;
                foreach (var part in (u.Query ?? "").TrimStart('?').Split('&'))
                {
                    int eq = part.IndexOf('=');
                    if (eq > 0 && part[..eq] == "v")
                    {
                        id = Uri.UnescapeDataString(part[(eq + 1)..]);
                        break;
                    }
                }
                if (string.IsNullOrEmpty(id) && u.AbsolutePath.StartsWith("/shorts/", StringComparison.OrdinalIgnoreCase))
                    id = u.AbsolutePath["/shorts/".Length..].Trim('/');
                if (!string.IsNullOrEmpty(id))
                    return "https://www.youtube.com/watch?v=" + id;
            }
        }
        catch { }
        return url;
    }

    private static async Task<(string video, string audio)> RunYtDlpGetUrls(
        string ytdlpPath, string url, string cookiesBrowser)
    {
        using var p = StartYtDlp(ytdlpPath, url, cookiesBrowser);
        if (p == null) return (null, null);

        var readOut = p.StandardOutput.ReadToEndAsync();
        var readErr = p.StandardError.ReadToEndAsync();
        if (!await Task.Run(() => p.WaitForExit(30_000)))
        {
            try { p.Kill(); } catch { }
            return (null, null);
        }
        if (p.ExitCode != 0)
        {
            string err = (await readErr).Trim();
            if (!string.IsNullOrEmpty(err))
                GD.PrintErr($"[VideoManager] yt-dlp failed: {err}");
            return (null, null);
        }

        var lines = (await readOut).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string v = lines.Length > 0 ? lines[0].Trim() : null;
        string a = lines.Length > 1 ? lines[1].Trim() : null;
        return (string.IsNullOrEmpty(v) ? null : v, string.IsNullOrEmpty(a) ? null : a);
    }

    private static string FindCookieBrowser()
    {
        string home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        if (System.IO.Directory.Exists(System.IO.Path.Combine(home, ".config", "google-chrome")))
            return "chrome";
        if (System.IO.Directory.Exists(System.IO.Path.Combine(home, ".config", "chromium")))
            return "chromium";
        if (System.IO.Directory.Exists(System.IO.Path.Combine(home, ".mozilla", "firefox")))
            return "firefox";
        string os = OS.GetName();
        if (os == "Windows" || os == "macOS") return "chrome";
        return null;
    }

    private static System.Diagnostics.Process StartYtDlp(string ytdlpPath, string url,
        string cookiesBrowser = null)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ytdlpPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[]
        {
            "--no-warnings", "--no-playlist",
            "--extractor-args", "youtube:player_client=android,ios,tv_embedded,web",
            "-g",
            "-f", $"18/b[height<={MaxHeight}]/bv*[height<={MaxHeight}]+ba/b",
        })
            psi.ArgumentList.Add(a);
        if (!string.IsNullOrEmpty(cookiesBrowser))
        {
            psi.ArgumentList.Add("--cookies-from-browser");
            psi.ArgumentList.Add(cookiesBrowser);
        }
        psi.ArgumentList.Add(url);
        return System.Diagnostics.Process.Start(psi);
    }

    private static string FindBinary(string name)
    {
        EnsureBundledBinsExtracted();

        bool isWindows = OS.GetName() == "Windows";
        string exeName = isWindows ? name + ".exe" : name;

        // yt-dlp ages in days — prefer a system copy over the one we extracted months ago.
        // ffmpeg stays bundled-first: the static build is what LoopbackMediaProxy expects.
        bool preferSystem = name == "yt-dlp";
        string home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        if (preferSystem)
        {
            string[] first = isWindows
                ? new[] { System.IO.Path.Combine(home, ".local", "bin", exeName) }
                : new[] { $"{home}/.local/bin/{name}", "/usr/local/bin/" + name, "/usr/bin/" + name };
            foreach (var c in first)
                try { if (System.IO.File.Exists(c)) return c; } catch { }
        }

        // 1. Prepackaged binaries extracted to user://bin/
        string userBin = ProjectSettings.GlobalizePath("user://bin");
        string bundled = System.IO.Path.Combine(userBin, exeName);
        if (System.IO.File.Exists(bundled)) return bundled;

        // 2. Next to the game executable (for non-embedded deployments)
        try
        {
            string exeDir = System.IO.Path.GetDirectoryName(OS.GetExecutablePath());
            if (!string.IsNullOrEmpty(exeDir))
            {
                string beside = System.IO.Path.Combine(exeDir, "bin", exeName);
                if (System.IO.File.Exists(beside)) return beside;
            }
        }
        catch { }

        // 3. System PATH locations
        string[] candidates = isWindows
            ? new[] {
                System.IO.Path.Combine(home, ".local", "bin", exeName),
                System.IO.Path.Combine(home, "bin", exeName),
            }
            : new[] {
                $"{home}/.local/bin/{name}",
                "/usr/local/bin/" + name,
                $"{home}/bin/{name}",
                "/usr/bin/" + name,
            };
        foreach (var c in candidates)
        {
            try
            {
                if (System.IO.File.Exists(c)) return c;
            }
            catch { }
        }

        // 4. Anything on PATH.
        try
        {
            string pathVar = System.Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathVar.Split(System.IO.Path.PathSeparator,
                                              StringSplitOptions.RemoveEmptyEntries))
            {
                string c = System.IO.Path.Combine(dir.Trim(), exeName);
                if (System.IO.File.Exists(c)) return c;
            }
        }
        catch { }

        // Returning the bare name here would hand callers a path that cannot be started, and
        // the resulting Win32Exception reads as "transcode failed" rather than "ffmpeg is
        // missing". Null is the honest answer and lets the caller fall back to the server.
        GD.PrintErr($"[VideoManager] {exeName} not found (bundled, beside-exe, or on PATH)");
        return null;
    }

    private static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// A readable label from a URL before the resolver gives us a real title.
    private void OnPlaybackStarted(Item item)
    {
        if (item == null) return;
        Toast?.Invoke($"▶ {item.Title}", 3);
        _startedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Broadcast(VideoNet.Play(item.Url, _startedUnixMs));
        TrySeekPlaying();
    }

    private void RememberSeek(string url, long startedUnixMs)
    {
        if (string.IsNullOrEmpty(url) || startedUnixMs <= 0) return;
        double sec = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startedUnixMs) / 1000.0;
        if (sec < 0.5) return;
        _seekUrl = url;
        _seekToSec = sec;
        _startedUnixMs = startedUnixMs;
    }

    private void TrySeekPlaying()
    {
        if (_seekToSec < 0.5 || NowPlaying == null) return;
        if (!string.Equals(NowPlaying.Url, _seekUrl, StringComparison.Ordinal)) return;
        PruneScreens();
        foreach (var s in _screens) s.SeekWhenReady(_seekToSec);
        _seekToSec = -1;
        _seekUrl = null;
    }

    private bool AlreadyHas(string url)
    {
        if (NowPlaying != null && string.Equals(NowPlaying.Url, url, StringComparison.Ordinal))
            return true;
        foreach (var i in _queue)
            if (string.Equals(i.Url, url, StringComparison.Ordinal)) return true;
        return false;
    }

    private void ReplyState()
    {
        if (NowPlaying == null && _queue.Count == 0) return;
        var upcoming = new string[_queue.Count];
        for (int i = 0; i < _queue.Count; i++) upcoming[i] = _queue[i].Url;
        Broadcast(VideoNet.State(NowPlaying?.Url, _startedUnixMs, upcoming));
    }

    private void ApplySnapshot(VideoNet.Msg msg)
    {
        if (msg == null) return;
        if (!string.IsNullOrEmpty(msg.Url) && AlreadyHas(msg.Url) && NowPlaying != null)
        {
            RememberSeek(msg.Url, msg.StartedUnixMs);
            TrySeekPlaying();
            return;
        }
        if (!string.IsNullOrEmpty(msg.Url))
        {
            RememberSeek(msg.Url, msg.StartedUnixMs);
            Enqueue(msg.Url, "room", fromNet: true);
        }
        if (msg.Queue != null)
        {
            foreach (var u in msg.Queue)
                Enqueue(u, "room", fromNet: true);
        }
    }

    private void Broadcast(string payload)
    {
        if (_applyingNet || _netSend == null || string.IsNullOrEmpty(payload)) return;
        _netSend(payload);
    }

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
