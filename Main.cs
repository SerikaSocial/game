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
    private VrPlayer _localVr;         // non-null in VR mode; the two are mutually exclusive
    private Node3D _localNode;
    private UI.InteractionPrompt _interactPrompt;
    private UI.VideoQueuePanel _videoQueuePanel;
    private UI.SettingsMenu _settingsMenu;
    private SerikaSocial.World.Interactor _interactor;
    /// One per VR hand, indexed 0 = left, 1 = right to match `VrPlayer.InteractPressed`.
    private readonly System.Collections.Generic.List<SerikaSocial.World.Interactor> _vrInteractors = new();
    private bool _vrMode;
    // In VR the only XRCamera3D lives inside VrPlayer, which is not spawned until after login.
    // Until then the XR viewport has no camera at all, so both eyes rendered an empty grey void
    // and the login screen was invisible in the headset. This temporary rig gives the XR
    // viewport a camera from startup; SpawnLocalPlayer frees it when the real rig takes over.
    private XROrigin3D _bootXrOrigin;
    // In VR every CanvasLayer is parented to this surface's SubViewport instead of to Main, so
    // the UI reaches the eye buffers via a world-space quad. Null in desktop/mobile mode.
    private UI.VrUiSurface _vrUi;
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
    private QuickMenu _quickMenu;
    private MainMenu _mainMenu;
    private ActionMenu _actionMenu;
    private CameraMenu _cameraMenu;
    private PhotoCamera _photoCam;
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
    private StrokeCanvas _strokeCanvas;
    private readonly List<HeldItemController> _heldItemControllers = new();

    public override void _Ready()
    {
        // Apply the purple brand theme to every Control in the client at once.
        GetTree().Root.Theme = Brand.Theme;

        // Detect the device tier and apply saved graphics settings before the first frame, so a
        // Quest never renders one frame at full desktop quality. Also seeds persisted control/
        // audio prefs used below.
        UI.DeviceProfile.Detect();
        _persistThirdPerson = UI.DeviceProfile.Settings.StartThirdPerson;

        // Initialize VR as early as possible — on Quest, OpenXR must be up before the
        // first viewport render or the app stays in a 2D panel instead of immersive VR.
        bool isTouchscreen = DisplayServer.IsTouchscreenAvailable();
        bool hasQuest = OS.HasFeature("quest");
        bool hasAndroid = OS.HasFeature("android");
        GD.Print($"VR decision: hasQuest={hasQuest} hasAndroid={hasAndroid} isTouchscreen={isTouchscreen}");
        bool wantVr = hasQuest
            || (hasAndroid && !isTouchscreen)
            || System.Array.IndexOf(OS.GetCmdlineArgs(), "--vr") >= 0
            || System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--vr") >= 0;
        GD.Print($"VR decision: wantVr={wantVr}");
        _vrMode = wantVr && VrPlayer.TryInitVr();
        GD.Print($"VR decision: _vrMode={_vrMode}");

        _worldRoot = new Node3D { Name = "WorldRoot" };
        AddChild(_worldRoot);
        _audio = new SpatialAudioManager { Name = "SpatialAudio" };
        AddChild(_audio);
        _voice = new VoiceManager { Name = "VoiceManager" };
        AddChild(_voice);
        _voice.VoiceFrameReady += OnVoiceFrameReady;
        Worlds.BuildLoginBackdrop(_worldRoot); // neutral backdrop behind the login screen
        if (_vrMode)
        {
            // The panel must exist *before* the boot rig, because the rig's laser pointer is
            // handed the surface it aims at, and before any UI is constructed below, since
            // AddUi() routes layers into it.
            _vrUi = new UI.VrUiSurface { Name = "VrUi" };
            AddChild(_vrUi);
            SpawnBootXrRig();
        }

        var args = ParseArgs();
        if (args.ContainsKey("serika-animtest"))
        {
            AnimDiagnostic.Run(this, args.GetValueOrDefault("clip", "Walk"),
                args.GetValueOrDefault("ska", null));
            return;
        }
        if (args.ContainsKey("serika-worldtest"))
        {
            WorldDiagnostic.Run(this, _worldRoot, args.GetValueOrDefault("world", null),
                args.GetValueOrDefault("ogv", null), args.GetValueOrDefault("shot", null));
            return;
        }
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
        AddUi(_hud);
        _hud.LoginPressed += () => _ = LoginThenRoute();
        _hud.EmailLoginPressed += (email, pass) => _ = LoginWithEmailRoute(email, pass);
        _hud.RetryPressed += () => _ = LoginThenRoute();
        _hud.HomePressed += EnterHome;
        _hud.JoinCommonsPressed += () => OpenWorldList();
        _hud.JoinWorldPressed += (worldId) => _ = ShowWorldDetailFor(worldId);
        _hud.JoinWorldFromDetailPressed += (worldId) => _ = JoinWorldById(worldId);
        _hud.WorldListClosed += CloseWorldList;

        // Loading screen must exist before TryRestoreSession so it can be shown
        // during session restore and startup checks.
        _loading = new LoadingScreen { Name = "LoadingScreen", Visible = false };
        AddUi(_loading);

        // Dev aid: SERIKA_DEBUG_LOADING=1 shows the loading screen immediately (for screenshots).
        if (OrDefault("SERIKA_DEBUG_LOADING", "") == "1")
            ShowLoading("Connecting to The Commons…");

        // Try to restore a saved session before showing the login screen.
        _ = TryRestoreSession();

        // Auto-updater: check CDN for a newer version. Non-blocking — runs in the
        // background and shows a dialog only if an update is available.
        _updater = new Updater { Name = "Updater" };
        AddUi(_updater);
        _updater.CurrentVersion = Hud.ClientVersion;
        _updater.CheckForUpdates();

        // The single pause hub. (The old always-hidden PauseMenu that duplicated all of this
        // has been removed — this is the only pause surface now.)
        _quickMenu = new QuickMenu { Name = "QuickMenu" };
        AddUi(_quickMenu);
        _quickMenu.HomePressed += EnterHome;
        _quickMenu.RespawnPressed += RespawnLocal;
        _quickMenu.QuitPressed += () => GetTree().Quit();
        _quickMenu.OpenMainMenuWorlds += () => { _mainMenu?.Open(_username, 1); SyncMenuHold(); };
        _quickMenu.OpenMainMenuAvatars += () => { OpenAvatarSelector(); SyncMenuHold(); };
        _quickMenu.OpenCameraMenu += OpenCameraMenu;
        _quickMenu.OpenRadialMenu += () => { _actionMenu?.Open(); SyncMenuHold(); };
        _quickMenu.OpenVideoQueue += () => { if (_videoQueuePanel?.HasVideo ?? false) _videoQueuePanel.Open(); else _inWorldHud?.Toast("No video screen in this world", 2); };
        _quickMenu.OpenSettings += () => { _settingsMenu?.Open(); SyncMenuHold(); };
        _quickMenu.CopyInvitePressed += CopyInviteLink;
        _quickMenu.MicTogglePressed += () => { ToggleMic(); _quickMenu.SetMic(_micActive); };
        _quickMenu.Closed += OnPauseClosed;

        // VRChat-style Main Menu (Big Menu)
        _mainMenu = new MainMenu { Name = "MainMenu" };
        AddUi(_mainMenu);
        _mainMenu.JoinWorldPressed += (id) => _ = ShowWorldDetailFor(id);
        _mainMenu.AvatarChosen += (id, url, name) => _ = EquipAvatar(id, url, name);
        _mainMenu.ImageLoader = url => _api.GetImageBytesAsync(url);
        _mainMenu.Closed += OnPauseClosed;

        // VRChat-style Action Menu (Radial Pie Menu)
        _actionMenu = new ActionMenu { Name = "ActionMenu" };
        AddUi(_actionMenu);
        _actionMenu.HomePressed += EnterHome;
        _actionMenu.RespawnPressed += RespawnLocal;
        _actionMenu.CameraPressed += OpenCameraMenu;
        _actionMenu.EmotePressed += e => { _localDesktop?.PlayEmote(e); _localVr?.PlayEmote(e); };
        _actionMenu.CustomEmotePressed += clip => { _localDesktop?.PlayCustomEmote(clip); _localVr?.PlayCustomEmote(clip); };
        _actionMenu.Closed += OnPauseClosed;

        // VRChat-style Camera & Photo Viewfinder Menu
        // The free-flying photo camera the viewfinder renders. Lives on the root so it keeps
        // filming while the player stands still, and survives world switches.
        _photoCam = new PhotoCamera { Name = "PhotoCamera" };
        AddChild(_photoCam);

        _cameraMenu = new CameraMenu { Name = "CameraMenu" };
        AddUi(_cameraMenu);
        _cameraMenu.Bind(_photoCam);
        _cameraMenu.PhotoTaken += () => _inWorldHud?.Toast("📷 Photo saved to user disk!");
        _cameraMenu.Closed += OnPauseClosed;

        _avatarSelector = new AvatarSelector { Name = "AvatarSelector" };
        AddUi(_avatarSelector);
        _avatarSelector.AvatarChosen += (id, url, name) => _ = EquipAvatar(id, url, name);
        _avatarSelector.Closed += OnAvatarSelectorClosed;

        // One owner for the cursor and for whether movement is live. Every screen takes a
        // named hold instead of poking Input.MouseMode itself; the sink pushes the resulting
        // controls-live flag onto whichever rig is currently spawned.
        UI.InputMode.ControlsSink = live =>
        {
            if (_localDesktop != null) _localDesktop.ControlsEnabled = live;
            if (_localVr != null) _localVr.ControlsEnabled = live;
        };

        _interactPrompt = new UI.InteractionPrompt { Name = "InteractionPrompt" };
        AddUi(_interactPrompt);

        _videoQueuePanel = new UI.VideoQueuePanel { Name = "VideoQueuePanel" };
        AddUi(_videoQueuePanel);
        _videoQueuePanel.Closed += SyncMenuHold;
        SerikaSocial.World.Video.VideoScreen.InteractionRequested += () =>
        {
            if (_videoQueuePanel?.HasVideo ?? false)
            {
                _videoQueuePanel.Open();
                SyncMenuHold();
            }
        };

        _settingsMenu = new UI.SettingsMenu { Name = "SettingsMenu" };
        AddUi(_settingsMenu);
        _settingsMenu.Closed += SyncMenuHold;
        _settingsMenu.SettingChanged += OnSettingChanged;

        _inWorldHud = new InWorldHud { Name = "InWorldHud" };
        AddUi(_inWorldHud);

        _chat = new ChatOverlay { Name = "ChatOverlay" };
        AddUi(_chat);
        _chat.MessageSubmitted += OnChatSubmitted;
        _chat.Closed += OnChatClosed;

    }

    /// Show the 3D loading screen with a status line during sign-in / connecting.
    private void ShowLoading(string status)
    {
        // Nothing to control behind a loading screen — free the cursor and stop feeding input
        // to whatever rig is mid-teardown. EnterHome / OnConnected set this back.
        UI.InputMode.SetPlayable(false);
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
        UI.InputMode.Hold(UI.InputMode.Chat);
    }

    private void OnChatSubmitted(string text)
    {
        // Echo locally, then send to the room (if connected).
        _chat.AddChat(_username, text);
        _transport?.SendChat(text);
    }

    /// Restore control/mouse after the chat box closes (whether via Enter or Escape).
    private void OnChatClosed() => UI.InputMode.Release(UI.InputMode.Chat);

    private void OnChatReceived(uint senderId, string text)
    {
        string name = _peerNames.GetValueOrDefault(senderId, $"peer{senderId}");
        _chat.AddChat(name, text);
    }

    // ── Physics object sync ────────────────────────────────────────────────────────────

    private readonly Dictionary<ushort, PhysicsProp> _props = new();

    /// Register a PhysicsProp so it can receive network sync updates.
    public void RegisterPhysicsProp(PhysicsProp prop)
    {
        _props[prop.NetId] = prop;
    }

    private void OnObjectSyncReceived(uint senderPeer, ushort objId,
        float x, float y, float z, float qx, float qy, float qz, float qw,
        float lvx, float lvy, float lvz)
    {
        // Stroke segments alias the upper NetId range — route them to the canvas, not to props.
        if (NetIds.IsStroke(objId))
        {
            if (_strokeCanvas != null && StrokeNetwork.TryUnpack(
                    x, y, z, qx, qy, qz, qw, lvx, lvy, lvz,
                    out var strokeId, out var idx, out var pos, out var hue, out var radius, out var flag))
            {
                _strokeCanvas.ApplyNetworkPoint(new StrokePoint(strokeId, idx, pos, hue, radius, flag));
            }
            return;
        }

        if (_props.TryGetValue(objId, out var prop))
            prop.ApplyNetworkSync(x, y, z, qx, qy, qz, qw, lvx, lvy, lvz);
    }

    private void OnPhysGrabReceived(uint senderPeer, byte grabType, ushort boneOrObjId, float x, float y, float z)
    {
        // grabType: 0=start, 1=update, 2=release
        // boneOrObjId: if it maps to a PhysicsProp, it's a prop grab; otherwise it's a hair/bone grab
        // on a remote avatar.
        if (_props.TryGetValue(boneOrObjId, out var prop))
        {
            if (grabType == 2) prop.ApplyNetworkRelease(x, y, z);
            return;
        }

        // Hair/PhysBone grab on a remote avatar: find the avatar's SpringBoneSystem.
        if (_remotes.TryGetValue(senderPeer, out var avatar))
        {
            var spring = FindSpringBones(avatar);
            if (spring == null) return;
            int chainIdx = boneOrObjId;
            if (grabType == 0) spring.ApplyRemoteGrab(chainIdx, new Vector3(x, y, z));
            else if (grabType == 1) spring.ApplyRemoteGrab(chainIdx, new Vector3(x, y, z));
            else if (grabType == 2) spring.ReleaseRemoteGrab(chainIdx);
        }
    }

    public override void _ExitTree()
    {
        ClearInstanceLock();
    }

    private static Avatar.SpringBoneSystem FindSpringBones(Node root)
    {
        for (int i = 0; i < root.GetChildCount(); i++)
        {
            var child = root.GetChild(i);
            if (child is Avatar.SpringBoneSystem sb) return sb;
            for (int j = 0; j < child.GetChildCount(); j++)
            {
                var grand = child.GetChild(j);
                if (grand is Avatar.SpringBoneSystem sb2) return sb2;
            }
        }
        return null;
    }

    // ── World ───────────────────────────────────────────────────────────────────────

    private Node3D _worldRoot;
    private Worlds.Home _homeInfo;

    /// Swap the active world geometry: free the old root, build the new space into a fresh one.
    private void SwapWorld(System.Action<Node3D> build)
    {
        _audio?.StopAllAmbient();
        _strokeCanvas?.QueueFree();
        _strokeCanvas = null;
        _worldRoot?.QueueFree();
        _worldRoot = new Node3D { Name = "WorldRoot" };
        AddChild(_worldRoot);
        build(_worldRoot);
        // A freshly built world's lights and environment come up at full quality no matter what
        // tier the device is on, so the profile has to be re-stamped onto every new world root.
        UI.DeviceProfile.ApplyToScene(_worldRoot);
        // Stroke canvas lives in every world — Home is single-player so strokes are local-only,
        // but The Commons and other multiplayer worlds broadcast them over ObjectSync.
        _strokeCanvas = new StrokeCanvas();
        _worldRoot.AddChild(_strokeCanvas);
        SetupVideoForWorld();
    }

    private SerikaSocial.World.Video.VideoManager _videoManager;

    /// After a world builds, wire any VideoScreens it exposed to a fresh per-world manager and
    /// point the queue panel at it. Worlds with no screen get no manager, and the pause menu's
    /// "Video Queue" button stays hidden.
    private void SetupVideoForWorld()
    {
        if (_videoManager != null) { _videoManager.QueueFree(); _videoManager = null; }
        _videoQueuePanel?.Hide();

        var screens = GetTree().GetNodesInGroup(SerikaSocial.World.Video.VideoScreen.Group);
        if (screens.Count == 0) { _videoQueuePanel?.Bind(null); return; }

        _videoManager = new SerikaSocial.World.Video.VideoManager { Name = "VideoManager" };
        AddChild(_videoManager);
        _videoManager.Configure(_api, _worldName);
        _videoManager.Toast += (msg, secs) => _inWorldHud?.Toast(msg, secs);
        // Skip screens belonging to the world we just left. QueueFree is deferred to the end of
        // the frame, so the outgoing world's nodes are still in the group when this runs;
        // registering one meant the manager held a screen that was about to be freed, and the
        // next queue action hit a disposed VideoStreamPlayer.
        foreach (var s in screens)
            if (s is SerikaSocial.World.Video.VideoScreen vs && !vs.IsQueuedForDeletion())
                _videoManager.RegisterScreen(vs);
        _videoQueuePanel?.Bind(_videoManager);
    }

    /// Build the cosy Home and wire its portal to open the world browser.
    private void BuildHomeWorld()
    {
        SwapWorld(root => _homeInfo = Worlds.BuildHome(root));
        _homeInfo.CommonsPortal.Entered += OnPortalEntered;
    }

    private void OnPortalEntered(Portal portal)
    {
        if (_api == null) return;
        switch (portal.Mode)
        {
            case PortalMode.DirectWorld:
                if (!string.IsNullOrEmpty(portal.TargetWorldId))
                    _ = ShowWorldDetailFor(portal.TargetWorldId);
                break;
            case PortalMode.CuratedList:
                OpenWorldList(portal.AllowedWorldIds);
                break;
            case PortalMode.Invite:
                if (!string.IsNullOrEmpty(portal.TargetWorldId))
                    _ = JoinWorldById(portal.TargetWorldId);
                break;
            default:
                OpenWorldList(null);
                break;
        }
    }

    /// Mount a 2D UI layer.
    ///
    /// On desktop/mobile this is just `AddChild`. In VR the layer goes into the `VrUiSurface`
    /// SubViewport instead, because a `CanvasLayer` parented to the XR viewport draws into a 2D
    /// canvas that Godot never composites into the eye buffers — the reason the headset showed
    /// the world but no login screen or menus.
    private void AddUi(CanvasLayer layer)
    {
        if (_vrMode && _vrUi != null)
        {
            _vrUi.Viewport.AddChild(layer);
            GD.Print($"VR UI: mounted {layer.Name} on panel ({_vrUi.Viewport.GetChildCount()} layers)");
        }
        else AddChild(layer);
    }

    /// Give the XR viewport a camera before the player rig exists.
    ///
    /// Godot renders an XR viewport strictly through the active `XRCamera3D`. `VrPlayer` owns the
    /// only one, and it is not built until `SpawnLocalPlayer` runs after a successful login — so
    /// from app start until then the headset had no camera and showed a flat grey void, with the
    /// login UI nowhere to be seen. This stand-in origin/camera pair renders the login backdrop
    /// (and the `CanvasLayer` UI drawn over it) until the real rig replaces it.
    private void SpawnBootXrRig()
    {
        _bootXrOrigin = new XROrigin3D { Name = "BootXrOrigin" };
        // Matches the eye height VrPlayer spawns at, so the login screen sits at a sane level.
        _bootXrOrigin.Position = new Vector3(0, 1, 0);
        _bootXrOrigin.AddChild(new XRCamera3D { Name = "BootXrCamera", Near = 0.05f, Far = 1000f });

        // The login screen is a menu, so it needs controllers to point with. Both hands get a
        // visible marker; the right one also carries the laser that drives the UI panel.
        var left = new XRController3D { Name = "BootLeftHand", Tracker = "left_hand", ShowWhenTracked = true };
        var right = new XRController3D { Name = "BootRightHand", Tracker = "right_hand", ShowWhenTracked = true };
        left.AddChild(BootHandMarker());
        right.AddChild(BootHandMarker());
        _bootXrOrigin.AddChild(left);
        _bootXrOrigin.AddChild(right);
        _bootXrOrigin.AddChild(new UI.VrUiPointer(right, _vrUi) { Name = "BootPointer" });

        AddChild(_bootXrOrigin);
        GD.Print("VR: boot XR rig active (pre-login)");

        static MeshInstance3D BootHandMarker() => new()
        {
            Name = "HandMarker",
            Mesh = new SphereMesh { Radius = 0.03f, Height = 0.06f },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = Brand.Accent,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
        };
    }

    /// One `Interactor` per VR hand, each with its own hand-mounted hint label.
    ///
    /// Two rather than one because either hand should be able to point at a seat, and because the
    /// interactor caches a target — sharing one between hands would make the prompt flicker between
    /// whatever each hand happened to be near.
    private void SetupVrInteractors(VrPlayer vr)
    {
        var hands = new[] { (hand: vr.LeftHand, isLeft: true), (hand: vr.RightHand, isLeft: false) };
        foreach (var (hand, isLeft) in hands)
        {
            if (hand == null) continue;
            var label = new UI.VrInteractLabel { Name = "InteractHint" };
            hand.AddChild(label);

            var interactor = SerikaSocial.World.Interactor.Create(
                new SerikaSocial.World.VrHandInteractRig(vr, hand, isLeft), label,
                isLeft ? "InteractorLeft" : "InteractorRight");
            AddChild(interactor);
            _vrInteractors.Add(interactor);
        }

        // The hand index matches the order the rigs were added above.
        vr.InteractPressed += i =>
            i >= 0 && i < _vrInteractors.Count && (_vrInteractors[i]?.TryInteract() ?? false);
    }

    /// Create one HeldItemController per interaction slot: desktop gets one (left-click = use),
    /// VR gets two (one per hand, trigger = use). Each controller polls the held prop for an
    /// IUsable and drives UseBegin/Tick/End while the button is held.
    private void SetupHeldItemControllers()
    {
        if (_localVr != null)
        {
            var vr = _localVr;
            var hands = new[] { (hand: vr.LeftHand, isLeft: true), (hand: vr.RightHand, isLeft: false) };
            for (int i = 0; i < hands.Length; i++)
            {
                var (hand, isLeft) = hands[i];
                if (hand == null) continue;
                int handIndex = i; // capture
                var rig = new SerikaSocial.World.VrHandInteractRig(vr, hand, isLeft);
                var ctrl = HeldItemController.Create(
                    rig,
                    getHeldUsable: () => vr.GetHeldProp(handIndex) as IUsable,
                    getUseDown: () => vr.GetTriggerValue(handIndex) > 0.6f,
                    getHoldTransform: () => GodotObject.IsInstanceValid(hand) ? hand.GlobalTransform : Transform3D.Identity,
                    name: isLeft ? "HeldItemLeft" : "HeldItemRight");
                AddChild(ctrl);
                _heldItemControllers.Add(ctrl);
            }
        }
        else if (_localDesktop != null)
        {
            var desktop = _localDesktop;
            bool touch = DisplayServer.IsTouchscreenAvailable();
            var rig = new SerikaSocial.World.DesktopInteractRig(desktop,
                touch ? SerikaSocial.World.InteractSource.Touch : SerikaSocial.World.InteractSource.Desktop);
            var ctrl = HeldItemController.Create(
                rig,
                getHeldUsable: () =>
                {
                    // Desktop only holds one item at a time via the Interactor; find it by
                    // scanning the props registry for one that is held by local and is IUsable.
                    foreach (var prop in _props.Values)
                        if (prop.HeldByLocal && prop is IUsable u) return u;
                    return null;
                },
                getUseDown: () => Input.IsMouseButtonPressed(MouseButton.Left),
                getHoldTransform: () =>
                {
                    var eye = desktop.EyePosition;
                    var aim = desktop.AimForward;
                    var hand = eye + aim * 0.6f - new Vector3(0, 0.3f, 0);
                    return new Transform3D(Basis.LookingAt(aim, Vector3.Up), hand);
                },
                name: "HeldItemDesktop");
            AddChild(ctrl);
            _heldItemControllers.Add(ctrl);
        }
    }

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
            _localVr = null;
        }

        // Interactors hold a reference to the rig they drive, so they go with it.
        _interactor?.QueueFree();
        _interactor = null;
        foreach (var vrInteractor in _vrInteractors) vrInteractor?.QueueFree();
        _vrInteractors.Clear();
        foreach (var hic in _heldItemControllers) hic?.QueueFree();
        _heldItemControllers.Clear();
        _interactPrompt?.Clear();

        void FreeBootXrRig()
        {
            if (_bootXrOrigin == null) return;
            _bootXrOrigin.QueueFree();
            _bootXrOrigin = null;
        }

        // Bring up VR only when it makes sense: on a Quest/Android build, or when a desktop
        // user explicitly asks with `--vr`. Otherwise OpenXR is never touched, so a normal
        // desktop launch produces no "failed to load runtime / no HMD" errors.
        // On touchscreen Android phones, skip VR — the OpenXR loader in the APK can partially
        // initialise and leave the viewport in a broken state, which prevented the LocalPlayer
        // and touch controls from working.
        if (_vrMode)
        {
            // Two XRCamera3Ds in one tree fight over the XR viewport, so the boot rig must go
            // before the real one is added.
            FreeBootXrRig();
            var vr = new VrPlayer { Name = "LocalPlayer", Position = new Vector3(0, 1, 0) };
            AddChild(vr);
            vr.UiSurface = _vrUi; // the controller ray needs a panel to aim at
            _local = vr;
            _localNode = vr;
            _localVr = vr;
            // VR used to stay a capsule — no avatar, so remote peers saw a featureless blob and
            // no bone pose was ever broadcast. It equips the same avatar the desktop rig does.
            vr.SetAvatar(AvatarLibrary.InstantiateOrDefault(_localAvatarPath));
            _actionMenu?.SetCustomEmotes(vr.Avatar?.CustomEmotes);
            // The headset has no Esc key, so the controller face buttons are the only way in.
            vr.MenuPressed += () => { _quickMenu?.Open(_username); SyncMenuHold(); };
            vr.ActionMenuPressed += () => { _actionMenu?.Open(); SyncMenuHold(); };

            // One interactor per hand, so VR can finally use seats, lay spots, interaction points
            // and video screens — none of which grip can reach, and all of which were desktop-only.
            // The hint has to live on the hand: a CanvasLayer prompt is composited onto the menu
            // panel in VR, nowhere near the thing being described.
            SetupVrInteractors(vr);

            // Parity with desktop: name tag prefs and profile picture apply to the VR rig too.
            _ = ApplyLocalIdentity();
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
            _actionMenu?.SetCustomEmotes(desktop.Avatar?.CustomEmotes);
            // Restore persisted camera mode across world switches.
            if (_persistThirdPerson) desktop.SetFirstPerson(false);
            SetupTouchControls(desktop);

            // E-to-interact on desktop; on a touchscreen the same pipeline is driven by the
            // interact button in TouchControls, which is why the source is tagged.
            //
            // Touch passes no prompt: the button itself carries the verb and only appears when
            // there is something to use, so the corner hint would say the same thing twice.
            bool touch = DisplayServer.IsTouchscreenAvailable();
            _interactor = SerikaSocial.World.Interactor.Create(
                desktop, touch ? null : _interactPrompt,
                touch ? SerikaSocial.World.InteractSource.Touch : SerikaSocial.World.InteractSource.Desktop);
            AddChild(_interactor);
            if (_touch != null) _touch.Interactor = _interactor;

            // Name card: apply tag/pfp prefs and download the local player's profile picture.
            _ = ApplyLocalIdentity();
            GD.Print("Desktop mode");
        }

        // HeldItemController: translates use input (mouse/trigger/touch) into IUsable calls.
        // One for desktop (left-click = use), two for VR (one per hand, trigger = use).
        SetupHeldItemControllers();

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
        },
        onMenuToggle: () => CallDeferred(nameof(OpenPauseMenu)),
        onChatToggle: () => CallDeferred(nameof(OpenChat)),
        onMicToggle: () => CallDeferred(nameof(ToggleMic)),
        onActionToggle: () =>
        {
            if (_actionMenu?.IsOpen ?? false) _actionMenu.Hide();
            else { _actionMenu?.Open(); if (_localDesktop != null) _localDesktop.ControlsEnabled = false; if (_localVr != null) _localVr.ControlsEnabled = false; }
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
                AvatarLibrary.CurrentDefaultPath = _localAvatarPath;
                GD.Print($"equipped custom avatar from {url}");
            }
        }
        catch (Exception e) { GD.PrintErr($"avatar fetch failed: {e.Message}"); }
    }

    /// Cache the shared default outfit — what a peer wears until their own avatar resolves.
    /// Kept separate from the local player's avatar so remotes never clone the viewer.
    private async Task FetchDefaultOutfit()
    {
        try
        {
            var avatars = await _api.GetAvatarsAsync(mine: false);
            string url = null;
            foreach (var a in avatars.EnumerateArray())
            {
                bool isDefault = a.TryGetProperty("isDefaultOutfit", out var d) && d.ValueKind == JsonValueKind.True;
                if (!isDefault) continue;
                if (a.TryGetProperty("downloadUrl", out var u) && u.ValueKind == JsonValueKind.String)
                { url = u.GetString(); break; }
            }
            if (string.IsNullOrEmpty(url)) return;

            DirAccess.MakeDirRecursiveAbsolute("user://avatars");
            string rel = "user://avatars/default-outfit.ska";
            string abs = ProjectSettings.GlobalizePath(rel);
            if (System.IO.File.Exists(abs) || await _api.DownloadToAsync(url, abs))
                AvatarLibrary.DefaultOutfitPath = rel;
        }
        catch (Exception e) { GD.PrintErr($"default outfit fetch failed: {e.Message}"); }
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
            _localPfpUrl = ReadAvatarUrl(user);
            _localTrust = ReadTrust(user);
            GD.Print($"logged in as {_username}");

            SaveSession(_api.SessionToken);

            // Fetch the user's chosen avatar (or a default outfit) so uploaded avatars are worn.
            SetLoadingStatus("Loading your avatar…");
            await FetchCurrentAvatar();
            await FetchDefaultOutfit();
            _ = FetchBlockList();

            await RouteAfterLogin();
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
            _localPfpUrl = ReadAvatarUrl(user);
            _localTrust = ReadTrust(user);
            GD.Print($"logged in as {_username} (email)");

            SaveSession(_api.SessionToken);

            SetLoadingStatus("Loading your avatar…");
            await FetchCurrentAvatar();
            await FetchDefaultOutfit();
            _ = FetchBlockList();

            await RouteAfterLogin();
        }
        catch (Exception e)
        {
            GD.PrintErr($"email login failed: {e.Message}");
            CallDeferred(nameof(ShowLoginError), FriendlyError(e));
        }
    }

    // JoinDefaultWorld removed — all worlds are joined remotely via the world browser.
    // The Home portal now opens the world list directly.

    // ── Login persistence ───────────────────────────────────────────────────────────

    private const string SessionPath = "user://session.json";
    private const string LockDir = "user://locks";

    /// Check whether another live Godot instance is already running (sharing the same
    /// user:// directory). Each instance writes a per-PID lock file; stale locks from
    /// crashed processes are cleaned up here. Used to force a second client to log in
    /// with a different account instead of silently sharing the first one's session.
    private static bool IsAnotherInstanceRunning()
    {
        try
        {
            var absDir = ProjectSettings.GlobalizePath(LockDir);
            if (!System.IO.Directory.Exists(absDir)) return false;

            int ourPid = System.Environment.ProcessId;
            bool found = false;
            foreach (var file in System.IO.Directory.GetFiles(absDir, "*.lock"))
            {
                try
                {
                    int pid = int.Parse(System.IO.Path.GetFileNameWithoutExtension(file));
                    if (pid == ourPid) continue;
                    try { System.Diagnostics.Process.GetProcessById(pid); found = true; }
                    catch { try { System.IO.File.Delete(file); } catch { } }
                }
                catch { }
            }
            return found;
        }
        catch { return false; }
    }

    private static void WriteInstanceLock()
    {
        try
        {
            var absDir = ProjectSettings.GlobalizePath(LockDir);
            System.IO.Directory.CreateDirectory(absDir);
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(absDir, $"{System.Environment.ProcessId}.lock"),
                System.Environment.ProcessId.ToString());
        }
        catch { }
    }

    private static void ClearInstanceLock()
    {
        try
        {
            var absPath = System.IO.Path.Combine(
                ProjectSettings.GlobalizePath(LockDir),
                $"{System.Environment.ProcessId}.lock");
            if (System.IO.File.Exists(absPath)) System.IO.File.Delete(absPath);
        }
        catch { }
    }

    private async Task TryRestoreSession()
    {
        WriteInstanceLock();
        ShowLoading("Starting up…");

        if (IsAnotherInstanceRunning())
        {
            GD.Print("another instance is already running — requiring separate login");
            SetLoadingStatus("Another instance is running — please log in");
            CallDeferred(nameof(ShowLoginScreen));
            return;
        }

        string token = LoadSession();
        if (string.IsNullOrEmpty(token))
        {
            CallDeferred(nameof(ShowLoginScreen));
            return;
        }

        SetLoadingStatus("Restoring your session…");
        try
        {
            _api = new ApiClient(ApiBaseUrl);
            var user = await _api.VerifySessionAsync(token);
            if (user.ValueKind == JsonValueKind.Object && user.TryGetProperty("id", out _))
            {
                _username = user.GetProperty("username").GetString();
                _currentAvatarId = ReadCurrentAvatarId(user);
                _localPfpUrl = ReadAvatarUrl(user);
                _localTrust = ReadTrust(user);
                GD.Print($"session restored as {_username}");

                SetLoadingStatus("Loading your avatar…");
                await FetchCurrentAvatar();
                await FetchDefaultOutfit();
                _ = FetchBlockList();

                await RouteAfterLogin();
                return;
            }
        }
        catch (Exception e) { GD.PrintErr($"session restore failed: {e.Message}"); }

        // Token expired or invalid — clear it and show login.
        ClearSession();
        CallDeferred(nameof(ShowLoginScreen));
    }

    private void ShowLoginScreen()
    {
        HideLoading();
        _hud?.ShowLogin();
    }

    private void SaveSession(string token)
    {
        if (string.IsNullOrEmpty(token)) return;
        try
        {
            var abs = ProjectSettings.GlobalizePath(SessionPath);
            System.IO.File.WriteAllText(abs, token);
            GD.Print("session token saved");
        }
        catch (Exception e) { GD.PrintErr($"failed to save session: {e.Message}"); }
    }

    private string LoadSession()
    {
        try
        {
            var abs = ProjectSettings.GlobalizePath(SessionPath);
            if (System.IO.File.Exists(abs))
                return System.IO.File.ReadAllText(abs).Trim();
        }
        catch { }
        return null;
    }

    private void ClearSession()
    {
        try
        {
            var abs = ProjectSettings.GlobalizePath(SessionPath);
            if (System.IO.File.Exists(abs)) System.IO.File.Delete(abs);
        }
        catch { }
    }

    /// Route after a fresh login or session restore: deep-linked world, or Home.
    private async Task RouteAfterLogin()
    {
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
            // Resolve this world's downloadUrl and make sure the local copy matches it.
            // The list is only populated once the world browser has been opened, so joining
            // through a portal or a deep link used to skip the download entirely and leave
            // WorldLoader to serve whatever stale bundle was already cached — which is how a
            // republished world could stay invisible indefinitely. Fall back to the per-world
            // endpoint so a join always knows the current version.
            string downloadUrl = null;
            if (_fetchedWorlds != null)
                downloadUrl = _fetchedWorlds.Find(w => w.id == worldId).downloadUrl;

            if (downloadUrl == null)
            {
                try
                {
                    var detail = await _api.GetWorldDetailAsync(worldId);
                    if (detail.TryGetProperty("downloadUrl", out var du) &&
                        du.ValueKind == JsonValueKind.String)
                        downloadUrl = du.GetString();
                }
                catch (Exception e) { GD.PrintErr($"world detail lookup failed: {e.Message}"); }
            }

            if (downloadUrl != null)
            {
                ShowLoading("Downloading world…");
                string localPath = await _api.DownloadWorldAsync(downloadUrl, worldId);
                if (localPath != null)
                    GD.Print($"world ready at {localPath}");
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
            CallDeferred(nameof(OnJoinFailed), FriendlyError(e));
        }
    }

    private void OnJoinFailed(string message)
    {
        HideLoading();
        if (_inWorld || _inHome || _api?.SessionToken != null)
        {
            EnterHome();
            _inWorldHud?.Toast($"Couldn't join: {message}", 5);
        }
        else
        {
            ShowLoginError(message);
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
        UI.InputMode.ReleaseAll();
        UI.InputMode.SetPlayable(true);
        _inWorldHud?.Toast("Welcome home · walk into the portal to travel · T to chat · Esc for menu", 6);

        _ = PopulateWorldList();
        MaybeStartTutorial();
    }

    // ── Home world list (opened from the pause menu, not forced) ──────────────────────

    private void OpenWorldList(List<string> filterWorldIds = null)
    {
        _hud?.ShowWorldList(_username);
        UI.InputMode.Hold(UI.InputMode.WorldList);
        _ = PopulateWorldList(filterWorldIds);
    }

    private void CloseWorldList()
    {
        _hud?.HideAll();
        UI.InputMode.Release(UI.InputMode.WorldList);
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
            if (_localVr != null) _localVr.ResetMotion();
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
        _avatarSelector.Configure(_api, _currentAvatarId);
        _avatarSelector.Open();
        SyncMenuHold();
    }

    private void OnAvatarSelectorClosed() => SyncMenuHold();

    /// Download the chosen avatar, swap the live rig, and persist the choice server-side.
    /// Each avatar is cached under its own `user://avatars/<id>.ska` so re-equipping a
    /// previously worn one is instant and never collides with AvatarLibrary's path cache.
    private async Task EquipAvatar(string id, string downloadUrl, string name)
    {
        ShowAvatarLoading($"Loading {name}…");
        try
        {
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
            AvatarLibrary.CurrentDefaultPath = rel;
            _localDesktop?.SetAvatar(avatar);
            _localVr?.SetAvatar(avatar);
            _actionMenu?.SetCustomEmotes(avatar?.CustomEmotes);

            // Persist so it's worn on the next join and by remotes after they resync.
            bool persisted = true;
            try { await _api.SelectAvatarAsync(id); }
            catch (Exception e)
            {
                GD.PrintErr($"avatar select persist failed: {e.Message}");
                persisted = false;
            }

            _inWorldHud?.Toast(
                persisted ? $"Now wearing {name}" : $"Now wearing {name} (save failed — won't persist)",
                persisted ? 3 : 5);
        }
        catch (Exception e)
        {
            GD.PrintErr($"equip failed: {e.Message}");
            _inWorldHud?.Toast("Couldn't equip that avatar", 3);
        }
        finally
        {
            HideAvatarLoading();
        }
    }

    // ── Avatar loading indicator ────────────────────────────────────────────────────
    private AvatarLoadingIndicator _avatarLoading;

    /// Spinner at the player's feet while an avatar downloads/imports. `.ska` files run to tens
    /// of megabytes, so without it a swap just looks like the client hanging.
    private void ShowAvatarLoading(string text)
    {
        HideAvatarLoading();
        if (_localNode == null) return;
        _avatarLoading = AvatarLoadingIndicator.Create(1.7f, text);
        _localNode.AddChild(_avatarLoading);
    }

    private void HideAvatarLoading()
    {
        if (_avatarLoading == null) return;
        _avatarLoading.QueueFree();
        _avatarLoading = null;
    }

    /// Pull the user's chosen avatar id out of the login response, or null if none set.
    /// The logged-in user's profile-picture URL, shown on the local name card.
    private string _localPfpUrl;

    /// The logged-in user's trust level (0..4), mirrored from the account. Gates creator actions
    /// server-side; shown as a chip in the hub so the player knows their standing.
    private int _localTrust;

    // Trust ladder 0..8 — mirrors TrustRank in server/api/src/trust.ts.
    private static readonly string[] TrustLabels =
        { "Visitor", "Newcomer", "Member", "Regular", "Known", "Creator", "Trusted", "Partner", "Verified Creator" };
    public static string TrustLabel(int level) => TrustLabels[System.Math.Clamp(level, 0, TrustLabels.Length - 1)];

    private static int ReadTrust(System.Text.Json.JsonElement user) =>
        user.TryGetProperty("trustLevel", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number
            ? v.GetInt32() : 0;

    /// Pull the profile-picture URL out of a login/session user object, or null if unset.
    private static string ReadAvatarUrl(System.Text.Json.JsonElement user) =>
        user.TryGetProperty("avatarUrl", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? v.GetString()
            : null;

    /// Download and apply the local player's profile picture to their name card, and push the
    /// current tag/pfp visibility preferences. Best-effort — a missing picture just leaves the
    /// chip hidden.
    private async Task ApplyLocalIdentity()
    {
        bool tags = UI.DeviceProfile.Settings.NameTags;
        bool pfp = UI.DeviceProfile.Settings.ProfilePictures;
        _localDesktop?.SetTagPrefs(tags, pfp);
        _localVr?.SetTagPrefs(tags, pfp);
        if (_api == null || string.IsNullOrEmpty(_localPfpUrl)) return;
        if (_localDesktop == null && _localVr == null) return;
        var bytes = await _api.GetImageBytesAsync(_localPfpUrl);
        if (bytes == null) return;
        _localDesktop?.SetProfilePicture(bytes);
        _localVr?.SetProfilePicture(bytes);
    }

    /// Resolve and apply a peer's profile picture from their account id.
    private async Task FetchRemotePfp(uint peerId, string userId)
    {
        if (_api == null || string.IsNullOrEmpty(userId)) return;
        try
        {
            string url = await _api.GetUserAvatarUrlAsync(userId);
            if (string.IsNullOrEmpty(url)) return;
            var bytes = await _api.GetImageBytesAsync(url);
            if (bytes != null && _remotes.TryGetValue(peerId, out var a))
                a.SetProfilePicture(bytes);
        }
        catch (Exception e) { GD.PrintErr($"remote pfp fetch failed: {e.Message}"); }
    }

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

        UI.InputMode.Hold(UI.InputMode.Tutorial);

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
            UI.InputMode.Release(UI.InputMode.Tutorial);
            return;
        }

        try
        {
            DialogueManagerRuntime.DialogueManager.DialogueEnded += OnTutorialEnded;
            DialogueManagerRuntime.DialogueManager.ShowExampleDialogueBalloon(dialogueRes, "tutorial_start");
        }
        catch (Exception e)
        {
            GD.PrintErr($"tutorial balloon failed: {e.Message}");
            DialogueManagerRuntime.DialogueManager.DialogueEnded -= OnTutorialEnded;
            UI.InputMode.Release(UI.InputMode.Tutorial);
        }
    }

    private void OnTutorialEnded(Resource _)
    {
        DialogueManagerRuntime.DialogueManager.DialogueEnded -= OnTutorialEnded;
        Tutorial.MarkSeen();
        UI.InputMode.Release(UI.InputMode.Tutorial);
    }

    private List<(string id, string name, string description, int capacity, string author, string downloadUrl)> _fetchedWorlds;

    private async Task PopulateWorldList(List<string> filterWorldIds = null)
    {
        if (_api == null) return;
        try
        {
            var worlds = await _api.GetWorldsAsync();
            var list = new List<(string id, string name, string description, int capacity, string author, string downloadUrl)>();
            foreach (var w in worlds.EnumerateArray())
            {
                string id = w.GetProperty("id").GetString();
                // Apply curated filter: if a filter list is provided, skip worlds not in it.
                if (filterWorldIds != null && !filterWorldIds.Contains(id))
                    continue;
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
        _mainMenu?.SetWorlds(_fetchedWorlds);
        _ = PopulateMainMenuAvatars();
    }

    private List<(string id, string name, string author, string thumbUrl, string dlUrl)> _fetchedAvatars;

    // Feed the VRChat-style Big Menu's Avatars tab from the same catalogue the in-world
    // AvatarSelector uses. Without this the Avatars tab renders "No avatars available".
    private async Task PopulateMainMenuAvatars()
    {
        if (_api == null || _mainMenu == null) return;
        try
        {
            var avatars = await _api.GetAvatarsAsync(false);
            var list = new List<(string id, string name, string author, string thumbUrl, string dlUrl)>();
            foreach (var a in avatars.EnumerateArray())
            {
                string id = a.GetProperty("id").GetString() ?? "";
                string name = a.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : "Untitled";
                string author = a.TryGetProperty("author", out var au) && au.ValueKind == JsonValueKind.String ? au.GetString() : null;
                string thumb = a.TryGetProperty("thumbnailUrl", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                string dl = a.TryGetProperty("downloadUrl", out var dl2) && dl2.ValueKind == JsonValueKind.String ? dl2.GetString() : null;
                list.Add((id, name, author, thumb, dl));
            }
            _fetchedAvatars = list;
            CallDeferred(nameof(OnAvatarsFetched));
        }
        catch (Exception e)
        {
            GD.PrintErr($"main-menu avatar fetch failed: {e.Message}");
        }
    }

    private void OnAvatarsFetched()
    {
        if (_fetchedAvatars != null) _mainMenu?.SetAvatars(_fetchedAvatars);
    }

    // ── World detail (in-game) ──────────────────────────────────────────────────────
    private JsonElement _pendingWorldDetail;

    private async Task ShowWorldDetailFor(string worldId)
    {
        if (_api == null) return;
        try
        {
            var detail = await _api.GetWorldDetailAsync(worldId);
            _pendingWorldDetail = detail;
            CallDeferred(nameof(OnWorldDetailFetched));
        }
        catch (Exception e)
        {
            GD.PrintErr($"world detail fetch failed: {e.Message}");
            // Fall back to direct join if detail fetch fails
            _ = JoinWorldById(worldId);
        }
    }

    private void OnWorldDetailFetched()
    {
        _hud?.ShowWorldDetail(_pendingWorldDetail);
    }

    private void TeardownRemotes()
    {
        foreach (var a in _remotes.Values) a.QueueFree();
        _remotes.Clear();
    }

    private void ShowLoginError(string message)
    {
        HideLoading();
        // There's no rig to control behind an error, so hand the cursor to the dialog rather
        // than leaving it captured under a message the player has to click.
        UI.InputMode.SetPlayable(false);
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

    private Vector3 _worldSpawn = new(0, 1, 8);

    /// Spawn a few marker pens on the benches in The Commons so players can pick them up and
    /// draw on the floor/walls. Each pen gets a deterministic NetId in the prop range.
    private void SpawnMarkerPens()
    {
        if (_worldRoot == null) return;

        // Only spawn pens in worlds that have surfaces to draw on.
        if (_currentWorldId != Worlds.IdCommons && _currentWorldId != Worlds.IdTestItems) return;

        ushort penNetId = 100; // deterministic start in the prop range
        var hues = new[] { 0.78f, 0.05f, 0.33f, 0.55f, 0.12f };
        for (int i = 0; i < hues.Length; i++)
        {
            float a = i * Mathf.Tau / hues.Length;
            var pos = new Vector3(Mathf.Cos(a) * 5, 0.5f, Mathf.Sin(a) * 5);
            var pen = new MarkerPen
            {
                Name = $"MarkerPen_{i}",
                NetId = (ushort)(penNetId + i),
                DrawHue = hues[i],
                Position = pos,
            };
            _worldRoot.AddChild(pen);
            RegisterPhysicsProp(pen);
            if (_strokeCanvas != null) pen.SetCanvas(_strokeCanvas);
        }
    }

    private void OnJoinReady(string endpoint, string ticket, string username, string worldName)
    {
        GD.Print($"OnJoinReady worldId={_currentWorldId} name={worldName}");
        _inHome = false;
        _worldName = worldName;
        ShowLoading($"Connecting to {worldName}…");
        SwapWorld(root => _worldSpawn = Worlds.BuildWorldForId(_currentWorldId, root));
        SpawnLocalPlayer();
        MoveLocalTo(_worldSpawn);
        SpawnMarkerPens();
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
        udp.ObjectSyncReceived += OnObjectSyncReceived;
        udp.PhysGrabReceived += OnPhysGrabReceived;
        udp.Rejected += OnTransportRejected;
        _transport = udp;
        udp.Connect(endpoint, ticket);

        // Wire the static bridge so PhysicsProp can send sync updates
        WorldNetwork.Send = (objId, x, y, z, qx, qy, qz, qw, lvx, lvy, lvz) =>
            _transport?.SendObjectSync(objId, x, y, z, qx, qy, qz, qw, lvx, lvy, lvz);

        // Wire the stroke canvas to the same channel — stroke points are aliased ObjectSync packets.
        if (_strokeCanvas != null)
            _strokeCanvas.SendStrokePoint = (objId, x, y, z, qx, qy, qz, qw, lvx, lvy, lvz) =>
                _transport?.SendObjectSync(objId, x, y, z, qx, qy, qz, qw, lvx, lvy, lvz);
    }

    /// The relay rejected us, or a live session went silent. Clean up remotes/transport,
    /// drop back to Home instantly and show a toast. Fires on the game thread (from Poll).
    private void OnTransportRejected(string reason)
    {
        GD.PrintErr($"transport rejected/lost: {reason}");
        _inWorld = false;
        TeardownRemotes();
        _transport?.Disconnect();
        _transport = null;

        EnterHome();
        _inWorldHud?.Toast($"Disconnected: {reason}", 5);
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
        UI.InputMode.ReleaseAll();
        UI.InputMode.SetPlayable(true);
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
        a.SetTagPrefs(UI.DeviceProfile.Settings.NameTags, UI.DeviceProfile.Settings.ProfilePictures);
        if (!string.IsNullOrEmpty(p.UserId) && _blockedUserIds.Contains(p.UserId))
        {
            a.ShowBean();   // blocked: never load their real model — and no pfp
            return;
        }
        _ = EquipRemoteAvatar(p.PeerId, p.UserId);
        _ = FetchRemotePfp(p.PeerId, p.UserId);
    }

    /// Resolve and equip a peer's own avatar. Until this lands they wear the default outfit;
    /// previously every remote wore the *local* player's model because the client had no way
    /// to look up who was wearing what.
    private async Task EquipRemoteAvatar(uint peerId, string userId)
    {
        if (_api == null || string.IsNullOrEmpty(userId)) return;
        if (_remotes.TryGetValue(peerId, out var pending)) pending.SetLoading(true);
        try
        {
            string url = await _api.GetAvatarUrlForUserAsync(userId);
            if (string.IsNullOrEmpty(url)) return;

            // Cached per user under user://avatars/, same convention as EquipAvatar.
            DirAccess.MakeDirRecursiveAbsolute("user://avatars");
            string rel = $"user://avatars/peer-{SanitizeId(userId)}.ska";
            string abs = ProjectSettings.GlobalizePath(rel);
            if (!System.IO.File.Exists(abs) && !await _api.DownloadToAsync(url, abs)) return;

            CallDeferred(nameof(ApplyRemoteAvatar), peerId, rel);
            return;
        }
        catch (Exception e) { GD.PrintErr($"remote avatar equip failed: {e.Message}"); }
        finally
        {
            // Clear the spinner on the paths that don't reach ApplyRemoteAvatar.
            CallDeferred(nameof(ClearRemoteLoading), peerId);
        }
    }

    private void ApplyRemoteAvatar(uint peerId, string path)
    {
        if (!_remotes.TryGetValue(peerId, out var a)) return;
        a.EquipAvatar(path, isReal: true);
        a.SetLoading(false);
    }

    private void ClearRemoteLoading(uint peerId)
    {
        if (_remotes.TryGetValue(peerId, out var a)) a.SetLoading(false);
    }

    // ── Per-frame ────────────────────────────────────────────────────────────────────

    // ── Pause menu ──────────────────────────────────────────────────────────────────

    /// Esc opens the pause menu whenever we're somewhere playable — Home included. The menu
    /// itself handles Esc-closes; while it's up the player's movement/look are suspended.
    private void ToggleCameraView()
    {
        if (_localDesktop == null) return;
        var mode = _localDesktop.CycleCameraMode();
        string toastMsg = mode switch
        {
            LocalPlayer.CameraModeEnum.ThirdPersonBack => "Third-person view (Back)",
            LocalPlayer.CameraModeEnum.ThirdPersonFront => "Third-person view (Front / Selfie)",
            _ => "First-person view",
        };
        _inWorldHud?.Toast(toastMsg);
    }

    private void OpenPauseMenu()
    {
        if (_quickMenu == null) return;
        // Feed the hub live state: the instance roster, where we are, and the real mic status.
        var others = new List<string>();
        foreach (var kv in _peerNames) others.Add(kv.Value);
        _quickMenu.SetLocation(_inHome ? "Home" : _worldName, invitable: _inWorld && !string.IsNullOrEmpty(_currentWorldId));
        _quickMenu.SetTrust(TrustLabel(_localTrust));
        _quickMenu.SetPlayers(_username, others);
        _quickMenu.SetMic(_micActive);
        _quickMenu.Open(_username);
        SyncMenuHold();
    }

    /// Open the photo viewfinder, dropping the phantom camera at the player's eye so the first
    /// frame shows what they were already looking at.
    private void OpenCameraMenu()
    {
        _cameraMenu?.Open(_local?.PoseTransform());
        SyncMenuHold();
    }

    private void OnPauseClosed() => SyncMenuHold();

    /// Reconcile the menu hold with what's actually on screen. Called after every open and
    /// close: the six overlay screens can open each other, so "did I open or close" isn't
    /// enough to know whether the cursor should still be free — only the aggregate is.
    private void SyncMenuHold()
    {
        if (!AnyMenuOpen) { UI.InputMode.Release(UI.InputMode.Menu); return; }

        if (_vrMode && _vrUi != null && !UI.InputMode.HasHold(UI.InputMode.Menu))
        {
            _vrUi.FaceCamera(_localVr?.HeadCamera ?? _bootXrOrigin?.GetNodeOrNull<XRCamera3D>("BootXrCamera"));
        }

        // The photo viewfinder is the one screen that wants movement suspended but the mouse
        // still captured — there the mouse aims the phantom camera, and freeing it would leave
        // the viewfinder unable to look anywhere.
        UI.InputMode.Hold(UI.InputMode.Menu, freeCursor: !(_cameraMenu?.IsOpen ?? false));
    }

    private bool AnyMenuOpen =>
        (_quickMenu?.IsOpen ?? false) || (_mainMenu?.IsOpen ?? false) ||
        (_actionMenu?.IsOpen ?? false) || (_cameraMenu?.IsOpen ?? false) ||
        (_avatarSelector?.IsOpen ?? false) ||
        (_settingsMenu?.IsOpen ?? false) ||
        (_videoQueuePanel?.IsOpen ?? false);

    /// Push a live setting change onto whatever it affects.
    private void OnSettingChanged(string what)
    {
        switch (what)
        {
            case "sensitivity":
                if (_local != null) _local.MouseSensitivity = UI.DeviceProfile.Settings.MouseSensitivity;
                break;
            case "name_tags":
            case "pfp":
                ApplyTagPrefsToAvatars();
                break;
        }
    }

    /// Re-apply name-tag / profile-picture visibility to the local rig and every remote.
    private void ApplyTagPrefsToAvatars()
    {
        bool tags = UI.DeviceProfile.Settings.NameTags;
        bool pfp = UI.DeviceProfile.Settings.ProfilePictures;
        _localDesktop?.SetTagPrefs(tags, pfp);
        _localVr?.SetTagPrefs(tags, pfp);
        foreach (var r in _remotes.Values) r.SetTagPrefs(tags, pfp);
    }

    /// Close whichever overlay menu is currently open (top-most wins). Returns true if one closed.
    private bool CloseOpenMenu()
    {
        // The radial menu steps back out of a submenu before closing entirely.
        if (_actionMenu?.IsOpen ?? false)
        {
            if (_actionMenu.BackOut()) return true;
            _actionMenu.Hide();
            return true;
        }
        if (_settingsMenu?.IsOpen ?? false) { _settingsMenu.Hide(); return true; }
        if (_quickMenu?.IsOpen ?? false) { _quickMenu.Hide(); return true; }
        if (_mainMenu?.IsOpen ?? false) { _mainMenu.Hide(); return true; }
        if (_cameraMenu?.IsOpen ?? false) { _cameraMenu.Hide(); return true; }
        if (_avatarSelector?.IsOpen ?? false) { _avatarSelector.Hide(); return true; }
        if (_videoQueuePanel?.IsOpen ?? false) { _videoQueuePanel.Hide(); return true; }
        return false;
    }

    // Menu-toggle keys are handled in _Input (not _UnhandledInput): Escape is the engine's
    // `ui_cancel` action, which any focused Control (a menu button, a LineEdit) swallows before
    // it ever reaches unhandled input — that's why Esc "did nothing, not even reveal the mouse".
    // _Input runs ahead of GUI focus, so the menu keys fire reliably.
    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } k) return;
        if (!(_inWorld || _inHome)) return;
        if (_chat is { IsTyping: true }) return; // let the chat box keep its own keys

        // Escape always resolves (close top-most, else open the quick menu). Every other
        // shortcut is ignored while a menu is up, so e.g. R can't close the radial ring in the
        // menu's own handler and then be re-opened by the toggle below in the same event.
        if (k.Keycode == Key.Escape)
        {
            if (!CloseOpenMenu()) OpenPauseMenu();
            SyncMenuHold();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (AnyMenuOpen && k.Keycode is not (Key.M or Key.R or Key.C)) return;

        switch (k.Keycode)
        {
            case Key.M:
                if (_mainMenu?.IsOpen ?? false) _mainMenu.Hide(); else _mainMenu?.Open(_username, 1);
                SyncMenuHold();
                GetViewport().SetInputAsHandled();
                return;

            case Key.R:
                if (_actionMenu?.IsOpen ?? false) _actionMenu.Hide(); else _actionMenu?.Open();
                SyncMenuHold();
                GetViewport().SetInputAsHandled();
                return;

            case Key.C:
                if (_cameraMenu?.IsOpen ?? false) { _cameraMenu.Hide(); SyncMenuHold(); }
                else OpenCameraMenu();
                GetViewport().SetInputAsHandled();
                return;

            // Tab frees the cursor without giving up movement, so you can click on world UI
            // (or just alt-tab-lite) mid-walk. Ignored while a menu is up — the cursor is
            // already free there and toggling would only desync the flag.
            case Key.Tab:
                if (UI.InputMode.ToggleManualCursor())
                {
                    _inWorldHud?.Toast(
                        UI.InputMode.CursorFree ? "Mouse free · Tab to look again" : "Mouse captured", 2);
                    GetViewport().SetInputAsHandled();
                }
                return;

            case Key.E:
                if (_interactor?.TryInteract() ?? false) GetViewport().SetInputAsHandled();
                return;

            // P toggles the video queue panel, but only in a world that actually has a screen.
            case Key.P:
                if (_videoQueuePanel?.HasVideo ?? false)
                {
                    if (_videoQueuePanel.IsOpen) _videoQueuePanel.Hide();
                    else _videoQueuePanel.Open();
                    GetViewport().SetInputAsHandled();
                }
                return;

            case Key.V:
                ToggleMic();
                GetViewport().SetInputAsHandled();
                return;

            case Key.F5:
                if (_localDesktop != null) { ToggleCameraView(); GetViewport().SetInputAsHandled(); }
                return;
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        // T opens the text chat (in Home or a world), when not already typing.
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.T }
            && (_inWorld || _inHome) && _chat is { IsTyping: false })
        {
            OpenChat();
            GetViewport().SetInputAsHandled();
        }



        // Emotes have no key binds — they're all chosen from the Action menu (R).
    }

    public override void _Process(double delta)
    {
        // Keep the VR UI panel smoothly positioned in front of whichever camera is currently live — the boot
        // rig before login, the player rig after.
        if (_vrUi != null)
            _vrUi.LazyFollow(_localVr?.HeadCamera
                ?? _bootXrOrigin?.GetNodeOrNull<XRCamera3D>("BootXrCamera"), (float)delta);
    }

    public override void _PhysicsProcess(double delta)
    {
        _transport?.Poll(delta);

        if (_local != null)
            _local.MouseSensitivity = UI.DeviceProfile.Settings.MouseSensitivity;

        if (_transport is { Connected_: true } && _local != null)
        {
            // Send pose at 20Hz.
            _poseTimer += delta;
            if (_poseTimer >= 0.05)
            {
                _poseTimer = 0;
                _transport.SendPose(AvatarPose.FromTransform(
                    _local.PoseTransform(), _poseSeq++,
                    _localDesktop?.Avatar ?? _localVr?.Avatar));
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
