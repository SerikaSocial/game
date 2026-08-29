using Godot;

namespace SerikaSocial.World;

/// Distance-based LOD system for world geometry. On low-tier devices (Quest), meshes are
/// categorised by size and assigned visibility ranges so distant detail fades out instead
/// of hammering the GPU every frame.
///
/// Also sets Godot's `LodBias` on each mesh, pushing the engine's built-in LOD transitions
/// closer on weaker hardware.
///
/// This is a load-time pass — it walks the scene tree once and stamps properties, then the
/// engine handles the per-frame culling. No per-frame cost from this class.
public static class WorldLod
{
    // Size thresholds (world-space AABB volume).
    private const float SmallVolume = 0.5f;   // props, decorations
    private const float MediumVolume = 8.0f;  // furniture, small structures

    // Visibility ranges per category (metres).
    private const float SmallFadeBegin = 15f;
    private const float SmallFadeEnd = 22f;
    private const float MedFadeBegin = 35f;
    private const float MedFadeEnd = 50f;

    /// Walk the world scene tree and apply LOD settings tuned for the current device tier.
    /// Only does meaningful work on `Tier.Low`; on desktop the overhead of full-detail worlds
    /// is negligible and the pop-in would just be ugly.
    public static void Apply(Node worldRoot)
    {
        var tier = UI.DeviceProfile.Current;
        if (tier == UI.DeviceProfile.Tier.High) return; // desktop: nothing to do

        float lodBias = tier == UI.DeviceProfile.Tier.Low ? 0.4f : 0.7f;
        int tagged = 0;

        foreach (Node n in worldRoot.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (n is not MeshInstance3D mi || mi.Mesh == null) continue;

            // Push LOD transitions closer on weaker hardware.
            mi.LodBias = lodBias;

            // Categorise by AABB volume and assign visibility ranges. Godot's
            // VisibilityRangeFadeMode.Dependencies makes meshes smoothly cross-fade rather
            // than pop, which looks better in VR where pops are very noticeable.
            var aabb = mi.GetAabb();
            float volume = aabb.Volume;

            if (volume < SmallVolume)
            {
                mi.VisibilityRangeEnd = tier == UI.DeviceProfile.Tier.Low ? SmallFadeEnd : SmallFadeEnd * 1.5f;
                mi.VisibilityRangeBegin = 0f;
                mi.VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Dependencies;
                tagged++;
            }
            else if (volume < MediumVolume)
            {
                mi.VisibilityRangeEnd = tier == UI.DeviceProfile.Tier.Low ? MedFadeEnd : MedFadeEnd * 1.3f;
                mi.VisibilityRangeBegin = 0f;
                mi.VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Dependencies;
                tagged++;
            }
            // Large structures (volume >= MediumVolume): always visible — these are walls,
            // floors, terrain that define the space.
        }

        if (tagged > 0)
            GD.Print($"WorldLod: tagged {tagged} meshes with visibility ranges (tier={tier}, lodBias={lodBias})");
    }

    /// Enable engine-level occlusion culling. On Quest the software rasteriser is
    /// essentially free (it uses spare CPU cores the GPU-bound frame doesn't need) and
    /// saves significant draw calls in enclosed worlds like the Cinema or Backrooms.
    public static void EnableOcclusionCulling()
    {
        // Off. The software occluder treats single-sided authored rooms (Cinema, Backrooms)
        // as closed volumes and culls the interior to black — which read as "there are no
        // lights" on Quest. Re-enable only with a tested occluder mesh per world.
        ProjectSettings.SetSetting("rendering/occlusion_culling/use_occlusion_culling", false);
    }
}
