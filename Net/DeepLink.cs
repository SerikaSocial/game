using System;
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
///   serikasocial://home                go to the personal Home
public static class DeepLink
{
    public const string Scheme = "serikasocial";

    public enum Kind { None, Home, World }

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
            var rest = url.Substring((Scheme + "://").Length).TrimEnd('/');
            if (rest.Length == 0) return Intent.None;
            var slash = rest.IndexOf('/');
            string host = slash < 0 ? rest : rest.Substring(0, slash);
            string tail = slash < 0 ? "" : rest.Substring(slash + 1);
            return host.ToLowerInvariant() switch
            {
                "home" => new Intent(Kind.Home, ""),
                "world" => IsUuidish(tail) ? new Intent(Kind.World, tail) : Intent.None,
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
    public static void RegisterHandler()
    {
        try
        {
            string exe = OS.GetExecutablePath();
            if (OS.HasFeature("windows")) RegisterWindows(exe);
            else if (OS.HasFeature("linux")) RegisterLinux(exe);
            // macOS registration is declared in the .app Info.plist (CFBundleURLTypes) at
            // export time rather than at runtime.
        }
        catch (Exception e)
        {
            GD.Print($"deep-link registration skipped: {e.Message}");
        }
    }

    private static void RegisterWindows(string exe)
    {
        // HKCU\Software\Classes\serikasocial → shell\open\command "exe" "%1"
        void Reg(params string[] a) => OS.Execute("reg", a);
        string root = @"HKCU\Software\Classes\" + Scheme;
        Reg("add", root, "/ve", "/d", "URL:Serika Social Protocol", "/f");
        Reg("add", root, "/v", "URL Protocol", "/d", "", "/f");
        Reg("add", root + @"\shell\open\command", "/ve", "/d", $"\"{exe}\" \"%1\"", "/f");
    }

    private static void RegisterLinux(string exe)
    {
        string apps = OS.GetEnvironment("HOME") + "/.local/share/applications";
        DirAccess.MakeDirRecursiveAbsolute(apps);
        string desktopPath = apps + "/serika-social.desktop";
        string contents =
            "[Desktop Entry]\n" +
            "Name=Serika Social\n" +
            "Exec=" + exe + " --serika-url=%u\n" +
            "Type=Application\n" +
            "Terminal=false\n" +
            "Categories=Game;\n" +
            "MimeType=x-scheme-handler/" + Scheme + ";\n";
        using (var f = FileAccess.Open(desktopPath, FileAccess.ModeFlags.Write))
            f?.StoreString(contents);
        // Point the scheme at our .desktop and refresh the desktop database.
        OS.Execute("xdg-mime", new[] { "default", "serika-social.desktop", "x-scheme-handler/" + Scheme });
        OS.Execute("update-desktop-database", new[] { apps });
    }
}
