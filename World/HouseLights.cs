using Godot;
using SerikaSocial.World.Video;

namespace SerikaSocial.World;

/// Theatre house lights: when a clip starts, the room goes down and the picture becomes the
/// only thing lighting it.
///
/// Attached by `WorldLoader` to any world whose manifest asks for the `dark` lighting mode and
/// that resolved at least one `SERIKA_VIDEO` marker. Deliberately *not* attached to bright
/// worlds — a video screen in a daylit plaza should not put out the sun.
///
/// Two halves, and both are needed to read as a cinema:
///
///   1. **The house goes down.** Three different things light one of these rooms and all three
///      have to go, or the result is merely dimmer rather than dark. `Light3D`s fade to black;
///      the emissive fittings (aisle strips, cove, step lights, exit signs) fade to a marker
///      glow, since they are *materials* and no amount of dimming lights touches them; and the
///      environment's ambient fill goes out, because a flat directionless fill is the one thing
///      that can never be made to look like it comes from the screen.
///
///   2. **The screen takes over.** A single omni in front of the picture, its colour and
///      brightness sampled from the frame being decoded, so a bright snow scene washes the front
///      rows and a night scene barely lifts them. Without this the room simply goes black, since
///      the screen material is `Unshaded` and emits nothing into the scene.
///
/// Restores on stop, so the lights come back up between clips.
public partial class HouseLights : Node
{
    // Three separate things light this room, and dimming only the first left it far brighter
    // than a cinema: the lamps, the glowing strips, and the environment's ambient fill. All
    // three are pulled down by the same `_dim` curve so they move together.

    /// How far the world's own lights drop while a picture is on. Desktop goes fully dark;
    /// Quest keeps a walkable remainder because Compatibility cannot light the room from the
    /// screen bounce alone.
    private static float DimTo => UI.DeviceProfile.IsStandaloneXr ? 0.18f : 0.0f;
    /// How far the *emissive* fittings drop — the aisle strips, step lights, cove and exit
    /// signs. Desktop: out entirely. Quest: a marker glow so the aisles stay readable.
    private static float EmissiveDimTo => UI.DeviceProfile.IsStandaloneXr ? 0.22f : 0.0f;
    /// Ambient fill while a picture is on. Desktop goes out; Quest keeps enough to see.
    private static float AmbientDimTo => UI.DeviceProfile.IsStandaloneXr ? 0.40f : 0.0f;

    /// Lights fade rather than switch: an instant cut reads as a bug, a fade reads as a cinema.
    /// Long, because the fade *is* the effect — at a couple of seconds it reads as a glitch you
    /// half-noticed rather than the house going down, and the point is to watch it happen.
    /// Slower going down (the film is starting) than coming back up.
    private const float FadeDownSeconds = 7.0f;
    private const float FadeUpSeconds = 4.5f;

    /// Longest frame allowed to advance the fade.
    ///
    /// The fade is driven by `delta`, and the two moments it runs are exactly the two moments
    /// the frame time spikes: a decoder spinning up, and a clip ending and tearing down. A
    /// single 2-second hitch would otherwise consume half the fade in one frame and the lights
    /// would appear to snap — which is precisely what "restore is instant" looked like. Capping
    /// the step makes a stall cost real time rather than fade progress.
    private const float MaxFadeStep = 1f / 20f;

    /// Peak energy of the screen bounce, for a fully white frame.
    private const float BounceEnergy = 3.2f;
    /// How fast the bounce chases the sampled frame colour, in e-folds per second. Low enough
    /// that a cut to black does not slam the room off, high enough that it still tracks the
    /// picture. This is also what made the *restore* look instant even while the lamps were
    /// still fading: the only light in the room died in a quarter of a second and the eye read
    /// that as the whole transition being over.
    private const float BounceResponse = 1.2f;

    private readonly System.Collections.Generic.List<(Light3D Light, float Base)> _house = new();
    private readonly System.Collections.Generic.List<(BaseMaterial3D Mat, float Base)> _emissive = new();
    private Godot.Environment _env;
    private float _ambientBase;
    private OmniLight3D _bounce;
    private bool _captured;

    /// 0 = house lights full, 1 = fully down.
    private float _dim;
    private Color _bounceColour = new(0f, 0f, 0f);
    private double _sampleClock;

    /// Seconds between frame samples. A `GetImage()` on the decoder's texture is a GPU→CPU
    /// readback and stalls the pipeline, so it runs at a handful of hertz, not per frame — the
    /// value is smoothed anyway, and average frame colour does not change meaningfully faster
    /// than this. Low tier does not sample at all and gets a fixed cool white.
    private static double SampleInterval => UI.DeviceProfile.Current switch
    {
        UI.DeviceProfile.Tier.High => 1.0 / 6.0,
        UI.DeviceProfile.Tier.Medium => 1.0 / 3.0,
        _ => double.PositiveInfinity,
    };

    public override void _Process(double delta)
    {
        // Capture on the first frame rather than in _Ready. The load path is still adding lights
        // and stamping the device profile over them while this node is being parented, so
        // anything read at _Ready time risks recording an energy that is about to change — and
        // that value is what the lights get restored to when the film ends.
        if (!_captured) Capture();

        bool playing = AnyScreenPlaying(out var screen);

        float step = Mathf.Min((float)delta, MaxFadeStep);
        float rate = step / (playing ? FadeDownSeconds : FadeUpSeconds);
        _dim = Mathf.Clamp(_dim + (playing ? rate : -rate), 0f, 1f);

        float scale = Mathf.Lerp(1f, DimTo, _dim);
        foreach (var (light, baseEnergy) in _house)
        {
            if (!GodotObject.IsInstanceValid(light)) continue;
            light.LightEnergy = baseEnergy * scale;
            // A light at zero energy still costs a shadow pass. Cull it outright once it is
            // contributing nothing — 9 lamps and 112 seats is a real saving on a Quest.
            light.Visible = light.LightEnergy > 0.001f;
        }

        float emissiveScale = Mathf.Lerp(1f, EmissiveDimTo, _dim);
        foreach (var (mat, baseEnergy) in _emissive)
        {
            if (!GodotObject.IsInstanceValid(mat)) continue;
            mat.EmissionEnergyMultiplier = baseEnergy * emissiveScale;
        }

        if (_env != null && GodotObject.IsInstanceValid(_env))
            _env.AmbientLightEnergy = Mathf.Lerp(_ambientBase, _ambientBase * AmbientDimTo, _dim);

        UpdateBounce(delta, playing, screen);
    }

    /// Snapshot every light the world brought, and build the screen bounce.
    private void Capture()
    {
        _captured = true;
        var world = GetParent();
        if (world == null) return;

        foreach (var node in world.FindChildren("*", "Light3D", true, false))
        {
            if (node is not Light3D l || l == _bounce) continue;
            _house.Add((l, l.LightEnergy));
        }

        CaptureEmissive(world);
        CaptureEnvironment();

        // The bounce is parented to the screen quad, so it follows a screen wherever the world
        // put it, and inherits nothing else. Placed a little in front along the quad's facing
        // (a QuadMesh points down its local +Z).
        //
        // Range and attenuation are deliberately tight. A wide, slow falloff lit the whole
        // auditorium evenly — including the far wall — which reads as "someone left a light on",
        // not as spill from a screen. Reaching about one and a half screen-widths with a square
        // falloff puts the front rows in the picture's light and leaves the back of the house
        // genuinely black, which is what a cinema looks like.
        if (FirstScreen() is { Surface: { } surface })
        {
            float width = (surface.Mesh as QuadMesh)?.Size.X ?? 6f;
            _bounce = new OmniLight3D
            {
                // Not "ScreenBounce" — the Cinema bundle already ships a light by that name, and
                // two identically named lights in one diagnostic dump is a trap.
                Name = "VideoBounce",
                Position = new Vector3(0, 0, width * 0.25f),
                OmniRange = Mathf.Clamp(width * 1.6f, 8f, 30f),
                OmniAttenuation = 2.0f,
                LightEnergy = 0f,
                Visible = false,
                // The picture already casts no shadow (the quad is unshaded); making its bounce
                // cast one would put hard seat-shaped shadows on the back wall from a light that
                // is meant to read as spill.
                ShadowEnabled = false,
            };
            surface.AddChild(_bounce);
        }

        GD.Print($"HouseLights: watching {_house.Count} world light(s), " +
                 $"{_emissive.Count} emissive surface(s), " +
                 $"ambient {(_env != null ? _ambientBase.ToString("0.00") : "none")}, " +
                 $"bounce {(_bounce != null ? "on" : "unavailable")}");
    }

    /// The glowing fittings — aisle strips, cove, step lights, exit signs — are emissive
    /// *materials*, not lights, which is why dimming `Light3D`s alone still left the Cinema lit
    /// up like a corridor. They have to be dimmed through their emission energy.
    ///
    /// Each one is duplicated onto a surface override rather than edited in place. A glTF
    /// material is a shared resource: a merged mesh uses one material across many surfaces, and
    /// mutating it would reach outside this world and leave no way back if the node tree is
    /// freed mid-fade. An override is per-instance and dies with the world.
    private void CaptureEmissive(Node world)
    {
        // A pathological user world could have thousands of surfaces; this is a cinema feature,
        // not a general renderer pass, so it gives up rather than duplicating unboundedly.
        const int MaxSurfaces = 512;

        foreach (var node in world.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (node is not MeshInstance3D mi || mi.Mesh == null) continue;
            // Never touch the picture itself: it is the one thing that stays lit.
            if (mi.Name.ToString() == "VideoScreen") continue;

            for (int i = 0; i < mi.Mesh.GetSurfaceCount(); i++)
            {
                if (_emissive.Count >= MaxSurfaces) return;
                if (mi.GetActiveMaterial(i) is not BaseMaterial3D src) continue;
                if (!src.EmissionEnabled || src.EmissionEnergyMultiplier <= 0.001f) continue;

                var dup = (BaseMaterial3D)src.Duplicate();
                mi.SetSurfaceOverrideMaterial(i, dup);
                _emissive.Add((dup, dup.EmissionEnergyMultiplier));
            }
        }
    }

    /// The environment's ambient fill. It lives on a `WorldEnvironment` that `WorldLoader` adds
    /// as a *sibling* of the world geometry, not inside it, so it is not reachable from this
    /// node's parent — hence the search from the tree root. Only one is ever live at a time.
    private void CaptureEnvironment()
    {
        var tree = GetTree();
        if (tree?.Root == null) return;
        foreach (var node in tree.Root.FindChildren("*", "WorldEnvironment", true, false))
        {
            if (node is not WorldEnvironment we || we.Environment == null) continue;
            _env = we.Environment;
            _ambientBase = _env.AmbientLightEnergy;
            return;
        }
    }

    /// Drive the screen bounce from the frame being decoded.
    private void UpdateBounce(double delta, bool playing, VideoScreen screen)
    {
        if (_bounce == null || !GodotObject.IsInstanceValid(_bounce)) return;

        Color target = new(0f, 0f, 0f);
        if (playing)
        {
            _sampleClock += delta;
            if (_sampleClock >= SampleInterval)
            {
                _sampleClock = 0;
                _sampled = SampleFrame(screen) ?? _sampled;
            }
            target = _sampled;
        }

        // Chase, don't snap — see BounceResponse. Capped like the fade, so a hitch cannot jump it.
        float k = Mathf.Min(1f, Mathf.Min((float)delta, MaxFadeStep) * BounceResponse);
        _bounceColour = _bounceColour.Lerp(target, k);

        // The bounce is only allowed to be as bright as the house is dark, so it fades up with
        // the same curve the lights fade down on and there is no moment where both are lit.
        float luma = 0.2126f * _bounceColour.R + 0.7152f * _bounceColour.G + 0.0722f * _bounceColour.B;
        _bounce.LightEnergy = BounceEnergy * luma * _dim;
        _bounce.Visible = _bounce.LightEnergy > 0.001f;
        if (_bounce.Visible && luma > 0.001f)
        {
            // Normalise to hue only: brightness is carried by energy, so a dim blue frame tints
            // the room blue rather than washing it grey.
            _bounce.LightColor = new Color(_bounceColour.R / luma, _bounceColour.G / luma,
                                           _bounceColour.B / luma).Clamp();
        }
    }

    private Color _sampled = new(0.8f, 0.85f, 1.0f); // cool white, the Low-tier fallback

    /// Average colour of the current frame, or null when it cannot be read.
    private static Color? SampleFrame(VideoScreen screen)
    {
        var tex = screen?.FrameTexture;
        if (tex == null) return null;
        var img = tex.GetImage();
        if (img == null || img.IsEmpty()) return null;

        // Downscale before averaging. Reading 1280x720 pixels one at a time from C# is far more
        // expensive than the readback itself; 32x18 is a coarse but perfectly good estimate of
        // where the frame's light is, and the result is temporally smoothed anyway.
        img.Resize(32, 18, Image.Interpolation.Bilinear);
        float r = 0, g = 0, b = 0;
        for (int y = 0; y < 18; y++)
        for (int x = 0; x < 32; x++)
        {
            var c = img.GetPixel(x, y);
            r += c.R; g += c.G; b += c.B;
        }
        const float n = 32 * 18;
        return new Color(r / n, g / n, b / n);
    }

    private bool AnyScreenPlaying(out VideoScreen playing)
    {
        playing = null;
        var tree = GetTree();
        if (tree == null) return false;
        foreach (var node in tree.GetNodesInGroup(VideoScreen.Group))
        {
            if (node is not VideoScreen s || !s.IsRendering) continue;
            playing = s;
            return true;
        }
        return false;
    }

    private VideoScreen FirstScreen()
    {
        var tree = GetTree();
        if (tree == null) return null;
        foreach (var node in tree.GetNodesInGroup(VideoScreen.Group))
            if (node is VideoScreen s) return s;
        return null;
    }
}
