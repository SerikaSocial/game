using Godot;
using System;
using System.Threading.Tasks;
using Serika.Net;
using SerikaSocial.World;

namespace SerikaSocial;

public partial class Main
{
    private DefaultHomeResolver.Resolved _defaultHome;
    private Task _enterHomeTask;
    private int _preparedHomeVersion;
    private string _homeName = "Home";

    // Both startup and the Home menu use the same registry. Re-entering refreshes an admin
    // selection; concurrent Home clicks share the request and cannot build duplicate worlds.
    private void EnterHome()
    {
        if (_travel.TryBegin(WorldTravelOperation.Destination.Home, out int version))
            _enterHomeTask = ResolveAndEnterHomeAsync(version);
    }

    private async Task ResolveAndEnterHomeAsync(int version)
    {
        ShowLoading("Loading Home…");
        try
        {
            _defaultHome = _api == null ? null : await DefaultHomeResolver.ResolveAsync(
                _api.BaseUrl, ProjectSettings.GlobalizePath("user://worlds"),
                FetchPersonalHomeSelection, world => _api.DownloadWorldAsync(world.DownloadUrl, world.Id), _localUserId);
        }
        catch (Exception e)
        {
            GD.PrintErr($"Home registry unavailable: {e.Message}");
            _defaultHome = null;
        }
        if (!_travel.IsCurrent(version, WorldTravelOperation.Destination.Home)) return;
        _preparedHomeVersion = version;
        CallDeferred(nameof(EnterPreparedHome));
    }

    private Worlds.Home BuildSelectedHome(Node3D root)
    {
        _homeName = "Home";
        if (_defaultHome != null)
        {
            try
            {
                var spawn = WorldLoader.LoadFromPath(_defaultHome.Path, _defaultHome.World.Id, root);
                if (spawn.HasValue && HasPlayableHomeGeometry(root))
                {
                    _homeName = _defaultHome.World.Name;
                    return new Worlds.Home(spawn.Value, null, null);
                }
            }
            catch (Exception e) { GD.PrintErr($"Selected Home failed to load: {e.Message}"); }
            // Remove partial importer output before building the complete offline fallback.
            foreach (Node child in root.GetChildren()) { root.RemoveChild(child); child.QueueFree(); }
        }
        return Worlds.BuildHome(root);
    }

    private static bool HasPlayableHomeGeometry(Node root)
    {
        bool visibleGeometry = false, solidGeometry = false;
        void Visit(Node node)
        {
            if (node is MeshInstance3D mesh && mesh.Visible && mesh.Mesh?.GetSurfaceCount() > 0)
                visibleGeometry = true;
            if (node is CollisionShape3D shape && shape.GetParent() is StaticBody3D
                && !shape.Disabled && shape.Shape != null) solidGeometry = true;
            foreach (Node child in node.GetChildren())
            {
                if (visibleGeometry && solidGeometry) return;
                Visit(child);
            }
        }
        Visit(root);
        return visibleGeometry && solidGeometry;
    }
}
