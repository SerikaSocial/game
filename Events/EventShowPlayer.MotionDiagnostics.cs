using Godot;
namespace SerikaSocial.Events;
public partial class EventShowPlayer
{
    public bool UsesBakedMotion => _animation?.UsesBakedConcertPose ?? false;
    /// Main-thread deterministic probe; includes the actual synchronized performer path.
    public void SampleMotionForDiagnostics(double seconds)
    {
        ShowTimeline.PerformerAt(_state.Config, seconds, out var position, out float yaw);
        _artist.GlobalPosition=position;_artist.RotationDegrees=new Vector3(0,yaw,0);
        _animation.SampleShowClip(_state.Config.Clip,seconds);
    }
}
