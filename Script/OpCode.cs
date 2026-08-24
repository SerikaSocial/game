namespace Serika.Script;

/// SerikaScript opcode set. MUST stay in lockstep with the OpCode table in
/// server/api/src/serikascript.ts — that agreement is the sandbox boundary. No dynamic dispatch,
/// no eval, no indirect calls outside HostCall.
public enum OpCode : byte
{
    Nop = 0x00,
    PushI = 0x01,   // + i32
    PushF = 0x02,   // + f32
    PushNil = 0x03,
    Pop = 0x04,
    Dup = 0x05,
    Load = 0x06,    // + u8 local slot
    Store = 0x07,   // + u8 local slot

    Add = 0x10, Sub = 0x11, Mul = 0x12, Div = 0x13, Mod = 0x14, Neg = 0x15,
    Eq = 0x20, Ne = 0x21, Lt = 0x22, Le = 0x23, Gt = 0x24, Ge = 0x25,
    And = 0x26, Or = 0x27, Not = 0x28,

    Jmp = 0x30,       // + i16 rel
    JmpIf = 0x31,     // + i16 rel (pops cond)
    JmpIfNot = 0x32,  // + i16 rel

    HostCall = 0x40,  // + u16 host-call id, + u8 argc
    Ret = 0x50,
    Halt = 0x51,
}

/// Host-call allowlist — the ONLY bridge from a script to the engine. Deny-by-default. MUST match
/// HostCall in server/api/src/serikascript.ts.
public enum HostCall : ushort
{
    Log = 0x0001, Time = 0x0002, Random = 0x0003,
    NodeMove = 0x0100, NodeRotate = 0x0101, NodeSetVisible = 0x0102, NodePlayAnim = 0x0103,
    SoundPlay = 0x0200, ScreenSetText = 0x0201,
    PlayerCount = 0x0300, PlayerPos = 0x0301,
    VarGet = 0x0400, VarSet = 0x0401,
    NetEmit = 0x0500,
}
