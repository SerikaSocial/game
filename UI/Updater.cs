using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Godot;
using HttpClient = System.Net.Http.HttpClient;

namespace SerikaSocial;

/// Auto-updater: checks CDN /version.txt at startup and prompts the player if a newer
/// build is available. Downloads the platform-appropriate zip, extracts it over the
/// current install, and restarts. Non-blocking — runs in the background so the game
/// continues to load while the check happens.
public partial class Updater : CanvasLayer
{
    private const string CDN_URL = "https://cdn-social.ado.ink";
    private const string VersionPath = CDN_URL + "/version.txt";

    public string CurrentVersion { get; set; } = "0.0.0";
    public event Action<string> UpdateAvailable;

    private AcceptDialog _dialog;
    private ProgressBar _progress;
    private Label _progressLabel;
    private bool _downloading;

    public override void _Ready()
    {
        Layer = 200;
        Visible = false;
    }

    /// Start the async version check. Fires UpdateAvailable on the main thread if a
    /// newer version is found. Silent on failure (network down, parse error, etc.).
    public void CheckForUpdates()
    {
        _ = CheckAsync();
    }

    private async Task CheckAsync()
    {
        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            var res = await http.GetStringAsync(VersionPath);
            string latest = res.Trim();
            GD.Print($"updater: local={CurrentVersion} remote={latest}");
            if (CompareVersions(latest, CurrentVersion) > 0)
            {
                CallDeferred(nameof(ShowUpdateDialog), latest);
            }
        }
        catch (Exception e)
        {
            GD.PrintErr($"updater: check failed: {e.Message}");
        }
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

    private void ShowUpdateDialog(string latestVersion)
    {
        UpdateAvailable?.Invoke(latestVersion);

        _dialog = new AcceptDialog
        {
            Title = "Update Available",
            DialogText = $"A new version of Serika Social is available!\n\n" +
                         $"Current: v{CurrentVersion}\n" +
                         $"Latest:  v{latestVersion}\n\n" +
                         $"Click Update to download and install the new version.\n" +
                         $"The game will restart automatically.",
            OkButtonText = "Update Now",
        };
        // AcceptDialog only has OK + Cancel. We use OK = Update, Cancel = Later.
        _dialog.OkButtonText = "Update Now";

        _dialog.Confirmed += () => _ = DownloadAndInstall(latestVersion);
        _dialog.Canceled += () => { _dialog.QueueFree(); _dialog = null; };

        AddChild(_dialog);
        _dialog.PopupCentered();
        Visible = true;
    }

    private string GetPlatformFile()
    {
        if (OS.HasFeature("windows"))
            return "SerikaSocial-windows-x86_64.zip";
        if (OS.HasFeature("linux") || OS.HasFeature("x11"))
            return "SerikaSocial-linux-x86_64.zip";
        if (OS.HasFeature("macos"))
            return "SerikaSocial-macos-universal.zip";
        if (OS.HasFeature("android"))
            return "SerikaSocial-android-mobile-arm64.zip";
        // Fallback: detect by OS name
        var osName = OS.GetName().ToLower();
        if (osName.Contains("windows")) return "SerikaSocial-windows-x86_64.zip";
        if (osName.Contains("linux")) return "SerikaSocial-linux-x86_64.zip";
        if (osName.Contains("macos") || osName.Contains("osx")) return "SerikaSocial-macos-universal.zip";
        return "SerikaSocial-linux-x86_64.zip";
    }

    private async Task DownloadAndInstall(string version)
    {
        if (_downloading) return;
        _downloading = true;

        // Replace the dialog with a progress view.
        _dialog?.QueueFree();
        _dialog = null;

        var vbox = new VBoxContainer
        {
            AnchorLeft = 0.5f, AnchorTop = 0.5f, AnchorRight = 0.5f, AnchorBottom = 0.5f,
            OffsetLeft = -240, OffsetTop = -100, OffsetRight = 240, OffsetBottom = 100,
        };
        AddChild(vbox);

        _progressLabel = new Label
        {
            Text = $"Downloading v{version}…",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _progressLabel.AddThemeFontSizeOverride("font_size", 18);
        _progressLabel.AddThemeColorOverride("font_color", Brand.TextHi);
        vbox.AddChild(_progressLabel);

        _progress = new ProgressBar
        {
            CustomMinimumSize = new Vector2(400, 24),
            MinValue = 0,
            MaxValue = 100,
            Value = 0,
        };
        vbox.AddChild(_progress);

        Visible = true;

        try
        {
            string file = GetPlatformFile();
            string url = $"{CDN_URL}/releases/{version}/{file}";

            // Download to a temp file.
            string tmpDir = ProjectSettings.GlobalizePath("user://updates");
            Directory.CreateDirectory(tmpDir);
            string zipPath = Path.Combine(tmpDir, file);

            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromMinutes(10);
            using var response = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            long totalBytes = response.Content.Headers.ContentLength ?? -1;
            long readBytes = 0;
            byte[] buffer = new byte[81920];

            await using (var input = await response.Content.ReadAsStreamAsync())
            await using (var output = File.Create(zipPath))
            {
                int n;
                while ((n = await input.ReadAsync(buffer)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n));
                    readBytes += n;
                    if (totalBytes > 0)
                    {
                        int pct = (int)(readBytes * 100 / totalBytes);
                        CallDeferred(nameof(SetProgress), pct);
                    }
                }
            }

            CallDeferred(nameof(SetProgressLabel), "Extracting…");

            // Extract the zip.
            string extractDir = Path.Combine(tmpDir, "extracted");
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
            Directory.CreateDirectory(extractDir);

            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extractDir, true);

            CallDeferred(nameof(SetProgressLabel), "Installing…");

            // Write a launcher script that replaces the current binary and restarts.
            string exePath = OS.GetExecutablePath();
            string exeDir = Path.GetDirectoryName(exePath) ?? ".";

            // Copy extracted files over the current install.
            foreach (var f in Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(extractDir, f);
                string dest = Path.Combine(exeDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                // Don't overwrite the running executable yet — do it via the launcher.
                if (Path.GetFullPath(dest) == Path.GetFullPath(exePath))
                    continue;
                File.Copy(f, dest, true);
            }

            // Create a launcher script that swaps the executable and restarts.
            string launcherPath = Path.Combine(tmpDir, "apply_update.sh");
            bool isWindows = OS.HasFeature("windows") || OS.GetName().ToLower().Contains("windows");

            if (isWindows)
            {
                launcherPath = Path.Combine(tmpDir, "apply_update.bat");
                string newExe = Path.Combine(extractDir, Path.GetFileName(exePath));
                await File.WriteAllTextAsync(launcherPath,
                    $"@echo off\r\n" +
                    $"timeout /t 1 /nobreak >nul\r\n" +
                    $"copy /Y \"{newExe}\" \"{exePath}\"\r\n" +
                    $"start \"\" \"{exePath}\"\r\n" +
                    $"del \"%~f0\"\r\n");
            }
            else
            {
                string newExe = Path.Combine(extractDir, Path.GetFileName(exePath));
                await File.WriteAllTextAsync(launcherPath,
                    $"#!/bin/sh\n" +
                    $"sleep 1\n" +
                    $"cp \"{newExe}\" \"{exePath}\"\n" +
                    $"chmod +x \"{exePath}\"\n" +
                    $"\"{exePath}\" &\n" +
                    $"rm \"$0\"\n");
                File.SetUnixFileMode(launcherPath, UnixFileMode.UserExecute | UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            // Launch the updater script and quit.
            var psi = new ProcessStartInfo
            {
                FileName = launcherPath,
                UseShellExecute = true,
                CreateNoWindow = true,
            };
            Process.Start(psi);

            CallDeferred(nameof(QuitGame));
        }
        catch (Exception e)
        {
            GD.PrintErr($"updater: download failed: {e.Message}");
            CallDeferred(nameof(ShowError), e.Message);
            _downloading = false;
        }
    }

    private void SetProgress(int pct)
    {
        if (_progress != null) _progress.Value = pct;
    }

    private void SetProgressLabel(string text)
    {
        if (_progressLabel != null) _progressLabel.Text = text;
    }

    private void ShowError(string msg)
    {
        if (vbox != null) vbox.QueueFree();
        var d = new AcceptDialog
        {
            Title = "Update Failed",
            DialogText = $"Could not download the update:\n{msg}\n\nPlease try again later or download manually from social.serika.dev",
            OkButtonText = "OK",
        };
        AddChild(d);
        d.PopupCentered();
        d.Confirmed += () => { d.QueueFree(); Visible = false; };
    }

    private VBoxContainer vbox;

    private void QuitGame()
    {
        GetTree().Quit();
    }
}
