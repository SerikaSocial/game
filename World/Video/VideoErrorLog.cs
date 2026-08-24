using System;
using Godot;

namespace SerikaSocial.World.Video;

/// Appends video playback failures to `user://logs/video-errors.log`.
///
/// The user asked for failures to be written to an error-log directory, separate from the
/// engine console, so a screen that quietly can't play something leaves a durable trail:
/// which URL, which world, and why. Best-effort — a logging failure must never take down
/// playback, so everything here swallows its own exceptions.
public static class VideoErrorLog
{
    private const string Dir = "user://logs";
    private const string Path = "user://logs/video-errors.log";

    /// Absolute on-disk path, surfaced in the queue UI so a user can find the file.
    public static string AbsolutePath => ProjectSettings.GlobalizePath(Path);

    /// Read the most recent error log entries.
    public static System.Collections.Generic.List<string> ReadRecent(int maxLines = 10)
    {
        var list = new System.Collections.Generic.List<string>();
        try
        {
            if (!FileAccess.FileExists(Path)) return list;
            using var f = FileAccess.Open(Path, FileAccess.ModeFlags.Read);
            if (f == null) return list;
            var all = new System.Collections.Generic.List<string>();
            while (!f.EofReached())
            {
                string line = f.GetLine();
                if (!string.IsNullOrWhiteSpace(line)) all.Add(line);
            }
            int start = Mathf.Max(0, all.Count - maxLines);
            for (int i = start; i < all.Count; i++) list.Add(all[i]);
        }
        catch { }
        return list;
    }

    public static void Record(string url, string worldName, string reason, string detail = null)
    {
        try
        {
            DirAccess.MakeDirRecursiveAbsolute(Dir);
            using var f = FileAccess.Open(Path, FileAccess.FileExists(Path) ? FileAccess.ModeFlags.ReadWrite : FileAccess.ModeFlags.Write);
            if (f == null)
            {
                // Fall back to the console so the failure isn't lost entirely.
                GD.PrintErr($"VideoErrorLog: cannot open {Path} — {reason}: {url}");
                return;
            }
            f.SeekEnd();
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t[{worldName}]\t{reason}\t{url}";
            if (!string.IsNullOrEmpty(detail)) line += $"\t{detail}";
            f.StoreLine(line);
        }
        catch (Exception e)
        {
            GD.PrintErr($"VideoErrorLog: {e.Message}");
        }
    }
}
