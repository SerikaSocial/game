using System;
using System.Collections.Generic;
using System.IO;
using Serika.Script;
using Xunit;

namespace Serika.Script.Tests;

/// Executes bytecode produced by the TypeScript compiler (tools/serikascript) in the C# VM.
///
/// This is the end-to-end half of the boundary. AllowlistAgreementTests proves the two sides
/// describe the same tables; this proves an artifact one of them PRODUCED is one the other can
/// actually run. The compiler validating its own output proves nothing about the VM — same reason
/// the wire codec has a golden corpus rather than two self-consistent test suites.
///
/// Regenerate with `bun tools/serikascript/gen-golden.ts` after any format or compiler change.
public class GoldenModuleTests
{
    private static byte[] LoadGolden(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "tools", "serikascript", "golden", name);
            if (File.Exists(candidate)) return File.ReadAllBytes(candidate);
            dir = dir.Parent;
        }
        throw new InvalidOperationException($"could not locate tools/serikascript/golden/{name}");
    }

    /// The host the golden script sees. Player count is fixed at 3 so the unrolled tie-in in
    /// on_ready exercises both the taken and untaken branches.
    private sealed class ParkourHost : IHostBridge
    {
        public readonly List<string> Logs = new();
        public readonly List<(int Channel, double Payload)> Emits = new();
        public readonly List<(int Player, int Node, int Point)> Attachments = new();
        public int Players = 3;

        public void Log(string message) => Logs.Add(message);
        public double Time() => 0;
        public double Random() => 0.5;
        public void NodeMove(int n, float x, float y, float z) { }
        public void NodeRotate(int n, float x, float y, float z) { }
        public void NodeSetVisible(int n, bool v) { }
        public void NodePlayAnim(int n, int a) { }
        public void SoundPlay(int c) { }
        public void ScreenSetText(int s, string t) { }
        public void ScreenSetNumber(int s, double v) { }
        public int PlayerCount() => Players;
        public (float X, float Y, float Z) PlayerPos(int i) => (0, 0, 0);
        public bool PlayerAttach(int player, int node, int point)
        {
            Attachments.Add((player, node, point));
            return true;
        }
        public bool PlayerDetach(int node) => false;
        public void NetEmit(int channel, double payload) => Emits.Add((channel, payload));
        public void ShaderSetFloat(int n, string u, float v) { }
        public void ShaderSetColor(int n, string u, float r, float g, float b, float a) { }
        public void ShaderSetVec4(int n, string u, float x, float y, float z, float w) { }
        public void ParticleBurst(int n) { }
        public void ParticleSetRate(int n, float r) { }
        public void SoundPlaySpatial(int c, float x, float y, float z) { }
        public void NetEmitString(int c, string p) { }
        public double NetSyncGet(string k) => 0;
        public void NetSyncSet(string k, double v) { }
    }

    private static ScriptModule Parkour() => ScriptModule.Load(LoadGolden("rope_parkour.sskb"), rank: 8);

    [Fact]
    public void GoldenModuleLoadsAndDeclaresItsHooks()
    {
        var m = Parkour();

        Assert.True(m.HasHook(HookId.OnReady));
        Assert.True(m.HasHook(HookId.OnTick));
        Assert.True(m.HasHook(HookId.OnEnterZone));
        Assert.False(m.HasHook(HookId.OnInteract));
        // Scan the whole pool: which index a literal lands on is an artifact of source order,
        // not part of the contract. The old assertion pinned an index and broke the first time
        // the script gained a board string.
        bool hasSummit = false;
        for (int i = 0; i < m.Strings.Count; i++)
            if (m.Strings.Get(i).Contains("summit reached")) hasSummit = true;
        Assert.True(hasSummit);
    }

    /// on_ready ties in exactly the players who are present — the unrolled guards must respect
    /// playerCount(), not attach all four unconditionally.
    [Fact]
    public void OnReadyAttachesOnlyPresentPlayers()
    {
        var host = new ParkourHost { Players = 3 };
        new ScriptVm(Parkour(), host).RunHook(HookId.OnReady);

        Assert.Equal(
            new[] { (0, 0, 0), (1, 1, 0), (2, 2, 0) },
            host.Attachments);
        Assert.Equal("rope parkour: team tied in", host.Logs[0]);
    }

    [Fact]
    public void EmptyLobbyTiesInNobody()
    {
        var host = new ParkourHost { Players = 0 };
        new ScriptVm(Parkour(), host).RunHook(HookId.OnReady);

        Assert.Empty(host.Attachments);
    }

    /// Checkpoint progress is monotonic: dropping back through a lower zone must not rewind the
    /// team's best, and must not re-emit progress.
    [Fact]
    public void CheckpointProgressOnlyMovesForward()
    {
        var host = new ParkourHost();
        var vm = new ScriptVm(Parkour(), host);
        vm.RunHook(HookId.OnReady);
        host.Emits.Clear();

        vm.RunHook(HookId.OnEnterZone, new double[] { 0, 1 });
        vm.RunHook(HookId.OnEnterZone, new double[] { 1, 2 });
        vm.RunHook(HookId.OnEnterZone, new double[] { 0, 1 }); // fell back — no progress
        vm.RunHook(HookId.OnEnterZone, new double[] { 2, 3 });

        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, host.Emits.ConvertAll(e => e.Payload));
        Assert.All(host.Emits, e => Assert.Equal(1, e.Channel));
    }

    /// The run timer accumulates across ticks and stops once the summit is claimed — the whole
    /// point of state surviving between hook invocations.
    [Fact]
    public void TimerAccumulatesAcrossTicksAndStopsAtTheSummit()
    {
        var host = new ParkourHost();
        var vm = new ScriptVm(Parkour(), host);
        vm.RunHook(HookId.OnReady);

        for (int i = 0; i < 10; i++) vm.RunHook(HookId.OnTick, new double[] { 0.5 });

        host.Emits.Clear();
        vm.RunHook(HookId.OnEnterZone, new double[] { 0, 4 }); // summit

        // channel 1 = progress, channel 2 = finish time
        var finish = host.Emits.Find(e => e.Channel == 2);
        Assert.Equal(5.0, finish.Payload, 3);
        Assert.Contains("summit reached", host.Logs);

        // Ticks after the finish must not advance the recorded time, and a second summit entry
        // must not re-announce.
        for (int i = 0; i < 4; i++) vm.RunHook(HookId.OnTick, new double[] { 0.5 });
        host.Emits.Clear();
        vm.RunHook(HookId.OnEnterZone, new double[] { 1, 4 });
        Assert.DoesNotContain(host.Emits, e => e.Channel == 2);
    }
}
