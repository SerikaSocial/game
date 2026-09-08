using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;

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
    private static Shader _concert;
    private static Shader _concertOutline;

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

    /// Concert-only material treatment. The original VRM remains the portable source of truth:
    /// recover its authored MToon face/hair ramp and shade colours, then apply light-energy-aware
    /// stage shading. Ordinary avatars, first-person proxies and worlds keep their existing look.
    public static void ApplyToConcertAvatar(Node model, byte[] skaBytes)
    {
        if (!Enabled || !UI.DeviceProfile.ToonShading || model == null) return;
        _concert ??= GD.Load<Shader>("res://Shaders/concert_performer.gdshader");
        _concertOutline ??= GD.Load<Shader>("res://Shaders/concert_outline.gdshader");
        if (_concert == null || _concertOutline == null) return;
        var authored = ReadConcertMaterials(skaBytes);
        int count = 0, outlines = 0, matched = 0;
        void Visit(Node node)
        {
            // A copied first-person/shadow representation must not be styled or counted again.
            if (node.HasMeta("serika_avatar_copy")) return;
            if (node is MeshInstance3D mesh && mesh.Mesh != null)
            {
                for (int s = 0; s < mesh.Mesh.GetSurfaceCount(); s++)
                {
                    // FromBytes has already installed the general toon override. Read the original
                    // mesh surface, whose imported material still owns the name, texture and UVs.
                    if (mesh.Mesh.SurfaceGetMaterial(s) is not BaseMaterial3D src) continue;
                    string name = src.ResourceName;
                    bool hair = name.Contains("HAIR", StringComparison.OrdinalIgnoreCase);
                    bool skin = name.Contains("SKIN", StringComparison.OrdinalIgnoreCase);
                    bool face = name.Contains("Face_", StringComparison.OrdinalIgnoreCase);
                    bool detail = name.Contains("EYE", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Brow", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Mouth", StringComparison.OrdinalIgnoreCase);
                    authored.TryGetValue(MaterialKey(name), out var settings);
                    if (settings != null) matched++;
                    var shade = settings?.Shade ?? (skin ? new Color(1, .88f, .85f) : new Color(.72f, .80f, .94f));
                    var mat = new ShaderMaterial { Shader = _concert, ResourceName = name + " / concert" };
                    mat.SetShaderParameter("albedo_color", src.AlbedoColor);
                    mat.SetShaderParameter("has_texture", src.AlbedoTexture != null);
                    if (src.AlbedoTexture != null) mat.SetShaderParameter("albedo_texture", src.AlbedoTexture);
                    mat.SetShaderParameter("uv_scale", new Vector2(src.Uv1Scale.X, src.Uv1Scale.Y));
                    mat.SetShaderParameter("uv_offset", new Vector2(src.Uv1Offset.X, src.Uv1Offset.Y));
                    float cutout = src.Transparency == BaseMaterial3D.TransparencyEnum.Disabled ? 0 :
                        src.Transparency == BaseMaterial3D.TransparencyEnum.AlphaScissor ?
                            Mathf.Min(src.AlphaScissorThreshold, .45f) : detail ? .25f : .4f;
                    mat.SetShaderParameter("alpha_scissor", cutout);
                    mat.SetShaderParameter("shade_color", shade);
                    mat.SetShaderParameter("shade_shift", settings?.Shift ?? (face || detail ? -.8f : 0));
                    mat.SetShaderParameter("shade_toony", settings?.Toony ?? (hair ? .6f : .8f));
                    mat.SetShaderParameter("shade_depth", skin || detail ? .84f : .65f);
                    mat.SetShaderParameter("light_wrap", face || detail ? .6f : .22f);
                    mat.SetShaderParameter("highlight_strength", hair ? .07f : skin || detail ? 0 : .018f);
                    mat.SetShaderParameter("rim_strength", hair ? .16f : skin || detail ? .025f : .055f);
                    mat.SetShaderParameter("surface_roughness", hair ? .52f : .85f);
                    // The face normals carry enough form already; importing its authored normal
                    // map at full strength turns a small anime nose into a hard, mottled wedge.
                    bool normal = hair && src.NormalEnabled && src.NormalTexture != null;
                    mat.SetShaderParameter("has_normal_texture", normal);
                    if (normal) mat.SetShaderParameter("normal_texture", src.NormalTexture);
                    mat.SetShaderParameter("normal_strength", .2f);
                    // Layered hair cards already have authored strand/highlight artwork. Hulls
                    // on each card create crawling dark pinstripes at their shared crown roots.
                    if (UI.DeviceProfile.AvatarOutline && !detail && !hair && (settings?.Outline ?? true))
                    {
                        var outline = new ShaderMaterial { Shader = _concertOutline };
                        outline.SetShaderParameter("outline_pixels", face ? .35f : .65f);
                        outline.SetShaderParameter("outline_color", skin ? new Color(.23f,.12f,.17f) : new Color(.07f,.07f,.105f));
                        outline.SetShaderParameter("has_texture", src.AlbedoTexture != null);
                        if (src.AlbedoTexture != null) outline.SetShaderParameter("albedo_texture", src.AlbedoTexture);
                        outline.SetShaderParameter("alpha_scissor", cutout);
                        outline.SetShaderParameter("uv_scale", new Vector2(src.Uv1Scale.X, src.Uv1Scale.Y));
                        outline.SetShaderParameter("uv_offset", new Vector2(src.Uv1Offset.X, src.Uv1Offset.Y));
                        mat.NextPass = outline; outlines++;
                    }
                    mesh.SetSurfaceOverrideMaterial(s, mat); count++;
                }
            }
            foreach (Node child in node.GetChildren()) Visit(child);
        }
        Visit(model);
        GD.Print($"Concert shading: {count} surfaces, {outlines} restrained outlines, {matched}/{authored.Count} authored MToon materials matched; renderer={RenderingServer.GetCurrentRenderingMethod()}");
    }

    private sealed record ConcertMaterial(Color Shade, float Shift, float Toony, bool Outline);
    private static string MaterialKey(string value) => value.Replace(" (Instance)", "").Replace("_Instance_", "").Replace(" ", "").ToLowerInvariant();
    private static Dictionary<string, ConcertMaterial> ReadConcertMaterials(byte[] skaBytes)
    {
        var result = new Dictionary<string, ConcertMaterial>();
        if (skaBytes == null) return result;
        try
        {
            byte[] glb = SkaFile.Parse(skaBytes).Glb;
            if (glb.Length < 20) return result;
            int length = checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12, 4)));
            using var json = JsonDocument.Parse(glb.AsMemory(20, length));
            if (!json.RootElement.TryGetProperty("extensions", out var extensions) ||
                !extensions.TryGetProperty("VRM", out var vrm) ||
                !vrm.TryGetProperty("materialProperties", out var materials)) return result;
            foreach (var material in materials.EnumerateArray())
            {
                var f = material.GetProperty("floatProperties");
                var v = material.GetProperty("vectorProperties");
                float Float(string key, float fallback) => f.TryGetProperty(key, out var n) ? n.GetSingle() : fallback;
                var shade = v.GetProperty("_ShadeColor");
                result[MaterialKey(material.GetProperty("name").GetString() ?? "")] = new ConcertMaterial(
                    new Color(shade[0].GetSingle(), shade[1].GetSingle(), shade[2].GetSingle()),
                    Mathf.Clamp(Float("_ShadeShift", 0), -1, 1), Mathf.Clamp(Float("_ShadeToony", .8f), 0, 1), Float("_OutlineWidthMode", 0) > 0);
            }
        }
        catch (Exception e) { GD.PrintErr($"Concert shading: MToon metadata unavailable ({e.Message}); using material-role defaults."); }
        return result;
    }

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
