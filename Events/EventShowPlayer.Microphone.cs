using System;
using System.Collections.Generic;
using Godot;
using SerikaSocial.Avatar;

namespace SerikaSocial.Events;

public partial class EventShowPlayer
{
    /// The performer lifts the microphone off its stand and sings holding it.
    ///
    /// The venue authors `SERIKA_EVENT_MIC_STAND` as five parts. The two sitting at the top of
    /// the pole are the microphone and its clip; the other three are the pole and base. Splitting
    /// them by height is what makes a pickup possible at all — the stand stays standing while the
    /// microphone itself moves into her hand.
    ///
    /// Nothing here accumulates: the handoff is a pure function of show time, so seeking backward
    /// past the pickup puts the microphone back on the stand and a late joiner arriving mid-song
    /// finds it already in her hand.
    private Node3D _micStand;
    private readonly List<(MeshInstance3D Mesh, bool Visible)> _micHeadOnStand = new();
    private BoneAttachment3D _micHandAttachment;
    private Node3D _micHandProp;
    private bool? _micHeld;

    /// How long after she finishes walking on and turning she takes the microphone. Long enough
    /// to read as a deliberate beat rather than the prop teleporting the instant she arrives.
    /// The baked reach in `bake_mocap.py` is authored to land on this same moment.
    public const double MicPickupDelay = 1.6;

    public bool MicrophoneHeld => _micHeld == true;
    public bool MicrophoneReady => IsInstanceValid(_micHandProp);
    public float MicrophoneFaceDistance {
        get {
            if (!MicrophoneReady || _micHandProp.GetChildCount()==0) return float.PositiveInfinity;
            var capsule = _micHandProp.GetChild<MeshInstance3D>(_micHandProp.GetChildCount()-1);
            var head = _artist.Skeleton.GetBoneGlobalPose(_artist.BoneOf("head"));
            var mouth = _artist.Skeleton.ToGlobal(head * new Vector3(0,.015f,-.065f));
            return capsule.ToGlobal(capsule.GetAabb().GetCenter()).DistanceTo(mouth);
        }
    }

    /// Show time at which the microphone leaves the stand: the end of the authored entrance
    /// path, plus the pickup beat.
    private double MicPickupTime
    {
        get {
            var path = _state?.Config?.PerformerPath;
            return (path is { Count: > 0 } ? path[^1].Time : 0) + MicPickupDelay;
        }
    }

    private void SetupShowMicrophone(Node3D world)
    {
        RestoreShowMicrophone();
        _micStand = world.FindChild("SERIKA_EVENT_MIC_STAND", true, false) as Node3D;
        if (!IsInstanceValid(_micStand) || !IsInstanceValid(_artist?.Skeleton)) return;

        // Classify by height within the stand, not by name — all five parts are called
        // "Microphone stand part". The head cluster is whatever sits near the top.
        var parts = new List<MeshInstance3D>();
        foreach (Node node in _micStand.FindChildren("*", "MeshInstance3D", true, false))
            parts.Add((MeshInstance3D)node);
        if (parts.Count == 0) return;
        float highest = float.NegativeInfinity;
        foreach (var part in parts) highest = Math.Max(highest, part.Position.Y);
        // A microphone and its clip sit together; the pole below them is far lower.
        foreach (var part in parts)
            if (part.Position.Y >= highest - .08f) _micHeadOnStand.Add((part, part.Visible));
        if (_micHeadOnStand.Count == 0) return;

        // The same palm-derived grip the light sticks use. It is built from the rig's own bind
        // anatomy, so it lands correctly on any humanoid rather than assuming Suisei's bone axes.
        if (!TryGetLightStickGrip(_artist, left: false, out var grip)) { _micHeadOnStand.Clear(); return; }

        int hand = _artist.BoneOf("rightHand");
        _micHandAttachment = new BoneAttachment3D {
            Name = "PerformerMicrophone",
            BoneName = _artist.Skeleton.GetBoneName(hand), BoneIdx = hand,
        };
        _artist.Skeleton.AddChild(_micHandAttachment);

        // Hidden until the pickup instant. The first `UpdateShowMicrophone` decides; without
        // this the microphone is in her hand AND on the stand for the whole preshow.
        _micHandProp = new Node3D { Name = "HandheldMic", Visible = false };
        _micHandAttachment.AddChild(_micHandProp);
        var bounds = _micHeadOnStand[0].Mesh.Transform * _micHeadOnStand[0].Mesh.GetAabb();
        foreach (var part in _micHeadOnStand) bounds = bounds.Merge(part.Mesh.Transform * part.Mesh.GetAabb());
        var handle = bounds.GetCenter();
        handle.Z = bounds.End.Z - .065f;
        var rebase = new Transform3D(new Basis(Vector3.Right, Mathf.Pi / 2), Vector3.Zero)
            * new Transform3D(Basis.Identity, -handle);
        foreach (var part in _micHeadOnStand) {
            var copy = (MeshInstance3D)part.Mesh.Duplicate();
            copy.Visible = true;
            // Bit 18 is the performer's reserved layer: her four dedicated lights cannot be
            // evicted from it, and a microphone lit differently from the hand holding it reads
            // as a floating prop.
            copy.Layers = 1u << 18;
            copy.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            _micHandProp.AddChild(copy);
            // Re-origin the cluster on the microphone body so the grip lands on the handle
            // rather than on the stand's coordinate origin.
            copy.Transform = rebase * part.Mesh.Transform;
        }
        // Held upright with the capsule angled away from the mouth, which is how a vocal mic is
        // actually held; the grip basis puts the prop's +Y along the axis of a tube in the fist.
        _micHandProp.Transform = grip * new Transform3D(
            Basis.FromEuler(new Vector3(Mathf.DegToRad(-18), 0, 0)), Vector3.Zero);
        _micHeld = null;
    }

    private void UpdateShowMicrophone(double seconds, bool performance)
    {
        if (_micHeadOnStand.Count == 0) return;
        bool held = performance && seconds >= MicPickupTime;
        if (_micHeld == held) return;
        _micHeld = held;
        foreach (var part in _micHeadOnStand)
            if (IsInstanceValid(part.Mesh)) part.Mesh.Visible = part.Visible && !held;
        if (IsInstanceValid(_micHandProp)) _micHandProp.Visible = held;
        // Her fingers are deliberately NOT forced closed here. The baked animation carries
        // rotation tracks for every humanoid bone including the fingers, and `_Process` samples
        // that clip AFTER presentation runs — so a procedural grip written from here would be
        // overwritten on the same frame. The hand shape around the microphone is authored in
        // `bake_mocap.py` instead, where it can be keyframed against the reach.
    }

    private void RestoreShowMicrophone()
    {
        foreach (var part in _micHeadOnStand)
            if (IsInstanceValid(part.Mesh)) part.Mesh.Visible = part.Visible;
        _micHeadOnStand.Clear();
        if (IsInstanceValid(_micHandAttachment)) _micHandAttachment.QueueFree();
        _micHandAttachment = null; _micHandProp = null; _micStand = null; _micHeld = null;
    }
}
