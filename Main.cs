using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Serika.Auth;
using Serika.Net;
using Serika.Net.Codec;
using SerikaSocial.Player;

namespace SerikaSocial;

/// Interface shared by LocalPlayer (desktop) and VrPlayer (OpenXR) so Main can treat
/// them uniformly for pose broadcasting and color/username assignment.
public interface IPlayer
{
    Transform3D PoseTransform();
    void SetAvatarColor(Color color);
    void SetUsername(string name);
    float MouseSensitivity { get; set; }
}

/// Boots the client: builds the default world, logs in via PKCE, joins an instance, and
/// wires the transport to spawn/interpolate remote avatars.
///
/// A headless smoke mode (`-- --serika-smoke --endpoint host:port --ticket TOKEN`) skips
/// login and connects directly, so the whole Godot↔relay path can be exercised in CI
/// without a browser or display.
public partial class Main : Node3D
{
    private ISerikaTransport _transport;
    private IPlayer _local;
    private Node3D _localNode;
    private bool _vrMode;
    private readonly Dictionary<uint, RemoteAvatar> _remotes = new();

    private double _poseTimer;
    private double _pingTimer;
    private byte _poseSeq;
    private bool _smoke;
    private double _smokeQuitTimer = 6.0;

    // Config. Defaults point at production; override with env vars for local dev
    // (e.g. SERIKA_API_URL=http://localhost:4100 SERIKA_ACCOUNTS_URL=http://localhost:3600).
    private string ApiBaseUrl => OrDefault("SERIKA_API_URL", "https://api-social.ado.ink");
    private string AccountsBaseUrl => OrDefault("SERIKA_ACCOUNTS_URL", "https://accounts.serika.dev");
    private string ClientId => OrDefault("SERIKA_CLIENT_ID", "serika-social-game");

    private Hud _hud;
    private PauseMenu _pauseMenu;
    private InWorldHud _inWorldHud;
    private DeepLink.Intent _pendingIntent = DeepLink.Intent.None;
    private bool _inHome;
    private bool _inWorld;

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
            return;
        }

        // Register the serikasocial:// handler so the website's "Open in app" works, and see
        // if we were launched from such a link (e.g. serikasocial://world/<id>).
        DeepLink.RegisterHandler();
        _pendingIntent = DeepLink.FromCommandLine();

        _hud = new Hud { Name = "Hud" };
        AddChild(_hud);
        _hud.LoginPressed += () => _ = LoginThenRoute();
        _hud.RetryPressed += () => _ = LoginThenRoute();
        _hud.HomePressed += EnterHome;
        _hud.JoinCommonsPressed += () => _ = JoinDefaultWorld();
        _hud.JoinWorldPressed += (worldId) => _ = JoinWorldById(worldId);
        _hud.ShowLogin();

        _pauseMenu = new PauseMenu { Name = "PauseMenu" };
        AddChild(_pauseMenu);
        _pauseMenu.HomePressed += EnterHome;
        _pauseMenu.QuitPressed += () => GetTree().Quit();

        _inWorldHud = new InWorldHud { Name = "InWorldHud" };
        AddChild(_inWorldHud);
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
                AmbientLightColor = new Color(0.4f, 0.45f, 0.55f),
                AmbientLightEnergy = 0.5f,
                FogEnabled = true,
                FogLightColor = new Color(0.5f, 0.55f, 0.65f),
                FogLightEnergy = 0.3f,
                FogDensity = 0.001f,
            },
        });

        var sun = new DirectionalLight3D { ShadowEnabled = true, LightEnergy = 0.8f };
        sun.RotationDegrees = new Vector3(-50, -30, 0);
        AddChild(sun);

        // "The Commons": a 40m floor with decorative elements.
        var floor = new StaticBody3D { Name = "Floor" };
        var floorMesh = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(40, 40) } };
        floorMesh.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.18f, 0.20f, 0.24f),
            Roughness = 0.9f,
        };
        floor.AddChild(floorMesh);
        var floorCol = new CollisionShape3D { Shape = new WorldBoundaryShape3D() };
        floor.AddChild(floorCol);
        AddChild(floor);

        // Decorative pillars at the corners
        float[] corners = { -15, 15 };
        foreach (float x in corners)
        {
            foreach (float z in corners)
            {
                var pillar = new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(1, 4, 1) },
                    Position = new Vector3(x, 2, z),
                };
                pillar.MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = new Color(0.12f, 0.13f, 0.16f),
                    Roughness = 0.8f,
                };
                AddChild(pillar);
            }
        }

        // Central platform — a gathering spot
        var platform = new MeshInstance3D
        {
            Mesh = new CylinderMesh { Height = 0.2f, TopRadius = 3, BottomRadius = 3 },
            Position = new Vector3(0, 0.1f, 0),
        };
        platform.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.22f, 0.24f, 0.28f),
            Roughness = 0.7f,
        };
        AddChild(platform);

        // Seating area — low benches around the platform
        for (int i = 0; i < 6; i++)
        {
            float angle = i * Mathf.Tau / 6f;
            var bench = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(2, 0.4f, 0.6f) },
                Position = new Vector3(Mathf.Cos(angle) * 5, 0.2f, Mathf.Sin(angle) * 5),
                RotationDegrees = new Vector3(0, -angle * 180f / Mathf.Pi, 0),
            };
            bench.MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.15f, 0.16f, 0.19f),
                Roughness = 0.85f,
            };
            AddChild(bench);
        }

        // Overhead lighting fixtures — glowing spheres on thin posts
        float[] lightPos = { -8, 0, 8 };
        foreach (float x in lightPos)
        {
            foreach (float z in lightPos)
            {
                if (x == 0 && z == 0) continue;
                var post = new MeshInstance3D
                {
                    Mesh = new CylinderMesh { Height = 5, TopRadius = 0.05f, BottomRadius = 0.05f },
                    Position = new Vector3(x, 2.5f, z),
                };
                post.MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = new Color(0.1f, 0.1f, 0.12f),
                    Roughness = 0.9f,
                };
                AddChild(post);

                var lamp = new OmniLight3D
                {
                    Position = new Vector3(x, 4.8f, z),
                    LightColor = new Color(0.9f, 0.88f, 0.75f),
                    LightEnergy = 0.6f,
                    OmniRange = 8f,
                    OmniAttenuation = 1.5f,
                    ShadowEnabled = true,
                };
                AddChild(lamp);

                var glow = new MeshInstance3D
                {
                    Mesh = new SphereMesh { Radius = 0.15f, Height = 0.3f },
                    Position = new Vector3(x, 4.8f, z),
                };
                glow.MaterialOverride = new StandardMaterial3D
                {
                    EmissionEnergyMultiplier = 2f,
                    Emission = new Color(0.9f, 0.88f, 0.75f),
                    AlbedoColor = new Color(0.9f, 0.88f, 0.75f),
                };
                AddChild(glow);
            }
        }

        // World signage — "The Commons" near the entrance
        var sign = new Label3D
        {
            Text = "The Commons",
            Position = new Vector3(0, 3.5f, -15),
            Billboard = BaseMaterial3D.BillboardModeEnum.Disabled,
            FontSize = 96,
            PixelSize = 0.01f,
        };
        sign.Modulate = new Color(0.7f, 0.75f, 0.85f);
        AddChild(sign);

        // Boundary walls — low walls at the edges so the world feels enclosed
        float[] wallEdges = { -19, 19 };
        foreach (float edge in wallEdges)
        {
            var wallX = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.5f, 1.5f, 40) },
                Position = new Vector3(edge, 0.75f, 0),
            };
            wallX.MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.1f, 0.11f, 0.14f),
                Roughness = 0.9f,
            };
            AddChild(wallX);

            var wallZ = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(40, 1.5f, 0.5f) },
                Position = new Vector3(0, 0.75f, edge),
            };
            wallZ.MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.1f, 0.11f, 0.14f),
                Roughness = 0.9f,
            };
            AddChild(wallZ);
        }

        // Spawn point marker — a subtle ring on the floor
        var spawnRing = new MeshInstance3D
        {
            Mesh = new CylinderMesh { Height = 0.02f, TopRadius = 0.8f, BottomRadius = 0.8f },
            Position = new Vector3(0, 0.11f, 8),
        };
        spawnRing.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.3f, 0.5f, 0.8f, 0.6f),
            Roughness = 0.5f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        };
        AddChild(spawnRing);
    }

    private void SpawnLocalPlayer()
    {
        // Detect VR at spawn time — if OpenXR is initialized, use VrPlayer.
        _vrMode = VrPlayer.IsVrAvailable();
        if (_vrMode)
        {
            var vr = new VrPlayer { Name = "LocalPlayer", Position = new Vector3(0, 1, 0) };
            AddChild(vr);
            _local = vr;
            _localNode = vr;
            GD.Print("VR mode: OpenXR initialized");
        }
        else
        {
            var desktop = new LocalPlayer { Name = "LocalPlayer", Position = new Vector3(0, 1, 0) };
            AddChild(desktop);
            _local = desktop;
            _localNode = desktop;
            GD.Print("Desktop mode");
        }
        if (_hud != null)
            _local.SetAvatarColor(_hud.AvatarColor);
        _local.SetUsername(_username);
    }

    // ── Login → route (Home, or a world from a deep link) ────────────────────────────

    private ApiClient _api;
    private string _username = "traveller";

    /// Sign in, then go where the launch intent says: a deep-linked world, or Home.
    private async Task LoginThenRoute()
    {
        try
        {
            _api = new ApiClient(ApiBaseUrl);
            var pkce = new PkceFlow();

            _hud?.SetStatus("Opening your browser to sign in…");
            OS.ShellOpen(pkce.AuthorizeUrl(AccountsBaseUrl, ClientId));
            GD.Print("opened browser for login…");

            _hud?.SetStatus("Waiting for you to sign in…");
            string code = await pkce.WaitForCodeAsync(TimeSpan.FromMinutes(3));

            _hud?.SetStatus("Signing in…");
            var user = await _api.ExchangeAsync(code, pkce.Verifier);
            _username = user.GetProperty("username").GetString();
            GD.Print($"logged in as {_username}");

            if (_pendingIntent.Kind == DeepLink.Kind.World)
            {
                await JoinWorldById(_pendingIntent.Arg);
                _pendingIntent = DeepLink.Intent.None;
            }
            else
            {
                // Default landing after login: the personal, single-player Home.
                CallDeferred(nameof(EnterHome));
            }
        }
        catch (Exception e)
        {
            GD.PrintErr($"login failed: {e.Message}");
            CallDeferred(nameof(ShowLoginError), FriendlyError(e));
        }
    }

    /// Join the built-in default world (the multiplayer commons) from within Home.
    private async Task JoinDefaultWorld()
    {
        if (_api == null) return;
        try
        {
            _hud?.SetStatus("Finding a world…");
            var worlds = await _api.GetWorldsAsync();
            await JoinWorldById(worlds[0].GetProperty("id").GetString());
        }
        catch (Exception e)
        {
            GD.PrintErr($"join failed: {e.Message}");
            CallDeferred(nameof(ShowLoginError), FriendlyError(e));
        }
    }

    /// Create/join an instance of a specific world and connect to its relay.
    private async Task JoinWorldById(string worldId)
    {
        try
        {
            _hud?.SetStatus("Joining world…");
            var joined = await _api.CreateInstanceAsync(worldId);
            string endpoint = joined.GetProperty("endpoint").GetString();
            string ticket = joined.GetProperty("ticket").GetString();
            string worldName = joined.TryGetProperty("worldName", out var wn) ? wn.GetString() : "the world";
            CallDeferred(nameof(OnJoinReady), endpoint, ticket, _username, worldName);
        }
        catch (Exception e)
        {
            GD.PrintErr($"join failed: {e.Message}");
            CallDeferred(nameof(ShowLoginError), FriendlyError(e));
        }
    }

    /// The personal Home: a single-player space with no relay connection. Instant, always
    /// available, and where the player lands after login.
    private void EnterHome()
    {
        _inHome = true;
        _inWorld = false;
        TeardownRemotes();
        _transport?.Disconnect();
        _transport = null;
        if (_local == null) SpawnLocalPlayer();
        _worldName = "Home";
        _hud?.ShowHome(_username);
        _ = PopulateWorldList();
    }

    private List<(string id, string name, string description, int capacity)> _fetchedWorlds;

    private async Task PopulateWorldList()
    {
        if (_api == null) return;
        try
        {
            var worlds = await _api.GetWorldsAsync();
            var list = new List<(string id, string name, string description, int capacity)>();
            foreach (var w in worlds.EnumerateArray())
            {
                string id = w.GetProperty("id").GetString();
                string name = w.GetProperty("name").GetString();
                string desc = w.TryGetProperty("description", out var d) ? d.GetString() : "";
                int cap = w.TryGetProperty("capacity", out var c) ? c.GetInt32() : 32;
                list.Add((id, name, desc ?? "", cap));
            }
            _fetchedWorlds = list;
            CallDeferred(nameof(OnWorldsFetched));
        }
        catch (Exception e)
        {
            GD.PrintErr($"world list fetch failed: {e.Message}");
        }
    }

    private void OnWorldsFetched()
    {
        _hud?.SetWorlds(_fetchedWorlds);
    }

    private void TeardownRemotes()
    {
        foreach (var a in _remotes.Values) a.QueueFree();
        _remotes.Clear();
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
        _inWorld = true;
        _inWorldHud.SetWorld(_worldName);
        _inWorldHud.SetPlayerCount(1 + others);
    }

    private void OnPeerJoined(PeerInfo p)
    {
        GD.Print($"SMOKE peer_join {p.PeerId} {p.Name}");
        SpawnRemote(p);
        _inWorldHud.SetPlayerCount(1 + _remotes.Count);
    }

    private void OnPeerLeft(uint peerId)
    {
        if (_remotes.Remove(peerId, out var a)) a.QueueFree();
        _inWorldHud.SetPlayerCount(1 + _remotes.Count);
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

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape } && _inWorld && !_pauseMenu.IsOpen)
        {
            _pauseMenu.Show();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        _transport?.Poll(delta);

        if (_local != null && _pauseMenu != null)
            _local.MouseSensitivity = _pauseMenu.MouseSensitivity;

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
