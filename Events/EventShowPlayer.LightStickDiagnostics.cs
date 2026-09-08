using System;
using System.Threading.Tasks;
using Godot;
using SerikaSocial.Avatar;
using SerikaSocial.Player;

namespace SerikaSocial.Events;

public partial class EventShowPlayer
{
    public sealed record LightStickGripResult(bool Passed, int Poses, float MaxPositionError,
        float MaxAxisErrorDegrees, float GripOffset, float VisibleLength);
    public sealed record LightStickLifecycleResult(bool Passed, bool BeanGrip, bool AvatarSwap,
        bool PlayerLeave, bool FirstPersonVisibility, bool Teardown,
        bool PairedClock = false, bool BeatResponse = false);

    /// Uses a separate accessory manager and off-stage test player, keeping live accessories
    /// and the caller's show configuration untouched. The player is the actual client class.
    public async Task<LightStickLifecycleResult> RunLightStickLifecycleChecks()
    {
        if (!LightStickTemplateReady) return new(false,false,false,false,false,false);
        var probe = new EventShowPlayer { Name = "ConcertStickLifecycleProbe",
            _state = new LiveEvent { Config = new ShowConfig { LightSticks = true } } };
        AddChild(probe);
        probe.SetupPlayerLightSticks(_lightStickWorld);
        var scene = new Node3D { Name = "IsolatedStickPlayers", Position = new Vector3(0,-200,0) };
        probe.AddChild(scene);
        bool bean = false, swapped = false, left = false, visible = false, restored = false;
        bool pairedClock = false, beatResponse = false;
        try {
            var player = new LocalPlayer { ControlsEnabled = false };
            scene.AddChild(player);
            player.SetProcess(false); player.SetPhysicsProcess(false);
            player.SetAvatar(AvatarInstance.CreateBean());
            probe.RefreshPlayerLightSticks(scene);
            ulong originalId = player.Avatar.GetInstanceId();
            if (probe._playerConcertSticks.TryGetValue(originalId, out var held)) {
                visible = held.Hands.Count == 2;
                foreach(var hand in held.Hands.Values) {
                    foreach (Node node in hand.Prop.FindChildren("*","MeshInstance3D",true,false)) {
                        uint layers = ((MeshInstance3D)node).Layers;
                        visible &= (layers & (0xfffffu & ~LocalPlayer.FpCullLayers)) != 0
                            && (layers & (0xfffffu & ~LocalPlayer.NonFpCullLayers)) != 0;
                    }
                }
                bean = (await probe.RunLightStickGripChecks(player.Avatar)).Passed;
            }
            var retiredAvatar = player.Avatar;
            retiredAvatar.SetConcertCheerEnabled(true);
            player.SetAvatar(AvatarInstance.CreateBean());
            probe.RefreshPlayerLightSticks(scene);
            bool cheerRetired = !retiredAvatar.ConcertCheerEnabled;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            probe.RefreshPlayerLightSticks(scene);
            swapped = probe.PlayerLightStickCount == 2 && probe.PlayerLightStickOwnerCount == 1
                && !probe._playerConcertSticks.ContainsKey(originalId)
                && probe._playerConcertSticks.ContainsKey(player.Avatar.GetInstanceId()) && cheerRetired;
            GD.Print($"CONCERT_STICK_CHEER retired_avatar_disabled={cheerRetired}");
            scene.RemoveChild(player); player.QueueFree();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            probe.RefreshPlayerLightSticks(scene);
            left = probe.PlayerLightStickCount == 0;
            var finalAvatar = AvatarInstance.CreateBean();
            scene.AddChild(finalAvatar);
            var finalProp = probe.AttachAvatarLightStick(finalAvatar);
            var finalLeft = probe.AttachAvatarLightStick(finalAvatar,true);
            var attachment = finalProp?.GetParent();
            var leftAttachment = finalLeft?.GetParent();
            if(finalProp!=null && finalLeft!=null) {
                probe._state.Config.MusicFps=10;
                probe._state.Config.Beats=new float[] {0,1,0,1,0};
                probe._lightStickScanDelay=1000;
                probe.UpdatePlayerLightSticks(.1,true,0);
                var firstRight=finalProp.Transform;var firstLeft=finalLeft.Transform;
                float firstGlow=probe._playerStickGlow.EmissionEnergyMultiplier;
                probe.UpdatePlayerLightSticks(.1,true,.4);
                bool paused=firstRight==finalProp.Transform && firstLeft==finalLeft.Transform
                    && firstGlow==probe._playerStickGlow.EmissionEnergyMultiplier;
                probe.UpdatePlayerLightSticks(.31,true,0);
                bool advanced=firstRight!=finalProp.Transform && firstLeft!=finalLeft.Transform;
                probe.UpdatePlayerLightSticks(.1,true,0);
                bool sought=firstRight==finalProp.Transform && firstLeft==finalLeft.Transform
                    && firstGlow==probe._playerStickGlow.EmissionEnergyMultiplier;
                probe._state.Config.Beats=new float[] {0,0,0,0,0};
                probe.UpdatePlayerLightSticks(.1,true,0);
                beatResponse=probe._playerStickGlow.EmissionEnergyMultiplier<firstGlow
                    && firstRight.Basis!=finalProp.Basis && firstLeft.Basis!=finalLeft.Basis
                    && firstRight.Origin==finalProp.Position && firstLeft.Origin==finalLeft.Position;
                probe.UpdatePlayerLightSticks(.1,false,0);
                var idleRight=finalProp.Transform;var idleLeft=finalLeft.Transform;
                probe.UpdatePlayerLightSticks(.9,false,0);
                pairedClock=paused && advanced && sought && idleRight==finalProp.Transform && idleLeft==finalLeft.Transform;
                GD.Print($"CONCERT_STICK_CLOCK pause={paused} advance={advanced} backward_seek={sought} "
                    + $"both_hand_beat_response={beatResponse} static_outside_show={idleRight==finalProp.Transform && idleLeft==finalLeft.Transform}");
            }
            probe.RestorePlayerLightSticks();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            restored = probe.PlayerLightStickCount == 0 && !IsInstanceValid(attachment)
                && !IsInstanceValid(finalProp) && !IsInstanceValid(leftAttachment) && !IsInstanceValid(finalLeft);
        }
        finally {
            probe.RestorePlayerLightSticks();
            probe.QueueFree();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        bool passed = bean && swapped && left && visible && restored && pairedClock && beatResponse;
        GD.Print($"CONCERT_STICK_LIFECYCLE pass={passed} bean={bean} swap={swapped} leave={left} "
            + $"fp_and_third_person={visible} teardown={restored} paired_clock={pairedClock} beat_response={beatResponse}");
        return new(passed,bean,swapped,left,visible,restored,pairedClock,beatResponse);
    }

    /// Rehearsal-only check. Temporarily pauses show sampling, drives the supplied avatar's
    /// wrists through four poses each, and measures the real BoneAttachments after scene frames.
    /// Restores the wrist, show processing state and pre-existing accessory afterward.
    public async Task<LightStickGripResult> RunLightStickGripChecks(AvatarInstance avatar)
    {
        if (!TryGetLightStickGrip(avatar,false,out _) || !TryGetLightStickGrip(avatar,true,out _) || !LightStickTemplateReady)
            return new(false, 0, float.PositiveInfinity, float.PositiveInfinity, 0, 0);
        var skeleton = avatar.Skeleton;
        int right = avatar.BoneOf("rightHand"), leftBone = avatar.BoneOf("leftHand");
        var originalRight = skeleton.GetBonePoseRotation(right);
        var originalLeft = skeleton.GetBonePoseRotation(leftBone);
        bool processed = IsProcessing();
        ulong id = avatar.GetInstanceId();
        bool existed = _playerConcertSticks.ContainsKey(id);
        float positionError = 0, angleError = 0, length = 0;
        float offset = 0;
        int poses = 0;
        var originalPropTransforms = new System.Collections.Generic.List<(Node3D Prop,Transform3D Transform)>();
        SetProcess(false);
        try {
            foreach(bool left in new[] {false,true}) {
                TryGetLightStickGrip(avatar,left,out var grip);
                int hand=left?leftBone:right;
                var original=left?originalLeft:originalRight;
                var rest=skeleton.GetBoneGlobalRest(hand);
                offset=Math.Max(offset,(rest*grip.Origin).DistanceTo(rest.Origin));
                var prop = AttachAvatarLightStick(avatar,left);
                if (prop == null) return new(false, 0, float.PositiveInfinity, float.PositiveInfinity, offset, 0);
                originalPropTransforms.Add((prop,prop.Transform));
                prop.Transform=grip;
                foreach (var rotation in new[] {
                    Quaternion.Identity, new Quaternion(Vector3.Right,.8f),
                    new Quaternion(Vector3.Up,-1.1f), new Quaternion(Vector3.Forward,1.4f)
                }) {
                    skeleton.SetBonePoseRotation(hand, original * rotation);
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    var expected = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(hand) * grip;
                    var actual = prop.GlobalTransform;
                    if (!actual.Origin.IsFinite() || !actual.Basis.Y.IsFinite())
                        return new(false, poses, float.PositiveInfinity, float.PositiveInfinity, offset, 0);
                    positionError = Math.Max(positionError, expected.Origin.DistanceTo(actual.Origin));
                    float dot = Mathf.Clamp(expected.Basis.Y.Normalized().Dot(actual.Basis.Y.Normalized()),-1,1);
                    angleError = Math.Max(angleError, Mathf.RadToDeg(Mathf.Acos(dot)));
                    length = actual.Basis.Y.Length() * .30f;
                    poses++;
                }
            }
        }
        finally {
            if (IsInstanceValid(skeleton)) {
                skeleton.SetBonePoseRotation(right, originalRight);
                skeleton.SetBonePoseRotation(leftBone, originalLeft);
            }
            foreach(var saved in originalPropTransforms) if(IsInstanceValid(saved.Prop))saved.Prop.Transform=saved.Transform;
            if (!existed && _playerConcertSticks.Remove(id, out var added))
                foreach(var hand in added.Hands.Values) if(IsInstanceValid(hand.Attachment))hand.Attachment.QueueFree();
            SetProcess(processed);
        }
        bool passed = poses == 8 && positionError < .002f && angleError < .15f
            && offset > .01f && offset < .13f && float.IsFinite(length) && length > .05f;
        GD.Print($"CONCERT_STICK_GRIP pass={passed} poses={poses} position_error={positionError:F6}m "
            + $"axis_error={angleError:F4}deg wrist_offset={offset:F4}m length={length:F4}m");
        return new(passed, poses, positionError, angleError, offset, length);
    }
}
