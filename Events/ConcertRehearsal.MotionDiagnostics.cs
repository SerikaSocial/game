using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
namespace SerikaSocial.Events;
public partial class ConcertRehearsal
{
    private void VerifyMotion()
    {
        var ranges=new List<(string Name,double Start,double End,string Kind)>();
        var boundaries=new List<double>();
        string timelinePath=Path.Combine(_folder,"motion-timeline.json");
        bool authoredTimeline=File.Exists(timelinePath);
        if(authoredTimeline) {
            using var timeline=JsonDocument.Parse(File.ReadAllText(timelinePath));
            var root=timeline.RootElement;
            Check(Math.Abs(root.GetProperty("revealTime").GetDouble()-_event.Config.RevealTime)<.001,"motion metadata agrees with the first-verse reveal clock");
            foreach(var range in root.GetProperty("diagnosticsRanges").EnumerateArray())
                ranges.Add((range.GetProperty("name").GetString(),range.GetProperty("start").GetDouble(),range.GetProperty("end").GetDouble(),range.GetProperty("kind").GetString()));
            boundaries.AddRange(root.GetProperty("diagnosticsBoundaries").EnumerateArray().Select(v=>v.GetDouble()));
            var choruses=root.GetProperty("chorusWindows").EnumerateArray().Select(v=>(Start:v.GetProperty("start").GetDouble(),End:v.GetProperty("end").GetDouble())).ToArray();
            var cues=root.GetProperty("danceCues").EnumerateArray().ToArray();
            // Every authored dance cue must have a matching probe range. This used to demand
            // exactly three, which silently made the choreography count part of the contract:
            // adding a fourth dance, or dropping a song that carried one, failed the verifier
            // without anything actually being wrong with the motion.
            Check(cues.Length>0&&ranges.Count(r=>r.Kind=="dance")==cues.Length,
                $"every authored chorus excerpt is verified ({cues.Length} cues, {ranges.Count(r=>r.Kind=="dance")} probed)");
            Check(cues.All(c=>choruses.Any(w=>c.GetProperty("start").GetDouble()>=w.Start && c.GetProperty("start").GetDouble()+c.GetProperty("duration").GetDouble()<=w.End+.0001)),"official choreography stays entirely inside verified chorus sections");
            Check(ranges.All(r=>r.Start>=0&&r.End>r.Start&&r.End<=_event.Config.Duration),"motion diagnostics ranges are valid for the authored duration");
        } else {
            ranges.AddRange(new[]{("entrance",2.7,13.3,"entrance"),("entrance turn",14.0,16.0,"turn"),("greeting",16.2,19.5,"standing"),("BIBBIDIBA choreography",67.0,84.0,"dance"),("BIBBIDIBA singing",25.0,60.0,"standing"),("KAIJU singing",270.0,500.0,"standing"),("Stellar singing",540.0,850.0,"standing")});
            boundaries.AddRange(new[]{14.0,14.3,19.6,20.0,66.0,85.15,150.0,169.15,206.0,225.15,243.234,513.608});
        }
        var artist=_show.Performer;var skeleton=artist.Skeleton;var roles=artist.RoleToBoneForDiagnostics();
        var globals=new Transform3D[skeleton.GetBoneCount()];var previous=new Vector3[2];
        int[] feet={roles["leftFoot"],roles["rightFoot"]};
        float[] restFloor=feet.Select(i=>skeleton.GetBoneGlobalRest(i).Origin.Y).ToArray();
        var rows=new List<object>();bool finite=true;int[] driven=roles.Values.Distinct().ToArray();var lastRotation=new Quaternion[skeleton.GetBoneCount()];var labels=roles.GroupBy(p=>p.Value).ToDictionary(g=>g.Key,g=>g.First().Key);
        foreach(var range in ranges) {
            var speeds=new List<float>();var groundedSpeeds=new List<float>();float minHead=100,minHipFootDrop=100,maxStep=0,maxJointAngle=0,maxWristAngle=0,maxKneeAngle=0;double maxStepAt=0,maxJointAt=0;string maxStepFoot="",maxJointRole="";float maxStepAnkleClearance=0;bool havePrevious=false;var previousLow=new bool[2];var previousGrounded=new bool[2];
            for(double t=range.Start;t<=range.End;t+=1.0/60) {
                _show.SampleMotionForDiagnostics(t);
                for(int i=0;i<globals.Length;i++) {
                    int p=skeleton.GetBoneParent(i);var local=skeleton.GetBonePose(i);globals[i]=p>=0?globals[p]*local:local;
                    finite &= local.Origin.IsFinite() && local.Basis.GetRotationQuaternion().IsFinite();
                }
                foreach(int bone in driven) {
                    var current=skeleton.GetBonePoseRotation(bone);
                    if(havePrevious) {float angle=current.AngleTo(lastRotation[bone]);if(angle>maxJointAngle){maxJointAngle=angle;maxJointAt=t;maxJointRole=labels[bone];}if(bone==roles["leftLowerLeg"]||bone==roles["rightLowerLeg"])maxKneeAngle=Math.Max(maxKneeAngle,angle);if(bone==roles["leftHand"]||bone==roles["rightHand"])maxWristAngle=Math.Max(maxWristAngle,angle);}
                    lastRotation[bone]=current;
                }
                minHead=Math.Min(minHead,globals[roles["head"]].Origin.Y);
                minHipFootDrop=Math.Min(minHipFootDrop,globals[roles["hips"]].Origin.Y-Math.Min(globals[feet[0]].Origin.Y,globals[feet[1]].Origin.Y));
                for(int f=0;f<2;f++) {
                    var local=globals[feet[f]].Origin;var world=skeleton.ToGlobal(local);bool low=local.Y<restFloor[f]+.032f;bool grounded=local.Y<restFloor[f]+.005f;
                    if(havePrevious) {float speed=new Vector2(world.X-previous[f].X,world.Z-previous[f].Z).Length()*60;if(speed>maxStep){maxStep=speed;maxStepAt=t;maxStepFoot=f==0?"leftFoot":"rightFoot";maxStepAnkleClearance=local.Y-restFloor[f];}if(low&&previousLow[f])speeds.Add(speed);if(grounded&&previousGrounded[f])groundedSpeeds.Add(speed);}
                    previous[f]=world;previousLow[f]=low;previousGrounded[f]=grounded;
                }
                havePrevious=true;
            }
            speeds.Sort();float p95=speeds.Count>0?speeds[(int)((speeds.Count-1)*.95f)]:0;float peak=speeds.Count>0?speeds[^1]:0;
            groundedSpeeds.Sort();float ground95=groundedSpeeds.Count>0?groundedSpeeds[(int)((groundedSpeeds.Count-1)*.95f)]:0;float groundMax=groundedSpeeds.Count>0?groundedSpeeds[^1]:0;
            rows.Add(new{segment=range.Name,startSeconds=range.Start,endSeconds=range.End,kind=range.Kind,maxJointStepDegrees=Mathf.RadToDeg(maxJointAngle),maxJointStepRole=maxJointRole,maxJointStepEndSeconds=maxJointAt,maxKneeStepDegrees=Mathf.RadToDeg(maxKneeAngle),maxWristStepDegrees=Mathf.RadToDeg(maxWristAngle),groundContactSamples=groundedSpeeds.Count,groundContactSpeedP95=ground95,groundContactSpeedMax=groundMax,nearFloorSamples=speeds.Count,nearFloorSpeedP95=p95,nearFloorSpeedMax=peak,allFootWorldSpeedMax=maxStep,maxFootSpeedRole=maxStepFoot,maxFootSpeedEndSeconds=maxStepAt,maxFootSpeedAnkleClearance=maxStepAnkleClearance,minimumHipToFootDrop=minHipFootDrop,minimumHeadHeight=minHead});
            // Near-floor speed includes heel takeoff/landing. The 5mm band measures actual planted contact.
            Check(groundedSpeeds.Count>60&&speeds.Count>60,$"{range.Name}: stance assertions have enough measured contacts");
            bool standing=range.Kind=="standing";
            if(standing||range.Kind=="entrance")Check(minHipFootDrop>.4f,$"{range.Name}: pelvis remains vertically above planted feet");
            if(standing)Check(p95<.015f&&peak<.06f,$"{range.Name}: planted feet remain stationary under runtime interpolation");
            if(range.Kind=="entrance")Check(maxKneeAngle<.21f&&maxStep<5f,"captured entrance swing stays within reachable leg geometry without rapid knee flexion");
            if(range.Kind=="entrance")Check(ground95<.08f&&groundMax<.30f,"captured entrance holds planted contacts while following actual stage travel (near-floor swing is reported separately)");
            Check(maxWristAngle<.26f&&maxJointAngle<.45f,$"{range.Name}: wrists and humanoid joints have no one-frame rotation snaps");
            Check(minHead>.95f,$"{range.Name}: retargeted head remains above stage and legs do not fold horizontally");
            GD.Print($"MOTION {range.Name} plantedP95={ground95:F3}m/s plantedPeak={groundMax:F3}m/s nearFloorP95={p95:F3}m/s nearFloorPeak={peak:F3}m/s headMin={minHead:F3}m samples={speeds.Count}");
        }
        Check(_show.UsesBakedMotion,"runtime validates and samples the exact supplied VRM bake");
        Check(finite,"all captured mocap pose samples are finite in runtime");
        
        float boundaryMax=0;double boundaryAt=0;string boundaryRole="";var beforeBoundary=new Quaternion[skeleton.GetBoneCount()];
        foreach(double boundary in boundaries) {
            _show.SampleMotionForDiagnostics(boundary-.016);
            foreach(int bone in driven)beforeBoundary[bone]=skeleton.GetBonePoseRotation(bone);
            _show.SampleMotionForDiagnostics(boundary+.016);
            foreach(int bone in driven) {float angle=beforeBoundary[bone].AngleTo(skeleton.GetBonePoseRotation(bone));if(angle>boundaryMax){boundaryMax=angle;boundaryAt=boundary;boundaryRole=labels[bone];}}
        }
        Check(boundaryMax<.25f,$"clip/song boundary rotations remain continuous ({Mathf.RadToDeg(boundaryMax):F2} degrees across 32ms)");
        File.WriteAllText(Path.Combine(_captureFolder,"motion-runtime.json"),JsonSerializer.Serialize(new{exactVrmBake=_show.UsesBakedMotion,finite,authoredTimeline,plantedHeightBandMetres=.005,nearFloorHeightBandMetres=.032,boundaryMaximumDegrees=Mathf.RadToDeg(boundaryMax),boundaryMaximumRole=boundaryRole,boundaryMaximumSeconds=boundaryAt,metrics=rows},new JsonSerializerOptions{WriteIndented=true}));
        _show.SampleMotionForDiagnostics(_event.Config.RevealTime);
    }
}
