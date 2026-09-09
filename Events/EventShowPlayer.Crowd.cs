using System;
using System.Collections.Generic;
using Godot;

namespace SerikaSocial.Events;

public partial class EventShowPlayer
{
    private readonly List<(MeshInstance3D Mesh, Material Original, ShaderMaterial Animated,
        GeometryInstance3D.ShadowCastingSetting Shadow, float CullMargin)> _crowdPenlights = new();
    private static readonly StringName CrowdTime = new("crowd_time"),
        CrowdBrightness = new("brightness"), CrowdMovement = new("movement"),
        CrowdHype = new("hype"), CrowdBeatWave = new("beat_wave");
    /// Spacing of the four delayed beat taps handed to the shader. A hit therefore
    /// reaches the back of the arena 0.21 s after the front — about half a beat at this
    /// show's tempo, so the pens visibly ripple instead of firing as one slab.
    private const double CrowdBeatTap = .07;
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
            // Measured, not guessed, against the 216,970 authored stick vertices: the
            // farthest sits .4613 m from its wrist pivot, the shader's per-stick constants
            // cap the composite swing at .748 rad of sway over .785 rad of tip, and the
            // beat thrust adds .224 m of lift. Chord + lift = .6912 m of travel away from
            // the rest position, so anything under that pops sticks off the screen edge.
            mesh.ExtraCullMargin = Math.Max(mesh.ExtraCullMargin, .70f);
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
        float level = performance ? .12f + .74f * RevealLevel : .16f;
        float movement = performance ? .38f + .62f * RevealLevel : .25f;
        // `musicEnergy` sits around .55 through a quiet verse and pins at 1 through a
        // chorus, so it has to be contrast-stretched before it drives anything: fed in raw,
        // the whole show reads as one continuous chorus, which is the flat uniform field
        // this replaces. Stretched, a verse lands near 0 and a drop near 1, and the crowd
        // audibly lifts between them. Gated by RevealLevel so the entrance stays calm.
        var config = _state?.Config;
        float energy = performance
            ? ShowTimeline.Envelope(config?.MusicEnergy, seconds, config?.MusicFps ?? 0) : 0;
        float hype = Mathf.SmoothStep(.42f, .92f, energy) * RevealLevel;
        // The audience is still holding sticks before the reveal, so the beat keeps a
        // quarter of its weight there and reaches full stroke only once the show is lit.
        float weight = performance ? .25f + .75f * RevealLevel : 0;
        var wave = weight > 0 ? new Vector4(
            ConcertPenlightBeat(seconds), ConcertPenlightBeat(seconds - CrowdBeatTap),
            ConcertPenlightBeat(seconds - 2 * CrowdBeatTap),
            ConcertPenlightBeat(seconds - 3 * CrowdBeatTap)) * weight : Vector4.Zero;
        // Five scalars per batch per frame, and every one of them is the same for all six.
        // Per-stick variety is derived in the vertex shader from the authored wrist pivot
        // and phase — nothing here may ever grow with the 8,345 sticks.
        foreach (var batch in _crowdPenlights) {
            batch.Animated.SetShaderParameter(CrowdTime, time);
            batch.Animated.SetShaderParameter(CrowdBrightness, level);
            batch.Animated.SetShaderParameter(CrowdMovement, movement);
            batch.Animated.SetShaderParameter(CrowdHype, hype);
            batch.Animated.SetShaderParameter(CrowdBeatWave, wave);
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
