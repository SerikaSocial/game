using System;
using System.Collections.Generic;

namespace Serika.Script;

/// Thrown when a bytecode blob fails to load or validate. Loading is a trust boundary: a bad
/// module is always a rejection, never a fixup (mirrors CodecException in the wire codec).
public sealed class ScriptValidationException : Exception
{
    public ScriptValidationException(string message) : base(message) { }
}

/// A parsed, validated SerikaScript module. This is the C# side of the SSKB container and re-runs
/// the same checks as the server validator (server/api/src/serikascript.ts) at load time — the
/// client never trusts that the server validated; it validates again. The two allowlists MUST
/// agree byte-for-byte.
public sealed class ScriptModule
{
    public const uint Magic = 0x5353_4B42; // "SSKB"
    public const byte Version = 1;

    public int BudgetTick { get; private init; }
    public int BudgetMemKiB { get; private init; }
    public IReadOnlyList<ushort> HostCalls { get; private init; } = Array.Empty<ushort>();
    public byte[] Code { get; private init; } = Array.Empty<byte>();

    private static readonly HashSet<byte> AllowedOpcodes = BuildOpcodeSet();
    private static readonly HashSet<ushort> AllowedHostCalls = BuildHostCallSet();

    private static HashSet<byte> BuildOpcodeSet()
    {
        var s = new HashSet<byte>();
        foreach (OpCode op in Enum.GetValues(typeof(OpCode))) s.Add((byte)op);
        return s;
    }

    private static HashSet<ushort> BuildHostCallSet()
    {
        var s = new HashSet<ushort>();
        foreach (HostCall h in Enum.GetValues(typeof(HostCall))) s.Add((ushort)h);
        return s;
    }

    /// Rank-scaled caps — mirrors scriptCaps() in the server validator.
    public static (int Tick, int MemKiB) Caps(int rank)
    {
        int tick = rank >= 8 ? 20000 : rank >= 6 ? 12000 : rank >= 5 ? 8000 : 4000;
        int mem = rank >= 8 ? 2048 : rank >= 6 ? 1024 : 512;
        return (tick, mem);
    }

    /// Parse + validate. Throws ScriptValidationException on any disallowed opcode/host call,
    /// out-of-cap budget, out-of-range jump, or malformed structure.
    public static ScriptModule Load(ReadOnlySpan<byte> buf, int rank)
    {
        if (buf.Length < 10) throw new ScriptValidationException("truncated header");
        uint magic = (uint)((buf[0] << 24) | (buf[1] << 16) | (buf[2] << 8) | buf[3]);
        if (magic != Magic) throw new ScriptValidationException("bad magic (not SSKB)");
        if (buf[4] != Version) throw new ScriptValidationException($"unsupported bytecode version {buf[4]}");

        var caps = Caps(rank);
        int budgetTick = buf[6] | (buf[7] << 8);
        int budgetMem = buf[8] | (buf[9] << 8);
        if (budgetTick <= 0 || budgetTick > caps.Tick)
            throw new ScriptValidationException($"per-tick budget {budgetTick} outside 1..{caps.Tick}");
        if (budgetMem <= 0 || budgetMem > caps.MemKiB)
            throw new ScriptValidationException($"memory budget {budgetMem} outside 1..{caps.MemKiB}");

        int p = 10;
        if (p + 2 > buf.Length) throw new ScriptValidationException("truncated host-call count");
        int nHost = buf[p] | (buf[p + 1] << 8); p += 2;
        var hostCalls = new List<ushort>(nHost);
        for (int i = 0; i < nHost; i++)
        {
            if (p + 2 > buf.Length) throw new ScriptValidationException("truncated host-call id");
            ushort id = (ushort)(buf[p] | (buf[p + 1] << 8)); p += 2;
            if (!AllowedHostCalls.Contains(id))
                throw new ScriptValidationException($"disallowed host call 0x{id:x}");
            hostCalls.Add(id);
        }

        if (p + 4 > buf.Length) throw new ScriptValidationException("truncated code length");
        long codeLen = (uint)(buf[p] | (buf[p + 1] << 8) | (buf[p + 2] << 16) | (buf[p + 3] << 24)); p += 4;
        if (p + codeLen > buf.Length) throw new ScriptValidationException("truncated code section");
        int codeBase = p;
        int codeEnd = p + (int)codeLen;

        // Walk the opcode stream: known opcodes only, host-call ids in the declared table, jumps
        // land inside the code section.
        while (p < codeEnd)
        {
            byte op = buf[p]; int opAt = p; p += 1;
            if (!AllowedOpcodes.Contains(op))
                throw new ScriptValidationException($"unknown opcode 0x{op:x} at {opAt}");
            switch ((OpCode)op)
            {
                case OpCode.PushI: case OpCode.PushF: p += 4; break;
                case OpCode.Load: case OpCode.Store: p += 1; break;
                case OpCode.Jmp: case OpCode.JmpIf: case OpCode.JmpIfNot:
                {
                    if (p + 2 > codeEnd) throw new ScriptValidationException($"truncated jump at {opAt}");
                    short rel = (short)(buf[p] | (buf[p + 1] << 8)); p += 2;
                    int target = p + rel - codeBase;
                    if (target < 0 || target > codeLen)
                        throw new ScriptValidationException($"jump target {target} outside code section");
                    break;
                }
                case OpCode.HostCall:
                {
                    if (p + 3 > codeEnd) throw new ScriptValidationException($"truncated host call at {opAt}");
                    ushort id = (ushort)(buf[p] | (buf[p + 1] << 8)); p += 3; // id(2) + argc(1)
                    if (!hostCalls.Contains(id))
                        throw new ScriptValidationException($"host call 0x{id:x} not in declared table");
                    break;
                }
                default: break; // zero-operand
            }
        }
        if (p != codeEnd) throw new ScriptValidationException("code section did not decode cleanly");

        var code = new byte[codeLen];
        buf.Slice(codeBase, (int)codeLen).CopyTo(code);
        return new ScriptModule
        {
            BudgetTick = budgetTick,
            BudgetMemKiB = budgetMem,
            HostCalls = hostCalls,
            Code = code,
        };
    }
}
