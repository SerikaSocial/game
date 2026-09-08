using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
namespace SerikaSocial.Events;

public partial class EventShowPlayer
{
    private readonly List<AudioStreamPlayer3D> _stageSpeakers = new();
    private AudioStreamPlayer3D _introSpeaker;
    private float _introOriginalVolume;
    private bool _introVolumeWasMuted;
    public bool StageAudioActive => _stageSpeakers.Count == 4;
    public int StageSpeakerCount => _stageSpeakers.Count;
    public bool StageAudioPlaying => StageAudioActive ? _stageSpeakers.All(s=>s.Playing) : _audio?.Playing == true;
    public double StageAudioSpread => StageAudioActive ? _stageSpeakers.Max(s=>s.GetPlaybackPosition())-_stageSpeakers.Min(s=>s.GetPlaybackPosition()) : 0;
    public bool IntroStageAudioPlaying => _introSpeaker?.Playing == true;
    public IReadOnlyList<AudioStreamPlayer3D> StageSpeakersForDiagnostics => _stageSpeakers;

    private void SetupStageAudio(Node3D world,string[] files)
    {
        if(!_state.Config.StageAudio)return;
        if(files.Length<6 || string.IsNullOrEmpty(files[4]) || string.IsNullOrEmpty(files[5]))
            throw new InvalidOperationException("Stage audio needs stage-left.ogg and stage-right.ogg in the portable show folder.");
        var left=LoadAudio(files[4]);var right=LoadAudio(files[5]);
        if(left==null || right==null || Math.Abs(left.GetLength()-_audio.Stream.GetLength())>.08 || Math.Abs(right.GetLength()-left.GetLength())>.02)
            throw new InvalidOperationException("Stage speaker channels must match the complete soundtrack duration.");
        string[] markers={"MAIN_L","MAIN_R","DELAY_L","DELAY_R"};
        for(int i=0;i<markers.Length;i++) {
            var marker=world.FindChild("SERIKA_EVENT_AUDIO_"+markers[i],true,false) as Node3D;
            if(marker==null)throw new InvalidOperationException("Venue is missing the stage speaker marker "+markers[i]+".");
            bool fill=i>=2;
            var speaker=new AudioStreamPlayer3D { Name="ConcertPA_"+markers[i],Stream=i%2==0?left:right,Bus="Music",
                VolumeDb=Math.Clamp(_state.Config.StageAudioGainDb,-30,0)+(fill?-5:0),
                MaxDb=Math.Clamp(_state.Config.StageAudioGainDb,-30,0)+(fill?-5:0), UnitSize=fill?30:65, MaxDistance=fill?95:260,
                AttenuationModel=AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance,
                PanningStrength=.72f, EmissionAngleEnabled=true,EmissionAngleDegrees=85,
                EmissionAngleFilterAttenuationDb=-9, AttenuationFilterCutoffHz=20500,
                DopplerTracking=AudioStreamPlayer3D.DopplerTrackingEnum.Disabled };
            AddChild(speaker);speaker.GlobalTransform=marker.GlobalTransform;
            _stageSpeakers.Add(speaker);
        }
        if(files.Length>6 && !string.IsNullOrEmpty(files[6]) && _introPlayer!=null) {
            var stream=LoadAudio(files[6]);
            if(stream==null)throw new InvalidOperationException("Intro stage audio could not be decoded.");
            _introSpeaker=new AudioStreamPlayer3D { Name="ConcertPA_Intro",Stream=stream,Bus="Music",
                VolumeDb=Math.Clamp(_state.Config.StageAudioGainDb,-30,0),UnitSize=70,MaxDistance=260,
                PanningStrength=.6f,AttenuationFilterCutoffHz=20500,MaxDb=Math.Clamp(_state.Config.StageAudioGainDb,-30,0) };
            AddChild(_introSpeaker);_introSpeaker.GlobalPosition=(_stageSpeakers[0].GlobalPosition+_stageSpeakers[1].GlobalPosition)*.5f;
            _introOriginalVolume=_introPlayer.VolumeDb;_introPlayer.VolumeDb=-80;_introVolumeWasMuted=true;
        }
    }
    private void StopShowAudio()
    {
        _audio?.Stop();foreach(var speaker in _stageSpeakers)if(IsInstanceValid(speaker))speaker.Stop();
    }
    private void UpdateShowAudio(double seconds,bool playing)
    {
        if(!playing || seconds>=Math.Min(_state.Config.Duration,_audio.Stream.GetLength())) { StopShowAudio();return; }
        double audible=AudioPosition+AudioServer.GetTimeSinceLastMix()-AudioServer.GetOutputLatency();
        if(!StageAudioPlaying || _playingRevision!=_state.Revision || Math.Abs(audible-seconds)>.20 || StageAudioSpread>.025) {
            // All coverage feeds use one timeline and start inside one audio-server lock.
            // No invented delay or reverb is added to the supplied outdoor concert mix.
            AudioServer.Lock();
            try {
                if(StageAudioActive) { _audio.Stop();foreach(var speaker in _stageSpeakers)speaker.Play((float)seconds); }
                else _audio.Play((float)seconds);
            } finally { AudioServer.Unlock(); }
            _playingRevision=_state.Revision;
        }
    }
    private void UpdateIntroStageAudio(bool performance)
    {
        if(_introSpeaker==null)return;
        if(performance || _introPlayer?.IsPlaying()!=true) { _introSpeaker.Stop();return; }
        double target=_introPlayer.StreamPosition;
        double audible=_introSpeaker.GetPlaybackPosition()+AudioServer.GetTimeSinceLastMix()-AudioServer.GetOutputLatency();
        if(!_introSpeaker.Playing || Math.Abs(audible-target)>.20)_introSpeaker.Play((float)target);
    }
    private void RestoreStageAudio()
    {
        foreach(var speaker in _stageSpeakers)if(IsInstanceValid(speaker)) { speaker.Stop();speaker.QueueFree(); }
        _stageSpeakers.Clear();
        if(IsInstanceValid(_introSpeaker)) { _introSpeaker.Stop();_introSpeaker.QueueFree(); }
        _introSpeaker=null;
        if(_introVolumeWasMuted && IsInstanceValid(_introPlayer))_introPlayer.VolumeDb=_introOriginalVolume;
        _introVolumeWasMuted=false;
    }
}
