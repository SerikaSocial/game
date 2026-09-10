using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Serika.Net;

namespace SerikaSocial.Game;

public enum GameModeKind { Imposter = 0, Gauntlet = 1 }
public enum GamePhase { Lobby = 0, Playing = 1, Meeting = 2, Ended = 3 }
public enum GameRole { Crew = 0, Imposter = 1 }
public enum GameOutcome { None = 0, CrewWin = 1, ImposterWin = 2, GauntletWin = 3, Abandoned = 4 }

/// Client mirror of a server-authoritative game session.
///
/// This holds a CACHE, not the truth. Every value here came from the server and can be stale by
/// up to one poll; the server re-checks every rule on every action regardless. That distinction
/// matters most for `MyRole`: the server tells each player only their own role, and there is
/// deliberately no client-side collection of everyone's roles to be read out of memory.
///
/// Polling rather than pushing is a deliberate trade. The relay's script channel exists and could
/// carry state, but it is client-originated and therefore untrusted — precisely wrong for the one
/// system whose whole purpose is that clients cannot be trusted. Authoritative state comes over
/// HTTP from the API, and the poll interval is the cost of that.
public partial class GameSession : Node
{
    /// Poll cadence while a game is live. Fast enough that a kill or a meeting appears promptly,
    /// slow enough that a full lobby is not hammering the API.
    private const double PollSeconds = 1.5;

    /// Backoff after a failed poll, so an API blip does not become a request storm.
    private const double ErrorBackoffSeconds = 5.0;

    private ApiClient _api;
    private string _instanceId;
    private double _pollTimer;
    private bool _inFlight;
    private int _consecutiveErrors;

    public GameModeKind Mode { get; private set; } = GameModeKind.Imposter;
    public GamePhase Phase { get; private set; } = GamePhase.Lobby;
    public GameOutcome Outcome { get; private set; } = GameOutcome.None;

    /// Null until the server has told us, and null for a spectator. Never populated for anyone
    /// but the local player.
    public GameRole? MyRole { get; private set; }
    public bool Alive { get; private set; } = true;
    public int Tasks { get; private set; }
    public int TasksRequired { get; private set; } = 5;
    public int AliveCount { get; private set; }
    public int TotalPlayers { get; private set; }
    public int Round { get; private set; }
    public int MaxRounds { get; private set; }
    public long MeetingEndsAtMs { get; private set; }

    /// User ids still in play, as last reported.
    public IReadOnlyList<string> AlivePlayers => _alive;
    private List<string> _alive = new();

    /// Unix ms after which we may kill again. 0 = ready.
    public long KillReadyAtMs { get; private set; }

    public bool HasSession { get; private set; }

    /// Raised when the phase changes, so the HUD can react rather than poll our fields.
    public event Action<GamePhase> PhaseChanged;
    /// Raised once when the local player learns their role.
    public event Action<GameRole> RoleRevealed;
    /// (type, payload) from the server's public event log — "died", "ejected", "meeting", …
    public event Action<string, JsonElement> GameEvent;
    public event Action<string> Error;

    private readonly HashSet<string> _seenEvents = new();

    public static GameSession Create(ApiClient api, string instanceId) =>
        new() { Name = "GameSession", _api = api, _instanceId = instanceId };

    public override void _Process(double delta)
    {
        if (_api == null || string.IsNullOrEmpty(_instanceId) || _inFlight) return;
        _pollTimer -= delta;
        if (_pollTimer > 0) return;
        _pollTimer = _consecutiveErrors > 0 ? ErrorBackoffSeconds : PollSeconds;
        _ = PollAsync();
    }

    /// One poll cycle. Everything is wrapped: this runs off the frame loop and an unhandled
    /// exception in an async void would be a logged error on desktop and a dead process on
    /// Android (CLAUDE.md).
    private async Task PollAsync()
    {
        _inFlight = true;
        try
        {
            var state = await _api.GetGameStateAsync(_instanceId);
            var me = await _api.GetGameMeAsync(_instanceId);
            ApplyState(state);
            ApplyMe(me);
            _consecutiveErrors = 0;
            HasSession = true;
        }
        catch (Exception e)
        {
            // "no_session" is the normal answer in a world where nobody started a game — it is
            // not an error worth surfacing, just an absence.
            if (e.Message.Contains("no_session"))
            {
                HasSession = false;
                _consecutiveErrors = 0;
            }
            else if (++_consecutiveErrors <= 2)
            {
                GD.PrintErr($"GameSession: poll failed: {e.Message}");
                Error?.Invoke(e.Message);
            }
        }
        finally
        {
            _inFlight = false;
        }
    }

    private void ApplyState(JsonElement s)
    {
        Mode = (GameModeKind)GetInt(s, "mode");
        Outcome = (GameOutcome)GetInt(s, "outcome");
        AliveCount = GetInt(s, "aliveCount");
        TotalPlayers = GetInt(s, "totalPlayers");
        Round = GetInt(s, "round");
        MaxRounds = GetInt(s, "maxRounds");
        MeetingEndsAtMs = GetLong(s, "meetingEndsAt");

        if (s.TryGetProperty("alive", out var alive) && alive.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var e in alive.EnumerateArray())
                if (e.GetString() is { } id) list.Add(id);
            _alive = list;
        }

        var next = (GamePhase)GetInt(s, "phase");
        if (next != Phase)
        {
            Phase = next;
            PhaseChanged?.Invoke(next);
        }

        // The event log is newest-first and capped server-side. Dedupe by (type, at) so a
        // re-poll does not replay a kill notification every 1.5 seconds.
        if (s.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
        {
            var ordered = new List<JsonElement>();
            foreach (var e in events.EnumerateArray()) ordered.Add(e);
            ordered.Reverse(); // oldest first, so listeners see them in order
            foreach (var e in ordered)
            {
                string type = e.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                long at = e.TryGetProperty("at", out var a) ? a.GetInt64() : 0;
                string key = $"{type}:{at}";
                if (!_seenEvents.Add(key)) continue;
                GameEvent?.Invoke(type, e);
            }
            if (_seenEvents.Count > 256) _seenEvents.Clear();
        }
    }

    private void ApplyMe(JsonElement m)
    {
        Alive = m.TryGetProperty("alive", out var a) && a.ValueKind == JsonValueKind.True;
        Tasks = GetInt(m, "tasks");
        TasksRequired = Math.Max(1, GetInt(m, "tasksRequired"));
        KillReadyAtMs = GetLong(m, "killReadyAt");

        if (m.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.Number)
        {
            var role = (GameRole)r.GetInt32();
            if (MyRole != role)
            {
                MyRole = role;
                RoleRevealed?.Invoke(role);
            }
        }
    }

    // ── actions. Each is a REQUEST; the server decides. ──

    public async Task<bool> TryCompleteTask()
    {
        try { await _api.CompleteTaskAsync(_instanceId); _pollTimer = 0; return true; }
        catch (Exception e) { Error?.Invoke(e.Message); return false; }
    }

    public async Task<bool> TryKill(string targetId, float distance)
    {
        try { await _api.KillAsync(_instanceId, targetId, distance); _pollTimer = 0; return true; }
        catch (Exception e) { Error?.Invoke(e.Message); return false; }
    }

    public async Task<bool> TryReport()
    {
        try { await _api.ReportBodyAsync(_instanceId); _pollTimer = 0; return true; }
        catch (Exception e) { Error?.Invoke(e.Message); return false; }
    }

    public async Task<bool> TryVote(string targetId)
    {
        try { await _api.VoteAsync(_instanceId, targetId); _pollTimer = 0; return true; }
        catch (Exception e) { Error?.Invoke(e.Message); return false; }
    }

    public async Task<bool> TryFinish()
    {
        try { await _api.ReportFinishAsync(_instanceId); _pollTimer = 0; return true; }
        catch (Exception e) { Error?.Invoke(e.Message); return false; }
    }

    public async Task<bool> TryStart(GameModeKind mode, int rounds = 3)
    {
        try { await _api.StartGameAsync(_instanceId, (int)mode, rounds); _pollTimer = 0; return true; }
        catch (Exception e) { Error?.Invoke(e.Message); return false; }
    }

    /// Whether the kill affordance should be enabled. A UI hint only — the server re-checks
    /// every one of these conditions and will refuse regardless of what we render.
    public bool CanAttemptKill =>
        Phase == GamePhase.Playing
        && MyRole == GameRole.Imposter
        && Alive
        && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= KillReadyAtMs;

    public double KillCooldownRemaining =>
        Math.Max(0, (KillReadyAtMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 1000.0);

    public double MeetingSecondsLeft =>
        MeetingEndsAtMs == 0 ? 0 : Math.Max(0, (MeetingEndsAtMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 1000.0);

    private static int GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static long GetLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
}
