using System;
using System.Collections.Generic;
using Godot;
namespace SerikaSocial.Events;

public partial class EventShowPlayer
{
    private sealed class ConcertLaserRay
    {
        public MeshInstance3D Mesh;
        public Material OriginalMaterial;
        public ShaderMaterial Material;
        public Transform3D OriginalTransform;
        public bool Visible;
        public GeometryInstance3D.ShadowCastingSetting Shadow;
        public int Fixture, Index;
    }
    private sealed class ConcertLaserLens
    {
        public MeshInstance3D Mesh;
        public Material Original;
        public StandardMaterial3D Material;
        public int Fixture;
    }
    private readonly List<ConcertLaserRay> _concertLaserRays = new();
    private readonly List<ConcertLaserLens> _concertLaserLenses = new();
    public int LaserFixtureCount { get; private set; }
    public int ActiveLaserRayCount { get; private set; }
    public float LaserStrength { get; private set; }
    public float MinimumLaserHeight { get; private set; }
    public string LaserPattern { get; private set; } = "off";
    private static readonly StringName LaserColorParameter=new("laser_color"), LaserStrengthParameter=new("beam_strength");
    private readonly struct LaserCueState
    {
        public readonly double Time;
        public readonly float Duration, Strength;
        public readonly int Group;
        public readonly string Pattern;
        public LaserCueState(double time,float duration,float strength,int group,string pattern)
        { Time=time;Duration=duration;Strength=strength;Group=group;Pattern=pattern; }
    }

    private void SetupShowLasers(Node3D world)
    {
        RestoreShowLasers();
        var shader=GD.Load<Shader>("res://Shaders/concert_laser.gdshader");
        var fixtures=new HashSet<int>();
        foreach(Node node in world.FindChildren("SERIKA_EVENT_LASER_RAY_*","MeshInstance3D",true,false)) {
            var mesh=(MeshInstance3D)node;var parts=mesh.Name.ToString().Split('_');
            if(parts.Length!=6 || !int.TryParse(parts[4],out int fixture) || !int.TryParse(parts[5],out int index)
                || fixture is <0 or >3 || index is <0 or >8) { GD.PushError($"Noncanonical concert laser ID: {mesh.Name}");continue; }
            var material=new ShaderMaterial{Shader=shader};material.SetShaderParameter(LaserStrengthParameter,0f);
            _concertLaserRays.Add(new ConcertLaserRay { Mesh=mesh,OriginalMaterial=mesh.MaterialOverride,
                Material=material,OriginalTransform=mesh.GlobalTransform,Visible=mesh.Visible,
                Shadow=mesh.CastShadow,Fixture=fixture,Index=index });
            fixtures.Add(fixture);mesh.MaterialOverride=material;mesh.Visible=false;
            mesh.CastShadow=GeometryInstance3D.ShadowCastingSetting.Off;
        }
        foreach(Node node in world.FindChildren("SERIKA_EVENT_LASER_LENS_*","MeshInstance3D",true,false)) {
            var mesh=(MeshInstance3D)node;var parts=mesh.Name.ToString().Split('_');
            if(parts.Length!=5 || !int.TryParse(parts[4],out int fixture) || fixture is <0 or >3)continue;
            var material=new StandardMaterial3D { ShadingMode=BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor=Colors.Black,EmissionEnabled=true,Emission=Colors.Black,EmissionEnergyMultiplier=0 };
            _concertLaserLenses.Add(new ConcertLaserLens { Mesh=mesh,Original=mesh.MaterialOverride,Material=material,Fixture=fixture });
            mesh.MaterialOverride=material;
        }
        LaserFixtureCount=fixtures.Count;
    }

    private static bool LaserGroupContains(int group,int fixture)=>group switch {
        0=>true,1=>fixture<2,2=>fixture>=2,3=>fixture is 0 or 3,_=>false
    };
    private static LaserCueState IntroLaserCue(double seconds,double reveal)
    {
        if(reveal<=0 || seconds<0 || seconds>=reveal)return default;
        double p=seconds/reveal;
        // Sparse operator windows, normalized to the actual first sung verse. Gaps
        // provide darkness between traces; the last fraction blanks before the reveal.
        foreach(var window in IntroLaserWindows) {
            if(p<window.Start || p>=window.End)continue;
            return new LaserCueState(reveal*window.Start,(float)(reveal*(window.End-window.Start)),
                window.Strength,window.Group,window.Pattern);
        }
        return default;
    }
    private static readonly (double Start,double End,float Strength,int Group,string Pattern)[] IntroLaserWindows = {
        (.16,.34,.30f,1,"comet"),(.38,.55,.36f,2,"comet"),
        (.60,.75,.34f,3,"lines"),(.79,.90,.48f,0,"lines"),(.91,.988,.70f,3,"fan")
    };
    private LaserCueState LaserCueAt(double seconds,int fixture)
    {
        var config=_state.Config;
        if(seconds<config.RevealTime) {
            var intro=IntroLaserCue(seconds,config.RevealTime);
            return LaserGroupContains(intro.Group,fixture)?intro:default;
        }
        ShowEffectKey selected=null;
        if(config.Effects!=null)foreach(var cue in config.Effects) {
            if(cue.Kind!="laser" || !LaserGroupContains(cue.Group,fixture) || !double.IsFinite(cue.Time)
                || !float.IsFinite(cue.Duration) || cue.Duration<=0 || !float.IsFinite(cue.Strength)
                || seconds<cue.Time || seconds>=cue.Time+cue.Duration)continue;
            if(selected==null || cue.Time>=selected.Time)selected=cue;
        }
        return selected==null?default:new LaserCueState(selected.Time,selected.Duration,Math.Clamp(selected.Strength,0,1),
            selected.Group,selected.Pattern is "fan" or "comet" ?selected.Pattern:"lines");
    }
    private static float LaserShutter(string pattern,int index)=>pattern switch {
        "fan"=>.72f+.28f*(1-Math.Abs(index-4)/4f),
        "comet"=>index is 3 or 4 or 5 ? (index==4?1:.34f):0,
        "lines"=>index is 1 or 4 or 7 ?1:0,_=>0
    };
    private static Vector3 LaserTarget(ConcertLaserRay ray,LaserCueState cue,double seconds)
    {
        var origin=ray.OriginalTransform.Origin;
        float u=(ray.Index-4)/4f;
        float age=(float)(seconds-cue.Time);
        float phase=Math.Clamp(age/Math.Max(.05f,cue.Duration),0,1);
        float side=ray.Fixture<2?-1:1;
        if(cue.Pattern=="comet") {
            float t=Math.Clamp(.10f+phase*.72f+ray.Fixture*.045f+(ray.Index-4)*.014f,0,1);
            return new Vector3(-27+54*t,26+14*t+15*Mathf.Pow(t,8)+2*Mathf.Sin(t*Mathf.Pi),-24-1.5f*Mathf.Sin(t*Mathf.Pi));
        }
        float travel=Mathf.Sin(age*.40f+ray.Fixture*1.1f)*3;
        return cue.Pattern=="fan"
            ?new Vector3(origin.X*.6f+side*18+u*22+travel,22+u*2+Mathf.Sin(age*.35f+ray.Fixture)*1.3f,94)
            :new Vector3(origin.X+side*5+u*8+travel,20+(ray.Index%3)*.65f+ray.Fixture*.45f,90+ray.Fixture*3);
    }
    private void UpdateShowLasers(double seconds, bool performance, bool highQuality, bool runCinematicFx)
    {
        ActiveLaserRayCount=0;LaserStrength=0;LaserPattern="off";MinimumLaserHeight=0;
        if(!highQuality && !runCinematicFx) return;
        Span<float> lensLevels=stackalloc float[4];
        lensLevels.Clear();
        bool valid=performance && double.IsFinite(seconds) && _state?.Config!=null;
        bool intro=valid&&seconds<_state.Config.RevealTime;
        Color color=new(.035f,.61f,1f);
        if(valid&&!intro&&_cueB.Color?.Length>=3)color=color.Lerp(new Color(_cueB.Color[0],_cueB.Color[1],_cueB.Color[2]),.55f);
        foreach(var ray in _concertLaserRays) {
            if(!IsInstanceValid(ray.Mesh))continue;
            var cue=valid?LaserCueAt(seconds,ray.Fixture):default;
            float level=0;
            if(cue.Strength>0&&cue.Duration>0) {
                float age=(float)(seconds-cue.Time);
                float attack=ShowTimeline.Ease(Math.Clamp(age/Math.Min(.18f,cue.Duration*.20f),0,1));
                float tail=Math.Min(.45f,cue.Duration*.20f);
                float release=1-ShowTimeline.Ease(Math.Clamp((age-(cue.Duration-tail))/tail,0,1));
                level=cue.Strength*LaserShutter(cue.Pattern,ray.Index)*attack*release;
                Vector3 origin=ray.OriginalTransform.Origin,target=LaserTarget(ray,cue,seconds);
                Vector3 direction=target-origin;float length=direction.Length();
                var basis=Basis.LookingAt(direction.Normalized(),Vector3.Up)*new Basis(Vector3.Right,-Mathf.Pi/2);
                ray.Mesh.GlobalTransform=new Transform3D(new Basis(basis.X,basis.Y*length,basis.Z),origin);
                if(level>.003f)MinimumLaserHeight=ActiveLaserRayCount==0?Math.Min(origin.Y,target.Y):Math.Min(MinimumLaserHeight,Math.Min(origin.Y,target.Y));
            }
            ray.Mesh.Visible=level>.003f;
            ray.Material.SetShaderParameter(LaserColorParameter,color);
            ray.Material.SetShaderParameter(LaserStrengthParameter,level);
            if(ray.Mesh.Visible) { ActiveLaserRayCount++;LaserStrength=Math.Max(LaserStrength,level);LaserPattern=cue.Pattern; }
            lensLevels[ray.Fixture]=Math.Max(lensLevels[ray.Fixture],level);
        }
        foreach(var lens in _concertLaserLenses)if(IsInstanceValid(lens.Mesh)) {
            float level=lensLevels[lens.Fixture];lens.Material.AlbedoColor=color*level*.15f;
            lens.Material.Emission=color;lens.Material.EmissionEnergyMultiplier=level*1.6f;
        }
    }
    private void RestoreShowLasers()
    {
        foreach(var ray in _concertLaserRays)if(IsInstanceValid(ray.Mesh)) {
            ray.Mesh.MaterialOverride=ray.OriginalMaterial;ray.Mesh.GlobalTransform=ray.OriginalTransform;
            ray.Mesh.Visible=ray.Visible;ray.Mesh.CastShadow=ray.Shadow;
        }
        foreach(var lens in _concertLaserLenses)if(IsInstanceValid(lens.Mesh))lens.Mesh.MaterialOverride=lens.Original;
        _concertLaserRays.Clear();_concertLaserLenses.Clear();
        LaserFixtureCount=ActiveLaserRayCount=0;LaserStrength=MinimumLaserHeight=0;LaserPattern="off";
    }
}
