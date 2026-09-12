using System;
using System.Collections.Generic;
using Godot;
using SerikaSocial.UI;

namespace SerikaSocial.Game;

/// The in-world UI for a game session.
///
/// TWO layers, and the split is not cosmetic. `Readout` is a persistent status display — role,
/// tasks, cooldown — so in VR it belongs on the wrist (`chrome: true`). `Voting` is an
/// interactive menu, so it belongs on the VR panel.
///
/// The voting layer HIDES ITSELF when there is no meeting, rather than hiding its inner controls.
/// `VrUiSurface.HasInteractiveUi` asks whether any *layer* is visible and gates both the panel
/// render and the laser pointer, so a layer that only ever hides its children pins a slab and a
/// laser in the player's face for the whole session. Per CLAUDE.md that exact defect has been
/// reintroduced twice by later layers; do not make it three times.
public partial class GameHud : Node
{
    private GameSession _session;
    private Func<string, string> _nameOf;      // userId -> display name
    private Action<string> _onToast;

    private CanvasLayer _readoutLayer;
    private CanvasLayer _votingLayer;

    private Label _roleLabel;
    private Label _statusLabel;
    private Label _cooldownLabel;
    private Panel _rolePanel;

    private VBoxContainer _voteList;
    private Label _voteTimer;
    private readonly List<Button> _voteButtons = new();
    private bool _votedThisMeeting;

    public static GameHud Create(GameSession session, Func<string, string> nameOf, Action<string> onToast) =>
        new() { Name = "GameHud", _session = session, _nameOf = nameOf, _onToast = onToast };

    /// Layers are created by the caller's AddUi so they route correctly in VR; this returns them
    /// for that purpose rather than parenting them itself.
    public (CanvasLayer Readout, CanvasLayer Voting) BuildLayers()
    {
        _readoutLayer = BuildReadout();
        _votingLayer = BuildVoting();
        _votingLayer.Visible = false;   // the layer, not just its contents
        return (_readoutLayer, _votingLayer);
    }

    public override void _Ready()
    {
        if (_session == null) return;
        _session.PhaseChanged += OnPhaseChanged;
        _session.RoleRevealed += OnRoleRevealed;
        _session.GameEvent += OnGameEvent;
    }

    public override void _ExitTree()
    {
        InputMode.Release("game-meeting");
        _readoutLayer?.QueueFree();
        _votingLayer?.QueueFree();
        if (_session == null) return;
        _session.PhaseChanged -= OnPhaseChanged;
        _session.RoleRevealed -= OnRoleRevealed;
        _session.GameEvent -= OnGameEvent;
    }

    private CanvasLayer BuildReadout()
    {
        var layer = new CanvasLayer { Name = "GameReadout" };
        var root = new MarginContainer { AnchorsPreset = (int)Control.LayoutPreset.TopWide };
        root.AddThemeConstantOverride("margin_top", 14);
        root.AddThemeConstantOverride("margin_left", 18);
        root.AddThemeConstantOverride("margin_right", 18);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);

        _rolePanel = new Panel { CustomMinimumSize = new Vector2(150, 44) };
        _rolePanel.AddThemeStyleboxOverride("panel", Brand.Panel(Brand.Bg1, 10, 1, Brand.Border));
        _roleLabel = new Label
        {
            Text = "—",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            AnchorsPreset = (int)Control.LayoutPreset.FullRect,
        };
        _roleLabel.AddThemeFontSizeOverride("font_size", Brand.Fs(18));
        _rolePanel.AddChild(_roleLabel);
        row.AddChild(_rolePanel);

        _statusLabel = new Label { Text = "", VerticalAlignment = VerticalAlignment.Center };
        _statusLabel.AddThemeFontSizeOverride("font_size", Brand.Fs(16));
        _statusLabel.AddThemeColorOverride("font_color", Brand.TextMid);
        row.AddChild(_statusLabel);

        _cooldownLabel = new Label { Text = "", VerticalAlignment = VerticalAlignment.Center };
        _cooldownLabel.AddThemeFontSizeOverride("font_size", Brand.Fs(16));
        _cooldownLabel.AddThemeColorOverride("font_color", Brand.Warning);
        row.AddChild(_cooldownLabel);

        root.AddChild(row);
        layer.AddChild(root);
        return layer;
    }

    private CanvasLayer BuildVoting()
    {
        var layer = new CanvasLayer { Name = "GameVoting" };
        layer.AddChild(Brand.Scrim());

        var center = new CenterContainer { AnchorsPreset = (int)Control.LayoutPreset.FullRect };
        var panel = new PanelContainer { CustomMinimumSize = Brand.Card(520, 520) };
        panel.AddThemeStyleboxOverride("panel", Brand.Panel(Brand.Bg1, 16, 1, Brand.Border));

        var margin = new MarginContainer();
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 20);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 10);

        var title = new Label { Text = "Emergency meeting", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", Brand.Fs(24));
        title.AddThemeColorOverride("font_color", Brand.TextHi);
        col.AddChild(title);

        _voteTimer = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center };
        _voteTimer.AddThemeFontSizeOverride("font_size", Brand.Fs(16));
        _voteTimer.AddThemeColorOverride("font_color", Brand.Accent);
        col.AddChild(_voteTimer);

        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _voteList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _voteList.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_voteList);
        col.AddChild(scroll);

        margin.AddChild(col);
        panel.AddChild(margin);
        center.AddChild(panel);
        layer.AddChild(center);
        return layer;
    }

    public override void _Process(double delta)
    {
        if (_session == null || _readoutLayer == null) return;

        bool live = _session.HasSession && _session.Phase != GamePhase.Lobby;
        _readoutLayer.Visible = live;
        if (!live) { if (_votingLayer.Visible) CloseMeeting(); return; }

        UpdateReadout();

        if (_session.Phase == GamePhase.Meeting)
        {
            if (!_votingLayer.Visible) OpenMeeting();
            _voteTimer.Text = $"{Mathf.CeilToInt((float)_session.MeetingSecondsLeft)}s";
        }
        else if (_votingLayer.Visible)
        {
            CloseMeeting();
        }
    }

    private void UpdateReadout()
    {
        if (_session.Phase == GamePhase.Ended)
        {
            _roleLabel.Text = "RESULT";
            _statusLabel.Text = DescribeOutcome();
            _cooldownLabel.Text = "Host can start another game at the lobby console";
            return;
        }
        double countdown = (_session.RoundStartedAt + 3000 - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 1000.0;
        if (_session.Phase == GamePhase.Playing && countdown > 0)
        {
            _roleLabel.Text = "GET READY";
            _statusLabel.Text = $"{Math.Ceiling(countdown)}";
            _cooldownLabel.Text = "The round starts after the countdown";
            return;
        }
        if (_session.Mode != GameModeKind.Imposter)
        {
            _roleLabel.Text = _session.Alive ? "IN PLAY" : "OUT";
            _rolePanel.AddThemeStyleboxOverride("panel",
                Brand.Panel(Brand.Bg1, 10, 1, _session.Alive ? Brand.Success : Brand.Danger));
            var elapsed = Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _session.RoundStartedAt - 3000) / 1000);
            _statusLabel.Text = _session.Mode == GameModeKind.Rope
                ? $"Canyon expedition · {elapsed / 60}:{elapsed % 60:00} · {_session.FinishedCount}/{_session.AliveCount} at summit"
                : $"Round {_session.Round}/{_session.MaxRounds}   ·   {_session.AliveCount} left · {elapsed / 60}:{elapsed % 60:00}";
            _cooldownLabel.Text = _session.Place > 0 ? (_session.Mode == GameModeKind.Rope ? "Regroup at the summit to complete the expedition" : $"Finished #{_session.Place} · wait for the next round") : "Cross all checkpoints, then the finish arch";
            return;
        }

        // Imposter mode. The role badge shows only OUR role — no other player's role exists on
        // this client to render.
        if (_session.MyRole is { } role)
        {
            bool imp = role == GameRole.Imposter;
            _roleLabel.Text = imp ? "IMPOSTER" : "CREWMATE";
            _roleLabel.AddThemeColorOverride("font_color", imp ? Brand.Danger : Brand.Accent);
            _rolePanel.AddThemeStyleboxOverride("panel",
                Brand.Panel(Brand.Bg1, 10, 1, imp ? Brand.Danger : Brand.Border));
        }
        else
        {
            _roleLabel.Text = "SPECTATOR";
            _roleLabel.AddThemeColorOverride("font_color", Brand.TextDim);
        }

        string ghost = _session.Alive ? "" : "   ·   GHOST";
        if (_session.MyRole == GameRole.Crew)
            _statusLabel.Text = $"Tasks {_session.Tasks}/{_session.TasksRequired}   ·   {_session.AliveCount} alive{ghost}";
        else
            _statusLabel.Text = $"{_session.AliveCount} alive{ghost}";

        double cd = _session.KillCooldownRemaining;
        _cooldownLabel.Text = _session.MyRole == GameRole.Imposter && _session.Alive && cd > 0
            ? $"kill in {Mathf.CeilToInt((float)cd)}s"
            : "";
    }

    private void OpenMeeting()
    {
        InputMode.Hold("game-meeting");
        _votedThisMeeting = false;
        _votingLayer.Visible = true;
        RebuildVoteList();
    }

    private void CloseMeeting()
    {
        InputMode.Release("game-meeting");
        _votingLayer.Visible = false;
        foreach (var b in _voteButtons) b.QueueFree();
        _voteButtons.Clear();
    }

    private void RebuildVoteList()
    {
        foreach (var b in _voteButtons) b.QueueFree();
        _voteButtons.Clear();

        // The dead may watch a meeting but not vote — the server refuses their vote anyway, so
        // offering the button would only produce an error the player cannot act on.
        bool canVote = _session.Alive;

        foreach (var id in _session.AlivePlayers)
        {
            var b = Brand.Ghost_(new Button
            {
                Text = _nameOf?.Invoke(id) ?? id,
                Disabled = !canVote,
                CustomMinimumSize = new Vector2(0, 40),
            });
            string target = id;
            b.Pressed += () => CastVote(target);
            _voteList.AddChild(b);
            _voteButtons.Add(b);
        }

        var skip = Brand.Ghost_(new Button
        {
            Text = "Skip vote",
            Disabled = !canVote,
            CustomMinimumSize = new Vector2(0, 40),
        });
        skip.Pressed += () => CastVote("skip");
        _voteList.AddChild(skip);
        _voteButtons.Add(skip);
    }

    private async void CastVote(string targetId)
    {
        if (_votedThisMeeting) return;
        _votedThisMeeting = true;
        foreach (var b in _voteButtons) b.Disabled = true;
        bool ok = await _session.TryVote(targetId);
        if (!ok)
        {
            // Let them try again — a refused vote is usually a phase change, not a permanent no.
            _votedThisMeeting = false;
            foreach (var b in _voteButtons) b.Disabled = !_session.Alive;
        }
    }

    private void OnPhaseChanged(GamePhase phase)
    {
        switch (phase)
        {
            case GamePhase.Meeting: _onToast?.Invoke("Emergency meeting"); break;
            case GamePhase.Ended: _onToast?.Invoke(DescribeOutcome()); break;
        }
    }

    private string DescribeOutcome() => _session.Outcome switch
    {
        GameOutcome.CrewWin => "Crew win",
        GameOutcome.ImposterWin => "Imposters win",
        GameOutcome.GauntletWin => _session.Winners.Count > 0
            ? "Winner: " + string.Join(", ", _session.Winners.ConvertAll(id => _nameOf(id))) : "Race complete",
        GameOutcome.RopeWin => "Expedition complete — the whole crew made it!",
        GameOutcome.Abandoned => "Round abandoned",
        _ => "Game over",
    };

    private void OnRoleRevealed(GameRole role) =>
        _onToast?.Invoke(role == GameRole.Imposter
            ? "You are the IMPOSTER"
            : "You are a CREWMATE");

    private void OnGameEvent(string type, System.Text.Json.JsonElement e)
    {
        switch (type)
        {
            case "died":
                // Names the victim only. The server never sends the killer, so there is nothing
                // here to leak even if this code wanted to.
                _onToast?.Invoke($"{NameFrom(e, "userId")} was found dead");
                break;
            case "ejected":
                bool tied = e.TryGetProperty("tied", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.True;
                if (tied) { _onToast?.Invoke("Nobody was ejected"); break; }
                string who = NameFrom(e, "userId");
                string was = e.TryGetProperty("role", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? (r.GetInt32() == (int)GameRole.Imposter ? " was an Imposter" : " was not an Imposter")
                    : "";
                _onToast?.Invoke($"{who} was ejected{was}");
                break;
            case "round":
                _onToast?.Invoke($"Round {(e.TryGetProperty("round", out var rn) ? rn.GetInt32() : 0)}");
                break;
            case "finished":
                _onToast?.Invoke($"{NameFrom(e, "userId")} qualified");
                break;
        }
    }

    private string NameFrom(System.Text.Json.JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? (_nameOf?.Invoke(v.GetString()) ?? v.GetString())
            : "Someone";
}
