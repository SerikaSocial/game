using System;
using System.Collections.Generic;
using Godot;

namespace SerikaSocial.Events;

public partial class EventShowPlayer
{
    private readonly List<(MeshInstance3D Mesh, Material Original, ShaderMaterial Animated,
        GeometryInstance3D.ShadowCastingSetting Shadow, float CullMargin)> _crowdPenlights = new();
    private static readonly StringName CrowdTime = new("crowd_time"),
        CrowdBrightness = new("brightness"), CrowdMovement = new("movement"), CrowdBeat = new("beat");
    public int CrowdStickCount { get; private set; }
    public int CrowdBatchCount => _crowdPenlights.Count;

    private void SetupConcertAudience(Node3D world)
    {
        var shader = GD.Load<Shader>("res://Shaders/concert_crowd_penlight.gdshader");
        foreach (Node node in world.FindChildren("SERIKA_EVENT_CROWD_STICKS_*", "MeshInstance3D", true, false)) {
            var mesh = (MeshInstance3D)node;
            var material = new ShaderMaterial { Shader = shader };
            _crowdPenlights.Add((mesh, mesh.MaterialOverride, material, mesh.CastShadow, mesh.ExtraCullMargin));
            mesh.MaterialOverride = material;
            mesh.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            // The longest .46 m tube moves under .12 m sideways at maximum authored sway.
            mesh.ExtraCullMargin = Math.Max(mesh.ExtraCullMargin, .16f);
            string name = mesh.Name;
            int separator = name.LastIndexOf('_');
            if (separator >= 0 && int.TryParse(name[(separator + 1)..], out int count))
                CrowdStickCount += count;
        }
    }

    private void UpdateConcertAudience(double seconds, bool performance, bool runCinematicFx)
    {
        float time = double.IsFinite(seconds) ? (float)seconds : 0;
        // Skip one full frame when running in reduced quality to keep shader bandwidth under control.
        if(!runCinematicFx) {
            return;
        }
        // Blue audience penlights stay faint during the concealed entrance; they do not
        // illuminate the artist or undo the rig blackout. Playback/seek controls this clock.
        float level = performance ? .10f + .65f * RevealLevel : .16f;
        float movement = performance ? .35f + .45f * RevealLevel : .25f;
        foreach (var batch in _crowdPenlights) {
            batch.Animated.SetShaderParameter(CrowdTime, time);
            batch.Animated.SetShaderParameter(CrowdBrightness, level);
            batch.Animated.SetShaderParameter(CrowdMovement, movement);
            batch.Animated.SetShaderParameter(CrowdBeat, performance?ConcertPenlightBeat(seconds):0);
        }
    }

    private void RestoreConcertAudience()
    {
        foreach (var batch in _crowdPenlights) if (IsInstanceValid(batch.Mesh)) {
            batch.Mesh.MaterialOverride = batch.Original;
            batch.Mesh.CastShadow = batch.Shadow;
            batch.Mesh.ExtraCullMargin = batch.CullMargin;
        }
        _crowdPenlights.Clear();
        CrowdStickCount = 0;
    }
}
