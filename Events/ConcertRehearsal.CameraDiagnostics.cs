using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;

namespace SerikaSocial.Events;

public partial class ConcertRehearsal
{
    private async Task VerifyCameraPresentation()
    {
        int initialFailures = _failures;
        bool originalPreview=_show.Preview, originalPlaying=_show.PreviewPlaying;
        double originalTime=_show.PreviewPosition;
        bool originalBroadcast=_broadcastView, originalCapture=_captureActive;
        bool originalTop=_camera.TopLevel, originalPanel=_panel.Visible;
        var originalCamera=_camera.GlobalTransform; float originalFov=_camera.Fov;
        var listener=_listener.GlobalTransform;
        var rows=new List<object>();
        async Task SetTime(double time) {
            _show.Preview=true;_show.PreviewPlaying=false;_show.PreviewPosition=time;
            // Two process boundaries include the runtime and the rehearsal mirror.
            await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        }
        async Task Capture(string name) {
            if(DisplayServer.GetName()=="headless")return;
            await Snapshot(name+"-viewer");
            var feed=_show.BroadcastCamera.GetViewport();
            using var image=feed.GetTexture().GetImage();
            image.SavePng(Path.Combine(_captureFolder,name+"-feed.png"));
            double sum=0,maximum=0;int count=0;
            for(int y=0;y<image.GetHeight();y+=4)for(int x=0;x<image.GetWidth();x+=4) {
                var color=image.GetPixel(x,y);double brightness=Math.Max(color.R,Math.Max(color.G,color.B));
                sum+=brightness;maximum=Math.Max(maximum,brightness);count++;
            }
            rows.Add(new {name,time=_show.PreviewPosition,opacity=_show.BroadcastFadeOpacity,feedMean=sum/Math.Max(count,1),feedMaximum=maximum});
            if(_show.BroadcastFadeOpacity>=.999f)Check(maximum<.001,"actual broadcast pixels reach black at the authored cut");
        }
        try {
            Check(_ready,"camera transition diagnostic loads the actual portable show");
            _panel.Hide();_captureActive=true;_broadcastView=true;_camera.TopLevel=true;
            _show.Preview=false;_show.PreviewPlaying=false;
            await ToSignal(GetTree().CreateTimer(.35),SceneTreeTimer.SignalName.Timeout);
            listener=_listener.GlobalTransform; // settle ordinary body gravity before checking camera isolation
            Check(_show.PreshowScreenCameraReady && _show.BroadcastFadeOpacity==0,"preshow uses an authored backdrop and has no cut fade");
            if(Arg("--no-intro")!="yes")Check(_show.IntroIsPlaying,"preshow screen video continues playing before Start");
            var backdrop=_show.GetParent().FindChildren("SERIKA_EVENT_BACKDROP*","MeshInstance3D",true,false)
                .OfType<MeshInstance3D>().OrderByDescending(m=>m.GetAabb().Size.X*m.GetAabb().Size.Y).FirstOrDefault();
            bool fitted=backdrop!=null;
            var fitViews=new List<object>();
            foreach(var size in new[]{new Vector2I(1280,720),new Vector2I(512,768)}) {
                var probe=new SubViewport { Size=size,RenderTargetUpdateMode=SubViewport.UpdateMode.Disabled };
                AddChild(probe);
                var camera=new Camera3D { Current=true };probe.AddChild(camera);
                try {
                    bool pose=_show.TryGetPreshowCameraPose((float)size.X/size.Y,out var position,out var target,out float fov);
                    fitted &= pose;
                    camera.GlobalPosition=position;camera.Fov=fov;
                    if(pose)camera.LookAt(target,Vector3.Up);
                    if(pose && backdrop!=null)for(int i=0;i<8;i++) {
                        var corner=backdrop.GlobalTransform*backdrop.GetAabb().GetEndpoint(i);
                        var pixel=camera.UnprojectPosition(corner);
                        fitted &= pixel.IsFinite() && !camera.IsPositionBehind(corner)
                            && pixel.X>=0 && pixel.X<=size.X && pixel.Y>=0 && pixel.Y<=size.Y;
                    }
                    fitViews.Add(new {size=new[]{size.X,size.Y},position=ShowTimeline.Array(position),target=ShowTimeline.Array(target),fov});
                } finally { probe.QueueFree(); }
            }
            rows.Add(new {name="preshow-screen-fit",views=fitViews});
            Check(fitted,"actual big-screen bounds fit both landscape and portrait camera projections");
            Check(!_show.TryGetPreshowCameraPose(float.NaN,out _,out _,out _),"invalid preshow aspect is rejected without non-finite camera state");
            await Snapshot("preshow-big-screen");
            var fadeLayer=_show.BroadcastCamera.GetViewport().GetNodeOrNull<CanvasLayer>("BroadcastCutFade");
            Check(fadeLayer!=null && fadeLayer.CustomViewport==_show.BroadcastCamera.GetViewport()
                && fadeLayer.GetViewport()!=GetViewport(),"cut overlay belongs only to the live-feed viewport");
            double cut=_event.Config.Cameras.Skip(1).First(k=>k.Cut && k.Time>_event.Config.RevealTime+1).Time;
            bool envelope=true;
            foreach(var sample in new[]{(-.3,0f),(-.25,0f),(-.125,.5f),(0.0,1f),(.125,.5f),(.25,0f),(.3,0f)})
                envelope &= Math.Abs(EventShowPlayer.EvaluateBroadcastFade(_event.Config,cut+sample.Item1)-sample.Item2)<.0001f;
            Check(envelope,"authored cut dips smoothly over250ms each side and is clear outside500ms");
            var continuous=new ShowConfig { Cameras=new List<CameraKey> {
                new(){Time=0,Cut=true},new(){Time=1,Cut=false},new(){Time=2,Cut=false} } };
            Check(EventShowPlayer.EvaluateBroadcastFade(continuous,0)==0
                && EventShowPlayer.EvaluateBroadcastFade(continuous,1)==0
                && EventShowPlayer.EvaluateBroadcastFade(_event.Config,cut,false)==0,
                "first key continuous paths and preshow never receive fades");
            bool revealClear=true;
            foreach(double offset in new[]{-.24,-.125,0,.125,.24})
                revealClear &= EventShowPlayer.EvaluateBroadcastFade(_event.Config,_event.Config.RevealTime+offset)==0;
            Check(revealClear,"the first sung verse reveal stays unobscured through the entire cut window");
            foreach(var sample in new[]{("cut-before",-.30),("cut-out",-.125),("cut-black",0.0),("cut-in",.125),("cut-after",.30)}) {
                await SetTime(cut+sample.Item2);await Capture(sample.Item1);
            }
            await SetTime(cut-.125);float opacity=_show.BroadcastFadeOpacity;var transform=_show.BroadcastCamera.GlobalTransform;
            await ToSignal(GetTree().CreateTimer(.2),SceneTreeTimer.SignalName.Timeout);
            Check(opacity==_show.BroadcastFadeOpacity && transform==_show.BroadcastCamera.GlobalTransform,
                "pausing inside a cut holds both fade opacity and broadcast camera");
            await SetTime(cut+2);await SetTime(cut-.125);
            Check(opacity==_show.BroadcastFadeOpacity && transform==_show.BroadcastCamera.GlobalTransform,
                "backward seeking reconstructs the identical fade and camera pose");
            await SetTime(cut);
            _broadcastView=false;_camera.GlobalPosition=new Vector3(0,1.7f,30);_camera.LookAt(new Vector3(0,13,-45));_camera.Fov=60;
            await Snapshot("audience-during-feed-black");
            Check(_listener.GlobalTransform.IsEqualApprox(listener),"camera fade and screen framing leave the player's audio listener fixed");
            _broadcastView=true;await SetTime(_event.Config.RevealTime);await Capture("first-verse-no-fade");
            _show.Preview=false;
            await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            Check(_show.BroadcastFadeOpacity==0 && fadeLayer?.GetNodeOrNull<ColorRect>("BlackDip")?.Visible==false,
                "returning to preshow clears the feed overlay immediately");
            File.WriteAllText(Path.Combine(_captureFolder,"camera-presentation.json"),JsonSerializer.Serialize(new {
                passed=_failures==initialFailures,cutSeconds=cut,revealSeconds=_event.Config.RevealTime,
                halfFadeSeconds=.25,samples=rows
            },new JsonSerializerOptions { WriteIndented=true }));
        } finally {
            _show.Preview=originalPreview;_show.PreviewPlaying=originalPlaying;_show.PreviewPosition=originalTime;
            _broadcastView=originalBroadcast;_captureActive=originalCapture;
            _camera.TopLevel=originalTop;_camera.GlobalTransform=originalCamera;_camera.Fov=originalFov;
            _panel.Visible=originalPanel;
        }
        GD.Print($"CONCERT_CAMERA_COMPLETE failures={_failures-initialFailures}");
    }
}
