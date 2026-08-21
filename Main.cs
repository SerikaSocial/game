using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Serika.Auth;
using Serika.Net;
using Serika.Net.Codec;
using SerikaSocial.Player;

namespace SerikaSocial;

/// Boots the client: builds the default world, logs in via PKCE, joins an instance, and
/// wires the transport to spawn/interpolate remote avatars.
///
/// A headless smoke mode (`-- --serika-smoke --endpoint host:port --ticket TOKEN`) skips
/// login and connects directly, so the whole Godot↔relay path can be exercised in CI
/// without a browser or display.
public partial class Main : Node3D
{
    private ISerikaTransport _transport;
    private LocalPlayer _local;
    private readonly Dictionary<uint, RemoteAvatar> _remotes = new();

    private double _poseTimer;
    private byte _poseSeq;
    private bool _smoke;
    private double _smokeQuitTimer = 6.0;

    // Config. Defaults point at production; override with env vars for local dev
    // (e.g. SERIKA_API_URL=http://localhost:4100 SERIKA_ACCOUNTS_URL=http://localhost:3600).
    private string ApiBaseUrl => OrDefault("SERIKA_API_URL", "https://api-social.ado.ink");
    private string AccountsBaseUrl => OrDefault("SERIKA_ACCOUNTS_URL", "https://accounts.serika.dev");
    private string ClientId => OrDefault("SERIKA_CLIENT_ID", "serika-social-game");

    private Hud _hud;

    public override void _Ready()
    {
        BuildWorld();

        var args = ParseArgs();
        if (args.ContainsKey("serika-smoke"))
        {
            _smoke = true;
            string endpoint = args.GetValueOrDefault("endpoint", "127.0.0.1:4200");
            string ticket = args.GetValueOrDefault("ticket", "");
            GD.Print($"SMOKE connecting to {endpoint}");
            SpawnLocalPlayer();
            ConnectTo(endpoint, ticket);
        }
        else
        {
            _hud = new Hud { Name = "Hud" };
            AddChild(_hud);
            _hud.LoginPressed += () => _ = LoginAndJoin();
            _hud.RetryPressed += () => _ = LoginAndJoin();
            _hud.ShowLogin();
        }
    }

    // ── World ───────────────────────────────────────────────────────────────────────

    private void BuildWorld()
    {
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = new Sky { SkyMaterial = new ProceduralSkyMaterial() },
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            },
        });

        var sun = new DirectionalLight3D { ShadowEnabled = true };
        sun.RotationDegrees = new Vector3(-50, -30, 0);
        AddChild(sun);

        // "The Commons": a 40m floor. This is the built-in world; user worlds stream in M4.
        var floor = new StaticBody3D { Name = "Floor" };
        var floorMesh = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(40, 40) } };
        floorMesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 0.32f, 0.35f) };
        floor.AddChild(floorMesh);
        var floorCol = new CollisionShape3D { Shape = new WorldBoundaryShape3D() };
        floor.AddChild(floorCol);
        AddChild(floor);
    }

    private void SpawnLocalPlayer()
    {
        _local = new LocalPlayer { Name = "LocalPlayer", Position = new Vector3(0, 1, 0) };
        AddChild(_local);
    }

    // ── Login → join ─────────────────────────────────────────────────────────────────

    private async Task LoginAndJoin()
    {
        try
        {
            var api = new ApiClient(ApiBaseUrl);
            var pkce = new PkceFlow();

            _hud?.SetStatus("Opening your browser to sign in…");
            OS.ShellOpen(pkce.AuthorizeUrl(AccountsBaseUrl, ClientId));
            GD.Print("opened browser for login…");

            _hud?.SetStatus("Waiting for you to sign in…");
            string code = await pkce.WaitForCodeAsync(TimeSpan.FromMinutes(3));

            _hud?.SetStatus("Signing in…");
            var user = await api.ExchangeAsync(code, pkce.Verifier);
            string username = user.GetProperty("username").GetString();
            GD.Print($"logged in as {username}");

            _hud?.SetStatus("Finding a world…");
            var worlds = await api.GetWorldsAsync();
            string worldId = worlds[0].GetProperty("id").GetString();
            string worldName = worlds[0].GetProperty("name").GetString();

            _hud?.SetStatus($"Joining {worldName}…");
            var joined = await api.CreateInstanceAsync(worldId);
            string endpoint = joined.GetProperty("endpoint").GetString();
            string ticket = joined.GetProperty("ticket").GetString();

            // Back to the game thread to touch the scene tree.
            CallDeferred(nameof(OnJoinReady), endpoint, ticket, username, worldName);
        }
        catch (Exception e)
        {
            GD.PrintErr($"login/join failed: {e.Message}");
            CallDeferred(nameof(ShowLoginError), FriendlyError(e));
        }
    }

    private void ShowLoginError(string message) => _hud?.ShowError(message);

    // Turn raw exceptions into something a player can act on.
    private static string FriendlyError(Exception e)
    {
        string m = e.Message ?? "";
        if (m.Contains("invalid_client") || m.Contains("Client not found"))
            return "This build isn't registered with Serika accounts yet. (OAuth client 'serika-social-game' is missing.)";
        if (m.Contains("timed out") || m.Contains("Timeout") || m.Contains("cancel"))
            return "Login timed out. Please try again.";
        if (m.Contains("refused") || m.Contains("resolve") || m.Contains("host"))
            return "Couldn't reach the servers. Check your connection and try again.";
        return $"Login failed: {m}";
    }

    private string _worldName = "the world";

    private void OnJoinReady(string endpoint, string ticket, string username, string worldName)
    {
        _worldName = worldName;
        _hud?.SetStatus($"Connecting to {worldName}…");
        SpawnLocalPlayer();
        ConnectTo(endpoint, ticket);
    }

    private void ConnectTo(string endpoint, string ticket)
    {
        var udp = new UdpTransport();
        udp.Connected += OnConnected;
        udp.PeerJoined += OnPeerJoined;
        udp.PeerLeft += OnPeerLeft;
        udp.PoseReceived += OnPoseReceived;
        udp.Rejected += reason =>
        {
            GD.PrintErr($"relay rejected us: {reason}");
            _hud?.ShowError($"The world server rejected the connection: {reason}");
        };
        _transport = udp;
        udp.Connect(endpoint, ticket);
    }

    // ── Transport events (fire on the game thread from Poll) ─────────────────────────

    private void OnConnected(uint selfId, PeerInfo[] peers)
    {
        GD.Print($"SMOKE connected self={selfId} peers={peers.Length}");
        foreach (var p in peers) SpawnRemote(p);
        int others = peers.Length;
        string who = others == 0 ? "You're the first one here." : $"{others} other {(others == 1 ? "person" : "people")} here.";
        _hud?.HideWithToast($"Welcome to {_worldName}. {who}");
    }

    private void OnPeerJoined(PeerInfo p)
    {
        GD.Print($"SMOKE peer_join {p.PeerId} {p.Name}");
        SpawnRemote(p);
    }

    private void OnPeerLeft(uint peerId)
    {
        if (_remotes.Remove(peerId, out var a)) a.QueueFree();
    }

    private void OnPoseReceived(uint peerId, PoseFrame frame)
    {
        if (_remotes.TryGetValue(peerId, out var a)) a.ApplyPose(frame);
        if (_smoke) GD.Print($"SMOKE pose_from {peerId} seq={frame.Sequence}");
    }

    private void SpawnRemote(PeerInfo p)
    {
        if (_remotes.ContainsKey(p.PeerId)) return;
        var a = RemoteAvatar.Create(p.PeerId, p.Name);
        _remotes[p.PeerId] = a;
        AddChild(a);
    }

    // ── Per-frame ────────────────────────────────────────────────────────────────────

    public override void _PhysicsProcess(double delta)
    {
        _transport?.Poll(delta);

        if (_transport is { Connected_: true } && _local != null)
        {
            // Send pose at 20Hz.
            _poseTimer += delta;
            if (_poseTimer >= 0.05)
            {
                _poseTimer = 0;
                _transport.SendPose(AvatarPose.FromTransform(_local.PoseTransform(), _poseSeq++));
            }
        }

        if (_smoke)
        {
            _smokeQuitTimer -= delta;
            if (_smokeQuitTimer <= 0)
            {
                GD.Print("SMOKE done");
                GetTree().Quit();
            }
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────

    private static string OrDefault(string env, string dflt)
    {
        var v = OS.GetEnvironment(env);
        return string.IsNullOrEmpty(v) ? dflt : v;
    }

    private static Dictionary<string, string> ParseArgs()
    {
        var d = new Dictionary<string, string>();
        var args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            string key = args[i][2..];
            int eq = key.IndexOf('=');
            if (eq >= 0) { d[key[..eq]] = key[(eq + 1)..]; continue; }
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) { d[key] = args[++i]; }
            else d[key] = "true";
        }
        return d;
    }
}
