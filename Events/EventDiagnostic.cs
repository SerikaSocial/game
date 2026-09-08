using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Serika.Net;
using SerikaSocial.UI;
using SerikaSocial.World;
namespace SerikaSocial.Events;

public partial class EventDiagnostic : Node3D
{
    private int _failures;
    private void Check(bool ok,string name){GD.Print($"EVENT {(ok?"PASS":"FAIL")} {name}");if(!ok)_failures++;}
    public override async void _Ready()
    {
        try {
            var args=OS.GetCmdlineUserArgs();string Arg(string key,string fallback="") {int i=System.Array.IndexOf(args,key);return i>=0&&i+1<args.Length?args[i+1]:fallback;}
            var keys=new List<CameraKey>{new(){Time=0,Position=new[]{0f,5.5f,-40},Target=new[]{0f,5.2f,-44}},new(){Time=10,Position=new[]{4f,5.5f,-40},Target=new[]{0f,5.2f,-44}}};
            ShowTimeline.CameraAt(keys,5,out var position,out _,out _);Check(position.DistanceTo(new Vector3(2,5.5f,-40))<.001,"camera path midpoint");
            Check(ShowTimeline.Position(15000,10000,60)==5,"late join uses server elapsed time");Check(ShowTimeline.Position(5000,10000,60)==0,"countdown clamps before start");
            var world=new Node3D();AddChild(world);var spawn=WorldLoader.LoadFromPath(Arg("--world"),"event-test",world);Check(spawn.HasValue,"venue loads");
            var screens=EventShowPlayer.FindPortraitScreens(world).ToArray();
            Check(screens.Length>=2,"both concert portrait displays exported");
            Check(world.FindChild("SERIKA_EVENT_PERFORMER*",true,false)!=null,"stage placement marker exported");
            await ToSignal(GetTree(),SceneTree.SignalName.PhysicsFrame);await ToSignal(GetTree(),SceneTree.SignalName.PhysicsFrame);
            var physics=GetWorld3D().DirectSpaceState;
            Check(physics.IntersectRay(PhysicsRayQueryParameters3D.Create(new Vector3(0,1,-20),new Vector3(0,1,-40))).Count>0,"audience cannot enter stage");
            Check(physics.IntersectRay(PhysicsRayQueryParameters3D.Create(new Vector3(78,1,0),new Vector3(84,1,0))).Count>0,"invisible side boundary is solid");
            var api=new ApiClient(Arg("--assets","http://127.0.0.1:41993"));
            var state=new LiveEvent {Id="diagnostic",WorldId="event-test",Status="live",Revision=1,ServerTime=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),StartedAt=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()-1000,
                Config=new ShowConfig{ArtistUrl="/artist.ska",AnimationUrl="/animation.glb?single-clip=1",AudioUrl="/track.wav",Clip="Dance_Simple",Duration=10,Cameras=keys}};
            var show=new EventShowPlayer();world.AddChild(show);show.Failed+=e=>{Check(false,"runtime prepare: "+e);};
            await show.Prepare(api,state,world);Check(show.ReadyToPlay,"real VRM artist + separate GLB + audio prepare");
            if(show.ReadyToPlay){
                if (screens.Length >= 2) {
                    Check(screens.All(m=>m.MaterialOverride is StandardMaterial3D),"screen materials assigned");
                    Check(((StandardMaterial3D)screens[0].MaterialOverride).AlbedoTexture==((StandardMaterial3D)screens[1].MaterialOverride).AlbedoTexture,"two displays share one viewport texture");
                }
                var rig=show.Performer.Skeleton;int hand=show.Performer.RoleToBoneForDiagnostics()["leftHand"];show.Preview=true;show.PreviewPosition=.15;
                await ToSignal(GetTree().CreateTimer(.02),SceneTreeTimer.SignalName.Timeout);
                var start=rig.GetBoneGlobalPose(hand).Origin;
                show.PreviewPosition=.65;
                await ToSignal(GetTree().CreateTimer(.02),SceneTreeTimer.SignalName.Timeout);
                Check(rig.GetBoneGlobalPose(hand).Origin.DistanceTo(start)>.001,"separate animation GLB moves the performer");
                show.Preview=false;
                await ToSignal(GetTree().CreateTimer(.6),SceneTreeTimer.SignalName.Timeout);
                Check(show.AudioPosition>1,"late join seeks audio past zero");
                Check(Enumerable.Range(0,rig.GetBoneCount()).All(i=>rig.GetBonePosePosition(i).IsFinite()&&rig.GetBonePoseRotation(i).IsFinite()),"animation retargeting never writes NaN");
                state.Status="open";state.Revision++;state.StartedAt=null;show.ApplyState(state);await ToSignal(GetTree().CreateTimer(.1),SceneTreeTimer.SignalName.Timeout);Check(show.AudioPosition<.1,"stop show stops audio");
            }
            var observer=new Camera3D{Position=new Vector3(0,1.7f,28),Far=2500,Current=true,Fov=72};AddChild(observer);observer.LookAt(new Vector3(0,12,-45));
            if(DisplayServer.GetName()!="headless"){
                await ToSignal(GetTree().CreateTimer(1),SceneTreeTimer.SignalName.Timeout);await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
                GetViewport().GetTexture().GetImage().SavePng(Arg("--out","/tmp/event-view.png"));
                var panel=new EventAdminPanel();AddChild(panel);panel.Visible=true;
                await ToSignal(GetTree().CreateTimer(.5),SceneTreeTimer.SignalName.Timeout);await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
                GetViewport().GetTexture().GetImage().SavePng(Arg("--out","/tmp/event-view.png")+".admin.png");
                var scroll=panel.FindChildren("*","ScrollContainer",true,false).OfType<ScrollContainer>().First();scroll.ScrollVertical=10000;
                await ToSignal(GetTree().CreateTimer(.2),SceneTreeTimer.SignalName.Timeout);await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
                GetViewport().GetTexture().GetImage().SavePng(Arg("--out","/tmp/event-view.png")+".controls.png");
                panel.Hide();var menu=new QuickMenu();AddChild(menu);menu.Open("Guest");
                state.Title="Hoshimachi Suisei";state.BannerUrl="fixture";state.Status="open";
                menu.EventsBanner.SetEvents(new[]{state},_=>Task.FromResult(System.IO.File.ReadAllBytes(Arg("--banner"))));
                await ToSignal(GetTree().CreateTimer(.3),SceneTreeTimer.SignalName.Timeout);await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
                GetViewport().GetTexture().GetImage().SavePng(Arg("--out","/tmp/event-view.png")+".menu.png");
            }
        }catch(Exception e){Check(false,e.ToString());}
        GD.Print($"EVENT_DIAGNOSTIC failures={_failures}");GetTree().Quit(_failures==0?0:1);
    }
}
