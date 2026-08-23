using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Serika.Auth;
using Serika.Net;
using Serika.Net.Codec;
using SerikaSocial.Audio;
using SerikaSocial.Avatar;
using SerikaSocial.Player;
using SerikaSocial.World;

namespace SerikaSocial;

/// Interface shared by LocalPlayer (desktop) and VrPlayer (OpenXR) so Main can treat
/// them uniformly for pose broadcasting and color/username assignment.
public interface IPlayer
{
    Transform3D PoseTransform();
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
    private LocalPlayer _localDesktop; // non-null in desktop mode; drives FP/TP toggle
    private Node3D _localNode;
    private bool _vrMode;
    private readonly Dictionary<uint, RemoteAvatar> _remotes = new();
    private readonly HashSet<string> _blockedUserIds = new();
    private readonly Dictionary<uint, string> _peerUserIds = new();

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
    private AvatarSelector _avatarSelector;
    private InWorldHud _inWorldHud;
    private ChatOverlay _chat;
    private LoadingScreen _loading;
    private TouchControls _touch; // non-null on touchscreen (mobile) devices
    private readonly Dictionary<uint, string> _peerNames = new();
    private DeepLink.Intent _pendingIntent = DeepLink.Intent.None;
    private bool _inHome;
    private bool _inWorld;
    private bool _persistThirdPerson; // survive world switches so camera mode is sticky
    private SpatialAudioManager _audio;
    private VoiceManager _voice;
    private bool _micActive;
    private Updater _updater;

    public override void _Ready()
    {
        // Apply the purple brand theme to every Control in the client at once.
        GetTree().Root.Theme = Brand.Theme;

        _worldRoot = new Node3D { Name = "WorldRoot" };
        AddChild(_worldRoot);
        _audio = new SpatialAudioManager { Name = "SpatialAudio" };
        AddChild(_audio);
        _voice = new VoiceManager { Name = "VoiceManager" };
        AddChild(_voice);
        _voice.VoiceFrameReady += OnVoiceFrameReady;
        Worlds.BuildCommons(_worldRoot); // backdrop behind the login screen

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
        _hud.EmailLoginPressed += (email, pass) => _ = LoginWithEmailRoute(email, pass);
        _hud.RetryPressed += () => _ = LoginThenRoute();
        _hud.HomePressed += EnterHome;
        _hud.JoinCommonsPressed += () => _ = JoinDefaultWorld();
        _hud.JoinWorldPressed += (worldId) => _ = JoinWorldById(worldId);
        _hud.WorldListClosed += CloseWorldList;
        _hud.ShowLogin();

        // Auto-updater: check CDN for a newer version. Non-blocking — runs in the
        // background and shows a dialog only if an update is available.
        _updater = new Updater { Name = "Updater" };
        AddChild(_updater);
        _updater.CurrentVersion = Hud.ClientVersion;
        _updater.CheckForUpdates();

        _pauseMenu = new PauseMenu { Name = "PauseMenu" };
        AddChild(_pauseMenu);
        _pauseMenu.HomePressed += EnterHome;
        _pauseMenu.WorldsPressed += () => { _pauseMenu.Hide(); OpenWorldList(); };
        _pauseMenu.QuitPressed += () => GetTree().Quit();
        _pauseMenu.RespawnPressed += RespawnLocal;
        _pauseMenu.CameraTogglePressed += () =>
        {
            if (_localDesktop == null) return;
            bool fp = _localDesktop.ToggleCameraMode();
            _persistThirdPerson = !fp;
            _inWorldHud?.Toast(fp ? "First-person view" : "Third-person view");
        };
        _pauseMenu.EmotePressed += e => _localDesktop?.PlayEmote(e);
        _pauseMenu.CopyInvitePressed += CopyInviteLink;
        _pauseMenu.AvatarsPressed += OpenAvatarSelector;
        _pauseMenu.Closed += OnPauseClosed;

        _avatarSelector = new AvatarSelector { Name = "AvatarSelector" };
        AddChild(_avatarSelector);
        _avatarSelector.AvatarChosen += (id, url, name) => _ = EquipAvatar(id, url, name);
        _avatarSelector.Closed += OnAvatarSelectorClosed;

        _inWorldHud = new InWorldHud { Name = "InWorldHud" };
        AddChild(_inWorldHud);

        _chat = new ChatOverlay { Name = "ChatOverlay" };
        AddChild(_chat);
        _chat.MessageSubmitted += OnChatSubmitted;
        _chat.Closed += OnChatClosed;

        _loading = new LoadingScreen { Name = "LoadingScreen", Visible = false };
        AddChild(_loading);

        // Dev aid: SERIKA_DEBUG_LOADING=1 shows the loading screen immediately (for screenshots).
        if (OrDefault("SERIKA_DEBUG_LOADING", "") == "1")
            ShowLoading("Connecting to The Commons…");
    }

    /// Show the 3D loading screen with a status line during sign-in / connecting.
    private void ShowLoading(string status)
    {
        _loading?.Present();
        _loading?.SetStatus(status);
        _hud?.SetStatus(status);
    }

    private void SetLoadingStatus(string status)
    {
        _loading?.SetStatus(status);
        _hud?.SetStatus(status);
    }

    private void HideLoading() => _loading?.HideWithFade();

    // ── Text chat ─────────────────────────────────────────────────────────────────────

    /// Open the chat box (T). Releases the mouse and suppresses movement while typing.
    private void OpenChat()
    {
        if (_chat.IsTyping) return;
        _chat.OpenInput();
        if (_localDesktop != null) _localDesktop.ControlsEnabled = false;
        _wasMouseCaptured = Input.MouseMode == Input.MouseModeEnum.Captured;
        if (_wasMouseCaptured) Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    private bool _wasMouseCaptured;

    private void OnChatSubmitted(string text)
    {
        // Echo locally, then send to the room (if connected).
        _chat.AddChat(_username, text);
        _transport?.SendChat(text);
    }

    /// Restore control/mouse after the chat box closes (whether via Enter or Escape).
    private void OnChatClosed()
    {
        if (_localDesktop != null) _localDesktop.ControlsEnabled = true;
        if (_wasMouseCaptured) Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    private void OnChatReceived(uint senderId, string text)
    {
        string name = _peerNames.GetValueOrDefault(senderId, $"peer{senderId}");
        _chat.AddChat(name, text);
    }

    // ── World ───────────────────────────────────────────────────────────────────────

    private Node3D _worldRoot;
    private Worlds.Home _homeInfo;

    /// Swap the active world geometry: free the old root, build the new space into a fresh one.
    private void SwapWorld(System.Action<Node3D> build)
    {
        _audio?.StopAllAmbient();
        _worldRoot?.QueueFree();
        _worldRoot = new Node3D { Name = "WorldRoot" };
        AddChild(_worldRoot);
        build(_worldRoot);
    }

    /// Build the cosy Home and wire its Commons portal to join the multiplayer world.
    private void BuildHomeWorld()
    {
        SwapWorld(root => _homeInfo = Worlds.BuildHome(root));
        _homeInfo.CommonsPortal.Entered += () => { if (_api != null) _ = JoinDefaultWorld(); };
    }

    private void BuildCommonsWorld() => SwapWorld(Worlds.BuildCommons);

    private void SpawnLocalPlayer()
    {
        // A previous player rig (e.g. Home's, when joining a world) must not survive — it
        // kept simulating and rendering, so you'd literally see yourself in the lobby.
        if (_localNode != null)
        {
            _localNode.QueueFree();
            _localNode = null;
            _local = null;
            _localDesktop = null;
        }

        // Bring up VR only when it makes sense: on a Quest/Android build, or when a desktop
        // user explicitly asks with `--vr`. Otherwise OpenXR is never touched, so a normal
        // desktop launch produces no "failed to load runtime / no HMD" errors.
        bool wantVr = OS.HasFeature("android")
            || System.Array.IndexOf(OS.GetCmdlineArgs(), "--vr") >= 0
            || System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--vr") >= 0;
        _vrMode = wantVr && VrPlayer.TryInitVr();
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
            _localDesktop = desktop;
            // Equip the cloud default avatar (or custom downloaded one). Falls back to the
            // procedural bean when no cloud default is available, so nobody is ever a capsule.
            desktop.SetAvatar(AvatarLibrary.InstantiateOrDefault(_localAvatarPath));
            // Restore persisted camera mode across world switches.
            if (_persistThirdPerson) desktop.SetFirstPerson(false);
            SetupTouchControls(desktop);
            GD.Print("Desktop mode");
        }
        _local.SetUsername(_username);
    }

    /// Create the mobile touch overlay on touchscreen devices and bind it to the player. Rebound
    /// each spawn since the player instance changes; created once.
    private void SetupTouchControls(LocalPlayer player)
    {
        if (!DisplayServer.IsTouchscreenAvailable()) return;
        if (_touch == null)
        {
            var layer = new CanvasLayer { Name = "TouchLayer", Layer = 40 };
            AddChild(layer);
            _touch = new TouchControls { Name = "TouchControls" };
            layer.AddChild(_touch);
        }
        _touch.Configure(player, () =>
        {
            bool fp = player.ToggleCameraMode();
            _persistThirdPerson = !fp;
            _inWorldHud?.Toast(fp ? "First-person view" : "Third-person view");
        });
    }

    // ── Login → route (Home, or a world from a deep link) ────────────────────────────

    private ApiClient _api;
    private string _username = "traveller";
    private string _localAvatarPath; // downloaded custom avatar (user://), else null → bundled default

    /// Fetch the logged-in user's current avatar (their chosen one or a default outfit) and cache
    /// its .ska under user://. Failure is silent — we fall back to the bundled default, then capsule.
    private async Task FetchCurrentAvatar()
    {
        try
        {
            string url = await _api.GetCurrentAvatarUrlAsync();
            if (string.IsNullOrEmpty(url)) return;
            DirAccess.MakeDirRecursiveAbsolute("user://avatars");
            string abs = ProjectSettings.GlobalizePath("user://avatars/current.ska");
            if (await _api.DownloadToAsync(url, abs))
            {
                _localAvatarPath = "user://avatars/current.ska";
                GD.Print($"equipped custom avatar from {url}");
            }
        }
        catch (Exception e) { GD.PrintErr($"avatar fetch failed: {e.Message}"); }
    }

    /// Fetch the logged-in user's block list so blocked peers show as beans in-world.
    private async Task FetchBlockList()
    {
        try
        {
            _blockedUserIds.Clear();
            var blocked = await _api.GetBlockedUsersAsync();
            foreach (var id in blocked) _blockedUserIds.Add(id);
            GD.Print($"block list: {_blockedUserIds.Count} blocked users");
        }
        catch (Exception e) { GD.PrintErr($"block list fetch failed: {e.Message}"); }
    }

    /// Sign in, then go where the launch intent says: a deep-linked world, or Home.
    private async Task LoginThenRoute()
    {
        try
        {
            _api = new ApiClient(ApiBaseUrl);
            var pkce = new PkceFlow();

            ShowLoading("Opening your browser to sign in…");
            OS.ShellOpen(pkce.AuthorizeUrl(AccountsBaseUrl, ClientId));
            GD.Print("opened browser for login…");

            SetLoadingStatus("Waiting for you to sign in…");
            string code = await pkce.WaitForCodeAsync(TimeSpan.FromMinutes(3));

            SetLoadingStatus("Signing in…");
            var user = await _api.ExchangeAsync(code, pkce.Verifier);
            _username = user.GetProperty("username").GetString();
            _currentAvatarId = ReadCurrentAvatarId(user);
            GD.Print($"logged in as {_username}");

            // Fetch the user's chosen avatar (or a default outfit) so uploaded avatars are worn.
            SetLoadingStatus("Loading your avatar…");
            await FetchCurrentAvatar();
            _ = FetchBlockList();

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

    /// Sign in with email+password (no browser required), then route the same as PKCE.
    private async Task LoginWithEmailRoute(string email, string password)
    {
        try
        {
            _api = new ApiClient(ApiBaseUrl);

            ShowLoading("Signing in…");
            var user = await _api.LoginWithEmailAsync(email, password);
            _username = user.GetProperty("username").GetString();
            _currentAvatarId = ReadCurrentAvatarId(user);
            GD.Print($"logged in as {_username} (email)");

            SetLoadingStatus("Loading your avatar…");
            await FetchCurrentAvatar();
            _ = FetchBlockList();

            if (_pendingIntent.Kind == DeepLink.Kind.World)
            {
                await JoinWorldById(_pendingIntent.Arg);
                _pendingIntent = DeepLink.Intent.None;
            }
            else
            {
                CallDeferred(nameof(EnterHome));
            }
        }
        catch (Exception e)
        {
            GD.PrintErr($"email login failed: {e.Message}");
            CallDeferred(nameof(ShowLoginError), FriendlyError(e));
        }
    }

    /// Join the built-in default world (the multiplayer commons) from within Home.
    private async Task JoinDefaultWorld()
    {
        if (_api == null) return;
        try
        {
            ShowLoading("Finding a world…");
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
    /// Downloads the world file first if a downloadUrl is available (VRChat-style caching).
    /// The id of the multiplayer world we're currently in, for building invite deep links.
    /// Null while in Home (single-player, nothing to invite to).
    private string _currentWorldId;

    private async Task JoinWorldById(string worldId)
    {
        _currentWorldId = worldId;
        try
        {
            // Check if we have a downloadUrl for this world and download it if not cached.
            if (_fetchedWorlds != null)
            {
                var match = _fetchedWorlds.Find(w => w.id == worldId);
                if (match.downloadUrl != null)
                {
                    ShowLoading("Downloading world…");
                    string localPath = await _api.DownloadWorldAsync(match.downloadUrl, worldId);
                    if (localPath != null)
                        GD.Print($"world cached at {localPath}");
                }
            }

            ShowLoading("Joining world…");
            // Match into an existing open instance if one has room (so players actually meet),
            // else this creates a fresh one.
            var joined = await _api.JoinWorldInstanceAsync(worldId);
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
        _currentWorldId = null;
        TeardownRemotes();
        _transport?.Disconnect();
        _transport = null;
        BuildHomeWorld();
        if (_local == null) SpawnLocalPlayer();
        MoveLocalTo(_homeInfo.Spawn);
        _worldName = "Home";
        HideLoading();

        // Home is a fully playable single-player space — no forced modal. Walk around freely;
        // step into the portal (or open the pause menu → Worlds) to travel.
        _hud?.HideAll();
        _inWorldHud?.SetWorld("Home");
        _inWorldHud?.SetPlayerCount(1);
        if (_localDesktop != null) _localDesktop.ControlsEnabled = true;
        if (!DisplayServer.GetName().Equals("headless"))
            Input.MouseMode = Input.MouseModeEnum.Captured;
        _inWorldHud?.Toast("Welcome home · walk into the portal to travel · T to chat · Esc for menu", 6);

        _ = PopulateWorldList();
        MaybeStartTutorial();
    }

    // ── Home world list (opened from the pause menu, not forced) ──────────────────────

    private void OpenWorldList()
    {
        _hud?.ShowWorldList(_username);
        if (_localDesktop != null) _localDesktop.ControlsEnabled = false;
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    private void CloseWorldList()
    {
        _hud?.HideAll();
        if (_inHome && _localDesktop != null) _localDesktop.ControlsEnabled = true;
        if (_inHome && !DisplayServer.GetName().Equals("headless"))
            Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    /// Where the local player last spawned in the current world — the target for respawn.
    private Vector3 _spawnPos = new(0, 1, 0);

    /// Teleport the local player (works for both desktop and VR rigs). Records the position
    /// as the current spawn so "Respawn" returns here.
    private void MoveLocalTo(Vector3 pos)
    {
        _spawnPos = pos;
        if (_localNode is Node3D n) n.GlobalPosition = pos;
    }

    /// Return the local player to the current world's spawn point and kill any momentum —
    /// the "unstick me" button for falling through geometry, getting wedged, or flung by
    /// physics. Works in Home and multiplayer alike (ownership means the relay just sees us
    /// move, no special-casing needed).
    private void RespawnLocal()
    {
        if (_localNode is Node3D n)
        {
            n.GlobalPosition = _spawnPos;
            if (_localDesktop != null) _localDesktop.ResetMotion();
        }
        _inWorldHud?.Toast("Respawned", 2);
    }

    /// Copy a shareable deep link to the current multiplayer world onto the clipboard, so a
    /// friend can paste it and their client (via the serikasocial:// handler) joins here.
    private void CopyInviteLink()
    {
        if (string.IsNullOrEmpty(_currentWorldId))
        {
            _inWorldHud?.Toast("No world to invite to", 2);
            return;
        }
        DisplayServer.ClipboardSet($"serikasocial://world/{_currentWorldId}");
        _inWorldHud?.Toast("Invite link copied to clipboard", 3);
    }

    // ── Avatar selector ───────────────────────────────────────────────────────────────

    /// The id of the avatar the player currently wears (from login / after equipping), so the
    /// selector can mark it and skip re-equipping it.
    private string _currentAvatarId;

    private void OpenAvatarSelector()
    {
        if (_api == null || _avatarSelector == null) return;
        _pauseMenu?.Hide();
        _avatarSelector.Configure(_api, _currentAvatarId);
        _avatarSelector.Open();
        if (_localDesktop != null) _localDesktop.ControlsEnabled = false;
    }

    private void OnAvatarSelectorClosed()
    {
        if ((_inWorld || _inHome) && _localDesktop != null && _chat is { IsTyping: false })
            _localDesktop.ControlsEnabled = true;
        if (!DisplayServer.GetName().Equals("headless"))
            Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    /// Download the chosen avatar, swap the live rig, and persist the choice server-side.
    /// Each avatar is cached under its own `user://avatars/<id>.ska` so re-equipping a
    /// previously worn one is instant and never collides with AvatarLibrary's path cache.
    private async Task EquipAvatar(string id, string downloadUrl, string name)
    {
        try
        {
            _inWorldHud?.Toast($"Equipping {name}…", 2);
            DirAccess.MakeDirRecursiveAbsolute("user://avatars");
            string rel = $"user://avatars/{SanitizeId(id)}.ska";
            string abs = ProjectSettings.GlobalizePath(rel);

            if (!System.IO.File.Exists(abs) && !await _api.DownloadToAsync(downloadUrl, abs))
            {
                _inWorldHud?.Toast("Couldn't download that avatar", 3);
                return;
            }

            var avatar = AvatarLibrary.Instantiate(rel);
            if (avatar == null)
            {
                _inWorldHud?.Toast("That avatar couldn't be loaded", 3);
                return;
            }

            _localAvatarPath = rel;
            _currentAvatarId = id;
            _localDesktop?.SetAvatar(avatar);

            // Persist so it's worn on the next join and by remotes after they resync.
            try { await _api.SelectAvatarAsync(id); }
            catch (Exception e) { GD.PrintErr($"avatar select persist failed: {e.Message}"); }

            _inWorldHud?.Toast($"Now wearing {name}", 3);
        }
        catch (Exception e)
        {
            GD.PrintErr($"equip failed: {e.Message}");
            _inWorldHud?.Toast("Couldn't equip that avatar", 3);
        }
    }

    /// Pull the user's chosen avatar id out of the login response, or null if none set.
    private static string ReadCurrentAvatarId(System.Text.Json.JsonElement user) =>
        user.TryGetProperty("currentAvatarId", out var v)
        && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? v.GetString()
            : null;

    /// Keep avatar ids to filename-safe characters before using one as a path component.
    private static string SanitizeId(string id)
    {
        var sb = new System.Text.StringBuilder(id.Length);
        foreach (char c in id)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return sb.Length == 0 ? "current" : sb.ToString();
    }

    private bool _tutorialShown;

    /// Show the first-time tutorial once via Dialogue Manager. Frees the mouse and suspends
    /// controls while the dialogue balloon is up. Loads a platform-specific dialogue file.
    private void MaybeStartTutorial()
    {
        if (_tutorialShown || _smoke || Tutorial.AlreadySeen()) return;
        _tutorialShown = true;

        if (_localDesktop != null) _localDesktop.ControlsEnabled = false;
        Input.MouseMode = Input.MouseModeEnum.Visible;

        string dialoguePath;
        if (_vrMode)
            dialoguePath = "res://Dialogue/tutorial_vr.dialogue";
        else if (DisplayServer.IsTouchscreenAvailable())
            dialoguePath = "res://Dialogue/tutorial_mobile.dialogue";
        else
            dialoguePath = "res://Dialogue/tutorial_desktop.dialogue";

        var dialogueRes = ResourceLoader.Load<Resource>(dialoguePath);
        if (dialogueRes == null)
        {
            GD.PrintErr($"tutorial dialogue not found at {dialoguePath} — skipping tutorial");
            if (_localDesktop != null) _localDesktop.ControlsEnabled = true;
            Input.MouseMode = Input.MouseModeEnum.Captured;
            return;
        }

        DialogueManagerRuntime.DialogueManager.DialogueEnded += OnTutorialEnded;
        DialogueManagerRuntime.DialogueManager.ShowExampleDialogueBalloon(dialogueRes, "tutorial_start");
    }

    private void OnTutorialEnded(Resource _)
    {
        DialogueManagerRuntime.DialogueManager.DialogueEnded -= OnTutorialEnded;
        Tutorial.MarkSeen();
        if (_localDesktop != null) _localDesktop.ControlsEnabled = true;
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    private List<(string id, string name, string description, int capacity, string author, string downloadUrl)> _fetchedWorlds;

    private async Task PopulateWorldList()
    {
        if (_api == null) return;
        try
        {
            var worlds = await _api.GetWorldsAsync();
            var list = new List<(string id, string name, string description, int capacity, string author, string downloadUrl)>();
            foreach (var w in worlds.EnumerateArray())
            {
                string id = w.GetProperty("id").GetString();
                string name = w.GetProperty("name").GetString();
                string desc = w.TryGetProperty("description", out var d) ? d.GetString() : "";
                int cap = w.TryGetProperty("capacity", out var c) ? c.GetInt32() : 32;
                string author = w.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
                string dlUrl = w.TryGetProperty("downloadUrl", out var dl) && dl.ValueKind == JsonValueKind.String ? dl.GetString() : null;
                list.Add((id, name, desc ?? "", cap, author, dlUrl));
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

    private void ShowLoginError(string message)
    {
        HideLoading();
        _hud?.ShowError(message);
    }

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
        _inHome = false;
        _worldName = worldName;
        ShowLoading($"Connecting to {worldName}…");
        BuildCommonsWorld();
        SpawnLocalPlayer();
        MoveLocalTo(new Vector3(0, 1, 8));
        ConnectTo(endpoint, ticket);
    }

    private void ConnectTo(string endpoint, string ticket)
    {
        var udp = new UdpTransport();
        udp.Connected += OnConnected;
        udp.PeerJoined += OnPeerJoined;
        udp.PeerLeft += OnPeerLeft;
        udp.PoseReceived += OnPoseReceived;
        udp.ChatReceived += OnChatReceived;
        udp.VoiceReceived += OnVoiceReceived;
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
        _peerNames.Clear();
        _peerUserIds.Clear();
        foreach (var p in peers)
        {
            SpawnRemote(p);
            _peerNames[p.PeerId] = p.Name;
            if (!string.IsNullOrEmpty(p.UserId)) _peerUserIds[p.PeerId] = p.UserId;
        }
        int others = peers.Length;
        string who = others == 0 ? "You're the first one here." : $"{others} other {(others == 1 ? "person" : "people")} here.";
        HideLoading();
        _hud?.HideWithToast($"Welcome to {_worldName}. {who}");
        _inWorld = true;
        _inWorldHud.SetWorld(_worldName);
        _inWorldHud.SetPlayerCount(1 + others);
        _chat.AddSystem($"Welcome to {_worldName}.");
    }

    private void OnPeerJoined(PeerInfo p)
    {
        GD.Print($"SMOKE peer_join {p.PeerId} {p.Name}");
        SpawnRemote(p);
        if (!string.IsNullOrEmpty(p.UserId)) _peerUserIds[p.PeerId] = p.UserId;
        _peerNames[p.PeerId] = p.Name;
        _inWorldHud.SetPlayerCount(1 + _remotes.Count);
        _chat.AddSystem($"{p.Name} joined the world");
    }

    private void OnPeerLeft(uint peerId)
    {
        if (_remotes.Remove(peerId, out var a)) a.QueueFree();
        _peerUserIds.Remove(peerId);
        string name = _peerNames.GetValueOrDefault(peerId, $"peer{peerId}");
        _peerNames.Remove(peerId);
        _inWorldHud.SetPlayerCount(1 + _remotes.Count);
        _chat.AddSystem($"{name} left the world");
    }

    private void OnPoseReceived(uint peerId, PoseFrame frame)
    {
        if (_remotes.TryGetValue(peerId, out var a)) a.ApplyPose(frame);
        if (_smoke) GD.Print($"SMOKE pose_from {peerId} seq={frame.Sequence}");
    }

    // ── Voice chat ────────────────────────────────────────────────────────────────────

    private void ToggleMic()
    {
        _micActive = !_micActive;
        if (_micActive) _voice.StartRecording();
        else _voice.StopRecording();
        _inWorldHud?.SetMicEnabled(_micActive);
        if (!_micActive) _inWorldHud?.SetMicLevel(0f);
        _inWorldHud?.Toast(_micActive ? "Microphone ON" : "Microphone OFF");
    }

    private void OnVoiceFrameReady(byte[] pcm)
    {
        byte rms = ComputeRms(pcm);
        _inWorldHud?.SetMicLevel(rms / 255f); // drive the bottom-left mic pulse from live capture
        if (_transport is not { Connected_: true }) return;
        var frame = new VoiceFrame { Sequence = 0, Rms = rms, Payload = pcm };
        _transport.SendVoice(frame);
    }

    private void OnVoiceReceived(uint peerId, VoiceFrame frame)
    {
        if (!_remotes.TryGetValue(peerId, out var avatar)) return;
        var player = avatar.GetNodeOrNull<AudioStreamPlayer3D>("VoicePlayer");
        if (player == null)
        {
            player = new AudioStreamPlayer3D { Name = "VoicePlayer", MaxDistance = 15f, UnitSize = 8f };
            avatar.AddChild(player);
        }
        _voice.PlayFrame(player, frame.Payload);
    }

    private static byte ComputeRms(byte[] pcm)
    {
        long sum = 0;
        int samples = pcm.Length / 2;
        for (int i = 0; i < samples; i++)
        {
            short s = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            sum += (long)s * s;
        }
        if (samples == 0) return 0;
        double rms = System.Math.Sqrt((double)sum / samples) / 32767.0;
        return (byte)System.Math.Clamp(rms * 255, 0, 255);
    }

    private void SpawnRemote(PeerInfo p)
    {
        if (_remotes.ContainsKey(p.PeerId)) return;
        var a = RemoteAvatar.Create(p.PeerId, p.Name);
        _remotes[p.PeerId] = a;
        AddChild(a);
        if (!string.IsNullOrEmpty(p.UserId) && _blockedUserIds.Contains(p.UserId))
            a.ShowBean();
    }

    // ── Per-frame ────────────────────────────────────────────────────────────────────

    // ── Pause menu ──────────────────────────────────────────────────────────────────

    /// Esc opens the pause menu whenever we're somewhere playable — Home included. The menu
    /// itself handles Esc-closes; while it's up the player's movement/look are suspended.
    private void OpenPauseMenu()
    {
        _pauseMenu.ShowMenu(_worldName, _inHome);
        if (_localDesktop != null) _localDesktop.ControlsEnabled = false;
    }

    private void OnPauseClosed()
    {
        // Re-enable controls unless another overlay that owns input is up right after us.
        if ((_inWorld || _inHome) && _localDesktop != null && _chat is { IsTyping: false })
            _localDesktop.ControlsEnabled = true;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape } && (_inWorld || _inHome) && !_pauseMenu.IsOpen)
        {
            OpenPauseMenu();
            GetViewport().SetInputAsHandled();
        }

        // V toggles first/third person (desktop only).
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.V }
            && _localDesktop != null && _chat is { IsTyping: false } && _pauseMenu is { IsOpen: false })
        {
            bool fp = _localDesktop.ToggleCameraMode();
            _persistThirdPerson = !fp;
            _inWorldHud?.Toast(fp ? "First-person view" : "Third-person view");
            GetViewport().SetInputAsHandled();
        }

        // T opens the text chat (in Home or a world), when not already typing.
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.T }
            && (_inWorld || _inHome) && _chat is { IsTyping: false } && _pauseMenu is { IsOpen: false })
        {
            OpenChat();
            GetViewport().SetInputAsHandled();
        }

        // M toggles the microphone (push-to-talk toggle).
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.M }
            && (_inWorld || _inHome) && _chat is { IsTyping: false } && _pauseMenu is { IsOpen: false })
        {
            ToggleMic();
            GetViewport().SetInputAsHandled();
        }

        // Emote keys: B=sit, N=dance, H=wave. Press again to cancel.
        if (@event is InputEventKey { Pressed: true, Echo: false } k
            && (_inWorld || _inHome) && _localDesktop != null
            && _chat is { IsTyping: false } && _pauseMenu is { IsOpen: false })
        {
            AvatarInstance.Emote? emote = k.Keycode switch
            {
                Key.B => AvatarInstance.Emote.Sit,
                Key.N => AvatarInstance.Emote.Dance,
                Key.H => AvatarInstance.Emote.Wave,
                _ => null,
            };
            if (emote.HasValue)
            {
                _localDesktop.PlayEmote(emote.Value);
                _inWorldHud?.Toast(emote.Value == AvatarInstance.Emote.None ? "Emote cancelled" : $"Emote: {emote.Value}");
                GetViewport().SetInputAsHandled();
            }
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
