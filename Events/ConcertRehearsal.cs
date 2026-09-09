using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using SerikaSocial.World;
namespace SerikaSocial.Events;

/// Local rehearsal of a portable show folder. No login, uploads or production services.
public partial class ConcertRehearsal : Node3D
{
    private EventShowPlayer _show;
    private LiveEvent _event;
    private CharacterBody3D _body;
    private Camera3D _camera;
    private AudioListener3D _listener;
    private bool _audioProbe, _captureActive;
    private Node3D _rehearsalGrip, _rehearsalLeftGrip;
    private Label _status;
    private HSlider _seek;
    private bool _dragging, _ready, _broadcastView = true;
    private Button _cameraViewButton;
    private ColorRect _broadcastFade;
    private float _pitch;
    private int _failures;
    private string _folder, _captureFolder;
    private PanelContainer _panel;
    private double _recordEnd = -1, _performanceLogClock;
    private bool _recordPerformer, _recordFace;
    private string Arg(string key,string fallback="") { var a=OS.GetCmdlineUserArgs();int i=System.Array.IndexOf(a,key);return i>=0&&i+1<a.Length?a[i+1]:fallback; }
    public override async void _Ready()
    {
        try {
            _folder=Arg("--show");_captureFolder=Arg("--out",_folder);Directory.CreateDirectory(_captureFolder);var data=JsonDocument.Parse(File.ReadAllText(Path.Combine(_folder,"show.json"))).RootElement;
            _event=new LiveEvent { Id="local-rehearsal",Title=data.GetProperty("title").GetString(),Status="open",Revision=1,
                ServerTime=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),Config=JsonSerializer.Deserialize<ShowConfig>(data.GetProperty("config").GetRawText(),LiveEvent.Json) };
            var world=new Node3D();AddChild(world);WorldLoader.LoadFromPath(Path.Combine(_folder,"venue.serikaworld"),"concert-rehearsal",world);
            _body=new CharacterBody3D { Position=new Vector3(0,1,-8) };AddChild(_body);
            _body.AddChild(new CollisionShape3D { Shape=new CapsuleShape3D{Radius=.3f,Height=1.8f} });
            _camera=new Camera3D { Position=new Vector3(0,.7f,0),Current=true,Far=2500,Fov=72 };_body.AddChild(_camera);
            _listener=new AudioListener3D { Position=new Vector3(0,.7f,0) };_body.AddChild(_listener);_listener.MakeCurrent();
            BuildControls();
            _show=new EventShowPlayer { ForceFullQuality=Arg("--verify")=="yes"||Arg("--production-only")=="yes"||Arg("--capture")=="yes"||Arg("--full-quality")=="yes" };world.AddChild(_show);_show.Failed+=e=>{_status.Text=e;GD.PrintErr(e);};
            await _show.PrepareFiles(_event,world,Task.FromResult(new[]{Path.Combine(_folder,"artist.ska"),Path.Combine(_folder,"show.glb"),Path.Combine(_folder,"show.ogg"),Arg("--no-intro")=="yes"?null:Path.Combine(_folder,"intro.ogv"),
                _event.Config.StageAudio?Path.Combine(_folder,"stage-left.ogg"):null,
                _event.Config.StageAudio?Path.Combine(_folder,"stage-right.ogg"):null,
                Arg("--no-intro")=="yes"||!File.Exists(Path.Combine(_folder,"intro-audio.ogg"))?null:Path.Combine(_folder,"intro-audio.ogg")}));
            _ready=_show.ReadyToPlay;
            UseBroadcastCamera(true);
            _rehearsalGrip=new Node3D { Position=new Vector3(.25f,-.25f,-.5f),RotationDegrees=new Vector3(-12,0,-15) };_camera.AddChild(_rehearsalGrip);_show.GiveRehearsalLightStick(_rehearsalGrip);
            _rehearsalLeftGrip=new Node3D { Position=new Vector3(-.25f,-.25f,-.5f),RotationDegrees=new Vector3(-12,0,15) };_camera.AddChild(_rehearsalLeftGrip);_show.GiveRehearsalLightStick(_rehearsalLeftGrip,true);
            _status.Text=_ready?"Intro loop · Start when ready. Hold right mouse to look; WASD to walk; L to cheer with both blue light sticks.":"Show preparation failed.";
            if(Arg("--clean")=="yes")_panel.Hide();
            if(Arg("--record-start")!="") {
                _panel.Hide();GetWindow().Mode=Window.ModeEnum.Windowed;GetWindow().Size=new Vector2I(1280,720);
                double start=double.Parse(Arg("--record-start"),System.Globalization.CultureInfo.InvariantCulture);
                _recordEnd=start+double.Parse(Arg("--record-duration","8"),System.Globalization.CultureInfo.InvariantCulture);
                string view=Arg("--record-view","performer");_recordPerformer=view is "performer" or "face";_recordFace=view=="face";
                _camera.TopLevel=true;_broadcastView=view=="broadcast";
                if(view=="front") { _camera.GlobalPosition=new Vector3(0,2,-23);_camera.LookAt(new Vector3(0,8,-44));_camera.Fov=64; }
                if(view=="rear") { _camera.GlobalPosition=new Vector3(0,15,115);_camera.LookAt(new Vector3(0,6,15));_camera.Fov=65; }
                if(view=="tribune") { _camera.GlobalPosition=new Vector3(52,12,56);_camera.LookAt(new Vector3(70,6,28));_camera.Fov=64; }
                if(view=="audience") { _camera.GlobalPosition=new Vector3(0,1.7f,30);_camera.LookAt(new Vector3(0,13,-45));_camera.Fov=60; }
                PlayAt(start);
            }
            else if(Arg("--player-crowd-capture")=="yes") { await CapturePlayerCrowd();GetTree().Quit(_failures==0?0:1); }
            else if(Arg("--capture")=="yes")await CaptureViews();
            else if(Arg("--camera-only")=="yes") { await VerifyCameraPresentation();GetTree().Quit(_failures==0?0:1); }
            else if(Arg("--production-only")=="yes") { await VerifyProduction();GetTree().Quit(_failures==0?0:1); }
            else if(Arg("--effects-only")=="yes") { await VerifyEffects();GetTree().Quit(_failures==0?0:1); }
            else if(Arg("--motion-only")=="yes") { VerifyMotion();GetTree().Quit(_failures==0?0:1); }
            else if(Arg("--verify")=="yes")await Verify();
            else if(Arg("--autostart")=="yes")PlayAt(0);
        }catch(Exception e){GD.PrintErr(e);if(_status!=null)_status.Text=e.Message;else GetTree().Quit(1);}
    }
    private void BuildControls()
    {
        var fadeLayer=new CanvasLayer { Name="RehearsalBroadcastFade",Layer=0 };AddChild(fadeLayer);
        _broadcastFade=new ColorRect { Color=Colors.Transparent,MouseFilter=Control.MouseFilterEnum.Ignore };
        fadeLayer.AddChild(_broadcastFade);_broadcastFade.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var layer=new CanvasLayer();AddChild(layer);
        var panel=_panel=new PanelContainer { Theme=Brand.Theme,Position=new Vector2(16,16),CustomMinimumSize=new Vector2(760,0) };panel.AddThemeStyleboxOverride("panel",Brand.Panel(Brand.Bg1,12,1,Brand.Border));layer.AddChild(panel);
        var col=new VBoxContainer();panel.AddChild(col);col.AddChild(new Label{Text=_event.Title});
        var row=new HBoxContainer();col.AddChild(row);
        Button AddButton(string text,Action callback){var b=new Button{Text=text};row.AddChild(b);b.Pressed+=()=>{if(_ready)callback();};return b;}
        AddButton("Loop intro",()=>{
            _show.Preview=false;_show.PreviewPlaying=false;_show.PreviewPosition=0;
            _event.Status="open";_event.Revision++;_event.StartedAt=null;_show.ApplyState(_event);
            _seek.Value=0;_status.Text="Intro loop · Start when ready. Audience view lets you walk the front viewing pens.";
            UseBroadcastCamera(true);
        });
        AddButton("Start show",()=>PlayAt(0));
        _cameraViewButton=AddButton("Audience view",()=>UseBroadcastCamera(!_broadcastView));
        AddButton("Pause / resume",()=>{if(_show.Preview)_show.PreviewPlaying=!_show.PreviewPlaying;});
        foreach(var segment in _event.Config.Segments){var s=segment;AddButton(s.Title,()=>PlayAt(s.Start));}
        _seek=new HSlider { MaxValue=_event.Config.Duration,Step=.1 };col.AddChild(_seek);_seek.DragStarted+=()=>_dragging=true;_seek.DragEnded+=changed=>{_dragging=false;if(changed)PlayAt(_seek.Value);};
        _status=new Label { Text="Loading show assets…" };col.AddChild(_status);
    }
    private void UseBroadcastCamera(bool active)
    {
        _broadcastView=active;_camera.TopLevel=active;
        if(!active){_camera.Position=new Vector3(0,.7f,0);_camera.Rotation=Vector3.Zero;_camera.Fov=72;}
        if(_cameraViewButton!=null)_cameraViewButton.Text=active?"Audience view":"Show camera";
    }
    private void PlayAt(double time){if(!_ready)return;_show.Preview=true;_show.PreviewPosition=time;_show.PreviewPlaying=true;GD.Print($"CONCERT_PLAY position={time:F3}");}
    public override void _Process(double delta)
    {
        if(!_ready)return;
        _performanceLogClock+=delta;
        if(_performanceLogClock>=10) { _performanceLogClock=0;GD.Print($"CONCERT_RUNTIME fps={Performance.GetMonitor(Performance.Monitor.TimeFps):F1} audio={_show.AudioPosition:F3} stageSpread={_show.StageAudioSpread:F4} timeline={_show.PreviewPosition:F3}"); }
        if(!_audioProbe)_listener.Rotation=new Vector3(_pitch,0,0);
        _rehearsalGrip.Visible=_rehearsalLeftGrip.Visible=!_broadcastView && !_recordPerformer && !_captureActive;
        if(_broadcastView){
            if(_show.IntroIsPlaying&&_show.TryGetPreshowCameraPose(GetViewport().GetVisibleRect().Size.Aspect(),out var introPosition,out var introTarget,out var introFov)){
                _camera.GlobalPosition=introPosition;_camera.LookAt(introTarget,Vector3.Up);_camera.Fov=introFov;
            }else{_camera.GlobalTransform=_show.BroadcastCamera.GlobalTransform;_camera.Fov=_show.BroadcastCamera.Fov;}
        }
        float fade=_broadcastView?_show.BroadcastFadeOpacity:0;
        _broadcastFade.Color=new Color(0,0,0,fade);_broadcastFade.Visible=fade>0;
        if(_recordEnd>=0) {
            if(_recordPerformer) {
                var origin=_show.Performer.GlobalPosition;
                _camera.GlobalPosition=origin+(_recordFace?new Vector3(.28f,1.44f,1.5f):new Vector3(2.1f,1.3f,4.2f));_camera.LookAt(origin+(_recordFace?new Vector3(0,1.33f,0):new Vector3(0,.78f,0)));_camera.Fov=_recordFace?30:33;
            }
            if(_show.PreviewPosition>=_recordEnd) { GD.Print($"CONCERT_RECORD_COMPLETE audio={_show.AudioPosition:F3} timeline={_show.PreviewPosition:F3}");GetTree().Quit(); }
        }
        if(!_dragging)_seek.Value=_show.PreviewPosition;
        if(_show.Preview){var s=_event.Config.Segments.LastOrDefault(s=>s.Start<=_show.PreviewPosition);_status.Text=$"{s?.Title} · {TimeSpan.FromSeconds(_show.PreviewPosition):mm\\:ss} / {TimeSpan.FromSeconds(_event.Config.Duration):mm\\:ss} · Right mouse + WASD";}
    }
    public override void _PhysicsProcess(double delta)
    {
        if(_body==null)return;
        float x=(Input.IsPhysicalKeyPressed(Key.D)?1:0)-(Input.IsPhysicalKeyPressed(Key.A)?1:0),z=(Input.IsPhysicalKeyPressed(Key.S)?1:0)-(Input.IsPhysicalKeyPressed(Key.W)?1:0);
        Vector3 move=_body.Basis*new Vector3(x,0,z).Normalized()*5;
        _body.Velocity=new Vector3(move.X,_body.IsOnFloor()?0:_body.Velocity.Y-(float)delta*9.8f,move.Z);_body.MoveAndSlide();
    }
    public override void _UnhandledInput(InputEvent e)
    {
        if(e is InputEventMouseButton b && b.ButtonIndex==MouseButton.Right)Input.MouseMode=b.Pressed?Input.MouseModeEnum.Captured:Input.MouseModeEnum.Visible;
        if(e is InputEventMouseMotion m && Input.MouseMode==Input.MouseModeEnum.Captured){_body.RotateY(-m.Relative.X*.003f);_pitch=Math.Clamp(_pitch-m.Relative.Y*.003f,-1.2f,1.2f);_camera.Rotation=new Vector3(_pitch,0,0);}
        if(e is InputEventKey wave && wave.Pressed && !wave.Echo && wave.PhysicalKeycode==Key.L && _rehearsalGrip!=null) {
            foreach(var pair in new[]{(_rehearsalGrip,1f),(_rehearsalLeftGrip,-1f)}) {
                var tween=CreateTween();tween.TweenProperty(pair.Item1,"rotation:z",-.8f*pair.Item2,.28);tween.TweenProperty(pair.Item1,"rotation:z",.35f*pair.Item2,.45);tween.TweenProperty(pair.Item1,"rotation:z",-.26f*pair.Item2,.35);
            }
        }
        if(e is InputEventKey k && k.Pressed && k.Keycode==Key.Escape)Input.MouseMode=Input.MouseModeEnum.Visible;
    }
    private void Check(bool ok,string what){GD.Print($"CONCERT {(ok?"PASS":"FAIL")} {what}");if(!ok)_failures++;}
    private async Task Snapshot(string name)
    {
        if(DisplayServer.GetName()=="headless")return;
        await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
        GetViewport().GetTexture().GetImage().SavePng(Path.Combine(_captureFolder,name+".png"));
    }
    /// Capture points derived from the show that was actually loaded.
    ///
    /// This was a fixed list ending in ("stellar",626.8) and ("stellar-finale",762.8). Those are
    /// past the end of a two-song show, so every default capture ran two samples against a
    /// clamped, finished timeline and wrote them out as if they were real frames.
    private (string,double)[] DefaultCaptureSamples()
    {
        var config=_event.Config;
        var samples=new List<(string,double)> {
            ("blackout",8.0), ("sky-build",config.RevealTime*.94),
            ("reveal",config.RevealTime+.4), ("reveal-smoke",config.RevealTime+3.0),
        };
        var fire=config.Effects.FirstOrDefault(e=>e.Kind=="fire"&&e.Time>config.RevealTime+30);
        if(fire!=null) samples.Add(("first-fire",fire.Time+.30));
        // One frame a third of the way into each song, plus one near its end, named for the song.
        foreach(var segment in config.Segments.Skip(1)) {
            string slug=segment.Title.Replace(" ","-").ToLowerInvariant();
            samples.Add((slug,segment.Start+segment.Duration*.34));
            samples.Add((slug+"-late",segment.Start+segment.Duration*.86));
        }
        return samples.Where(s=>s.Item2>=0&&s.Item2<config.Duration).ToArray();
    }
    private async Task CaptureViews()
    {
        _panel.Hide();_captureActive=true;GetWindow().Mode=Window.ModeEnum.Windowed;GetWindow().Size=new Vector2I(1280,720);
        foreach(var sample in (Arg("--sample")!=""?new[]{("sample",double.Parse(Arg("--sample"),System.Globalization.CultureInfo.InvariantCulture))}:DefaultCaptureSamples())) {
            PlayAt(sample.Item2);_show.PreviewPlaying=false;_broadcastView=false;_camera.TopLevel=true;
            _camera.GlobalPosition=new Vector3(0,1.7f,30);_camera.LookAt(new Vector3(0,13,-45));_camera.Fov=60;
            await ToSignal(GetTree().CreateTimer(.2),SceneTreeTimer.SignalName.Timeout);await Snapshot(sample.Item1+"-audience");GD.Print($"CAPTURE_LIGHTS {sample.Item1} bound={_show.MovingHeadCount} shafts={_show.ActiveShaftCount} look={_show.LightingLook} fire={_show.ActiveFireCount} smoke={_show.ActiveSmokeCount} sparks={_show.ActiveSparkCount} sky={_show.SkySparkleStrength:F3}");
            _camera.GlobalPosition=new Vector3(0,2,-23);_camera.LookAt(new Vector3(0,8,-44));_camera.Fov=64;
            await ToSignal(GetTree().CreateTimer(.1),SceneTreeTimer.SignalName.Timeout);await Snapshot(sample.Item1+"-front");
            _broadcastView=true;
            await ToSignal(GetTree().CreateTimer(.15),SceneTreeTimer.SignalName.Timeout);await Snapshot(sample.Item1+"-broadcast");
            if(Arg("--sample")!="") {
                _broadcastView=false;_camera.GlobalPosition=new Vector3(0,15,115);_camera.LookAt(new Vector3(0,6,15));_camera.Fov=65;
                await ToSignal(GetTree().CreateTimer(.1),SceneTreeTimer.SignalName.Timeout);await Snapshot("sample-rear");
                _camera.GlobalPosition=new Vector3(52,12,56);_camera.LookAt(new Vector3(70,6,28));_camera.Fov=64;
                await ToSignal(GetTree().CreateTimer(.1),SceneTreeTimer.SignalName.Timeout);await Snapshot("sample-tribune");
                _camera.GlobalPosition=_show.Performer.GlobalPosition+new Vector3(.28f,1.44f,1.5f);_camera.LookAt(_show.Performer.GlobalPosition+new Vector3(0,1.33f,0));_camera.Fov=30;
                await ToSignal(GetTree().CreateTimer(.1),SceneTreeTimer.SignalName.Timeout);await Snapshot("sample-face");
            }
        }
        if(Arg("--sample")=="") {
            PlayAt(_event.Config.RevealTime+.4);_show.PreviewPlaying=false;_broadcastView=false;_camera.TopLevel=true;
            await ToSignal(GetTree().CreateTimer(.1),SceneTreeTimer.SignalName.Timeout);
            if(_show.GetParent().FindChild("SERIKA_EVENT_MOVING_0_10",true,false) is Node3D fixture) {
                _camera.GlobalPosition=fixture.GlobalPosition+new Vector3(-1.3f,1.1f,1.8f);_camera.LookAt(fixture.GlobalPosition+new Vector3(0,-.12f,0));_camera.Fov=40;
                await Snapshot("moving-head-detail");
            }
        }
        _broadcastView=false;_camera.GlobalPosition=new Vector3(0,1.7f,30);_camera.LookAt(new Vector3(0,13,-45));_camera.Fov=60;
        _captureActive=false;await ToSignal(GetTree().CreateTimer(.12),SceneTreeTimer.SignalName.Timeout);await Snapshot("player-both-lightsticks");
        GD.Print("CONCERT_CAPTURE_COMPLETE");GetTree().Quit();
    }
    private async Task Verify()
    {
        Check(_ready,"all supplied assets load");Check(_show.MovingHeadCount==36,"all 36 concert moving heads bind to the timeline");await ToSignal(GetTree().CreateTimer(1),SceneTreeTimer.SignalName.Timeout);
        Check(_show.IntroIsPlaying,"intro video decodes and loops before show");Check(!_show.Performer.Visible,"performer hidden before show");await Snapshot("preview-intro");
        await ToSignal(GetTree(),SceneTree.SignalName.PhysicsFrame);
        var physics=GetWorld3D().DirectSpaceState;
        Check(physics.IntersectRay(PhysicsRayQueryParameters3D.Create(new Vector3(0,1,-20),new Vector3(0,1,-40))).Count>0,"audience exclusion still blocks stage entry");
        Check(physics.IntersectRay(PhysicsRayQueryParameters3D.Create(new Vector3(78,1,0),new Vector3(84,1,0))).Count>0,"invisible map boundary remains solid");
        var displays=EventShowPlayer.FindPortraitScreens(_show.GetParent()).ToArray();
        Check(displays.Length==2,"two portrait screens preserved");
        var rig=_show.Performer.Skeleton;int hand=_show.Performer.RoleToBoneForDiagnostics()["rightHand"];
        foreach(var segment in _event.Config.Segments){PlayAt(segment.Start+Math.Min(40,segment.Duration/2));await ToSignal(GetTree().CreateTimer(.4),SceneTreeTimer.SignalName.Timeout);
            bool concealed=_event.Config.RevealTime>0&&_show.PreviewPosition<_event.Config.RevealTime;
            Check(!_show.IntroIsPlaying && _show.Performer.Visible!=concealed,segment.Title+" switches intro to the correct reveal state");
            if(displays.Length==2)Check(displays[0].MaterialOverride is StandardMaterial3D a && displays[1].MaterialOverride is StandardMaterial3D b && a.AlbedoTexture==b.AlbedoTexture,segment.Title+" portrait screens share live engine camera");
            Check(Math.Abs(_show.AudioPosition-_show.PreviewPosition)<.7,segment.Title+" audio seek follows timeline");
            Check(Enumerable.Range(0,rig.GetBoneCount()).All(i=>rig.GetBonePosePosition(i).IsFinite()&&rig.GetBonePoseRotation(i).IsFinite()),segment.Title+" rig is finite");
            var roles=_show.Performer.RoleToBoneForDiagnostics();
            float hipsY=rig.GetBoneGlobalPose(roles["hips"]).Origin.Y;
            Check(hipsY-rig.GetBoneGlobalPose(roles["leftLowerLeg"]).Origin.Y>.20f && hipsY-rig.GetBoneGlobalPose(roles["rightLowerLeg"]).Origin.Y>.20f,segment.Title+" legs remain in a grounded standing stance");
            GD.Print($"PERFORMER {segment.Title} root={_show.Performer.GlobalPosition} head={rig.ToGlobal(rig.GetBoneGlobalPose(_show.Performer.RoleToBoneForDiagnostics()["head"]).Origin)}");
            await Snapshot("preview-"+segment.Title.Replace(" ","-"));
            _broadcastView=true;_camera.TopLevel=true;await ToSignal(GetTree().CreateTimer(.2),SceneTreeTimer.SignalName.Timeout);await Snapshot("performer-"+segment.Title.Replace(" ","-"));
            _broadcastView=false;_camera.TopLevel=false;_camera.Position=new Vector3(0,.7f,0);_camera.Rotation=Vector3.Zero;_camera.Fov=72;}
        var heads=_show.GetParent().FindChildren("SERIKA_EVENT_MOVING_*","Node3D",true,false);
        // Probe the looks this show ACTUALLY authors, one sample per distinct look, taken just
        // after the cue fires. These were four hardcoded (name, second) pairs lifted off the
        // three-song timeline, so trimming a song left one pointing past the end of the show and
        // it reported "0 shafts" — a failure that says nothing about the lighting and everything
        // about a stale constant.
        foreach(var look in _event.Config.Lights.Where(l=>l.Time+.35<_event.Config.Duration)
                    .GroupBy(l=>l.Look??"unnamed").Select(g=>(Name:g.Key,Time:g.First().Time+.35))) {
            PlayAt(look.Time);_show.PreviewPlaying=false;
            await ToSignal(GetTree().CreateTimer(.1),SceneTreeTimer.SignalName.Timeout);
            // A blackout or a held transition is authored to show nothing; every other look
            // must actually put shafts in the air, and none may flood the room.
            bool dark=look.Name is "black" or "hold";
            Check(_show.ActiveShaftCount<=24 && (dark?_show.ActiveShaftCount==0:_show.ActiveShaftCount>0),
                $"{look.Name} reserves negative space ({_show.ActiveShaftCount} shafts)");
            // Blinders are authored on purpose at the loud looks — the two BIBBIDIBA bumps are a
            // deliberate part of the show. What must never happen is one firing during a quiet
            // look, which is the "indiscriminate flashing" this has always been guarding against.
            if(look.Name is "black" or "intimate" or "side" or "hold")
                Check(_show.BlinderStrength==0,$"{look.Name} has no indiscriminate blinder flashing");
            await Snapshot("lighting-"+look.Name);
        }
        Check(heads.All(n=>((Node3D)n).GlobalBasis.X.IsFinite()&&((Node3D)n).GlobalBasis.Y.IsFinite()),"all moving head orientations remain finite");
        if (_event.Config.Beats.Length > 0) {
            var c=_event.Config;
            int peak=Array.IndexOf(c.Beats,c.Beats.Max());
            PlayAt(peak/c.MusicFps);_show.PreviewPlaying=false;
            await ToSignal(GetTree().CreateTimer(.1),SceneTreeTimer.SignalName.Timeout);
            Check(_show.BeatStrength>.7f,"recorded musical onset drives beat pulse after seek");
            await Snapshot("preview-beat-lights");
            PlayAt(8);_show.PreviewPlaying=false;
            await ToSignal(GetTree().CreateTimer(.1),SceneTreeTimer.SignalName.Timeout);
            Check(_show.MouthOpening==0 && !_show.Performer.Visible,"musical entrance keeps the concealed performer and mouth hidden");
            int vocal=Array.IndexOf(c.Mouth,c.Mouth.Max());
            PlayAt(vocal/c.MouthFps);_show.PreviewPlaying=false;
            await ToSignal(GetTree().CreateTimer(.1),SceneTreeTimer.SignalName.Timeout);
            float actual=0;int morphCount=0;
            foreach(var node in _show.Performer.FindChildren("*","MeshInstance3D",true,false)) {
                if(node is not MeshInstance3D mesh || mesh.Mesh is not ArrayMesh am) continue;
                for(int i=0;i<am.GetBlendShapeCount();i++) {
                    string name=am.GetBlendShapeName(i).ToString().ToLowerInvariant();
                    if(name=="a" || name=="aa" || name.Contains("mth_a")) {actual=Math.Max(actual,mesh.GetBlendShapeValue(i));morphCount++;}
                }
            }
            Check(actual>.4f,$"singing opens actual model mouth morph (weight {actual:F3}, {morphCount} candidates)");
            _broadcastView=false;_camera.TopLevel=true;_camera.GlobalPosition=ShowTimeline.Vector(c.Performer)+new Vector3(.65f,1.44f,1.5f);_camera.LookAt(ShowTimeline.Vector(c.Performer)+new Vector3(0,1.33f,0));_camera.Fov=34;
            await Snapshot("performer-singing");
            _camera.TopLevel=false;_camera.Position=new Vector3(0,.7f,0);_camera.Rotation=Vector3.Zero;_camera.Fov=72;
        }
        var finalSong = _event.Config.Segments.Last();
        PlayAt(finalSong.Start+4);_broadcastView=true;_camera.TopLevel=true;
        await ToSignal(GetTree().CreateTimer(.3),SceneTreeTimer.SignalName.Timeout);
        await Snapshot("performer-closeup");
        _broadcastView=false;_camera.TopLevel=false;_camera.Position=new Vector3(0,.7f,0);_camera.Rotation=Vector3.Zero;_camera.Fov=72;
        if (_event.Config.PerformerPath.Count > 0) {
            ShowTimeline.PerformerAt(_event.Config,0,out var wing,out _);
            ShowTimeline.PerformerAt(_event.Config,_event.Config.RevealTime,out var center,out var yaw);
            Check(wing.DistanceTo(center)>5 && center.DistanceTo(ShowTimeline.Vector(_event.Config.Performer))<.001f && Math.Abs(yaw-180)<.01,"concealed entrance arrives from wing and faces audience by the first verse");
            PlayAt(8); _show.PreviewPlaying=false;
            await ToSignal(GetTree().CreateTimer(.1),SceneTreeTimer.SignalName.Timeout);
            ShowTimeline.PerformerAt(_event.Config,8,out var expected,out _);
            Check(expected.DistanceTo(_show.Performer.GlobalPosition)<.001f,"seeking restores entrance travel exactly");
        }
        float maxHandStep=0;
        foreach(double boundary in _event.Config.PerformerPath.Select(k=>k.Time).Concat(_event.Config.Segments.Select(k=>k.Start)).Where(t=>t>0)) {
            PlayAt(boundary-.016);_show.PreviewPlaying=false;
            await ToSignal(GetTree().CreateTimer(.06),SceneTreeTimer.SignalName.Timeout);
            var handBefore=rig.GetBoneGlobalPose(hand).Origin;
            _show.PreviewPosition=boundary+.016;
            await ToSignal(GetTree().CreateTimer(.06),SceneTreeTimer.SignalName.Timeout);
            maxHandStep=Math.Max(maxHandStep,handBefore.DistanceTo(rig.GetBoneGlobalPose(hand).Origin));
        }
        Check(maxHandStep<.04f,$"entrance and song boundaries have no arm snap (max {maxHandStep:F4}m / 32ms)");
        PlayAt(100);_show.PreviewPlaying=false;await ToSignal(GetTree().CreateTimer(.1),SceneTreeTimer.SignalName.Timeout);var before=rig.GetBoneGlobalPose(hand).Origin;
        _show.PreviewPosition=120;await ToSignal(GetTree().CreateTimer(.1),SceneTreeTimer.SignalName.Timeout);Check(before.DistanceTo(rig.GetBoneGlobalPose(hand).Origin)>.003,"authored arm motion changes across musical phrases");
        _show.Preview=false;_event.Status="live";_event.Revision++;
        _event.ServerTime=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();_event.StartedAt=_event.ServerTime-100000;_show.ApplyState(_event);
        await ToSignal(GetTree().CreateTimer(.3),SceneTreeTimer.SignalName.Timeout);
        Check(Math.Abs(_show.AudioPosition-100.3)<.7,"late join seeks soundtrack from synchronized server time");
        _event.Revision++;_event.ServerTime=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();_event.StartedAt=_event.ServerTime-200000;_show.ApplyState(_event);
        await ToSignal(GetTree().CreateTimer(.3),SceneTreeTimer.SignalName.Timeout);
        Check(Math.Abs(_show.AudioPosition-200.3)<.7,"admin revision seeks all show channels to new server time");
        _show.ApplyState(new LiveEvent { Revision=_event.Revision-1,Status="open",Config=_event.Config });
        await ToSignal(GetTree().CreateTimer(.1),SceneTreeTimer.SignalName.Timeout);
        Check(_show.AudioPosition>200,"stale event revision cannot interrupt playback");
        _event.Status="open";_event.Revision++;_event.StartedAt=null;_show.ApplyState(_event);
        await ToSignal(GetTree().CreateTimer(.2),SceneTreeTimer.SignalName.Timeout);Check(_show.IntroIsPlaying,"stop returns to intro");
        await VerifyEffects();
        VerifyMotion();
        GD.Print($"CONCERT_COMPLETE failures={_failures}");GetTree().Quit(_failures==0?0:1);
    }
}
