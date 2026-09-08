using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using Godot;
using Serika.Net;
using SerikaSocial.Events;
namespace SerikaSocial.UI;

/// Staff setup wizard: files, venue, camera points, then explicit event/show controls.
public partial class EventAdminPanel : CanvasLayer
{
    public event Action Closed;
    public event Action<LiveEvent> VisitVenue;
    public event Action<LiveEvent,double,bool> PreviewRequested;
    public Func<Camera3D> CurrentCamera;
    public Func<Vector3?> PerformerMarker;
    public ApiClient Api { get; set; }
    public bool IsOpen => Visible;
    private OptionButton _shows, _venues, _clips;
    private LineEdit _title;
    private Label _status, _assets;
    private ItemList _points;
    private SpinBox _introDuration, _time, _duration, _yaw, _scale, _cx, _cy, _cz, _tx, _ty, _tz, _fov;
    private HSlider _preview;
    private Button _open, _play, _stop, _close;
    private readonly List<Button> _buttons = new();
    private LiveEvent[] _events = Array.Empty<LiveEvent>();
    private List<string> _venueIds = new();
    private LiveEvent _selected;
    private ShowConfig _draft = new();
    private string _bannerKey;
    private bool _busy, _loaded;
    private VBoxContainer _body;
    public override void _Ready()
    {
        Layer = 116; Visible = false; AddChild(Brand.Scrim(.75f));
        var center = new CenterContainer(); center.SetAnchorsPreset(Control.LayoutPreset.FullRect); AddChild(center);
        var card = new PanelContainer { Theme = Brand.Theme, CustomMinimumSize = Brand.FitCard(GetViewport(), 920, 720) };
        card.AddThemeStyleboxOverride("panel", Brand.Panel(Brand.Bg1, 16, 1, Brand.Border)); center.AddChild(card);
        var margin = new MarginContainer(); foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_"+side, 20); card.AddChild(margin);
        var outer = new VBoxContainer(); margin.AddChild(outer);
        var header = Row(); outer.AddChild(header); header.AddChild(new Label { Text = "Admin · Events", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        AddButton(header,"Close panel",Hide);
        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; outer.AddChild(scroll);
        _body = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; _body.AddThemeConstantOverride("separation", 10); scroll.AddChild(_body);
        _shows = new OptionButton(); _body.AddChild(_shows); _shows.ItemSelected += i => SelectShow((int)i);
        var commands = Row(); _body.AddChild(commands); AddButton(commands,"Refresh",()=>Run(Refresh)); AddButton(commands,"New show",NewShow); AddButton(commands,"Import show folder…",()=>Pick("*.json ; Show manifest",ImportShow));
        Heading("1 · Venue and event banner");
        _title = new LineEdit { PlaceholderText = "Event title", Text = "Hoshimachi Suisei", MaxLength = 100 }; _body.AddChild(_title);
        var venues = Row(); _body.AddChild(venues); _venues = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; venues.AddChild(_venues);
        AddButton(venues,"Upload venue…",()=>Pick("*.serikaworld ; Serika world", async p => {
            var venue = await Api.UploadEventFileAsync("venue", p, System.IO.Path.GetFileNameWithoutExtension(p));
            await RefreshVenues(); _venues.Select(_venueIds.IndexOf(venue.GetProperty("id").GetString()));
        }));
        AddButton(_body,"Choose PNG banner…",()=>Pick("*.png ; PNG banner",async p => { var a = await Api.UploadEventFileAsync("banner",p); _bannerKey=a.GetProperty("key").GetString(); }));
        var introRow=Row();_body.AddChild(introRow);
        AddButton(introRow,"Intro loop OGV…",()=>Pick("*.ogv ; Ogg Theora intro",async p=>{var a=await Api.UploadEventFileAsync("intro",p);_draft.IntroKey=a.GetProperty("key").GetString();_draft.IntroUrl=a.GetProperty("url").GetString();}));
        _introDuration=Number(introRow,"Intro duration (s)",.1,14400,232.4667,.01);
        Heading("2 · Performer, animation and music");
        var files = Row(); _body.AddChild(files);
        AddButton(files,"Artist VRM / GLB…",()=>Pick("*.vrm,*.glb ; Humanoid model",async p => { var a=await Api.UploadEventFileAsync("artist",p); _draft.ArtistKey=a.GetProperty("key").GetString(); _draft.ArtistUrl=a.GetProperty("url").GetString(); }));
        AddButton(files,"Animation GLB…",()=>Pick("*.glb ; Animation GLB",async p => {
            var a=await Api.UploadEventFileAsync("animation",p); _draft.AnimationKey=a.GetProperty("key").GetString(); _draft.AnimationUrl=a.GetProperty("url").GetString();
            _clips.Clear(); foreach(var clip in a.GetProperty("clips").EnumerateArray()) _clips.AddItem(clip.GetProperty("name").GetString());
            if(_clips.ItemCount>0) _clips.Select(0);
        }));
        AddButton(files,"Audio track…",()=>Pick("*.ogg,*.wav,*.mp3 ; Audio track",async p => {
            var audio=EventShowPlayer.LoadAudio(p); if(audio==null || audio.GetLength()<=0) throw new InvalidOperationException("The audio track cannot be decoded.");
            _duration.Value=audio.GetLength(); _preview.MaxValue=audio.GetLength();
            var a=await Api.UploadEventFileAsync("audio",p); _draft.AudioKey=a.GetProperty("key").GetString(); _draft.AudioUrl=a.GetProperty("url").GetString();
            _draft.StageAudio=false;_draft.StageAudioLeftKey=_draft.StageAudioLeftUrl=null;_draft.StageAudioRightKey=_draft.StageAudioRightUrl=null;
        }));
        _assets=new Label { AutowrapMode=TextServer.AutowrapMode.WordSmart };_body.AddChild(_assets);
        _clips = new OptionButton(); _body.AddChild(_clips);
        var tuning = Row(); _body.AddChild(tuning); _duration=Number(tuning,"Duration (s)",.1,14400,180,.1); _yaw=Number(tuning,"Artist yaw",-360,360,180,1); _scale=Number(tuning,"Artist scale",.1,10,1,.05);
        AddButton(_body,"Place artist at venue stage marker",()=> { var p=PerformerMarker?.Invoke(); if(!p.HasValue) { Status("Visit the venue first; it needs a SERIKA_EVENT_PERFORMER marker."); return; } _draft.Performer=ShowTimeline.Array(p.Value); Status("Performer placed at stage marker."); });
        Heading("3 · Camera path");
        _body.AddChild(new Label { Text="Move to a camera position in the venue, reopen this panel, choose a time and capture. The camera follows the path and looks at the artist.", AutowrapMode=TextServer.AutowrapMode.WordSmart });
        var capture=Row();_body.AddChild(capture);_time=Number(capture,"Time (s)",0,14400,0,.1);
        AddButton(capture,"Capture camera here",Capture); AddButton(capture,"Use Suisei camera preset",()=> { _draft.Cameras=new(){new CameraKey(),new CameraKey{Time=Math.Min(30,_duration.Value),Position=new[]{-3f,5.7f,-40f},Fov=35},new CameraKey{Time=Math.Min(60,_duration.Value),Position=new[]{3f,5.7f,-40f},Fov=40}}; _draft.Cameras=_draft.Cameras.GroupBy(k=>k.Time).Select(g=>g.First()).OrderBy(k=>k.Time).ToList(); RefreshPoints(); });
        _points=new ItemList { CustomMinimumSize=new Vector2(0,100) };_body.AddChild(_points);
        _points.ItemSelected += i => {var k=_draft.Cameras[(int)i];_time.Value=k.Time;_cx.Value=k.Position[0];_cy.Value=k.Position[1];_cz.Value=k.Position[2];_tx.Value=k.Target[0];_ty.Value=k.Target[1];_tz.Value=k.Target[2];_fov.Value=k.Fov;};
        var cameraPosition=Row();_body.AddChild(cameraPosition);_cx=Number(cameraPosition,"Camera X",-1000,1000,0,.1);_cy=Number(cameraPosition,"Y",-1000,1000,5.5,.1);_cz=Number(cameraPosition,"Z",-1000,1000,-40,.1);_fov=Number(cameraPosition,"FOV",10,120,40,1);
        var cameraTarget=Row();_body.AddChild(cameraTarget);_tx=Number(cameraTarget,"Look at X",-1000,1000,0,.1);_ty=Number(cameraTarget,"Y",-1000,1000,5.2,.1);_tz=Number(cameraTarget,"Z",-1000,1000,-44,.1);
        AddButton(_body,"Apply edited camera point",()=>{var chosen=_points.GetSelectedItems();if(chosen.Length>0)_draft.Cameras.RemoveAt(chosen[0]);_draft.Cameras.RemoveAll(k=>Math.Abs(k.Time-_time.Value)<.001);_draft.Cameras.Add(new CameraKey{Time=_time.Value,Position=new[]{(float)_cx.Value,(float)_cy.Value,(float)_cz.Value},Target=new[]{(float)_tx.Value,(float)_ty.Value,(float)_tz.Value},Fov=(float)_fov.Value});_draft.Cameras.Sort((a,b)=>a.Time.CompareTo(b.Time));RefreshPoints();});
        AddButton(_body,"Remove selected camera point",()=> { var chosen=_points.GetSelectedItems(); if(chosen.Length>0){_draft.Cameras.RemoveAt(chosen[0]);RefreshPoints();} });
        _preview=new HSlider { MinValue=0,MaxValue=180,Step=.1 };_body.AddChild(_preview);
        var previewRow=Row();_body.AddChild(previewRow);
        AddButton(previewRow,"Visit venue",()=> { if(_selected!=null){var e=_selected;Hide();VisitVenue?.Invoke(e);}else Status("Save the show first."); });
        AddButton(previewRow,"Preview at scrubber + show path",()=> { if(_selected!=null){PullDraft();_selected.Config=_draft;PreviewRequested?.Invoke(_selected,_preview.Value,true);}else Status("Save the show first."); });
        AddButton(previewRow,"Stop preview",()=>PreviewRequested?.Invoke(null,0,false));
        AddButton(_body,"Save show",()=>Run(Save));
        Heading("4 · Event controls");
        var control=Row();_body.AddChild(control);
        _open=AddButton(control,"Open event",()=>Run(()=>SendControl("open")));
        _play=AddButton(control,"Start show · 10s",()=>Run(()=>SendControl("play")));
        _stop=AddButton(control,"Stop show",()=>Run(()=>SendControl("stop")));
        _close=AddButton(control,"Close event",()=>Run(()=>SendControl("close")));
        _body.AddChild(new Label {Text="Open event shows the join banner. Start show synchronizes music, animation and cameras for everyone. Stop show keeps doors open; Close event returns guests Home.",AutowrapMode=TextServer.AutowrapMode.WordSmart});
        _status=new Label {AutowrapMode=TextServer.AutowrapMode.WordSmart};outer.AddChild(_status);
        RefreshPoints();UpdateControls();
    }
    private void Heading(string text){var l=new Label{Text=text};l.AddThemeColorOverride("font_color",Brand.AccentSoft);_body.AddChild(l);}
    private static HBoxContainer Row()=>new(){SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};
    private Button AddButton(Node parent,string text,Action action){var b=Brand.Ghost_(new Button{Text=text,CustomMinimumSize=new Vector2(0,42)});b.Pressed+=action;parent.AddChild(b);_buttons.Add(b);return b;}
    private static SpinBox Number(Node parent,string title,double min,double max,double value,double step){var col=new VBoxContainer();parent.AddChild(col);col.AddChild(new Label{Text=title});var n=new SpinBox{MinValue=min,MaxValue=max,Step=step,Value=value,CustomMinimumSize=new Vector2(120,0)};col.AddChild(n);return n;}
    public void Status(string message){if(_status!=null)_status.Text=message;}
    private async void Run(Func<Task> action){if(_busy)return;_busy=true;UpdateControls();Status("Working…");try{await action();if(IsInstanceValid(this))Status("Ready.");}catch(Exception e){if(IsInstanceValid(this))Status(e.Message);}finally{if(IsInstanceValid(this)){_busy=false;UpdateControls();}}}
    private void Pick(string filter,Func<string,Task> selected){if(_busy)return;var dialog=new FileDialog{FileMode=FileDialog.FileModeEnum.OpenFile,Access=FileDialog.AccessEnum.Filesystem,Filters=new[]{filter},UseNativeDialog=true};AddChild(dialog);dialog.FileSelected+=p=>{dialog.QueueFree();Run(async()=>{await selected(p);Status("Uploaded "+System.IO.Path.GetFileName(p));});};dialog.Canceled+=()=>dialog.QueueFree();dialog.PopupCenteredRatio(.8f);}
    private void PullDraft(){if(_draft.IntroKey!=null)_draft.IntroDuration=_introDuration.Value;_draft.Clip=_clips.Selected>=0?_clips.GetItemText(_clips.Selected):"";_draft.Duration=_duration.Value;_draft.Yaw=(float)_yaw.Value;_draft.Scale=(float)_scale.Value;}
    private async Task Save(){if(_selected?.IsOpen==true)throw new InvalidOperationException("Close the event before editing its show.");PullDraft();if(_venues.Selected<0||_venueIds.Count==0)throw new InvalidOperationException("Upload or select an event venue.");
        var body=new{worldId=_venueIds[_venues.Selected],title=_title.Text,bannerKey=_bannerKey,config=_draft};
        var json=await Api.EventRequestAsync(_selected!=null&&_selected.Status is "draft" or "ended"?"/v1/admin/events/"+_selected.Id+"/config":"/v1/admin/events/",body);
        var id=json.GetProperty("id").GetString();await Refresh();for(int i=0;i<_events.Length;i++)if(_events[i].Id==id){_shows.Select(i+1);SelectShow(i+1);break;}}
    private async Task SendControl(string action){if(_selected==null)return;await Api.EventRequestAsync("/v1/admin/events/"+_selected.Id+"/control",new{action,revision=_selected.Revision});await Refresh();}
    private async Task RefreshVenues(){var data=await Api.EventRequestAsync("/v1/admin/events/venues");_venues.Clear();_venueIds.Clear();foreach(var v in data.EnumerateArray()){_venueIds.Add(v.GetProperty("id").GetString());_venues.AddItem(v.GetProperty("name").GetString());}}
    private async Task Refresh(){string id=_selected?.Id;_events=await Api.GetEventsAsync(true);_shows.Clear();_shows.AddItem("New show");foreach(var e in _events)_shows.AddItem(e.Title+" · "+e.Status);await RefreshVenues();int index=System.Array.FindIndex(_events,e=>e.Id==id);if(index>=0){_shows.Select(index+1);SelectShow(index+1);}}
    private void SelectShow(int index){if(index==0){NewShow();return;}_selected=_events[index-1];_draft=JsonSerializer.Deserialize<ShowConfig>(JsonSerializer.Serialize(_selected.Config,LiveEvent.Json),LiveEvent.Json);_bannerKey=_selected.BannerKey;_introDuration.Value=_draft.IntroDuration>0?_draft.IntroDuration:232.4667;_title.Text=_selected.Title;_duration.Value=_draft.Duration;_yaw.Value=_draft.Yaw;_scale.Value=_draft.Scale;_clips.Clear();_clips.AddItem(_draft.Clip);_clips.Select(0);_preview.MaxValue=_draft.Duration;int venue=_venueIds.IndexOf(_selected.WorldId);if(venue>=0)_venues.Select(venue);RefreshPoints();UpdateControls();}
    private void NewShow(){_selected=null;_draft=new();_bannerKey=null;_title.Text="Hoshimachi Suisei";_clips.Clear();_duration.Value=180;_yaw.Value=180;_scale.Value=1;_shows.Select(0);RefreshPoints();UpdateControls();}
    private void Capture(){var camera=CurrentCamera?.Invoke();if(camera==null){Status("Visit the venue first.");return;}var key=new CameraKey{Time=_time.Value,Position=ShowTimeline.Array(camera.GlobalPosition),Target=ShowTimeline.Array(ShowTimeline.Vector(_draft.Performer)+Vector3.Up*1.4f),Fov=camera.Fov};_draft.Cameras.RemoveAll(k=>Math.Abs(k.Time-key.Time)<.001);_draft.Cameras.Add(key);_draft.Cameras.Sort((a,b)=>a.Time.CompareTo(b.Time));RefreshPoints();}
    private void RefreshPoints(){if(_points==null)return;_points.Clear();foreach(var p in _draft.Cameras)_points.AddItem($"{p.Time:0.0}s    ({p.Position[0]:0.0}, {p.Position[1]:0.0}, {p.Position[2]:0.0})    FOV {p.Fov:0}");}
    private void UpdateControls(){if(_assets!=null)_assets.Text=$"Banner: {(_bannerKey!=null?"ready":"needed")}  ·  Artist: {(_draft.ArtistKey!=null?"ready":"needed")}  ·  Animation: {(_draft.AnimationKey!=null?"ready":"needed")}  ·  Audio: {(_draft.AudioKey!=null?"ready":"needed")}";foreach(var b in _buttons)b.Disabled=_busy;if(_open==null)return;_open.Disabled=_busy||_selected?.Status is not ("draft" or "ended");_play.Disabled=_busy||_selected?.Status!="open";_stop.Disabled=_busy||_selected?.Status!="live";_close.Disabled=_busy||!(_selected?.IsOpen??false);}
    public void Open(){Visible=true;if(!_loaded)Run(async()=>{await Refresh();_loaded=true;});}
    public new void Hide(){Visible=false;Closed?.Invoke();}
}
