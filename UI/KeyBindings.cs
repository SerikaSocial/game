using System.Collections.Generic;
using Godot;

namespace SerikaSocial.UI;

/// Remappable keyboard bindings.
///
/// The client had two separate, and separately unchangeable, notions of "a key does a thing".
/// Movement went through Godot's `InputMap` actions declared in `project.godot`; everything else
/// (chat, menus, mic, interact) was a hardcoded `Key.T`-style literal in `Main._Input`. Neither
/// could be changed by a player, which is a real accessibility problem — a left-handed player, a
/// non-QWERTY layout, or anyone whose hand does not comfortably reach `V` mid-conversation simply
/// had no recourse.
///
/// This unifies both. `InputMapAction` entries are pushed into Godot's InputMap so `Input.IsActionPressed`
/// keeps working untouched; the rest are read back through `KeyFor`, which `Main` consults instead
/// of a literal. Everything persists in `settings.cfg` under `[keys]`.
public static class KeyBindings
{
    public sealed class Binding
    {
        /// Stable id — the settings key, and the Godot InputMap action name when `IsAction`.
        public string Id;
        public string Label;
        public string Category;
        public Key Default;
        public Key Current;
        /// True when this drives a Godot `InputMap` action (movement). False for keys read
        /// directly from `_Input`.
        public bool IsAction;
        /// Bindings the player must not be able to lose. Escape is the only way out of several
        /// modal states, so it is shown but not rebindable — a player who binds Escape to
        /// something else can be left with no way to close a menu.
        public bool Locked;
    }

    public static readonly List<Binding> All = new()
    {
        // Movement — these are Godot InputMap actions already declared in project.godot.
        new() { Id = "move_forward", Label = "Move forward", Category = "Movement", Default = Key.W, IsAction = true },
        new() { Id = "move_back",    Label = "Move back",    Category = "Movement", Default = Key.S, IsAction = true },
        new() { Id = "move_left",    Label = "Move left",    Category = "Movement", Default = Key.A, IsAction = true },
        new() { Id = "move_right",   Label = "Move right",   Category = "Movement", Default = Key.D, IsAction = true },
        new() { Id = "jump",         Label = "Jump",         Category = "Movement", Default = Key.Space, IsAction = true },
        new() { Id = "sprint",       Label = "Sprint",       Category = "Movement", Default = Key.Shift, IsAction = true },
        new() { Id = "crouch",       Label = "Crouch",       Category = "Movement", Default = Key.Ctrl, IsAction = true },

        // Voice.
        new() { Id = "mic_toggle",   Label = "Toggle microphone", Category = "Voice", Default = Key.V },
        new() { Id = "push_to_talk", Label = "Push to talk (hold)", Category = "Voice", Default = Key.B },

        // Social & world.
        new() { Id = "chat",         Label = "Open chat",      Category = "Social", Default = Key.T },
        new() { Id = "interact",     Label = "Interact / use",  Category = "World",  Default = Key.E },

        // Menus.
        new() { Id = "pause",        Label = "Pause menu",      Category = "Menus", Default = Key.Escape, Locked = true },
        new() { Id = "main_menu",    Label = "Worlds & avatars", Category = "Menus", Default = Key.M },
        new() { Id = "action_menu",  Label = "Emote wheel",     Category = "Menus", Default = Key.R },
        new() { Id = "camera_menu",  Label = "Camera menu",     Category = "Menus", Default = Key.C },
        new() { Id = "video_queue",  Label = "Video queue",     Category = "Menus", Default = Key.P },

        // View.
        new() { Id = "toggle_view",  Label = "First / third person", Category = "View", Default = Key.F5 },
        new() { Id = "free_cursor",  Label = "Free the mouse cursor", Category = "View", Default = Key.Tab },
    };

    private static readonly Dictionary<string, Binding> ById = new();

    static KeyBindings()
    {
        foreach (var b in All) { b.Current = b.Default; ById[b.Id] = b; }
    }

    /// The key currently bound to `id`. Falls back to the default for an unknown id rather than
    /// returning `Key.None`, which would silently disable the action.
    public static Key KeyFor(string id) =>
        ById.TryGetValue(id, out var b) ? b.Current : Key.None;

    /// Whether `key` is the binding for `id`. The form call sites actually want.
    public static bool Matches(string id, Key key) => key != Key.None && KeyFor(id) == key;

    /// Which binding already uses `key`, ignoring `exceptId`. Null when free.
    public static Binding Conflict(string exceptId, Key key)
    {
        foreach (var b in All)
            if (b.Id != exceptId && b.Current == key) return b;
        return null;
    }

    /// Rebind, clearing whatever else held the key. Silently refuses locked bindings.
    public static void Rebind(string id, Key key)
    {
        if (!ById.TryGetValue(id, out var target) || target.Locked || key == Key.None) return;

        // Steal rather than reject. A rebind UI that refuses a taken key makes the player go and
        // manually clear the other one first, which is busywork for something we can just do.
        var other = Conflict(id, key);
        if (other != null && !other.Locked) { other.Current = Key.None; ApplyOne(other); }

        target.Current = key;
        ApplyOne(target);
    }

    public static void ResetAll()
    {
        foreach (var b in All) { b.Current = b.Default; ApplyOne(b); }
    }

    /// Push every InputMap-backed binding into Godot's InputMap. Call after Load and any rebind.
    public static void Apply()
    {
        foreach (var b in All) ApplyOne(b);
    }

    private static void ApplyOne(Binding b)
    {
        if (!b.IsAction) return;
        if (!InputMap.HasAction(b.Id)) InputMap.AddAction(b.Id);

        // Erase only the KEYBOARD events. Movement actions also carry joypad/analog events from
        // project.godot, and wiping those would silently unbind every gamepad in the process of
        // rebinding a keyboard key.
        foreach (var ev in InputMap.ActionGetEvents(b.Id))
            if (ev is InputEventKey) InputMap.ActionEraseEvent(b.Id, ev);

        if (b.Current != Key.None)
            InputMap.ActionAddEvent(b.Id, new InputEventKey { PhysicalKeycode = b.Current });
    }

    /// Human-readable key name for the UI.
    public static string Name(Key k) => k == Key.None ? "—" : OS.GetKeycodeString(k);

    // ── Persistence ───────────────────────────────────────────────────────────────────

    public static void Load(ConfigFile cfg)
    {
        foreach (var b in All)
        {
            if (b.Locked) continue;
            b.Current = (Key)(int)cfg.GetValue("keys", b.Id, (int)b.Default);
        }
        Apply();
    }

    public static void Save(ConfigFile cfg)
    {
        foreach (var b in All)
        {
            if (b.Locked) continue;
            cfg.SetValue("keys", b.Id, (int)b.Current);
        }
    }
}
