using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SerikaSocial.World;
using SerikaSocial.Player;
using SerikaSocial.UI;

namespace SerikaSocial.Game;

/// First-party game affordances. The API owns outcomes; authored markers own placement.
/// No role, kill, vote or finish ever comes from an untrusted script message.
public partial class MinigameWorld : Node
{
    private GameSession _session;
    private Node3D _world;
    private Func<CharacterBody3D> _body;
    private Func<bool> _host;
    private Func<int> _count;
    private Func<Dictionary<uint, string>> _ids;
    private Func<Dictionary<uint, RemoteAvatar>> _peers;
    private Func<string, string> _name;
    private Action<Vector3> _teleport;
    private Action<string> _toast;
    private Action<string, bool> _menu;
    private readonly List<GameActionPoint> _actions = new();
    private readonly List<Label3D> _boards = new();
    private readonly List<CheckpointMonitor> _monitors = new();
    private readonly Dictionary<string, GameActionPoint> _targets = new();
    private readonly Dictionary<string, Node3D> _markers = new();
    private readonly Dictionary<int, Node3D> _lamps = new();
    private Node3D _finish, _start, _spectator, _lobby, _buttonCap;
    private Vector3 _buttonHome;
    private readonly List<CheckpointNode> _pads = new();
    private readonly List<RopeTeam> _ropes = new();
    private int _baseCheckpoint, _requiredCheckpoint, _repairTask = -1;
    private double _repairTime, _returnTime, _pressTime;
    private bool _returned;
    private GamePhase? _lastPhase;
    private int _checkpoint, _round;
    private long _started;
    private double _timer, _retry, _finishWait;
    private bool _disposed;
    private readonly Dictionary<GeometryInstance3D, float> _ghostMeshes = new();
    private readonly List<GameActionPoint> _bodies = new();

    public static MinigameWorld Create(GameSession session, Node3D world,
        Func<CharacterBody3D> body, Func<bool> host, Func<int> count,
        Func<Dictionary<uint, string>> ids, Func<Dictionary<uint, RemoteAvatar>> peers,
        Func<string, string> name, Action<Vector3> teleport, Action<string> toast,
        Action<string, bool> menu) => new()
        {
            Name = "MinigameWorld", _session = session, _world = world, _body = body,
            _host = host, _count = count, _ids = ids, _peers = peers,
            _name = name, _teleport = teleport, _toast = toast, _menu = menu,
        };

    private bool Playing => _session.HasSession && _session.Phase == GamePhase.Playing && _session.Alive;
    private int Minimum => _session.Mode == GameModeKind.Imposter ? 4 : 1;
    private bool Course => _session.Mode != GameModeKind.Imposter;
    private string Title => _session.Mode switch { GameModeKind.Imposter => "S.S. ASTER", GameModeKind.Rope => "CANYON CREW", _ => "THE GAUNTLET" };
    private string StageTitle => _session.Mode == GameModeKind.Rope ? "CANYON EXPEDITION" : _session.Round switch
    { 1 => "TIDAL DASH", 2 => "ROTOR RUN", _ => "CROWN CLIMB" };
    private double Countdown => (_session.RoundStartedAt + 3000 - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 1000.0;
    private bool Lobby => !_session.HasSession || _session.Phase is GamePhase.Lobby or GamePhase.Ended;
    private string HostLabel => Lobby
        ? (_count() < Minimum ? $"Need {Minimum} players · {_count()} here" : $"Start {_session.Mode}")
        : _session.Phase == GamePhase.Meeting ? "Close meeting" :
        _session.Mode == GameModeKind.Gauntlet && _session.FinishedCount > 0 ? "Advance race round" : "End game / return to lobby";
    private bool HostEnabled => !_session.Busy && _host() && (Lobby ? _count() >= Minimum :
        _session.Phase == GamePhase.Meeting ? _session.MeetingSecondsLeft <= 0 :
        _session.Phase == GamePhase.Playing);

    public override void _Ready()
    {
        foreach (var node in Walk(_world).ToArray())
        {
            if (node is CheckpointMonitor monitor) _monitors.Add(monitor);
            if (node is CheckpointNode pad) { _pads.Add(pad); pad.Reached += OnCheckpoint; }
            if (node is RopeTeam rope) { _ropes.Add(rope); rope.Enabled = false; rope.IncludePeer = IncludeRopePeer; }
            if (node is not Node3D marker) continue;
            string name = marker.Name.ToString();
            if (name.StartsWith("SERIKA_GAME_")) _markers[name] = marker;
            if (name.StartsWith("SERIKA_SNODE") && WorldLoader.TryParseMarkerIndex(name, "SERIKA_SNODE", out int slot)
                && slot >= 6 && slot <= 15) _lamps[slot - 6] = marker;
        }
        _markers.TryGetValue("SERIKA_GAME_SPAWN", out _start);
        _markers.TryGetValue("SERIKA_GAME_FINISH", out _finish);
        _markers.TryGetValue("SERIKA_GAME_SPECTATOR", out _spectator);
        _markers.TryGetValue("SERIKA_GAME_LOBBY", out _lobby);
        _markers.TryGetValue("SERIKA_GAME_STARTCAP", out _buttonCap);
        if (_buttonCap != null) _buttonHome = _buttonCap.Position;
        foreach (var monitor in _monitors) monitor.Enabled = false;
        foreach (var (name, marker) in _markers)
        {
            if (name == "SERIKA_GAME_START")
            {
                AddAction(marker, () => HostLabel, () => HostEnabled, HostAction);
                if (!_markers.ContainsKey("SERIKA_GAME_BOARD")) AddBoard(marker, new Vector3(0, 1.3f, 0));
            }
            else if (name.StartsWith("SERIKA_GAME_BOARD")) AddBoard(marker, Vector3.Zero);
            else if (name == "SERIKA_GAME_MEETING")
                AddAction(marker, () => "Call emergency meeting", () => Playing && Countdown <= 0 && !_session.Busy, () => _ = _session.TryReport());
            else if (WorldLoader.TryParseMarkerIndex(name, "SERIKA_GAME_TASK", out int id))
            {
                AddAction(marker, () => _repairTask == id ? $"Repairing {RoomName(id)} · {Math.Min(100, (int)(_repairTime / 3 * 100))}%" : $"Repair {RoomName(id)} · stay nearby for 3s",
                    () => Playing && Countdown <= 0 && !_session.Busy && (_repairTask < 0 || _repairTask == id) &&
                    _session.MyRole == GameRole.Crew && !_session.CompletedTasks.Contains(id), () => BeginTask(id));
            }
            else if (WorldLoader.TryParseMarkerIndex(name, "SERIKA_GAME_VENT", out int vent))
            {
                string partner = $"SERIKA_GAME_VENT{(vent < 10 ? vent + 10 : vent - 10)}";
                if (_markers.TryGetValue(partner, out var destination))
                    AddAction(marker, () => "Use vent", () => Playing && Countdown <= 0 && _session.MyRole == GameRole.Imposter,
                        () => Teleport(destination.GlobalPosition + Vector3.Up * .5f));
            }
        }
        _session.Updated += OnUpdated;
        _session.GameEvent += OnGameEvent;
    }

    private static string RoomName(int id) => id switch
    { 0 => "Reactor", 1 => "Medbay", 2 => "Comms", 3 => "Storage", 4 => "Galley", 5 => "Engines", 6 => "Electrical", 7 => "Hydroponics", 8 => "Security", 9 => "Laboratory", _ => "station" };

    private bool IncludeRopePeer(int index)
    {
        var order = _peers().Keys.OrderBy(id => id).ToArray();
        return index > 0 && index <= order.Length && _ids().TryGetValue(order[index - 1], out var user) &&
            _session.AlivePlayers.Contains(user);
    }

    private void BeginTask(int id)
    {
        if (_repairTask >= 0) return;
        _repairTask = id; _repairTime = 0;
        _toast($"Repairing {RoomName(id)} — stay by the console");
    }

    private async void CompleteTask(int id)
    {
        bool ok = await _session.TryCompleteTask(id);
        if (ok && !_disposed) { _toast($"{RoomName(id)} complete"); UpdateLamps(); }
    }

    public async void HostAction()
    {
        if (!HostEnabled) return;
        _pressTime = .35;
        if (Lobby) await _session.TryStart(_session.Mode);
        else if (_session.Phase == GamePhase.Meeting) await _session.TryCloseMeeting();
        else if (_session.Mode == GameModeKind.Gauntlet && _session.FinishedCount > 0) await _session.TryEndRound();
        else await _session.TryAbort();
    }

    private void Teleport(Vector3 pos)
    {
        var body = _body();
        if (body == null) return;
        _teleport(pos);
        body.Velocity = Vector3.Zero;
        WorldLoader.RaiseCheckpointReached(pos);
    }

    private void OnCheckpoint(CheckpointNode pad)
    {
        if (Playing && Course && Countdown <= 0 && pad.Index == _checkpoint + 1 && pad.Index <= _requiredCheckpoint)
            _checkpoint = pad.Index;
    }

    private void OnGameEvent(string type, System.Text.Json.JsonElement data)
    {
        if (type != "died" || !data.TryGetProperty("userId", out var user)) return;
        string id = user.GetString();
        foreach (var (peerId, uid) in _ids())
        {
            if (uid != id || !_peers().TryGetValue(peerId, out var peer)) continue;
            var point = new GameActionPoint { Label = () => $"Report {_name(id)}",
                Available = () => !_disposed && Playing && !_session.Busy,
                Use = () => _ = _session.TryReport() };
            _world.AddChild(point);
            point.GlobalPosition = peer.GlobalPosition + Vector3.Up * .3f;
            var label = new Label3D { Text = $"{_name(id)}\nDOWN · REPORT", FontSize = 40, PixelSize = .003f,
                Modulate = Brand.Danger, Billboard = BaseMaterial3D.BillboardModeEnum.FixedY };
            point.AddChild(label);
            _actions.Add(point);
            _bodies.Add(point);
            break;
        }
    }

    private void SetGhost(Node root, bool ghost)
    {
        foreach (var mesh in Walk(root).OfType<GeometryInstance3D>())
        {
            if (ghost)
            {
                if (!_ghostMeshes.ContainsKey(mesh)) _ghostMeshes[mesh] = mesh.Transparency;
                mesh.Transparency = .65f;
            }
            else if (_ghostMeshes.Remove(mesh, out float original)) mesh.Transparency = original;
        }
    }

    private void OnUpdated()
    {
        if (_disposed) return;
        bool newRound = _started != _session.StartedAt || _round != _session.Round;
        if (newRound)
        {
            _started = _session.StartedAt;
            _round = _session.Round;
            _finishWait = 0; _repairTask = -1; _returned = false; _returnTime = 0;
            foreach (var body in _bodies) if (GodotObject.IsInstanceValid(body)) body.QueueFree();
            _bodies.Clear();
            int stage = _session.Mode == GameModeKind.Gauntlet ? Math.Clamp(_session.Round, 1, 3) : 1;
            bool stagedMap = _markers.TryGetValue($"SERIKA_GAME_SPAWN{stage}", out var stageStart);
            if (stagedMap)
            {
                _start = stageStart;
                _markers.TryGetValue($"SERIKA_GAME_FINISH{stage}", out _finish);
                _markers.TryGetValue($"SERIKA_GAME_SPECTATOR{stage}", out _spectator);
            }
            _baseCheckpoint = stagedMap ? stage * 50 : 0;
            _checkpoint = _baseCheckpoint;
            var stagePads = _pads.Where(p => p.Index >= _baseCheckpoint && p.Index < _baseCheckpoint + 50).ToArray();
            _requiredCheckpoint = stagePads.Length == 0 ? _baseCheckpoint : stagePads.Max(p => p.Index);
            foreach (var monitor in _monitors)
                monitor.Configure(stagePads.FirstOrDefault(p => p.Index == _baseCheckpoint), _baseCheckpoint, _requiredCheckpoint);
            if (_start != null && _session.Phase != GamePhase.Ended)
            {
                var destination = !_session.Alive && _spectator != null ? _spectator : _start;
                Teleport(GridPosition(destination, _session.Alive));
                _toast(Course ? $"{StageTitle} — get ready" : "Airlock released — check your role");
            }
        }
        if (_session.Phase == GamePhase.Meeting && _lastPhase != GamePhase.Meeting &&
            _markers.TryGetValue("SERIKA_GAME_MEETINGSPAWN", out var meeting)) Teleport(GridPosition(meeting, true));
        if (_session.Phase == GamePhase.Ended && _lastPhase != GamePhase.Ended) { _returnTime = 0; _returned = false; }
        _lastPhase = _session.Phase;
        bool live = _session.HasSession && _session.Phase != GamePhase.Ended;
        var local = _body();
        if (local != null) SetGhost(local, live && !_session.Alive);
        foreach (var (peerId, id) in _ids())
            if (_peers().TryGetValue(peerId, out var peer)) SetGhost(peer, live && !_session.AlivePlayers.Contains(id));
        if (_session.Phase == GamePhase.Meeting)
        {
            foreach (var body in _bodies) if (GodotObject.IsInstanceValid(body)) body.QueueFree();
            _bodies.Clear();
        }
        UpdateLamps();
        UpdateTargets();
    }

    private Vector3 GridPosition(Node3D destination, bool spread)
    {
        var position = destination.GlobalPosition;
        if (spread)
        {
            var roster = _session.AlivePlayers.OrderBy(id => id, StringComparer.Ordinal).ToArray();
            int slot = Array.FindIndex(roster, id => _name(id) == "You");
            if (slot >= 0) position += new Vector3((slot % 4 - 1.5f) * 1.5f, 0, slot / 4 * 1.5f);
        }
        return position;
    }

    private void UpdateLamps()
    {
        if (_session.Mode != GameModeKind.Imposter) return;
        foreach (var (id, lamp) in _lamps) lamp.Visible = _session.CompletedTasks.Contains(id);
    }

    private void UpdateTargets()
    {
        if (_session.Mode != GameModeKind.Imposter) return;
        foreach (var (peerId, userId) in _ids())
        {
            if (_targets.ContainsKey(userId) || !_peers().TryGetValue(peerId, out var peer)) continue;
            var point = new GameActionPoint { Name = "PlayerGameAction", Position = Vector3.Up };
            point.Label = () => $"Kill {_name(userId)}";
            point.Available = () => !_disposed && Countdown <= 0 && _session.CanAttemptKill && !_session.Busy &&
                _session.AlivePlayers.Contains(userId) && GodotObject.IsInstanceValid(peer) &&
                _body() != null && _body().GlobalPosition.DistanceTo(peer.GlobalPosition) <= 3;
            point.Use = () => _ = _session.TryKill(userId, _body().GlobalPosition.DistanceTo(peer.GlobalPosition));
            peer.AddChild(point);
            _targets[userId] = point;
            _actions.Add(point);
        }
        foreach (var id in _targets.Where(pair => !GodotObject.IsInstanceValid(pair.Value)).Select(pair => pair.Key).ToArray())
            _targets.Remove(id);
    }

    public override void _Process(double delta)
    {
        if (_disposed) return;
        _pressTime = Math.Max(0, _pressTime - delta);
        if (_buttonCap != null) _buttonCap.Position = _buttonHome + Vector3.Down * (_pressTime > 0 ? .07f : 0);
        _timer -= delta; _retry -= delta;
        if (_timer > 0) return;
        _timer = .2;
        double countdown = Countdown;
        bool staged = Playing && countdown > 0;
        if (staged) InputMode.Hold("game-countdown", freeCursor: false);
        else InputMode.Release("game-countdown");
        foreach (var monitor in _monitors) monitor.Enabled = Course && Playing && !staged;
        foreach (var rope in _ropes) rope.Enabled = _session.Mode == GameModeKind.Rope && Playing && !staged;
        _menu(_host() ? HostLabel : null, HostEnabled);
        string text = Lobby ? $"{Title} / LOBBY\n{_count()} here · {Minimum}+ to play\n{(_host() ? "PRESS THE GREEN START BUTTON" : "Waiting for the instance host")}" :
            _session.Phase == GamePhase.Meeting ? $"MEETING · {Math.Ceiling(_session.MeetingSecondsLeft)}s\nVote on the meeting panel" :
            Course ? $"{StageTitle}\n{_checkpoint - _baseCheckpoint} / {_requiredCheckpoint - _baseCheckpoint} checkpoints\n{_session.FinishedCount} / {_session.AliveCount} finished" :
            $"SHIP IN FLIGHT\n{_session.AliveCount} alive\nRepair 5 of 10 systems / report in the mess";
        if (staged) text = $"{(Course ? StageTitle : "CREW DEPLOYING")}\nGET READY · {Math.Ceiling(countdown)}";
        if (_session.HasSession && _session.Phase == GamePhase.Ended && !_returned)
        {
            _returnTime += .2;
            text = $"GAME COMPLETE\nReturning to lobby in {Math.Max(0, Math.Ceiling(7 - _returnTime))}s";
            if (_returnTime >= 7)
            {
                _returned = true;
                if (_lobby != null) Teleport(GridPosition(_lobby, true));
            }
        }
        foreach (var board in _boards) if (board.Text != text) board.Text = text;
        if (_repairTask >= 0)
        {
            int task = _repairTask;
            var target = _markers[$"SERIKA_GAME_TASK{task}"];
            var player = _body();
            if (!Playing || _session.MyRole != GameRole.Crew || player == null || player.GlobalPosition.DistanceTo(target.GlobalPosition) > 3)
            { _repairTask = -1; _toast("Repair cancelled — stay by the console"); }
            else
            {
                _repairTime += .2;
                if (_repairTime >= 3 && !_session.Busy) { _repairTask = -1; CompleteTask(task); }
            }
        }
        if (_session.Phase == GamePhase.Meeting && _session.MeetingSecondsLeft <= 0 && _retry <= 0)
        { _retry = 3; _ = _session.TryCloseMeeting(); }
        if (!Course || _session.Phase != GamePhase.Playing || staged) return;
        if (_session.Mode == GameModeKind.Gauntlet && _host() && _session.FinishedCount > 0)
        {
            _finishWait += .2;
            if ((_finishWait >= 30 || _session.FinishedCount >= _session.AliveCount) && _retry <= 0)
            { _retry = 3; _ = _session.TryEndRound(); }
        }
        var body = _body();
        if (_session.Alive && _finish != null && body != null && _checkpoint >= _requiredCheckpoint &&
            _requiredCheckpoint > _baseCheckpoint && _session.Place == 0 && _retry <= 0)
        {
            var local = _finish.ToLocal(body.GlobalPosition + Vector3.Up * .8f);
            if (Math.Abs(local.X) <= .5f && Math.Abs(local.Y) <= .5f && Math.Abs(local.Z) <= .5f)
            { _retry = 2; _ = Finish(); }
        }
    }

    private async System.Threading.Tasks.Task Finish()
    {
        if (await _session.TryFinish() && !_disposed) _toast(_session.Mode == GameModeKind.Rope ? "Summit reached — waiting for the rest of the crew" : $"Finished #{_session.Place}!");
    }

    private void AddAction(Node3D marker, Func<string> label, Func<bool> enabled, Action use)
    {
        var point = new GameActionPoint { Label = label, Available = enabled, Use = use };
        _world.AddChild(point);
        point.GlobalPosition = marker.GlobalPosition;
        _actions.Add(point);
    }

    private void AddBoard(Node3D marker, Vector3 offset)
    {
        var label = new Label3D { FontSize = 60, PixelSize = .0045f, OutlineSize = 8,
            Modulate = Brand.TextHi, Billboard = BaseMaterial3D.BillboardModeEnum.FixedY,
            NoDepthTest = false, Text = "Lobby" };
        _world.AddChild(label);
        label.GlobalPosition = marker.GlobalPosition + offset;
        _boards.Add(label);
    }

    public override void _ExitTree()
    {
        _disposed = true;
        InputMode.Release("game-countdown");
        _session.Updated -= OnUpdated;
        _session.GameEvent -= OnGameEvent;
        foreach (var (mesh, transparency) in _ghostMeshes)
            if (GodotObject.IsInstanceValid(mesh)) mesh.Transparency = transparency;
        foreach (var action in _actions) if (GodotObject.IsInstanceValid(action)) { action.Available = () => false; action.QueueFree(); }
        foreach (var board in _boards) if (GodotObject.IsInstanceValid(board)) board.QueueFree();
        foreach (var pad in Walk(_world).OfType<CheckpointNode>()) pad.Reached -= OnCheckpoint;
    }

    private static IEnumerable<Node> Walk(Node root)
    {
        if (!GodotObject.IsInstanceValid(root)) yield break;
        foreach (var child in root.GetChildren()) { yield return child; foreach (var desc in Walk(child)) yield return desc; }
    }
}

public partial class GameActionPoint : Node3D, IInteractable
{
    public Func<string> Label;
    public Func<bool> Available;
    public Action Use;
    public string PromptText => Label?.Invoke() ?? "Use";
    public float Range => 3;
    public Vector3 FocusPoint => GlobalPosition;
    public bool CanInteract
    {
        get
        {
            if (!IsInsideTree() || IsQueuedForDeletion() || !(Available?.Invoke() ?? false)) return false;
            var body = WorldLoader.LocalBody?.Invoke();
            return body == null || body.GlobalPosition.DistanceTo(GlobalPosition) > Range + 1 || !Blocked(body);
        }
    }
    private bool Blocked(Node3D body)
    {
        var eye = body.GlobalPosition + Vector3.Up * 1.2f;
        var ray = PhysicsRayQueryParameters3D.Create(eye, FocusPoint, PhysicsLayers.World);
        return GetWorld3D().DirectSpaceState.IntersectRay(ray).Count > 0;
    }
    public override void _Ready() => AddToGroup(Interactable.Group);
    public void Interact(in InteractionContext ctx)
    {
        if (!CanInteract || ctx.Player is not Node3D body || body.GlobalPosition.DistanceTo(GlobalPosition) > Range + 1) return;
        // A nearby target on the other side of a room wall is not reachable.
        if (Blocked(body)) return;
        Use?.Invoke();
    }
}
