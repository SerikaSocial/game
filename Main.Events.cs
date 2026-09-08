using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SerikaSocial.Events;
using SerikaSocial.UI;
using Serika.Net;
namespace SerikaSocial;

public partial class Main
{
    private EventAdminPanel _eventAdmin;
    private EventShowPlayer _eventShow;
    private LiveEvent[] _liveEvents = Array.Empty<LiveEvent>();
    private double _eventPoll, _eventListPoll;
    private bool _eventPolling, _eventPreview, _eventStaff;
    private ApiClient _eventApi;
    private void InitializeEvents()
    {
        _eventAdmin = new EventAdminPanel { Name = "EventAdmin" }; AddUi(_eventAdmin);
        _eventAdmin.Closed += SyncMenuHold;
        _eventAdmin.CurrentCamera = () => _localVr?.HeadCamera ?? GetViewport().GetCamera3D();
        _eventAdmin.PerformerMarker = () => (_worldRoot?.FindChild("SERIKA_EVENT_PERFORMER*", true, false) as Node3D)?.GlobalPosition;
        _eventAdmin.VisitVenue += e => { _ = JoinWorldById(e.WorldId, true); };
        _eventAdmin.PreviewRequested += (e, seconds, path) => _ = PreviewEvent(e, seconds, path);
        _quickMenu.AdminEventsPressed += OpenEventAdmin;
        _mainMenu.AdminEventsPressed += OpenEventAdmin;
        _quickMenu.EventsBanner.JoinRequested += id => _ = JoinEvent(id);
        _mainMenu.EventsBanner.JoinRequested += id => _ = JoinEvent(id);
        _quickMenu.EventOptions.HidePlayersToggled += on => { _eventHidePlayers = on; ApplyEventPlayerVisibility(); };
        _quickMenu.EventOptions.MutePlayersToggled += on => { _eventMutePlayers = on; if (_voice != null) _voice.MuteEveryone = on; };
        _quickMenu.EventOptions.EffectsToggled += on => {
            UI.DeviceProfile.Settings.EventEffects = !on;
            UI.DeviceProfile.Settings.Save();
            if (_eventShow != null && GodotObject.IsInstanceValid(_eventShow)) _eventShow.EffectsEnabled = !on;
        };
    }

    private bool _eventHidePlayers, _eventMutePlayers;
    /// Attendees only. Hiding "all players" at a concert means the other people in the hall —
    /// the performer is the show, and the local player's own rig is not a peer.
    private void ApplyEventPlayerVisibility()
    {
        foreach (var remote in _remotes.Values)
            if (GodotObject.IsInstanceValid(remote)) remote.Visible = !_eventHidePlayers;
    }

    /// The venue owns these controls, so they follow the player in and out of it. Leaving drops
    /// the blanket mute and un-hides everyone: they are venue triage, not standing preferences.
    /// "Disable effects" is the exception — it lives in settings and survives the trip.
    private void SyncEventPresence()
    {
        bool inEvent = _inWorld && _eventShow != null && GodotObject.IsInstanceValid(_eventShow);
        _quickMenu.SetInEvent(inEvent);
        _mainMenu.EventsBanner.Suppressed = inEvent;
        if (!inEvent && (_eventHidePlayers || _eventMutePlayers)) {
            _eventHidePlayers = _eventMutePlayers = false;
            if (_voice != null) _voice.MuteEveryone = false;
            ApplyEventPlayerVisibility();
        }
        if (inEvent) _quickMenu.EventOptions.SetState(_eventHidePlayers, _eventMutePlayers, !UI.DeviceProfile.Settings.EventEffects);
    }
    private void OpenEventAdmin()
    {
        if (!_eventStaff) return;
        CloseAllMenus(); _eventAdmin.Api = _api; _eventAdmin.Open(); SyncMenuHold();
    }
    private void TickEvents(double delta)
    {
        if (_api?.SessionToken == null || _eventAdmin == null) return;
        _eventPoll += delta; _eventListPoll += delta;
        SyncEventPresence();
        if (_eventPoll < 2 || _eventPolling) return;
        _eventPoll = 0; _ = PollEvents();
    }
    private async Task PollEvents()
    {
        _eventPolling = true;
        try {
            var api = _api;
            if (_eventApi != api) {
                var user = await api.EventRequestAsync("/v1/session/me");
                if(_api != api)return; _eventApi = api;
                _eventStaff = user.TryGetProperty("isAdmin", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.True;
                _quickMenu.SetEventAdmin(_eventStaff); _mainMenu.SetEventAdmin(_eventStaff); _eventListPoll = 20;
            }
            if (_eventListPoll >= 10) {
                _eventListPoll = 0; _liveEvents = await api.GetEventsAsync();
                if (_api != api) return;
                _quickMenu.EventsBanner.SetEvents(_liveEvents, url => api.GetImageBytesAsync(api.EventAssetUrl(url)));
                _mainMenu.EventsBanner.SetEvents(_liveEvents, url => api.GetImageBytesAsync(api.EventAssetUrl(url)));
            }
            if (_eventShow != null && (!GodotObject.IsInstanceValid(_eventShow) || !_eventShow.IsInsideTree())) _eventShow = null;
            if (_eventPreview) return;
            if (_eventShow != null) {
                var player = _eventShow; ulong before = Time.GetTicksMsec(); var state = await api.GetEventAsync(player.EventId);
                if (player != _eventShow || !GodotObject.IsInstanceValid(player)) return;
                _eventShow.ApplyState(state, Time.GetTicksMsec() - before);
                if (!state.IsOpen) { StopEventPlayer(); _inWorldHud?.Toast("The event has ended.", 4); EnterHome(); }
            } else if (_inWorld && _currentWorldId != null) {
                var state = _liveEvents.FirstOrDefault(e => e.WorldId == _currentWorldId);
                if (state != null) {
                    string worldId = _currentWorldId; var world = _worldRoot;
                    state = await api.GetEventAsync(state.Id);
                    if (_api != api || world != _worldRoot || worldId != _currentWorldId || !state.IsOpen) return;
                    _eventShow = new EventShowPlayer { Name = "LiveEventShow", EffectsEnabled = UI.DeviceProfile.Settings.EventEffects };
                    _worldRoot.AddChild(_eventShow);
                    _eventShow.Failed += message => _inWorldHud?.Toast("Show could not load: " + message, 8);
                    // Building the show is one long synchronous frame — the performer rig, the
                    // animation GLB, the stage rig and every one of its shaders. The first visit
                    // on a machine compiles those shaders and the window simply stops responding
                    // for a while, which reads as a crash unless something said so beforehand.
                    // This toast is raised while the assets are still downloading so it is
                    // already on screen when the stall lands.
                    _inWorldHud?.Toast("Loading the show… the first time in a venue can take a few minutes.", 240);
                    var show = _eventShow;
                    await _eventShow.Prepare(api, state, _worldRoot);
                    if (GodotObject.IsInstanceValid(show) && show == _eventShow)
                        _inWorldHud?.Toast(show.ReadyToPlay ? "The show is ready." : "The show could not be loaded.", 4);
                }
            }
        } catch (Exception e) { GD.PrintErr("Events: " + e.Message); }
        finally { _eventPolling = false; }
    }
    private void StopEventPlayer()
    {
        _eventPreview = false;
        if (GodotObject.IsInstanceValid(_eventShow)) { _eventShow.GetParent()?.RemoveChild(_eventShow); _eventShow.QueueFree(); }
        _eventShow = null;
    }
    private async Task JoinEvent(string id)
    {
        if (_api == null || !_travel.TryBegin(WorldTravelOperation.Destination.Instance, out int version)) return;
        CloseAllMenus(); CloseWorldList();
        try {
            var state = await _api.GetEventAsync(id);
            if (!state.IsOpen) throw new InvalidOperationException("The event is closed.");
            await PrepareWorldDownload(state.WorldId); ShowLoading("Joining " + state.Title + "…");
            var joined = await _api.EventRequestAsync("/v1/events/" + id + "/join", new {});
            CompleteInstanceJoin(joined, state.WorldId, version); _eventListPoll = 20;
        } catch (Exception e) { CallDeferred(nameof(OnJoinFailed), e.Message, version); }
    }
    private async Task PreviewEvent(LiveEvent state, double seconds, bool drawPath)
    {
        if (!_eventStaff) return;
        if (state == null) { StopEventPlayer(); return; }
        if (_currentWorldId != state.WorldId || !_inWorld) { _eventAdmin.Status("Visit this event venue first."); return; }
        StopEventPlayer(); _eventPreview = true;
        _eventShow = new EventShowPlayer { Name = "StaffShowPreview", Preview = true, PreviewPlaying = true, PreviewPosition = seconds }; _worldRoot.AddChild(_eventShow);
        _eventShow.Failed += _eventAdmin.Status;
        await _eventShow.Prepare(_api, state, _worldRoot);
        if (GodotObject.IsInstanceValid(_eventShow) && _eventShow.ReadyToPlay) {
            _eventShow.DrawCameraPath(drawPath); _eventAdmin.Status($"Preview ready at {seconds:0.0}s. Both stage screens show the camera output.");
        }
    }
}
