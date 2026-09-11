using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;
namespace SerikaSocial.Events;

public sealed class CameraKey
{
    public bool Cut { get; set; }
    public double Time { get; set; }
    public float[] Position { get; set; } = new float[] { 0, 5.5f, -40 };
    public float[] Target { get; set; } = new float[] { 0, 5.2f, -44 };
    public float Fov { get; set; } = 40;
}
public sealed class PerformerKey
{
    public double Time { get; set; }
    public float[] Position { get; set; }
    public float Yaw { get; set; }
}
public sealed class ShowSegment
{
    public string Title { get; set; }
    public double Start { get; set; }
    public double Duration { get; set; }
}
public sealed class ShowLightKey
{
    public double Time { get; set; }
    public float[] Color { get; set; } = new float[] { .65f, .8f, 1 };
    public float Energy { get; set; } = 1;
    public string Look { get; set; } = "intimate";
    public float Fade { get; set; } = 3;
    public float Accent { get; set; }
}
public sealed class ShowEffectKey
{
    public double Time { get; set; }
    public float Duration { get; set; } = 1;
    public string Kind { get; set; }
    public string Pattern { get; set; } = "lines";
    public float Strength { get; set; } = 1;
    public int Group { get; set; }
    public int Seed { get; set; }
}
public sealed class ShowConfig
{
    public double RevealTime { get; set; }
    public bool StageAudio { get; set; }
    public string StageAudioLeftKey { get; set; }
    public string StageAudioRightKey { get; set; }
    public string StageAudioLeftUrl { get; set; }
    public string StageAudioRightUrl { get; set; }
    public string IntroAudioKey { get; set; }
    public string IntroAudioUrl { get; set; }
    public float StageAudioGainDb { get; set; } = -6;
    public bool LightSticks { get; set; }
    public List<ShowEffectKey> Effects { get; set; } = new();
    public string IntroKey { get; set; }
    public string IntroUrl { get; set; }
    public double IntroDuration { get; set; }
    public List<PerformerKey> PerformerPath { get; set; } = new();
    public List<ShowSegment> Segments { get; set; } = new();
    public List<ShowLightKey> Lights { get; set; } = new();
    public float MusicFps { get; set; } = 25;
    public float[] Beats { get; set; } = System.Array.Empty<float>();
    public float[] MusicEnergy { get; set; } = System.Array.Empty<float>();
    public float MouthFps { get; set; } = 5;
    public float MouthGain { get; set; } = 1.3f;
    public float[] MouthRound { get; set; } = System.Array.Empty<float>();
    public float[] Mouth { get; set; } = System.Array.Empty<float>();
    public string ArtistKey { get; set; }
    public string AnimationKey { get; set; }
    public string AudioKey { get; set; }
    public string ArtistUrl { get; set; }
    public string AnimationUrl { get; set; }
    public string AudioUrl { get; set; }
    public string Clip { get; set; }
    public double Duration { get; set; }
    public float[] Performer { get; set; } = new float[] { 0, 4.2f, -44 };
    public float Yaw { get; set; } = 180;
    public float Scale { get; set; } = 1;
    public List<CameraKey> Cameras { get; set; } = new() { new CameraKey() };
    public string VideoUrl { get; set; }
    public string PreshowVideoUrl { get; set; }
    public double PreshowStartSeconds { get; set; }
    public double PreshowDuration { get; set; }
    public long ScheduledStart { get; set; }
    public bool IsVideoEvent => !string.IsNullOrEmpty(VideoUrl);
}
public sealed class LiveEvent
{
    public string Id { get; set; }
    public string WorldId { get; set; }
    public string Title { get; set; }
    public string BannerKey { get; set; }
    public string BannerUrl { get; set; }
    public string Status { get; set; }
    public int Revision { get; set; }
    public long? StartedAt { get; set; }
    public long ServerTime { get; set; }
    public ShowConfig Config { get; set; }
    public bool IsOpen => Status is "open" or "live";
    // AotJsonCamelContext reproduces this contract with compile-time metadata: iOS is an AOT
    // build and has no reflection-based serializer to fall back on.
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = AotJsonCamelContext.Default,
    };
}
public static class ShowTimeline
{
    public static double Position(long serverNow, long? startedAt, double duration) =>
        startedAt.HasValue ? Math.Clamp((serverNow - startedAt.Value) / 1000.0, 0, duration) : 0;
    public static Vector3 Vector(float[] v) => new(v[0], v[1], v[2]);
    public static float[] Array(Vector3 v) => new[] { v.X, v.Y, v.Z };
    public static float Envelope(float[] samples, double seconds, float fps)
    {
        if (samples == null || samples.Length == 0 || seconds < 0 || !float.IsFinite(fps) || fps <= 0) return 0;
        double frame=seconds*fps; int index=(int)Math.Min(frame,samples.Length-1);
        if (frame>=samples.Length) return 0;
        return Mathf.Lerp(samples[index],samples[Math.Min(index+1,samples.Length-1)],(float)(frame-index));
    }
    public static float Ease(float t) => t*t*t*(t*(t*6-15)+10);
    public static void PerformerAt(ShowConfig config, double seconds, out Vector3 position, out float yaw)
    {
        var keys = config.PerformerPath;
        if (keys == null || keys.Count == 0) { position = Vector(config.Performer); yaw = config.Yaw; return; }
        int i = 0; while (i+1 < keys.Count && keys[i+1].Time <= seconds) i++;
        var a = keys[i]; var b = keys[Math.Min(i+1, keys.Count-1)];
        float t = b.Time > a.Time ? Ease((float)Math.Clamp((seconds-a.Time)/(b.Time-a.Time),0,1)) : 0;
        position = Vector(a.Position).Lerp(Vector(b.Position),t);
        yaw = Mathf.RadToDeg(Mathf.LerpAngle(Mathf.DegToRad(a.Yaw),Mathf.DegToRad(b.Yaw),t));
    }
    public static void CameraAt(IReadOnlyList<CameraKey> keys, double seconds, out Vector3 position, out Vector3 target, out float fov)
    {
        int i = 0;
        while (i + 1 < keys.Count && keys[i + 1].Time <= seconds) i++;
        var a = keys[i]; var b = keys[Math.Min(i + 1, keys.Count - 1)];
        float t = b.Time > a.Time ? (float)Math.Clamp((seconds - a.Time) / (b.Time - a.Time), 0, 1) : 0;
        t = b.Cut ? 0 : Ease(t);
        position = Vector(a.Position).Lerp(Vector(b.Position), t);
        target = Vector(a.Target).Lerp(Vector(b.Target), t);
        fov = Mathf.Lerp(a.Fov, b.Fov, t);
    }
}
