using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SerikaSocial.UI;

namespace SerikaSocial;

/// Screenshots every 2D screen in the client.
///
///   Godot --path game -- --serika-uishot --out /tmp/ui [--screens login,quick,settings]
///
/// (No `--headless` — headless has no rendering server and every capture comes back blank.)
///
/// Every screen in this client is built in code rather than as a `.tscn`, which means there is
/// no editor canvas to look at: the only way to see what a screen actually looks like is to run
/// the game and get to it. That makes small styling regressions cheap to introduce and
/// expensive to notice. This drives the real screens — the real `Brand` theme, the real fonts,
/// the real layout code — and writes one PNG each, so a change can be diffed rather than
/// described.
///
/// It deliberately captures the *root* viewport rather than a private one, because the root is
/// what the player sees, including anything that overlaps or steals focus.
public partial class Main
{
    /// Set from `_Ready` before session restore and the update check run, both of which would
    /// otherwise race the screenshots and swap the screen mid-capture.
    private bool _uiShotMode;

    /// A screen: how to show it, and how to put it away again.
    private sealed record UiScreen(string Name, Action Show);

    private List<UiScreen> BuildUiScreens()
    {
        // Stand-in content. The screens are being photographed for layout and styling, so they
        // need *something* plausible in them — an empty world list and a nameless player make
        // a screen look broken in ways it isn't.
        const string user = "SerikaTester";
        var worlds = new List<(string, string, string, int, string, string)>
        {
            ("commons", "The Commons", "The main public hangout. Always open.", 40, "Serika", ""),
            ("cinema",  "Cinema",      "Watch videos together on the big screen.", 16, "Serika", ""),
            ("gallery", "Mirror Gallery", "A quiet room of mirrors.",             8,  "Serika", ""),
        };
        var avatars = new List<(string, string, string, string, string)>
        {
            ("a1", "Suisei",  "Serika", "", ""),
            ("a2", "Shiroko", "Serika", "", ""),
        };

        return new List<UiScreen>
        {
            new("login",     () => _hud.ShowLogin()),
            new("worldlist", () => _hud.ShowWorldList(user)),
            new("loading",   () => { _loading.Present(); _loading.SetStatus("Connecting to The Commons…"); }),
            new("quickmenu", () =>
            {
                _quickMenu.SetLocation("The Commons", true);
                _quickMenu.SetPlayers(user, new (string, string)[] { ("u-aris", "Aris"), ("u-hoshino", "Hoshino"), ("u-nonomi", "Nonomi") });
                _quickMenu.SetTrust("Trusted");
                _quickMenu.Open(user);
            }),
            // The same menu standing inside a live event: the join banner gives its slot up to
            // the in-venue options, which is the whole point and is invisible in `quickmenu`.
            new("quickmenu_event", () =>
            {
                _quickMenu.SetLocation("Comet Hall", true);
                _quickMenu.SetPlayers(user, new (string, string)[] { ("u-aris", "Aris"), ("u-hoshino", "Hoshino") });
                _quickMenu.SetTrust("Trusted");
                _quickMenu.SetInEvent(true);
                _quickMenu.EventOptions.SetState(false, true, false);
                _quickMenu.Open(user);
            }),
            new("mainmenu_worlds",  () => { _mainMenu.SetWorlds(worlds); _mainMenu.Open(user, 1); }),
            new("mainmenu_avatars", () => { _mainMenu.SetAvatars(avatars); _mainMenu.Open(user, 2); }),
            new("settings",  () => _settingsMenu.Open()),
            new("actionmenu",() => _actionMenu.Open()),
            new("cameramenu",() => _cameraMenu.Open()),
            new("avatars",   () => _avatarSelector.Open()),
            // Not a screen the player ever sees — a contact sheet of the icon set, so the
            // icons can be eyeballed as a group instead of hunting them across five menus.
            new("iconsheet", ShowIconSheet),
            new("chat",      () =>
            {
                _chat.AddChat("Aris", "has anyone got the cinema working yet");
                _chat.AddChat(user, "testing the overlay right now");
                _chat.OpenInput();
            }),
        };
    }

    /// Puts every screen away so the next capture starts from a clean frame. Screens are
    /// independent siblings on their own layers — without this, each shot accumulates the
    /// previous ones behind it.
    private CanvasLayer _iconSheet;

    private void ShowIconSheet()
    {
        _iconSheet = new CanvasLayer { Layer = 90 };
        // Same rule as every other screen: in VR it belongs on the panel, not the flat root.
        if (_vrShotSurface != null) _vrShotSurface.Viewport.AddChild(_iconSheet);
        else AddChild(_iconSheet);

        var bg = new ColorRect { Color = Brand.Bg0 };
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _iconSheet.AddChild(bg);

        // A CenterContainer rather than anchor maths: the grid's size isn't known until it
        // has laid its children out, so centring it by hand lands it off-screen.
        var centre = new CenterContainer();
        centre.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _iconSheet.AddChild(centre);

        var grid = new GridContainer { Columns = 9 };
        grid.AddThemeConstantOverride("h_separation", 26);
        grid.AddThemeConstantOverride("v_separation", 20);
        centre.AddChild(grid);

        foreach (Icons.Kind k in Enum.GetValues<Icons.Kind>())
        {
            var cell = new VBoxContainer();
            cell.AddThemeConstantOverride("separation", 6);

            // Drawn large here so stroke weight and shape are legible; the menus use ~20 px.
            cell.AddChild(new TextureRect
            {
                Texture = Icons.Get(k, 48, Brand.TextHi),
                CustomMinimumSize = new Vector2(48, 48),
                StretchMode = TextureRect.StretchModeEnum.KeepCentered,
            });

            var name = new Label { Text = k.ToString(), HorizontalAlignment = HorizontalAlignment.Center };
            name.AddThemeFontSizeOverride("font_size", 11);
            name.AddThemeColorOverride("font_color", Brand.TextDim);
            cell.AddChild(name);
            grid.AddChild(cell);
        }
    }

    private void ResetUiForShot()
    {
        if (_iconSheet != null) { _iconSheet.QueueFree(); _iconSheet = null; }
        _hud?.HideAll();
        _loading?.Hide();
        _quickMenu?.Hide();
        _mainMenu?.Hide();
        _settingsMenu?.Hide();
        _actionMenu?.Hide();
        _cameraMenu?.Hide();
        _avatarSelector?.Hide();
        _videoQueuePanel?.Hide();
        _chat?.CloseInput();
    }

    /// Where the VR shots are captured from, and how wide a slice of the world they cover.
    ///
    /// The point of the VR mode is to answer "is this readable in the headset", and that is a
    /// question about *angular* size, not pixels — so the capture has to reproduce the player's
    /// field of view, not just the panel's texture. A Quest's binocular FOV is roughly 96°
    /// horizontal; at 16:10 that is a ~70° vertical. Capturing that at 1200 px tall gives about
    /// 17 pixels per degree, close to a Quest 2's ~20 ppd, so text that is illegible in these
    /// shots is illegible in the headset.
    private const float EyeFovDegrees = 70f;
    private static readonly Vector2I EyeShotSize = new(1920, 1200);

    private SubViewport _vrEyeViewport;
    private VrUiSurface _vrShotSurface;

    /// Rebuild the UI onto a real `VrUiSurface` and point a camera at it from eye height.
    ///
    /// This is the same class VR actually uses, mounted the same way `AddUi` mounts it, so what
    /// the shots show is the real panel: the real curvature, the real backing resolution, the
    /// real angular size at the real 1.7 m distance.
    private void SetUpVrShots()
    {
        _vrEyeViewport = new SubViewport
        {
            Name = "VrEyeViewport",
            Size = EyeShotSize,
            OwnWorld3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        AddChild(_vrEyeViewport);

        var world = new Node3D { Name = "VrShotWorld" };
        _vrEyeViewport.AddChild(world);

        // A dim backdrop: the panel is transparent chrome, so on pure black the contrast is
        // flattering in a way the headset never is.
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.16f, 0.15f, 0.21f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.5f, 0.5f, 0.6f),
            AmbientLightEnergy = 0.6f,
        };
        world.AddChild(new WorldEnvironment { Environment = env });

        _vrShotSurface = new VrUiSurface { Name = "VrUi" };
        world.AddChild(_vrShotSurface);

        // Move every screen off the flat root and onto the panel. `AddUi` does this at startup
        // when `_vrMode` is set; here the layers already exist, so they are reparented.
        foreach (var child in GetChildren())
        {
            if (child is CanvasLayer layer)
            {
                RemoveChild(layer);
                _vrShotSurface.Viewport.AddChild(layer);
            }
        }

        var cam = new Camera3D { Name = "EyeCam", Fov = EyeFovDegrees, Current = true };
        world.AddChild(cam);
        cam.Position = new Vector3(0, 1.2f, 0);   // the panel parks at y=1.2, 1.7 m ahead

        GD.Print($"UISHOT vr: panel {_vrShotSurface.Resolution.X}x{_vrShotSurface.Resolution.Y}, " +
                 $"{_vrShotSurface.Viewport.GetChildCount()} layer(s) mounted, " +
                 $"tier={DeviceProfile.Current}");
    }

    private void StartUiShots(string outPrefix, string screens, bool vr)
    {
        outPrefix ??= "user://ui";

        if (DisplayServer.GetName() == "headless")
        {
            GD.Print("UISHOT FAIL: running headless — nothing to capture. " +
                     "Drop --headless (use xvfb-run if there is no display).");
            GetTree().Quit(1);
            return;
        }

        if (vr) SetUpVrShots();

        var all = BuildUiScreens();
        var wanted = string.IsNullOrEmpty(screens)
            ? all
            : screens.Split(',', StringSplitOptions.RemoveEmptyEntries)
                     .Select(s => s.Trim())
                     .Select(s => all.FirstOrDefault(x => x.Name == s))
                     .Where(x => x != null)
                     .ToList();

        if (wanted.Count == 0)
        {
            GD.Print($"UISHOT FAIL: no screens matched '{screens}'. " +
                     $"Known: {string.Join(", ", all.Select(a => a.Name))}");
            GetTree().Quit(1);
            return;
        }

        GD.Print($"UISHOT capturing {wanted.Count} screen(s) → {outPrefix}_*.png");
        AddChild(new UiShotTaker(this, wanted, outPrefix));
    }

    /// Shows one screen per two frames — one to apply it, one to let it render before the
    /// read-back — then writes the root viewport out.
    private sealed partial class UiShotTaker : Node
    {
        /// Screens fade and animate in. Capturing on the frame after `Open()` catches them
        /// half-transparent, so give each one a moment to settle. VR needs longer: the panel
        /// lazily re-anchors and its viewport is only re-enabled once a layer becomes visible.
        private const int SettleFrames = 12;
        private const int VrSettleFrames = 30;

        private readonly Main _main;
        private readonly List<UiScreen> _screens;
        private readonly string _prefix;
        private int _index = -1;
        private int _wait;

        public UiShotTaker(Main main, List<UiScreen> screens, string prefix)
        {
            _main = main;
            _screens = screens;
            _prefix = prefix;
        }

        public override void _Process(double delta)
        {
            if (_wait > 0) { _wait--; return; }

            // Capture whatever the previous iteration set up, then advance.
            if (_index >= 0) Capture(_screens[_index].Name);

            _index++;
            if (_index >= _screens.Count)
            {
                GD.Print("UISHOT: done");
                GetTree().Quit(0);
                return;
            }

            _main.ResetUiForShot();
            try { _screens[_index].Show(); }
            catch (Exception e) { GD.Print($"UISHOT FAIL {_screens[_index].Name}: {e.Message}"); }
            _wait = _main._vrEyeViewport != null ? VrSettleFrames : SettleFrames;
        }

        private void Capture(string name)
        {
            string path = $"{_prefix}_{name}.png";

            // In VR mode the root viewport holds nothing — the screens live on the panel — so
            // the shot has to come from the eye camera looking at it.
            var img = _main._vrEyeViewport != null
                ? _main._vrEyeViewport.GetTexture()?.GetImage()
                : GetViewport().GetTexture()?.GetImage();
            if (img == null) { GD.Print($"UISHOT FAIL: no image for {name}"); return; }

            var err = img.SavePng(path);
            if (err != Error.Ok) { GD.Print($"UISHOT FAIL: SavePng({path}) = {err}"); return; }
            GD.Print($"UISHOT wrote {path} ({img.GetWidth()}x{img.GetHeight()})");
        }
    }
}
