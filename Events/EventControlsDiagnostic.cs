using System;
using System.Threading.Tasks;
using Godot;
using SerikaSocial.Player;
namespace SerikaSocial.Events;
public partial class EventControlsDiagnostic : Node3D
{
    private int _failures;
    private void Check(bool ok,string name) { GD.Print($"EVENT_CONTROLS {(ok?"PASS":"FAIL")} {name}");if(!ok)_failures++; }
    public override async void _Ready() {
        try {
            var floor=new StaticBody3D { Position=new Vector3(0,-.1f,0) };AddChild(floor);
            floor.AddChild(new CollisionShape3D { Shape=new BoxShape3D { Size=new Vector3(20,.2f,20) } });
            var player=new LocalPlayer();AddChild(player);
            player.SetFirstPerson(true);player.EventAudienceMode=true;
            Check(player.CurrentCameraMode==LocalPlayer.CameraModeEnum.ThirdPersonBack,"entry forces rear audience camera");
            player.SetFirstPerson(true);player.SetCameraMode(LocalPlayer.CameraModeEnum.ThirdPersonFront);player.CycleCameraMode();
            Check(player.CurrentCameraMode==LocalPlayer.CameraModeEnum.ThirdPersonBack,"all camera selection routes reject first and selfie views");
            for(int i=0;i<30;i++)await ToSignal(GetTree(),SceneTree.SignalName.PhysicsFrame);
            Check(player.IsOnFloor(),"jump test starts grounded");
            player.ExternalJump=true;
            for(int i=0;i<3;i++)await ToSignal(GetTree(),SceneTree.SignalName.PhysicsFrame);
            Check(player.IsOnFloor() && player.Velocity.Y<=0 && !player.ExternalJump,"event touch jump is consumed without jumping");
            player.EventAudienceMode=false;
            Check(player.CurrentCameraMode==LocalPlayer.CameraModeEnum.FirstPerson && player.JumpEnabled,"leaving restores previous camera and jumping");
            player.ExternalJump=true;
            for(int i=0;i<3;i++)await ToSignal(GetTree(),SceneTree.SignalName.PhysicsFrame);
            Check(player.Velocity.Y>0,"normal-world touch jumping still works");
            player.QueueFree();floor.QueueFree();
            await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        }catch(Exception e){Check(false,e.ToString());}
        GetTree().Quit(_failures==0?0:1);
    }
}
