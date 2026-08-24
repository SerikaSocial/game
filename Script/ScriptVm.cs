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
    private const int WatchdogMs = 8; // per invocation wall-clock ceiling

    private readonly ScriptModule _module;
    private readonly IHostBridge _host;
    private readonly double[] _stack = new double[MaxStack];
    private readonly double[] _locals = new double[MaxLocals];
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

    /// Run the module from offset 0 until Ret/Halt or the end of code. Enforces the per-tick
    /// instruction budget and an 8 ms watchdog. Throws ScriptKilledException on any breach.
    public void Run()
    {
        _sp = 0;
        byte[] code = _module.Code;
        int budget = _module.BudgetTick;
        int pc = 0;
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
        Span<double> args = stackalloc double[8];
        if (argc > 8) throw new ScriptKilledException("too many host-call args");
        for (int i = argc - 1; i >= 0; i--) args[i] = Pop();

        switch ((HostCall)id)
        {
            case HostCall.Log: _host.Log(args.Length > 0 ? args[0].ToString() : ""); Push(0); break;
            case HostCall.Time: Push(_host.Time()); break;
            case HostCall.Random: Push(_host.Random()); break;
            case HostCall.NodeMove: _host.NodeMove((int)args[0], (float)args[1], (float)args[2], (float)args[3]); Push(0); break;
            case HostCall.NodeRotate: _host.NodeRotate((int)args[0], (float)args[1], (float)args[2], (float)args[3]); Push(0); break;
            case HostCall.NodeSetVisible: _host.NodeSetVisible((int)args[0], args[1] != 0); Push(0); break;
            case HostCall.NodePlayAnim: _host.NodePlayAnim((int)args[0], (int)args[1]); Push(0); break;
            case HostCall.SoundPlay: _host.SoundPlay((int)args[0]); Push(0); break;
            case HostCall.ScreenSetText: _host.ScreenSetText((int)args[0], ((int)args[1]).ToString()); Push(0); break;
            case HostCall.PlayerCount: Push(_host.PlayerCount()); break;
            case HostCall.PlayerPos: { var pos = _host.PlayerPos((int)args[0]); Push(pos.X); break; }
            case HostCall.VarGet: Push(_locals[(int)args[0] & 0xFF]); break;
            case HostCall.VarSet: _locals[(int)args[0] & 0xFF] = args[1]; Push(0); break;
            case HostCall.NetEmit: _host.NetEmit((int)args[0], args.Length > 1 ? args[1] : 0); Push(0); break;

            // Trust≥5: Shader params (args packed as nodeSlot, uniformName-as-int-id, values)
            // The uniform name is passed as a string pool index; the bridge resolves it.
            case HostCall.ShaderSetFloat: _host.ShaderSetFloat((int)args[0], StringPool.Get((int)args[1]), (float)args[2]); Push(0); break;
            case HostCall.ShaderSetColor: _host.ShaderSetColor((int)args[0], StringPool.Get((int)args[1]), (float)args[2], (float)args[3], (float)args[4], (float)args[5]); Push(0); break;
            case HostCall.ShaderSetVec4: _host.ShaderSetVec4((int)args[0], StringPool.Get((int)args[1]), (float)args[2], (float)args[3], (float)args[4], (float)args[5]); Push(0); break;

            // Trust≥6: Particles and spatial audio
            case HostCall.ParticleBurst: _host.ParticleBurst((int)args[0]); Push(0); break;
            case HostCall.ParticleSetRate: _host.ParticleSetRate((int)args[0], (float)args[1]); Push(0); break;
            case HostCall.SoundPlaySpatial: _host.SoundPlaySpatial((int)args[0], (float)args[1], (float)args[2], (float)args[3]); Push(0); break;

            // Trust≥8: Advanced networking
            case HostCall.NetEmitString: _host.NetEmitString((int)args[0], StringPool.Get((int)args[1])); Push(0); break;
            case HostCall.NetSyncGet: Push(_host.NetSyncGet(StringPool.Get((int)args[0]))); break;
            case HostCall.NetSyncSet: _host.NetSyncSet(StringPool.Get((int)args[0]), args[1]); Push(0); break;

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
