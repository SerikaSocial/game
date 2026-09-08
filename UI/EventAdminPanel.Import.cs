using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SerikaSocial.Events;
namespace SerikaSocial.UI;

public partial class EventAdminPanel
{
    private async Task ImportShow(string manifest)
    {
        var root=Path.GetDirectoryName(Path.GetFullPath(manifest));
        using var json=JsonDocument.Parse(await File.ReadAllTextAsync(manifest));
        var config=JsonSerializer.Deserialize<ShowConfig>(json.RootElement.GetProperty("config").GetRawText(),LiveEvent.Json);
        PreflightShowImport(root,config);
        // Folder imports always bind their own uploaded files, not stale URLs from
        // the machine/event from which a portable manifest was exported.
        config.StageAudioLeftKey=config.StageAudioLeftUrl=null;
        config.StageAudioRightKey=config.StageAudioRightUrl=null;
        config.IntroAudioKey=config.IntroAudioUrl=null;
        NewShow();_draft=config;_title.Text=json.RootElement.GetProperty("title").GetString();
        Status("Uploading venue…");var venue=await Api.UploadEventFileAsync("venue",Path.Combine(root,"venue.serikaworld"),_title.Text.Length>80?_title.Text[..80]:_title.Text);
        await RefreshVenues();_venues.Select(_venueIds.IndexOf(venue.GetProperty("id").GetString()));
        Status("Uploading banner…");_bannerKey=(await Api.UploadEventFileAsync("banner",Path.Combine(root,"banner.png"))).GetProperty("key").GetString();
        Status("Uploading artist…");var artist=await Api.UploadEventFileAsync("artist",Path.Combine(root,"artist.vrm"));_draft.ArtistKey=artist.GetProperty("key").GetString();_draft.ArtistUrl=artist.GetProperty("url").GetString();
        Status("Uploading animation…");var animation=await Api.UploadEventFileAsync("animation",Path.Combine(root,"show.glb"));_draft.AnimationKey=animation.GetProperty("key").GetString();_draft.AnimationUrl=animation.GetProperty("url").GetString();
        _clips.Clear();foreach(var clip in animation.GetProperty("clips").EnumerateArray())_clips.AddItem(clip.GetProperty("name").GetString());
        int selected=Enumerable.Range(0,_clips.ItemCount).FirstOrDefault(i=>_clips.GetItemText(i)==_draft.Clip);_clips.Select(selected);
        Status("Uploading soundtrack…");var audio=await Api.UploadEventFileAsync("audio",Path.Combine(root,"show.ogg"));_draft.AudioKey=audio.GetProperty("key").GetString();_draft.AudioUrl=audio.GetProperty("url").GetString();
        if(_draft.StageAudio){
            Status("Uploading left stage audio…");var left=await Api.UploadEventFileAsync("audio",Path.Combine(root,"stage-left.ogg"));_draft.StageAudioLeftKey=left.GetProperty("key").GetString();_draft.StageAudioLeftUrl=left.GetProperty("url").GetString();
            Status("Uploading right stage audio…");var right=await Api.UploadEventFileAsync("audio",Path.Combine(root,"stage-right.ogg"));_draft.StageAudioRightKey=right.GetProperty("key").GetString();_draft.StageAudioRightUrl=right.GetProperty("url").GetString();
        }
        if(File.Exists(Path.Combine(root,"intro-audio.ogg"))){Status("Uploading intro audio…");var introAudio=await Api.UploadEventFileAsync("audio",Path.Combine(root,"intro-audio.ogg"));_draft.IntroAudioKey=introAudio.GetProperty("key").GetString();_draft.IntroAudioUrl=introAudio.GetProperty("url").GetString();}
        if(File.Exists(Path.Combine(root,"intro.ogv"))){Status("Uploading intro loop…");var intro=await Api.UploadEventFileAsync("intro",Path.Combine(root,"intro.ogv"));_draft.IntroKey=intro.GetProperty("key").GetString();_draft.IntroUrl=intro.GetProperty("url").GetString();}
        _duration.Value=_draft.Duration;_yaw.Value=_draft.Yaw;_scale.Value=_draft.Scale;_introDuration.Value=_draft.IntroDuration;_preview.MaxValue=_draft.Duration;RefreshPoints();await Save();
    }

    internal static void PreflightShowImport(string root,ShowConfig config)
    {
        if(config==null)throw new InvalidOperationException("Show settings are missing.");
        var required=new System.Collections.Generic.List<string>{"venue.serikaworld","banner.png","artist.vrm","show.glb","show.ogg"};
        if(config.StageAudio)required.AddRange(new[]{"stage-left.ogg","stage-right.ogg"});
        foreach(string name in required)
            if(!File.Exists(Path.Combine(root,name)))throw new InvalidOperationException("Show folder is missing "+name);
        if(config.StageAudio){
            double duration=ValidateImportedOgg(Path.Combine(root,"show.ogg"),false,null);
            double left=ValidateImportedOgg(Path.Combine(root,"stage-left.ogg"),true,duration);
            double right=ValidateImportedOgg(Path.Combine(root,"stage-right.ogg"),true,duration);
            if(Math.Abs(left-right)>.02)throw new InvalidOperationException("Left and right stage channels must have matching durations.");
        }
        string intro=Path.Combine(root,"intro-audio.ogg");
        if(File.Exists(intro))ValidateImportedOgg(intro,false,null);
    }

    private static double ValidateImportedOgg(string path,bool mono,double? duration)
    {
        string name=Path.GetFileName(path);
        using var audio=EventShowPlayer.LoadAudio(path);
        if(audio==null || audio.GetLength()<=0)throw new InvalidOperationException(name+" cannot be decoded as Ogg Vorbis audio.");
        if(duration.HasValue && Math.Abs(audio.GetLength()-duration.Value)>.08)
            throw new InvalidOperationException(name+" must cover the complete show duration.");
        if(!mono)return audio.GetLength();
        // A standard Vorbis stream's first Ogg page contains its identification
        // packet: 27-byte page header, segment table, type/signature/version/channel.
        using var file=File.OpenRead(path);var header=new byte[512];int read=file.Read(header);
        int packet=read>26?27+header[26]:read;
        if(read<packet+12 || header[0]!=(byte)'O' || header[1]!=(byte)'g' || header[2]!=(byte)'g' || header[3]!=(byte)'S'
            || header[packet]!=1 || System.Text.Encoding.ASCII.GetString(header,packet+1,6)!="vorbis" || header[packet+11]!=1)
            throw new InvalidOperationException(name+" must be a mono Ogg Vorbis stage channel.");
        return audio.GetLength();
    }
}
