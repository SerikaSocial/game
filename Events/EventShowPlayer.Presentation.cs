using System;
using System.Collections.Generic;
using Godot;
namespace SerikaSocial.Events;

public partial class EventShowPlayer
{
    private SubViewport _introViewport;
    private VideoStreamPlayer _introPlayer;
    private Material _introPortrait, _introLandscape;
    private bool? _presentationLive;
    private bool _compatibilityLighting;
    private bool _mobileLighting;
    private bool? _revealedPresentation;
    private readonly Material _blackoutBackdrop = new StandardMaterial3D { ShadingMode=BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor=Colors.Black };
    private readonly List<(Godot.Environment Environment, float Ambient)> _showEnvironments = new();
    private readonly List<(DirectionalLight3D Light, float Energy, bool Shadow)> _moonLights = new();
    public bool EntranceConcealed { get; private set; }
    public float RevealLevel { get; private set; } = 1;
    private readonly List<(MeshInstance3D Mesh, Material Material)> _backdrops = new();
    private readonly List<(Node3D Node, bool Visible)> _backdropArt = new();
    /// A stage light plus everything needed to drive it and to put it back.
    ///
    /// This used to be a tuple and the update loop wrote all fourteen properties to every light
    /// on every frame, unconditionally — cull mask, colour, energy (twice), position, spot angle,
    /// shadow flags, both biases. Each write crosses into the rendering server, and a cull-mask
    /// write re-pairs the light against scene geometry. Measured on the real venue: 13 show
    /// lights x 14 writes x 60 fps is ~11k binding calls a second for values that are constant
    /// for seconds at a time. That is worth removing, but be honest about the size of it — it is
    /// NOT why a GTX 1050 Ti runs this venue at 35 fps. Writes are now change-guarded and the
    /// per-frame constants (name tests, `OS.HasFeature`) resolved once, here.
    private sealed class ShowLight
    {
        public Light3D Light;
        public SpotLight3D Spot;          // cached cast; the per-frame `is` test was not free
        public Color Color;
        public float Energy;
        public Vector3 Position;
        public bool Key, Rim, Hall, Shadow;
        public Basis Basis;
        public uint Mask;
        public float Angle, Range, AngleAttenuation, Bias, NormalBias;
        public bool StageFront, Moonlight;   // were string comparisons, every light, every frame
    }
    private readonly List<ShowLight> _showLights = new();

    // Write only on change. Reading a property is a binding call; WRITING one is a binding call
    // plus rendering-server work, and for the cull mask a pair/unpair cycle. The show holds most
    // of these constant for seconds at a time, so the guard elides almost all of them.
    private static void SetMask(Light3D l, uint v) { if (l.LightCullMask != v) l.LightCullMask = v; }
    private static void SetColor(Light3D l, Color v) { if (l.LightColor != v) l.LightColor = v; }
    private static void SetEnergy(Light3D l, float v) { if (!Mathf.IsEqualApprox(l.LightEnergy, v)) l.LightEnergy = v; }
    private static void SetShadow(Light3D l, bool v) { if (l.ShadowEnabled != v) l.ShadowEnabled = v; }
    private static void SetPosition(Node3D n, Vector3 v) { if (!n.GlobalPosition.IsEqualApprox(v)) n.GlobalPosition = v; }
    private static void SetBias(Light3D l, float bias, float normalBias)
    {
        if (!Mathf.IsEqualApprox(l.ShadowBias, bias)) l.ShadowBias = bias;
        if (!Mathf.IsEqualApprox(l.ShadowNormalBias, normalBias)) l.ShadowNormalBias = normalBias;
    }
    private static void SetSpot(SpotLight3D s, float angle, float attenuation, float range)
    {
        if (s == null) return;
        if (!Mathf.IsEqualApprox(s.SpotAngle, angle)) s.SpotAngle = angle;
        if (!Mathf.IsEqualApprox(s.SpotAngleAttenuation, attenuation)) s.SpotAngleAttenuation = attenuation;
        if (!Mathf.IsEqualApprox(s.SpotRange, range)) s.SpotRange = range;
    }
    private readonly List<(MeshInstance3D Mesh, Material Original, ShaderMaterial Material, Vector3 Rotation, bool Visible)> _beams = new();
    private readonly List<(MeshInstance3D Mesh, Material Original, StandardMaterial3D Material)> _practicals = new();
    public float BeatStrength { get; private set; }
    public float MouthOpening { get; private set; }
    private readonly List<(Node3D Node, bool Visible)> _microphones = new();
    public bool IntroIsPlaying => _introPlayer?.IsPlaying() ?? false;

    private void SetupPresentation(Node3D world, string introPath)
    {
        _compatibilityLighting = RenderingServer.GetCurrentRenderingMethod() == "gl_compatibility";
        foreach (Node node in world.FindChildren("*", "WorldEnvironment", true, false))
            if (node is WorldEnvironment environment && environment.Environment != null)
                _showEnvironments.Add((environment.Environment,environment.Environment.AmbientLightEnergy));
        foreach (Node node in world.FindChildren("*", "DirectionalLight3D", true, false))
            if (node is DirectionalLight3D moon) _moonLights.Add((moon,moon.LightEnergy,moon.ShadowEnabled));
        // Every name test and platform query is resolved HERE, once. They used to run per light
        // per frame inside the update loop.
        bool mobile = OS.HasFeature("mobile");
        foreach (Node node in world.FindChildren("*", "Light3D", true, false)) {
            if (node is not Light3D light || light is DirectionalLight3D) continue;
            string lightName = light.Name.ToString();
            if (lightName.StartsWith("SERIKA_EVENT_SPOT") || lightName.StartsWith("SERIKA_EVENT_FX_LIGHT")) continue;
            var spot = light as SpotLight3D;
            _showLights.Add(new ShowLight {
                Light = light, Spot = spot,
                Color = light.LightColor, Energy = light.LightEnergy, Position = light.GlobalPosition,
                Key = lightName.StartsWith("Performer key"),
                Rim = lightName.StartsWith("Side key"),
                Hall = lightName.StartsWith("SERIKA_EVENT_HALL") || lightName.Contains("Audience") || lightName == "Moonlight",
                StageFront = lightName == "Stage front light",
                Moonlight = lightName == "Moonlight",
                Shadow = light.ShadowEnabled, Basis = light.GlobalBasis, Mask = light.LightCullMask,
                Angle = spot?.SpotAngle ?? 0, Range = spot?.SpotRange ?? 0,
                AngleAttenuation = spot?.SpotAngleAttenuation ?? 0,
                Bias = light.ShadowBias, NormalBias = light.ShadowNormalBias,
            });
        }
        _mobileLighting = mobile;
        var beamShader = new Shader { Code = @"shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled, shadows_disabled;
uniform vec3 beam_color : source_color = vec3(.25,.55,1.0);
uniform float strength = 1.0;
void fragment() { ALBEDO=beam_color; ALPHA=.012*strength*pow(1.0-UV.y,1.5)*pow(abs(dot(NORMAL,VIEW)),1.5); }" };
        foreach (Node node in world.FindChildren("*", "Node3D", true, false)) {
            if (node is MeshInstance3D mesh && mesh.Name.ToString().StartsWith("SERIKA_EVENT_BEAM") && !mesh.Name.ToString().StartsWith("SERIKA_EVENT_BEAM_SOFT")) {
                var material = new ShaderMaterial { Shader = beamShader };
                _beams.Add((mesh,mesh.MaterialOverride,material,mesh.Rotation,mesh.Visible)); mesh.MaterialOverride=material;
            }
            if (node is MeshInstance3D led && (led.Name.ToString().StartsWith("SERIKA_EVENT_PRACTICAL") || led.Name == "Stage lower blue trim")) {
                var material = new StandardMaterial3D { ShadingMode=BaseMaterial3D.ShadingModeEnum.Unshaded,
                    EmissionEnabled=true, CullMode=BaseMaterial3D.CullModeEnum.Disabled };
                _practicals.Add((led,led.MaterialOverride,material));led.MaterialOverride=material;
            }
            if (node is Node3D mic && mic.Name == "SERIKA_EVENT_MIC_STAND") _microphones.Add((mic,mic.Visible));
        }
        SetupShowMicrophone(world);
        SetupConcertRig(world);
        // Skipped outright when effects are off — building the pyro/laser/crowd rigs only to
        // never step them still swaps every one of those materials and costs the shader
        // compilation this option exists to avoid.
        if (_effectsEnabled) { SetupShowEffects(world); SetupShowLasers(world); SetupConcertAudience(world); }
        foreach (Node node in world.FindChildren("*", "Node3D", true, false)) {
            string name = node.Name;
            if (node is MeshInstance3D mesh && name.StartsWith("SERIKA_EVENT_BACKDROP")) _backdrops.Add((mesh, mesh.MaterialOverride));
            if (node is Node3D art && (name.StartsWith("Comet screen graphic") || name == "Artist English name" || name == "Artist Japanese name"))
                _backdropArt.Add((art, art.Visible));
        }
        if (introPath == null) return;
        _introViewport = new SubViewport { Name = "PreshowVideo", Size = new Vector2I(960,540), Disable3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        AddChild(_introViewport);
        _introPlayer = new VideoStreamPlayer { Name = "IntroLoop", Expand = true, Size = new Vector2(960,540),
            Stream = new VideoStreamTheora { File = introPath }, Bus = "Music" };
        _introViewport.AddChild(_introPlayer);
        _introPlayer.Finished += () => { if (_presentationLive == false && IsInsideTree()) _introPlayer.Play(); };
        var landscape = new ShaderMaterial { Shader = new Shader { Code = @"shader_type spatial;
render_mode unshaded, cull_disabled;
uniform sampler2D video_frame : source_color;
void fragment() { vec2 p=UV; p.x=(p.x-.5)/.7296+.5;
ALBEDO=(p.x<0.0 || p.x>1.0) ? vec3(0.0) : texture(video_frame,p).rgb; }" } };
        landscape.SetShaderParameter("video_frame", _introViewport.GetTexture()); _introLandscape=landscape;
        var letterbox = new ShaderMaterial { Shader = new Shader { Code = @"shader_type spatial;
render_mode unshaded, cull_disabled;
uniform sampler2D video_frame : source_color;
void fragment() { vec2 p=UV; p.y=(p.y-.5)/.375+.5;
ALBEDO=(p.y<0.0 || p.y>1.0) ? vec3(0.0) : texture(video_frame,p).rgb; }" } };
        letterbox.SetShaderParameter("video_frame", _introViewport.GetTexture()); _introPortrait = letterbox;
    }
    private void UpdatePresentation(double seconds, bool performance, bool highQuality, double delta, bool runCinematicFx)
    {
        if (_presentationLive != performance) {
            _presentationLive = performance;
            _revealedPresentation = null;
            if (_introPlayer != null) {
                if (performance) _introPlayer.Stop(); else _introPlayer.Play();
                _introViewport.RenderTargetUpdateMode = performance ? SubViewport.UpdateMode.Disabled : SubViewport.UpdateMode.Always;
                foreach (var screen in _screens) screen.Mesh.MaterialOverride = performance
                    ? new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoTexture = _feed.GetTexture(), CullMode = BaseMaterial3D.CullModeEnum.Disabled }
                    : _introPortrait;
            }
            _feed.RenderTargetUpdateMode = performance ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        }
        EvaluateLighting(seconds,performance,out Color color,out float energy);
        var config=_state.Config;
        EntranceConcealed=performance && config.RevealTime>0 && seconds<config.RevealTime;
        RevealLevel=EntranceConcealed?0:performance&&config.RevealTime>0?ShowTimeline.Ease((float)Math.Clamp((seconds-config.RevealTime)/.35,0,1)):1;
        bool revealed=performance&&!EntranceConcealed;
        _artist.Visible=revealed;
        if(_revealedPresentation!=revealed) {
            _revealedPresentation=revealed;
            foreach(var backdrop in _backdrops) backdrop.Mesh.MaterialOverride=performance?(revealed?backdrop.Material:_blackoutBackdrop):(_introLandscape??backdrop.Material);
            foreach(var art in _backdropArt) art.Node.Visible=art.Visible&&(revealed||(!performance&&_introLandscape==null));
        }
        foreach(var environment in _showEnvironments) environment.Environment.AmbientLightEnergy=environment.Ambient*(.015f+.985f*RevealLevel)*(_compatibilityLighting?.30f:1f);
        foreach(var moon in _moonLights) { moon.Light.LightEnergy=moon.Energy*RevealLevel*(_compatibilityLighting?.22f:1f);if(_compatibilityLighting)moon.Light.ShadowEnabled=false; }
        BeatStrength=performance ? ShowTimeline.Envelope(config.Beats,seconds,config.MusicFps) : 0;
        float music=performance ? ShowTimeline.Envelope(config.MusicEnergy,seconds,config.MusicFps) : 0;
        var alternate=color.Lerp(new Color(.42f,.22f,1),.4f);
        // Energy is computed in full and written ONCE. It used to be assigned two or three times
        // per light per frame, and every assignment was a separate trip into the rendering server.
        float rendererExposure=_compatibilityLighting?.28f:1f;
        var keyTint=new Color(1,.93f,.85f);
        var rimTint=color.Lerp(new Color(.42f,.58f,.85f),.3f);
        var hallTint=new Color(.22f,.32f,.52f);
        foreach(var lamp in _showLights) {
            if(!IsInstanceValid(lamp.Light)) continue;
            if(lamp.Key || lamp.Rim) {
                SetMask(lamp.Light, 1u | (1u<<18) | (1u<<17));
                // Neutral follow spots illuminate the singer independently of the show look.
                SetPosition(lamp.Light, lamp.Position+(performance?_artist.GlobalPosition-ShowTimeline.Vector(config.Performer):Vector3.Zero));
                if(lamp.Spot!=null) {
                    lamp.Spot.LookAt(_artist.GlobalPosition+new Vector3(0,1.05f,0));
                    SetSpot(lamp.Spot, lamp.Key?17:24, 1.5f, lamp.Key?18:14);
                }
                SetColor(lamp.Light, lamp.Key?keyTint:rimTint);
                // Actual scene review: Compatibility spots need lower exposure and no artist shadow pass.
                float keyBaseEnergy = lamp.Key ? (lamp.Position.X < 0 ? 18f : 8f) : 7f * energy;
                SetEnergy(lamp.Light, keyBaseEnergy * rendererExposure * (highQuality ? 1f : 0.85f) * RevealLevel);
                SetShadow(lamp.Light, lamp.Key && lamp.Position.X<0 && !_mobileLighting && !_compatibilityLighting);
                SetBias(lamp.Light, .035f, .45f);
            } else {
                SetMask(lamp.Light, lamp.StageFront?1u<<17:1u);
                SetColor(lamp.Light, lamp.Hall?hallTint:color);
                // Navigation stays dim and stable. No whole-venue beat flashing.
                // The directional moon already supplies the night wash; removing its
                // broad point duplicate keeps the audience within the GL Omni budget.
                float e = lamp.Moonlight ? 0f
                    : lamp.Energy * (lamp.Hall ? (performance ? 0.45f : 0.8f)
                        : (performance ? Mathf.Lerp(.28f, .35f, highQuality ? 1 : .85f) * energy : 0.18f))
                      * RevealLevel * (_compatibilityLighting?.35f:1f);
                SetEnergy(lamp.Light, e);
                SetShadow(lamp.Light, false);
            }
        }
        for(int i=0;i<_practicals.Count;i++) {
            var led=_practicals[i];
            var tint=new Color(.12f,.35f,.6f).Lerp(color,.22f);
            led.Material.AlbedoColor=tint*.10f*RevealLevel;
            led.Material.Emission=tint;led.Material.EmissionEnergyMultiplier=.14f*RevealLevel;
        }
        double entranceEnd = _state.Config.PerformerPath?.Count > 0 ? _state.Config.PerformerPath[^1].Time : 0;
        foreach(var mic in _microphones) mic.Node.Visible = mic.Visible && performance && seconds >= entranceEnd;
        // The stand appears when she arrives; a beat later she lifts the microphone off it.
        UpdateShowMicrophone(seconds, performance);
        foreach(var beam in _beams) beam.Mesh.Visible=false; // Legacy decorative beams are retired.
        UpdateConcertRig(seconds,performance,color,music,BeatStrength,highQuality);
        if(_effectsEnabled) {
            UpdateShowEffects(seconds,performance,highQuality,runCinematicFx);
            UpdateShowLasers(seconds,performance,highQuality,runCinematicFx);
            UpdateConcertAudience(seconds,performance,runCinematicFx);
        }
        // The authored vocal envelope opens the actual vowel morphs. Sampling between
        // 25 Hz keys keeps consonant closures visible without 5 Hz stair stepping.
        float level=revealed ? ShowTimeline.Envelope(config.Mouth,seconds,config.MouthFps) : 0;
        float rounded=ShowTimeline.Envelope(config.MouthRound,seconds,config.MouthFps);
        MouthOpening=Mathf.Clamp(level*config.MouthGain,0,1);
        _artist.SetVoiceLipSync(MouthOpening, MouthOpening*(1-.35f*rounded),0,MouthOpening*.30f*rounded,0,0);
        _artist.UpdateFacialDynamics((float)delta);

    }
    private void RestorePresentation()
    {
        _introPlayer?.Stop();
        RestoreShowMicrophone();
        RestoreShowEffects();
        RestoreShowLasers();
        RestoreConcertAudience();
        RestoreConcertRig();
        foreach(var environment in _showEnvironments) environment.Environment.AmbientLightEnergy=environment.Ambient;
        foreach(var moon in _moonLights) if(IsInstanceValid(moon.Light)) { moon.Light.LightEnergy=moon.Energy;moon.Light.ShadowEnabled=moon.Shadow; }
        _showEnvironments.Clear();_moonLights.Clear();
        foreach(var led in _practicals) if(IsInstanceValid(led.Mesh)) led.Mesh.MaterialOverride=led.Original;
        foreach(var beam in _beams) if(IsInstanceValid(beam.Mesh)) { beam.Mesh.MaterialOverride=beam.Original; beam.Mesh.Rotation=beam.Rotation;beam.Mesh.Visible=beam.Visible; }
        foreach(var mic in _microphones) if(IsInstanceValid(mic.Node)) mic.Node.Visible=mic.Visible;
        foreach(var backdrop in _backdrops) if(IsInstanceValid(backdrop.Mesh)) backdrop.Mesh.MaterialOverride=backdrop.Material;
        foreach(var art in _backdropArt) if(IsInstanceValid(art.Node)) art.Node.Visible=art.Visible;
        // Change-guarded, like the update loop. Restoring unconditionally re-paired every light
        // against scene geometry and tripped `geom->softshadow_count==0 - BUG!` once per light,
        // each with a full stack trace — 448 of them, 1.5 MB of log, in a single teardown.
        foreach(var lamp in _showLights) if(IsInstanceValid(lamp.Light) && lamp.Light.IsInsideTree()) {
            SetColor(lamp.Light, lamp.Color);
            SetEnergy(lamp.Light, lamp.Energy);
            SetPosition(lamp.Light, lamp.Position);
            SetShadow(lamp.Light, lamp.Shadow);
            if(!lamp.Light.GlobalBasis.IsEqualApprox(lamp.Basis)) lamp.Light.GlobalBasis=lamp.Basis;
            SetMask(lamp.Light, lamp.Mask);
            SetBias(lamp.Light, lamp.Bias, lamp.NormalBias);
            SetSpot(lamp.Spot, lamp.Angle, lamp.AngleAttenuation, lamp.Range);
        }
    }
}
