using Godot;

namespace SerikaSocial.Avatar;

/// Shared plumbing for the extra render copies of an avatar mesh — the ShadowsOnly twin and the
/// first-person proxy.
///
/// Both are attached as CHILDREN of the mesh they copy rather than siblings of it. That single
/// choice buys visibility inheritance: avatar toggles, and rigs that ship meshes switched off (a
/// retarget source, an unused outfit variant), hide the source node, and a sibling copy would
/// happily keep rendering and casting geometry the player cannot see. It cost a bow floating in
/// first person and a garment casting a shadow after being toggled off.
///
/// The price is that `MeshInstance3D.Skeleton` — a path relative to the instance — no longer
/// points anywhere useful, so it is re-derived from the copy's new depth.
public static class AvatarCopy
{
    /// Marks a node as one of these generated copies. Any code that walks an avatar's meshes must
    /// check it, or it will find twins and proxies among the real geometry — and, since making a
    /// copy of a mesh adds a child to it, a naive recursive walk that copies as it goes recurses
    /// forever.
    public const string GeneratedMeta = "serika_avatar_copy";

    /// True if `node` is a generated copy rather than the avatar's own geometry.
    public static bool IsGenerated(Node node) => node.HasMeta(GeneratedMeta);

    /// Parent `copy` under `src` and fix up its skinning path. The copy sits at identity, so its
    /// global transform is exactly the source's.
    public static void Attach(MeshInstance3D src, MeshInstance3D copy)
    {
        var skeleton = src.GetNodeOrNull<Skeleton3D>(src.Skeleton);
        copy.SetMeta(GeneratedMeta, true);
        copy.Transform = Transform3D.Identity;
        src.AddChild(copy);
        if (skeleton != null) copy.Skeleton = copy.GetPathTo(skeleton);
    }
}
