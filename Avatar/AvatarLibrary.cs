using System.Collections.Generic;
using Godot;

namespace SerikaSocial.Avatar;

/// Central registry for avatars the client can equip. Caches the raw `.ska` bytes per source so
/// repeated instantiation (e.g. one per remote player) doesn't re-hit disk. Each call to
/// `Instantiate` still builds a fresh `AvatarInstance` because every avatar needs its own
/// Skeleton3D to be posed independently.
///
/// The bundled default (`res://Assets/Avatars/suisei.ska`) is the fallback outfit until the
/// account/world tells us otherwise. Downloaded avatars live under `user://avatars/<id>.ska`.
public static class AvatarLibrary
{
    public const string DefaultAvatarPath = "res://Assets/Avatars/suisei.ska";

    private static readonly Dictionary<string, byte[]> Cache = new();

    /// The path the client uses when no per-user avatar has been chosen. Overridable so the
    /// world/account layer can point everyone at a different default without touching call sites.
    public static string CurrentDefaultPath { get; set; } = DefaultAvatarPath;

    public static byte[] LoadBytes(string path)
    {
        if (Cache.TryGetValue(path, out var cached)) return cached;
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (f == null)
        {
            GD.PrintErr($"AvatarLibrary: cannot open {path} ({FileAccess.GetOpenError()})");
            return null;
        }
        var bytes = f.GetBuffer((long)f.GetLength());
        Cache[path] = bytes;
        return bytes;
    }

    /// Build an avatar from a path, or null on failure (caller falls back to the capsule).
    public static AvatarInstance Instantiate(string path)
    {
        var bytes = LoadBytes(path);
        return bytes == null ? null : AvatarInstance.FromBytes(bytes);
    }

    /// Build the current default avatar.
    public static AvatarInstance InstantiateDefault() => Instantiate(CurrentDefaultPath);
}
