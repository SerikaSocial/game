using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace SerikaSocial.World.Video;

/// A tiny loopback HTTP relay that lets the bundled ffmpeg read remote media.
///
/// **Why this exists.** The prepackaged ffmpeg is a fully static build, and a statically
/// linked glibc cannot call `getaddrinfo` — NSS is a dynamic-loading mechanism and there is
/// nothing to load. So the binary *segfaults* the moment it is handed a URL with a hostname.
/// It is fine against a literal IP: `-i http://127.0.0.1:port/0` returns a clean response
/// where `-i https://rr5---sn-....googlevideo.com/...` dies with SIGSEGV. That crash is the
/// actual reason "ffmpeg doesn't work" — not the arguments, not the codecs.
///
/// So ffmpeg is never given a hostname or a TLS endpoint. It connects to 127.0.0.1 and this
/// class does the DNS, the TLS and the HTTP against the real CDN using .NET's stack, which
/// is dynamically linked and works everywhere. Byte ranges are passed through in both
/// directions so ffmpeg can still seek — mp4 keeps its index at the end of the file, and
/// without range support it could not read the moov atom.
///
/// Bound to an ephemeral port on the loopback interface only, and alive only for the duration
/// of one transcode, so it is not a listening surface in any meaningful sense.
public sealed class LoopbackMediaProxy : IDisposable
{
    private readonly HttpListener _listener;
    private readonly List<string> _targets;
    private readonly System.Net.Http.HttpClient _http;
    private readonly CancellationTokenSource _cts = new();

    // Flow-control gate. When "closed" (paused), byte copying to ffmpeg blocks, so ffmpeg's
    // input read stalls and the encoder stops burning CPU. Reopening lets it continue. This is
    // how the manager keeps ffmpeg from racing through a whole clip ahead of playback — the
    // encoder only runs while playback still needs more buffer. Starts open.
    private volatile TaskCompletionSource<bool> _gate;

    public int Port { get; }

    private LoopbackMediaProxy(HttpListener listener, int port, List<string> targets)
    {
        _listener = listener;
        Port = port;
        _targets = targets;
        _http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _gate = Opened();
    }

    private static TaskCompletionSource<bool> Opened()
    {
        var t = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        t.SetResult(true);
        return t;
    }

    /// Stall byte delivery to ffmpeg. Idempotent; the encoder winds down within a second or two
    /// as its internal buffers drain.
    public void Pause()
    {
        if (!_gate.Task.IsCompleted) return; // already paused
        _gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// Resume byte delivery. Idempotent.
    public void Resume()
    {
        _gate.TrySetResult(true);
    }

    public bool IsPaused => !_gate.Task.IsCompleted;

    /// The loopback URL ffmpeg should use for track `index`.
    public string UrlFor(int index) => $"http://127.0.0.1:{Port}/{index}";

    /// Bind an ephemeral loopback port and start serving. Returns null if no port could be
    /// claimed, so the caller can fall back to the server-side transcode.
    public static LoopbackMediaProxy Start(List<string> remoteUrls)
    {
        if (remoteUrls == null || remoteUrls.Count == 0) return null;

        // HttpListener has no "port 0" mode, so probe a few high ports. Loopback prefixes do
        // not need a URL ACL on Windows, unlike wildcard ones.
        var rng = new Random();
        for (int attempt = 0; attempt < 12; attempt++)
        {
            int port = rng.Next(38000, 48000);
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
            }
            catch
            {
                try { listener.Close(); } catch { }
                continue; // port taken — try another
            }

            var proxy = new LoopbackMediaProxy(listener, port, remoteUrls);
            _ = proxy.AcceptLoopAsync();
            return proxy;
        }

        GD.PrintErr("[LoopbackMediaProxy] could not bind a loopback port");
        return null;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; } // listener stopped
            _ = HandleAsync(ctx);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            if (!int.TryParse(ctx.Request.Url.AbsolutePath.Trim('/'), out int idx) ||
                idx < 0 || idx >= _targets.Count)
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, _targets[idx]);
            // Forward the range verbatim; ffmpeg relies on this to seek.
            string range = ctx.Request.Headers["Range"];
            if (!string.IsNullOrEmpty(range) &&
                System.Net.Http.Headers.RangeHeaderValue.TryParse(range, out var parsed))
            {
                req.Headers.Range = parsed;
            }

            using var up = await _http.SendAsync(
                req, HttpCompletionOption.ResponseHeadersRead, _cts.Token);

            ctx.Response.StatusCode = (int)up.StatusCode;
            if (up.Content.Headers.ContentLength is long len)
                ctx.Response.ContentLength64 = len;
            if (up.Content.Headers.ContentType != null)
                ctx.Response.ContentType = up.Content.Headers.ContentType.ToString();
            if (up.Content.Headers.ContentRange != null)
                ctx.Response.Headers["Content-Range"] = up.Content.Headers.ContentRange.ToString();
            ctx.Response.Headers["Accept-Ranges"] = "bytes";

            using var stream = await up.Content.ReadAsStreamAsync(_cts.Token);
            // Manual copy so each chunk waits on the flow-control gate. A plain CopyToAsync
            // would hand ffmpeg the whole stream as fast as the CDN serves it, which is exactly
            // the "race the entire clip" behaviour we are trying to prevent.
            var buf = new byte[64 * 1024];
            int n;
            while ((n = await stream.ReadAsync(buf, _cts.Token)) > 0)
            {
                // Block here while paused. Re-read the field each iteration because Pause swaps
                // in a fresh gate.
                while (!_gate.Task.IsCompleted)
                    await _gate.Task.WaitAsync(_cts.Token);
                await ctx.Response.OutputStream.WriteAsync(buf.AsMemory(0, n), _cts.Token);
            }
        }
        catch
        {
            // ffmpeg drops connections routinely when it seeks or when it has read enough.
            // A broken pipe here is normal operation, not an error worth surfacing.
        }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
    }

    public void Dispose()
    {
        try { _gate.TrySetResult(true); } catch { } // release any blocked copy so it can cancel
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        try { _http.Dispose(); } catch { }
        try { _cts.Dispose(); } catch { }
    }
}
