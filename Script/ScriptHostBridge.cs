using System;
using System.Collections.Generic;
using Godot;

namespace Serika.Script;

/// The Godot implementation of the sandbox's only bridge to the engine.
///
/// IHostBridge documents the scope rules; THIS is where they are enforced. Every node the script
/// can touch comes from a table built by the world loader from `SERIKA_SNODE*` markers, and a slot
/// outside that table is a no-op — never an engine lookup, never a path, never a `ResourceLoader`.
/// If you add a method here, the question to answer first is "what is the smallest thing this can
/// reach", not "what would be convenient".
///
/// Nothing in this class may throw. It is called from inside ScriptVm.Dispatch, which is called
/// from the frame loop; per CLAUDE.md an unhandled exception there is a logged error on desktop
/// and a *dead process* on Android. Malformed input gets a benign return value.
public sealed class ScriptHostBridge : IHostBridge
{
    /// Ceiling on simultaneous attachments, so a script cannot bury the instance in props.
    private const int MaxAttachments = 8;

    /// LOG is developer output, not a channel. Uncapped it is a log-spam DoS (and CLAUDE.md notes
    /// file logging is now always on, so it hits the disk).
    private const int MaxLogsPerSecond = 10;

    private readonly IReadOnlyList<Node3D> _nodes;
    private readonly IReadOnlyList<AudioStream> _clips;
    private readonly IScriptPlayers _players;
    private readonly Node3D _worldRoot;
    private readonly string _label;

    /// Where each attached node came from, so detach can put it back exactly.
    private readonly Dictionary<int, Node> _attachedFrom = new();

    private readonly ulong _startMsec;
    private ulong _rngState;
    private int _logsThisSecond;
    private ulong _logWindowStart;

    public ScriptHostBridge(
        string label,
        Node3D worldRoot,
        IReadOnlyList<Node3D> nodes,
        IReadOnlyList<AudioStream> clips,
        IScriptPlayers players,
        ulong seed = 0x9E3779B97F4A7C15)
    {
        _label = label;
        _worldRoot = worldRoot;
        _nodes = nodes ?? Array.Empty<Node3D>();
        _clips = clips ?? Array.Empty<AudioStream>();
        _players = players;
        _startMsec = Time.GetTicksMsec();
        _rngState = seed;
    }

    /// Resolve a declared node slot. Out of range, freed, or removed from the tree all answer
    /// null — a script holding a slot whose node the world removed must not resurrect it.
    private Node3D NodeAt(int slot)
    {
        if (slot < 0 || slot >= _nodes.Count) return null;
        var n = _nodes[slot];
        return GodotObject.IsInstanceValid(n) && n.IsInsideTree() ? n : null;
    }

    // ── util ──

    public void Log(string message)
    {
        ulong now = Time.GetTicksMsec();
        if (now - _logWindowStart >= 1000) { _logWindowStart = now; _logsThisSecond = 0; }
        if (_logsThisSecond++ >= MaxLogsPerSecond) return;
        GD.Print($"[script:{_label}] {message}");
    }

    public double Time_() => (Time.GetTicksMsec() - _startMsec) / 1000.0;
    double IHostBridge.Time() => Time_();

    /// Deterministic PRNG (SplitMix64). Deterministic is a requirement, not a nicety: two clients
    /// running the same script must agree, and Godot's global RNG is shared mutable state the
    /// script would otherwise be perturbing for everyone else.
    public double Random()
    {
        _rngState += 0x9E3779B97F4A7C15UL;
        ulong z = _rngState;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z >> 11) * (1.0 / 9007199254740992.0); // 53 bits -> [0,1)
    }

    // ── own nodes ──

    public void NodeMove(int slot, float x, float y, float z)
    {
        var n = NodeAt(slot);
        if (n == null) return;
        if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z)) return;
        n.Position = new Vector3(x, y, z);
    }

    public void NodeRotate(int slot, float x, float y, float z)
    {
        var n = NodeAt(slot);
        if (n == null) return;
        if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z)) return;
        n.Rotation = new Vector3(Mathf.DegToRad(x), Mathf.DegToRad(y), Mathf.DegToRad(z));
    }

    public void NodeSetVisible(int slot, bool visible)
    {
        var n = NodeAt(slot);
        if (n != null) n.Visible = visible;
    }

    public void NodePlayAnim(int slot, int animId)
    {
        var n = NodeAt(slot);
        if (n == null) return;
        // Only an AnimationPlayer the script's own node owns, and only by index — a name would
        // let a script reach animations it was never granted.
        if (n.GetNodeOrNull<AnimationPlayer>("AnimationPlayer") is not { } ap) return;
        var list = ap.GetAnimationList();
        if (animId < 0 || animId >= list.Length) return;
        ap.Play(list[animId]);
    }

    // ── media ──

    public void SoundPlay(int clipSlot)
    {
        if (clipSlot < 0 || clipSlot >= _clips.Count) return;
        var p = new AudioStreamPlayer { Stream = _clips[clipSlot], Autoplay = false };
        _worldRoot.AddChild(p);
        p.Finished += p.QueueFree;
        p.Play();
    }

    public void SoundPlaySpatial(int clipSlot, float x, float y, float z)
    {
        if (clipSlot < 0 || clipSlot >= _clips.Count) return;
        if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z)) return;
        var p = new AudioStreamPlayer3D { Stream = _clips[clipSlot], Position = new Vector3(x, y, z) };
        _worldRoot.AddChild(p);
        p.Finished += p.QueueFree;
        p.Play();
    }

    public void ScreenSetText(int screenSlot, string text)
    {
        var n = NodeAt(screenSlot);
        if (n?.GetNodeOrNull<Label3D>("Label") is { } label) label.Text = text;
    }

    /// Boards are how a dev-made minigame shows state (timers, scores, votes). Rendered as the
    /// board's Label3D text with a stable format so integers read as integers, not "3.0000003".
    public void ScreenSetNumber(int screenSlot, double value)
    {
        var n = NodeAt(screenSlot);
        if (n?.GetNodeOrNull<Label3D>("Label") is not { } label) return;
        label.Text = Math.Abs(value % 1) < 1e-9
            ? ((long)Math.Round(value)).ToString()
            : value.ToString("0.0");
    }

    // ── players (read-only) ──

    public int PlayerCount() => _players?.Count ?? 0;

    public (float X, float Y, float Z) PlayerPos(int playerIndex)
    {
        if (_players != null && _players.TryGetPosition(playerIndex, out var p)) return (p.X, p.Y, p.Z);
        return (0, 0, 0);
    }

    /// Parent one of the script's own nodes to a player bone.
    ///
    /// One-directional by construction: this reparents OUR node under THEIR skeleton. There is no
    /// path from here to a player's velocity or transform, and there must never be one — see §5.1.
    public bool PlayerAttach(int playerIndex, int nodeSlot, int attachPoint)
    {
        var node = NodeAt(nodeSlot);
        if (node == null || _players == null) return false;
        if (!Enum.IsDefined(typeof(ScriptAttachPoint), attachPoint)) return false;
        if (!_attachedFrom.ContainsKey(nodeSlot) && _attachedFrom.Count >= MaxAttachments) return false;

        var target = _players.GetAttachTarget(playerIndex, (ScriptAttachPoint)attachPoint);
        if (target == null) return false;

        var origin = node.GetParent();
        if (origin == null) return false;

        // Remember the FIRST parent only. Re-attaching an already-attached node must still detach
        // back to the world, not to whichever bone it happened to be on last.
        if (!_attachedFrom.ContainsKey(nodeSlot)) _attachedFrom[nodeSlot] = origin;

        node.Reparent(target, keepGlobalTransform: false);
        node.Transform = Transform3D.Identity;
        return true;
    }

    public bool PlayerDetach(int nodeSlot)
    {
        if (!_attachedFrom.TryGetValue(nodeSlot, out var origin)) return false;
        _attachedFrom.Remove(nodeSlot);

        var node = NodeAt(nodeSlot);
        if (node == null) return false;
        if (!GodotObject.IsInstanceValid(origin) || !origin.IsInsideTree()) origin = _worldRoot;

        node.Reparent(origin, keepGlobalTransform: true);
        return true;
    }

    /// Put every attachment back. Called when the script is unloaded or hard-killed, so a dead
    /// script does not leave props welded to people.
    public void DetachAll()
    {
        foreach (int slot in new List<int>(_attachedFrom.Keys)) PlayerDetach(slot);
    }

    // ── networking ──
    //
    // NET_EMIT has no relay message yet: the wire has no script channel, and adding one is a
    // proto change under the project's proto-first rule. Until then this is observable locally
    // (so single-player logic and the diagnostics work) and goes no further. It deliberately does
    // NOT fall back to some other channel — quietly reusing chat or object-sync would be exactly
    // the "new socket" the design forbids.

    /// Raised on NET_EMIT so the host can surface it. Not a network path.
    public event Action<int, double> Emitted;

    public void NetEmit(int channel, double payload) => Emitted?.Invoke(channel, payload);

    public void NetEmitString(int channel, string payload) { }
    public double NetSyncGet(string key) => 0;
    public void NetSyncSet(string key, double value) { }

    // ── shaders / particles (declared nodes only) ──

    public void ShaderSetFloat(int slot, string uniformName, float value)
    {
        if (MaterialAt(slot) is { } m && IsFinite(value)) m.SetShaderParameter(uniformName, value);
    }

    public void ShaderSetColor(int slot, string uniformName, float r, float g, float b, float a)
    {
        if (MaterialAt(slot) is { } m) m.SetShaderParameter(uniformName, new Color(r, g, b, a));
    }

    public void ShaderSetVec4(int slot, string uniformName, float x, float y, float z, float w)
    {
        if (MaterialAt(slot) is { } m) m.SetShaderParameter(uniformName, new Vector4(x, y, z, w));
    }

    private ShaderMaterial MaterialAt(int slot) =>
        NodeAt(slot) is MeshInstance3D mi ? mi.MaterialOverride as ShaderMaterial : null;

    public void ParticleBurst(int slot)
    {
        if (NodeAt(slot) is GpuParticles3D p) { p.Emitting = false; p.Restart(); p.Emitting = true; }
    }

    public void ParticleSetRate(int slot, float rate)
    {
        if (NodeAt(slot) is GpuParticles3D p && IsFinite(rate))
            p.Amount = Mathf.Clamp((int)rate, 1, 4096);
    }

    /// A NaN or infinity written into a transform propagates through physics and rendering and is
    /// very hard to trace back to its source. Reject at the boundary.
    private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
}
