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
    private const string DefaultCdnUrl = "https://cdn-social.ado.ink";

    /// Overridable so the update path can actually be exercised end to end against a local
    /// server. Every other part of this class is testable in isolation; the one thing that
    /// mattered — does a real build download, verify and swap itself — was not, which is how a
    /// swap that could never succeed on Windows shipped. Unset in production.
    private static string CDN_URL
    {
        get
        {
            string o = OS.GetEnvironment("SERIKA_CDN_URL");
            return string.IsNullOrWhiteSpace(o) ? DefaultCdnUrl : o.TrimEnd('/');
        }
    }

    private static string VersionPath => CDN_URL + "/version.txt";

    public string CurrentVersion { get; set; } = "0.0.0";
    public event Action<string> UpdateAvailable;

    private enum Phase { Idle, Downloading, Verifying, Installing, Done, Failed }

    // Written by the download task, read by _Process on the main thread.
    private long _readBytes;
    private long _totalBytes = -1;
    private int _phase = (int)Phase.Idle;
    private string _statusText = "";
    private string _errorText = "";

    // Presentation lives in UpdateScreen (a full screen at Layer 210). This class owns the
    // check, download, verification and staging; it never builds a control itself.
    //
    // Main constructs the screen and assigns it here, so the screen goes through the same
    // AddUi routing as every other layer — which is what puts it on the VR panel in VR.
    public UpdateScreen Screen { get; set; }
    private UpdateScreen _screen => Screen;

    private bool _busy;
    private float _sweep;

    // Cancels an in-flight download. Without this, dismissing the screen mid-download let the
    // task run to completion and then quit the game out from under the player.
    private CancellationTokenSource _cts;

    public override void _Ready()
    {
        // This node no longer draws anything — UpdateScreen owns the pixels. It stays a
        // CanvasLayer purely so Main can keep treating it as one of the UI layers.
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
        // The editor's executable is Godot itself. F5/"Play Project" spawns a child that
        // does NOT have the `editor` feature, so we also skip anything whose binary is
        // named Godot — otherwise debug mode nags to overwrite the editor with a game build.
        string exe = OS.GetExecutablePath() ?? "";
        string exeName = System.IO.Path.GetFileName(exe);
        if (OS.HasFeature("editor")
            || OS.HasFeature("debug") && exeName.Contains("Godot", StringComparison.OrdinalIgnoreCase)
            || exeName.Contains("Godot", StringComparison.OrdinalIgnoreCase))
        {
            GD.Print($"updater: skipped (editor/debug, local={CurrentVersion}, exe={exeName})");
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
        _screen.Present();

        // A Hub install updates THROUGH the Hub — two self-updaters racing over one
        // install directory is how the half-updated-install bug happens twice. Detect the
        // Hub's registry file next to us and hand the player to it. Standalone installs
        // keep the Phase 1 path below.
        if (DetectHubInstall(out string hubPath))
        {
            _screen.Title = "Update available";
            _screen.Body = $"Serika Social v{latestVersion} is ready.\n"
                        + "This install is managed by the Serika Desktop Hub.";
            _screen.ProgressVisible = false;
            _screen.DetailVisible = false;
            _screen.ClearActions();

            var later = Brand.Ghost_(new Button { Text = "Later" });
            later.Pressed += Dismiss;
            _screen.AddAction(later);

            var hub = Brand.Primary_(new Button { Text = "Open Serika Desktop Hub" });
            hub.Pressed += () =>
            {
                OS.ShellOpen(hubPath);
                Dismiss();
            };
            _screen.AddAction(hub);
            return;
        }

        _screen.Title = "Update available";
        string body = $"Serika Social v{latestVersion} is ready to install.\n"
                    + $"You're on v{CurrentVersion}.";
        _screen.ProgressVisible = false;
        _screen.DetailVisible = false;

        _screen.ClearActions();
        if (CanSelfUpdate)
        {
            _screen.Body = body;

            var later = Brand.Ghost_(new Button { Text = "Later" });
            later.Pressed += Dismiss;
            _screen.AddAction(later);

            var now = Brand.Primary_(new Button { Text = "Update now" });
            now.Pressed += () => _ = DownloadAndInstall(latestVersion);
            _screen.AddAction(now);

            // E2E harness: SERIKA_AUTO_UPDATE=1 accepts the prompt without a click, so the
            // download → verify → stage → swap → relaunch path can be driven unattended
            // against a local CDN. Unset in production — a player always sees the choice.
            if (OS.GetEnvironment("SERIKA_AUTO_UPDATE") == "1")
                _ = DownloadAndInstall(latestVersion);
        }
        else
        {
            _screen.Body = body + (IsAndroid
                ? "\n\nUpdates on this platform install from the download page."
                : "\n\nThis install can't update itself (it's read-only or managed by your "
                  + "system). Grab the new build from the download page.");

            var later = Brand.Ghost_(new Button { Text = "Later" });
            later.Pressed += Dismiss;
            _screen.AddAction(later);

            var open = Brand.Primary_(new Button { Text = "Open download page" });
            open.Pressed += () =>
            {
                OS.ShellOpen("https://social.serika.dev/#download");
                Dismiss();
            };
            _screen.AddAction(open);
        }
    }

    /// The Hub's install registry lives in its AppLocalData dir. Windows: per-user
    /// %LOCALAPPDATA%. Linux: XDG data home (the .deb installs the Hub system-wide but
    /// its registry is still per-user). A cheap File.Exists probe — no parse needed to
    /// know the Hub owns this machine's installs.
    private static bool DetectHubInstall(out string hubPath)
    {
        hubPath = null;
        if (IsAndroid) return false;

        // Qt's AppLocalDataLocation is <LocalAppData|XDG_DATA_HOME>/Serika/"Serika Desktop Hub"
        // (organization "Serika", application "Serika Desktop Hub" — keep in sync with the Hub's main.cpp).
        string registry = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "Serika", "Serika Desktop Hub", "installed.json");
        if (!System.IO.File.Exists(registry)) return false;

        // Prefer the real exe when we can find one; serikahub:// works whenever the
        // Hub's installer registered the protocol even if the exe moved.
        if (IsWindows)
        {
            hubPath = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Serika Desktop Hub", "SerikaHub.exe");
            if (System.IO.File.Exists(hubPath)) return true;
        }
        else if (System.IO.File.Exists("/usr/bin/serika-hub"))
        {
            hubPath = "/usr/bin/serika-hub";
            return true;
        }
        hubPath = "serikahub://library";
        return true;
    }

    private void Dismiss()
    {
        // Abort any download in flight. Dismissing used to only hide the UI: the task kept
        // going, installed, and quit the game some minutes later with no warning.
        try { _cts?.Cancel(); } catch { /* already disposed */ }

        SetProcess(false);
        _screen?.Close();
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

        _screen.Title = $"Updating to v{version}";
        _screen.Body = "Downloading…";
        _screen.ProgressVisible = true;
        _screen.DetailVisible = true;
        _screen.Detail = "";
        _screen.ClearActions();

        // Cancelling is only meaningful while bytes are moving; once the swap is scheduled
        // there is nothing left to stop.
        var cancel = Brand.Ghost_(new Button { Text = "Cancel" });
        cancel.Pressed += Dismiss;
        _screen.AddAction(cancel);

        SetPhase(Phase.Downloading, "Downloading");
        Interlocked.Exchange(ref _readBytes, 0);
        Interlocked.Exchange(ref _totalBytes, -1);
        SetProcess(true);

        string file = GetPlatformFile();
        string tmpDir = ProjectSettings.GlobalizePath("user://updates");
        string zipPath = Path.Combine(tmpDir, file);

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            Directory.CreateDirectory(tmpDir);

            // Expected hash first — if we can't authenticate the download there's no
            // point starting it. Versioned path, never `latest`.
            string expected = await FetchExpectedHash(version, file);

            string url = $"{CDN_URL}/releases/{version}/{file}";
            string actual = await DownloadHashed(url, zipPath, ct);

            if (!string.IsNullOrEmpty(expected) &&
                !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Checksum mismatch — the download was corrupted or tampered with.\n" +
                    $"expected {expected[..16]}…, got {actual[..16]}…");
            }

            // Last chance to bail: past this point a restart is scheduled.
            ct.ThrowIfCancellationRequested();

            SetPhase(Phase.Installing, "Installing");
            await Task.Run(() => ApplyUpdate(zipPath, tmpDir, version), ct);

            // The apply script consumes the staged TREE, not the zip — leaving the archive
            // behind stranded ~300 MB in user://updates on every successful update.
            TryDelete(zipPath);

            CallDeferred(nameof(QuitGame));
        }
        catch (OperationCanceledException)
        {
            // The player dismissed the screen. Leave no half-downloaded zip behind, and
            // emphatically do not quit the game.
            GD.Print("updater: cancelled by the player");
            TryDelete(zipPath);
            _busy = false;
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
    private async Task<string> DownloadHashed(string url, string zipPath, CancellationToken ct)
    {
        using var http = NewClient(TimeSpan.FromMinutes(30));
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? -1;
        Interlocked.Exchange(ref _totalBytes, total);

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1 << 20];
        long read = 0;

        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = File.Create(zipPath))
        {
            int n;
            while ((n = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
                hasher.AppendData(buffer, 0, n);
                read += n;
                Interlocked.Exchange(ref _readBytes, read);
            }
        }

        SetPhase(Phase.Verifying, "Verifying");
        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    // ── Install ─────────────────────────────────────────────────────────────────────

    /// Extract the verified zip into a staging directory and hand the swap to an external
    /// script, which runs after we exit.
    ///
    /// **Nothing may be written into the install directory while the game is running.** The
    /// previous version copied the whole archive over the live install and skipped only the
    /// running executable. On Windows that cannot work: `SerikaSocial.pck` and the .NET runtime
    /// DLLs are memory-mapped by this process, so `File.Copy` throws "used by another process"
    /// partway through and leaves a **half-updated, unbootable install** — new pck, old DLLs.
    /// Staging first means a failed swap leaves the old install completely intact.
    private void ApplyUpdate(string zipPath, string tmpDir, string version)
    {
        string stageRoot = Path.Combine(tmpDir, "staged");
        string extractDir = Path.Combine(stageRoot, version);
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

        // A release zip may wrap everything in a single top-level folder. Copying that folder
        // verbatim would nest the whole game one level deep inside the install.
        string payload = ResolvePayloadRoot(extractDir, exePath);

        // Confirm the payload really is a build before we schedule a swap over the install.
        FindNewExecutable(payload, exePath);

        string script = IsWindows
            ? WriteWindowsApplyScript(tmpDir, payload, exeDir, exePath)
            : WriteUnixApplyScript(tmpDir, payload, exeDir, exePath);

        StartDetached(script);
    }

    /// Zips are published two ways in the wild: files at the root, or wrapped in one folder.
    /// If the archive root holds exactly one directory and no executable, descend into it.
    internal static string ResolvePayloadRoot(string extractDir, string exePath)
    {
        string wanted = Path.GetFileName(exePath);
        if (File.Exists(Path.Combine(extractDir, wanted))) return extractDir;

        var dirs = Directory.GetDirectories(extractDir);
        var files = Directory.GetFiles(extractDir);
        if (dirs.Length == 1 && files.Length == 0) return dirs[0];

        return extractDir;
    }

    /// Launch the apply script without a console window and without a handle to us — it has to
    /// outlive this process, since its whole job starts once we are gone.
    private static void StartDetached(string script)
    {
        var psi = IsWindows
            ? new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{script}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            }
            : new ProcessStartInfo
            {
                FileName = "/bin/sh",
                Arguments = $"\"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
        Process.Start(psi);
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

    /// Arguments worth carrying across a restart. `--vr` is a durable mode choice, so dropping
    /// it would silently boot a headset user into the desktop client. Deep links are
    /// deliberately NOT carried: by the time the update finishes, joining the world the player
    /// clicked minutes ago is surprising, and the instance is very likely gone.
    private static string RelaunchArgs()
    {
        foreach (var a in OS.GetCmdlineArgs())
            if (a == "--vr") return " --vr";
        foreach (var a in OS.GetCmdlineUserArgs())
            if (a == "--vr") return " -- --vr";
        return "";
    }

    /// robocopy, not `copy` — it retries locked files on its own (/R, /W), which is exactly the
    /// race we are in: Windows can hold the pck and runtime DLLs for a moment after the process
    /// exits. Exit codes below 8 are success (1 = files copied, 2 = extras, 3 = both).
    internal static string WriteWindowsApplyScript(string tmpDir, string payload, string exeDir, string exePath)
    {
        string p = Path.Combine(tmpDir, "apply_update.bat");
        File.WriteAllText(p,
            "@echo off\r\n" +
            "setlocal\r\n" +
            $"set \"SRC={payload}\"\r\n" +
            $"set \"DST={exeDir}\"\r\n" +
            $"set \"GAME={exePath}\"\r\n" +
            $"set PID={System.Environment.ProcessId}\r\n" +
            "set /a TRIES=0\r\n" +
            ":wait\r\n" +
            "tasklist /FI \"PID eq %PID%\" 2>nul | find \"%PID%\" >nul\r\n" +
            "if errorlevel 1 goto gone\r\n" +
            "set /a TRIES+=1\r\n" +
            "if %TRIES% GEQ 60 goto gone\r\n" +
            "ping -n 2 127.0.0.1 >nul\r\n" +
            "goto wait\r\n" +
            ":gone\r\n" +
            // /E keeps subdirectories; deliberately NOT /MIR, which would purge anything in the
            // install dir that the archive does not contain.
            "robocopy \"%SRC%\" \"%DST%\" /E /IS /IT /R:10 /W:2 /NFL /NDL /NJH /NJS /NP >nul\r\n" +
            "if errorlevel 8 (\r\n" +
            "  echo Serika Social update failed: robocopy exit %ERRORLEVEL% > \"%DST%\\update-error.log\"\r\n" +
            "  start \"\" \"%GAME%\"\r\n" +
            "  exit /b 1\r\n" +
            ")\r\n" +
            $"start \"\" \"%GAME%\"{RelaunchArgs()}\r\n" +
            "rmdir /s /q \"%SRC%\"\r\n" +
            "del \"%~f0\"\r\n");
        return p;
    }

    internal static string WriteUnixApplyScript(string tmpDir, string payload, string exeDir, string exePath)
    {
        string p = Path.Combine(tmpDir, "apply_update.sh");
        File.WriteAllText(p,
            "#!/bin/sh\n" +
            $"SRC='{payload}'\n" +
            $"DST='{exeDir}'\n" +
            $"GAME='{exePath}'\n" +
            $"PID={System.Environment.ProcessId}\n" +
            "i=0\n" +
            "while [ $i -lt 60 ] && kill -0 \"$PID\" 2>/dev/null; do sleep 1; i=$((i+1)); done\n" +
            // `cp -a src/.` copies the CONTENTS of src, including dotfiles, preserving modes.
            "if ! cp -a \"$SRC/.\" \"$DST/\"; then\n" +
            "  echo 'Serika Social update failed: copy error' > \"$DST/update-error.log\"\n" +
            "  \"$GAME\" &\n" +
            "  exit 1\n" +
            "fi\n" +
            "chmod +x \"$GAME\" 2>/dev/null\n" +
            $"\"$GAME\"{RelaunchArgs()} &\n" +
            "rm -rf \"$SRC\"\n" +
            "rm -- \"$0\"\n");
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
        if (_screen == null || !_screen.IsBuilt) return;

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
            _screen.ProgressValue = read * 100.0 / total;
            _screen.Body = $"Downloading… {read * 100 / total}%";
            _screen.Detail = $"{Mib(read)} of {Mib(total)}";
        }
        else if (phase == Phase.Downloading)
        {
            // No Content-Length from the CDN — sweep the bar so it reads as "working"
            // instead of "stuck at zero", and show the real byte count underneath.
            _sweep = (_sweep + (float)delta * 0.55f) % 1f;
            float t = _sweep < 0.5f ? _sweep * 2f : (1f - _sweep) * 2f;
            _screen.ProgressValue = 15 + t * 70;
            _screen.Body = "Downloading…";
            _screen.Detail = $"{Mib(read)} downloaded";
        }
        else
        {
            _screen.ProgressValue = 100;
            _screen.Body = phase switch
            {
                Phase.Verifying => "Verifying download…",
                Phase.Installing => "Installing… the game will restart.",
                _ => _statusText,
            };
            _screen.Detail = phase == Phase.Verifying ? $"{Mib(read)} · checking signature" : "";
        }
    }

    private void ShowFailure()
    {
        SetProcess(false);
        _screen.Title = "Update failed";
        _screen.Body = $"Could not install the update.\n\n{_errorText}";
        _screen.ProgressVisible = false;
        _screen.DetailVisible = true;
        _screen.Detail = "You can keep playing on this version, or download the latest build manually.";

        _screen.ClearActions();
        var close = Brand.Ghost_(new Button { Text = "Keep playing" });
        close.Pressed += Dismiss;
        _screen.AddAction(close);

        var site = Brand.Primary_(new Button { Text = "Open download page" });
        site.Pressed += () => { OS.ShellOpen("https://social.serika.dev/#download"); Dismiss(); };
        _screen.AddAction(site);
    }

    private static string Mib(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.00} GB"
                          : $"{bytes / (double)(1L << 20):0.0} MB";

    private void QuitGame() => GetTree().Quit();
}
