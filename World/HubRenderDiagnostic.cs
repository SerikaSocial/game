using Godot;
using System;
using System.IO;
namespace SerikaSocial.World;
// Local artifact review harness. Not part of the client release patch.
public partial class HubRenderDiagnostic : Node3D
{
 public override async void _Ready()
 {
  try {
   var path=System.Environment.GetEnvironmentVariable("SERIKA_WORLD_BUNDLE");
   var output=System.Environment.GetEnvironmentVariable("SERIKA_HUB_SHOTS");
   Directory.CreateDirectory(output);
   WorldLoader.LoadFromPath(path,"hub-render",this);
   var camera=new Camera3D{Current=true,Fov=70};AddChild(camera);
   (string,Vector3,Vector3)[] shots={
    ("atrium",new(0,1.78f,10.7f),new(0,2.18f,-4.8f)),
    ("aquarium",new(-11.6f,1.92f,4.6f),new(-14.5f,1.8f,2.4f)),
    ("portals",new(0,1.7f,-6.4f),new(0,1.6f,-12.1f)),
    ("lounge",new(-5.8f,1.7f,1.1f),new(-12.0f,1.5f,-2.0f)),
    ("mirror",new(11.2f,1.6f,6.8f),new(14.9f,1.6f,6.8f)),
    ("tea-corner",new(-7.3f,1.8f,6.2f),new(-11.0f,1.2f,11.5f))};
   foreach(var (name,pos,target) in shots){
    camera.GlobalPosition=pos;camera.LookAt(target);
    for(int i=0;i<45;i++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
    await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
    var image=GetViewport().GetTexture().GetImage();image.SavePng(Path.Combine(output,"hub-game-"+name+".png"));
    GD.Print("HUB_RENDER captured "+name);
   }
   GetTree().Quit(0);
  }catch(Exception ex){GD.PrintErr(ex);GetTree().Quit(1);}
 }
}
