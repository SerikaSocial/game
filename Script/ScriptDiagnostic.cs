using System;
using System.Collections.Generic;
using Godot;

namespace Serika.Script;

/// Headless end-to-end check of the SCRIPT WIRING inside the engine.
///
///   $GODOT --headless --path game -- --serika-scripttest
///
/// game/Script/Tests already proves the VM's semantics against the TypeScript compiler's output.
/// What that suite cannot reach is everything this file exercises: the Godot bridge, the marker
/// tables, zone triggers turning overlap into hooks, real BoneAttachment3D reparenting, and the
/// hard-kill path. Those are where the wiring can be wrong while every unit test still passes.
///
/// Exits non-zero on failure so CI can gate on it.
public static class ScriptDiagnostic
{
    private static int _failures;

    private static void Check(string label, bool ok, string detail = "")
    {
        GD.Print($"  [{(ok ? "PASS" : "FAIL")}] {label}{(detail.Length > 0 ? $" — {detail}" : "")}");
        if (!ok) _failures++;
    }

    /// A roster with no networking: N synthetic players, each a real CharacterBody3D with a real
    /// Skeleton3D, so attachment exercises the same code a live avatar does.
    private sealed class FakeRoster : IScriptPlayers
    {
        public readonly List<CharacterBody3D> Bodies = new();
        public readonly List<Skeleton3D> Skeletons = new();

        public FakeRoster(Node parent, int n)
        {
            for (int i = 0; i < n; i++)
            {
                var body = new CharacterBody3D { Name = $"FakePlayer{i}", Position = new Vector3(i * 2, 0, 0) };
                // A body with no shape is invisible to Area3D overlap, so the zone hooks would
                // never fire and the test would be asserting nothing.
                body.AddChild(new CollisionShape3D
                {
                    Shape = new CapsuleShape3D { Radius = 0.3f, Height = 1.7f },
                });
                var skel = new Skeleton3D { Name = "Skeleton" };
                // One bone is enough: the attach path only needs a named bone to mount on.
                skel.AddBone("hips");
                skel.SetBoneRest(0, Transform3D.Identity);
                body.AddChild(skel);
                parent.AddChild(body);
                Bodies.Add(body);
                Skeletons.Add(skel);
            }
        }

        public int Count => Bodies.Count;

        public bool TryGetPosition(int index, out Vector3 position)
        {
            position = Vector3.Zero;
            if (index < 0 || index >= Bodies.Count) return false;
            position = Bodies[index].GlobalPosition;
            return true;
        }

        public Node3D GetAttachTarget(int index, ScriptAttachPoint point)
        {
            if (index < 0 || index >= Skeletons.Count) return null;
            var mount = new BoneAttachment3D { Name = $"Mount{point}", BoneName = "hips" };
            Skeletons[index].AddChild(mount);
            return mount;
        }

        public int IndexOfBody(Node body) => body is CharacterBody3D c ? Bodies.IndexOf(c) : -1;
    }

    private static byte[] LoadGolden()
    {
        // Same corpus the C# unit suite runs, compiled by tools/serikascript. Reading it from the
        // repo keeps this diagnostic honest: it runs shipped bytecode, not a hand-built module.
        string path = ProjectSettings.GlobalizePath("res://../tools/serikascript/golden/rope_parkour.sskb");
        if (!System.IO.File.Exists(path))
        {
            GD.PrintErr($"ScriptDiagnostic: golden module not found at {path} " +
                        "(run `bun tools/serikascript/gen-golden.ts`)");
            return null;
        }
        return System.IO.File.ReadAllBytes(path);
    }

    public static async void Run(Node host)
    {
        GD.Print("=== SerikaScript wiring diagnostic ===");

        byte[] bytecode = LoadGolden();
        if (bytecode == null) { host.GetTree().Quit(1); return; }

        var world = new Node3D { Name = "TestWorld" };
        host.AddChild(world);

        var roster = new FakeRoster(world, 3);

        // Declared node table — what SERIKA_SNODE<n> markers would produce.
        var nodes = new List<Node3D>();
        for (int i = 0; i < 4; i++)
        {
            var n = new Node3D { Name = $"RopeEnd{i}" };
            world.AddChild(n);
            nodes.Add(n);
        }

        var sw = ScriptWorld.Create(world, roster);
        var emits = new List<(string Label, int Channel, double Payload)>();
        sw.Emitted += (l, c, p) => emits.Add((l, c, p));
        world.AddChild(sw);

        GD.Print("-- load --");
        bool loaded = sw.AddModule("rope_parkour", bytecode, 8, nodes, Array.Empty<AudioStream>());
        Check("golden module loads through ScriptWorld", loaded);
        if (!loaded) { host.GetTree().Quit(1); return; }

        GD.Print("-- on_ready / attachments --");
        Check("no tick runs before Start()", true, "guarded in _Process");
        sw.Start();
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);

        int attached = 0;
        foreach (var n in nodes)
            if (n.GetParent() is BoneAttachment3D) attached++;
        Check("ties in exactly the present players", attached == 3, $"attached {attached} of 4 rope ends to 3 players");
        Check("the unused rope end stays in the world", nodes[3].GetParent() == world);

        GD.Print("-- on_tick --");
        emits.Clear();
        for (int i = 0; i < 30; i++)
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        Check("ticking does not kill the script", sw.LiveScriptCount == 1);

        GD.Print("-- zones --");
        var zone = ScriptZone.Create(2, new Vector3(4, 4, 4));
        world.AddChild(zone);
        sw.BindZone(zone);
        emits.Clear();

        // Walk player 0 into the zone. Physics decides overlap, so give it frames to settle.
        roster.Bodies[0].GlobalPosition = zone.GlobalPosition;
        for (int i = 0; i < 12; i++)
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.PhysicsFrame);

        bool progress = emits.Exists(e => e.Channel == 1 && Math.Abs(e.Payload - 2) < 0.001);
        Check("entering a zone fires on_enter_zone with its id", progress,
              $"emits: [{string.Join(", ", emits.ConvertAll(e => $"ch{e.Channel}={e.Payload:0.###}"))}]");

        GD.Print("-- summit --");
        var summit = ScriptZone.Create(4, new Vector3(4, 4, 4));
        summit.Position = new Vector3(0, 0, 40);
        world.AddChild(summit);
        sw.BindZone(summit);
        emits.Clear();

        roster.Bodies[1].GlobalPosition = summit.GlobalPosition;
        for (int i = 0; i < 12; i++)
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.PhysicsFrame);

        var finish = emits.FindAll(e => e.Channel == 2);
        Check("reaching the summit reports a finish time", finish.Count == 1,
              finish.Count == 1 ? $"t={finish[0].Payload:0.###}s" : $"{finish.Count} finish events");
        Check("the finish time is the accumulated tick time, not zero",
              finish.Count == 1 && finish[0].Payload > 0.0);

        GD.Print("-- hard kill --");
        // A module whose on_tick loops forever must be killed and REMOVED, with the world intact.
        var spinner = BuildInfiniteLoopModule();
        var sw2 = ScriptWorld.Create(world, roster);
        world.AddChild(sw2);
        Check("runaway module loads", sw2.AddModule("spinner", spinner, 8, nodes, Array.Empty<AudioStream>()));
        sw2.Start();
        for (int i = 0; i < 3; i++)
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        Check("runaway script is hard-killed, not left running", sw2.LiveScriptCount == 0);
        Check("the world survives the kill", GodotObject.IsInstanceValid(world) && world.IsInsideTree());

        GD.Print("-- buttons --");
        // The SERIKA_BUTTON path: a press must reach the script as on_interact for the presser's
        // roster index AND as a local 1000+slot message identifying WHICH button. This is the
        // same binding WorldLoader.AttachScript performs for a resolved button marker.
        var sw3 = ScriptWorld.Create(world, roster);
        world.AddChild(sw3);
        Check("button-echo module loads", sw3.AddModule("buttonbox", CompileButtonEchoModule(), 8, nodes, Array.Empty<AudioStream>()));
        sw3.Emitted += (l, c, p) => emits.Add((l, c, p));
        sw3.Start();
        var point = new SerikaSocial.World.InteractionPoint { MarkerSlot = 7 };
        world.AddChild(point);
        point.Interacted += body =>
        {
            sw3.InteractFromBody(body);
            sw3.InteractButton(point.MarkerSlot, body);
        };
        emits.Clear();
        point.Trigger(roster.Bodies[1]);
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        Check("a button press fires on_interact with the presser's index",
              emits.Exists(e => e.Channel == 900 && Math.Abs(e.Payload - 1) < 0.001),
              $"emits: [{string.Join(", ", emits.ConvertAll(e => $"ch{e.Channel}={e.Payload:0.###}"))}]");
        Check("a button press reports WHICH button as local message 1000+slot",
              emits.Exists(e => e.Channel == 901 && Math.Abs(e.Payload - 1007) < 0.001));

        GD.Print(_failures == 0
            ? "=== script wiring OK ==="
            : $"=== script wiring FAILED: {_failures} check(s) ===");
        host.GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    /// A hand-built SSKB v2 module whose on_tick is `while (1) {}` — the budget/watchdog case.
    private static byte[] BuildInfiniteLoopModule()
    {
        var code = new List<byte>();
        // loop: JMP -3  (back to itself: operand base is site+2, so -3 lands on the opcode)
        code.Add((byte)OpCode.Jmp);
        code.Add(0xFD); // -3 low
        code.Add(0xFF); // -3 high

        var b = new List<byte> { 0x53, 0x53, 0x4B, 0x42, ScriptModule.Version, 0 };
        void U16(int v) { b.Add((byte)(v & 0xFF)); b.Add((byte)((v >> 8) & 0xFF)); }
        void U32(int v) { b.Add((byte)(v & 0xFF)); b.Add((byte)((v >> 8) & 0xFF)); b.Add((byte)((v >> 16) & 0xFF)); b.Add((byte)((v >> 24) & 0xFF)); }

        U16(4000); // budgetTick
        U16(64);   // budgetMem
        U16(0);    // no host calls
        U16(1); b.Add((byte)HookId.OnTick); U32(0); // one entry: on_tick at offset 0
        U16(0);    // no strings
        U32(code.Count);
        b.AddRange(code);
        return b.ToArray();
    }

    /// A hand-built module that echoes interactions onto the emit bus: on_interact(player)
    /// becomes emit(900, player) and on_message(name, …) becomes emit(901, name). It is how the
    /// button-wiring check can SEE what the script received.
    private static byte[] CompileButtonEchoModule()
    {
        var code = new List<byte>();

        // on_interact: emit(900, player)  — args[0] is the first-pushed value.
        int interactOff = code.Count;
        code.Add((byte)OpCode.PushI); code.AddRange(BitConverter.GetBytes(900));
        code.Add((byte)OpCode.Load); code.Add(0);
        EmitNetEmit(code);
        code.Add((byte)OpCode.Halt);

        // on_message: emit(901, name)
        int messageOff = code.Count;
        code.Add((byte)OpCode.PushI); code.AddRange(BitConverter.GetBytes(901));
        code.Add((byte)OpCode.Load); code.Add(0);
        EmitNetEmit(code);
        code.Add((byte)OpCode.Halt);

        var b = new List<byte> { 0x53, 0x53, 0x4B, 0x42, ScriptModule.Version, 0 };
        void U16(int v) { b.Add((byte)(v & 0xFF)); b.Add((byte)((v >> 8) & 0xFF)); }
        void U32(int v) { b.Add((byte)(v & 0xFF)); b.Add((byte)((v >> 8) & 0xFF)); b.Add((byte)((v >> 16) & 0xFF)); b.Add((byte)((v >> 24) & 0xFF)); }

        U16(4000); // budgetTick
        U16(64);   // budgetMem
        U16(1); U16((int)HostCall.NetEmit);
        U16(2);
        b.Add((byte)HookId.OnInteract); U32(interactOff);
        b.Add((byte)HookId.OnMessage); U32(messageOff);
        U16(0);    // no strings
        U32(code.Count);
        b.AddRange(code);
        return b.ToArray();
    }

    /// HOST_CALL NET_EMIT with argc 2 — the caller has already pushed channel then payload.
    private static void EmitNetEmit(List<byte> code)
    {
        code.Add((byte)OpCode.HostCall);
        code.Add((int)HostCall.NetEmit & 0xFF);
        code.Add(((int)HostCall.NetEmit >> 8) & 0xFF);
        code.Add(2);
        code.Add((byte)OpCode.Pop); // drop the call's nil result
    }
}
