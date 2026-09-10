using System;
using System.Diagnostics;

namespace Serika.Script;

/// Raised when a script exceeds its instruction budget, stack limits, or the wall-clock watchdog.
/// The caller (the world's script host) catches this, removes the offending script, and keeps the
/// instance running — a runaway script never takes down the frame.
public sealed class ScriptKilledException : Exception
{
    public ScriptKilledException(string reason) : base(reason) { }
}

/// A hand-written stack VM for validated SerikaScript bytecode. Deliberately no Godot types and no
/// allocation in the hot loop — it reaches the engine only through IHostBridge. One VM instance
/// holds one script's isolated state (stack + locals); nothing is shared between scripts.
public sealed class ScriptVm
{
    private const int MaxStack = 256;
    private const int MaxLocals = 256;
    private const int MaxVars = 256;
    private const int WatchdogMs = 8; // per invocation wall-clock ceiling

    private readonly ScriptModule _module;
    private readonly IHostBridge _host;
    private readonly double[] _stack = new double[MaxStack];
    private readonly double[] _locals = new double[MaxLocals];

    /// The script-local variable store behind VAR_GET/VAR_SET. This is deliberately a SEPARATE
    /// array from _locals: VarGet/VarSet used to index _locals, so a script holding a local in
    /// slot N and a var in slot N silently clobbered one with the other. §4 of the design
    /// specifies locals and the variable store as distinct state. Keep them distinct.
    private readonly double[] _vars = new double[MaxVars];
    private int _sp;

    public ScriptVm(ScriptModule module, IHostBridge host)
    {
        _module = module;
        _host = host;
    }

    private void Push(double v)
    {
        if (_sp >= MaxStack) throw new ScriptKilledException("stack overflow");
        _stack[_sp++] = v;
    }

    private double Pop()
    {
        if (_sp <= 0) throw new ScriptKilledException("stack underflow");
        return _stack[--_sp];
    }

    /// Dispatch one event hook. Returns false when the module does not define it — an absent hook
    /// is normal, not an error. Arguments are placed in locals 0..n-1 for the hook to LOAD.
    ///
    /// This is the ONLY way to enter a script. There is deliberately no "run the whole module"
    /// entry point: execution is event-driven per §4, and each hook gets its own fresh budget and
    /// watchdog so a slow on_tick cannot starve on_interact.
    public bool RunHook(HookId hook, ReadOnlySpan<double> args)
    {
        int entry = _module.EntryPoint(hook);
        if (entry < 0) return false;

        int arity = HookArity.Of(hook);
        if (args.Length < arity)
            throw new ScriptKilledException($"hook 0x{(byte)hook:x} needs {arity} args, got {args.Length}");

        // Locals do NOT persist across hooks — only the variable store does. Two invocations must
        // not be able to read each other's leftovers.
        Array.Clear(_locals, 0, _locals.Length);
        for (int i = 0; i < arity; i++) _locals[i] = args[i];

        Execute(entry);
        return true;
    }

    /// Convenience for zero-argument hooks.
    public bool RunHook(HookId hook) => RunHook(hook, ReadOnlySpan<double>.Empty);

    /// Execute from a code offset until Ret/Halt or the end of code. Enforces the per-invocation
    /// instruction budget and an 8 ms watchdog. Throws ScriptKilledException on any breach.
    private void Execute(int entry)
    {
        _sp = 0;
        byte[] code = _module.Code;
        int budget = _module.BudgetTick;
        int pc = entry;
        var sw = Stopwatch.StartNew();

        while (pc < code.Length)
        {
            if (--budget < 0) throw new ScriptKilledException("instruction budget exceeded");
            if ((budget & 0x3FF) == 0 && sw.ElapsedMilliseconds > WatchdogMs)
                throw new ScriptKilledException("watchdog: wall-clock exceeded");

            var op = (OpCode)code[pc++];
            switch (op)
            {
                case OpCode.Nop: break;
                case OpCode.PushI: Push(ReadI32(code, ref pc)); break;
                case OpCode.PushF: Push(ReadF32(code, ref pc)); break;
                case OpCode.PushNil: Push(0); break;
                case OpCode.Pop: Pop(); break;
                case OpCode.Dup: { double v = Pop(); Push(v); Push(v); break; }
                case OpCode.Load: { int s = code[pc++]; Push(_locals[s]); break; }
                case OpCode.Store: { int s = code[pc++]; _locals[s] = Pop(); break; }

                case OpCode.Add: { double b = Pop(), a = Pop(); Push(a + b); break; }
                case OpCode.Sub: { double b = Pop(), a = Pop(); Push(a - b); break; }
                case OpCode.Mul: { double b = Pop(), a = Pop(); Push(a * b); break; }
                case OpCode.Div: { double b = Pop(), a = Pop(); Push(b == 0 ? 0 : a / b); break; }
                case OpCode.Mod: { double b = Pop(), a = Pop(); Push(b == 0 ? 0 : a % b); break; }
                case OpCode.Neg: Push(-Pop()); break;

                case OpCode.Eq: { double b = Pop(), a = Pop(); Push(a == b ? 1 : 0); break; }
                case OpCode.Ne: { double b = Pop(), a = Pop(); Push(a != b ? 1 : 0); break; }
                case OpCode.Lt: { double b = Pop(), a = Pop(); Push(a < b ? 1 : 0); break; }
                case OpCode.Le: { double b = Pop(), a = Pop(); Push(a <= b ? 1 : 0); break; }
                case OpCode.Gt: { double b = Pop(), a = Pop(); Push(a > b ? 1 : 0); break; }
                case OpCode.Ge: { double b = Pop(), a = Pop(); Push(a >= b ? 1 : 0); break; }
                case OpCode.And: { double b = Pop(), a = Pop(); Push((a != 0 && b != 0) ? 1 : 0); break; }
                case OpCode.Or: { double b = Pop(), a = Pop(); Push((a != 0 || b != 0) ? 1 : 0); break; }
                case OpCode.Not: Push(Pop() == 0 ? 1 : 0); break;

                case OpCode.Jmp: { short rel = ReadI16(code, ref pc); pc += rel; break; }
                case OpCode.JmpIf: { short rel = ReadI16(code, ref pc); if (Pop() != 0) pc += rel; break; }
                case OpCode.JmpIfNot: { short rel = ReadI16(code, ref pc); if (Pop() == 0) pc += rel; break; }

                case OpCode.HostCall: Dispatch(code, ref pc); break;
                case OpCode.Ret: case OpCode.Halt: return;

                default: throw new ScriptKilledException($"unhandled opcode 0x{(byte)op:x}");
            }
            if (pc < 0 || pc > code.Length) throw new ScriptKilledException("pc out of range");
        }
    }

    // Dispatch an allowlisted host call. The module has already proven the id is declared and on
    // the allowlist; scope enforcement (declared nodes only) lives in the IHostBridge impl.
    private void Dispatch(byte[] code, ref int pc)
    {
        ushort id = (ushort)(code[pc] | (code[pc + 1] << 8)); pc += 2;
        int argc = code[pc++];
        if (argc > 8) throw new ScriptKilledException("too many host-call args");
        Span<double> args = stackalloc double[8];
        for (int i = argc - 1; i >= 0; i--) args[i] = Pop();

        // NOTE: arity is `argc`, never `args.Length` — args is a fixed 8-slot buffer, so
        // args.Length is always 8 and any guard written against it is unconditionally true.
        switch ((HostCall)id)
        {
            // LOG takes a string-table index, not a number. It stringified args[0] before the
            // container had a string table, which meant a script could not log a message — only
            // a bare number — making it useless for the one thing it exists for.
            case HostCall.Log: _host.Log(argc > 0 ? _module.Strings.Get((int)args[0]) : ""); Push(0); break;
            case HostCall.Time: Push(_host.Time()); break;
            case HostCall.Random: Push(_host.Random()); break;
            case HostCall.NodeMove: _host.NodeMove((int)args[0], (float)args[1], (float)args[2], (float)args[3]); Push(0); break;
            case HostCall.NodeRotate: _host.NodeRotate((int)args[0], (float)args[1], (float)args[2], (float)args[3]); Push(0); break;
            case HostCall.NodeSetVisible: _host.NodeSetVisible((int)args[0], args[1] != 0); Push(0); break;
            case HostCall.NodePlayAnim: _host.NodePlayAnim((int)args[0], (int)args[1]); Push(0); break;
            case HostCall.SoundPlay: _host.SoundPlay((int)args[0]); Push(0); break;
            // The text arrives as a string-table index (the compiler interns string literals);
            // resolving it is what makes setText show the words instead of the index.
            case HostCall.ScreenSetText: _host.ScreenSetText((int)args[0], _module.Strings.Get((int)args[1])); Push(0); break;
            case HostCall.ScreenSetNumber: _host.ScreenSetNumber((int)args[0], args[1]); Push(0); break;
            case HostCall.PlayerCount: Push(_host.PlayerCount()); break;
            // (playerIndex, axis) -> component. The single-value return convention means one
            // component per call; pushing only X (as this used to) makes a player's height
            // unreadable, which most gameplay depends on.
            case HostCall.PlayerPos:
            {
                var pos = _host.PlayerPos((int)args[0]);
                int axis = argc > 1 ? (int)args[1] : 0;
                Push(axis switch { 1 => pos.Y, 2 => pos.Z, _ => pos.X });
                break;
            }
            case HostCall.PlayerAttach:
                Push(_host.PlayerAttach((int)args[0], (int)args[1], (int)args[2]) ? 1 : 0); break;
            case HostCall.PlayerDetach: Push(_host.PlayerDetach((int)args[0]) ? 1 : 0); break;
            case HostCall.VarGet: Push(_vars[(int)args[0] & 0xFF]); break;
            case HostCall.VarSet: _vars[(int)args[0] & 0xFF] = args[1]; Push(0); break;
            case HostCall.NetEmit: _host.NetEmit((int)args[0], argc > 1 ? args[1] : 0); Push(0); break;

            // Trust≥5: Shader params (args packed as nodeSlot, uniformName-as-int-id, values)
            // The uniform name is passed as a string pool index; the bridge resolves it.
            case HostCall.ShaderSetFloat: _host.ShaderSetFloat((int)args[0], _module.Strings.Get((int)args[1]), (float)args[2]); Push(0); break;
            case HostCall.ShaderSetColor: _host.ShaderSetColor((int)args[0], _module.Strings.Get((int)args[1]), (float)args[2], (float)args[3], (float)args[4], (float)args[5]); Push(0); break;
            case HostCall.ShaderSetVec4: _host.ShaderSetVec4((int)args[0], _module.Strings.Get((int)args[1]), (float)args[2], (float)args[3], (float)args[4], (float)args[5]); Push(0); break;

            // Trust≥6: Particles and spatial audio
            case HostCall.ParticleBurst: _host.ParticleBurst((int)args[0]); Push(0); break;
            case HostCall.ParticleSetRate: _host.ParticleSetRate((int)args[0], (float)args[1]); Push(0); break;
            case HostCall.SoundPlaySpatial: _host.SoundPlaySpatial((int)args[0], (float)args[1], (float)args[2], (float)args[3]); Push(0); break;

            // Trust≥8: Advanced networking
            case HostCall.NetEmitString: _host.NetEmitString((int)args[0], _module.Strings.Get((int)args[1])); Push(0); break;
            case HostCall.NetSyncGet: Push(_host.NetSyncGet(_module.Strings.Get((int)args[0]))); break;
            case HostCall.NetSyncSet: _host.NetSyncSet(_module.Strings.Get((int)args[0]), args[1]); Push(0); break;

            default: throw new ScriptKilledException($"host call 0x{id:x} reached VM but is not dispatchable");
        }
    }

    private static int ReadI32(byte[] c, ref int p)
    {
        int v = c[p] | (c[p + 1] << 8) | (c[p + 2] << 16) | (c[p + 3] << 24); p += 4; return v;
    }

    private static float ReadF32(byte[] c, ref int p)
    {
        float v = BitConverter.ToSingle(c, p); p += 4; return v;
    }

    private static short ReadI16(byte[] c, ref int p)
    {
        short v = (short)(c[p] | (c[p + 1] << 8)); p += 2; return v;
    }
}
