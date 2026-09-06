using System;
using System.Diagnostics;
using System.IO;
using Godot;

namespace SerikaSocial;

/// serikasocial:// deep links. Two jobs:
///  1. Parse an incoming link (passed as a launch argument by the OS) into an intent.
///  2. Register this executable as the handler for the scheme, so a browser's
///     "Open in app" button launches the client. Registration is best-effort and never
///     fatal — a failure just means the user launches the client manually.
///
/// Link grammar:
///   serikasocial://world/<worldId>     join a world (creates/joins an instance)
///   serikasocial://instance/<uuid>      join an exact public/invited instance
///   serikasocial://home                go to the personal Home
public static class DeepLink
{
    public const string Scheme = "serikasocial";

    public enum Kind { None, Home, World, Instance }

    public readonly struct Intent
    {
        public Intent(Kind kind, string arg) { Kind = kind; Arg = arg; }
        public Kind Kind { get; }
        public string Arg { get; }
        public static readonly Intent None = new(Kind.None, "");
    }

    /// Scan launch arguments for a serikasocial:// URL and parse it.
    public static Intent FromCommandLine()
    {
        foreach (var raw in OS.GetCmdlineArgs())
        {
            var arg = raw;
            // Also accept `--serika-url=serikasocial://…` for manual/dev launches.
            if (arg.StartsWith("--serika-url="))
                arg = arg.Substring("--serika-url=".Length);
            if (arg.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase))
                return Parse(arg);
        }
        return Intent.None;
    }

    public static Intent Parse(string url)
    {
        try
        {
            if (string.IsNullOrEmpty(url) || !url.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase))
                return Intent.None;
            var rest = url.Substring((Scheme + "://").Length).TrimEnd('/');
            if (rest.Length == 0) return Intent.None;
            var slash = rest.IndexOf('/');
            string host = slash < 0 ? rest : rest.Substring(0, slash);
            string tail = slash < 0 ? "" : rest.Substring(slash + 1);
            return host.ToLowerInvariant() switch
            {
                "home" => tail.Length == 0 ? new Intent(Kind.Home, "") : Intent.None,
                "world" => IsUuidish(tail) ? new Intent(Kind.World, tail) : Intent.None,
                "instance" => Guid.TryParseExact(tail, "D", out _) ? new Intent(Kind.Instance, tail) : Intent.None,
                _ => Intent.None,
            };
        }
        catch { return Intent.None; }
    }

    // Guard against a link injecting anything weird into the world-id path.
    private static bool IsUuidish(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 64) return false;
        foreach (var c in s)
            if (!(char.IsLetterOrDigit(c) || c == '-')) return false;
        return true;
    }

    /// Register this binary as the serikasocial:// handler for the current user. Best-effort,
    /// idempotent, and silent on failure. Called once at startup.
    ///
    /// Runs on a background thread because process creation is blocking. None of this work
    /// touches the scene tree, so it is safe off-thread; the only Godot call that wants the
    /// main thread is `OS.GetExecutablePath`, which we read here and capture.
    public static void RegisterHandler()
    {
        string exe;
        bool isWindows, isLinux;
        try
        {
            exe = OS.GetExecutablePath();
            isWindows = OS.HasFeature("windows");
            isLinux = OS.HasFeature("linux");
        }
        catch (Exception e)
        {
            GD.Print($"deep-link registration skipped: {e.Message}");
            return;
        }

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                if (isWindows) RegisterWindows(exe);
                else if (isLinux) RegisterLinux(exe);
                // macOS registration is declared in the .app Info.plist (CFBundleURLTypes) at
                // export time rather than at runtime.
            }
            catch (Exception e)
            {
                GD.Print($"deep-link registration skipped: {e.Message}");
            }
        });
    }

    /// Run a process with proper argument quoting. OS.Execute joins arguments with spaces
    /// on Windows, which breaks values containing spaces (e.g. "URL:Serika Social Protocol").
    /// ProcessStartInfo.ArgumentList handles quoting correctly per-argument.
    private static int RunProcess(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            var p = Process.Start(psi);
            p?.WaitForExit();
            return p?.ExitCode ?? -1;
        }
        catch (Exception e)
        {
            GD.Print($"deep-link: {file} failed: {e.Message}");
            return -1;
        }
    }

    private static void RegisterWindows(string exe)
    {
        // HKCU\Software\Classes\serikasocial → shell\open\command "exe" "%1"
        string root = @"HKCU\Software\Classes\" + Scheme;
        RunProcess("reg", "add", root, "/ve", "/d", "URL:Serika Social Protocol", "/f");
        RunProcess("reg", "add", root, "/v", "URL Protocol", "/d", "", "/f");
        RunProcess("reg", "add", root + @"\shell\open\command", "/ve", "/d", $"\"{exe}\" \"%1\"", "/f");
        GD.Print("deep-link: Windows URL scheme registered");
    }

    private static void RegisterLinux(string exe)
    {
        string apps = OS.GetEnvironment("HOME") + "/.local/share/applications";
        Directory.CreateDirectory(apps);
        string desktopPath = apps + "/serika-social.desktop";
        string contents =
            "[Desktop Entry]\n" +
            "Name=Serika Social\n" +
            "Exec=" + exe + " --serika-url=%u\n" +
            "Type=Application\n" +
            "Terminal=false\n" +
            "Categories=Game;\n" +
            "MimeType=x-scheme-handler/" + Scheme + ";\n";
        File.WriteAllText(desktopPath, contents);
        // Point the scheme at our .desktop and refresh the desktop database.
        RunProcess("xdg-mime", "default", "serika-social.desktop", "x-scheme-handler/" + Scheme);
        RunProcess("update-desktop-database", apps);
        GD.Print("deep-link: Linux URL scheme registered");
    }
}
