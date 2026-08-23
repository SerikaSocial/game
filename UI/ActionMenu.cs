using System;
using System.Collections.Generic;
using Godot;
using SerikaSocial.Avatar;

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

    private Control _radialControl;
    private bool _active;
    private int _hoveredSlice = -1;
    private Vector2 _centerPos;

    private const float OuterRadius = 190f;
    private const float InnerRadius = 65f;
    /// Follows the active ring so submenus of a different size still line up.
    private int SliceCount => _slices.Length;

    /// Root ring. Emotes live here (and only here) — there are no emote key binds.
    private static readonly (string title, string icon)[] RootSlices =
    {
        ("Emotes", "💃"),
        ("Poses", "🧘"),
        ("Reactions", "😀"),
        ("Camera", "📷"),
        ("Respawn", "⟲"),
        ("Go Home", "🏠"),
    };

    /// Submenus, each a ring of emotes. Cancel returns the avatar to normal.
    private static readonly Dictionary<int, (string title, string icon, AvatarInstance.Emote emote)[]> SubSlices = new()
    {
        [0] = new[]
        {
            ("Dance", "💃", AvatarInstance.Emote.Dance),
            ("Charleston", "🕺", AvatarInstance.Emote.DanceCharleston),
            ("Victory", "🏆", AvatarInstance.Emote.Victory),
            ("Fist Pump", "✊", AvatarInstance.Emote.VictoryFist),
            ("Backflip", "🤸", AvatarInstance.Emote.Backflip),
            ("Stop", "✖", AvatarInstance.Emote.None),
        },
        [1] = new[]
        {
            ("Sit", "🧍", AvatarInstance.Emote.Sit),
            ("Meditate", "🧘", AvatarInstance.Emote.Meditate),
            ("Sleep", "😴", AvatarInstance.Emote.Sleeping),
            ("Bow", "🙇", AvatarInstance.Emote.Bow),
            ("Shiver", "🥶", AvatarInstance.Emote.Shivering),
            ("Stop", "✖", AvatarInstance.Emote.None),
        },
        [2] = new[]
        {
            ("Wave", "👋", AvatarInstance.Emote.Greeting),
            ("Yes", "👍", AvatarInstance.Emote.Yes),
            ("No", "👎", AvatarInstance.Emote.Reject),
            ("Confused", "❓", AvatarInstance.Emote.Confused),
            ("Dizzy", "💫", AvatarInstance.Emote.Dizzy),
            ("Stop", "✖", AvatarInstance.Emote.None),
        },
    };

    // -1 = root ring, else the index of the open submenu.
    private int _openSub = -1;

    private (string title, string icon)[] _slices = RootSlices;

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
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    private void ResetToRoot()
    {
        _openSub = -1;
        _slices = RootSlices;
        _hoveredSlice = -1;
        _radialControl?.QueueRedraw();
    }

    public new void Hide()
    {
        _active = false;
        Visible = false;
        Input.MouseMode = Input.MouseModeEnum.Captured;
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
        if (_openSub < 0) return false;
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
            if (_openSub >= 0) ResetToRoot();
            else Hide();
            return;
        }

        // Inside a submenu: the slice is an emote.
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
            case 0: case 1: case 2:   // Emotes / Poses / Reactions → open that ring
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
        var entries = SubSlices[index];
        var ring = new (string, string)[entries.Length];
        for (int i = 0; i < entries.Length; i++) ring[i] = (entries[i].title, entries[i].icon);
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
            _radialControl.DrawString(font, labelPos + new Vector2(-16, -4), sliceData.icon, HorizontalAlignment.Center, -1, 24, Colors.White);
            _radialControl.DrawString(font, labelPos + new Vector2(-40, 18), sliceData.title, HorizontalAlignment.Center, 80, 12, isHovered ? Colors.White : Brand.TextMid);
        }

        // Draw Center Circle (Close ring)
        Color centerCol = (_hoveredSlice < 0) ? Brand.PrimaryLo : Brand.Bg2;
        _radialControl.DrawCircle(center, InnerRadius, centerCol);
        _radialControl.DrawArc(center, InnerRadius, 0, Mathf.Tau, 32, Brand.Border, 2f);
        _radialControl.DrawString(ThemeDB.FallbackFont, center + new Vector2(-10, 8), "✕", HorizontalAlignment.Center, -1, 22, Brand.TextHi);
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
