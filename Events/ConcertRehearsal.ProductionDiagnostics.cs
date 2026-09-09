using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
namespace SerikaSocial.Events;

public partial class ConcertRehearsal
{
    private async Task VerifyProduction()
    {
        async Task Wait(double seconds=.15)=>await ToSignal(GetTree().CreateTimer(seconds),SceneTreeTimer.SignalName.Timeout);
        Check(_ready,"production show assets load");await Wait(.5);
        Check(_show.StageSpeakerCount==4,"four authored main and audience coverage speaker feeds bind");
        if(Arg("--no-intro")!="yes")Check(_show.IntroIsPlaying&&_show.IntroStageAudioPlaying,"preshow video and positional intro audio play before Start");
        Check(_show.CrowdStickCount==8345&&_show.CrowdBatchCount==6,"middle/rear pens and seated tribunes contain 8345 blue light sticks in six batches");
        Check(_show.LaserFixtureCount==4,"four laser projectors bind to authored optics");
        await ToSignal(GetTree(),SceneTree.SignalName.PhysicsFrame);
        var physics=GetWorld3D().DirectSpaceState;
        bool Blocked(Vector3 from, Vector3 to) {
            using var query=PhysicsRayQueryParameters3D.Create(from,to);
            // The rehearsal observer now spawns on the central route under test.
            // Test authored venue access without treating that observer as an obstacle.
            query.Exclude=new Godot.Collections.Array<Rid> { _body.GetRid() };
            return physics.IntersectRay(query).Count>0;
        }
        foreach(float x in new[]{-30f,30f})Check(physics.IntersectRay(PhysicsRayQueryParameters3D.Create(new Vector3(x,1,52),new Vector3(x,1,58))).Count>0,$"rear crowd pen {x} blocks audience entry");
        Check(physics.IntersectRay(PhysicsRayQueryParameters3D.Create(new Vector3(0,1,52),new Vector3(0,1,58))).Count==0,"central audience route stays open between rear pens");
        foreach(float side in new[]{-1f,1f}) {
            float x=side*30;
            Check(Blocked(new Vector3(x,1,15),new Vector3(x,1,21))
                && Blocked(new Vector3(x,1,47),new Vector3(x,1,41))
                && Blocked(new Vector3(side*3,1,31),new Vector3(side*8,1,31))
                && Blocked(new Vector3(side*59,1,31),new Vector3(side*55,1,31))
                && Blocked(new Vector3(x,3.5f,15),new Vector3(x,3.5f,21)),
                $"middle crowd pen {side} blocks all four edges and jumps above its visible rail");
            Check(!Blocked(new Vector3(x,1,-20),new Vector3(x,1,5)),
                $"front player pen {side} remains open from front to back");
        }
        Check(!Blocked(new Vector3(0,1,-20),new Vector3(0,1,77)),
            "central aisle stays open from the front player pens to FOH approach");
        Check(!Blocked(new Vector3(-56,1,10),new Vector3(56,1,10))
            && !Blocked(new Vector3(-56,1,46),new Vector3(56,1,46)),
            "cross aisles before and behind the closed middle pair remain open");
        Check(!Blocked(new Vector3(-8,1,79),new Vector3(8,1,79))
            && !Blocked(new Vector3(-8,1,91),new Vector3(8,1,91))
            && !Blocked(new Vector3(-8,1,79),new Vector3(-8,1,91))
            && !Blocked(new Vector3(8,1,79),new Vector3(8,1,91)),
            "existing FOH approach and side clearances remain accessible");
        Check(_show.CrowdWashCount==4,"four front-truss heads stay permanently audience-facing");
        PlayAt(_event.Config.RevealTime*.50);_show.PreviewPlaying=false;await Wait();
        Check(_show.ActiveSirenCount==12&&_show.SirenStrength>.2f,"blue rotating alarm beacons sweep the concealed entrance");
        PlayAt(_event.Config.RevealTime*.84);_show.PreviewPlaying=false;await Wait();
        Check(_show.EntranceConcealed&&_show.ActiveLaserRayCount>0&&_show.MinimumLaserHeight>10,"intro laser traces appear high above concealed performer and audience");
        Check(_show.ActiveSirenCount==12&&_show.SirenStrength>.2f,"alarm beacons keep sweeping through the late entrance");
        string laserState=$"{_show.ActiveLaserRayCount}/{_show.LaserStrength:F6}/{_show.LaserPattern}";
        PlayAt(_event.Config.RevealTime+10);_show.PreviewPlaying=false;await Wait();
        PlayAt(_event.Config.RevealTime*.84);_show.PreviewPlaying=false;await Wait();
        Check(laserState==$"{_show.ActiveLaserRayCount}/{_show.LaserStrength:F6}/{_show.LaserPattern}","backward seek exactly reconstructs laser cue");
        PlayAt(_event.Config.RevealTime+.5);await Wait(.5);
        Check(_show.Performer.Visible&&_show.StageAudioPlaying&&!_show.IntroStageAudioPlaying,"first verse reveal swaps preshow to stage PA music");
        Check(_show.ActiveSirenCount==0&&_show.SirenStrength==0,"alarm beacons cut the instant the stage takes over");
        Check(Math.Abs(_show.AudioPosition-_show.PreviewPosition)<.20&&_show.StageAudioSpread<.025,"all four speaker feeds follow timeline within one mix block");
        double pickup=EventShowPlayer.MicPickupDelay+(_event.Config.PerformerPath.Count>0?_event.Config.PerformerPath[^1].Time:0);
        PlayAt(pickup-.25);_show.PreviewPlaying=false;await Wait();
        Check(_show.MicrophoneReady&&!_show.MicrophoneHeld,"microphone stays on the stand until the pickup beat");
        PlayAt(pickup+.25);_show.PreviewPlaying=false;await Wait();
        Check(_show.MicrophoneHeld,"performer lifts the microphone after arriving");
        PlayAt(pickup+1.3);_show.PreviewPlaying=false;await Wait();
        var artist=_show.Performer;var skeleton=artist.Skeleton;
        var head=skeleton.GetBoneGlobalPose(artist.BoneOf("head")).Origin;
        var hand=skeleton.GetBoneGlobalPose(artist.BoneOf("rightHand")).Origin;
        var restHead=skeleton.GetBoneGlobalRest(artist.BoneOf("head")).Origin;
        var restEye=skeleton.GetBoneGlobalRest(artist.BoneOf("rightEye")).Origin;
        var forward=new Vector3(0,0,Math.Sign(restEye.Z-restHead.Z));
        Check((hand-head).Dot(forward)>.07f && hand.DistanceTo(head)<.25f,"microphone hand is in front of the face, not behind the head");
        Check(_show.MicrophoneFaceDistance<.18f,$"microphone capsule is beside the mouth ({_show.MicrophoneFaceDistance:F3}m)");
        Check(_show.CrowdPersonCount>4000&&_show.CrowdBodyBatchCount==6,"spectator bodies and penlights share per-person jump and sway anchors");
        foreach(var cue in _event.Config.Lights) foreach(double offset in new[]{.10,.35,(double)cue.Fade*.5,Math.Max(.1, (double)cue.Fade-.01)}) {
            if(cue.Time+offset>=_event.Config.Duration)continue;
            PlayAt(cue.Time+offset);_show.PreviewPlaying=false;await Wait(.035);
            Check(_show.ActiveShaftCount<=24,$"shaft budget through {cue.Look} fade at {cue.Time+offset:F2}s ({_show.ActiveShaftCount})");
        }
        var ending=_event.Config.Segments.LastOrDefault(s=>s.Title=="Thank you");
        if(ending!=null) {
            Check(ending.Duration>=8 && ending.Start+ending.Duration<=_event.Config.Duration+.001,"thank-you hold is inside the timeline for at least eight seconds");
            foreach(double time in new[]{ending.Start+.5,ending.Start+ending.Duration-.5}) {
                PlayAt(time);_show.PreviewPlaying=false;await Wait();
                Check(_show.ThankYouVisible&&_show.Performer.Visible&&_show.ActiveShaftCount>0,"thank-you card and performer remain visible throughout the readable end hold");
            }
        }
        _show.PreviewPlaying=false;await Wait();Check(!_show.StageAudioPlaying,"pause silences every stage feed");
        PlayAt(310);await Wait(.5);Check(Math.Abs(_show.AudioPosition-_show.PreviewPosition)<.20&&_show.StageAudioSpread<.025,"seeking restarts every channel at matching phase");
        var listenerBefore=_listener.GlobalTransform;_broadcastView=true;_camera.TopLevel=true;await Wait();
        Check(_listener.GlobalTransform.IsEqualApprox(listenerBefore)&&_listener.IsCurrent(),"broadcast flyovers do not move the player's audio listener");
        _broadcastView=false;_camera.TopLevel=false;_camera.Position=new Vector3(0,.7f,0);_camera.Rotation=Vector3.Zero;
        await VerifyStagePanning();
        var grip=await _show.RunLightStickGripChecks(_show.Performer);Check(grip.Passed,"blue light stick follows the real wrist across eight left/right wrist poses");
        // Against the real VRM, not the bean the lifecycle test uses: the bean has no finger
        // bones, and a skipped finger check is not a passing one.
        Check(_show.RunLightStickFingerGripCheck(_show.Performer),"holding a light stick closes the fingers around it and releasing reopens them");
        var life=await _show.RunLightStickLifecycleChecks();Check(life.Passed,"blue light sticks support bean avatars, swaps, leave, first person and teardown");
        _show.Preview=false;_event.Status="open";_event.Revision++;_show.ApplyState(_event);await Wait(.3);
        Check(!_show.StageAudioPlaying&&_show.ActiveLaserRayCount==0,"return to preshow stops stage music and lasers");
        GD.Print($"CONCERT_PRODUCTION_COMPLETE failures={_failures}");
    }
    private async Task VerifyStagePanning()
    {
        if(!_show.StageAudioActive)return;
        var speakers=_show.StageSpeakersForDiagnostics;var source=speakers[0];var streams=speakers.Select(s=>s.Stream).ToArray();
        bool oldProcess=_show.IsProcessing();var originalListener=_listener.GlobalTransform;bool originalTop=_listener.TopLevel;
        int bus=AudioServer.BusCount;AudioServer.AddBus();AudioServer.SetBusName(bus,"ConcertPanningProbe");
        var capture=new AudioEffectCapture { BufferLength=1 };AudioServer.AddBusEffect(bus,capture);
        byte[] pcm=new byte[48000*2];
        for(int i=0;i<48000;i++){short sample=(short)(Math.Sin(i*2*Math.PI*440/48000)*8000);pcm[i*2]=(byte)sample;pcm[i*2+1]=(byte)(sample>>8);}
        var tone=new AudioStreamWav { Format=AudioStreamWav.FormatEnum.Format16Bits,MixRate=48000,Stereo=false,Data=pcm,LoopMode=AudioStreamWav.LoopModeEnum.Forward,LoopBegin=0,LoopEnd=48000 };
        var originalBuses=speakers.Select(s=>s.Bus.ToString()).ToArray();string oldBus=source.Bus;float oldGain=source.VolumeDb;
        try {
            _audioProbe=true;_show.SetProcess(false);foreach(var s in speakers)s.Stop();
            source.Stream=tone;source.Bus="ConcertPanningProbe";source.VolumeDb=-6;_listener.TopLevel=true;
            async Task<Vector2> Measure(Vector3 position,Vector3 target) {
                source.Stop();_listener.GlobalPosition=position;_listener.LookAt(target);capture.ClearBuffer();source.Play();
                await ToSignal(GetTree().CreateTimer(.35),SceneTreeTimer.SignalName.Timeout);
                capture.ClearBuffer();await ToSignal(GetTree().CreateTimer(.25),SceneTreeTimer.SignalName.Timeout);
                var samples=capture.GetBuffer(capture.GetFramesAvailable());double l=0,r=0;
                foreach(var sample in samples){l+=sample.X*sample.X;r+=sample.Y*sample.Y;}
                return samples.Length>0?new Vector2((float)Math.Sqrt(l/samples.Length),(float)Math.Sqrt(r/samples.Length)):Vector2.Zero;
            }
            var front=new Vector3(0,1.7f,-15);
            var facing=await Measure(front,front+Vector3.Forward);
            var reverse=await Measure(front,front+Vector3.Back);
            var far=await Measure(new Vector3(0,1.7f,110),new Vector3(0,1.7f,109));
            GD.Print($"CONCERT_AUDIO_PCM facing={facing} reverse={reverse} far={far}");
            Check(facing.X>.001&&facing.X>facing.Y*1.15f,"actual mixed PCM places left stage array toward left ear");
            Check(reverse.Y>reverse.X*1.15f,"turning listener reverses the audible stage direction");
            Check(facing.Length()>far.Length()*1.35f,"stage audio attenuates with audience distance");
            Check(facing.IsFinite()&&reverse.IsFinite()&&far.IsFinite(),"direction and distance probe samples remain finite");
            for(int i=0;i<speakers.Count;i++){speakers[i].Stop();speakers[i].Stream=streams[i];speakers[i].Bus="ConcertPanningProbe";}
            var energy=_event.Config.MusicEnergy;double loud=Array.IndexOf(energy,energy.Max())/_event.Config.MusicFps;
            foreach(var position in new[]{front,new Vector3(-30,1.7f,-25),new Vector3(30,1.7f,-25),new Vector3(0,1.7f,88)}) {
                _listener.GlobalPosition=position;_listener.LookAt(position+Vector3.Forward);
                AudioServer.Lock();try{foreach(var speaker in speakers)speaker.Play((float)Math.Max(0,loud-.25));}finally{AudioServer.Unlock();}
                await ToSignal(GetTree().CreateTimer(.12),SceneTreeTimer.SignalName.Timeout);capture.ClearBuffer();
                await ToSignal(GetTree().CreateTimer(.65),SceneTreeTimer.SignalName.Timeout);
                var mixed=capture.GetBuffer(capture.GetFramesAvailable());float peak=0;int fullScale=0;
                foreach(var sample in mixed){peak=Math.Max(peak,Math.Max(Math.Abs(sample.X),Math.Abs(sample.Y)));if(Math.Abs(sample.X)>=.999f||Math.Abs(sample.Y)>=.999f)fullScale++;}
                GD.Print($"CONCERT_AUDIO_MIX position={position} sourceTime={loud:F3} peak={peak:F5} fullScaleFrames={fullScale} measuredFrames={mixed.Length}");
                Check(mixed.Length>1000&&peak>.001f&&peak<.98f&&fullScale==0,$"sampled loud passage retains headroom at audience position {position}");
            }

        } finally {
            source.Stop();source.Bus=oldBus;source.VolumeDb=oldGain;
            for(int i=0;i<speakers.Count;i++){speakers[i].Stop();speakers[i].Stream=streams[i];speakers[i].Bus=originalBuses[i];}
            _listener.TopLevel=originalTop;_listener.GlobalTransform=originalListener;
            _show.SetProcess(oldProcess);_audioProbe=false;AudioServer.RemoveBus(bus);
        }
    }
}
