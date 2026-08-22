using Godot;
using System.Collections.Generic;

namespace SerikaSocial.Audio;

/// A spatial audio manager that places ambient and point-source sounds in the 3D world.
/// Uses AudioStreamPlayer3D for positional audio with distance-based attenuation.
/// In VR, Godot's AudioStreamPlayer3D automatically provides HRTF spatialization.
public partial class SpatialAudioManager : Node
{
    public static SpatialAudioManager Instance { get; private set; }

    private readonly Dictionary<string, AudioStreamPlayer3D> _ambientPlayers = new();
    private AudioStreamPlayer _uiPlayer;
    private bool _muted;

    public override void _Ready()
    {
        Instance = this;

        _uiPlayer = new AudioStreamPlayer
        {
            Name = "UIPlayer",
            VolumeDb = -6f,
        };
        AddChild(_uiPlayer);
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    /// Play a one-shot 3D sound at a world position (e.g. portal enter, footstep).
    public void Play3D(string resourcePath, Vector3 position, float volumeDb = -6f, float maxDistance = 20f)
    {
        if (_muted) return;
        var stream = ResourceLoader.Load<AudioStream>(resourcePath);
        if (stream == null) return;

        var player = new AudioStreamPlayer3D
        {
            Stream = stream,
            Position = position,
            VolumeDb = volumeDb,
            MaxDistance = maxDistance,
            AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance,
            UnitSize = 8f,
            Autoplay = true,
        };
        AddChild(player);
        player.Finished += () => player.QueueFree();
    }

    /// Start a looping ambient sound at a position (e.g. fireplace crackle, wind).
    public void PlayAmbient(string name, string resourcePath, Vector3 position, float volumeDb = -12f, float maxDistance = 15f)
    {
        if (_muted) return;
        if (_ambientPlayers.ContainsKey(name)) return;

        var stream = ResourceLoader.Load<AudioStream>(resourcePath);
        if (stream == null) return;

        var player = new AudioStreamPlayer3D
        {
            Name = $"Ambient_{name}",
            Stream = stream,
            Position = position,
            VolumeDb = volumeDb,
            MaxDistance = maxDistance,
            AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance,
            UnitSize = 6f,
            Autoplay = true,
        };
        AddChild(player);
        _ambientPlayers[name] = player;
    }

    /// Stop a named ambient sound.
    public void StopAmbient(string name)
    {
        if (_ambientPlayers.TryGetValue(name, out var player))
        {
            player.Stop();
            player.QueueFree();
            _ambientPlayers.Remove(name);
        }
    }

    /// Play a UI sound (non-spatial, e.g. button click, notification).
    public void PlayUI(string resourcePath, float volumeDb = -8f)
    {
        if (_muted) return;
        var stream = ResourceLoader.Load<AudioStream>(resourcePath);
        if (stream == null) return;
        _uiPlayer.Stream = stream;
        _uiPlayer.VolumeDb = volumeDb;
        _uiPlayer.Play();
    }

    /// Set the master mute state.
    public void SetMuted(bool muted)
    {
        _muted = muted;
        var master = AudioServer.GetBusIndex("Master");
        if (master >= 0)
            AudioServer.SetBusMute(master, muted);
    }

    /// Set the master volume (0.0 to 1.0).
    public void SetVolume(float volume)
    {
        var master = AudioServer.GetBusIndex("Master");
        if (master >= 0)
            AudioServer.SetBusVolumeDb(master, Mathf.LinearToDb(Mathf.Clamp(volume, 0f, 1f)));
    }

    /// Stop all ambient sounds (used when leaving a world).
    public void StopAllAmbient()
    {
        foreach (var kvp in _ambientPlayers)
        {
            kvp.Value.Stop();
            kvp.Value.QueueFree();
        }
        _ambientPlayers.Clear();
    }
}
