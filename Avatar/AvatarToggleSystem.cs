using System;
using System.Collections.Generic;
using Godot;

namespace SerikaSocial.Avatar;

/// Manages avatar toggle state — named on/off switches for mesh groups, extracted from VRC
/// avatar expression parameters. Each toggle controls the visibility of specific mesh nodes
/// in the avatar hierarchy. The toggle names come from .ska v2 metadata; the mesh associations
/// are resolved by name matching (toggle name → child node name in the avatar model).
///
/// Toggle state is per-instance and can be driven by:
/// - The avatar's radial menu (in-world)
/// - The avatar selector UI
/// - Network sync (future — requires proto change)
public sealed class AvatarToggleSystem
{
    private readonly AvatarInstance _avatar;
    private readonly Dictionary<string, bool> _states = new();
    // Meshes whose node name matches the toggle — hide/show the entire MeshInstance3D.
    private readonly Dictionary<string, List<MeshInstance3D>> _toggleMeshes = new();
    // (mesh, surfaceIndex) pairs whose material name matches the toggle — hide/show one surface.
    private readonly Dictionary<string, List<(MeshInstance3D mesh, int surface)>> _toggleSurfaces = new();

    public IReadOnlyDictionary<string, bool> States => _states;

    public AvatarToggleSystem(AvatarInstance avatar)
    {
        _avatar = avatar ?? throw new ArgumentNullException(nameof(avatar));
        Initialize();
    }

    private void Initialize()
    {
        if (_avatar.Meta?.Toggles == null || _avatar.Meta.Toggles.Count == 0) return;

        foreach (var toggle in _avatar.Meta.Toggles)
        {
            _states[toggle.Name] = toggle.DefaultOn;

            // Match by node name: find MeshInstance3D nodes whose name contains the toggle name.
            var meshes = new List<MeshInstance3D>();
            FindMeshesByName(_avatar, toggle.Name, meshes);
            _toggleMeshes[toggle.Name] = meshes;

            // Match by material name: find surfaces whose material name contains the toggle name.
            // This handles props (shield, sword, etc.) that are primitives within a single mesh
            // rather than separate mesh nodes.
            var surfaces = new List<(MeshInstance3D, int)>();
            FindSurfacesByMaterialName(_avatar, toggle.Name, surfaces);
            _toggleSurfaces[toggle.Name] = surfaces;
        }

        ApplyAll();
    }

    /// Find all MeshInstance3D nodes whose name contains the toggle name (case-insensitive).
    private static void FindMeshesByName(Node root, string name, List<MeshInstance3D> result)
    {
        if (root is MeshInstance3D mesh && mesh.Name.ToString().Contains(name, StringComparison.OrdinalIgnoreCase))
            result.Add(mesh);
        foreach (var child in root.GetChildren())
            FindMeshesByName(child, name, result);
    }

    /// Find all (mesh, surfaceIndex) pairs whose material name contains the toggle name.
    /// This catches props like shields and swords that are individual primitives within a
    /// single mesh, not separate mesh nodes — the node-name match above would miss them.
    private static void FindSurfacesByMaterialName(Node root, string name, List<(MeshInstance3D, int)> result)
    {
        if (root is MeshInstance3D mi && mi.Mesh != null)
        {
            int count = mi.Mesh.GetSurfaceCount();
            for (int s = 0; s < count; s++)
            {
                var mat = mi.Mesh.SurfaceGetMaterial(s);
                if (mat == null) mat = mi.GetSurfaceOverrideMaterial(s);
                string matName = mat?.ResourceName ?? "";
                if (matName.Contains(name, StringComparison.OrdinalIgnoreCase))
                    result.Add((mi, s));
            }
        }
        foreach (var child in root.GetChildren())
            FindSurfacesByMaterialName(child, name, result);
    }

    /// Set a toggle's state and apply it.
    public void SetToggle(string name, bool on)
    {
        if (!_states.ContainsKey(name)) return;
        _states[name] = on;
        ApplyToggle(name);
    }

    /// Toggle a toggle's state (flip on/off).
    public void Toggle(string name)
    {
        if (!_states.ContainsKey(name)) return;
        _states[name] = !_states[name];
        ApplyToggle(name);
    }

    /// Get a toggle's current state.
    public bool GetToggle(string name) => _states.TryGetValue(name, out var v) && v;

    /// Apply all toggle states to the avatar meshes.
    public void ApplyAll()
    {
        foreach (var name in _states.Keys)
            ApplyToggle(name);
    }

    private void ApplyToggle(string name)
    {
        // Node-level: hide/show entire MeshInstance3D nodes.
        if (_toggleMeshes.TryGetValue(name, out var meshes))
        {
            bool on = _states[name];
            foreach (var mesh in meshes)
            {
                if (GodotObject.IsInstanceValid(mesh))
                    mesh.Visible = on;
            }
        }

        // Surface-level: hide/show individual primitives within a mesh by setting their
        // override material to null (hidden) or restoring the original (visible). We use
        // a sentinel: when hiding, we set the override to a transparent material; when
        // showing, we clear the override so the original material shows through.
        if (_toggleSurfaces.TryGetValue(name, out var surfaces))
        {
            bool on = _states[name];
            foreach (var (mesh, surface) in surfaces)
            {
                if (!GodotObject.IsInstanceValid(mesh)) continue;
                if (on)
                    mesh.SetSurfaceOverrideMaterial(surface, null);
                else
                    mesh.SetSurfaceOverrideMaterial(surface, Hidden);
            }
        }
    }

    /// A fully transparent material used to hide individual surfaces without affecting the
    /// rest of the mesh. Using an override material means the original material is preserved
    /// and can be restored by clearing the override.
    private static StandardMaterial3D _hidden;
    private static StandardMaterial3D Hidden => _hidden ??= new StandardMaterial3D
    {
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        AlbedoColor = new Color(0, 0, 0, 0),
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        NoDepthTest = true,
        ResourceName = "__toggle_hidden__",
    };
}
