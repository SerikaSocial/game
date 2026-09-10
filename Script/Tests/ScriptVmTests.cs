using System;
using System.Collections.Generic;
using System.IO;
using Serika.Script;
using Xunit;

namespace Serika.Script.Tests;

/// Records what the VM asked the engine to do. The real bridge additionally enforces scope
/// (declared nodes only); these tests are about the VM's own semantics, so this fake is
/// deliberately permissive and just observes.
internal sealed class FakeHost : IHostBridge
{
    public readonly List<string> Logs = new();
    public readonly List<(int Channel, double Payload)> Emits = new();
    public (float X, float Y, float Z) PlayerPosition = (1f, 2f, 3f);

    public void Log(string message) => Logs.Add(message);
    public double Time() => 0;
    public double Random() => 0.5;
    public void NodeMove(int nodeSlot, float x, float y, float z) { }
    public void NodeRotate(int nodeSlot, float x, float y, float z) { }
    public void NodeSetVisible(int nodeSlot, bool visible) { }
    public void NodePlayAnim(int nodeSlot, int animId) { }
    public void SoundPlay(int clipSlot) { }
    public void ScreenSetText(int screenSlot, string text) { }
    public int PlayerCount() => 1;
    public (float X, float Y, float Z) PlayerPos(int playerIndex) => PlayerPosition;
    public readonly List<(int Player, int Node, int Point)> Attachments = new();
    public readonly List<int> Detachments = new();
    public bool PlayerAttach(int playerIndex, int nodeSlot, int attachPoint)
    {
        Attachments.Add((playerIndex, nodeSlot, attachPoint));
        return true;
    }
    public bool PlayerDetach(int nodeSlot) { Detachments.Add(nodeSlot); return true; }
    public void NetEmit(int channel, double payload) => Emits.Add((channel, payload));
    public void ShaderSetFloat(int nodeSlot, string uniformName, float value) { }
    public void ShaderSetColor(int nodeSlot, string uniformName, float r, float g, float b, float a) { }
    public void ShaderSetVec4(int nodeSlot, string uniformName, float x, float y, float z, float w) { }
    public void ParticleBurst(int nodeSlot) { }
    public void ParticleSetRate(int nodeSlot, float rate) { }
    public void SoundPlaySpatial(int clipSlot, float x, float y, float z) { }
    public void NetEmitString(int channel, string payload) { }
    public double NetSyncGet(string key) => 0;
    public void NetSyncSet(string key, double value) { }
}

/// Builds a well-formed SSKB container so tests can exercise the VM through the real
/// ScriptModule.Load path rather than constructing modules behind its back — the loader is half
/// of the sandbox boundary and should not be bypassed by its own tests.
internal sealed class SskbBuilder
{
    private readonly List<byte> _code = new();
    private readonly List<ushort> _hostCalls = new();
    private readonly List<(HookId Hook, uint Offset)> _entries = new();
    private readonly List<string> _strings = new();

    /// Mark the current code position as the entry point for a hook.
    public SskbBuilder Entry(HookId hook)
    {
        _entries.Add((hook, (uint)_code.Count));
        return this;
    }

    public SskbBuilder Entry(HookId hook, uint offset) { _entries.Add((hook, offset)); return this; }

    public SskbBuilder Str(string s) { _strings.Add(s); return this; }

    public SskbBuilder PushI(int v)
    {
        _code.Add((byte)OpCode.PushI);
        _code.AddRange(BitConverter.GetBytes(v));
        return this;
    }

    public SskbBuilder Store(byte slot) { _code.Add((byte)OpCode.Store); _code.Add(slot); return this; }
    public SskbBuilder Load(byte slot) { _code.Add((byte)OpCode.Load); _code.Add(slot); return this; }
    public SskbBuilder Pop() { _code.Add((byte)OpCode.Pop); return this; }
    public SskbBuilder Halt() { _code.Add((byte)OpCode.Halt); return this; }

    public SskbBuilder Call(HostCall id, byte argc)
    {
        if (!_hostCalls.Contains((ushort)id)) _hostCalls.Add((ushort)id);
        _code.Add((byte)OpCode.HostCall);
        _code.AddRange(BitConverter.GetBytes((ushort)id));
        _code.Add(argc);
        return this;
    }

    public byte[] Build(int budgetTick = 1000, int budgetMemKiB = 64)
    {
        var b = new List<byte> { 0x53, 0x53, 0x4B, 0x42, ScriptModule.Version, 0 }; // "SSKB", version, flags
        b.AddRange(BitConverter.GetBytes((ushort)budgetTick));
        b.AddRange(BitConverter.GetBytes((ushort)budgetMemKiB));
        b.AddRange(BitConverter.GetBytes((ushort)_hostCalls.Count));
        foreach (var h in _hostCalls) b.AddRange(BitConverter.GetBytes(h));

        // Default to a single on_ready entry at offset 0 so simple tests need not declare one.
        var entries = _entries.Count > 0 ? _entries : new List<(HookId, uint)> { (HookId.OnReady, 0u) };
        b.AddRange(BitConverter.GetBytes((ushort)entries.Count));
        foreach (var (hook, offset) in entries)
        {
            b.Add((byte)hook);
            b.AddRange(BitConverter.GetBytes(offset));
        }

        b.AddRange(BitConverter.GetBytes((ushort)_strings.Count));
        foreach (var s in _strings)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(s);
            b.AddRange(BitConverter.GetBytes((ushort)bytes.Length));
            b.AddRange(bytes);
        }

        b.AddRange(BitConverter.GetBytes((uint)_code.Count));
        b.AddRange(_code);
        return b.ToArray();
    }
}

public class ScriptVmTests
{
    private static (FakeHost Host, ScriptVm Vm) Run(
        SskbBuilder builder, int rank = 0, HookId hook = HookId.OnReady, params double[] args)
    {
        var module = ScriptModule.Load(builder.Build(), rank);
        var host = new FakeHost();
        var vm = new ScriptVm(module, host);
        vm.RunHook(hook, args);
        return (host, vm);
    }

    /// REGRESSION. VAR_GET/VAR_SET used to index the same array as LOAD/STORE, so a script holding
    /// a local in slot N and a variable in slot N silently destroyed one with the other. §4 of the
    /// design specifies them as distinct state. This is the shape of bug that produces "the
    /// scoreboard resets itself sometimes" and is near-impossible to find from the symptom.
    [Fact]
    public void VariableStoreDoesNotAliasLocals()
    {
        var code = new SskbBuilder()
            .PushI(7).Store(3)                       // local[3] = 7
            .PushI(3).PushI(99).Call(HostCall.VarSet, 2).Pop() // var[3] = 99
            .PushI(0).Load(3).Call(HostCall.NetEmit, 2).Pop()  // report local[3]
            .Halt();

        var (host, _) = Run(code);

        Assert.Single(host.Emits);
        Assert.Equal(7, host.Emits[0].Payload); // 99 here means the aliasing bug is back
    }

    /// The variable store must still round-trip its own values — the fix must separate the two
    /// arrays without breaking VAR_GET.
    [Fact]
    public void VariableStoreRoundTrips()
    {
        var code = new SskbBuilder()
            .PushI(5).PushI(42).Call(HostCall.VarSet, 2).Pop()
            .PushI(0).PushI(5).Call(HostCall.VarGet, 1).Call(HostCall.NetEmit, 2).Pop()
            .Halt();

        var (host, _) = Run(code);

        Assert.Single(host.Emits);
        Assert.Equal(42, host.Emits[0].Payload);
    }

    /// PLAYER_POS used to push only X, which left a player's height unreadable — fatal for a
    /// parkour or any height-based gameplay. It now takes an axis selector.
    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(1, 2.0)]
    [InlineData(2, 3.0)]
    public void PlayerPosReturnsRequestedAxis(int axis, double expected)
    {
        var code = new SskbBuilder()
            .PushI(0).PushI(axis).Call(HostCall.PlayerPos, 2).Store(0)
            .PushI(0).Load(0).Call(HostCall.NetEmit, 2).Pop()
            .Halt();

        var (host, _) = Run(code);

        Assert.Single(host.Emits);
        Assert.Equal(expected, host.Emits[0].Payload, 3);
    }

    /// Arity comes from `argc`, not from the fixed 8-slot argument buffer. The old code guarded on
    /// `args.Length`, which is always 8, so a zero-arg LOG read slot 0 anyway. Only latent today
    /// because stackalloc happens to be zero-initialised — pin the intended semantics so it stays
    /// correct if that ever changes.
    [Fact]
    public void ZeroArgLogPassesEmptyString()
    {
        var code = new SskbBuilder().Str("unused").Call(HostCall.Log, 0).Pop().Halt();

        var (host, _) = Run(code);

        Assert.Single(host.Logs);
        Assert.Equal("", host.Logs[0]);
    }

    /// LOG resolves a string-table index. Before the table existed it stringified a number, so a
    /// script could not log a message at all.
    [Fact]
    public void LogResolvesStringTableIndex()
    {
        var code = new SskbBuilder().Str("first").Str("summit reached")
            .Entry(HookId.OnReady).PushI(1).Call(HostCall.Log, 1).Pop().Halt();

        var (host, _) = Run(code);

        Assert.Equal("summit reached", host.Logs[0]);
    }

    /// Per-script isolation (T5). The pool used to be a static global whose own comment claimed it
    /// was per-module: two loaded modules shared one table, and loading the second invalidated the
    /// first's live indices.
    [Fact]
    public void StringPoolsAreIsolatedBetweenModules()
    {
        var a = ScriptModule.Load(new SskbBuilder().Halt().Build(), 0);
        var b = ScriptModule.Load(new SskbBuilder().Halt().Build(), 0);

        int idx = a.Strings.Register("secret-key");

        Assert.Equal("secret-key", a.Strings.Get(idx));
        Assert.Equal("", b.Strings.Get(idx)); // module B must not see module A's strings
        Assert.Equal(0, b.Strings.Count);
    }

    /// Hooks are dispatched independently from their own entry offsets. v1 had no entry table at
    /// all — execution always began at offset 0 — so the entire event model in §4 was unreachable.
    [Fact]
    public void EachHookRunsFromItsOwnEntryPoint()
    {
        var b = new SskbBuilder();
        b.Entry(HookId.OnReady)
            .PushI(0).PushI(11).Call(HostCall.NetEmit, 2).Pop().Halt();
        b.Entry(HookId.OnInteract)
            .PushI(0).PushI(22).Call(HostCall.NetEmit, 2).Pop().Halt();

        var module = ScriptModule.Load(b.Build(), 0);
        var host = new FakeHost();
        var vm = new ScriptVm(module, host);

        Assert.True(vm.RunHook(HookId.OnReady));
        Assert.True(vm.RunHook(HookId.OnInteract, new double[] { 3 }));

        Assert.Equal(new[] { 11.0, 22.0 }, host.Emits.ConvertAll(e => e.Payload));
    }

    /// A hook the module doesn't define is not an error — the host dispatches every event and the
    /// module answers only what it handles.
    [Fact]
    public void UndefinedHookIsSkippedNotAnError()
    {
        var module = ScriptModule.Load(new SskbBuilder().Entry(HookId.OnReady).Halt().Build(), 0);
        var vm = new ScriptVm(module, new FakeHost());

        Assert.True(vm.RunHook(HookId.OnReady));
        Assert.False(vm.RunHook(HookId.OnTick, new double[] { 0.016 }));
    }

    /// Hook arguments arrive in locals 0..n-1. on_tick(dt) was the specific thing v1 could not
    /// express: there was nowhere for dt to go.
    [Fact]
    public void HookArgumentsArriveInLocals()
    {
        var code = new SskbBuilder()
            .Entry(HookId.OnEnterZone)
            .PushI(0).Load(1).Call(HostCall.NetEmit, 2).Pop() // report zoneId (arg 1)
            .Halt();

        var module = ScriptModule.Load(code.Build(), 0);
        var host = new FakeHost();
        new ScriptVm(module, host).RunHook(HookId.OnEnterZone, new double[] { 7, 42 });

        Assert.Single(host.Emits);
        Assert.Equal(42, host.Emits[0].Payload);
    }

    /// Locals must not leak between hook invocations, or one event can read another's leftovers.
    /// The variable store is the *only* thing that persists.
    [Fact]
    public void LocalsAreClearedBetweenHooksButVariablesPersist()
    {
        var b = new SskbBuilder();
        b.Entry(HookId.OnReady)
            .PushI(99).Store(5)                                   // local[5] = 99 (must not survive)
            .PushI(1).PushI(77).Call(HostCall.VarSet, 2).Pop()    // var[1] = 77 (must survive)
            .Halt();
        b.Entry(HookId.OnTick)
            .PushI(0).Load(5).Call(HostCall.NetEmit, 2).Pop()
            .PushI(0).PushI(1).Call(HostCall.VarGet, 1).Call(HostCall.NetEmit, 2).Pop()
            .Halt();

        var module = ScriptModule.Load(b.Build(), 0);
        var host = new FakeHost();
        var vm = new ScriptVm(module, host);
        vm.RunHook(HookId.OnReady);
        vm.RunHook(HookId.OnTick, new double[] { 0.016 });

        Assert.Equal(new[] { 0.0, 77.0 }, host.Emits.ConvertAll(e => e.Payload));
    }

    /// Attachment is the one call that touches a player, and it is one-directional by design.
    [Fact]
    public void PlayerAttachIsDispatchedAndGatedByTrust()
    {
        byte[] blob = new SskbBuilder()
            .Entry(HookId.OnReady)
            .PushI(2).PushI(0).PushI(3).Call(HostCall.PlayerAttach, 3).Pop()
            .Halt()
            .Build();

        Assert.Throws<ScriptValidationException>(() => ScriptModule.Load(blob, rank: 4));

        var host = new FakeHost();
        new ScriptVm(ScriptModule.Load(blob, rank: 5), host).RunHook(HookId.OnReady);
        Assert.Equal((2, 0, 3), host.Attachments[0]);
    }

    /// The string table must land in the pool at the exact indices the bytecode was compiled
    /// against — including duplicates, which a deduplicating load would collapse and shift.
    [Fact]
    public void StringTableLoadsAtVerbatimIndices()
    {
        var module = ScriptModule.Load(
            new SskbBuilder().Str("alpha").Str("beta").Str("alpha").Halt().Build(), 0);

        Assert.Equal(3, module.Strings.Count);
        Assert.Equal("alpha", module.Strings.Get(0));
        Assert.Equal("beta", module.Strings.Get(1));
        Assert.Equal("alpha", module.Strings.Get(2));
    }

    /// An entry offset outside the code section is the same escape class as a bad jump (T9).
    [Fact]
    public void EntryOffsetOutsideCodeSectionIsRejected()
    {
        var ex = Assert.Throws<ScriptValidationException>(() =>
            ScriptModule.Load(new SskbBuilder().Entry(HookId.OnReady, 9999).Halt().Build(), 0));
        Assert.Contains("outside code section", ex.Message);
    }

    /// Two entries for one hook make dispatch ambiguous.
    [Fact]
    public void DuplicateHookEntryIsRejected()
    {
        var ex = Assert.Throws<ScriptValidationException>(() =>
            ScriptModule.Load(
                new SskbBuilder().Entry(HookId.OnReady, 0).Entry(HookId.OnReady, 0).Halt().Build(), 0));
        Assert.Contains("duplicate entry", ex.Message);
    }

    /// The loader is a trust boundary: a call above the author's rank is a rejection at load, not
    /// something the VM is left to catch at dispatch.
    [Fact]
    public void HostCallAboveAuthorRankIsRejectedAtLoad()
    {
        byte[] blob = new SskbBuilder().Call(HostCall.NetSyncSet, 2).Halt().Build();

        var ex = Assert.Throws<ScriptValidationException>(() => ScriptModule.Load(blob, rank: 4));
        Assert.Contains("requires trust level 8", ex.Message);

        ScriptModule.Load(blob, rank: 8); // same bytes, sufficient rank — must load
    }
}
