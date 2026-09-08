using System;
using Godot;

namespace SerikaSocial.Events;

public partial class EventShowPlayer
{
    private const double BroadcastFadeHalfSeconds = .25;
    private CanvasLayer _broadcastFadeLayer;
    private ColorRect _broadcastFadeRect;
    private readonly Vector3[] _preshowScreenCorners = new Vector3[8];
    private Vector3 _preshowScreenCentre, _preshowScreenFront;
    private bool _preshowScreenReady;

    public float BroadcastFadeOpacity { get; private set; }
    public bool PreshowScreenCameraReady => _preshowScreenReady;

    private void SetupBroadcastCamera(Node3D world)
    {
        RestoreBroadcastCamera();
        // A canvas belongs to precisely one viewport. The audience gameplay/VR
        // viewport never receives this overlay or an additional scene render.
        _broadcastFadeLayer = new CanvasLayer { Name = "BroadcastCutFade", Layer = 50, CustomViewport = _feed };
        _feed.AddChild(_broadcastFadeLayer);
        _broadcastFadeRect = new ColorRect { Name = "BlackDip", Color = new Color(0,0,0,0),
            MouseFilter = Control.MouseFilterEnum.Ignore, FocusMode = Control.FocusModeEnum.None };
        _broadcastFadeLayer.AddChild(_broadcastFadeRect);
        _broadcastFadeRect.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _broadcastFadeRect.Visible = false;

        // Fit the existing largest authored backdrop; never point into the two
        // portrait feed screens (which are excluded from the broadcast cull mask).
        MeshInstance3D screen = null;
        float largestArea = 0;
        foreach (Node node in world.FindChildren("SERIKA_EVENT_BACKDROP*", "MeshInstance3D", true, false)) {
            if (node is not MeshInstance3D mesh || mesh.Mesh == null) continue;
            var size = mesh.GetAabb().Size * mesh.GlobalBasis.Scale.Abs();
            float area = Math.Max(size.X*size.Y, Math.Max(size.X*size.Z,size.Y*size.Z));
            if (area > largestArea) { screen = mesh; largestArea = area; }
        }
        if (screen != null) {
            var bounds = screen.GetAabb();
            _preshowScreenCentre = screen.GlobalTransform * bounds.GetCenter();
            var size = bounds.Size;
            var normal = size.X <= size.Y && size.X <= size.Z ? screen.GlobalBasis.X
                : size.Y <= size.Z ? screen.GlobalBasis.Y : screen.GlobalBasis.Z;
            normal = normal.Normalized();
            var towardStage = ShowTimeline.Vector(_state.Config.Performer) - _preshowScreenCentre;
            if (normal.Dot(towardStage) < 0) normal = -normal;
            _preshowScreenFront = normal;
            for (int i=0;i<8;i++) _preshowScreenCorners[i] = screen.GlobalTransform * bounds.GetEndpoint(i);
            _preshowScreenReady = normal.IsFinite() && normal.LengthSquared() > .9f
                && Math.Abs(normal.Dot(Vector3.Up)) < .98f && largestArea > .01f;
        }
        UpdateBroadcastCamera(0,false);
    }

    /// Fits the authored big screen for a vertical-FOV camera at the requested
    /// viewport aspect. Rehearsal can use its landscape aspect without changing
    /// the portrait live-feed camera or the player's independent audio listener.
    public bool TryGetPreshowCameraPose(float aspect, out Vector3 position, out Vector3 target, out float fov)
    {
        position = target = Vector3.Zero; fov = 50;
        if (!_preshowScreenReady || !float.IsFinite(aspect) || aspect <= 0) return false;
        var right = Vector3.Up.Cross(_preshowScreenFront).Normalized();
        var up = _preshowScreenFront.Cross(right).Normalized();
        float halfWidth=0, halfHeight=0, frontDepth=0;
        foreach (var corner in _preshowScreenCorners) {
            var offset = corner - _preshowScreenCentre;
            halfWidth = Math.Max(halfWidth,Math.Abs(offset.Dot(right)));
            halfHeight = Math.Max(halfHeight,Math.Abs(offset.Dot(up)));
            frontDepth = Math.Max(frontDepth,offset.Dot(_preshowScreenFront));
        }
        float tangent = Mathf.Tan(Mathf.DegToRad(fov*.5f));
        float distance = Math.Max(halfHeight,halfWidth/aspect) * 1.08f / tangent + frontDepth;
        target = _preshowScreenCentre;
        position = target + _preshowScreenFront * Math.Max(distance,1);
        return position.IsFinite() && target.IsFinite();
    }

    /// Absolute show-time envelope: .25 seconds out and .25 seconds in. Camera
    /// sampling still makes its authored cut exactly at full black. No tween or
    /// accumulated transition state can drift after pause or backward seeking.
    public static float EvaluateBroadcastFade(ShowConfig config, double seconds, bool performance = true)
    {
        if (!performance || config?.Cameras == null || !double.IsFinite(seconds)) return 0;
        float opacity = 0;
        for (int i=1;i<config.Cameras.Count;i++) {
            var key = config.Cameras[i];
            if (!key.Cut || !double.IsFinite(key.Time)
                || (config.RevealTime > 0 && Math.Abs(key.Time-config.RevealTime) <= .05)) continue;
            double distance = Math.Abs(seconds-key.Time);
            if (distance >= BroadcastFadeHalfSeconds) continue;
            opacity = Math.Max(opacity,ShowTimeline.Ease((float)(1-distance/BroadcastFadeHalfSeconds)));
        }
        return opacity;
    }

    private void UpdateBroadcastCamera(double seconds, bool performance)
    {
        if (!IsInstanceValid(_camera) || _state?.Config == null) return;
        Vector3 position, target; float fov;
        float aspect = _feed != null && _feed.Size.Y > 0 ? (float)_feed.Size.X/_feed.Size.Y : 2f/3;
        if (performance || !TryGetPreshowCameraPose(aspect,out position,out target,out fov))
            ShowTimeline.CameraAt(_state.Config.Cameras,performance?seconds:0,out position,out target,out fov);
        _camera.GlobalPosition = position; _camera.Fov = fov;
        if (position.DistanceSquaredTo(target) > .0001f) _camera.LookAt(target,Vector3.Up);
        BroadcastFadeOpacity = EvaluateBroadcastFade(_state.Config,seconds,performance);
        if (IsInstanceValid(_broadcastFadeRect)) {
            _broadcastFadeRect.Color = new Color(0,0,0,BroadcastFadeOpacity);
            _broadcastFadeRect.Visible = BroadcastFadeOpacity > 0;
        }
    }

    private void RestoreBroadcastCamera()
    {
        BroadcastFadeOpacity = 0;
        _preshowScreenReady = false;
        if (IsInstanceValid(_broadcastFadeLayer)) {
            _broadcastFadeLayer.Visible = false;
            if (!_broadcastFadeLayer.IsQueuedForDeletion()) _broadcastFadeLayer.QueueFree();
        }
        _broadcastFadeLayer = null; _broadcastFadeRect = null;
    }
}
