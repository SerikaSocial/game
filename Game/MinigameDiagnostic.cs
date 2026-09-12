using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Serika.Net;
using Serika.Script;
using SerikaSocial.Player;
using SerikaSocial.World;
using SerikaSocial.UI;

namespace SerikaSocial.Game;

/// Actual bundle import, real player locomotion and marker-driven gameplay against a local
/// HTTP fixture. Does not substitute for a multiplayer relay/headset playtest.
/// --headless --path game res://Game/MinigameDiagnostic.tscn -- --bundles <directory>
/// Omit --headless and add --shots <directory> to capture the same worlds in-engine.
public partial class MinigameDiagnostic : Node3D
{
    private LocalPlayer _player;
    private Node3D _world;
    private int _checks, _failed;
    private string _shots;
    private bool _visualOnly;
    private readonly Dictionary<uint, RemoteAvatar> _peers = new();
    private Vector2 _move;
    private bool _jump;

    public override void _PhysicsProcess(double delta)
    {
        if (_player == null) return;
        _player.ExternalMove = _move;
        _player.ExternalJump = _jump;
        _jump = false;
    }

    public override async void _Ready()
    {
        ProcessPhysicsPriority = -100;
        var args = OS.GetCmdlineUserArgs();
        string Arg(string key) { int i = Array.IndexOf(args, key); return i >= 0 && i+1 < args.Length ? args[i+1] : null; }
        string bundles = Arg("--bundles");
        _shots = Arg("--shots");
        _visualOnly = args.Contains("--visual-only");
        try
        {
            if (bundles == null) throw new ArgumentException("--bundles directory required");
            if (_shots != null) Directory.CreateDirectory(_shots);
            _player = new LocalPlayer(); AddChild(_player);
            WorldLoader.LocalBody = () => _player;
            WorldLoader.ScriptPlayers = new ScriptPlayerRoster(() => _player, _peers);
            foreach (string key in new[] { "imposter-station", "gauntlet-course", "rope-parkour" })
            {
                _world = new Node3D(); AddChild(_world);
                var spawn = WorldLoader.LoadFromPath(Path.Combine(bundles, key+".serikaworld"), key, _world);
                Check(spawn.HasValue, key+" imports");
                _player.GlobalPosition = spawn ?? Vector3.Up; _player.Velocity = Vector3.Zero;
                await Frames(45);
                Check(_player.IsOnFloor(), key+" spawn has solid support");
                foreach (var pad in Walk(_world).OfType<CheckpointNode>())
                {
                    var hit = GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(
                        pad.RespawnPoint + Vector3.Up * .1f, pad.RespawnPoint - Vector3.Up * 1.2f, 1));
                    Check(hit.Count > 0, key+$" checkpoint {pad.Index} has a floor");
                }
                if (_visualOnly)
                {
                    using var fixture = new GameHttpFixture();
                    var session = GameSession.Create(new ApiClient(fixture.Url), "preview", key == "imposter-station" ? GameModeKind.Imposter : key == "rope-parkour" ? GameModeKind.Rope : GameModeKind.Gauntlet);
                    AddChild(session);
                    var controller = MakeController(session);
                    await Frames(20);
                    if (key == "imposter-station")
                    {
                        await Shot("station-lobby", new Vector3(8,2.5f,51), new Vector3(0,2,40));
                        await Shot("station-spine", new Vector3(0,2.2f,29), new Vector3(0,2,-20));
                        await Shot("station-bridge", new Vector3(0,2.4f,-33), new Vector3(0,3,-150));
                        await Shot("station-reactor", new Vector3(-14,2,-20), new Vector3(-9,1.8f,-27));
                    }
                    if (key == "gauntlet-course")
                    {
                        await Shot("gauntlet-lobby", new Vector3(11,3,37), new Vector3(0,2,24));
                        await Shot("gauntlet-tidal", new Vector3(-43,25,-31), new Vector3(-70,0,-53));
                        await Shot("gauntlet-rotor", new Vector3(25,20,-17), new Vector3(0,0,-43));
                        await Shot("gauntlet-crown", new Vector3(98,28,-43), new Vector3(70,5,-67));
                    }
                    if (key == "rope-parkour")
                    {
                        await Shot("rope-lobby", new Vector3(10,3,34), new Vector3(0,2,22));
                        await Shot("rope-viaduct", new Vector3(14,13,5), new Vector3(0,2,-27));
                        await Shot("rope-canyon", new Vector3(24,22,-56), new Vector3(0,5,-78));
                        await Shot("rope-summit", new Vector3(15,18,-94), new Vector3(0,8,-117));
                    }
                    controller.QueueFree(); session.QueueFree(); await Frames(3);
                }
                else
                {
                    if (key == "imposter-station") await Station();
                    if (key == "gauntlet-course") await Gauntlet();
                    if (key == "rope-parkour") await Rope();
                }
                _move = Vector2.Zero;
                RemoveChild(_world); _world.QueueFree(); await Frames(3);
            }
            if (!_visualOnly) { RopeRecovery(); await SessionBoundary(); }
        }
        catch (Exception e) { GD.PrintErr(e); _failed++; }
        finally
        {
            WorldLoader.LocalBody = null; WorldLoader.ScriptPlayers = null;
            GD.Print($"MINIGAME: {_checks} checks, {_failed} failures");
            GetTree().Quit(_failed == 0 ? 0 : 1);
        }
    }

    private async Task Station()
    {
        var nodes = Walk(_world).OfType<Node3D>().ToArray();
        Check(nodes.Count(n => n.Name.ToString().StartsWith("SERIKA_GAME_TASK")) == 10, "ten distinct ship system consoles");
        Check(nodes.Count(n => n.Name.ToString().StartsWith("SERIKA_GAME_VENT")) == 10, "five paired vent routes");
        Check(!Walk(_world).OfType<WarpPad>().Any(), "crew cannot auto-warp through vents");
        await NativeStation();
        // Walk each opposed doorway. These paths catch rotated wall segments and sealed exits.
        foreach (float z in new[] { -24f, -12f, 0f, 12f, 24f })
        {
            foreach (float side in new[] { -1f, 1f })
            {
                _player.GlobalPosition = new Vector3(side*3, .2f, z); _player.Velocity = Vector3.Zero;
                await Frames(8);
                bool passed = await MoveTo(new Vector3(side*20, 0, z), 330);
                Check(passed, $"room at x={side*11} z={z} connects spine to outer loop");
            }
        }
        await Shot("station-arrival", new Vector3(0,2,18), new Vector3(0,1.8f,0));
        await Shot("station-reactor", new Vector3(-14,2,-8), new Vector3(-9,1.6f,-14));
    }

    private async Task NativeStation()
    {
        using var fixture = new GameHttpFixture { Mode = 0, Role = 0 };
        var session = GameSession.Create(new ApiClient(fixture.Url), "fixture"); AddChild(session);
        bool host = false;
        int players = 3;
        var controller = MinigameWorld.Create(session, _world, () => _player, () => host, () => players,
            () => new Dictionary<uint,string>(), () => _peers, id => id == "me" ? "You" : id,
            pos => _player.GlobalPosition = pos, _ => {}, (_,_)=>{});
        AddChild(controller);
        var start = Walk(_world).OfType<GameActionPoint>().Single(p=>p.PromptText.StartsWith("Need"));
        Check(!start.CanInteract,"non-host cannot start at the console");
        host = true;
        Check(!start.CanInteract,"host cannot start with fewer than four players");
        players = 4;
        Check(start.CanInteract,"host can start with four players");
        _player.GlobalPosition = new Vector3(start.GlobalPosition.X, .2f, start.GlobalPosition.Z + 1.5f);
        var ctx = new InteractionContext(InteractSource.Desktop,_player,_player,_player.GlobalPosition,
            Vector3.Forward,_player.GlobalPosition,false);
        start.Interact(in ctx);
        await Until(()=>session.HasSession,240);
        Check(session.HasSession,"interacting with the world console starts a session");
        var task = Walk(_world).OfType<GameActionPoint>().First(p=>p.PromptText.StartsWith("Repair "));
        Check(task.CanInteract,"crew sees a task affordance");
        _player.GlobalPosition=new Vector3(task.GlobalPosition.X,.1f,task.GlobalPosition.Z+1);
        task.Interact(in ctx);
        await Frames(60);
        Check(fixture.LastTask < 0, "repair requires time at the console, not an instant click");
        _player.GlobalPosition += Vector3.Right*6;await Frames(25);
        Check(fixture.LastTask < 0, "leaving a console cancels the repair");
        _player.GlobalPosition=new Vector3(task.GlobalPosition.X,.1f,task.GlobalPosition.Z+1);
        task.Interact(in ctx);
        await Until(()=>session.CompletedTasks.Count>0,600);
        Check(!task.CanInteract && fixture.LastTask>=0,"completed console becomes unavailable and reaches API");
        var vent = Walk(_world).OfType<GameActionPoint>().First(p=>p.PromptText=="Use vent");
        Check(!vent.CanInteract,"crew cannot use the shortcut vents");
        fixture.Role=1;
        await Until(()=>session.MyRole==GameRole.Imposter,240);
        Check(vent.CanInteract,"imposter can use a vent");
        _player.GlobalPosition=vent.GlobalPosition;
        Vector3 before=_player.GlobalPosition;
        vent.Interact(in ctx);
        Check(_player.GlobalPosition.DistanceTo(before)>10,"vent interaction teleports to its paired room");
        var meeting=Walk(_world).OfType<GameActionPoint>().Single(p=>p.PromptText=="Call emergency meeting");
        _player.GlobalPosition=meeting.GlobalPosition+Vector3.Back;
        meeting.Interact(in ctx);
        await Until(()=>session.Phase==GamePhase.Meeting,600);
        Check(session.Phase==GamePhase.Meeting && _player.GlobalPosition.Length()<8,"meeting gathers players at the central mess");
        Check(!vent.CanInteract,"vents close during discussion");
        await session.TryCloseMeeting();await Until(()=>session.Phase==GamePhase.Playing,600);
        Check(session.Phase==GamePhase.Playing,"closing the meeting resumes ship play");
        await session.TryAbort();await Until(()=>session.Phase==GamePhase.Ended,600);await Frames(470);
        var lobby=Walk(_world).OfType<Node3D>().Single(n=>n.Name=="SERIKA_GAME_LOBBY");
        Check(_player.GlobalPosition.DistanceTo(lobby.GlobalPosition)<5,"ship result returns the crew to the launch lounge");
        Press(start);await Until(()=>session.Phase==GamePhase.Playing,600);
        Check(session.Phase==GamePhase.Playing,"ship physical button starts a new flight");
        controller.QueueFree();session.QueueFree();await Frames(3);
    }

    private MinigameWorld MakeController(GameSession session, Func<bool> host = null, Func<int> count = null)
    {
        var controller = MinigameWorld.Create(session, _world, () => _player, host ?? (() => true), count ?? (() => 1),
            () => new Dictionary<uint,string>(), () => _peers, id => id == "me" ? "You" : id,
            pos => _player.GlobalPosition = pos, _ => {}, (_,_)=>{});
        AddChild(controller); return controller;
    }

    private void Press(GameActionPoint button, InteractSource source = InteractSource.Desktop)
    {
        _player.GlobalPosition = new Vector3(button.GlobalPosition.X, .2f, button.GlobalPosition.Z + 1.5f);
        _player.Velocity = Vector3.Zero;
        var ctx = new InteractionContext(source, _player, _player, _player.GlobalPosition+Vector3.Up,
            Vector3.Forward, button.GlobalPosition, source == InteractSource.VrLeft);
        button.Interact(in ctx);
    }

    private async Task Gauntlet()
    {
        var movers = Walk(_world).OfType<AnimatableBody3D>().ToArray();
        Check(movers.Length == 16, "sixteen hazards across three arenas have moving collision");
        Check(Walk(_world).Count(n=>n.Name.ToString().StartsWith("SERIKA_GAME_FINISH"))==3, "three separate finish volumes");
        var pivot = Walk(_world).OfType<Node3D>().First(n=>n.Name=="SERIKA_SNODE14");
        var before = pivot.GlobalPosition; await Frames(35);
        Check(before.DistanceTo(pivot.GlobalPosition)>.01f, "tidal platforms animate in the first arena");
        var monitor=Walk(_world).OfType<CheckpointMonitor>().Single();
        var pads=Walk(_world).OfType<CheckpointNode>().ToArray();
        monitor.Configure(pads.Single(p=>p.Index==50),50,53); monitor.Enabled=true;
        _player.GlobalPosition = new Vector3(-70,.2f,-35); _player.Velocity=Vector3.Zero; await Frames(8);
        for(int i=0;i<5;i++)
        {
            float x=i==4?-71:i%2==0?-69:-71;
            float takeoff=i==0?-38.3f:-(41+(i-1)*6+1.95f);
            Check(await MoveTo(new Vector3(x,0,takeoff),160), $"tidal jump {i+1} takeoff reachable");
            float landing=i==4?-63:-(41+i*6-1.2f);
            _jump=true;
            bool landed=await MoveTo(new Vector3(x,0,landing),120,true);
            Check(landed && _player.GlobalPosition.Y>-.2f, $"tidal jump {i+1} lands at normal walk speed");
            if(!landed)break;
            await Frames(25);
        }
        // The finale is a real connected ascent, including both split ramps.
        monitor.Enabled=false;
        foreach(float side in new[]{-1f,1f})
        {
            _player.GlobalPosition=new Vector3(70+side*7,.2f,-4);_player.Velocity=Vector3.Zero;await Frames(10);
            foreach(var target in new[]{new Vector3(70+side*7,2.5f,-23),new Vector3(70+side*7,5,-46),
                new Vector3(77,5,-51),new Vector3(77,8,-70),new Vector3(74,10,-89)})
                Check(await MoveTo(target,700, hopHazards: true), $"crown route {side} reaches elevation {target.Y} at {target.Z}");
        }
        // Drive the physical button, stage selection, checkpoint filtering and replay through HTTP.
        await CourseFlow(GameModeKind.Gauntlet);
    }

    private async Task Rope()
    {
        Check(Walk(_world).OfType<RopeTeam>().Count()==1, "canyon enables native rope physics");
        var points=Walk(_world).OfType<Node3D>().Where(n=>n.Name.ToString().StartsWith("SERIKA_ROUTE1_"))
            .OrderBy(n=>int.Parse(n.Name.ToString().Split('_')[^1])).ToArray();
        Check(points.Length>=28, "canyon has four varied sections and a complete route");
        var monitor=Walk(_world).OfType<CheckpointMonitor>().Single();
        monitor.Enabled=false;
        _player.GlobalPosition=points[0].GlobalPosition;_player.Velocity=Vector3.Zero;await Frames(15);
        for(int i=1;i<points.Length;i++)
        {
            var previous=points[i-1].GlobalPosition;var target=points[i].GlobalPosition;
            bool jump=target.Y>previous.Y+.15f;
            if(jump)
            {
                var flat=target-previous;flat.Y=0;
                var direction=flat.Normalized();
                float edge=0;
                for(float distance=.15f;distance<5;distance+=.15f)
                {
                    var probe=previous+direction*distance;
                    var hit=GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(
                        probe+Vector3.Up*.4f,probe-Vector3.Up*.4f,PhysicsLayers.World));
                    if(hit.Count==0)break;
                    edge=distance;
                }
                var takeoff=previous+direction*Math.Max(0,edge-.4f);
                Check(await MoveTo(takeoff,120), $"canyon takeoff {i} reachable");
                _jump=true;
            }
            bool reached=await MoveTo(target,240,jump);await Frames(12);
            Check(reached && _player.IsOnFloor() && _player.GlobalPosition.Y>target.Y-.3f, $"canyon segment {i} is traversable and solid");
            if(!reached)break;
        }
        await CourseFlow(GameModeKind.Rope);
    }

    private async Task CourseFlow(GameModeKind mode)
    {
        using var fixture=new GameHttpFixture {Mode=(int)mode,Count=1};
        var session=GameSession.Create(new ApiClient(fixture.Url),"fixture",mode);AddChild(session);
        fixture.RoundStarted=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+60000;
        var controller=MakeController(session);
        var button=Walk(_world).OfType<GameActionPoint>().Single(p=>p.PromptText.StartsWith("Start"));
        Check(button.CanInteract, $"{mode}: physical lobby button permits solo practice");
        var lobby=Walk(_world).OfType<Node3D>().Single(n=>n.Name=="SERIKA_GAME_LOBBY");
        var monitor=Walk(_world).OfType<CheckpointMonitor>().Single();
        Check(!monitor.Enabled, $"{mode}: lobby does not activate course checkpoints");
        Press(button, mode==GameModeKind.Rope?InteractSource.VrLeft:InteractSource.Desktop);
        await Until(()=>session.HasSession,600);
        Check(session.HasSession && session.Mode==mode, $"{mode}: physical start reaches correct API mode");
        await Frames(20);
        Check(InputMode.HasHold("game-countdown") && !monitor.Enabled, $"{mode}: countdown holds movement and checkpoints");
        fixture.RoundStarted=fixture.Started;
        await Until(()=>session.RoundStartedAt==fixture.Started,600);await Frames(20);
        Check(!InputMode.HasHold("game-countdown") && monitor.Enabled, $"{mode}: countdown releases the course");
        int rounds=mode==GameModeKind.Gauntlet?3:1;
        for(int round=1;round<=rounds;round++)
        {
            await Until(()=>session.Round==round,600);await Frames(15);
            int stage=mode==GameModeKind.Gauntlet?round:1;
            var start=Walk(_world).OfType<Node3D>().Single(n=>n.Name== $"SERIKA_GAME_SPAWN{stage}");
            Check(_player.GlobalPosition.DistanceTo(start.GlobalPosition)<5, $"{mode}: round {round} deploys to its own arena");
            var finish=Walk(_world).OfType<Node3D>().Single(n=>n.Name==$"SERIKA_GAME_FINISH{stage}");
            int reports=fixture.FinishReports;
            _player.GlobalPosition=finish.GlobalPosition;_player.Velocity=Vector3.Zero;await Frames(35);
            Check(fixture.FinishReports==reports, $"{mode}: finish rejects skipped checkpoints in round {round}");
            var checkpoints=Walk(_world).OfType<CheckpointNode>().Where(p=>p.Index>=stage*50 && p.Index<stage*50+50).OrderBy(p=>p.Index).ToArray();
            foreach(var pad in checkpoints)
            { _player.GlobalPosition=pad.RespawnPoint;_player.Velocity=Vector3.Zero;await Frames(18); }
            Check(monitor.Current==checkpoints[^1], $"{mode}: active stage owns recovery");
            // A previous arena's checkpoint cannot steal the new round's respawn.
            foreach(var old in Walk(_world).OfType<CheckpointNode>().Where(p=>p.Index<stage*50)) monitor.NotifyReached(old);
            Check(monitor.Current==checkpoints[^1], $"{mode}: previous-stage checkpoints are ignored");
            _player.GlobalPosition=finish.GlobalPosition;_player.Velocity=Vector3.Zero;
            await Until(()=>fixture.FinishReports>reports,600);
            Check(fixture.FinishReports==reports+1 && fixture.LastFinishRound==round && fixture.LastFinishStart==session.StartedAt,
                $"{mode}: crossing finish submits this match and round exactly once");
            if(mode==GameModeKind.Gauntlet) await Until(()=>session.Round>round || session.Phase==GamePhase.Ended,900);
        }
        await Until(()=>session.Phase==GamePhase.Ended,600);
        Check(session.Phase==GamePhase.Ended, $"{mode}: match reaches results");
        await Frames(470);
        Check(_player.GlobalPosition.DistanceTo(lobby.GlobalPosition)<5, $"{mode}: results return player to lobby");
        Press(button);await Until(()=>session.Phase==GamePhase.Playing && session.Round==1,600);
        Check(session.Phase==GamePhase.Playing && session.Round==1, $"{mode}: physical button starts another game");
        await session.TryAbort();await Until(()=>session.Phase==GamePhase.Ended,600);
        controller.QueueFree();session.QueueFree();await Frames(3);
    }

    private sealed class RopeRoster : IScriptPlayers
    {
        public int Count => 2;
        public Vector3 Remote;
        public bool TryGetPosition(int index, out Vector3 position)
        { position = Remote; return index == 1; }
        public Node3D GetAttachTarget(int index, ScriptAttachPoint point) => null;
        public int IndexOfBody(Node body) => -1;
    }

    private void RopeRecovery()
    {
        var body = new CharacterBody3D(); AddChild(body);
        body.GlobalPosition = new Vector3(100,100,100);
        var roster = new RopeRoster { Remote = body.GlobalPosition + Vector3.Right*5 };
        var rope = RopeTeam.Create(roster, () => body, 8); AddChild(rope);
        rope.SetPhysicsProcess(false);
        rope._PhysicsProcess(1.0/60);
        roster.Remote += Vector3.Right*3.5f;
        rope._PhysicsProcess(1.0/60);
        Check(body.GlobalPosition.X > 100, "taut native rope pulls toward teammate");
        body.GlobalPosition -= Vector3.Up*28;
        var restart = body.GlobalPosition;
        for(int i=0;i<60;i++) rope._PhysicsProcess(1.0/60);
        Check(body.GlobalPosition.IsEqualApprox(restart), "summit restart releases rope until regrouped");
        roster.Remote = body.GlobalPosition + Vector3.Right*5;
        rope._PhysicsProcess(1.0/60);
        roster.Remote += Vector3.Right*3.5f;
        rope._PhysicsProcess(1.0/60);
        Check(body.GlobalPosition.X > restart.X, "rope reconnects after regrouping");
        roster.Remote += Vector3.Up*20;
        var before = body.GlobalPosition;
        for(int i=0;i<60;i++) rope._PhysicsProcess(1.0/60);
        Check(body.GlobalPosition.IsEqualApprox(before), "teammate teleport cannot drag local player");
        rope.QueueFree(); body.QueueFree();
    }

    private async Task<bool> MoveTo(Vector3 target, int frames, bool airborne=false, bool hopHazards=false)
    {
        bool leftFloor = !airborne;
        float lowest = Math.Min(target.Y, _player.GlobalPosition.Y) - 2;
        for (int i=0;i<frames;i++)
        {
            Vector3 offset = target - _player.GlobalPosition;
            var flat = new Vector2(offset.X,offset.Z);
            if (!_player.IsOnFloor()) leftFloor = true;
            if (hopHazards && _player.IsOnFloor()) _jump = true;
            _move = flat.Length() > .12f ? flat.Normalized() : Vector2.Zero;
            if (flat.Length() < .15f && (!airborne || (leftFloor && _player.IsOnFloor()))) { _move = Vector2.Zero; return true; }
            if (_player.GlobalPosition.Y < lowest) break;
            await Frames(1);
        }
        _move = Vector2.Zero;
        GD.Print($"MINIGAME position {_player.GlobalPosition}, wanted {target}");
        return false;
    }

    private async Task Shot(string name, Vector3 eye, Vector3 target)
    {
        if (_shots == null) return;
        var camera = new Camera3D { Fov=70 }; _world.AddChild(camera);
        camera.GlobalPosition=eye; camera.LookAt(target); camera.Current=true;
        await Frames(12); await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        GetViewport().GetTexture().GetImage().SavePng(Path.Combine(_shots,name+".png"));
        camera.QueueFree();
    }

    // HTTP error bodies used to be accepted as a successful lobby and silently reset mode to
    // Imposter. Exercise the shipping ApiClient and GameSession, including a repeated role.
    private async Task SessionBoundary()
    {
        using var fixture = new GameHttpFixture();
        var session = GameSession.Create(new ApiClient(fixture.Url), "fixture", GameModeKind.Gauntlet);
        AddChild(session);
        await Until(()=>fixture.Polls>0 && !session.HasSession, 120);
        Check(!session.HasSession && session.Mode==GameModeKind.Gauntlet, "no_session keeps the selected lobby mode");
        Check(await session.TryStart(GameModeKind.Gauntlet), "host action reaches HTTP start");
        await Until(()=>session.HasSession, 240);
        Check(session.Alive && session.Round==1, "session reads authenticated player and round");
        Check(await session.TryFinish() && session.Place==1, "finish uses server-assigned place");
        Check(await session.TryEndRound(), "round close reaches API");
        await Until(()=>session.Round==2, 240);
        Check(session.Place==0, "new round clears the old finish place");
        fixture.Role=0; fixture.Mode=0; fixture.Started++;
        await Until(()=>session.Mode==GameModeKind.Imposter, 240);
        Check(await session.TryCompleteTask(3) && session.CompletedTasks.Contains(3) && fixture.LastTask==3,
            "task request carries the specific console id");
        session.QueueFree(); await Frames(3);
    }

    private async Task Until(Func<bool> condition, int frames)
    { for(int i=0;i<frames && !condition();i++) await Frames(1); }
    private async Task Frames(int n) { for(int i=0;i<n;i++) await ToSignal(GetTree(),SceneTree.SignalName.PhysicsFrame); }
    private void Check(bool ok,string name) { _checks++; if(!ok)_failed++; GD.Print($"MINIGAME {(ok?"PASS":"FAIL")}: {name}"); }
    private static IEnumerable<Node> Walk(Node n)
    { foreach(var c in n.GetChildren()) { yield return c; foreach(var d in Walk(c))yield return d; } }
}
