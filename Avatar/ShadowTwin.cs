using Godot;

namespace SerikaSocial.Avatar;

/// Renders a shadow-only twin of a first-person-hidden mesh.
///
/// Why this exists: Godot 4 couples view visibility to shadow casting — a mesh culled from the
/// local player's first-person camera (render layers) is also dropped from that camera's
/// shadow pass, so hiding your own hair made your own first-person shadow bald. But the hair
/// still has to be hidden from that camera, and still has to cast — for your shadow, for the
/// mirror's shadow, and for everyone else.
///
/// The twin solves the split: it is a `CastShadow = ShadowsOnly` copy on a layer no player
/// camera culls. ShadowsOnly geometry is excluded from every camera's *color* pass (so it is
/// never seen directly, not even in mirrors — the original mesh is what mirrors show) but it
/// IS evaluated by shadow passes, and shadow passes cull by the rendering camera's mask —
/// which keeps the twin's layer. Result: the first-person camera sees no hair, and every
/// shadow in the scene — including the one that camera itself renders — keeps the full
/// silhouette. Other cameras' shadow passes get the same silhouette twice at identical depth,
/// which is a no-op.
///
/// Twins share the source's Mesh, Skin, Skeleton path and materials, so animation and toon
/// alpha-scissor shadowing behave identically. Idempotent per mesh (meta-flagged).
public static class ShadowTwin
{
    /// Ensure a twin exists for `src`, casting on `layer` (a layer no player camera culls —
    /// pass `LocalPlayer.FpAvatarLayer`). Returns the twin.
    public static MeshInstance3D Ensure(MeshInstance3D src, uint layer)
    {
        // HasMeta first: GetMeta logs an error for a missing key even when a default is supplied.
        if (src.HasMeta("serika_shadow_twin") &&
            src.GetMeta("serika_shadow_twin").As<MeshInstance3D>() is { } existing &&
            GodotObject.IsInstanceValid(existing))
            return existing;

        var twin = new MeshInstance3D
        {
            Name = src.Name + "ShadowTwin",
            Mesh = src.Mesh,
            Skin = src.Skin,
            Layers = layer,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly,
        };

        // Carry the toon-shaded overrides so the twin's shadow alpha (hair-card scissoring)
        // matches what the visible mesh casts.
        for (int s = 0; s < src.Mesh.GetSurfaceCount(); s++)
            twin.SetSurfaceOverrideMaterial(s, src.GetSurfaceOverrideMaterial(s));
        if (src.MaterialOverride != null) twin.MaterialOverride = src.MaterialOverride;

        // Parented UNDER the source, at identity, so Godot's visibility inheritance does the
        // bookkeeping: a mesh switched off by an avatar toggle takes its shadow with it. As a
        // sibling the twin outlived its source's Visible=false and kept casting a garment the
        // player had removed. The skeleton path has to be re-derived for the deeper node.
        AvatarCopy.Attach(src, twin);
        src.SetMeta("serika_shadow_twin", twin);
        return twin;
    }
}
