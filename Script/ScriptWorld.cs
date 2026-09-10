using System;
using System.Collections.Generic;
using Godot;

namespace Serika.Script;

/// Runs a world's scripts. One of these is attached under the world root when a `.serikaworld`
/// carries a `script.sskb`; worlds without one never construct it and pay nothing.
///
/// Everything here is built around one rule from CLAUDE.md: a script must never be able to take
/// the client down. A runaway or malformed script is hard-killed and REMOVED, and the world keeps
/// running. That means every dispatch site is wrapped — `_Process` runs inside the engine's frame
/// loop, and an unhandled exception there is a logged error on desktop and a dead process on
/// Android.
public partial class ScriptWorld : Node
{
    /// One loaded script and everything it owns.
    private sealed class Entry
    {
        public string Label;
        public ScriptModule Module;
        public ScriptVm Vm;
        public ScriptHostBridge Bridge;
        public bool Dead;
    }

    private readonly List<Entry> _scripts = new();
    private IScriptPlayers _players;
    private Node3D _worldRoot;

    /// Surfaced NET_EMIT calls: (label, channel, payload). The relay has no script channel yet, so
    /// this is where emitted values become observable — see ScriptHostBridge.NetEmit.
    public event Action<string, int, double> Emitted;

    public int LiveScriptCount
    {
        get
        {
            int n = 0;
            foreach (var s in _scripts) if (!s.Dead) n++;
            return n;
        }
    }

    public static ScriptWorld Create(Node3D worldRoot, IScriptPlayers players)
    {
        var sw = new ScriptWorld { Name = "ScriptWorld", _worldRoot = worldRoot, _players = players };
        return sw;
    }

    /// Load one module. Returns false and logs on rejection — a bad script is a world that loads
    /// without behaviour, never a world that fails to load.
    public bool AddModule(string label, byte[] bytecode, int authorRank,
                          IReadOnlyList<Node3D> nodes, IReadOnlyList<AudioStream> clips)
    {
        try
        {
            var module = ScriptModule.Load(bytecode, authorRank);
            var bridge = new ScriptHostBridge(label, _worldRoot, nodes, clips, _players);
            bridge.Emitted += (ch, payload) => Emitted?.Invoke(label, ch, payload);

            _scripts.Add(new Entry
            {
                Label = label,
                Module = module,
                Vm = new ScriptVm(module, bridge),
                Bridge = bridge,
            });
            GD.Print($"ScriptWorld: loaded '{label}' — {module.Code.Length} B, hooks [{string.Join(",", module.Hooks)}]");
            return true;
        }
        catch (ScriptValidationException e)
        {
            GD.PrintErr($"ScriptWorld: rejected '{label}': {e.Message}");
            return false;
        }
    }

    /// Wire a zone's signals into the hooks. Call once per zone after loading modules.
    public void BindZone(ScriptZone zone)
    {
        zone.Entered += (id, body) =>
        {
            int player = _players?.IndexOfBody(body) ?? -1;
            if (player >= 0) Dispatch(HookId.OnEnterZone, player, id);
        };
        zone.Exited += (id, body) =>
        {
            int player = _players?.IndexOfBody(body) ?? -1;
            if (player >= 0) Dispatch(HookId.OnExitZone, player, id);
        };
    }

    /// Fire on_ready, once, after every module is loaded and every zone bound.
    ///
    /// This is NOT `_Ready()`. `_Ready` runs the instant the node enters the tree, which is before
    /// the caller has had a chance to call AddModule — so on_ready silently never ran for any
    /// script. It also has to come after BindZone, or a script that reacts to a zone during
    /// startup would miss events it should have seen.
    public void Start()
    {
        if (_started) return;
        _started = true;
        Dispatch(HookId.OnReady);
    }

    private bool _started;

    public override void _Process(double delta)
    {
        // Never tick before on_ready — a script's tick almost always assumes its variables were
        // initialised there.
        if (!_started || LiveScriptCount == 0) return;
        Dispatch(HookId.OnTick, delta);
    }

    /// Fire on_interact for a player — call this from an interaction prompt.
    public void Interact(int playerIndex) => Dispatch(HookId.OnInteract, playerIndex);

    /// Fire on_interact for whichever player a physics body belongs to. This is how a world
    /// button (`SERIKA_BUTTON<n>`, resolved as an InteractionPoint) reaches the script: the
    /// loader binds the button's Interacted signal here, and the body resolves to the local
    /// machine's roster index for that player. Out-of-tree or unknown bodies are a no-op.
    public void InteractFromBody(Node3D body)
    {
        int player = _players?.IndexOfBody(body) ?? -1;
        if (player >= 0) Dispatch(HookId.OnInteract, player);
    }

    /// Tell the script WHICH button was pressed, as a LOCAL on_message at channel 1000+slot with
    /// the presser's roster index as payload. The shared on_interact hook carries only who —
    /// a script with six task consoles and a kill button needs to know which was used. This is
    /// local-only (it never touches the wire), and scripts that emit their own channels simply
    /// avoid 1000+ to stay clear of it.
    public void InteractButton(int markerSlot, Node3D body)
    {
        if (markerSlot < 0) return;
        int player = _players?.IndexOfBody(body) ?? -1;
        if (player >= 0) Dispatch(HookId.OnMessage, 1000 + markerSlot, player);
    }

    /// Deliver a message hook.
    public void Message(int nameId, double payload) => Dispatch(HookId.OnMessage, nameId, payload);

    /// Run one hook across every live script.
    ///
    /// A script that breaches its budget, its stack or the watchdog is killed HERE and marked
    /// dead: the exception never leaves this method, the other scripts still run, and the frame
    /// completes. This is the hard-kill the design promises (T4).
    private void Dispatch(HookId hook, params double[] args)
    {
        for (int i = 0; i < _scripts.Count; i++)
        {
            var s = _scripts[i];
            if (s.Dead || !s.Module.HasHook(hook)) continue;

            try
            {
                s.Vm.RunHook(hook, args);
            }
            catch (ScriptKilledException e)
            {
                Kill(s, $"killed on {hook}: {e.Message}");
            }
            catch (Exception e)
            {
                // A bug in the bridge, not the script. Still must not reach the frame loop, but
                // it deserves a louder, differently-worded report — silently treating it as a
                // script fault would hide engine-side defects.
                Kill(s, $"host fault on {hook}: {e.GetType().Name}: {e.Message}");
            }
        }
    }

    private void Kill(Entry s, string reason)
    {
        s.Dead = true;
        // A dead script must not leave props welded to players.
        try { s.Bridge.DetachAll(); } catch (Exception e) { GD.PrintErr($"ScriptWorld: detach failed: {e.Message}"); }
        GD.PrintErr($"ScriptWorld: '{s.Label}' {reason}");
    }

    public override void _ExitTree()
    {
        foreach (var s in _scripts)
        {
            try { s.Bridge.DetachAll(); } catch (Exception) { /* tearing down anyway */ }
        }
        _scripts.Clear();
    }
}
