using System;
using System.Collections.Generic;
using Godot;
using SerikaSocial.Player;

namespace SerikaSocial;

/// On-screen controls for the mobile (non-Quest) Android build: a dynamic left-thumb joystick for
/// movement, right-side drag to look, and Jump / View / Menu / Chat / Mic / Action buttons.
/// Multi-touch is tracked by finger index so moving and looking work simultaneously.
/// Only shown when a touchscreen is present.
///
/// Input is read globally via _Input (screen touch/drag events) and pushed into the LocalPlayer's
/// ExternalMove / AddLook / ExternalJump; visuals are drawn in _Draw.
public partial class TouchControls : Control
{
    private const float JoyRadius = 110f;
    private const float ButtonRadius = 70f;
    private const float SmallRadius = 46f;

    private LocalPlayer _player;
    private Action _onViewToggle;
    private Action _onMenuToggle;
    private Action _onChatToggle;
    private Action _onMicToggle;
    private Action _onActionToggle;

    private int _moveFinger = -1, _lookFinger = -1;
    private readonly HashSet<int> _buttonFingers = new();
    private Vector2 _moveCenter, _moveKnob;

    public void Configure(LocalPlayer player, Action onViewToggle,
        Action onMenuToggle = null, Action onChatToggle = null,
        Action onMicToggle = null, Action onActionToggle = null)
    {
        _player = player;
        _onViewToggle = onViewToggle;
        _onMenuToggle = onMenuToggle;
        _onChatToggle = onChatToggle;
        _onMicToggle = onMicToggle;
        _onActionToggle = onActionToggle;
    }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore; // touches come through _Input, don't block the world
    }

    private Vector2 Size2 => GetViewportRect().Size;
    // Right-side action buttons, stacked from bottom-right upward.
    private Vector2 JumpCenter => new(Size2.X - 130, Size2.Y - 130);
    private Vector2 ViewCenter => new(Size2.X - 130, Size2.Y - 250);
    private Vector2 MicCenter => new(Size2.X - 130, Size2.Y - 360);
    // Left-side system buttons, stacked from top-left downward.
    private Vector2 MenuCenter => new(70, 70);
    private Vector2 ChatCenter => new(170, 70);
    private Vector2 ActionCenter => new(270, 70);

    public override void _Input(InputEvent e)
    {
        if (_player == null) return;

        if (e is InputEventScreenTouch t)
        {
            if (t.Pressed) OnPress(t.Index, t.Position);
            else OnRelease(t.Index);
        }
        else if (e is InputEventScreenDrag d)
        {
            if (d.Index == _moveFinger) UpdateMove(d.Position);
            else if (d.Index == _lookFinger) _player.AddLook(d.Relative);
        }
    }

    private void OnPress(int index, Vector2 pos)
    {
        // Right-side action buttons
        if (pos.DistanceTo(JumpCenter) <= ButtonRadius)
        {
            _player.ExternalJump = true;
            _buttonFingers.Add(index);
        }
        else if (pos.DistanceTo(ViewCenter) <= SmallRadius)
        {
            _onViewToggle?.Invoke();
            _buttonFingers.Add(index);
        }
        else if (pos.DistanceTo(MicCenter) <= SmallRadius)
        {
            _onMicToggle?.Invoke();
            _buttonFingers.Add(index);
        }
        // Left-side system buttons
        else if (pos.DistanceTo(MenuCenter) <= SmallRadius)
        {
            _onMenuToggle?.Invoke();
            _buttonFingers.Add(index);
        }
        else if (pos.DistanceTo(ChatCenter) <= SmallRadius)
        {
            _onChatToggle?.Invoke();
            _buttonFingers.Add(index);
        }
        else if (pos.DistanceTo(ActionCenter) <= SmallRadius)
        {
            _onActionToggle?.Invoke();
            _buttonFingers.Add(index);
        }
        // Left half = movement joystick, right half = look drag
        else if (pos.X < Size2.X * 0.5f && _moveFinger < 0)
        {
            _moveFinger = index;
            _moveCenter = pos;
            _moveKnob = pos;
            QueueRedraw();
        }
        else if (_lookFinger < 0)
        {
            _lookFinger = index;
        }
    }

    private void OnRelease(int index)
    {
        if (index == _moveFinger)
        {
            _moveFinger = -1;
            _player.ExternalMove = Vector2.Zero;
            QueueRedraw();
        }
        else if (index == _lookFinger)
        {
            _lookFinger = -1;
        }
        _buttonFingers.Remove(index);
    }

    private void UpdateMove(Vector2 pos)
    {
        var offset = pos - _moveCenter;
        if (offset.Length() > JoyRadius) offset = offset.Normalized() * JoyRadius;
        _moveKnob = _moveCenter + offset;
        // Screen-up (negative Y) is forward (W = input.Y negative); right is strafe-right.
        _player.ExternalMove = offset / JoyRadius;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var ring = new Color(1, 1, 1, 0.22f);
        var fill = new Color(1, 1, 1, 0.32f);
        var font = ThemeDB.FallbackFont;

        if (_moveFinger >= 0)
        {
            DrawCircle(_moveCenter, JoyRadius, new Color(1, 1, 1, 0.10f));
            DrawArc(_moveCenter, JoyRadius, 0, Mathf.Tau, 48, ring, 3, true);
            DrawCircle(_moveKnob, 42, fill);
        }

        // Right-side action buttons
        DrawCircle(JumpCenter, ButtonRadius, new Color(0.35f, 0.6f, 1f, 0.28f));
        DrawArc(JumpCenter, ButtonRadius, 0, Mathf.Tau, 40, ring, 3, true);
        DrawString(font, JumpCenter + new Vector2(-26, 6), "Jump", HorizontalAlignment.Left, -1, 22, new Color(1, 1, 1, 0.85f));

        DrawCircle(ViewCenter, SmallRadius, new Color(1f, 1f, 1f, 0.18f));
        DrawArc(ViewCenter, SmallRadius, 0, Mathf.Tau, 32, ring, 2, true);
        DrawString(font, ViewCenter + new Vector2(-20, 6), "View", HorizontalAlignment.Left, -1, 18, new Color(1, 1, 1, 0.8f));

        DrawCircle(MicCenter, SmallRadius, new Color(1f, 0.5f, 0.5f, 0.18f));
        DrawArc(MicCenter, SmallRadius, 0, Mathf.Tau, 32, ring, 2, true);
        DrawString(font, MicCenter + new Vector2(-20, 6), "Mic", HorizontalAlignment.Left, -1, 18, new Color(1, 1, 1, 0.8f));

        // Left-side system buttons
        DrawCircle(MenuCenter, SmallRadius, new Color(0.6f, 0.4f, 1f, 0.22f));
        DrawArc(MenuCenter, SmallRadius, 0, Mathf.Tau, 32, ring, 2, true);
        DrawString(font, MenuCenter + new Vector2(-22, 6), "Menu", HorizontalAlignment.Left, -1, 18, new Color(1, 1, 1, 0.8f));

        DrawCircle(ChatCenter, SmallRadius, new Color(0.4f, 0.7f, 1f, 0.18f));
        DrawArc(ChatCenter, SmallRadius, 0, Mathf.Tau, 32, ring, 2, true);
        DrawString(font, ChatCenter + new Vector2(-20, 6), "Chat", HorizontalAlignment.Left, -1, 18, new Color(1, 1, 1, 0.8f));

        DrawCircle(ActionCenter, SmallRadius, new Color(1f, 0.7f, 0.3f, 0.18f));
        DrawArc(ActionCenter, SmallRadius, 0, Mathf.Tau, 32, ring, 2, true);
        DrawString(font, ActionCenter + new Vector2(-24, 6), "Action", HorizontalAlignment.Left, -1, 16, new Color(1, 1, 1, 0.8f));
    }
}
