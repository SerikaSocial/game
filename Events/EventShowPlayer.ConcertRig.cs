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
        public MeshInstance3D Beam;
        public ShaderMaterial BeamMaterial;
        public StandardMaterial3D Lens;
        public SpotLight3D Spot;
        public float SpotEnergy;
        public uint SpotMask;
        public bool SpotVisible, SpotShadow;
        public Color SpotColor;
    }
    private readonly List<MovingHead> _movingHeads = new();
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
            var head=new MovingHead { Root=root,OriginalBasis=root.GlobalBasis,Bank=bank,Index=index };
            if(root.GetParent() is Node3D pan && pan.Name.ToString().StartsWith("SERIKA_EVENT_PAN")) { head.Pan=pan;head.OriginalPanBasis=pan.GlobalBasis; }
            foreach(Node child in root.FindChildren("*","MeshInstance3D",true,false)) {
                var mesh=(MeshInstance3D)child;
                if(mesh.Name.ToString().StartsWith("SERIKA_EVENT_BEAM_SOFT")) {
                    _rigSurfaces.Add((mesh,mesh.MaterialOverride,mesh.Visible,mesh.CastShadow));
                    head.Beam=mesh;head.BeamMaterial=new ShaderMaterial{Shader=shader};mesh.MaterialOverride=head.BeamMaterial;
                    mesh.CastShadow=GeometryInstance3D.ShadowCastingSetting.Off;
                } else if(mesh.Name.ToString().StartsWith("SERIKA_EVENT_LENS")) {
                    _rigSurfaces.Add((mesh,mesh.MaterialOverride,mesh.Visible,mesh.CastShadow));
                    head.Lens=RigLens();mesh.MaterialOverride=head.Lens;
                }
            }
            foreach(Node child in root.FindChildren("*","SpotLight3D",true,false)) {
                head.Spot=(SpotLight3D)child;head.SpotEnergy=head.Spot.LightEnergy;head.SpotVisible=head.Spot.Visible;
                head.SpotShadow=head.Spot.ShadowEnabled;head.SpotColor=head.Spot.LightColor;head.SpotMask=head.Spot.LightCullMask;
                head.Spot.ShadowEnabled=false;
                head.Spot.LightCullMask=1u|ScenicStageLayer;
            }
            if(head.Beam!=null)_movingHeads.Add(head);
        }
        // Real spots are budgeted rear-first. Four scenic spots plus the performer's four
        // dedicated spots fit the Mobile/Compatibility eight-light cap on the shared deck.
        _movingHeads.Sort((a,b)=>a.Bank!=b.Bank?b.Bank.CompareTo(a.Bank):a.Index.CompareTo(b.Index));
        foreach(Node bankNode in world.FindChildren("SERIKA_EVENT_BLINDER_*","Node3D",true,false)) {
            var parts=bankNode.Name.ToString().Split('_');
            if(parts.Length!=4 || !int.TryParse(parts[3],out int bank))continue;
            foreach(Node node in bankNode.FindChildren("SERIKA_EVENT_BLINDER_LENS*","MeshInstance3D",true,false)) {
                var mesh=(MeshInstance3D)node;_rigSurfaces.Add((mesh,mesh.MaterialOverride,mesh.Visible,mesh.CastShadow));
                var mat=RigLens();mesh.MaterialOverride=mat;_blinders.Add((mat,bank));
            }
        }
        BuildRigHits();
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
        // Sparse, offset groups. Fixtures never converge on centre stage or sweep through screens.
        if(h.Bank==0)return look switch {
            "entrance"=>h.Index is 1 or 10?.30f:0,
            "intimate"=>h.Index is 1 or 7?.24f:0,
            "side"=>h.Index is 1 or 4 or 8?.50f:0,
            "lift"=>h.Index is 1 or 3 or 7 or 10?.75f:0,
            "sweep"=>h.Index is 1 or 4 or 7 or 10?.65f:0,
            "anthem"=>h.Index is 1 or 8?.45f:0,
            "reveal"=>h.Index is 1 or 3 or 5 or 8 or 10?.72f:0,
            "drive"=>h.Index is 1 or 4 or 8?.56f:0,
            "finale"=>h.Index is 0 or 1 or 3 or 5 or 8 or 10?.82f:0,
            _=>0
        };
        if(h.Bank==2)return look switch {
            "entrance"=>h.Index is 2 or 9?.24f:0,
            "intimate"=>h.Index is 1 or 9?.28f:0,
            "side"=>h.Index is 0 or 3 or 7 or 10?.58f:0,
            "lift"=>h.Index is 0 or 2 or 4 or 7 or 9?.78f:0,
            "sweep"=>h.Index is 1 or 4 or 6 or 9?.65f:0,
            "anthem"=>h.Index is 0 or 2 or 7 or 10?.58f:0,
            "reveal"=>h.Index is 1 or 2 or 4 or 6 or 8 or 10?.9f:0,
            "drive"=>h.Index is 0 or 3 or 7 or 10?.62f:0,
            "finale"=>h.Index is 0 or 2 or 4 or 6 or 8 or 10?.95f:0,
            _=>0
        };
        return look switch {
            "side"=>h.Index is 7?.25f:0,
            "lift"=>h.Index is 1 or 4 or 10?.45f:0,
            "sweep"=>h.Index is 4 or 10?.40f:0,
            "anthem"=>h.Index is 4 or 10?.28f:0,
            "reveal"=>h.Index is 0 or 4 or 7 or 11?.54f:0,
            "drive"=>h.Index is 2 or 7?.40f:0,
            "finale"=>h.Index is 1 or 4 or 7 or 10?.64f:0,
            _=>0
        };
    }
    private static Vector3 HeadTarget(ShowLightKey cue,MovingHead h,double seconds)
    {
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
            bool selected=rhythmic&&hit>=0&&(h.Index+h.Bank*2)%6==hit%6;
            if(open&&selected)intensity=Mathf.Max(intensity,pulse*1.6f);
            intensity*=reveal*(.90f+.10f*music);
            if(!highQuality) intensity*=0.75f;
            Color tint=h.Bank==1?palette.Lerp(new Color(.70f,.78f,.85f),.65f):h.Bank==2?palette.Lerp(new Color(.63f,.76f,.92f),.18f):palette;
            if(selected)tint=tint.Lerp(new Color(.95f,.90f,.80f),pulse*.28f);
            h.Beam.Visible=intensity>.005f;
            if(h.Beam.Visible)ActiveShaftCount++;
            h.BeamMaterial.SetShaderParameter(HazeTint,tint);
            h.BeamMaterial.SetShaderParameter(HazeIntensity,intensity);
            if(h.Lens!=null) { h.Lens.AlbedoColor=open?tint*(.012f+intensity*.22f):Colors.Black;h.Lens.Emission=open?tint:Colors.Black;h.Lens.EmissionEnergyMultiplier=intensity*2.6f; }
            if(h.Spot!=null) {
                int scenicSpotLimit = highQuality ? 4 : 2;
                bool active=open&&enabled&&intensity>.02f&&h.SpotVisible&&ActiveScenicSpotCount<scenicSpotLimit;
                h.Spot.Visible=active;h.Spot.LightColor=tint;h.Spot.LightEnergy=active?intensity*(h.Bank==2?8f:h.Bank==1?4f:2f):0;
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
            if(IsInstanceValid(head.Spot)) { head.Spot.LightEnergy=head.SpotEnergy;head.Spot.LightColor=head.SpotColor;head.Spot.Visible=head.SpotVisible;head.Spot.ShadowEnabled=head.SpotShadow;head.Spot.LightCullMask=head.SpotMask; }
        }
        _stageReceivers.Clear();_rigSurfaces.Clear();_movingHeads.Clear();_blinders.Clear();_rigHits.Clear();
    }
}
