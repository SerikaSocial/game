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
///
/// Trust gating: calls marked with [Trust≥N] are only available to scripts authored by users
/// with trust level ≥ N. The validator rejects the call at load time if the rank is too low;
/// the VM also checks at dispatch time as defence in depth.
public enum HostCall : ushort
{
    // ── Tier 0 (Visitor) — always available ──
    Log = 0x0001, Time = 0x0002, Random = 0x0003,
    NodeMove = 0x0100, NodeRotate = 0x0101, NodeSetVisible = 0x0102, NodePlayAnim = 0x0103,
    SoundPlay = 0x0200, ScreenSetText = 0x0201,
    PlayerCount = 0x0300, PlayerPos = 0x0301,
    VarGet = 0x0400, VarSet = 0x0401,
    NetEmit = 0x0500,

    // ── Tier 5 (Creator) — custom material/shader params ──
    /// Set a shader uniform by name on a declared material node. [Trust≥5]
    ShaderSetFloat = 0x0600,
    /// Set a shader uniform vec4 (color) by name. [Trust≥5]
    ShaderSetColor = 0x0601,
    /// Set a shader uniform float array (e.g. gradient stops). [Trust≥5]
    ShaderSetVec4 = 0x0602,

    // ── Tier 6 (Trusted) — particle effects, audio zones ──
    /// Play a particle burst on a declared particle node. [Trust≥6]
    ParticleBurst = 0x0700,
    /// Set particle emission rate. [Trust≥6]
    ParticleSetRate = 0x0701,
    /// Play a spatial sound at a world position. [Trust≥6]
    SoundPlaySpatial = 0x0702,

    // ── Tier 8 (Verified Creator) — advanced networking ──
    /// Emit a structured network message with a string payload. [Trust≥8]
    NetEmitString = 0x0800,
    /// Request a sync var from the relay. [Trust≥8]
    NetSyncGet = 0x0801,
    /// Set a sync var on the relay. [Trust≥8]
    NetSyncSet = 0x0802,
}

/// Trust-level requirements for HostCalls. Used by the validator and VM to gate capabilities.
public static class HostCallTrust
{
    /// Minimum trust level required to use a HostCall. 0 = always available.
    public static int MinTrust(HostCall call) => call switch
    {
        HostCall.ShaderSetFloat or HostCall.ShaderSetColor or HostCall.ShaderSetVec4 => 5,
        HostCall.ParticleBurst or HostCall.ParticleSetRate or HostCall.SoundPlaySpatial => 6,
        HostCall.NetEmitString or HostCall.NetSyncGet or HostCall.NetSyncSet => 8,
        _ => 0,
    };
}
