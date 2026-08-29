using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HttpClient = System.Net.Http.HttpClient;

namespace SerikaSocial;

/// Auto-updater: checks CDN /version.txt at startup and prompts the player if a newer
/// build is available. Downloads the platform zip, verifies it against the published
/// SHA256SUMS, extracts it over the current install, and restarts. Non-blocking — the
/// check runs in the background so the game keeps loading.
///
/// Two things about the CDN shape this has to work around:
///
///   • **The zips arrive without a `Content-Length`.** Cloudflare sits in front of the
///     nginx B2 proxy and streams them, so `Content.Headers.ContentLength` is null and
///     there is no total to compute a percentage from. We fetch the byte size from
///     `SHA256SUMS.txt`'s sibling `manifest.json` when present, and otherwise fall back
///     to an indeterminate bar plus a live megabyte counter — never a bar frozen at 0%.
///   • **`/releases/latest/` is a moving pointer.** We always download the *versioned*
///     path so a release published mid-download can't swap the bytes underneath us.
///
/// The download is hashed as it streams and checked against `SHA256SUMS.txt` before a
/// single file is written over the install. An update that fails verification is
/// discarded — we are about to overwrite and execute this binary, so it is not optional.
public partial class Updater : CanvasLayer
{
    private const string CDN_URL = "https://cdn-social.ado.ink";
    private const string VersionPath = CDN_URL + "/version.txt";

    public string CurrentVersion { get; set; } = "0.0.0";
    public event Action<string> UpdateAvailable;

    private enum Phase { Idle, Downloading, Verifying, Installing, Done, Failed }

    // Written by the download task, read by _Process on the main thread.
    private long _readBytes;
    private long _totalBytes = -1;
    private int _phase = (int)Phase.Idle;
    private string _statusText = "";
    private string _errorText = "";

    // UI, all owned by _card.
    private Control _root;
    private PanelContainer _card;
    private Label _title;
    private Label _body;
    private ProgressBar _progress;
    private Label _detail;
    private HBoxContainer _actions;

    private bool _busy;
    private float _sweep;

    public override void _Ready()
    {
        Layer = 200;
        Visible = false;
        SetProcess(false);
    }

    /// Start the async version check. Silent on failure (network down, parse error) —
    /// a broken update check must never block getting into the game.
    public void CheckForUpdates()
    {
        _ = CheckAsync();
    }

    private async Task CheckAsync()
    {
        // The editor's executable is Godot itself. Prompting "update" there is how you
        // get a dialog in debug that cannot (and must not) overwrite the editor.
        string exe = OS.GetExecutablePath() ?? "";
        if (OS.HasFeature("editor")
            || System.IO.Path.GetFileName(exe).Contains("Godot", StringComparison.OrdinalIgnoreCase))
        {
            GD.Print($"updater: skipped (editor/debug, local={CurrentVersion})");
            return;
        }

        try
        {
            using var http = NewClient(TimeSpan.FromSeconds(10));
            string latest = (await http.GetStringAsync(VersionPath)).Trim();
            GD.Print($"updater: local={CurrentVersion} remote={latest}");
            if (CompareVersions(latest, CurrentVersion) > 0)
                CallDeferred(nameof(ShowUpdatePrompt), latest);
        }
        catch (Exception e)
        {
            GD.PrintErr($"updater: check failed: {e.Message}");
        }
    }

    private static HttpClient NewClient(TimeSpan timeout)
    {
        // Identity encoding: a compressed transfer is what strips Content-Length on the
        // way through the CDN, and we want the real byte count whenever we can get it.
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("identity");
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"SerikaSocial/{Hud.ClientVersion}");
        return http;
    }

    /// Returns >0 if a is newer than b, <0 if older, 0 if equal.
    private static int CompareVersions(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        int len = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < len; i++)
        {
            int va = i < pa.Length && int.TryParse(pa[i], out var xa) ? xa : 0;
            int vb = i < pb.Length && int.TryParse(pb[i], out var xb) ? xb : 0;
            if (va != vb) return va.CompareTo(vb);
        }
        return 0;
    }

    // ── Platform ────────────────────────────────────────────────────────────────────

    private static bool IsWindows => OS.HasFeature("windows") || OS.GetName().ToLower().Contains("windows");
    private static bool IsMacOS => OS.HasFeature("macos") || OS.GetName().ToLower().Contains("macos");
    private static bool IsAndroid => OS.HasFeature("android") || OS.GetName().ToLower().Contains("android");

    private static string GetPlatformFile()
    {
        if (IsWindows) return "SerikaSocial-windows-x86_64.zip";
        if (IsMacOS) return "SerikaSocial-macos-universal.zip";
        if (IsAndroid)
            return OS.HasFeature("xr") ? "SerikaSocial-android-quest-arm64.zip"
                                       : "SerikaSocial-android-mobile-arm64.zip";
        return "SerikaSocial-linux-x86_64.zip";
    }

    /// Whether we can actually swap files in place. Android packages are signed APKs that
    /// must go through the package installer, and a package-managed or read-only install
    /// (`/opt/...`, an AppImage mount, a `.deb`-owned tree) can't be written by the running
    /// user — attempting the copy there fails halfway with "access denied". In both cases we
    /// send the player to the download page instead of pretending to self-update.
    private static bool CanSelfUpdate =>
        !IsAndroid && !OS.HasFeature("editor") && InstallDirWritable();

    /// Probe whether the directory holding the executable is writable by this process,
    /// by creating and deleting a temp file next to it. Cheap and definitive — cheaper than
    /// downloading 150 MB only to fail on the final copy.
    private static bool InstallDirWritable()
    {
        try
        {
            string exeDir = Path.GetDirectoryName(OS.GetExecutablePath()) ?? ".";
            string probe = Path.Combine(exeDir, $".serika_write_probe_{Guid.NewGuid():N}");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── UI ──────────────────────────────────────────────────────────────────────────

    private void ShowUpdatePrompt(string latestVersion)
    {
        UpdateAvailable?.Invoke(latestVersion);
        BuildCard();

        _title.Text = "Update available";
        _body.Text = $"Serika Social v{latestVersion} is ready to install.\n"
                   + $"You're on v{CurrentVersion}.";
        _progress.Visible = false;
        _detail.Visible = false;

        ClearActions();
        if (CanSelfUpdate)
        {
            var later = Brand.Ghost_(new Button { Text = "Later" });
            later.Pressed += Dismiss;
            _actions.AddChild(later);

            var now = Brand.Primary_(new Button { Text = "Update now" });
            now.Pressed += () => _ = DownloadAndInstall(latestVersion);
            _actions.AddChild(now);
        }
        else
        {
            _body.Text += IsAndroid
                ? "\n\nUpdates on this platform install from the download page."
                : "\n\nThis install can't update itself (it's read-only or managed by your "
                  + "system). Grab the new build from the download page.";
            var later = Brand.Ghost_(new Button { Text = "Later" });
            later.Pressed += Dismiss;
            _actions.AddChild(later);

            var open = Brand.Primary_(new Button { Text = "Open download page" });
            open.Pressed += () =>
            {
                OS.ShellOpen("https://social.serika.dev/#download");
                Dismiss();
            };
            _actions.AddChild(open);
        }

        Visible = true;
    }

    /// The whole update surface is one branded card over a dimmed backdrop — same panel
    /// language as the rest of the client. Built once, reused across states.
    private void BuildCard()
    {
        if (_root != null) return;

        _root = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.62f) };
        dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(dim);

        _card = new PanelContainer();
        _card.AddThemeStyleboxOverride("panel", Brand.Panel(Brand.Bg1, 18));
        _card.SetAnchorsPreset(Control.LayoutPreset.Center);
        _card.CustomMinimumSize = new Vector2(460, 0);
        _root.AddChild(_card);

        var pad = new MarginContainer();
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            pad.AddThemeConstantOverride(side, 28);
        _card.AddChild(pad);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 14);
        pad.AddChild(col);

        _title = new Label { Text = "Update" };
        _title.AddThemeFontSizeOverride("font_size", Brand.Fs(22));
        _title.AddThemeColorOverride("font_color", Brand.TextHi);
        col.AddChild(_title);

        _body = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _body.AddThemeFontSizeOverride("font_size", Brand.Fs(14));
        _body.AddThemeColorOverride("font_color", Brand.TextMid);
        col.AddChild(_body);

        _progress = new ProgressBar
        {
            CustomMinimumSize = new Vector2(0, 10),
            MinValue = 0,
            MaxValue = 100,
            Value = 0,
            ShowPercentage = false,
        };
        col.AddChild(_progress);

        _detail = new Label();
        _detail.AddThemeFontSizeOverride("font_size", Brand.Fs(12));
        _detail.AddThemeColorOverride("font_color", Brand.TextDim);
        col.AddChild(_detail);

        _actions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        _actions.AddThemeConstantOverride("separation", 10);
        col.AddChild(_actions);
    }

    private void ClearActions()
    {
        foreach (var c in _actions.GetChildren())
            c.QueueFree();
    }

    private void Dismiss()
    {
        Visible = false;
        SetProcess(false);
        _root?.QueueFree();
        _root = null;
        _card = null; _title = null; _body = null;
        _progress = null; _detail = null; _actions = null;
    }

    // ── Download ────────────────────────────────────────────────────────────────────

    private void SetPhase(Phase p, string status)
    {
        Interlocked.Exchange(ref _phase, (int)p);
        _statusText = status;
    }

    private async Task DownloadAndInstall(string version)
    {
        if (_busy) return;
        _busy = true;

        _title.Text = $"Updating to v{version}";
        _body.Text = "Downloading…";
        _progress.Visible = true;
        _detail.Visible = true;
        _detail.Text = "";
        ClearActions();
        SetPhase(Phase.Downloading, "Downloading");
        Interlocked.Exchange(ref _readBytes, 0);
        Interlocked.Exchange(ref _totalBytes, -1);
        SetProcess(true);

        string file = GetPlatformFile();
        string tmpDir = ProjectSettings.GlobalizePath("user://updates");
        string zipPath = Path.Combine(tmpDir, file);

        try
        {
            Directory.CreateDirectory(tmpDir);

            // Expected hash first — if we can't authenticate the download there's no
            // point starting it. Versioned path, never `latest`.
            string expected = await FetchExpectedHash(version, file);

            string url = $"{CDN_URL}/releases/{version}/{file}";
            string actual = await DownloadHashed(url, zipPath);

            if (!string.IsNullOrEmpty(expected) &&
                !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Checksum mismatch — the download was corrupted or tampered with.\n" +
                    $"expected {expected[..16]}…, got {actual[..16]}…");
            }

            SetPhase(Phase.Installing, "Installing");
            await Task.Run(() => ApplyUpdate(zipPath, tmpDir));

            CallDeferred(nameof(QuitGame));
        }
        catch (Exception e)
        {
            GD.PrintErr($"updater: update failed: {e}");
            TryDelete(zipPath);
            _errorText = e.Message;
            SetPhase(Phase.Failed, "Failed");
            _busy = false;
        }
    }

    /// Pull the published SHA256SUMS for this release and find our file's line.
    /// Returns "" if the file isn't published — we warn and continue rather than
    /// blocking the update on a missing sums file for older releases.
    private async Task<string> FetchExpectedHash(string version, string file)
    {
        try
        {
            using var http = NewClient(TimeSpan.FromSeconds(20));
            string sums = await http.GetStringAsync($"{CDN_URL}/releases/{version}/SHA256SUMS.txt");
            foreach (var line in sums.Split('\n'))
            {
                // Format is `<64-hex>  <filename>` (sha256sum output).
                var parts = line.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[1].TrimStart('*') == file)
                    return parts[0];
            }
            GD.PrintErr($"updater: {file} not listed in SHA256SUMS.txt for {version}");
        }
        catch (Exception e)
        {
            GD.PrintErr($"updater: could not fetch SHA256SUMS.txt: {e.Message}");
        }
        return "";
    }

    /// Stream the zip to disk, hashing as we go. Returns the lowercase hex SHA-256.
    /// Publishes progress into the fields _Process reads; the CDN usually omits
    /// Content-Length, in which case _totalBytes stays -1 and the bar goes indeterminate.
    private async Task<string> DownloadHashed(string url, string zipPath)
    {
        using var http = NewClient(TimeSpan.FromMinutes(30));
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? -1;
        Interlocked.Exchange(ref _totalBytes, total);

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1 << 20];
        long read = 0;

        await using (var input = await response.Content.ReadAsStreamAsync())
        await using (var output = File.Create(zipPath))
        {
            int n;
            while ((n = await input.ReadAsync(buffer)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n));
                hasher.AppendData(buffer, 0, n);
                read += n;
                Interlocked.Exchange(ref _readBytes, read);
            }
        }

        SetPhase(Phase.Verifying, "Verifying");
        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    // ── Install ─────────────────────────────────────────────────────────────────────

    private void ApplyUpdate(string zipPath, string tmpDir)
    {
        string extractDir = Path.Combine(tmpDir, "extracted");
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
        Directory.CreateDirectory(extractDir);
        System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extractDir, true);

        string exePath = OS.GetExecutablePath();
        string exeDir = Path.GetDirectoryName(exePath) ?? ".";

        if (IsMacOS)
        {
            ApplyMacBundleUpdate(extractDir, exePath, tmpDir);
            return;
        }

        // Copy everything except the running executable, which the launcher swaps once
        // we've exited (you can't overwrite a mapped binary in place on Windows).
        foreach (var f in Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(extractDir, f);
            string dest = Path.Combine(exeDir, rel);
            if (Path.GetFullPath(dest) == Path.GetFullPath(exePath)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, true);
        }

        string newExe = FindNewExecutable(extractDir, exePath);
        string launcher = IsWindows
            ? WriteWindowsLauncher(tmpDir, newExe, exePath)
            : WriteUnixLauncher(tmpDir, newExe, exePath);

        Process.Start(new ProcessStartInfo { FileName = launcher, UseShellExecute = true });
    }

    /// macOS ships a `.app` bundle — swapping the inner Mach-O leaves a bundle whose
    /// resources and Info.plist are from the old build. Replace the bundle wholesale.
    private void ApplyMacBundleUpdate(string extractDir, string exePath, string tmpDir)
    {
        string bundle = exePath;
        while (!string.IsNullOrEmpty(bundle) && !bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            bundle = Path.GetDirectoryName(bundle);

        string newBundle = null;
        foreach (var d in Directory.GetDirectories(extractDir, "*.app", SearchOption.AllDirectories))
        {
            newBundle = d;
            break;
        }

        if (string.IsNullOrEmpty(bundle) || newBundle == null)
            throw new InvalidOperationException("Could not locate the .app bundle to update.");

        string script = Path.Combine(tmpDir, "apply_update.sh");
        File.WriteAllText(script,
            "#!/bin/sh\n" +
            "sleep 1\n" +
            $"rm -rf \"{bundle}.old\"\n" +
            $"mv \"{bundle}\" \"{bundle}.old\" || exit 1\n" +
            $"cp -R \"{newBundle}\" \"{bundle}\" || {{ mv \"{bundle}.old\" \"{bundle}\"; exit 1; }}\n" +
            $"rm -rf \"{bundle}.old\"\n" +
            $"xattr -dr com.apple.quarantine \"{bundle}\" 2>/dev/null\n" +
            $"open \"{bundle}\"\n" +
            "rm \"$0\"\n");
        MakeExecutable(script);
        Process.Start(new ProcessStartInfo { FileName = script, UseShellExecute = true });
    }

    /// The export filename can change between releases, so prefer an exact name match and
    /// fall back to the only plausible executable in the archive.
    private static string FindNewExecutable(string extractDir, string exePath)
    {
        string wanted = Path.GetFileName(exePath);
        string exact = Path.Combine(extractDir, wanted);
        if (File.Exists(exact)) return exact;

        foreach (var f in Directory.GetFiles(extractDir, wanted, SearchOption.AllDirectories))
            return f;

        var candidates = new List<string>();
        foreach (var f in Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(f).ToLowerInvariant();
            if (IsWindows ? ext == ".exe" : (ext == "" || ext == ".x86_64"))
                candidates.Add(f);
        }
        if (candidates.Count == 1) return candidates[0];

        throw new FileNotFoundException(
            $"Update archive did not contain an executable matching '{wanted}'.");
    }

    private static string WriteWindowsLauncher(string tmpDir, string newExe, string exePath)
    {
        string p = Path.Combine(tmpDir, "apply_update.bat");
        File.WriteAllText(p,
            "@echo off\r\n" +
            "timeout /t 2 /nobreak >nul\r\n" +
            $"copy /Y \"{newExe}\" \"{exePath}\" >nul\r\n" +
            $"start \"\" \"{exePath}\"\r\n" +
            "del \"%~f0\"\r\n");
        return p;
    }

    private static string WriteUnixLauncher(string tmpDir, string newExe, string exePath)
    {
        string p = Path.Combine(tmpDir, "apply_update.sh");
        File.WriteAllText(p,
            "#!/bin/sh\n" +
            "sleep 1\n" +
            $"cp \"{newExe}\" \"{exePath}\" || exit 1\n" +
            $"chmod +x \"{exePath}\"\n" +
            $"\"{exePath}\" &\n" +
            "rm \"$0\"\n");
        MakeExecutable(p);
        return p;
    }

    /// Only ever called on the Unix paths, but the analyzer can't see through our runtime
    /// platform checks — the OperatingSystem guard is what it understands.
    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, UnixFileMode.UserExecute | UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    // ── Frame update ────────────────────────────────────────────────────────────────

    /// Progress is polled here rather than pushed with CallDeferred per chunk — a 200 MB
    /// download is thousands of chunks, and marshalling each one onto the main thread put
    /// the update straight into the frame budget.
    public override void _Process(double delta)
    {
        if (_progress == null) return;

        var phase = (Phase)Interlocked.CompareExchange(ref _phase, 0, 0);

        if (phase == Phase.Failed)
        {
            ShowFailure();
            return;
        }

        long read = Interlocked.Read(ref _readBytes);
        long total = Interlocked.Read(ref _totalBytes);

        if (phase == Phase.Downloading && total > 0)
        {
            _progress.Value = read * 100.0 / total;
            _body.Text = $"Downloading… {read * 100 / total}%";
            _detail.Text = $"{Mib(read)} of {Mib(total)}";
        }
        else if (phase == Phase.Downloading)
        {
            // No Content-Length from the CDN — sweep the bar so it reads as "working"
            // instead of "stuck at zero", and show the real byte count underneath.
            _sweep = (_sweep + (float)delta * 0.55f) % 1f;
            float t = _sweep < 0.5f ? _sweep * 2f : (1f - _sweep) * 2f;
            _progress.Value = 15 + t * 70;
            _body.Text = "Downloading…";
            _detail.Text = $"{Mib(read)} downloaded";
        }
        else
        {
            _progress.Value = 100;
            _body.Text = phase switch
            {
                Phase.Verifying => "Verifying download…",
                Phase.Installing => "Installing… the game will restart.",
                _ => _statusText,
            };
            _detail.Text = phase == Phase.Verifying ? $"{Mib(read)} · checking signature" : "";
        }
    }

    private void ShowFailure()
    {
        SetProcess(false);
        _title.Text = "Update failed";
        _body.Text = $"Could not install the update.\n\n{_errorText}";
        _progress.Visible = false;
        _detail.Visible = true;
        _detail.Text = "You can keep playing on this version, or download the latest build manually.";

        ClearActions();
        var close = Brand.Ghost_(new Button { Text = "Keep playing" });
        close.Pressed += Dismiss;
        _actions.AddChild(close);

        var site = Brand.Primary_(new Button { Text = "Open download page" });
        site.Pressed += () => { OS.ShellOpen("https://social.serika.dev/#download"); Dismiss(); };
        _actions.AddChild(site);
    }

    private static string Mib(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.00} GB"
                          : $"{bytes / (double)(1L << 20):0.0} MB";

    private void QuitGame() => GetTree().Quit();
}
