using System;
using System.Collections.Generic;
using Godot;
using SerikaSocial.Avatar;
using SerikaSocial.Player;

namespace SerikaSocial.Events;

public partial class EventShowPlayer
{
    private sealed class PlayerConcertStick
    {
        public AvatarInstance Avatar;
        /// Set when this holder is a network peer. Kept because a peer streaming its pose never
        /// runs `Animate`, so the hand grip has to be stepped from here instead.
        public RemoteAvatar Remote;
        public readonly Dictionary<bool, HeldConcertStick> Hands = new();
        public bool ScanManaged;
    }
    private sealed class HeldConcertStick
    {
        public BoneAttachment3D Attachment;
        public Node3D Prop;
        public Transform3D RestGrip;
        public bool Left;
    }

    private Node3D _lightStickTemplate, _lightStickWorld;
    private bool _lightStickTemplateVisible;
    private double _lightStickScanDelay;
    private LocalPlayer _lightStickLocalPlayer;
    private AvatarInstance _lightStickCheerAvatar;
    private readonly Dictionary<ulong, PlayerConcertStick> _playerConcertSticks = new();
    private readonly List<HeldConcertStick> _demonstrationSticks = new();
    private StandardMaterial3D _playerStickGlow;
    private bool _autoConcertCheer = true;
    public int PlayerLightStickCount {
        get { int count=0;foreach(var owner in _playerConcertSticks.Values) count+=owner.Hands.Count;return count; }
    }
    public int PlayerLightStickOwnerCount => _playerConcertSticks.Count;
    public bool LightStickTemplateReady => IsInstanceValid(_lightStickTemplate);

    private void SetupPlayerLightSticks(Node3D world)
    {
        RestorePlayerLightSticks();
        _lightStickWorld = world;
        _lightStickTemplate = world.FindChild("SERIKA_EVENT_LIGHTSTICK_TEMPLATE", true, false) as Node3D;
        if (!IsInstanceValid(_lightStickTemplate)) return;
        _lightStickTemplateVisible = _lightStickTemplate.Visible;
        _lightStickTemplate.Visible = false;
        _playerStickGlow = new StandardMaterial3D {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(.018f,.34f,1), EmissionEnabled = true,
            Emission = new Color(.01f,.22f,1), EmissionEnergyMultiplier = .4f
        };
        _lightStickScanDelay = 0;
        _autoConcertCheer = true;
    }

    private Node3D CloneConcertLightStick(Node3D parent)
    {
        if (!IsInstanceValid(_lightStickTemplate) || !IsInstanceValid(parent)) return null;
        var prop = _lightStickTemplate.Duplicate() as Node3D;
        if (prop == null) return null;
        prop.Name = "ConcertBlueLightStick";
        prop.Transform = Transform3D.Identity;
        prop.Visible = true;
        foreach (Node child in prop.FindChildren("*", "MeshInstance3D", true, false)) {
            var mesh = (MeshInstance3D)child;
            // The same accessory is visible in local first person, mirrors and peers.
            mesh.Layers = 1;
            mesh.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            if (mesh.Name.ToString().Contains("luminous", StringComparison.OrdinalIgnoreCase))
                mesh.MaterialOverride = _playerStickGlow;
        }
        parent.AddChild(prop);
        return prop;
    }

    /// Attach the authored prop to an existing grip node; used by the rehearsal preview.
    public Node3D GiveRehearsalLightStick(Node3D grip, bool left = false)
    {
        var prop = CloneConcertLightStick(grip);
        if (prop != null) _demonstrationSticks.Add(new HeldConcertStick {
            Prop=prop, RestGrip=Transform3D.Identity, Left=left
        });
        return prop;
    }

    /// Derive a palm grip from the avatar's own bind anatomy, independent of bone axes.
    public static bool TryGetLightStickGrip(AvatarInstance avatar, out Transform3D localGrip)
        => TryGetLightStickGrip(avatar, false, out localGrip);

    public static bool TryGetLightStickGrip(AvatarInstance avatar, bool left, out Transform3D localGrip)
    {
        localGrip = Transform3D.Identity;
        if (!IsInstanceValid(avatar) || !IsInstanceValid(avatar.Skeleton)) return false;
        var skeleton = avatar.Skeleton;
        string side = left ? "left" : "right";
        int hand = avatar.BoneOf(side+"Hand");
        if (hand < 0) return false;
        var rest = skeleton.GetBoneGlobalRest(hand);
        if (!float.IsFinite(rest.Basis.Determinant()) || Math.Abs(rest.Basis.Determinant()) < 1e-8f) return false;
        int middle = avatar.BoneOf(side+"MiddleProximal"), index = avatar.BoneOf(side+"IndexProximal"),
            little = avatar.BoneOf(side+"LittleProximal");
        Vector3 finger;
        float palmLength;
        if (middle >= 0) {
            finger = skeleton.GetBoneGlobalRest(middle).Origin - rest.Origin;
            palmLength = Mathf.Clamp(finger.Length(), .035f, .12f);
        } else {
            int forearm = avatar.BoneOf(side+"LowerArm");
            finger = forearm >= 0 ? rest.Origin - skeleton.GetBoneGlobalRest(forearm).Origin : Vector3.Down;
            palmLength = .06f;
        }
        if (!finger.IsFinite() || finger.LengthSquared() < 1e-8f) return false;
        finger = finger.Normalized();
        Basis gripBasis;
        Vector3 center;
        if (index >= 0 && little >= 0) {
            // Across the palm toward the index/thumb side is the axis of a held tube.
            var thumbSide = skeleton.GetBoneGlobalRest(index).Origin - skeleton.GetBoneGlobalRest(little).Origin;
            thumbSide -= finger * thumbSide.Dot(finger);
            if (thumbSide.LengthSquared() < 1e-8f) return false;
            thumbSide = thumbSide.Normalized();
            // Mirroring changes palm handedness; keep both grip centers on the palm side.
            var backOfHand = (thumbSide.Cross(finger) * (left ? -1 : 1)).Normalized();
            gripBasis = new Basis(backOfHand, thumbSide, backOfHand.Cross(thumbSide).Normalized());
            center = rest.Origin + finger * (palmLength * .55f) - backOfHand * .018f;
        } else {
            // Minimal bean rigs have no finger anatomy. Keep the accessory upright in rest
            // and let the actual wrist pose rotate it, without inventing finger rotations.
            gripBasis = Basis.Identity;
            var palmSide = Vector3.Forward - finger * Vector3.Forward.Dot(finger);
            if (palmSide.LengthSquared() < .01f) palmSide = left ? Vector3.Left : Vector3.Right;
            center = rest.Origin + finger * (palmLength * .55f) + palmSide.Normalized() * .055f;
        }
        localGrip = rest.AffineInverse() * new Transform3D(gripBasis, center);
        return localGrip.Origin.IsFinite() && localGrip.Basis.X.IsFinite()
            && localGrip.Basis.Y.IsFinite() && localGrip.Basis.Z.IsFinite();
    }

    /// A diagnostic can attach a real avatar explicitly; live scanning never includes Suisei.
    public Node3D AttachAvatarLightStick(AvatarInstance avatar, bool left = false)
    {
        var owner=EnsureAvatarLightSticks(avatar,false);
        return owner!=null && owner.Hands.TryGetValue(left,out var held)?held.Prop:null;
    }

    public void RemoveAvatarLightSticks(AvatarInstance avatar)
    {
        if(!IsInstanceValid(avatar) || !_playerConcertSticks.Remove(avatar.GetInstanceId(),out var owner))return;
        avatar.ReleaseConcertPropGrip();
        foreach(var hand in owner.Hands.Values)
            if(IsInstanceValid(hand.Attachment))hand.Attachment.QueueFree();
    }

    /// Samples the shipping prop/glow path for a paused rehearsal capture.
    public void SamplePlayerLightStickPresentation(double seconds,bool performance=true)
        => UpdatePlayerLightSticks(seconds,performance,0);

    private PlayerConcertStick EnsureAvatarLightSticks(AvatarInstance avatar, bool scanManaged, RemoteAvatar remote = null)
    {
        if (!IsInstanceValid(_lightStickTemplate) || !IsInstanceValid(avatar) || !IsInstanceValid(avatar.Skeleton)) return null;
        ulong id = avatar.GetInstanceId();
        if (!_playerConcertSticks.TryGetValue(id,out var owner)) {
            owner=new PlayerConcertStick { Avatar=avatar, ScanManaged=scanManaged };
            _playerConcertSticks[id]=owner;
        }
        owner.ScanManaged |= scanManaged;
        if (remote != null) owner.Remote = remote;
        // A hand carrying a stick is closed around it, cheer or no cheer. For a local player
        // this also goes out on the wire, because LOD0 pose frames carry the finger bones.
        avatar.SetConcertPropGrip(true);
        foreach(bool left in new[] {false,true}) {
            if(owner.Hands.TryGetValue(left,out var existing)) {
                if(IsInstanceValid(existing.Prop))continue;
                if(IsInstanceValid(existing.Attachment))existing.Attachment.QueueFree();
                owner.Hands.Remove(left);
            }
            if(!TryGetLightStickGrip(avatar,left,out var grip))continue;
            int hand = avatar.BoneOf(left?"leftHand":"rightHand");
            var attachment = new BoneAttachment3D {
                Name = left?"ConcertBlueLightStickLeft":"ConcertBlueLightStickRight",
                BoneName = avatar.Skeleton.GetBoneName(hand), BoneIdx = hand
            };
            avatar.Skeleton.AddChild(attachment);
            var prop = CloneConcertLightStick(attachment);
            if (prop == null) { attachment.QueueFree();continue; }
            prop.Transform = grip;
            owner.Hands[left]=new HeldConcertStick {
                Attachment=attachment, Prop=prop, RestGrip=grip, Left=left
            };
        }
        return owner;
    }

    private void FindConcertPlayers(Node node, HashSet<ulong> present)
    {
        AvatarInstance avatar = null;
        RemoteAvatar peer = null;
        if (node is LocalPlayer local) { avatar = local.Avatar; _lightStickLocalPlayer = local; }
        else if (node is VrPlayer vr) avatar = vr.Avatar;
        else if (node is RemoteAvatar remote) { avatar = remote.Avatar; peer = remote; }
        if (avatar != null && IsInstanceValid(avatar) && avatar != _artist) {
            present.Add(avatar.GetInstanceId());
            // Both local VR and remote users attach to the same solved avatar wrist, so
            // existing replicated hand poses produce the same prop pose for every viewer.
            EnsureAvatarLightSticks(avatar, true, peer);
            return;
        }
        // Players live outside the imported venue. Skip its geometry and all avatar/UI
        // subtrees so a half-second player scan never walks thousands of mesh surfaces.
        if (node == this || node == _lightStickWorld || node is AvatarInstance
            || node is Skeleton3D || node is MeshInstance3D || node is CanvasItem) return;
        foreach (Node child in node.GetChildren()) FindConcertPlayers(child, present);
    }

    private void UpdatePlayerLightSticks(double seconds, bool performance, double delta)
    {
        if (!LightStickTemplateReady) return;
        float beat = ConcertPenlightBeat(seconds);
        if(IsInstanceValid(_playerStickGlow))
            _playerStickGlow.EmissionEnergyMultiplier = performance ? .36f + .24f * beat : .36f;
        SetConcertCheerAvatar(IsInstanceValid(_lightStickLocalPlayer) ? _lightStickLocalPlayer.Avatar : null);
        if(IsInstanceValid(_lightStickCheerAvatar)) {
            _lightStickCheerAvatar.SetConcertCheerClock(seconds);
            _lightStickCheerAvatar.SetConcertCheerEnabled(_autoConcertCheer && performance
                && _lightStickLocalPlayer.ControlsEnabled && GetViewport().GuiGetFocusOwner()==null
                && _state?.Config?.LightSticks==true);
        }
        foreach(var owner in _playerConcertSticks.Values) {
            foreach(var hand in owner.Hands.Values) AnimateConcertLightStick(hand,seconds,performance,beat);
            if(!IsInstanceValid(owner.Avatar)) continue;
            owner.Avatar.SetConcertPropGrip(true);
            // A peer whose pose is arriving on the wire never reaches `Animate`, so its grip has
            // no other driver. Stand down when that pose is LOD0 and already carries the fingers.
            if(IsInstanceValid(owner.Remote) && !owner.Remote.LocalAnimationRunning)
                owner.Avatar.DriveStreamedPropGrip(delta, owner.Remote.StreamingFingers);
        }
        foreach(var hand in _demonstrationSticks) AnimateConcertLightStick(hand,seconds,performance,beat);
        _lightStickScanDelay -= Math.Max(0, delta);
        if (_lightStickScanDelay > 0) return;
        _lightStickScanDelay = .5;
        RefreshPlayerLightSticks(GetTree().Root);
    }

    private float ConcertPenlightBeat(double seconds)
    {
        var values=_state?.Config?.Beats;
        float fps=_state?.Config?.MusicFps??0;
        if(values==null || values.Length==0 || !float.IsFinite(fps) || fps<=0 || !double.IsFinite(seconds))return 0;
        double frame=Math.Clamp(seconds*fps,0,values.Length-1);
        int first=(int)frame,second=Math.Min(first+1,values.Length-1);
        float value=Mathf.Lerp(values[first],values[second],(float)(frame-first));
        return float.IsFinite(value)?Math.Clamp(value,0,1):0;
    }

    private static void AnimateConcertLightStick(HeldConcertStick hand,double seconds,bool performance,float beat)
    {
        if(!IsInstanceValid(hand.Prop))return;
        float time=double.IsFinite(seconds)?(float)seconds:0;
        float phase=hand.Left?1.1f:0;
        float sway=performance?(float)Math.Sin(time*2.1+phase)*(.025f+.035f*beat):0;
        float tip=performance?(float)Math.Sin(time*1.13+phase)*.022f:0;
        // Rotate the prop about its grip, never displace the hand or override tracked arms.
        hand.Prop.Transform=hand.RestGrip*new Transform3D(Basis.FromEuler(new Vector3(tip,0,sway)),Vector3.Zero);
    }

    private void RefreshPlayerLightSticks(Node scanRoot)
    {
        var present = new HashSet<ulong>();
        _lightStickLocalPlayer = null;
        bool enabled = _state?.Config?.LightSticks == true;
        if (enabled) FindConcertPlayers(scanRoot, present);
        SetConcertCheerAvatar(IsInstanceValid(_lightStickLocalPlayer) ? _lightStickLocalPlayer.Avatar : null);
        var expired = new List<ulong>();
        foreach (var pair in _playerConcertSticks) {
            var held = pair.Value;
            if (!IsInstanceValid(held.Avatar) || held.ScanManaged && !present.Contains(pair.Key)) {
                if (IsInstanceValid(held.Avatar)) held.Avatar.ReleaseConcertPropGrip();
                foreach(var hand in held.Hands.Values)
                    if (IsInstanceValid(hand.Attachment)) hand.Attachment.QueueFree();
                expired.Add(pair.Key);
            }
        }
        foreach (ulong id in expired) _playerConcertSticks.Remove(id);
    }

    private void SetConcertCheerAvatar(AvatarInstance avatar)
    {
        if (_lightStickCheerAvatar == avatar) return;
        if (IsInstanceValid(_lightStickCheerAvatar)) _lightStickCheerAvatar.SetConcertCheerEnabled(false);
        _lightStickCheerAvatar = IsInstanceValid(avatar) ? avatar : null;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_state?.Config?.LightSticks != true || !LightStickTemplateReady
            || @event is not InputEventKey { Pressed: true, Echo: false } key
            || key.PhysicalKeycode != Key.L && key.Keycode != Key.L
            || key.CtrlPressed || key.AltPressed || key.MetaPressed) return;
        var viewport = GetViewport();
        if (viewport.GuiGetFocusOwner() != null || !IsInstanceValid(_lightStickLocalPlayer)
            || !_lightStickLocalPlayer.ControlsEnabled) return;
        _autoConcertCheer = !_autoConcertCheer;
        if(!_autoConcertCheer) _lightStickLocalPlayer.Avatar?.SetConcertCheerEnabled(false);
        viewport.SetInputAsHandled();
    }

    private void RestorePlayerLightSticks()
    {
        SetConcertCheerAvatar(null);
        foreach (var held in _playerConcertSticks.Values) {
            if (IsInstanceValid(held.Avatar)) held.Avatar.ReleaseConcertPropGrip();
            foreach(var hand in held.Hands.Values)
                if (IsInstanceValid(hand.Attachment)) hand.Attachment.QueueFree();
        }
        foreach (var hand in _demonstrationSticks)
            if (IsInstanceValid(hand.Prop)) hand.Prop.QueueFree();
        _playerConcertSticks.Clear();
        _demonstrationSticks.Clear();
        if (IsInstanceValid(_lightStickTemplate)) _lightStickTemplate.Visible = _lightStickTemplateVisible;
        _lightStickTemplate = null;
        _lightStickWorld = null;
        _lightStickLocalPlayer = null;
        _playerStickGlow = null;
        _lightStickScanDelay = 0;
    }
}
