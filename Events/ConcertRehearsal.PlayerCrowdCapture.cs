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
    /// Exercise the shipping avatar, cheer overlay and both BoneAttachment3D props.
    /// The camera-mounted rehearsal demonstration is hidden throughout this capture.
    private async Task CapturePlayerCrowd()
    {
        Check(_ready&&_show.Performer?.Skeleton!=null,"real avatar is ready for paired cheer capture");
        if(!_ready||_show.Performer?.Skeleton==null)return;
        var avatar=_show.Performer;var skeleton=avatar.Skeleton;
        int[] hands={avatar.BoneOf("rightHand"),avatar.BoneOf("leftHand")};
        int head=avatar.BoneOf("head");
        int[] arms=new[]{"rightShoulder","rightUpperArm","rightLowerArm","rightHand",
            "leftShoulder","leftUpperArm","leftLowerArm","leftHand"}.Select(avatar.BoneOf).ToArray();
        if(hands.Any(h=>h<0)||head<0||arms.Any(h=>h<0)) { Check(false,"paired cheer capture needs mapped arm bones");return; }
        var savedBones=Enumerable.Range(0,skeleton.GetBoneCount()).Select(skeleton.GetBonePose).ToArray();
        var savedAvatar=avatar.GlobalTransform;bool savedAvatarVisible=avatar.Visible,savedCheer=avatar.ConcertCheerEnabled;
        bool savedProcess=_show.IsProcessing(),savedPhysics=IsPhysicsProcessing();
        bool savedPreview=_show.Preview,savedPlaying=_show.PreviewPlaying;double savedPosition=_show.PreviewPosition;
        bool savedCapture=_captureActive,savedBroadcast=_broadcastView,savedPanel=_panel.Visible;
        bool savedCameraTop=_camera.TopLevel;var savedCamera=_camera.GlobalTransform;float savedFov=_camera.Fov;
        var savedWindowMode=GetWindow().Mode;var savedWindowSize=GetWindow().Size;
        bool hadProps=skeleton.FindChild("ConcertBlueLightStickRight",true,false)!=null
            || skeleton.FindChild("ConcertBlueLightStickLeft",true,false)!=null;
        var props=new Node3D[2];var originalProps=new Transform3D[2];
        float maxPositionError=0,maxAxisError=0,maxArmStep=0,maxHandStep=0,minHandGap=float.PositiveInfinity;
        float minRaisedStickUpDot=1;
        float[] maxLift={-100,-100};int overhead=0;
        var records=new List<object>();var captureStates=new Dictionary<int,(string Name,double Time)> {
            [24]=("01-raising",.4),[48]=("02-overhead",.8),[60]=("03-pump",1.0),[78]=("04-lowering",1.3)
        };
        var previousArms=new Quaternion[arms.Length];var previousHands=new Vector3[2];
        try {
            _panel.Hide();_captureActive=true;_broadcastView=false;SetPhysicsProcess(false);
            GetWindow().Mode=Window.ModeEnum.Windowed;GetWindow().Size=new Vector2I(1280,720);
            PlayAt(_event.Config.RevealTime+5);_show.PreviewPlaying=false;
            await ToSignal(GetTree().CreateTimer(.15),SceneTreeTimer.SignalName.Timeout);
            _show.SetProcess(false);_camera.TopLevel=true;avatar.Visible=true;
            for(int side=0;side<2;side++) {
                props[side]=_show.AttachAvatarLightStick(avatar,side==1);
                if(props[side]==null)throw new InvalidOperationException("A paired avatar light stick did not attach.");
                originalProps[side]=props[side].Transform;
            }
            avatar.SetConcertCheerEnabled(true);
            for(int frame=0;frame<240;frame++) {
                double time=frame/60.0;
                await ToSignal(GetTree(),SceneTree.SignalName.PhysicsFrame);
                avatar.SetConcertCheerClock(time);avatar.Animate(1.0/60,0,true);
                _show.SamplePlayerLightStickPresentation(time,true);
                // Let the real Skeleton3D update its BoneAttachment children before measuring.
                await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
                await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
                var handPositions=new Vector3[2];var tipPositions=new Vector3[2];
                var headWorld=skeleton.GlobalTransform*skeleton.GetBoneGlobalPose(head).Origin;
                for(int side=0;side<2;side++) {
                    var wrist=skeleton.GlobalTransform*skeleton.GetBoneGlobalPose(hands[side]);
                    var expected=wrist*props[side].Transform;var actual=props[side].GlobalTransform;
                    maxPositionError=Math.Max(maxPositionError,expected.Origin.DistanceTo(actual.Origin));
                    maxAxisError=Math.Max(maxAxisError,Mathf.RadToDeg(Mathf.Acos(Math.Clamp(expected.Basis.Y.Normalized().Dot(actual.Basis.Y.Normalized()),-1,1))));
                    handPositions[side]=wrist.Origin;tipPositions[side]=actual*new Vector3(0,.30f,0);
                    if(frame>=30)minRaisedStickUpDot=Math.Min(minRaisedStickUpDot,actual.Basis.Y.Normalized().Dot(Vector3.Up));
                    maxLift[side]=Math.Max(maxLift[side],wrist.Origin.Y-headWorld.Y);
                    if(frame>0)maxHandStep=Math.Max(maxHandStep,wrist.Origin.DistanceTo(previousHands[side]));
                    previousHands[side]=wrist.Origin;
                }
                if(handPositions.All(p=>p.Y>headWorld.Y+.10f))overhead++;
                minHandGap=Math.Min(minHandGap,handPositions[0].DistanceTo(handPositions[1]));
                for(int j=0;j<arms.Length;j++) {
                    var rotation=skeleton.GetBonePoseRotation(arms[j]);
                    if(frame>0)maxArmStep=Math.Max(maxArmStep,rotation.AngleTo(previousArms[j]));
                    previousArms[j]=rotation;
                }
                records.Add(new {time,rightHand=ShowTimeline.Array(handPositions[0]),leftHand=ShowTimeline.Array(handPositions[1]),
                    rightTip=ShowTimeline.Array(tipPositions[0]),leftTip=ShowTimeline.Array(tipPositions[1])});
                if(captureStates.TryGetValue(frame,out var sample)) {
                    var origin=avatar.GlobalPosition;
                    _camera.GlobalPosition=origin+new Vector3(.2f,1.18f,3.7f);
                    _camera.LookAt(origin+new Vector3(0,1.12f,0));_camera.Fov=39;
                    await Snapshot("player-cheer-"+sample.Name);
                    if(frame==48)foreach(int side in new[]{0,1}) {
                        var center=props[side].GlobalPosition;
                        // Grip X is the back-of-hand normal; inspect each mirrored
                        // palm from its own front rather than using a world-axis camera.
                        _camera.GlobalPosition=center-props[side].GlobalBasis.X.Normalized()*.42f
                            +props[side].GlobalBasis.Y.Normalized()*.04f+props[side].GlobalBasis.Z.Normalized()*.10f;
                        _camera.LookAt(center+props[side].GlobalBasis.Y.Normalized()*.07f);_camera.Fov=45;
                        await Snapshot(side==0?"player-cheer-right-grip":"player-cheer-left-grip");
                    }
                }
            }
            Check(avatar.ConcertCheerReady,"captured cheer cache drives the prepared avatar");
            Check(maxPositionError<.002f&&maxAxisError<.15f,"both props remain on their actual solved wrists throughout four seconds");
            Check(overhead>30&&maxLift.All(h=>h>.20f),"both hands raise the blue light sticks above the head");
            Check(float.IsFinite(maxArmStep)&&maxArmStep<.35f,"captured arm loop remains continuous during the real attachment capture");
            Check(minHandGap>.18f,"paired hands remain separated instead of crossing through each other");
            Check(minRaisedStickUpDot>.30f,"cheering sticks stay upward instead of pointing backward into the shoulders");
            File.WriteAllText(Path.Combine(_captureFolder,"player-cheer-metrics.json"),JsonSerializer.Serialize(new {
                passed=avatar.ConcertCheerReady&&maxPositionError<.002f&&maxAxisError<.15f&&overhead>30&&maxLift.All(h=>h>.20f)&&maxArmStep<.35f&&minHandGap>.18f&&minRaisedStickUpDot>.30f,
                samples=records.Count,source="existing project Victory clip; mirrored arm adaptation; actual supplied avatar and authored props",
                maxGripPositionErrorMetres=maxPositionError,maxGripAxisErrorDegrees=maxAxisError,maxArmStepDegrees=Mathf.RadToDeg(maxArmStep),
                maxHandStepMetres=maxHandStep,minHandSeparationMetres=minHandGap,minimumRaisedStickUpDot=minRaisedStickUpDot,maxRightHandAboveHeadMetres=maxLift[0],maxLeftHandAboveHeadMetres=maxLift[1],overheadFrames=overhead,
                captures=captureStates.Values.Select(s=>new {s.Name,s.Time}),trajectory=records
            },new JsonSerializerOptions{WriteIndented=true}));
            GD.Print($"CONCERT_PLAYER_CROWD_CAPTURE samples={records.Count} grip_error={maxPositionError:F6}m axis_error={maxAxisError:F4}deg arm_step={Mathf.RadToDeg(maxArmStep):F3}deg overhead={overhead}");
        }
        finally {
            avatar.SetConcertCheerEnabled(false);
            // Complete the cheer's current 0.42s fade before restoring the borrowed
            // skeleton; one second also leaves room for future gentle blend tuning.
            for(int i=0;i<60;i++)avatar.Animate(1.0/60,0,true);
            avatar.SetConcertCheerEnabled(savedCheer);avatar.SetConcertCheerClock(savedPosition);
            for(int i=0;i<savedBones.Length;i++)skeleton.SetBonePose(i,savedBones[i]);
            avatar.GlobalTransform=savedAvatar;avatar.Visible=savedAvatarVisible;
            if(!hadProps)_show.RemoveAvatarLightSticks(avatar);
            else for(int i=0;i<props.Length;i++)if(IsInstanceValid(props[i]))props[i].Transform=originalProps[i];
            _show.Preview=savedPreview;_show.PreviewPlaying=savedPlaying;_show.PreviewPosition=savedPosition;_show.SetProcess(savedProcess);
            _captureActive=savedCapture;_broadcastView=savedBroadcast;_panel.Visible=savedPanel;
            _camera.TopLevel=savedCameraTop;_camera.GlobalTransform=savedCamera;_camera.Fov=savedFov;SetPhysicsProcess(savedPhysics);
            GetWindow().Mode=savedWindowMode;GetWindow().Size=savedWindowSize;
        }
    }
}
