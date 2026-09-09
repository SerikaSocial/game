using Godot;
using SerikaSocial.Player;
using SerikaSocial.UI;
namespace SerikaSocial;
public partial class Main
{
    private Node3D _eventRulesWorld;
    private bool _eventVenue;
    private double _eventRulesProbe;
    private Camera3D _eventFocusCamera;
    /// Keeps the spatial mix on the player's own body while the tracked stage camera flies.
    ///
    /// Godot has no listener by default: the CURRENT Camera3D is the listener. So making the
    /// focus camera current silently moved the ears onto a camera that cranes out over the
    /// audience and swings to face the stage — the PA speakers panned and attenuated with the
    /// shot, and a flyover across the crowd smeared the mix into nothing. Reported as "sound in
    /// F5 flyover cam is not sticky to the stage". The player has not moved, so their mix must
    /// not either; an explicit listener on the view camera outranks whichever camera is current.
    /// The rehearsal scene never saw this because ConcertRehearsal plants its own body listener.
    private AudioListener3D _eventFocusListener;
    private Label _eventControlsHint;
    private ColorRect _eventFocusFade;
    private void TickEventAudienceControls(double delta)
    {
        if (!_inWorld || _worldRoot != _eventRulesWorld) {
            LeaveEventFocus();
            _eventVenue = false; _eventRulesProbe = 0; _eventRulesWorld = _worldRoot;
        }
        if (_inWorld && !_eventVenue && (_eventRulesProbe -= delta) <= 0) {
            _eventRulesProbe = .5;
            _eventVenue = _worldRoot?.FindChild("SERIKA_EVENT_PERFORMER*", true, false) != null;
        }
        if (GodotObject.IsInstanceValid(_localDesktop)) _localDesktop.EventAudienceMode = _eventVenue;
        if (GodotObject.IsInstanceValid(_localVr)) _localVr.EventAudienceMode = _eventVenue;
        if (_eventVenue && _eventControlsHint == null) {
            var layer = new CanvasLayer { Name = "EventControlsHint", Layer = 5 };
            AddUi(layer, chrome: true);
            _eventFocusFade = new ColorRect { Color = Colors.Transparent, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
            layer.AddChild(_eventFocusFade);
            _eventFocusFade.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _eventControlsHint = new Label { Theme = Brand.Theme, MouseFilter = Control.MouseFilterEnum.Ignore,
                HorizontalAlignment = HorizontalAlignment.Center };
            layer.AddChild(_eventControlsHint);
            _eventControlsHint.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom);
            _eventControlsHint.Position = new Vector2(-350,-70);
            _eventControlsHint.Size = new Vector2(700,40);
        }
        if (_eventControlsHint != null) {
            _eventControlsHint.Visible = _eventVenue;
            _eventControlsHint.Text = _vrMode ? "Concert venue · Jumping disabled" :
                $"{KeyBindings.KeyFor("toggle_view")} · Tracked stage camera / first-person view   ·   Jumping disabled";
        }
        if (!GodotObject.IsInstanceValid(_eventFocusCamera)) return;
        if (!_eventVenue || !GodotObject.IsInstanceValid(_eventShow) || !_eventShow.ReadyToPlay) { LeaveEventFocus(); return; }
        // The view camera is rebuilt on respawn and on an avatar swap, which frees the listener
        // with it and silently drops the mix back onto the flyover. Re-anchor while focused.
        AnchorEventFocusListener();
        _eventFocusFade.Visible = true;
        _eventFocusFade.Color = new Color(0,0,0,_eventShow.BroadcastFadeOpacity);
        var source = _eventShow.BroadcastCamera;
        _eventFocusCamera.GlobalTransform = source.GlobalTransform;
        _eventFocusCamera.Fov = source.Fov;
        if (_eventShow.IntroIsPlaying && _eventShow.TryGetPreshowCameraPose(GetViewport().GetVisibleRect().Size.Aspect(), out var position, out var target, out var fov)) {
            _eventFocusCamera.GlobalPosition = position; _eventFocusCamera.LookAt(target); _eventFocusCamera.Fov = fov;
        }
    }
    private void ToggleEventFocus()
    {
        if (GodotObject.IsInstanceValid(_eventFocusCamera)) { LeaveEventFocus(); return; }
        if (!GodotObject.IsInstanceValid(_eventShow) || !_eventShow.ReadyToPlay) {
            _inWorldHud?.Toast("The show is loading. Focus view will be ready shortly."); return;
        }
        _eventFocusCamera = new Camera3D { Name = "EventFocus", Far = 2500,
            CullMask = 0xfffff & ~LocalPlayer.NonFpCullLayers };
        _worldRoot.AddChild(_eventFocusCamera);
        TickEventAudienceControls(0);
        // Pin the ears to the body BEFORE the camera goes current, so no frame is ever
        // mixed from the flyover's position.
        AnchorEventFocusListener();
        _eventFocusCamera.MakeCurrent();
    }
    /// Parent an explicit listener to whichever view camera the player is actually looking
    /// through. It follows the head for free and, being explicit, keeps the mix even though a
    /// different camera is current.
    private void AnchorEventFocusListener()
    {
        var head = GodotObject.IsInstanceValid(_localVr) ? (Node3D)_localVr.HeadCamera
            : GodotObject.IsInstanceValid(_localDesktop) ? _localDesktop.ViewCamera : null;
        if (head == null) return;
        if (!GodotObject.IsInstanceValid(_eventFocusListener)) {
            _eventFocusListener = new AudioListener3D { Name = "EventFocusListener" };
            head.AddChild(_eventFocusListener);
        } else if (_eventFocusListener.GetParent() != head) {
            _eventFocusListener.Reparent(head, false);
            _eventFocusListener.Transform = Transform3D.Identity;
        }
        _eventFocusListener.MakeCurrent();
    }
    private void LeaveEventFocus()
    {
        if (GodotObject.IsInstanceValid(_eventFocusListener)) {
            // Hand the mix back to the current camera, which is the player's own again.
            _eventFocusListener.ClearCurrent();
            _eventFocusListener.QueueFree();
        }
        _eventFocusListener = null;
        if (GodotObject.IsInstanceValid(_eventFocusCamera)) {
            if (GodotObject.IsInstanceValid(_localDesktop)) _localDesktop.ViewCamera?.MakeCurrent();
            _eventFocusCamera.QueueFree();
        }
        _eventFocusCamera = null;
        if (GodotObject.IsInstanceValid(_eventFocusFade)) _eventFocusFade.Visible = false;
    }
}
