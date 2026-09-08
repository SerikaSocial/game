using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SerikaSocial.UI;

namespace SerikaSocial;

/// Exercises the actual world-detail controls without changing any server account.
public partial class WorldActionsDiagnostic : Node
{
    private int _failures;
    private string _output;
    private readonly List<object> _checks = new();
    private const string World = "9608b154-97f5-4d78-9096-ecbb99ce72d5";
    private const string Instance = "11111111-1111-4111-8111-111111111111";
    public override async void _Ready()
    {
        _output = System.Environment.GetEnvironmentVariable("SERIKA_UI_SHOTS") ?? "/tmp/serika-world-actions";
        Directory.CreateDirectory(_output);
        GetTree().Root.Theme = Brand.Theme;
        try
        {
            foreach (var (name, size, vr) in new[] {
                ("desktop", new Vector2I(1280,800), false),
                ("small-window", new Vector2I(640,480), false),
                ("vr-panel", new Vector2I(1000,640), true) }) await Exercise(name,size,vr);
        }
        catch(Exception e) { Check("diagnostic exception",false,e.ToString()); }
        File.WriteAllText(Path.Combine(_output,"world-actions-validation.json"), JsonSerializer.Serialize(new { passed=_failures==0, failures=_failures, checks=_checks },new JsonSerializerOptions { WriteIndented=true }));
        GD.Print($"WORLD ACTIONS: {_checks.Count-_failures}/{_checks.Count} checks passed");
        GetTree().Quit(_failures==0 ? 0 : 1);
    }
    private async Task Exercise(string name, Vector2I size, bool vr)
    {
        VrUiSurface.Active=vr;
        var viewport=new SubViewport { Size=size, TransparentBg=true, RenderTargetUpdateMode=SubViewport.UpdateMode.Always };
        AddChild(viewport);
        var hud=new Hud(); viewport.AddChild(hud);
        string preview=System.Environment.GetEnvironmentVariable("SERIKA_UI_WORLD_THUMB");
        if(!string.IsNullOrEmpty(preview)) hud.ImageLoader=_=>Task.FromResult(File.ReadAllBytes(preview));
        string home=null,room=null,joinedWorld=null,privateWorld=null,publicWorld=null;
        bool reset=false,browse=false,refresh=false;
        hud.SetHomePressed+=id=>home=id; hud.NewPrivateInstancePressed+=id=>privateWorld=id;
        hud.JoinWorldFromDetailPressed+=id=>publicWorld=id;
        hud.JoinInstancePressed+=(id,wid)=>{ room=id; joinedWorld=wid; };
        hud.ResetHomePressed+=()=>reset=true;
        hud.BrowseWorldsPressed+=()=>browse=true; hud.RefreshWorldPressed+=id=>refresh=id==World;
        var world=JsonSerializer.SerializeToElement(new { id=World,name="Serika Social Hub",author="pikachubolk",capacity=32,releaseStatus=2,downloadUrl="https://example.invalid/hub.glb",thumbnailUrl=string.IsNullOrEmpty(preview)?null:"fixture-preview",description="A cozy home with warm lamps, an aquarium, quiet corners and places to spend time together.",tags=new[]{"cozy","social","home"},instances=new[]{
            new { id=Instance,access=0,region="Europe",playerCount=3,capacity=32 },
            new { id="private-never-list",access=4,region="Private",playerCount=1,capacity=16 },
            new { id="full-instance",access=0,region="Europe",playerCount=16,capacity=16 }
        }});
        hud.ShowWorldDetail(world); await Settle();
        var panel=(Control)hud.FindChild("WorldDetail",true,false);
        Button Named(string n)=>(Button)hud.FindChild(n,true,false);
        Button Text(string t)=>Descendants(panel).OfType<Button>().Single(b=>b.Text==t);
        Check(name+" thumbnail decoded",string.IsNullOrEmpty(preview)||Descendants(panel).OfType<TextureRect>().Any(t=>t.Texture is ImageTexture));
        Check(name+" detail fits viewport",Contains(size,panel),panel.GetGlobalRect().ToString());
        Check(name+" footer always visible",new[]{"SetHome","ResetHome","NewPrivate","JoinPublic"}.All(n=>Contains(size,Named(n))));
        Check(name+" private rooms excluded",Descendants(panel).OfType<Button>().Count(b=>b.Text=="Join")==1 && !Descendants(panel).OfType<Label>().Any(l=>l.Text.Contains("Private  ·")));
        Check(name+" full rooms disabled",Text("Full").Disabled);
        await Click(viewport,Named("SetHome")); Check(name+" Home emits selected world",home==World);
        await Click(viewport,Named("NewPrivate")); Check(name+" private creation emits selected world",privateWorld==World);
        await Click(viewport,Named("JoinPublic")); Check(name+" public join emits selected world",publicWorld==World);
        Text("Join").GrabFocus(); await Settle(); await Click(viewport,Text("Join"));
        Check(name+" instance join preserves exact IDs",room==Instance && joinedWorld==World);
        hud.SetHomeSelection(World); await Settle();
        Check(name+" current Home marked and reset enabled",Named("SetHome").Text=="Your Home" && Named("SetHome").Disabled && !Named("ResetHome").Disabled);
        await Click(viewport,Named("ResetHome")); Check(name+" reset emits",reset);
        hud.SetHomeSelection(World,true); Check(name+" pending Home changes disabled",Named("SetHome").Disabled && Named("ResetHome").Disabled);
        hud.SetHomeSelection(null); Check(name+" platform default cannot be redundantly reset",Named("ResetHome").Disabled);
        await Click(viewport,Text("Refresh")); await Click(viewport,Text("← Worlds"));
        Check(name+" browse and refresh controls work",browse&&refresh);
        var scroll=Descendants(panel).OfType<ScrollContainer>().Single(); scroll.ScrollVertical=0; await Settle(); Capture(viewport,name+"-world-detail");
        var stress=JsonSerializer.SerializeToElement(new { id=World,name=new string('W',200),author=new string('A',400),capacity=32,releaseStatus=2,downloadUrl="https://example.invalid/hub.glb",description=string.Join(" ",Enumerable.Repeat("Long community description with useful information.",100)),tags=Enumerable.Repeat(new string('T',100),20).ToArray(),instances=Array.Empty<object>() });
        hud.ShowWorldDetail(stress); hud.SetWorldActionStatus("Couldn't reach Serika. Check your connection and try again.",true); await Settle();
        Check(name+" long content preserves fixed travel controls",Contains(size,panel)&&new[]{"SetHome","ResetHome","NewPrivate","JoinPublic"}.All(n=>Contains(size,Named(n))));
        Capture(viewport,name+"-long-content");
        viewport.Size=new Vector2I(720,520); await Settle();
        Check(name+" resize preserves panel bounds",Contains(viewport.Size,panel));
        hud.QueueFree(); await Settle(); viewport.QueueFree(); await Settle();
    }
    private async Task Click(SubViewport viewport, Control control)
    {
        var visible=control.GetGlobalRect();
        for(Node ancestor=control.GetParent();ancestor!=null;ancestor=ancestor.GetParent())
            if(ancestor is Control { ClipContents:true } clip) visible=visible.Intersection(clip.GetGlobalRect());
        var p=visible.GetCenter();
        viewport.PushInput(new InputEventMouseMotion { Position=p,GlobalPosition=p });
        viewport.PushInput(new InputEventMouseButton { Position=p,GlobalPosition=p,ButtonIndex=MouseButton.Left,Pressed=true });
        viewport.PushInput(new InputEventMouseButton { Position=p,GlobalPosition=p,ButtonIndex=MouseButton.Left,Pressed=false });
        await Settle();
    }
    private async Task Settle() { for(int i=0;i<5;i++) await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame); }
    private void Capture(SubViewport viewport,string name) { if(DisplayServer.GetName()!="headless") viewport.GetTexture().GetImage().SavePng(Path.Combine(_output,name+".png")); }
    private static bool Contains(Vector2I size,Control c) { var r=c.GetGlobalRect();return r.Position.X>=0&&r.Position.Y>=0&&r.End.X<=size.X+1&&r.End.Y<=size.Y+1; }
    private static IEnumerable<Node> Descendants(Node n) { foreach(Node c in n.GetChildren()) { yield return c;foreach(var d in Descendants(c)) yield return d; } }
    private void Check(string name,bool passed,string detail=null) { _checks.Add(new{name,passed,detail});if(!passed)_failures++;GD.Print($"WORLD ACTIONS {(passed?"PASS":"FAIL")}: {name} {detail}"); }
}
