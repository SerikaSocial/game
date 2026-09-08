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
                $"{KeyBindings.KeyFor("toggle_view")} · Focus stage / return to audience view   ·   Jumping disabled";
        }
        if (!GodotObject.IsInstanceValid(_eventFocusCamera)) return;
        if (!_eventVenue || !GodotObject.IsInstanceValid(_eventShow) || !_eventShow.ReadyToPlay) { LeaveEventFocus(); return; }
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
        _eventFocusCamera.MakeCurrent();
    }
    private void LeaveEventFocus()
    {
        if (GodotObject.IsInstanceValid(_eventFocusCamera)) {
            if (GodotObject.IsInstanceValid(_localDesktop)) _localDesktop.ViewCamera?.MakeCurrent();
            _eventFocusCamera.QueueFree();
        }
        _eventFocusCamera = null;
        if (GodotObject.IsInstanceValid(_eventFocusFade)) _eventFocusFade.Visible = false;
    }
}
