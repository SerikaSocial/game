using System;
using System.Collections.Generic;
using Godot;
using SerikaSocial.UI;

namespace SerikaSocial.Player;

/// Photographs every real 2D screen **as it appears in the headset**.
///
///   Godot --path game --windowed --audio-driver Dummy -- --serika-uivr --out /tmp/uivr [--screens a,b]
///
/// (Needs a real display — `DISPLAY=:1` or `xvfb-run`. Headless has no rendering server and
/// every capture comes back blank, so every visual check silently passes.)
///
/// **Why this exists on top of the other two.** `--serika-vrsim` proves the *routing*: a screen
/// mounted on `VrUiSurface` ends up on the panel and a controller ray can click it. It does that
/// with a synthetic probe button, so it says nothing about whether a real screen is legible or
/// even fits. `--serika-uishot --vr` photographs the real screens, but it builds them from `Main`
/// and then *reparents* them onto a panel that is created afterwards — every screen is therefore
/// constructed while `VrUiSurface.Active` is still false for at least part of its life, and the
/// screens that branch on it (`Brand.Scrim`, `Hud.BuildHomeButton`, and now the VR type scale)
/// bake in the desktop answer. It also captures at ~15 px/deg, which is noticeably softer than a
/// Quest and flags text that is actually fine.
///
/// This one sets `VrUiSurface.Active` first, constructs every screen fresh underneath a real
/// `VrUiSurface`, positions the panel through the shipping `FaceCamera` path, and captures from a
/// camera at the headset eye with the capture sized so that **1 degree of the player's view is
/// 20 capture pixels** — a Quest 2's angular resolution. Text that cannot be read in these PNGs
/// cannot be read in the headset.
///
/// It also prints numbers, because "is this too small" is arithmetic and not an opinion:
///
///   * the panel's measured angular subtense, from unprojecting its own corners;
///   * `logical px per degree`, which converts any `font_size` in `UI/` into an angular size;
///   * the alpha bounding box of what each screen actually drew on the panel, in logical px —
///     which catches both overflow (content wider than `LogicalSize`, silently clipped by the
///     viewport) and the opposite failure, a screen using 30% of the panel and leaving the rest
///     as a slab of empty violet;
///   * the fraction of the panel each screen paints nearly opaque, which is how a full-screen
///     scrim — a desktop idiom that is simply wrong on a floating panel — shows up as a number.
public static partial class VrUiShotDiagnostic
{
    /// Capture geometry. `Camera3D.Fov` is the **vertical** angle under the default keep-height
    /// aspect mode, so the pixels-per-degree of a pinhole capture is
    /// `(height/2) / tan(fov/2) * (pi/180)`, uniform across the frame in tangent space.
    /// 1600 px at 70 deg gives `800/0.7002*0.01745 = 19.9` px/deg, i.e. a Quest 2.
    private const float EyeFovDegrees = 70f;
    private static readonly Vector2I EyeShotSize = new(2560, 1600);

    /// Where the eye sits. Standing head height; the panel is placed relative to it by the
    /// shipping `FaceCamera`, so the distance and height in the shot are the ones VR uses.
    private const float EyeHeight = 1.6f;

    /// Frames between showing a screen and reading it back. Screens fade and animate in, and the
    /// panel's viewport is only re-enabled once some layer turns visible, so a capture on the next
    /// frame catches a half-transparent screen on a stale render target.
    private const int SettleFrames = 20;

    public static void Run(Node host, string outPrefix, string screens)
    {
        outPrefix ??= "/tmp/uivr";

        if (DisplayServer.GetName() == "headless")
        {
            GD.Print("UIVR FAIL: running headless — there is no rendering server, so every " +
                     "capture would be blank and every check would pass. Use DISPLAY=:1 or xvfb-run.");
            host.GetTree().Quit(1);
            return;
        }

        // MUST be first. `Brand.Scrim` and several screens consult this while *constructing*, so a
        // screen built before this line is a desktop screen for the rest of its life — which is
        // exactly the flaw that made the older `--serika-uishot --vr` shots misleading.
        VrUiSurface.Active = true;

        // The brand theme, exactly as `Main` installs it. Without it every Control that styles
        // itself through the theme rather than through an explicit override — plain `Button`s,
        // `LineEdit`s, scrollbars — falls back to Godot's default grey, and the shots then report
        // a branding defect that only exists in the fixture. The VR keyboard is entirely
        // theme-styled and looked like a stock Android keyboard until this was added.
        host.GetTree().Root.Theme = Brand.Theme;

        var viewport = new SubViewport
        {
            Name = "UivrEye",
            Size = EyeShotSize,
            OwnWorld3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        host.AddChild(viewport);

        var world = new Node3D { Name = "UivrWorld" };
        viewport.AddChild(world);
        BuildRoom(world);

        var surface = new VrUiSurface { Name = "VrUi" };
        world.AddChild(surface);

        var cam = new Camera3D
        {
            Name = "EyeCam",
            Fov = EyeFovDegrees,
            KeepAspect = Camera3D.KeepAspectEnum.Height,
            Current = true,
            Position = new Vector3(0, EyeHeight, 0),
        };
        world.AddChild(cam);

        host.AddChild(new Driver(host, surface, viewport, cam, outPrefix, screens));
    }

    /// A plain lit room rather than a void.
    ///
    /// The panel is transparent chrome with a translucent scrim behind the card, so on a black
    /// background it always looks crisp and well-seated. In a world it is hanging in front of lit
    /// geometry, and that is the condition under which "the scrim is a slab" and "this text has no
    /// contrast" are actually visible. The walls are deliberately mid-grey and *bright*: a dim
    /// room flatters the panel the same way black does.
    private static void BuildRoom(Node3D root)
    {
        root.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.42f, 0.44f, 0.50f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.85f, 0.85f, 0.92f),
                AmbientLightEnergy = 1.0f,
            },
        });

        Slab(root, new Vector3(0, 0, 0), new Vector3(12, 0.1f, 12), new Color(0.38f, 0.40f, 0.46f));
        Slab(root, new Vector3(0, 1.6f, -3.2f), new Vector3(12, 3.2f, 0.2f), new Color(0.55f, 0.52f, 0.48f));
        Slab(root, new Vector3(-3.2f, 1.6f, 0), new Vector3(0.2f, 3.2f, 12), new Color(0.48f, 0.55f, 0.52f));

        // A high-contrast reference the panel is seen against: if a screen's own background is
        // darker than this it reads as a slab, and if it is lighter it disappears into the wall.
        Slab(root, new Vector3(0, 1.35f, -3.05f), new Vector3(1.2f, 0.9f, 0.05f), new Color(0.9f, 0.9f, 0.95f));
    }

    private static void Slab(Node3D root, Vector3 at, Vector3 size, Color color)
    {
        root.AddChild(new MeshInstance3D
        {
            Position = at,
            Mesh = new BoxMesh { Size = size },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = color,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
        });
    }

    /// One screen: a name, the layer, and how to bring it up.
    private sealed record Shot(string Name, CanvasLayer Layer, Action Show);

    // ------------------------------------------------------------------ the run

    private sealed partial class Driver : Node
    {
        private readonly Node _host;
        private readonly VrUiSurface _ui;
        private readonly SubViewport _eye;
        private readonly Camera3D _cam;
        private readonly string _out;
        private readonly string _filter;

        private List<Shot> _shots;
        private int _index = -1;
        private int _wait = 4;      // let the surface's _Ready run before anything is mounted
        private bool _built;
        private int _failures;

        /// Logical UI pixels per degree of the player's view. Filled in by `MeasurePanel` from
        /// the panel's own unprojected corners, so it is measured rather than assumed.
        private float _logicalPxPerDeg;

        public Driver(Node host, VrUiSurface ui, SubViewport eye, Camera3D cam, string outPrefix, string filter)
        {
            _host = host; _ui = ui; _eye = eye; _cam = cam; _out = outPrefix; _filter = filter;
        }

        public override void _Process(double delta)
        {
            if (_wait > 0) { _wait--; return; }

            if (!_built)
            {
                _built = true;
                // The panel parks itself at a fixed local offset in `_Ready`; `FaceCamera` is what
                // VR actually uses to place it, so use that or the measured geometry is fiction.
                _ui.FaceCamera(_cam);
                MeasurePanel();
                _shots = BuildShots();
                if (_shots.Count == 0)
                {
                    GD.Print($"UIVR FAIL: no screens matched '{_filter}'");
                    GetTree().Quit(1);
                    return;
                }
                _wait = SettleFrames;
                return;
            }

            if (_index >= 0) Capture(_shots[_index]);

            _index++;
            if (_index >= _shots.Count)
            {
                GD.Print(_failures == 0
                    ? "UIVR: done, no captures failed"
                    : $"UIVR: done with {_failures} capture failure(s)");
                GetTree().Quit(_failures == 0 ? 0 : 1);
                return;
            }

            HideAll();
            try { _shots[_index].Show(); }
            catch (Exception e) { GD.Print($"UIVR FAIL {_shots[_index].Name}: {e.Message}"); _failures++; }
            _wait = SettleFrames;
        }

        // ---------------------------------------------------------------- geometry

        /// Unproject the panel's own corners and report what the player's eye actually sees.
        ///
        /// Everything downstream — "is this font big enough" — is a conversion through
        /// `logical px per degree`, so it has to come from the real mesh at its real distance and
        /// not from the constants in `VrUiSurface`, which is where an assumption would hide.
        private void MeasurePanel()
        {
            var xf = _ui.Panel.GlobalTransform;
            float aspect = (float)_ui.LogicalSize.Y / _ui.LogicalSize.X;
            // The panel mesh is a 2.0 m chord; its half-extents in panel-local space.
            float halfW = 1.0f;
            float halfH = 1.0f * aspect;

            var left = xf * new Vector3(-halfW, 0, 0);
            var right = xf * new Vector3(halfW, 0, 0);
            var top = xf * new Vector3(0, halfH, 0);
            var bottom = xf * new Vector3(0, -halfH, 0);
            var eye = _cam.GlobalPosition;

            float hDeg = Mathf.RadToDeg((left - eye).Normalized().AngleTo((right - eye).Normalized()));
            float vDeg = Mathf.RadToDeg((top - eye).Normalized().AngleTo((bottom - eye).Normalized()));

            var pl = _cam.UnprojectPosition(left);
            var pr = _cam.UnprojectPosition(right);
            var pt = _cam.UnprojectPosition(top);
            var pb = _cam.UnprojectPosition(bottom);
            float wPx = Mathf.Abs(pr.X - pl.X);
            float hPx = Mathf.Abs(pb.Y - pt.Y);

            _logicalPxPerDeg = _ui.LogicalSize.X / Mathf.Max(hDeg, 0.001f);

            float capturePxPerDeg = wPx / Mathf.Max(hDeg, 0.001f);
            float dist = (xf.Origin - eye).Length();

            GD.Print($"UIVR geometry: eye at y={eye.Y:F2}, panel centre {dist:F2} m away");
            GD.Print($"UIVR geometry: panel subtends {hDeg:F1} deg x {vDeg:F1} deg " +
                     $"({wPx:F0} x {hPx:F0} capture px of {EyeShotSize.X}x{EyeShotSize.Y})");
            GD.Print($"UIVR geometry: capture {capturePxPerDeg:F1} px/deg (Quest 2 is ~20), " +
                     $"logical {_logicalPxPerDeg:F1} px/deg over {_ui.LogicalSize.X}x{_ui.LogicalSize.Y}");
            // Godot's `font_size` is the em box; cap height is roughly 0.72 of it, and ~0.5 deg of
            // cap height is the comfortable floor for reading in a headset.
            float floor = 0.5f * _logicalPxPerDeg / 0.72f;
            GD.Print($"UIVR geometry: minimum comfortable font_size = {floor:F1} px " +
                     $"(0.5 deg cap height); font_size 15 reads {15 * 0.72f / _logicalPxPerDeg:F2} deg, " +
                     $"12 reads {12 * 0.72f / _logicalPxPerDeg:F2} deg");
        }

        // ---------------------------------------------------------------- the screens

        private Hud _hud;
        private LoadingScreen _loading;
        private QuickMenu _quick;
        private MainMenu _main;
        private ActionMenu _action;
        private CameraMenu _camera;
        private AvatarSelector _avatars;
        private SettingsMenu _settings;
        private VideoQueuePanel _video;
        private ChatOverlay _chat;
        private VrKeyboard _keyboard;

        private T Mount<T>(T layer) where T : CanvasLayer
        {
            _ui.Viewport.AddChild(layer);
            return layer;
        }

        private List<Shot> BuildShots()
        {
            const string user = "SerikaTester";
            var worlds = new List<(string, string, string, int, string, string)>
            {
                ("commons", "The Commons", "The main public hangout. Always open, always busy.", 40, "Serika", ""),
                ("cinema", "Cinema", "Watch videos together on the big screen.", 16, "Serika", ""),
                ("gallery", "Mirror Gallery", "A quiet room of mirrors.", 8, "Serika", ""),
                ("lab", "Physics Lab", "Throw things at other people.", 12, "Aris", ""),
                ("dojo", "Sunset Dojo", "A rooftop at golden hour.", 24, "Hoshino", ""),
            };
            var avatars = new List<(string, string, string, string, string)>
            {
                ("a1", "Suisei", "Serika", "", ""),
                ("a2", "Shiroko", "Serika", "", ""),
                ("a3", "Aris", "Serika", "", ""),
            };

            _hud = Mount(new Hud { Name = "Hud" });
            _loading = Mount(new LoadingScreen { Name = "LoadingScreen", Visible = false });
            _quick = Mount(new QuickMenu { Name = "QuickMenu" });
            _main = Mount(new MainMenu { Name = "MainMenu" });
            _action = Mount(new ActionMenu { Name = "ActionMenu" });
            _camera = Mount(new CameraMenu { Name = "CameraMenu" });
            _avatars = Mount(new AvatarSelector { Name = "AvatarSelector" });
            _settings = Mount(new SettingsMenu { Name = "SettingsMenu" });
            _video = Mount(new VideoQueuePanel { Name = "VideoQueuePanel" });
            _chat = Mount(new ChatOverlay { Name = "ChatOverlay" });
            _keyboard = Mount(new VrKeyboard { Name = "VrKeyboard" });
            _keyboard.Attach(_ui.Viewport);

            var all = new List<Shot>
            {
                new("login", _hud, () => _hud.ShowLogin()),

                // The flow the user complained about, shot end to end: the list you pick from,
                // the detail card you land on, and the screen you stare at while it connects.
                new("worldlist", _hud, () => { _hud.SetWorlds(worlds); _hud.ShowWorldList(user); }),
                new("worldlist_empty", _hud, () =>
                {
                    _hud.SetWorlds(new List<(string, string, string, int, string, string)>());
                    _hud.ShowWorldList(user);
                }),
                new("worlddetail", _hud, () =>
                {
                    _hud.SetWorlds(worlds);
                    _hud.ShowWorldList(user);
                    _hud.ShowWorldDetail(System.Text.Json.JsonDocument.Parse(
                        """
                        {"id":"commons","name":"The Commons","description":"The main public hangout. Always open, always busy. Come and say hello.","capacity":40,"visitCount":18422,"author":"Serika","isBuiltin":true,"tags":["public","social","hangout"],
                         "instances":[{"playerCount":12,"capacity":40,"access":0,"mode":0,"region":"eu"},
                                      {"playerCount":40,"capacity":40,"access":0,"mode":0,"region":"us"}]}
                        """).RootElement);
                }),
                new("loading", _loading, () => { _loading.Present(); _loading.SetStatus("Connecting to The Commons…"); }),
                new("connecting", _hud, () => _hud.SetStatus("Signing you in…")),
                new("error", _hud, () => _hud.ShowError("Couldn't reach the server. Check your connection and try again.")),

                new("quickmenu", _quick, () =>
                {
                    _quick.SetLocation("The Commons", true);
                    _quick.SetPlayers(user, new (string, string)[] { ("u-aris", "Aris"), ("u-hoshino", "Hoshino"), ("u-nonomi", "Nonomi"), ("u-shiroko", "Shiroko") });
                    _quick.SetTrust("Trusted");
                    _quick.Open(user);
                }),
                new("mainmenu_worlds", _main, () => { _main.SetWorlds(worlds); _main.Open(user, 1); }),
                new("mainmenu_avatars", _main, () => { _main.SetAvatars(avatars); _main.Open(user, 2); }),
                new("settings", _settings, () => _settings.Open()),
                new("actionmenu", _action, () => _action.Open()),
                new("cameramenu", _camera, () => _camera.Open()),
                new("avatarselector", _avatars, () => _avatars.Open()),
                new("videoqueue", _video, () => _video.Open()),
                new("chat", _chat, () =>
                {
                    _chat.AddChat("Aris", "has anyone got the cinema working yet");
                    _chat.AddChat(user, "testing the overlay right now");
                    _chat.AddSystem("Hoshino joined");
                    _chat.OpenInput();
                }),
                // `Show()`, not `Visible = true`: the layer and the inner panel are separate flags
                // and the keyboard normally raises both from its focus poll, which will not fire
                // here because nothing on the panel holds keyboard focus in a headless-input run.
                new("keyboard", _keyboard, () => { _chat.OpenInput(); _keyboard.Show(); }),
            };

            if (string.IsNullOrEmpty(_filter)) return all;
            var wanted = new List<Shot>();
            foreach (var name in _filter.Split(',', StringSplitOptions.RemoveEmptyEntries))
                foreach (var s in all)
                    if (s.Name == name.Trim()) wanted.Add(s);
            return wanted;
        }

        /// Put every screen away. Screens are independent sibling layers on one panel, so without
        /// this each shot accumulates the previous ones behind it — and on a *transparent* panel
        /// that is much harder to notice than on a monitor.
        private void HideAll()
        {
            _hud?.HideAll();
            _loading?.Hide();
            _quick?.Hide();
            _main?.Hide();
            _action?.Hide();
            _camera?.Hide();
            _avatars?.Hide();
            _settings?.Hide();
            _video?.Hide();
            _chat?.CloseInput();
            if (_keyboard != null) _keyboard.Visible = false;
        }

        // ---------------------------------------------------------------- capture

        private void Capture(Shot shot)
        {
            RenderingServer.ForceDraw();

            var eyeImg = _eye.GetTexture()?.GetImage();
            if (eyeImg == null) { GD.Print($"UIVR FAIL: no eye image for {shot.Name}"); _failures++; return; }
            string path = $"{_out}_{shot.Name}.png";
            var err = eyeImg.SavePng(path);
            if (err != Error.Ok) { GD.Print($"UIVR FAIL: SavePng({path}) = {err}"); _failures++; return; }

            Analyse(shot, path);
        }

        /// Measure what the screen drew, on the panel's own render target.
        ///
        /// Reading the *eye* shot cannot answer this: the panel is transparent and sits in front of
        /// a lit room, so "is this a pixel of UI" is not separable from "is this a pixel of wall".
        /// The panel's texture has real alpha, so the same question is trivial there — and it is in
        /// logical coordinates, which is the space the layout code is written in.
        private void Analyse(Shot shot, string path)
        {
            var img = _ui.Viewport.GetTexture()?.GetImage();
            if (img == null) { GD.Print($"UIVR {shot.Name}: no panel texture"); return; }

            int w = img.GetWidth(), h = img.GetHeight();
            // The render target is `Resolution`; the layout happens in `LogicalSize`. Report in
            // logical px so a number here can be compared directly against the layout code.
            float sx = (float)_ui.LogicalSize.X / w, sy = (float)_ui.LogicalSize.Y / h;

            int minX = w, minY = h, maxX = -1, maxY = -1;
            long inked = 0, opaque = 0;
            const int Step = 2; // 1920x1200 read back per screen; every other pixel is plenty
            for (int y = 0; y < h; y += Step)
            for (int x = 0; x < w; x += Step)
            {
                float a = img.GetPixel(x, y).A;
                if (a <= 0.04f) continue;
                inked++;
                if (a >= 0.85f) opaque++;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }

            long sampled = (long)((w + Step - 1) / Step) * ((h + Step - 1) / Step);
            if (maxX < 0)
            {
                // Drawing nothing is only a defect if the layer still claims to be visible.
                //
                // That combination is this project's most expensive VR bug: `HasInteractiveUi`
                // walks the panel's layers and asks each one whether it is `Visible`, and it gates
                // both the panel render and the laser pointer. A layer that hides its inner card
                // but leaves its own flag set therefore pins a 2 m slab and a lit laser in front of
                // the player for the whole session, drawing nothing. It has happened four times
                // (mic indicator, InteractionPrompt, ChatOverlay, VideoQueuePanel/VrKeyboard), so
                // it gets a named check rather than an eyeball.
                bool visible = shot.Layer?.Visible ?? false;
                GD.Print(visible
                    ? $"UIVR {shot.Name}: PINNED — layer is Visible but drew nothing, so the panel " +
                      $"and the laser pointer stay on with an empty slab. -> {path}"
                    : $"UIVR {shot.Name}: blank and layer hidden — correctly declined to show. -> {path}");
                if (visible) _failures++;
                return;
            }

            float bx = minX * sx, by = minY * sy;
            float bw = (maxX - minX) * sx, bh = (maxY - minY) * sy;
            float fill = 100f * inked / sampled;
            float slab = 100f * opaque / sampled;

            // Content wider or taller than the logical size is silently clipped by the viewport —
            // on a monitor an over-wide dialog at least gets a scrollbar or spills visibly off the
            // edge, but here it is simply cut off by the panel border and looks intentional.
            bool clipped = minX <= Step || minY <= Step ||
                           maxX >= w - 1 - Step || maxY >= h - 1 - Step;

            string flags = "";
            if (clipped && fill < 92f) flags += " CLIPPED?";
            // A screen that inks essentially every pixel of the panel is not a card floating in the
            // room, it is a wall: the desktop full-bleed backdrop / full-screen scrim idiom. A
            // legitimately opaque *card* leaves the panel's margin uninked, so the threshold is on
            // total coverage rather than on opacity, which a big card also hits.
            if (fill > 97f) flags += " SLAB";
            if (fill < 22f) flags += " TINY";                 // a postage stamp on a 61 deg panel

            GD.Print($"UIVR {shot.Name}: bbox {bw:F0}x{bh:F0} at ({bx:F0},{by:F0}) of " +
                     $"{_ui.LogicalSize.X}x{_ui.LogicalSize.Y} logical px " +
                     $"= {bw / _logicalPxPerDeg:F1} x {bh / _logicalPxPerDeg:F1} deg; " +
                     $"ink {fill:F0}%, opaque {slab:F0}%{flags} -> {path}");
        }
    }
}
