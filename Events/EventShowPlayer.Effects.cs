using System;
using System.Collections.Generic;
using Godot;
namespace SerikaSocial.Events;

public partial class EventShowPlayer
{
    private sealed class ShowEffectSurface
    {
        public MeshInstance3D Mesh;
        public Material Original;
        public bool Visible;
        public float CullMargin;
        public GeometryInstance3D.ShadowCastingSetting Shadow;
        public ShaderMaterial Material;
        public string Kind;
        public int Group, Index;
    }
    private readonly List<ShowEffectSurface> _showEffectSurfaces = new();
    private sealed class ShowFireLight
    {
        public OmniLight3D Light;
        public int Group;
        public Color Color;
        public float Energy, Range;
        public uint Mask;
        public bool Visible, Shadow;
    }
    private readonly List<ShowFireLight> _showFireLights = new();
    public int EffectNodeCount => _showEffectSurfaces.Count;
    // Counts are active emitter meshes, not individual rendered cards.
    public int ActiveFireCount { get; private set; }
    public int ActiveSmokeCount { get; private set; }
    public int ActiveSparkCount { get; private set; }
    public int ActiveFireLightCount { get; private set; }
    public float SkySparkleStrength { get; private set; }
    private static readonly StringName FxAge = new("event_age"), FxDuration = new("event_duration"),
        FxStrength = new("strength"), FxSeed = new("cue_seed"), FxTime = new("show_time");

    private void SetupShowEffects(Node3D world)
    {
        RestoreShowEffects();
        var fire = GD.Load<Shader>("res://Shaders/concert_fire.gdshader");
        var smoke = GD.Load<Shader>("res://Shaders/concert_smoke.gdshader");
        var sparkle = GD.Load<Shader>("res://Shaders/concert_sparkle.gdshader");
        foreach (Node node in world.FindChildren("SERIKA_EVENT_FX_*", "MeshInstance3D", true, false)) {
            var mesh = (MeshInstance3D)node;
            var parts = mesh.Name.ToString().Split('_');
            if (parts.Length != 6 || !int.TryParse(parts[4], out int group) || !int.TryParse(parts[5], out int index)) continue;
            string kind = parts[3].ToLowerInvariant();
            if (kind is not ("fire" or "smoke" or "sparks" or "sky")) continue;
            var mat = new ShaderMaterial { Shader = kind == "fire" ? fire : kind == "smoke" ? smoke : sparkle };
            mat.SetShaderParameter(FxStrength, 0f);
            if (kind is "sparks" or "sky") mat.SetShaderParameter("sky_mode", kind == "sky");
            _showEffectSurfaces.Add(new ShowEffectSurface {
                Mesh=mesh, Original=mesh.MaterialOverride, Visible=mesh.Visible, Shadow=mesh.CastShadow, CullMargin=mesh.ExtraCullMargin,
                Material=mat, Kind=kind, Group=group, Index=index
            });
            mesh.MaterialOverride=mat;
            mesh.CastShadow=GeometryInstance3D.ShadowCastingSetting.Off;
            if (kind is "smoke" or "sparks") mesh.ExtraCullMargin=18;
            else if (kind=="sky") mesh.ExtraCullMargin=3;
            mesh.Visible=false;
        }
        foreach (Node node in world.FindChildren("SERIKA_EVENT_FX_LIGHT_*", "OmniLight3D", true, false)) {
            var light=(OmniLight3D)node;
            var parts=light.Name.ToString().Split('_');
            if (parts.Length!=6 || !int.TryParse(parts[4],out int group)) continue;
            _showFireLights.Add(new ShowFireLight { Light=light,Group=group,Color=light.LightColor,
                Energy=light.LightEnergy,Range=light.OmniRange,Mask=light.LightCullMask,
                Visible=light.Visible,Shadow=light.ShadowEnabled });
            light.Visible=false; light.ShadowEnabled=false; light.LightCullMask=1u<<17;
            light.OmniRange=7; light.LightColor=new Color(1f,.37f,.075f);
        }
    }

    private static ShowEffectKey ActiveShowEffect(ShowConfig config, string kind, int group, double seconds)
    {
        ShowEffectKey selected=null;
        if (config?.Effects==null) return null;
        foreach (var cue in config.Effects) {
            if (cue.Kind!=kind || (cue.Group!=0 && cue.Group!=group)
                || !double.IsFinite(cue.Time) || !float.IsFinite(cue.Duration) || cue.Duration<=0
                || !float.IsFinite(cue.Strength) || seconds<cue.Time || seconds>=cue.Time+cue.Duration) continue;
            if (selected==null || cue.Time>=selected.Time) selected=cue;
        }
        return selected;
    }

    private void UpdateShowEffects(double seconds, bool performance, bool highQuality, bool runCinematicFx)
    {
        ActiveFireCount=ActiveSmokeCount=ActiveSparkCount=0;
        ActiveFireLightCount=0;
        SkySparkleStrength=0;
        var config=_state?.Config;
        double reveal=config?.RevealTime ?? 0;
        bool hasTime = double.IsFinite(seconds);
        if (performance && hasTime && reveal>.5 && seconds>=.5 && seconds<reveal+2) {
            float rise=Mathf.Pow((float)Math.Clamp((seconds-.5)/(reveal-.5),0,1),2.2f);
            float fade=1-ShowTimeline.Ease((float)Math.Clamp((seconds-reveal)/2,0,1));
            SkySparkleStrength=rise*fade;
        }

        if (!highQuality && !runCinematicFx) {
            // Keep sky sparkle alive without evaluating every FX cue every frame.
            foreach (var surface in _showEffectSurfaces) {
                if (!IsInstanceValid(surface.Mesh) || !hasTime) continue;
                if (surface.Kind == "sky") {
                    surface.Mesh.Visible = SkySparkleStrength>.001f;
                    surface.Material.SetShaderParameter(FxStrength,SkySparkleStrength);
                    surface.Material.SetShaderParameter(FxAge, 0f);
                    surface.Material.SetShaderParameter(FxDuration, 1f);
                    surface.Material.SetShaderParameter(FxSeed, surface.Index*17+surface.Group*71);
                }
                surface.Material.SetShaderParameter(FxTime,(float)seconds);
            }
            foreach (var saved in _showFireLights) {
                if (!IsInstanceValid(saved.Light)) continue;
                saved.Light.Visible=false;
                saved.Light.LightEnergy=0;
            }
            return;
        }

        foreach (var surface in _showEffectSurfaces) {
            if (!IsInstanceValid(surface.Mesh)) continue;
            float strength=0, age=0, duration=1, seed=surface.Index*17+surface.Group*71;
            if (surface.Kind == "sky") {
                strength=SkySparkleStrength;
            } else if (performance && hasTime && config?.Effects != null) {
                // One authored mesh per bank/kind. If cues overlap, the latest active cue wins;
                // evaluating from absolute time makes forward/backward seeks identical.
                ShowEffectKey selected=ActiveShowEffect(config,surface.Kind,surface.Group,seconds);
                if (selected != null) {
                    strength=Math.Clamp(selected.Strength,0,1);
                    age=(float)(seconds-selected.Time);
                    duration=selected.Duration;
                    seed+=(selected.Seed%8191)*.173f;
                }
            }
            surface.Mesh.Visible=strength>.001f;
            surface.Material.SetShaderParameter(FxStrength,strength);
            surface.Material.SetShaderParameter(FxAge,age);
            surface.Material.SetShaderParameter(FxDuration,duration);
            surface.Material.SetShaderParameter(FxSeed,seed);
            surface.Material.SetShaderParameter(FxTime,(float)(hasTime?seconds:0));
            if (!surface.Mesh.Visible) continue;
            if (surface.Kind=="fire") ActiveFireCount++;
            else if (surface.Kind=="smoke") ActiveSmokeCount++;
            else if (surface.Kind=="sparks") ActiveSparkCount++;
        }
        foreach (var saved in _showFireLights) {
            if (!IsInstanceValid(saved.Light)) continue;
            float gate=0;
            var cue=performance && hasTime ? ActiveShowEffect(config,"fire",saved.Group,seconds):null;
            if (cue!=null) {
                float age=(float)(seconds-cue.Time);
                // Match the cubic smoothstep attack/release used by the jet shader.
                gate=Mathf.SmoothStep(0,1,Math.Clamp(age/Math.Min(.13f,cue.Duration*.2f),0,1))
                    *(1-Mathf.SmoothStep(0,1,Math.Clamp((age-cue.Duration*.62f)
                        /(cue.Duration*.38f),0,1)))
                    *Math.Clamp(cue.Strength,0,1);
            }
            saved.Light.LightEnergy=gate*6;
            saved.Light.Visible=gate>.001f;
            if(saved.Light.Visible) ActiveFireLightCount++;
        }
    }

    private void RestoreShowEffects()
    {
        foreach (var surface in _showEffectSurfaces) if (IsInstanceValid(surface.Mesh)) {
            surface.Mesh.MaterialOverride=surface.Original;
            surface.Mesh.Visible=surface.Visible;
            surface.Mesh.CastShadow=surface.Shadow;
            surface.Mesh.ExtraCullMargin=surface.CullMargin;
        }
        _showEffectSurfaces.Clear();
        foreach (var saved in _showFireLights) if (IsInstanceValid(saved.Light)) {
            saved.Light.LightColor=saved.Color; saved.Light.LightEnergy=saved.Energy;
            saved.Light.OmniRange=saved.Range; saved.Light.LightCullMask=saved.Mask;
            saved.Light.Visible=saved.Visible; saved.Light.ShadowEnabled=saved.Shadow;
        }
        _showFireLights.Clear();
        ActiveFireCount=ActiveSmokeCount=ActiveSparkCount=0;
        ActiveFireLightCount=0;
        SkySparkleStrength=0;
    }
}
