using System;
using System.Collections.Generic;
using Godot;
using SerikaSocial.Avatar;

using SerikaSocial.UI;

namespace SerikaSocial;

/// Radial action menu (`R`), modelled on VRChat's. A root ring of six wedges leads into
/// emote submenus; the centre steps back out of a submenu, or closes from the root.
///
/// This is the only way to trigger an emote — emotes deliberately have no key binds.
public partial class ActionMenu : CanvasLayer
{
    public event Action Closed;
    public event Action CameraPressed;
    public event Action HomePressed;
    public event Action RespawnPressed;
    public event Action<AvatarInstance.Emote> EmotePressed;
    /// A custom, avatar-authored clip was picked (empty string = stop / return to normal).
    public event Action<string> CustomEmotePressed;

    // The avatar's own dance/emote clips, shown in place of the default "Emotes" ring when the
    // equipped avatar ships any. Null/empty → the built-in ring is used (the fallback).
    private (string title, Icons.Kind icon, string clip)[] _customRing;
    private bool _inCustomSub;

    /// Feed the equipped avatar's custom clip names. Trimmed to fit the ring; a "Stop" wedge is
    /// always appended so a custom dance can be cancelled from the same menu.
    public void SetCustomEmotes(System.Collections.Generic.IReadOnlyList<string> names)
    {
        if (names == null || names.Count == 0) { _customRing = null; return; }

        int n = Math.Min(names.Count, 7);
        _customRing = new (string, Icons.Kind, string)[n + 1];
        for (int i = 0; i < n; i++)
            _customRing[i] = (PrettyClip(names[i]), Icons.Kind.Music, names[i]);
        _customRing[n] = ("Stop", Icons.Kind.Close, "");
    }

    /// Turn a raw clip name ("Dance_HipHop", "emote.wave 01") into a menu-friendly label.
    private static string PrettyClip(string raw)
    {
        string s = raw.Replace('_', ' ').Replace('.', ' ').Trim();
        if (s.Length > 14) s = s[..14];
        return s.Length == 0 ? "Clip" : char.ToUpper(s[0]) + s[1..];
    }

    private Control _radialControl;
    private bool _active;
    private int _hoveredSlice = -1;
    private Vector2 _centerPos;

    /// Larger on the VR panel.
    ///
    /// The wheel is drawn in logical pixels, so its angular size is fixed by these two numbers: at
    /// 190 px against `VrUiSurface.LogicalSize` of 1000 across a 60.9° panel it subtends 23°, on a
    /// surface with 61° to spend, and every wedge is a *ray* target aimed from 1.7 m away. 260 px
    /// takes it to 32° and each of the six wedges to roughly 16° of arc, without the ring reaching
    /// the panel edge where the curvature starts to skew the hit test.
    private static float OuterRadius => VrUiSurface.Active ? 260f : 190f;
    private static float InnerRadius => VrUiSurface.Active ? 88f : 65f;
    /// Follows the active ring so submenus of a different size still line up.
    private int SliceCount => _slices.Length;

    /// Root ring. Emotes live here (and only here) — there are no emote key binds.
    private static readonly (string title, Icons.Kind icon)[] RootSlices =
    {
        ("Emotes", Icons.Kind.Music),
        ("Poses", Icons.Kind.Sit),
        ("Reactions", Icons.Kind.Smile),
        ("Camera", Icons.Kind.Camera),
        ("Respawn", Icons.Kind.Refresh),
        ("Go Home", Icons.Kind.Home),
    };

    /// Submenus, each a ring of emotes. Cancel returns the avatar to normal.
    private static readonly Dictionary<int, (string title, Icons.Kind icon, AvatarInstance.Emote emote)[]> SubSlices = new()
    {
        [0] = new[]
        {
            ("Dance", Icons.Kind.Music, AvatarInstance.Emote.Dance),
            ("Charleston", Icons.Kind.Person, AvatarInstance.Emote.DanceCharleston),
            ("Victory", Icons.Kind.Trophy, AvatarInstance.Emote.Victory),
            ("Fist Pump", Icons.Kind.Star, AvatarInstance.Emote.VictoryFist),
            ("Backflip", Icons.Kind.Bolt, AvatarInstance.Emote.Backflip),
            ("Stop", Icons.Kind.Close, AvatarInstance.Emote.None),
        },
        [1] = new[]
        {
            ("Sit", Icons.Kind.Sit, AvatarInstance.Emote.Sit),
            ("Meditate", Icons.Kind.Focus, AvatarInstance.Emote.Meditate),
            ("Sleep", Icons.Kind.Moon, AvatarInstance.Emote.Sleeping),
            ("Bow", Icons.Kind.Person, AvatarInstance.Emote.Bow),
            ("Shiver", Icons.Kind.Snowflake, AvatarInstance.Emote.Shivering),
            ("Stop", Icons.Kind.Close, AvatarInstance.Emote.None),
        },
        [2] = new[]
        {
            ("Wave", Icons.Kind.Wave, AvatarInstance.Emote.Greeting),
            ("Yes", Icons.Kind.ThumbUp, AvatarInstance.Emote.Yes),
            ("No", Icons.Kind.ThumbDown, AvatarInstance.Emote.Reject),
            ("Confused", Icons.Kind.Question, AvatarInstance.Emote.Confused),
            ("Dizzy", Icons.Kind.Star, AvatarInstance.Emote.Dizzy),
            ("Stop", Icons.Kind.Close, AvatarInstance.Emote.None),
        },
    };

    // -1 = root ring, else the index of the open submenu.
    private int _openSub = -1;

    private (string title, Icons.Kind icon)[] _slices = RootSlices;

    public override void _Ready()
    {
        Layer = 98;
        Visible = false;

        // Must not capture the mouse: a Control defaults to MouseFilter.Stop, which consumes
        // clicks as GUI input so they never reach _UnhandledInput — that left every wedge
        // unselectable. The ring is drawn here but hit-testing is done geometrically.
        _radialControl = new Control
        {
            AnchorRight = 1,
            AnchorBottom = 1,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _radialControl.Draw += DrawRadialMenu;
        AddChild(_radialControl);
    }

    public void Open()
    {
        _active = true;
        Visible = true;
        ResetToRoot();
    }

    private void ResetToRoot()
    {
        _openSub = -1;
        _inCustomSub = false;
        _slices = RootSlices;
        _hoveredSlice = -1;
        _radialControl?.QueueRedraw();
    }

    public new void Hide()
    {
        _active = false;
        Visible = false;
        Closed?.Invoke();
    }

    public bool IsOpen => Visible;

    public override void _Process(double delta)
    {
        if (!IsOpen) return;
        Vector2 mousePos = GetViewport().GetMousePosition();
        UpdateHoverSlice(mousePos);
    }

    // Handled in _Input rather than _UnhandledInput so nothing higher in the UI can eat the
    // click or the Escape (which is the engine's `ui_cancel` action) while the ring is open.
    public override void _Input(InputEvent @event)
    {
        if (!IsOpen) return;

        if (@event is InputEventMouseMotion mm)
        {
            UpdateHoverSlice(mm.Position);
            return;
        }

        if (@event is InputEventMouseButton { Pressed: true } mb)
        {
            if (mb.ButtonIndex == MouseButton.Left)
            {
                SelectSlice(mb.Position);
                GetViewport().SetInputAsHandled();
            }
            else if (mb.ButtonIndex == MouseButton.Right)
            {
                // Right-click steps back out of a submenu, or closes.
                if (_openSub >= 0) ResetToRoot(); else Hide();
                GetViewport().SetInputAsHandled();
            }
            return;
        }

        if (@event is not InputEventKey { Pressed: true, Echo: false } k) return;

        // Number keys pick a wedge; Enter/Space confirms whatever is hovered.
        if (k.Keycode >= Key.Key1 && k.Keycode <= Key.Key9)
        {
            int idx = (int)(k.Keycode - Key.Key1);
            if (idx < SliceCount) { _hoveredSlice = idx; Activate(); }
            GetViewport().SetInputAsHandled();
            return;
        }

        // Escape/R are deliberately NOT handled here — Main owns them, so the ring can't be
        // closed here and immediately reopened by Main's toggle in the same event.
        if (k.Keycode is Key.Enter or Key.KpEnter or Key.Space)
        {
            Activate();
            GetViewport().SetInputAsHandled();
        }
    }

    /// Step back out of a submenu. Returns false if already at the root (caller should close).
    public bool BackOut()
    {
        if (_openSub < 0 && !_inCustomSub) return false;
        ResetToRoot();
        return true;
    }

    private void UpdateHoverSlice(Vector2 mousePos)
    {
        Vector2 center = GetViewport().GetVisibleRect().Size * 0.5f;
        Vector2 delta = mousePos - center;
        float dist = delta.Length();

        int oldHover = _hoveredSlice;
        if (dist < InnerRadius || dist > OuterRadius + 30f)
        {
            _hoveredSlice = -1; // Center ring or outside
        }
        else
        {
            // Must use the exact same slice bounds as DrawRadialMenu(), otherwise the
            // highlighted/selected slice doesn't line up with the wedge under the cursor.
            float sliceAngle = Mathf.Tau / SliceCount;
            float startAngleOffset = -Mathf.Pi / 2f - sliceAngle / 2f;
            float rel = Mathf.Atan2(delta.Y, delta.X) - startAngleOffset;
            rel = Mathf.PosMod(rel, Mathf.Tau);
            _hoveredSlice = Mathf.FloorToInt(rel / sliceAngle) % SliceCount;
        }

        if (oldHover != _hoveredSlice)
            _radialControl.QueueRedraw();
    }

    private void SelectSlice(Vector2 clickPos)
    {
        UpdateHoverSlice(clickPos);
        Activate();
    }

    /// Act on whatever wedge is currently hovered (or the centre, if none).
    private void Activate()
    {
        if (_hoveredSlice < 0)
        {
            // Centre: step back out of a submenu, or close from the root ring.
            if (_openSub >= 0 || _inCustomSub) ResetToRoot();
            else Hide();
            return;
        }

        // Inside the avatar's custom-clip ring: emit the clip name (empty = stop).
        if (_inCustomSub)
        {
            if (_hoveredSlice < _customRing.Length)
            {
                var picked = _customRing[_hoveredSlice];
                Hide();
                CustomEmotePressed?.Invoke(picked.clip);
            }
            return;
        }

        // Inside a built-in submenu: the slice is an emote.
        if (_openSub >= 0)
        {
            var entries = SubSlices[_openSub];
            if (_hoveredSlice < entries.Length)
            {
                var picked = entries[_hoveredSlice];
                Hide();
                EmotePressed?.Invoke(picked.emote);
            }
            return;
        }

        switch (_hoveredSlice)
        {
            case 0:   // Emotes → the avatar's own dances if it has any, else the default ring
                if (_customRing != null) OpenCustomSub();
                else OpenSub(0);
                break;
            case 1: case 2:   // Poses / Reactions → that built-in ring
                OpenSub(_hoveredSlice);
                break;
            case 3:
                Hide();
                CameraPressed?.Invoke();
                break;
            case 4:
                Hide();
                RespawnPressed?.Invoke();
                break;
            case 5:
                Hide();
                HomePressed?.Invoke();
                break;
        }
    }

    private void OpenSub(int index)
    {
        _openSub = index;
        _inCustomSub = false;
        var entries = SubSlices[index];
        var ring = new (string, Icons.Kind)[entries.Length];
        for (int i = 0; i < entries.Length; i++) ring[i] = (entries[i].title, entries[i].icon);
        _slices = ring;
        _hoveredSlice = -1;
        _radialControl.QueueRedraw();
    }

    /// Open the equipped avatar's own dance/emote ring.
    private void OpenCustomSub()
    {
        _inCustomSub = true;
        _openSub = -1;
        var ring = new (string, Icons.Kind)[_customRing.Length];
        for (int i = 0; i < _customRing.Length; i++) ring[i] = (_customRing[i].title, _customRing[i].icon);
        _slices = ring;
        _hoveredSlice = -1;
        _radialControl.QueueRedraw();
    }

    private void DrawRadialMenu()
    {
        if (!Visible) return;

        Vector2 center = _radialControl.Size * 0.5f;
        _centerPos = center;

        // Dark backdrop glow circle
        _radialControl.DrawCircle(center, OuterRadius + 10f, new Color(0.05f, 0.08f, 0.12f, 0.85f));

        float sliceAngle = Mathf.Tau / SliceCount;
        float startAngleOffset = -Mathf.Pi / 2f - sliceAngle / 2f;

        // Draw 6 radial slices
        for (int i = 0; i < SliceCount; i++)
        {
            float a1 = startAngleOffset + i * sliceAngle;
            float a2 = a1 + sliceAngle;

            bool isHovered = (i == _hoveredSlice);
            Color fillCol = isHovered ? Brand.Primary : new Color(0.12f, 0.16f, 0.24f, 0.9f);
            Color borderCol = isHovered ? Brand.Accent : Brand.Border;

            DrawArcSegment(center, InnerRadius, OuterRadius, a1, a2, fillCol, borderCol);

            // Draw Icon & Title in slice center
            float midAngle = (a1 + a2) * 0.5f;
            float iconRadius = (InnerRadius + OuterRadius) * 0.5f;
            Vector2 labelPos = center + new Vector2(Mathf.Cos(midAngle), Mathf.Sin(midAngle)) * iconRadius;

            var sliceData = _slices[i];
            var font = ThemeDB.FallbackFont;
            const int iconPx = 26;
            var iconTex = Icons.Get(sliceData.icon, iconPx, isHovered ? Colors.White : Brand.AccentSoft);
            _radialControl.DrawTexture(iconTex, labelPos + new Vector2(-iconPx / 2f, -iconPx - 2));
            _radialControl.DrawString(font, labelPos + new Vector2(-40, 16), sliceData.title, HorizontalAlignment.Center, 80, 12, isHovered ? Colors.White : Brand.TextMid);
        }

        // Draw Center Circle (Close ring)
        Color centerCol = (_hoveredSlice < 0) ? Brand.PrimaryLo : Brand.Bg2;
        _radialControl.DrawCircle(center, InnerRadius, centerCol);
        _radialControl.DrawArc(center, InnerRadius, 0, Mathf.Tau, 32, Brand.Border, 2f);
        const int closePx = 20;
        _radialControl.DrawTexture(Icons.Get(Icons.Kind.Close, closePx, Brand.TextHi),
                                   center - new Vector2(closePx / 2f, closePx / 2f));
    }

    private void DrawArcSegment(Vector2 center, float innerR, float outerR, float a1, float a2, Color fill, Color border)
    {
        int points = 16;
        var verts = new Vector2[points * 2 + 2];

        for (int i = 0; i <= points; i++)
        {
            float t = (float)i / points;
            float a = Mathf.Lerp(a1, a2, t);
            verts[i] = center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * outerR;
            verts[points * 2 + 1 - i] = center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * innerR;
        }

        _radialControl.DrawColoredPolygon(verts, fill);

        // Border outline
        for (int i = 0; i < verts.Length - 1; i++)
            _radialControl.DrawLine(verts[i], verts[i + 1], border, 1.5f);
        _radialControl.DrawLine(verts[^1], verts[0], border, 1.5f);
    }
}
