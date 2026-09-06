using Godot;
using System;
using System.Text.Json;
using System.Threading.Tasks;
using Serika.Net;
using SerikaSocial.UI;

namespace SerikaSocial;

public partial class Main
{
    private string _personalHomeWorldId;
    private bool _savingHome;
    private int _homePreferenceRevision;
    private readonly WorldTravelOperation _travel = new();
    private bool _joiningInstance => _travel.Target == WorldTravelOperation.Destination.Instance;
    private int _transportTravelVersion;
    private sealed record PreparedInstance(int Version, string WorldId, string InstanceId, int Access,
        string Owner, string Name, string Endpoint, string Ticket);
    private PreparedInstance _preparedInstance;
    private int _currentInstanceAccess;
    private string _currentInstanceOwner;
    private string LocationWorldId => _inHome ? _defaultHome?.World.Id : _currentWorldId;
    private bool CanInviteHere => !string.IsNullOrEmpty(_currentInstanceId)
        && (_currentInstanceAccess == 0 || _currentInstanceOwner == _localUserId);

    private async Task<JsonElement> FetchPersonalHomeSelection()
    {
        var api = _api;
        string account = _localUserId;
        int revision = _homePreferenceRevision;
        var response = await api.GetPersonalHomeAsync();
        if (_api == api && _localUserId == account && _homePreferenceRevision == revision) ReadPersonalHome(response);
        return response;
    }

    private async Task RefreshPersonalHomeSelection()
    {
        try { await FetchPersonalHomeSelection(); UpdateWorldActions(); }
        catch (Exception e) { GD.Print($"Personal Home preference refresh unavailable: {e.Message}"); }
    }

    private void ReadPersonalHome(JsonElement response)
    {
        _personalHomeWorldId = response.TryGetProperty("homeWorldId", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString() : null;
    }

    private async Task SavePersonalHome(string worldId)
    {
        if (_api?.SessionToken == null || _savingHome) return;
        _savingHome = true;
        _homePreferenceRevision++;
        UpdateWorldActions();
        _hud?.SetWorldActionStatus("Saving your Home…");
        _quickMenu?.SetWorldActionStatus("Saving your Home…");
        try
        {
            ReadPersonalHome(await _api.SetPersonalHomeAsync(worldId));
            DefaultHomeResolver.ForgetSelection(ProjectSettings.GlobalizePath("user://worlds"), _localUserId);
            string message = worldId == null ? "Home reset to the community default." : "Saved as your Home. Use Go Home to visit.";
            _hud?.SetWorldActionStatus(message);
            _quickMenu?.SetWorldActionStatus(message);
            _inWorldHud?.Toast(message, 4);
        }
        catch (Exception e)
        {
            string message = WorldActionError(e);
            _hud?.SetWorldActionStatus(message, true);
            _quickMenu?.SetWorldActionStatus(message, true);
        }
        finally
        {
            _savingHome = false;
            UpdateWorldActions();
        }
    }

    private void UpdateWorldActions()
    {
        string id = LocationWorldId;
        _hud?.SetHomeSelection(_personalHomeWorldId, _savingHome);
        _quickMenu?.SetWorldActions(!string.IsNullOrEmpty(id), id != null && id == _personalHomeWorldId,
            !string.IsNullOrEmpty(id), _inHome ? "Personal Home" : _currentInstanceAccess == 0 ? "Public instance" : "Private · invite only", _savingHome || _travel.Active);
        _quickMenu?.SetHomeResetAvailable(_personalHomeWorldId != null && !_savingHome);
    }

    private void OpenWorldCatalogue()
    {
        CloseAllMenus();
        CloseWorldList();
        _mainMenu?.Open(_username, 1);
        SyncMenuHold();
    }

    private void OpenSocialMenu()
    {
        CloseAllMenus();
        CloseWorldList();
        _socialPanel?.Configure(_api);
        _socialPanel?.Open();
        SyncMenuHold();
    }

    private void ToggleActionMenu()
    {
        if (_actionMenu?.IsOpen ?? false)
        {
            if (!_actionMenu.BackOut()) _actionMenu.Hide();
        }
        else
        {
            CloseAllMenus();
            CloseWorldList();
            RecentreVrPanel();
            _actionMenu?.Open();
        }
        SyncMenuHold();
    }

    private static string WorldActionError(Exception e)
    {
        string code = e.Message ?? "";
        if (code.Contains("instance_private") || code.Contains("invite_required") || code.Contains("not_allowed") || code.Contains("forbidden"))
            return "This instance is private. Ask its owner for an invitation.";
        if (code.Contains("full")) return "That instance is full. Choose another or start your own.";
        if (code.Contains("closed") || code.Contains("not_found")) return "That instance or world is no longer available.";
        if (code.Contains("not_published") || code.Contains("home_world")) return "Choose a published, ready world for your Home.";
        if (code.Contains("maintenance")) return "Serika is under maintenance. Please try again shortly.";
        if (code.Contains("no_relay")) return "No server is available right now. Please try again shortly.";
        if (e is System.Net.Http.HttpRequestException || e is TaskCanceledException)
            return "Couldn't reach Serika. Check your connection and try again.";
        GD.PrintErr($"World action failed: {code}");
        return "That action couldn't be completed. Please try again.";
    }

    private Task JoinPrivateWorld(string worldId) => JoinWorldById(worldId, createPrivate: true);

    private async Task JoinExactInstance(string instanceId, string worldId = null)
    {
        if (_api == null || string.IsNullOrEmpty(instanceId)
            || !_travel.TryBegin(WorldTravelOperation.Destination.Instance, out int version)) return;
        CloseAllMenus();
        CloseWorldList();
        ShowLoading("Joining this instance…");
        try
        {
            // Resolve/download first: the single-use relay ticket lasts only 60 seconds.
            var instance = await _api.GetInstanceDetailAsync(instanceId);
            worldId = instance.TryGetProperty("worldId", out var wid) ? wid.GetString() : worldId;
            if (string.IsNullOrEmpty(worldId)) throw new InvalidOperationException("world_not_found");
            await PrepareWorldDownload(worldId);
            var joined = await _api.JoinInstanceByIdAsync(instanceId);
            CompleteInstanceJoin(joined, worldId, version);
        }
        catch (Exception e)
        {
            CallDeferred(nameof(OnJoinFailed), WorldActionError(e), version);
        }
    }

    private async Task PrepareWorldDownload(string worldId)
    {
        var detail = await _api.GetWorldDetailAsync(worldId);
        if (!detail.TryGetProperty("downloadUrl", out var du) || du.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("world_not_found");
        ShowLoading("Preparing world…");
        if (await _api.DownloadWorldAsync(du.GetString(), worldId) == null)
            throw new System.Net.Http.HttpRequestException("World download failed");
    }

    private void CompleteInstanceJoin(JsonElement joined, string worldId, int version)
    {
        if (!_travel.IsCurrent(version, WorldTravelOperation.Destination.Instance)) return;
        var instance = joined.GetProperty("instance");
        // Keep the current instance's privacy and destination together until the old transport
        // is detached. A peer event from that transport must never publish mixed old/new state.
        _preparedInstance = new PreparedInstance(version, worldId, instance.GetProperty("id").GetString(),
            instance.TryGetProperty("access", out var access) ? access.GetInt32() : 0,
            instance.TryGetProperty("ownerId", out var owner) && owner.ValueKind == JsonValueKind.String ? owner.GetString() : null,
            joined.TryGetProperty("worldName", out var wn) ? wn.GetString() : "World",
            joined.GetProperty("endpoint").GetString(), joined.GetProperty("ticket").GetString());
        CallDeferred(nameof(OnJoinReady), version);
    }

    private void UpdatePrivatePresence(int playerCount)
    {
        // A private gathering never publishes its destination or a join secret to Discord.
        RpcPresence.UpdateState(_currentInstanceAccess == 0 ? _worldName : "In a private instance", playerCount, 16,
            _currentInstanceAccess == 0 ? _currentWorldId : null);
    }
}
