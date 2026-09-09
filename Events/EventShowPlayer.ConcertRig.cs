using System;
using System.Collections.Generic;
using Godot;
namespace SerikaSocial.Events;

public partial class EventShowPlayer
{
    private sealed class MovingHead
    {
        public Node3D Root, Pan;
        public Basis OriginalPanBasis;
        public Basis OriginalBasis;
        public int Bank, Index;
        public bool Crowd;                    // audience-facing wash, not a stage fixture
        public MeshInstance3D Beam;
        public Vector3 BeamScale;
        public ShaderMaterial BeamMaterial;
        public StandardMaterial3D Lens;
        public SpotLight3D Spot;
        public float SpotEnergy;
        public uint SpotMask;
        public bool SpotVisible, SpotShadow;
        public Color SpotColor;
        public float SpotAngle, SpotRange, SpotAttenuation;
    }
    private readonly List<MovingHead> _movingHeads = new();
    /// Front-truss heads turned round to face the audience.
    ///
    /// Bank 1 is the only bank in front of the performer (z = -32, y = 26) — banks 0 and 2 sit
    /// behind her, so aiming those at the crowd would fire straight through the singer. Four of
    /// bank 1's twelve heads are permanently audience-facing: the outer pair (x = ±27) fans the
    /// flanks and the inner pair (x = ±7.4) is exactly the pair that carries a real SpotLight3D,
    /// so the crowd gets actual light down the centre and not only haze. Bank 1's own spots at
    /// 1 and 10 stay on stage duty, so nothing is taken away from the singer.
    ///
    /// FOUR, not six, and the reason is the shaft budget. Every look is probed mid-fade, so the
    /// outgoing look's fixtures are still lit when the incoming look's open: the count that
    /// matters is the UNION across a cue transition, not either look on its own. Six crowd heads
    /// put the sweep→finale union at exactly 24, the hard ceiling, with nothing left for the
    /// onset-hit group that can light up to six more heads at an instant the verifier never
    /// samples. Four keeps the measured worst case at 21, the same slack the rig had before.
    ///
    /// The set is fixed for the whole show: a fixture that swings from stage duty to crowd duty
    /// between cues pops, and its beam length would have to change with it.
    private static bool IsCrowdWash(int bank,int index)=>bank==1 && index is 0 or 4 or 7 or 11;
    // A crowd shaft has to cross ~60 m of venue instead of ~20 m of stage. The haze shader
    // integrates in MODEL space, so a UNIFORM scale lengthens and widens the volume without
    // touching its brightness or breaking the analytic cone intersection. Non-uniform scale would.
    private const float CrowdBeamScale = 2.25f;
    /// One rotating blue emergency beacon: a yaw pivot carrying two opposed beams.
    ///
    /// Two beams 180 degrees apart is what makes a rotating beacon read as an ALARM rather than
    /// as a searchlight — the venue is swept twice per revolution, which is the cadence the eye
    /// recognises. The pivot's angle is integrated analytically from show time, so a seek, a
    /// pause or a late join reconstruct the identical sweep.
    private sealed class SirenBeacon { public Node3D Pivot; public float Direction, Phase; }
    private readonly List<SirenBeacon> _sirens = new();
    private Node3D _sirenRoot;
    private ShaderMaterial _sirenBeamMaterial;
    private StandardMaterial3D _sirenDomeMaterial;
    private float _sirenWritten = -1;
    public float SirenStrength { get; private set; }
    public int ActiveSirenCount { get; private set; }
    public int CrowdWashCount { get; private set; }
    // Mount, and the tilt of the beam off horizontal. Positive tilts up. The two low beacons
    // stand on the deck (top y = 4.2, front fascia z = -36.9) and rake UP over the audience; the
    // high ones hang off the front truss and the rear tower tops and rake DOWN across it, so the
    // crowd is inside the sweep from both directions instead of under a ceiling of parallel beams.
    private static readonly (Vector3 Mount,float Tilt)[] SirenMounts = {
        (new Vector3(-28.5f, 5.6f,-38.5f),   9f),
        (new Vector3( 28.5f, 5.6f,-38.5f),   9f),
        (new Vector3(-20.4f,19.3f,-45.9f),  -4f),
        (new Vector3( 20.4f,19.3f,-45.9f),  -4f),
        (new Vector3(-29.5f,25.6f,-32.2f), -11f),
        (new Vector3( 29.5f,25.6f,-32.2f), -11f),
    };
    private static readonly Color SirenTint = new(.16f,.42f,1f);
    // Entrance-relative, so this works for any show that authors a reveal. The alarm stays out of
    // the first fifth of the entrance: the opening is a genuine blackout and the beacons arriving
    // into it is the event.
    private const double SirenStart = .18, SirenBaseRate = .85, SirenPeakRate = 3.4;
    private const float SirenBeamScale = 1.9f;
    private readonly List<(MeshInstance3D Mesh, Material Original, bool Visible, GeometryInstance3D.ShadowCastingSetting Shadow)> _rigSurfaces = new();
    private readonly List<(MeshInstance3D Mesh, uint Layers)> _stageReceivers = new();
    private const uint ScenicStageLayer = 1u << 17;
    private readonly List<(StandardMaterial3D Material, int Bank)> _blinders = new();
    private readonly List<double> _rigHits = new();
    public int MovingHeadCount => _movingHeads.Count;
    public int ActiveShaftCount { get; private set; }
    public int ActiveScenicSpotCount { get; private set; }
    public int StageReceiverCount => _stageReceivers.Count;
    public float BlinderStrength { get; private set; }
    public string LightingLook { get; private set; } = "preshow";
    private static readonly StringName HazeTint = new("tint"), HazeIntensity = new("intensity");
    private ShowLightKey _cueA = new(), _cueB = new();
    private float _cueBlend;
    private int _cueIndex;

    private void SetupConcertRig(Node3D world)
    {
        // The broad deck intersects all fire lights and several audience washes. Isolate its
        // receivers so six local fire lights plus the stage-front Omni stay below the separate
        // eight-Omni-per-mesh limit in Mobile/Compatibility. Camera visibility is unchanged.
        foreach(Node node in world.FindChildren("*","MeshInstance3D",true,false)) {
            var mesh=(MeshInstance3D)node;
            if(!IsStageLightReceiver(mesh,world))continue;
            _stageReceivers.Add((mesh,mesh.Layers));
            mesh.Layers=(mesh.Layers&~1u)|ScenicStageLayer;
        }
        var shader=GD.Load<Shader>("res://Shaders/concert_haze.gdshader");
        foreach(Node node in world.FindChildren("SERIKA_EVENT_MOVING_*","Node3D",true,false)) {
            var root=(Node3D)node;var parts=root.Name.ToString().Split('_');
            if(parts.Length!=5 || !int.TryParse(parts[3],out int bank) || !int.TryParse(parts[4],out int index))continue;
            if(bank is <0 or >2 || index is <0 or >11) { GD.PushError($"Noncanonical concert fixture ID: {root.Name}. Rebuild the venue rig.");continue; }
            var head=new MovingHead { Root=root,OriginalBasis=root.GlobalBasis,Bank=bank,Index=index,Crowd=IsCrowdWash(bank,index) };
            if(root.GetParent() is Node3D pan && pan.Name.ToString().StartsWith("SERIKA_EVENT_PAN")) { head.Pan=pan;head.OriginalPanBasis=pan.GlobalBasis; }
            foreach(Node child in root.FindChildren("*","MeshInstance3D",true,false)) {
                var mesh=(MeshInstance3D)child;
                if(mesh.Name.ToString().StartsWith("SERIKA_EVENT_BEAM_SOFT")) {
                    _rigSurfaces.Add((mesh,mesh.MaterialOverride,mesh.Visible,mesh.CastShadow));
                    head.Beam=mesh;head.BeamMaterial=new ShaderMaterial{Shader=shader};mesh.MaterialOverride=head.BeamMaterial;
                    mesh.CastShadow=GeometryInstance3D.ShadowCastingSetting.Off;
                    // Set ONCE, never per frame: the throw a fixture has is a property of the
                    // fixture, and a beam that changes length between cues pops.
                    head.BeamScale=mesh.Scale;
                    if(head.Crowd)mesh.Scale=Vector3.One*CrowdBeamScale;
                } else if(mesh.Name.ToString().StartsWith("SERIKA_EVENT_LENS")) {
                    _rigSurfaces.Add((mesh,mesh.MaterialOverride,mesh.Visible,mesh.CastShadow));
                    head.Lens=RigLens();mesh.MaterialOverride=head.Lens;
                }
            }
            foreach(Node child in root.FindChildren("*","SpotLight3D",true,false)) {
                head.Spot=(SpotLight3D)child;head.SpotEnergy=head.Spot.LightEnergy;head.SpotVisible=head.Spot.Visible;
                head.SpotShadow=head.Spot.ShadowEnabled;head.SpotColor=head.Spot.LightColor;head.SpotMask=head.Spot.LightCullMask;
                head.SpotAngle=head.Spot.SpotAngle;head.SpotRange=head.Spot.SpotRange;head.SpotAttenuation=head.Spot.SpotAngleAttenuation;
                head.Spot.ShadowEnabled=false;
                // A crowd wash is deliberately kept OFF the isolated stage receiver layer: it
                // must not spill on the deck, and staying off bit 17 leaves the stage's own
                // eight-spot budget exactly as it was. Wide and long, because it has to arrive.
                head.Spot.LightCullMask=head.Crowd?1u:1u|ScenicStageLayer;
                if(head.Crowd) { head.Spot.SpotAngle=19f;head.Spot.SpotAngleAttenuation=1.1f;head.Spot.SpotRange=72f; }
            }
            if(head.Beam!=null)_movingHeads.Add(head);
        }
        // Real spots are budgeted crowd-first, then rear-first. Only two crowd washes carry a
        // spot, so they claim at most two of the four slots and the rear washes keep the rest;
        // without the crowd term bank 2 fills all four and the audience never gets real light.
        // Four scenic spots plus the performer's four dedicated spots still fit the
        // Mobile/Compatibility eight-light cap on the shared deck.
        _movingHeads.Sort((a,b)=>a.Crowd!=b.Crowd?(a.Crowd?-1:1)
            :a.Bank!=b.Bank?b.Bank.CompareTo(a.Bank):a.Index.CompareTo(b.Index));
        CrowdWashCount=_movingHeads.FindAll(h=>h.Crowd).Count;
        foreach(Node bankNode in world.FindChildren("SERIKA_EVENT_BLINDER_*","Node3D",true,false)) {
            var parts=bankNode.Name.ToString().Split('_');
            if(parts.Length!=4 || !int.TryParse(parts[3],out int bank))continue;
            foreach(Node node in bankNode.FindChildren("SERIKA_EVENT_BLINDER_LENS*","MeshInstance3D",true,false)) {
                var mesh=(MeshInstance3D)node;_rigSurfaces.Add((mesh,mesh.MaterialOverride,mesh.Visible,mesh.CastShadow));
                var mat=RigLens();mesh.MaterialOverride=mat;_blinders.Add((mat,bank));
            }
        }
        BuildSirenBeacons(world,shader);
        BuildRigHits();
    }
    /// Blue rotating alarm beacons for the concealed entrance.
    ///
    /// Deliberately NOT Light3D. `VerifyEffects` asserts every venue light reads zero energy
    /// through the whole entrance, and it asserts that because the entrance is the one passage
    /// where the performer must stay invisible — a real light that grazes her defeats the reveal.
    /// Additive haze volumes and emissive lenses give the beacon its whole visual read without
    /// putting a single new light in the scene, which is also the only version of this that a
    /// GTX 1050 Ti can afford.
    ///
    /// The beam mesh is BORROWED from an authored moving head, so this adds no geometry to the
    /// bundle and no new mesh resource at runtime. Every beam shares ONE material and every dome
    /// shares one more, so the whole system costs two shader writes and six rotations a frame —
    /// and nothing at all outside the entrance, where the root is hidden.
    private void BuildSirenBeacons(Node3D world,Shader shader)
    {
        if(_movingHeads.Count==0)return;
        var beamMesh=_movingHeads[0].Beam?.Mesh;
        if(beamMesh==null)return;
        _sirenRoot=new Node3D { Name="SERIKA_EVENT_SIREN_RIG",Visible=false };
        world.AddChild(_sirenRoot);
        _sirenBeamMaterial=new ShaderMaterial { Shader=shader };
        _sirenBeamMaterial.SetShaderParameter(HazeTint,SirenTint);
        _sirenBeamMaterial.SetShaderParameter(HazeIntensity,0f);
        _sirenDomeMaterial=new StandardMaterial3D {
            ShadingMode=BaseMaterial3D.ShadingModeEnum.Unshaded,EmissionEnabled=true,
            AlbedoColor=Colors.Black,Emission=SirenTint,EmissionEnergyMultiplier=0
        };
        var dome=new SphereMesh { Radius=.30f,Height=.60f,RadialSegments=12,Rings=6 };
        for(int i=0;i<SirenMounts.Length;i++) {
            var (mount,tilt)=SirenMounts[i];
            var unit=new Node3D { Name=$"SERIKA_EVENT_SIREN_{i:00}",Position=mount };
            _sirenRoot.AddChild(unit);
            unit.AddChild(new MeshInstance3D { Name="SERIKA_EVENT_SIREN_DOME",Mesh=dome,
                MaterialOverride=_sirenDomeMaterial,CastShadow=GeometryInstance3D.ShadowCastingSetting.Off });
            var pivot=new Node3D { Name="SERIKA_EVENT_SIREN_PAN" };
            unit.AddChild(pivot);
            // The haze volume's optical axis is local +Y. Swinging it onto -Z (forward) is a
            // -90 degree turn about X; the mount's tilt is the remainder.
            var aim=new Basis(Vector3.Right,-Mathf.DegToRad(90f-tilt)).Scaled(Vector3.One*SirenBeamScale);
            for(int side=0;side<2;side++) {
                var arm=new Node3D { Name="SERIKA_EVENT_SIREN_ARM",Rotation=new Vector3(0,side*Mathf.Pi,0) };
                pivot.AddChild(arm);
                arm.AddChild(new MeshInstance3D { Name="SERIKA_EVENT_SIREN_BEAM",Mesh=beamMesh,
                    MaterialOverride=_sirenBeamMaterial,CastShadow=GeometryInstance3D.ShadowCastingSetting.Off,
                    Transform=new Transform3D(aim,Vector3.Zero) });
            }
            _sirens.Add(new SirenBeacon { Pivot=pivot,Direction=i%2==0?1:-1,Phase=i*1.04f });
        }
    }
    /// Drive the entrance alarm. Pure function of show time — no accumulated tween state.
    private void UpdateSirenBeacons(double seconds,bool live)
    {
        ActiveSirenCount=0;SirenStrength=0;
        if(_sirenRoot==null || !IsInstanceValid(_sirenRoot))return;
        double reveal=_state.Config.RevealTime,start=reveal*SirenStart,span=reveal-start;
        float level=0;
        if(live&&reveal>0&&span>1&&seconds>=start&&seconds<reveal) {
            double tau=seconds-start;float u=(float)(tau/span);
            // Fades up over the first third of its window, then grows into the reveal, where the
            // stage takes over and the alarm cuts. Quadratic, so the last ten seconds carry most
            // of the build rather than the whole entrance sitting at one brightness.
            level=ShowTimeline.Ease(Math.Min(1f,u*3.2f))*(.42f+.58f*u*u);
            // A slow breathing cadence at roughly two-thirds of a hertz. Not a strobe: this has
            // to read as dramatic, and a fast on/off blue light reads as a fault indicator.
            level*=.72f+.28f*(float)Math.Pow(.5+.5*Math.Sin(seconds*Math.Tau*.62),2.2);
            // Rotation speed ramps linearly with time, so the ANGLE is its integral. Writing the
            // angle as rate*t with a varying rate would jump every time the rate changed, and
            // stepping an angle per frame would not survive a seek.
            double angle=SirenBaseRate*tau+(SirenPeakRate-SirenBaseRate)*tau*tau/(2*span);
            foreach(var beacon in _sirens)
                beacon.Pivot.Rotation=new Vector3(0,(float)(angle*beacon.Direction)+beacon.Phase,0);
            ActiveSirenCount=_sirens.Count*2;
        }
        SirenStrength=level;
        bool visible=level>.002f;
        if(!visible)ActiveSirenCount=0;
        if(_sirenRoot.Visible!=visible)_sirenRoot.Visible=visible;
        if(!visible) { if(_sirenWritten!=0) { _sirenWritten=0;WriteSirenLevel(0); } return; }
        // Shared materials, so this is two shader writes and three material writes for the whole
        // system — not per beam. Guarded anyway; the ramp alone moves far slower than a frame.
        if(Math.Abs(_sirenWritten-level)>.002f) { _sirenWritten=level;WriteSirenLevel(level); }
    }
    private void WriteSirenLevel(float level)
    {
        _sirenBeamMaterial.SetShaderParameter(HazeIntensity,level);
        _sirenDomeMaterial.AlbedoColor=SirenTint*(.04f+level*.45f);
        _sirenDomeMaterial.EmissionEnergyMultiplier=level*6f;
    }
    private static bool IsStageLightReceiver(MeshInstance3D mesh,Node3D world)
    {
        string name=mesh.Name;
        if(name.StartsWith("Main performer deck") || name=="Stage fascia navy"
            || name.StartsWith("Band riser") || name=="Backstage apron"
            || name.StartsWith("Moving head fixture"))return true;
        for(Node parent=mesh;parent!=null&&parent!=world;parent=parent.GetParent()) {
            string parentName=parent.Name;
            if(parentName.StartsWith("SERIKA_EVENT_FIXTURE_")
                || parentName.StartsWith("SERIKA_EVENT_BLINDER_"))return true;
        }
        return false;
    }
    private static StandardMaterial3D RigLens()=>new() {
        // Convex glass reacts to nearby real lights; a separate machined bezel stays dark.
        Metallic=.65f,Roughness=.15f,EmissionEnabled=true,
        AlbedoColor=Colors.Black,Emission=Colors.Black,EmissionEnergyMultiplier=0
    };
    private void BuildRigHits()
    {
        _rigHits.Clear();
        var samples=_state.Config.Beats;float fps=_state.Config.MusicFps;
        if(samples==null || !float.IsFinite(fps) || fps<=0)return;
        for(int i=1;i+1<samples.Length;i++) {
            double at=i/(double)fps;
            if(at<_state.Config.RevealTime || samples[i]<.42f || samples[i]<samples[i-1] || samples[i]<=samples[i+1])continue;
            if(_rigHits.Count==0 || at-_rigHits[^1]>=.24)_rigHits.Add(at);
        }
    }
    private int RigHitAt(double seconds,out float pulse)
    {
        int i=_rigHits.BinarySearch(seconds);if(i<0)i=~i-1;
        double age=i>=0?seconds-_rigHits[i]:100;
        // Recorded onsets get a short lamp bump with a readable decay, not a global strobe.
        pulse=age>=0&&age<.42?ShowTimeline.Ease((float)Math.Min(1,age/.045))*(float)Math.Exp(-age*7.0):0;
        return i;
    }
    private void EvaluateLighting(double seconds,bool live,out Color palette,out float energy)
    {
        var cues=_state.Config.Lights;
        if(cues==null || cues.Count==0) { _cueA=_cueB=new ShowLightKey();_cueBlend=1;palette=new Color(.25f,.45f,.8f);energy=.4f;LightingLook=live?"intimate":"preshow";return; }
        int i=0;while(i+1<cues.Count&&cues[i+1].Time<=seconds)i++;
        _cueIndex=i;
        _cueB=cues[i];_cueA=cues[Math.Max(0,i-1)];
        _cueBlend=i==0?1:ShowTimeline.Ease((float)Math.Clamp((seconds-_cueB.Time)/Math.Max(.1,_cueB.Fade),0,1));
        palette=new Color(_cueA.Color[0],_cueA.Color[1],_cueA.Color[2]).Lerp(new Color(_cueB.Color[0],_cueB.Color[1],_cueB.Color[2]),_cueBlend);
        energy=Mathf.Lerp(_cueA.Energy,_cueB.Energy,_cueBlend);
        LightingLook=live?_cueB.Look:"preshow";
    }
    private static float Shutter(string look,MovingHead h)
    {
        // Audience washes only open on the looks that are ABOUT the room — the reveal, the lifts
        // and the finales. Every quiet look (black, intimate, side, hold) leaves them shut, so
        // the negative space the whole rig is built around is unchanged: a ballad still has the
        // audience in darkness, and the crowd wash is an event when it arrives.
        if(h.Crowd)return look switch {
            "reveal"=>1.0f,
            "finale"=>1.25f,
            "drive"=>1.05f,
            "lift"=>.85f,
            "sweep"=>.95f,
            "anthem"=>.75f,
            _=>0
        };
        // Sparse, offset groups. Fixtures never converge on centre stage or sweep through screens.
        if(h.Bank==0)return look switch {
            "entrance"=>h.Index is 1 or 10?.30f:0,
            "intimate"=>h.Index is 1 or 7?.24f:0,
            "side"=>h.Index is 1 or 4 or 8?.50f:0,
            "lift"=>h.Index is 1 or 3 or 4 or 7 or 8 or 10?.85f:0,
            "sweep"=>h.Index is 0 or 1 or 4 or 5 or 7 or 10?.85f:0,
            "anthem"=>h.Index is 1 or 3 or 7 or 8?.65f:0,
            "reveal"=>h.Index is 1 or 3 or 5 or 7 or 8 or 10?.95f:0,
            "drive"=>h.Index is 0 or 1 or 3 or 4 or 7 or 8 or 10?1f:0,
            "finale"=>h.Index is 0 or 1 or 3 or 4 or 5 or 7 or 8 or 10?1.1f:0,
            _=>0
        };
        if(h.Bank==2)return look switch {
            "entrance"=>h.Index is 2 or 9?.24f:0,
            "intimate"=>h.Index is 1 or 9?.28f:0,
            "side"=>h.Index is 0 or 3 or 7 or 10?.58f:0,
            "lift"=>h.Index is 0 or 1 or 2 or 4 or 7 or 9 or 10?.95f:0,
            "sweep"=>h.Index is 1 or 3 or 4 or 6 or 8 or 9?.90f:0,
            "anthem"=>h.Index is 0 or 2 or 4 or 7 or 10?.72f:0,
            "reveal"=>h.Index is 1 or 2 or 4 or 6 or 8 or 10?1.05f:0,
            "drive"=>h.Index is 0 or 2 or 3 or 4 or 7 or 8 or 10?1.05f:0,
            "finale"=>h.Index is 0 or 1 or 2 or 4 or 6 or 7 or 8 or 9 or 10?1.2f:0,
            _=>0
        };
        return look switch {
            "side"=>h.Index is 7?.25f:0,
            "lift"=>h.Index is 1 or 4 or 10?.45f:0,
            "sweep"=>h.Index is 4 or 10?.40f:0,
            "anthem"=>h.Index is 4 or 10?.28f:0,
            "reveal"=>h.Index is 0 or 4 or 7 or 11?.54f:0,
            "drive"=>h.Index is 1 or 10?.65f:0,
            "finale"=>h.Index is 1 or 4 or 7 or 10?.64f:0,
            _=>0
        };
    }
    private static Vector3 HeadTarget(ShowLightKey cue,MovingHead h,double seconds)
    {
        if(h.Crowd) {
            // Out over the audience floor. The aim point is on the GROUND, not at standing eye
            // height — the shaft still rakes over the crowd on its way there, but its axis never
            // lies along the row of faces, which in a headset is the difference between a wash
            // and being stared at by a searchlight.
            float fan=(float)Math.Sin((seconds-cue.Time)*(cue.Look is "finale" or "drive"?.30:.18)+h.Index*1.05);
            return new Vector3(-24+h.Index*4.4f+fan*8.5f,1.1f,16+(h.Index%3)*11f);
        }
        float x=h.Root.GlobalPosition.X;
        // Movement is an intentionally slow phrase gesture only on lift/sweep cues.
        float travel=cue.Look is "sweep" or "lift" or "drive" or "finale" ? (float)Math.Sin((seconds-cue.Time)*.10+h.Index*.7)*2.4f:0;
        float offset=h.Index%3==0?3.5f:h.Index%3==1?-2.5f:1.2f;
        if(h.Bank==2) {
            int slot=h.Index%6;float side=h.Index<6?-1:1;
            // Low rear heads cross high above the singer. Upper heads carve diagonal pools
            // behind her; none aim toward audience-eye height or the portrait screen planes.
            return slot switch {
                0=>new Vector3(-side*9+travel,23,-41.5f),
                1=>new Vector3(-side*5+travel,28,-42.5f),
                2=>new Vector3(side*5+travel,17,-43),
                3=>new Vector3(side*8+travel,9.2f,-42),
                4=>new Vector3(side*10+travel,4.3f,-41.5f),
                _=>new Vector3(side*5+travel,4.3f,-43)
            };
        }
        return h.Bank==0
            ?new Vector3(x+offset+travel,30,-48+(h.Index%3)*1.5f)
            :new Vector3(x+offset+travel,4.24f,-48+(h.Index%3)*2.0f);
    }
    private void UpdateConcertRig(double seconds,bool live,Color palette,float music,float beat,bool highQuality)
    {
        ActiveShaftCount=0;ActiveScenicSpotCount=0;
        UpdateSirenBeacons(seconds,live);
        bool open=live&&seconds>=_state.Config.RevealTime;
        float reveal=open?RevealLevel:0;
        int hit=RigHitAt(seconds,out float pulse);
        bool rhythmic=_cueB.Look is "drive" or "finale";
        foreach(var h in _movingHeads) {
            bool enabled = highQuality || ((h.Index + h.Bank) % 2 == 0);
            Vector3 target=HeadTarget(_cueA,h,seconds).Lerp(HeadTarget(_cueB,h,seconds),_cueBlend);
            Vector3 horizontal=target-h.Root.GlobalPosition;horizontal.Y=0;
            if(h.Pan!=null&&horizontal.LengthSquared()>.001f)h.Pan.GlobalBasis=Basis.LookingAt(horizontal.Normalized(),Vector3.Up);
            h.Root.GlobalBasis=Basis.LookingAt((target-h.Root.GlobalPosition).Normalized(),Vector3.Up)*new Basis(Vector3.Right,-Mathf.Pi/2);
            float intensity=open?Mathf.Lerp(Shutter(_cueA.Look,h),Shutter(_cueB.Look,h),_cueBlend):0;
            if (!enabled) intensity=0;
            // Deterministic hit-index groups survive seeks and peer sync. At most two heads
            // in each bank receive a hit; the rest hold the authored theatre look.
            bool selected=enabled&&intensity>.005f&&rhythmic&&hit>=0&&(h.Index+h.Bank*2)%6==hit%6;
            if(open&&selected)intensity*=1f+pulse*.65f;
            intensity*=reveal*(.90f+.10f*music)*Mathf.Clamp(Mathf.Lerp(_cueA.Energy,_cueB.Energy,_cueBlend),0,1.8f);
            if(!highQuality) intensity*=0.75f;
            // A crowd wash keeps most of the cue colour — the point of pointing a fixture at the
            // audience is that the room takes the show's colour. The stage-facing bank-1 heads
            // stay near-white because they are separation light on the singer, not colour.
            Color tint=h.Crowd?palette.Lerp(new Color(.58f,.74f,1f),.22f)
                :h.Bank==1?palette.Lerp(new Color(.70f,.78f,.85f),.65f)
                :h.Bank==2?palette.Lerp(new Color(.63f,.76f,.92f),.18f):palette;
            if(selected)tint=tint.Lerp(new Color(.95f,.90f,.80f),pulse*.28f);
            h.Beam.Visible=intensity>.005f;
            if(h.Beam.Visible)ActiveShaftCount++;
            h.BeamMaterial.SetShaderParameter(HazeTint,tint);
            h.BeamMaterial.SetShaderParameter(HazeIntensity,intensity);
            if(h.Lens!=null) { h.Lens.AlbedoColor=open?tint*(.012f+intensity*.22f):Colors.Black;h.Lens.Emission=open?tint:Colors.Black;h.Lens.EmissionEnergyMultiplier=intensity*2.6f; }
            if(h.Spot!=null) {
                int scenicSpotLimit = highQuality ? 4 : 2;
                bool active=open&&enabled&&intensity>.02f&&h.SpotVisible&&ActiveScenicSpotCount<scenicSpotLimit;
                h.Spot.Visible=active;h.Spot.LightColor=tint;
                h.Spot.LightEnergy=active?intensity*(h.Crowd?5.5f:h.Bank==2?8f:h.Bank==1?4f:2f):0;
                if(active)ActiveScenicSpotCount++;
            }
        }
        // Single operator-authored bumps, with a soft 120ms attack and 900ms decay.
        // No onset-triggered blinder bank and no periodic strobing.
        double age=seconds-_cueB.Time;
        BlinderStrength=open&&age>=0&&age<1.2?reveal*_cueB.Accent*ShowTimeline.Ease((float)Math.Clamp(age/.12,0,1))*(1-ShowTimeline.Ease((float)Math.Clamp((age-.12)/1.08,0,1))):0;
        var warm=new Color(1,.72f,.40f);
        foreach(var bank in _blinders) {
            float level=bank.Bank%2==_cueIndex%2?BlinderStrength:0;
            bank.Material.AlbedoColor=open?warm*(.008f+level*.35f):Colors.Black;
            bank.Material.Emission=open?warm:Colors.Black;bank.Material.EmissionEnergyMultiplier=level*2;
        }
    }
    private void RestoreConcertRig()
    {
        foreach(var receiver in _stageReceivers) if(IsInstanceValid(receiver.Mesh))receiver.Mesh.Layers=receiver.Layers;
        foreach(var surface in _rigSurfaces) if(IsInstanceValid(surface.Mesh)) { surface.Mesh.MaterialOverride=surface.Original;surface.Mesh.Visible=surface.Visible; surface.Mesh.CastShadow=surface.Shadow; }
        foreach(var head in _movingHeads) {
            if(IsInstanceValid(head.Pan))head.Pan.GlobalBasis=head.OriginalPanBasis;
            if(IsInstanceValid(head.Root))head.Root.GlobalBasis=head.OriginalBasis;
            if(IsInstanceValid(head.Beam))head.Beam.Scale=head.BeamScale;
            if(IsInstanceValid(head.Spot)) { head.Spot.LightEnergy=head.SpotEnergy;head.Spot.LightColor=head.SpotColor;head.Spot.Visible=head.SpotVisible;head.Spot.ShadowEnabled=head.SpotShadow;head.Spot.LightCullMask=head.SpotMask;
                head.Spot.SpotAngle=head.SpotAngle;head.Spot.SpotRange=head.SpotRange;head.Spot.SpotAngleAttenuation=head.SpotAttenuation; }
        }
        // The beacons are ours end to end, so teardown is a free, not a restore.
        if(IsInstanceValid(_sirenRoot))_sirenRoot.QueueFree();
        _sirenRoot=null;_sirenBeamMaterial=null;_sirenDomeMaterial=null;_sirenWritten=-1;
        SirenStrength=0;ActiveSirenCount=0;CrowdWashCount=0;
        _sirens.Clear();
        _stageReceivers.Clear();_rigSurfaces.Clear();_movingHeads.Clear();_blinders.Clear();_rigHits.Clear();
    }
}
