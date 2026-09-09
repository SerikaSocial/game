using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
namespace SerikaSocial.Events;

public partial class ConcertRehearsal
{
    private async Task VerifyEffects()
    {
        if(_event.Config.RevealTime<=0)return;
        async Task Seek(double time) {
            PlayAt(time);_show.PreviewPlaying=false;
            await ToSignal(GetTree().CreateTimer(.07),SceneTreeTimer.SignalName.Timeout);
        }
        string State() => $"{_show.ActiveFireCount}/{_show.ActiveSmokeCount}/{_show.ActiveSparkCount}/{_show.ActiveFireLightCount}/{_show.SkySparkleStrength:F5}/{_show.ActiveShaftCount}";
        Check(_show.EffectNodeCount==19,"all 19 authored effect banks bind to the show");
        // First presentation frame after load still has the avatar visible from setup.
        await Seek(0);
        await Seek(8);
        Check(_show.EntranceConcealed&&!_show.Performer.Visible&&_show.RevealLevel==0,"entrance is concealed even under ambient avatar shading");
        Check(_show.ActiveShaftCount==0&&_show.BlinderStrength==0&&_show.ActiveFireLightCount==0,"all stage optics and pyro remain dark before reveal");
        Check(_show.CrowdWashCount==4,"audience washes are bound without adding a new look name");
        Check(_show.ActiveSirenCount==0,"alarm beacons stay dark through the opening blackout");
        await Seek(_event.Config.RevealTime*.50);
        Check(_show.ActiveSirenCount==12&&_show.SirenStrength>.2f&&_show.ActiveShaftCount==0,"entrance alarms are haze volumes, not stage lights");
        var lights=_show.GetParent().FindChildren("*","Light3D",true,false).OfType<Light3D>();
        Check(lights.All(l=>l.LightEnergy<=.0001f),"all venue light energies are zero during the concealed entrance");
        var art=_show.GetParent().FindChildren("*","Node3D",true,false).OfType<Node3D>().Where(n=>n.Name=="Artist English name"||n.Name=="Artist Japanese name"||n.Name.ToString().StartsWith("Comet screen graphic")).ToArray();
        Check(art.Length>0&&art.All(n=>!n.Visible),"emissive stage artwork is concealed with or without an intro video");
        float early=_show.SkySparkleStrength;
        await Seek(_event.Config.RevealTime*.985);
        Check(early>0&&_show.SkySparkleStrength>early&&_show.SkySparkleStrength>.9f,"sky sparkle buildup rises toward the reveal");
        await Seek(_event.Config.RevealTime+.4);
        Check(_show.Performer.Visible&&_show.RevealLevel==1,"performer and follow spots reveal at the music start");
        Check(_show.ActiveFireCount==6&&_show.ActiveFireLightCount==6&&_show.ActiveSmokeCount==6,"reveal fires all six flank jets, spill lights and smoke plumes");
        Check(_show.ActiveScenicSpotCount<=4,"scenic spots preserve the four dedicated performer light slots");
        Check(_show.StageReceiverCount>=250,"stage deck and fixture receivers use their isolated lighting layer");
        Check(lights.OfType<OmniLight3D>().Count(l=>l.Visible&&l.LightEnergy>0&&(l.LightCullMask&(1u<<17))!=0)<=8,"six fire spill lights fit the stage Omni light budget");
        await Seek(_event.Config.RevealTime+2.2);
        Check(_show.SkySparkleStrength==0&&_show.ActiveFireCount==0&&_show.ActiveFireLightCount==0&&_show.ActiveSmokeCount>0,"reveal flame and sky decay while smoke drifts away");
        var selected=_event.Config.Effects.First(e=>e.Kind=="fire"&&e.Time>50);
        double sample=selected.Time+Math.Min(.3,selected.Duration*.4);
        await Seek(sample);string first=State();
        var surfaces=_show.GetParent().FindChildren("SERIKA_EVENT_FX_*","MeshInstance3D",true,false).OfType<MeshInstance3D>().Where(m=>m.MaterialOverride is ShaderMaterial).ToArray();
        float[] ages=surfaces.Select(m=>((ShaderMaterial)m.MaterialOverride).GetShaderParameter("event_age").AsSingle()).ToArray();
        await ToSignal(GetTree().CreateTimer(.2),SceneTreeTimer.SignalName.Timeout);
        Check(State()==first&&surfaces.Select((m,i)=>((ShaderMaterial)m.MaterialOverride).GetShaderParameter("event_age").AsSingle()==ages[i]).All(v=>v),"pausing holds the effect ages and selected banks");
        await Seek(700);await Seek(sample);
        Check(State()==first&&surfaces.Select((m,i)=>((ShaderMaterial)m.MaterialOverride).GetShaderParameter("event_age").AsSingle()==ages[i]).All(v=>v),"backward seeking reconstructs identical effect state");
        await Seek(_event.Config.Segments.Last().Start+6.4);
        Check(_show.ActiveFireCount+_show.ActiveSmokeCount+_show.ActiveSparkCount==0,"quiet song transition contains no stray effects");
        _show.Preview=false;_event.Status="open";_event.StartedAt=null;_event.Revision++;_show.ApplyState(_event);
        await ToSignal(GetTree().CreateTimer(.15),SceneTreeTimer.SignalName.Timeout);
        Check((Arg("--no-intro")=="yes"||_show.IntroIsPlaying)&&_show.ActiveFireCount+_show.ActiveSmokeCount+_show.ActiveSparkCount==0&&_show.SkySparkleStrength==0,"stop clears spectacle and restores looping preshow");
        if(Arg("--no-intro")=="yes")Check(art.All(n=>n.Visible),"no-intro stop restores authored stage art");
        GD.Print($"CONCERT_EFFECTS_COMPLETE failures={_failures}");
    }
}
