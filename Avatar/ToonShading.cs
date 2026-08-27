using Godot;

namespace SerikaSocial.Avatar;

/// Re-materialises imported avatars and worlds with Serika's cel/toon look.
///
/// VRM and PMX both import as flat `StandardMaterial3D` (glTF PBR) — physically-based, smoothly
/// lit, and completely wrong for the anime characters and stylised worlds this app is built
/// around. This walks a freshly-imported subtree and, for every mesh surface, reads the source
/// material's albedo texture, colour and transparency and rebuilds it as a `ShaderMaterial` on
/// `toon_character.gdshader` — banded lighting, cool shadow tint, rim light — with an inverted-
/// hull outline pass for avatars.
///
/// Everything the toon look needs is recovered from the imported material, so it works uniformly
/// on VRM, PMX and plain-GLB worlds without any format-specific knowledge. It is applied once at
/// load, on the surface override slot, so the first-person head-cull layers and the spring-bone
/// system (which touch nodes and skeletons, not materials) are unaffected.
public static class ToonShading
{
    private static Shader _toon;
    private static Shader _toonAvatar; // backface-culled variant — see ApplyToAvatar
    private static Shader _outline;

    private static Shader Toon => _toon ??= GD.Load<Shader>("res://Shaders/toon_character.gdshader");
    private static Shader ToonAvatar => _toonAvatar ??=
        GD.Load<Shader>("res://Shaders/toon_character_avatar.gdshader");
    private static Shader Outline => _outline ??= GD.Load<Shader>("res://Shaders/outline.gdshader");

    /// Global switch. Off falls back to whatever the mesh imported with (flat PBR).
    public static bool Enabled { get; set; } = true;

    /// Toon-shade an avatar: banded lighting plus an outline. `outlineWidth` scales the hull
    /// expansion; 0 disables the outline (worlds pass 0).
    ///
    /// Avatars use the BACKFACE-CULLED shader variant (toon_character_avatar.gdshader). The
    /// base shader is double-sided (`cull_disabled`) because imported MMD/VRM clothing is often
    /// authored to be seen from both sides — correct for worlds you orbit freely, disastrous
    /// for a first-person camera that sits exactly at the eyes: pitching down or up filled the
    /// view with the lit interior of your own torso or skull ("I see my own body internally /
    /// the inside of my head"). Front-faces-only rendering removes those interior shells for
    /// the local player while the exterior look, alpha-scissor hair shadows and outlines are
    /// unchanged. Note this affects EVERY viewer of an avatar in third person too — a garment
    /// viewed from its un-authored side becomes see-through instead of showing a shaded
    /// interior; that trade is standard character rendering.
    public static void ApplyToAvatar(Node model, float outlineWidth = 1.4f)
        => Apply(model, outlineWidth, ToonAvatar);

    /// Whether *worlds* get the cel treatment. Off by default — see `ApplyToWorld`.
    public static bool WorldsEnabled { get; set; }

    /// Toon-shade a world. No outline by default — an outline on every wall and prop reads as
    /// noise, where on a character it reads as line art.
    ///
    /// Disabled by default, because the cel ramp and a room are a bad match. Banding N·L into
    /// three steps and disabling specular is exactly what you want on a character's cheek and
    /// exactly what you do not want on a wall: every lamp's falloff collapses into a flat disc,
    /// so a sconce stops looking like a light and starts looking like someone painted a circle
    /// behind it. Characters keep the cel look; the room they stand in gets real light, real
    /// specular and real shadows — which is the standard anime-character-in-a-lit-set approach.
    public static void ApplyToWorld(Node root, float outlineWidth = 0f)
    {
        if (!WorldsEnabled) return;
        Apply(root, outlineWidth, Toon);
    }

    private static void Apply(Node node, float outlineWidth, Shader shader)
    {
        if (!Enabled || !UI.DeviceProfile.ToonShading || node == null) return;
        if (!UI.DeviceProfile.AvatarOutline) outlineWidth = 0f;
        if (shader == null) { GD.PrintErr("ToonShading: shader failed to load"); return; }

        int count = 0;
        Walk(node, outlineWidth, shader, ref count);
        if (count > 0)
            GD.Print($"ToonShading: restyled {count} surface(s)"
                     + (outlineWidth > 0f ? " with outline" : ""));
    }

    private static void Walk(Node node, float outlineWidth, Shader shader, ref int count)
    {
        if (node is MeshInstance3D mi && mi.Mesh != null)
            count += Restyle(mi, outlineWidth, shader);

        foreach (var child in node.GetChildren())
            Walk(child, outlineWidth, shader, ref count);
    }

    private static int Restyle(MeshInstance3D mi, float outlineWidth, Shader shader)
    {
        int surfaces = mi.Mesh.GetSurfaceCount();
        int done = 0;

        for (int s = 0; s < surfaces; s++)
        {
            // Prefer whatever is actually active on the surface (an override wins over the mesh's
            // own material), so a re-style is idempotent-ish and picks up glTF materials.
            var src = mi.GetActiveMaterial(s) as BaseMaterial3D;
            if (src == null) continue;

            var toon = new ShaderMaterial { Shader = shader };

            var tex = src.AlbedoTexture;
            toon.SetShaderParameter("has_texture", tex != null);
            if (tex != null) toon.SetShaderParameter("albedo_texture", tex);
            toon.SetShaderParameter("albedo_color", src.AlbedoColor);

            // Carry transparency across. VRM/PMX hair and clothing arrive as alpha-scissor
            // (cutout) or alpha-blend; either way we scissor, which sorts cleanly with the
            // depth-prepass and avoids the sorting halos blend produces on layered hair.
            // The 0.4 default (not 0.5) keeps wispy strand pixels casting: at 0.5 the
            // semi-transparent fringe of a hair card drops out of the SHADOW map entirely and
            // a full head of hair reads as a bald scalp in its own ground silhouette.
            float scissor = 0f;
            if (src.Transparency == BaseMaterial3D.TransparencyEnum.AlphaScissor)
                scissor = Mathf.Min(src.AlphaScissorThreshold, 0.45f);
            else if (src.Transparency == BaseMaterial3D.TransparencyEnum.Alpha ||
                     src.Transparency == BaseMaterial3D.TransparencyEnum.AlphaDepthPrePass)
                scissor = 0.4f;
            toon.SetShaderParameter("alpha_scissor", scissor);

            if (outlineWidth > 0f)
            {
                var outline = new ShaderMaterial { Shader = Outline };
                outline.SetShaderParameter("outline_width", outlineWidth);
                outline.SetShaderParameter("has_texture", tex != null);
                if (tex != null) outline.SetShaderParameter("albedo_texture", tex);
                outline.SetShaderParameter("alpha_scissor", scissor);
                toon.NextPass = outline;
            }

            mi.SetSurfaceOverrideMaterial(s, toon);
            done++;
        }
        return done;
    }
}
