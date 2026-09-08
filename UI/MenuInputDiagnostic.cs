using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using SerikaSocial.Player;

namespace SerikaSocial.UI;

/// Standalone shipping-path regression scene. Uses XRServer trackers, without a headset.
public partial class MenuInputDiagnostic : Node3D
{
    private readonly List<object> _checks = new();
    private int _failures;
    private VrSimDevice _device;
    private VrPlayer _player;
    private VrUiSurface _surface;
    private ActionMenu _menu;

    public override async void _Ready()
    {
        try { await Run(); }
        catch (Exception error) { Check("diagnostic_completed", false, error.ToString()); }
        finally
        {
            _device?.Remove();
            string output = System.Environment.GetEnvironmentVariable("SERIKA_MENU_REPORT");
            if (!string.IsNullOrEmpty(output)) System.IO.File.WriteAllText(output,
                System.Text.Json.JsonSerializer.Serialize(new { checks = _checks, failures = _failures,
                    limitation = "Simulated OpenXR trackers. Hardware bindings, stereo comfort and tracking quality are unverified." },
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            GD.Print($"MENUINPUT complete checks={_checks.Count} failures={_failures}");
            GetTree().Quit(_failures == 0 ? 0 : 1);
        }
    }

    private void Check(string name, bool pass, string detail = "")
    {
        _checks.Add(new { name, pass, detail });
        if (!pass) _failures++;
        GD.Print($"MENUINPUT {(pass ? "PASS" : "FAIL")} {name} {detail}");
    }

    private async Task Frames(int count = 4)
    {
        _device?.Commit();
        for (int i = 0; i < count; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private async Task Run()
    {
        var gate = new VrPointerPress();
        Check("held_trigger_on_open_is_ignored", gate.Step(true, true, 1) == VrPointerPress.Change.None);
        Check("held_trigger_does_not_arm", gate.Step(true, true, 1) == VrPointerPress.Change.None);
        gate.Step(true, true, 0);
        Check("fresh_press_after_release", gate.Step(true, true, 1) == VrPointerPress.Change.Press);
        Check("analogue_hysteresis", gate.Step(true, true, .5f) == VrPointerPress.Change.None && gate.Captured);
        Check("normal_release", gate.Step(true, true, 0) == VrPointerPress.Change.Release);
        gate.Step(true, false, 1);
        Check("drag_held_trigger_into_panel_does_not_click", gate.Step(true, true, 1) == VrPointerPress.Change.None);
        gate.Step(true, true, 0); gate.Step(true, true, 1);
        Check("off_panel_drag_cancels", gate.Step(true, false, 1) == VrPointerPress.Change.Cancel);
        Check("reenter_during_press_does_not_click", gate.Step(true, true, 1) == VrPointerPress.Change.None);
        gate.Step(true, true, 0); gate.Step(true, true, 1);
        Check("tracking_loss_cancels", gate.Step(false, true, 1) == VrPointerPress.Change.Cancel);
        Check("held_trigger_after_retracking_is_ignored", gate.Step(true, true, 1) == VrPointerPress.Change.None);
        gate.Step(true, true, 0); gate.Step(true, true, 1);
        Check("menu_close_cancels", gate.Step(false, false, 1) == VrPointerPress.Change.Cancel);

        VrUiSurface.Active = true;
        _surface = new VrUiSurface(); AddChild(_surface);
        _menu = new ActionMenu(); _surface.Viewport.AddChild(_menu);
        _menu.Open(); await Frames();
        _menu.SelectFromStick(new Vector2(1, 0));
        Check("wheel_requires_initial_neutral", _menu.SelectedSlice == -1);
        _menu.SelectFromStick(Vector2.Zero); _menu.SelectFromStick(new Vector2(0, 1));
        Check("stick_up_selects_first_wedge", _menu.SelectedSlice == 0);
        _menu.ConfirmSelection();
        Check("confirm_enters_emotes", _menu.PageTitle == "Emotes");
        _menu.SelectFromStick(new Vector2(1, 0));
        Check("submenu_requires_neutral", _menu.SelectedSlice == -1);
        Check("submenu_back_returns_to_root", _menu.BackOut() && _menu.PageTitle == "Actions");
        _menu.SetCustomEmotes(new[] { "Dance_Test" });
        _menu.SelectFromStick(Vector2.Zero); _menu.SelectFromStick(new Vector2(0, 1)); _menu.ConfirmSelection();
        Check("avatar_emotes_have_back", _menu.PageTitle == "Avatar emotes" && _menu.BackOut());
        _menu.SetCustomEmotes(null);
        _menu._Input(new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.Left, Position = new Vector2(-100, -100) });
        Check("click_outside_ring_does_not_close", _menu.IsOpen);
        _menu.Hide();

        _surface.Position = new Vector3(0, 1.65f, -1.7f); _surface.Panel.Visible = true;
        const float halfAngle = 7 * Mathf.Pi / 180;
        float radius = 1 / Mathf.Sin(halfAngle);
        for (int i = 0; i < 5; i++)
        {
            float u = .05f + i * .225f;
            float angle = (u * 2 - 1) * halfAngle;
            var point = _surface.ToGlobal(new Vector3(Mathf.Sin(angle) * radius, .21f, radius * (1 - Mathf.Cos(angle))));
            var origin = new Vector3(-.48f, 1.2f, -.18f);
            bool hit = _surface.RayHit(origin, (point - origin).Normalized(), out var actual)
                && _surface.WorldToViewport(actual, out var pixel) && Math.Abs(pixel.X - u * 1000) < .1f;
            Check($"curved_panel_oblique_ray_{i}", hit);
        }
        Check("panel_miss_is_not_clickable", !_surface.RayHit(new Vector3(3, 1.6f, 0), Vector3.Forward, out _));
        Check("back_of_panel_is_not_clickable", !_surface.RayHit(new Vector3(0, 1.6f, -3), Vector3.Back, out _));

        _device = new VrSimDevice(); _device.Install();
        var floor = new StaticBody3D { Position = new Vector3(0, -.5f, 0) };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(20, 1, 20) } }); AddChild(floor);
        _player = new VrPlayer { UiSurface = _surface, ActionMenu = _menu }; AddChild(_player);
        int quick = 0, action = 0, interacts = 0, respawns = 0;
        _player.MenuPressed += () => quick++;
        _player.ActionMenuPressed += () => { action++; if (_menu.IsOpen) _menu.Hide(); else _menu.Open(); _player.ControlsEnabled = !_menu.IsOpen; };
        _menu.Closed += () => _player.ControlsEnabled = true;
        _menu.RespawnPressed += () => respawns++;
        _player.InteractPressed += _ => { interacts++; return true; };
        await Frames(10);
        Check("real_controller_trackers_are_bound", _player.LeftHand.GetHasTrackingData() && _player.RightHand.GetHasTrackingData());
        var layer = new CanvasLayer();
        var button = new Button { Text = "Pointer test", Position = new Vector2(350, 220), Size = new Vector2(300, 200) };
        int clicks = 0; button.Pressed += () => clicks++;
        layer.AddChild(button); _surface.Viewport.AddChild(layer);
        _player.ControlsEnabled = false;
        _device.Hand[1] = new Transform3D(Basis.Identity, new Vector3(0, 1.65f, -.3f));
        _device.Trigger[1] = 1; await Frames(8);
        Check("visible_panel_held_trigger_does_not_press_button", !button.ButtonPressed && clicks == 0);
        _device.Trigger[1] = 0; await Frames();
        _device.Trigger[1] = 1; await Frames();
        Check("tracked_ray_can_press_real_button", button.ButtonPressed);
        _device.Hand[1] = new Transform3D(Basis.FromEuler(new Vector3(0, 1.5f, 0)), _device.Hand[1].Origin);
        await Frames(); _device.Trigger[1] = 0; await Frames();
        Check("leaving_panel_cancels_real_button", !button.ButtonPressed && clicks == 0);
        _device.Hand[1] = new Transform3D(Basis.Identity, _device.Hand[1].Origin); await Frames();
        _device.Trigger[1] = 1; await Frames(); _device.Trigger[1] = 0; await Frames();
        Check("fresh_controller_click_activates_real_button", clicks == 1, clicks.ToString());
        _device.Trigger[1] = 1; await Frames(); _device.HandTracked[1] = false; await Frames();
        Check("tracking_loss_cancels_real_button", !button.ButtonPressed && clicks == 1);
        _device.HandTracked[1] = true; await Frames();
        Check("tracking_return_held_trigger_does_not_press_button", !button.ButtonPressed && clicks == 1);
        _device.Trigger[1] = 0; layer.Hide(); _player.ControlsEnabled = true; await Frames();
        _device.SecondaryButton[1] = true; await Frames(5); _device.SecondaryButton[1] = false; await Frames();
        Check("by_tap_opens_quick_only", quick == 1 && action == 0);
        _device.SecondaryButton[1] = true; await Frames(27); _device.SecondaryButton[1] = false; await Frames();
        Check("by_hold_opens_action_once", action == 1 && quick == 1 && _menu.IsOpen);
        _device.Stick[1] = new Vector2(-.866f, -.5f); await Frames();
        Check("controller_stick_selects_respawn", _menu.SelectedSlice == 4, _menu.SelectedSlice.ToString());
        Vector3 before = _player.Position; float yaw = _player.Rotation.Y;
        _device.PrimaryButton[1] = true; await Frames();
        Check("controller_a_confirms_selection", respawns == 1 && !_menu.IsOpen);
        Check("held_menu_stick_does_not_turn_after_close", Math.Abs(_player.Rotation.Y - yaw) < .001f);
        _device.PrimaryButton[1] = false; _device.Stick[1] = Vector2.Zero; await Frames();
        _device.Stick[1] = new Vector2(1, 0); await Frames();
        Check("turn_resumes_after_neutral", Math.Abs(_player.Rotation.Y - yaw) > .1f);
        _device.ClearInputs(); await Frames();
        action = quick = 0; _device.MenuButton[0] = _device.MenuButton[1] = true; await Frames(68);
        _device.MenuButton[0] = _device.MenuButton[1] = false; await Frames();
        Check("recenter_chord_does_not_open_menus", action == 0 && quick == 0);
        _player.ControlsEnabled = false; _device.Trigger[1] = 1; await Frames();
        _player.ControlsEnabled = true; await Frames();
        Check("menu_trigger_does_not_leak_into_world", interacts == 0);
        _device.Trigger[1] = 0; await Frames(); _device.Trigger[1] = 1; await Frames();
        Check("world_trigger_resumes_after_release", interacts == 1);
        _device.ClearInputs(); _device.HandTracked[1] = false; _player.ControlsEnabled = false; _menu.Open(); await Frames();
        Check("left_controller_fallback_has_pointer", _player.PointerVisible);
        _menu.Hide(); await Frames(8);
        Check("closed_menu_hides_pointer", !_player.PointerVisible);
    }
}
