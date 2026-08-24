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
    private readonly Dictionary<string, List<MeshInstance3D>> _toggleMeshes = new();

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

            // Find meshes that match this toggle name. VRC toggles typically control
            // GameObjects by name; we match child node names in the avatar model.
            var meshes = new List<MeshInstance3D>();
            FindMeshesByName(_avatar, toggle.Name, meshes);
            _toggleMeshes[toggle.Name] = meshes;
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
        if (!_toggleMeshes.TryGetValue(name, out var meshes)) return;
        bool on = _states[name];
        foreach (var mesh in meshes)
        {
            if (GodotObject.IsInstanceValid(mesh))
                mesh.Visible = on;
        }
    }
}
