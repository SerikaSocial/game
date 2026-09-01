using Godot;

namespace SerikaSocial.Player;

/// Headless keybinding diagnostic.
///
///   Godot --headless --path game -- --serika-keytest
///
/// A rebind system is easy to write and easy to get subtly wrong in ways that only show up on a
/// player's machine: a key that reads back correctly but was never pushed into Godot's `InputMap`,
/// a conflict resolution that leaves two actions on one key, a save/load round trip that quietly
/// drops to defaults, or an InputMap rewrite that wipes the gamepad events sitting alongside the
/// keyboard one. All of those are arithmetic, not judgement, so they belong in a test.
public static class KeyBindDiagnostic
{
    public static void Run(Node host)
    {
        int fails = 0;
        void Check(string name, bool ok, string extra = "")
        {
            GD.Print($"KEYTEST {(ok ? "OK  " : "FAIL")} {name}{(extra.Length > 0 ? " — " + extra : "")}");
            if (!ok) fails++;
        }

        UI.KeyBindings.ResetAll();

        // Defaults land in Godot's InputMap, not just in our own table. This is the check that
        // catches "the setting saved but the game still uses the old key".
        Check("defaults applied to InputMap",
            InputMap.HasAction("move_forward") && HasKey("move_forward", Key.W),
            $"move_forward has W = {HasKey("move_forward", Key.W)}");

        // Rebind an InputMap-backed action end to end.
        UI.KeyBindings.Rebind("move_forward", Key.Up);
        Check("rebind reaches InputMap",
            HasKey("move_forward", Key.Up) && !HasKey("move_forward", Key.W),
            $"Up={HasKey("move_forward", Key.Up)} W={HasKey("move_forward", Key.W)}");

        // A direct (non-InputMap) key is readable through KeyFor.
        UI.KeyBindings.Rebind("chat", Key.Y);
        Check("direct key rebinds", UI.KeyBindings.KeyFor("chat") == Key.Y,
            UI.KeyBindings.Name(UI.KeyBindings.KeyFor("chat")));

        // Conflict: taking a key must remove it from whoever held it, leaving exactly one owner.
        UI.KeyBindings.Rebind("interact", Key.Y);
        int owners = 0;
        foreach (var b in UI.KeyBindings.All) if (b.Current == Key.Y) owners++;
        Check("conflict steals the key", owners == 1 && UI.KeyBindings.KeyFor("interact") == Key.Y,
            $"{owners} binding(s) hold Y; chat is now {UI.KeyBindings.Name(UI.KeyBindings.KeyFor("chat"))}");

        // Locked bindings are immovable — Escape must always close a menu.
        var escBefore = UI.KeyBindings.KeyFor("pause");
        UI.KeyBindings.Rebind("pause", Key.Key9);
        Check("locked binding refuses rebind", UI.KeyBindings.KeyFor("pause") == escBefore,
            UI.KeyBindings.Name(UI.KeyBindings.KeyFor("pause")));

        // Save/load round trip through the real ConfigFile path.
        var cfg = new ConfigFile();
        UI.KeyBindings.Rebind("mic_toggle", Key.F8);
        UI.KeyBindings.Save(cfg);
        UI.KeyBindings.ResetAll();
        Check("reset restores defaults", UI.KeyBindings.KeyFor("mic_toggle") == Key.V,
            UI.KeyBindings.Name(UI.KeyBindings.KeyFor("mic_toggle")));
        UI.KeyBindings.Load(cfg);
        Check("load restores saved key", UI.KeyBindings.KeyFor("mic_toggle") == Key.F8,
            UI.KeyBindings.Name(UI.KeyBindings.KeyFor("mic_toggle")));
        Check("load re-applies to InputMap", HasKey("move_forward", Key.Up),
            "move_forward should still be Up after a load");

        // Rebinding a keyboard key must not disturb non-keyboard events on the same action.
        InputMap.ActionAddEvent("move_left", new InputEventJoypadMotion { Axis = JoyAxis.LeftX, AxisValue = -1f });
        UI.KeyBindings.Rebind("move_left", Key.J);
        bool joypadKept = false;
        foreach (var ev in InputMap.ActionGetEvents("move_left"))
            if (ev is InputEventJoypadMotion) joypadKept = true;
        Check("rebind preserves gamepad events", joypadKept && HasKey("move_left", Key.J),
            $"joypad kept={joypadKept}");

        UI.KeyBindings.ResetAll();
        GD.Print(fails == 0 ? "KEYTEST PASS" : $"KEYTEST FAIL — {fails} check(s) failed");
        host.GetTree().Quit(fails == 0 ? 0 : 1);
    }

    private static bool HasKey(string action, Key k)
    {
        if (!InputMap.HasAction(action)) return false;
        foreach (var ev in InputMap.ActionGetEvents(action))
            if (ev is InputEventKey ek && (ek.PhysicalKeycode == k || ek.Keycode == k)) return true;
        return false;
    }
}
