using System.Collections.Generic;
using Godot;

namespace SerikaSocial.Avatar;

/// Central registry for avatars the client can equip. Caches the raw `.ska` bytes per source so
/// repeated instantiation (e.g. one per remote player) doesn't re-hit disk. Each call to
/// `Instantiate` still builds a fresh `AvatarInstance` because every avatar needs its own
/// Skeleton3D to be posed independently.
///
/// There is no bundled default avatar — the default outfit is set by the web admin and served
/// from the API. When no cloud default is available (offline, API down, or a blocked user),
/// callers fall back to the procedural bean via `InstantiateBean()` / `InstantiateOrDefault()`.
/// Downloaded avatars live under `user://avatars/<id>.ska`.
public static class AvatarLibrary
{
    private static readonly Dictionary<string, byte[]> Cache = new();

    /// The LOCAL player's equipped avatar. Only the local rig may use this — a remote player
    /// wearing it is the bug where everyone in the instance looked like you.
    public static string CurrentDefaultPath { get; set; } = null;

    /// The shared default outfit, used for any peer whose own avatar can't be resolved.
    /// Deliberately separate from `CurrentDefaultPath`: falling back to the local player's
    /// avatar makes every remote a clone of the viewer.
    public static string DefaultOutfitPath { get; set; } = null;

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

    /// Build an avatar from a path, or null on failure (caller falls back to the bean).
    public static AvatarInstance Instantiate(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var bytes = LoadBytes(path);
        return bytes == null ? null : AvatarInstance.FromBytes(bytes);
    }

    /// Build the procedural bean avatar — the offline / blocked-user fallback.
    public static AvatarInstance InstantiateBean() => AvatarInstance.CreateBean();

    /// Try `path`, falling back to the bean on failure or null path. Never returns null.
    public static AvatarInstance InstantiateOrDefault(string path)
    {
        var av = Instantiate(path);
        return av ?? InstantiateBean();
    }

    /// Build the current cloud default avatar, or the bean if no default is set / load fails.
    public static AvatarInstance InstantiateDefault() => InstantiateOrDefault(CurrentDefaultPath);
}
