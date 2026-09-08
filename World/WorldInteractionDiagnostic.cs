using Godot;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Collections.Generic;
using SerikaSocial.Player;
using SerikaSocial.Avatar;

namespace SerikaSocial.World;

/// Real GLB → bundle → loader → physics/animation/player regression, with injected XR poses.
/// Godot --headless --path game res://World/WorldInteractionDiagnostic.tscn
/// Optional SERIKA_HUB_BUNDLE checks the final authored hub as well as this small fixture.
public partial class WorldInteractionDiagnostic : Node3D
{
    private int _checks, _failures;
    private VrPlayer _vr;
    private CharacterBody3D _portalProbe;
    private Vector3 _portalProbeMotion;
    private Vector3 _head = new(0, 1.62f, 0);
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "serika-interaction-" + Guid.NewGuid().ToString("N"));
    private const string Target = "00000000-0000-0000-0000-0000000000e0";

    public override void _PhysicsProcess(double delta)
    {
        if (_portalProbe != null) _portalProbe.MoveAndCollide(_portalProbeMotion);
        if (_vr == null || !GodotObject.IsInstanceValid(_vr)) return;
        _vr.HeadCamera.Position = _head;
        _vr.HeadCamera.Rotation = new Vector3(0, .23f, 0);
        _vr.LeftHand.Position = new Vector3(-.23f, 1.15f, -.32f);
        _vr.RightHand.Position = new Vector3(.23f, 1.15f, -.32f);
    }

    public override async void _Ready()
    {
        ProcessPhysicsPriority = -100;
        try
        {
            Directory.CreateDirectory(_temporary);
            var source = BuildFixture();
            AddChild(source);
            var glb = Path.Combine(_temporary, "fixture.glb");
            var document = new GltfDocument();
            var state = new GltfState();
            Check(document.AppendFromScene(source, state) == Error.Ok && document.WriteToFilesystem(state, glb) == Error.Ok,
                "fixture exports actual marker and animated GLB");
            RemoveChild(source); source.Free();
            string bundle = Package(glb);
            var root = new Node3D { Name = "LoadedFixture" }; AddChild(root);
            var spawn = WorldLoader.LoadFromPath(bundle, "interaction-test", root);
            Check(spawn.HasValue && spawn.Value.DistanceTo(new Vector3(0, .05f, 3)) < .01f, "SPAWN survives unknown SERIKA node");
            await Frames(3);
            var environment = Descendants(root).OfType<WorldEnvironment>().Single().Environment;
            Check(environment.AmbientLightColor.IsEqualApprox(new Color(.75f, .82f, .9f)) &&
                  Mathf.Abs(environment.AmbientLightEnergy - .45f) < .001f,
                  "daylit manifest uses neutral daylight ambient");
            var nodes = Descendants(root).ToList();
            var portals = nodes.OfType<Portal>().ToList();
            Check(portals.Count == 1, "only valid UUID marker becomes portal");
            var portal = portals.Single();
            Check(portal.Mode == PortalMode.DirectWorld && portal.TargetWorldId == Target && portal.Label == "The Commons",
                "portal carries exact direct travel destination and label");
            Check(Mathf.Abs(portal.PortalWidth - 2.65f) < .001f && Mathf.Abs(portal.PortalHeight - 2.55f) < .001f &&
                  Mathf.Abs(portal.TriggerDepth - .8f) < .001f, "authored width/height/depth survive GLB import");
            Check(portal.Scale.DistanceTo(Vector3.One) < .001f, "portal body has unit scale");
            Check(nodes.Any(n => n.Name == "SERIKA_FISH_Example" && !n.IsQueuedForDeletion()), "unknown SERIKA nodes are preserved");
            Check(nodes.OfType<StaticBody3D>().Count() == 1, "portal and seats add no solid collision");
            var player = nodes.OfType<AnimationPlayer>().Single();
            Check(player.IsPlaying() && player.CurrentAnimation == "AquariumSwim", "AquariumSwim automatically starts");
            var clip = player.GetAnimation("AquariumSwim");
            Check(clip.LoopMode == Animation.LoopModeEnum.Linear, "aquarium animation loops");
            var fish = nodes.OfType<Node3D>().Single(n => n.Name == "FISH_ROOT_01");
            player.Seek(.5, true); float p1 = fish.Position.X;
            player.Advance(.5); float p2 = fish.Position.X;
            Check(Mathf.Abs(p2 - p1) > .2f, "imported animation actually moves fish geometry");
            player.Seek(clip.Length - .05, true); player.Advance(.10);
            Check(player.CurrentAnimationPosition < .15, "fish animation wraps rather than stopping");

            int entered = 0; Action<Portal> handler = p => { if (p.TargetWorldId == Target) entered++; };
            Portal.BindWorld(root, handler); Portal.BindWorld(root, handler);
            var remote = Body(PhysicsLayers.RemotePlayer); AddChild(remote);
            remote.Position = portal.GlobalPosition + Vector3.Up;
            await Frames(4);
            Check(entered == 0, "remote body cannot trigger travel");
            remote.QueueFree();
            var local = Body(PhysicsLayers.LocalPlayer); AddChild(local);
            local.Position = portal.GlobalPosition + Vector3.Up;
            await Frames(4);
            Check(entered == 1, "local body entering authored portal triggers exactly once after idempotent binding");
            local.QueueFree();

            var seat = nodes.OfType<SeatNode>().Single();
            var desktop = new LocalPlayer { Name = "DesktopTest", Position = new Vector3(3, .03f, 1), ControlsEnabled = false };
            AddChild(desktop); desktop.SetAvatar(NewAvatar());
            await Frames(3);
            var approach = desktop.GlobalPosition;
            var ctx = new InteractionContext(InteractSource.Desktop, desktop, desktop, approach, Vector3.Forward, approach, false);
            seat.Interact(ctx); await Frames(40);
            Check(ReferenceEquals(desktop.Occupying, seat) && !seat.CanInteract, "desktop interacts with authored seat");
            Check(SeatPose.PinHips(desktop.Avatar, seat.AnchorPosition).Length() < .005f, "desktop animated hips contact seat anchor");
            Check((-desktop.PoseTransform().Basis.Z).Dot(Vector3.Forward) > .99f, "zero-yaw seat faces -Z / Blender +Y");
            CheckRemoteSeatedPose(desktop.Avatar, desktop.PoseTransform(), seat.AnchorPosition, "desktop");
            desktop.StandUp();
            Check(desktop.GlobalPosition.DistanceTo(approach) < .01f && seat.CanInteract, "desktop stands at clear approach point");
            desktop.QueueFree(); await Frames(3);

            _vr = new VrPlayer { Name = "VrTest", Position = new Vector3(3, .04f, 1), ControlsEnabled = false };
            AddChild(_vr); _vr.SetAvatar(NewAvatar());
            VrTestInput.Active = true;
            await Frames(40);
            var vrApproach = _vr.GlobalPosition;
            var vrCtx = new InteractionContext(InteractSource.VrLeft, _vr, null, _vr.LeftHand.GlobalPosition,
                Vector3.Forward, _vr.LeftHand.GlobalPosition, true);
            var trackedBefore = _vr.HeadCamera.Transform;
            seat.Interact(vrCtx); await Frames(40);
            Check(ReferenceEquals(_vr.Occupying, seat) && !seat.CanInteract, "VR hand interaction occupies the authored seat");
            Check(_vr.HeadCamera.Transform.IsEqualApprox(trackedBefore), "seating leaves tracked camera-local pose intact");
            Check(SeatPose.PinHips(_vr.Avatar, seat.AnchorPosition).Length() < .01f, "VR seated IK pins hips to cushion");
            CheckSeatedHead("rest");
            CheckRemoteSeatedPose(_vr.Avatar, _vr.PoseTransform(), seat.AnchorPosition, "VR");
            var handRig = new VrHandInteractRig(_vr, _vr.LeftHand, true);
            Check(ReferenceEquals(handRig.Occupying, seat), "VR hand rig exposes Stand up interaction");
            var eyeBefore = _vr.HeadCamera.GlobalPosition;
            _head += new Vector3(.12f, -.03f, 0); await Frames(6);
            Check(_vr.HeadCamera.GlobalPosition.DistanceTo(eyeBefore) > .1f && ReferenceEquals(_vr.Occupying, seat),
                "seated head lean remains tracked without dragging hips");
            Check(SeatPose.PinHips(_vr.Avatar, seat.AnchorPosition).Length() < .01f, "head lean retains chair contact");
            CheckSeatedHead("lean");
            handRig.StandUp();
            Check(_vr.GlobalPosition.DistanceTo(vrApproach) < .01f && seat.CanInteract, "VR interact returns to clear approach point");
            await Frames(3); seat.Interact(vrCtx); await Frames(3);
            _vr.ControlsEnabled = true;
            if (UI.DeviceProfile.Settings.VrMoveOnRightStick) VrTestInput.RightStick = new Vector2(.6f, 0);
            else VrTestInput.LeftStick = new Vector2(.6f, 0);
            await Frames(4);
            Check(_vr.Occupying == null && seat.CanInteract, "VR move stick exits chair and resumes locomotion");
            VrTestInput.RightStick = VrTestInput.LeftStick = Vector2.Zero;
            _vr.ControlsEnabled = false; await Frames(2); seat.Interact(vrCtx); await Frames(2);
            var respawn = new Vector3(-3, .2f, 3); _vr.GlobalPosition = respawn; _vr.ResetMotion();
            Check(_vr.Occupying == null && _vr.GlobalPosition.DistanceTo(respawn) < .01f, "VR respawn releases chair without undoing teleport");
            _vr.QueueFree(); _vr = null; await Frames(3);
            root.QueueFree(); await Frames(3);

            var finalBundle = System.Environment.GetEnvironmentVariable("SERIKA_HUB_BUNDLE");
            if (!string.IsNullOrWhiteSpace(finalBundle)) await CheckFinalHub(finalBundle);
            GD.Print($"WORLD_INTERACTION_TEST: {(_failures == 0 ? "PASS" : "FAIL")} ({_checks} checks, {_failures} failures)");
            GetTree().Quit(_failures == 0 ? 0 : 1);
        }
        catch (Exception e) { GD.PrintErr($"WORLD_INTERACTION_TEST: exception {e}"); GetTree().Quit(1); }
        finally { VrTestInput.Active = false; try { Directory.Delete(_temporary, true); } catch { } }
    }

    private void CheckSeatedHead(string pose)
    {
        var avatar = _vr.Avatar;
        var head = avatar.Skeleton.GlobalTransform * avatar.Skeleton.GetBoneGlobalPose(avatar.BoneOf("head")).Origin;
        var want = _vr.HeadCamera.GlobalPosition - _vr.HeadCamera.GlobalBasis * avatar.EyeRestOffset;
        var error = head.DistanceTo(want);
        Check(error < .06f, $"seated VR {pose}: avatar head reaches tracked eye point ({error * 100:0.0}cm error)");
    }

    private static AvatarInstance NewAvatar()
    {
        var path = System.Environment.GetEnvironmentVariable("SERIKA_SEAT_AVATAR");
        return string.IsNullOrWhiteSpace(path) ? AvatarInstance.CreateBean() : AvatarInstance.FromPath(path);
    }

    private async System.Threading.Tasks.Task Frames(int count)
    {
        for (int i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    }

    private void CheckRemoteSeatedPose(AvatarInstance avatar, Transform3D transform, Vector3 anchor, string label)
    {
        var remote = NewAvatar(); AddChild(remote); remote.GlobalTransform = transform;
        var rotations = new Serika.Net.Codec.Quat[55]; avatar.CaptureBonePose(rotations); remote.ApplyBonePose(rotations);
        Check(SeatPose.PinHips(remote, anchor).Length() < .01f, $"{label} seated pelvis height survives existing rotation-only pose format");
        remote.Free();
    }

    private async System.Threading.Tasks.Task CheckFinalHub(string path)
    {
        var hub = new Node3D { Name = "FinalHub" }; AddChild(hub);
        Check(WorldLoader.LoadFromPath(path, "final-hub", hub).HasValue, "final hub loads through shipping loader");
        await Frames(3);
        var nodes = Descendants(hub).ToList();
        Check(nodes.OfType<Portal>().Count() == 5 && nodes.OfType<Portal>().All(p => p.Mode == PortalMode.DirectWorld), "final hub has five functional direct portals");
        Check(nodes.OfType<SeatNode>().Count() >= 12, "final hub seating markers become interactable seats");
        Check(nodes.OfType<Mirror>().Any(), "final hub mirror markers become reflection nodes");
        var swimmers = nodes.OfType<AnimationPlayer>().Where(p => p.IsPlaying() && p.CurrentAnimation.ToString().Contains("AquariumSwim")).ToList();
        Check(swimmers.Count == 1, "final hub fish share one running swim animation");
        var fish = nodes.OfType<Node3D>().Where(n => n.Name.ToString().StartsWith("FISH_ROOT_")).ToList();
        Check(fish.Count >= 10, $"final hub retains {fish.Count} animated fish roots");
        var positions = fish.Select(f => f.GlobalPosition).ToArray();
        swimmers.FirstOrDefault()?.Advance(1);
        Check(fish.Where((f, i) => f.GlobalPosition.DistanceTo(positions[i]) > .01f).Count() == fish.Count, "all final hub fish move on imported tracks");
        foreach (var seat in nodes.OfType<SeatNode>())
        {
            var forward = new Basis(Vector3.Up, seat.SitYaw) * Vector3.Forward;
            var right = new Basis(Vector3.Up, seat.SitYaw) * Vector3.Right;
            bool reachable = false;
            foreach (float distance in new[] { .75f, .9f, 1.05f, 1.2f })
            foreach (float side in new[] { 0f, -.35f, .35f })
            {
                var point = seat.AnchorPosition + forward * distance + right * side;
                var floor = GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(
                    point + Vector3.Up, point - Vector3.Up * 1.2f, PhysicsLayers.World));
                if (floor.Count == 0 || floor["normal"].AsVector3().Y < .7f) continue;
                var ground = floor["position"].AsVector3();
                if (ground.Y > seat.AnchorPosition.Y - .25f ||
                    (ground + Vector3.Up * 1.5f).DistanceTo(seat.FocusPoint) > seat.Range) continue;
                var query = new PhysicsShapeQueryParameters3D
                {
                    Shape = new CapsuleShape3D { Radius = .30f, Height = 1.70f },
                    Transform = new Transform3D(Basis.Identity, ground + Vector3.Up * .90f),
                    CollisionMask = PhysicsLayers.World, Margin = .005f,
                };
                if (GetWorld3D().DirectSpaceState.IntersectShape(query, 1).Count == 0) reachable = true;
            }
            Check(reachable, $"{seat.Name}: clear standing approach within sitting interaction range");
        }
        foreach (var portal in nodes.OfType<Portal>())
        {
            bool entered = false;
            void OnEntry(Portal _) => entered = true;
            portal.Entered += OnEntry;
            _portalProbe = Body(PhysicsLayers.LocalPlayer);
            hub.AddChild(_portalProbe);
            var normal = portal.GlobalBasis.Z;
            _portalProbe.GlobalPosition = portal.GlobalPosition + normal * 1.6f + Vector3.Up * .85f;
            _portalProbeMotion = -normal * .08f;
            for (int i = 0; i < 35 && !entered; i++) await Frames(1);
            Check(entered, $"{portal.Label}: walking capsule triggers travel before backing-wall collision");
            portal.Entered -= OnEntry;
            _portalProbeMotion = Vector3.Zero;
            _portalProbe.QueueFree(); _portalProbe = null;
            await Frames(2);
        }
    }

    private Node3D BuildFixture()
    {
        var root = new Node3D { Name = "HubFixture" };
        root.AddChild(new MeshInstance3D { Name = "COL_Floor", Mesh = new BoxMesh { Size = new Vector3(30, .2f, 30) }, Position = new Vector3(0, -.1f, 0) });
        root.AddChild(new Node3D { Name = "SPAWN", Position = new Vector3(0, .05f, 3) });
        root.AddChild(new Node3D { Name = "SERIKA_PORTAL_" + Target + "__The_Commons", Scale = new Vector3(2.65f, 2.55f, .8f), Position = new Vector3(0, 0, -3) });
        root.AddChild(new Node3D { Name = "SERIKA_PORTAL_bad__Invalid", Position = new Vector3(-5, 0, 0) });
        root.AddChild(new Node3D { Name = "SERIKA_SEAT_Cushion", Position = new Vector3(3, .57f, 0) });
        root.AddChild(new Node3D { Name = "SERIKA_FISH_Example", Position = new Vector3(8, 2, 0) });
        var fish = new Node3D { Name = "FISH_ROOT_01", Position = new Vector3(0, 2, 0) }; root.AddChild(fish);
        fish.AddChild(new MeshInstance3D { Name = "Fish", Mesh = new SphereMesh { Radius = .1f, Height = .2f } });
        var animation = new Animation { Length = 2 };
        var track = animation.AddTrack(Animation.TrackType.Position3D); animation.TrackSetPath(track, "FISH_ROOT_01");
        animation.PositionTrackInsertKey(track, 0, new Vector3(0, 2, 0));
        animation.PositionTrackInsertKey(track, 1, new Vector3(1, 2, 0));
        animation.PositionTrackInsertKey(track, 2, new Vector3(0, 2, 0));
        var library = new AnimationLibrary(); library.AddAnimation("AquariumSwim", animation);
        var player = new AnimationPlayer { Name = "AnimationPlayer" }; player.AddAnimationLibrary("", library); root.AddChild(player);
        return root;
    }

    private string Package(string glb)
    {
        var path = Path.Combine(_temporary, "fixture.serikaworld");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        zip.CreateEntryFromFile(glb, "world.glb");
        using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
        writer.Write("{\"format\":\"glb\",\"version\":1,\"modelFile\":\"world.glb\",\"collision\":\"authored\",\"shading\":\"pbr\",\"lighting\":\"daylit\"}");
        return path;
    }

    private static CharacterBody3D Body(uint layer)
    {
        var body = new CharacterBody3D { CollisionLayer = layer, CollisionMask = PhysicsLayers.World };
        body.AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = .25f, Height = 1.6f } });
        return body;
    }
    private void Check(bool ok, string message) { _checks++; if (!ok) _failures++; GD.Print($"WORLD_INTERACTION_TEST: {(ok ? "PASS" : "FAIL")} {message}"); }
    private static IEnumerable<Node> Descendants(Node root)
    { foreach (var child in root.GetChildren()) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
}
