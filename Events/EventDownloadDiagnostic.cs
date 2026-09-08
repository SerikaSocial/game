using System;
using System.IO;
using Godot;
using Serika.Net;
namespace SerikaSocial.Events;
public partial class EventDownloadDiagnostic : Node
{
    public override async void _Ready()
    {
        try {
            var args=OS.GetCmdlineUserArgs(); int i=Array.IndexOf(args,"--world");
            string id=args[i+1]; var api=new ApiClient("https://api-social.ado.ink");
            string session=ProjectSettings.GlobalizePath("user://session.json");
            if(File.Exists(session)) await api.VerifySessionAsync(File.ReadAllText(session).Trim());
            var detail=await api.GetWorldDetailAsync(id);
            string path=await api.DownloadWorldAsync(detail.GetProperty("downloadUrl").GetString(),id);
            GD.Print($"EVENT_DOWNLOAD {(path!=null?"PASS":"FAIL")} world={id} bytes={(path==null?0:new FileInfo(path).Length)}");
            if(path!=null) { using var zip=System.IO.Compression.ZipFile.OpenRead(path); if(zip.GetEntry("world.glb")==null)throw new Exception("Missing world.glb"); }
            int eventArg=Array.IndexOf(args,"--event");
            if(path!=null && eventArg>=0) {
                var world=new Node3D();AddChild(world);
                SerikaSocial.World.WorldLoader.LoadFromPath(path,id,world);
                var state=await api.GetEventAsync(args[eventArg+1]);
                var show=new EventShowPlayer();world.AddChild(show);
                show.Failed+=message=>GD.PrintErr("EVENT_ASSETS "+message);
                await show.Prepare(api,state,world);
                if(!show.ReadyToPlay)throw new Exception("Live event assets did not prepare.");
                await ToSignal(GetTree().CreateTimer(2),SceneTreeTimer.SignalName.Timeout);
                GD.Print($"EVENT_ASSETS PASS status={state.Status} audio={show.AudioPosition:F3} speakers={show.StageSpeakerCount}");
            }
            GetTree().Quit(path==null?1:0);
        } catch(Exception e) { GD.PrintErr(e);GetTree().Quit(1); }
    }
}
