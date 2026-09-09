using System;
using System.Collections.Generic;
using Godot;
using SerikaSocial.Events;

namespace SerikaSocial.Avatar;

public sealed partial class AvatarInstance
{
    private bool _concertCheerRequested, _concertCheerAttempted;
    private bool _concertPropGripRequested;
    private float _concertCheerBlend, _concertPropGripBlend;
    private double _concertCheerClock, _concertCheerLength;
    private int[] _concertCheerBones;
    private Quaternion[,] _concertCheerFrames;
    private Quaternion[] _concertArmBase, _concertArmLast;
    private HandPoser[] _concertCheerHands;
    private int[] _concertGripBones;
    private Quaternion[] _concertGripBaseline, _concertGripLast;
    private readonly float[] _concertGripCurl = new float[5];

    /// Concert-only opt-in from the desktop player. Other emotes and locomotion keep priority.
    public bool ConcertCheerEnabled => _concertCheerRequested;
    public bool ConcertCheerReady => _concertCheerFrames != null;
    public void SetConcertCheerEnabled(bool enabled) => _concertCheerRequested = enabled;
    public void SetConcertCheerClock(double seconds)
    {
        if (double.IsFinite(seconds)) _concertCheerClock = Math.Max(0, seconds);
    }

    private static float CheerEase(float value)
    {
        value = Mathf.Clamp(value, 0, 1);
        return value * value * value * (value * (value * 6 - 15) + 10);
    }

    private bool CacheConcertCheer()
    {
        if (_concertCheerAttempted) return ConcertCheerReady;
        _concertCheerAttempted = true;
        string[] limbs = { "Shoulder", "UpperArm", "LowerArm", "Hand" };
        _concertCheerBones = new int[8];
        for (int j = 0; j < limbs.Length; j++) {
            _concertCheerBones[j] = BoneOf("right" + limbs[j]);
            _concertCheerBones[j + 4] = BoneOf("left" + limbs[j]);
            if (j > 0 && (_concertCheerBones[j] < 0 || _concertCheerBones[j + 4] < 0)) return false;
        }
        // Sample the already shipped existing clip once, then keep only its arm tracks.
        // A separate retargeter leaves the normal locomotion state machine untouched.
        var saved = new Transform3D[Skeleton.GetBoneCount()];
        for (int i = 0; i < saved.Length; i++) saved[i] = Skeleton.GetBonePose(i);
        AnimRetargeter capture = null;
        try {
            capture = AnimRetargeter.Create("res://Assets/Animations/locomotion.glb", Skeleton, _roleToBone);
            if (capture == null || capture.ShowClipLength("Victory") <= 0) return false;
            AddChild(capture);
            _concertCheerLength = capture.ShowClipLength("Victory");
            int count = (int)Math.Ceiling(_concertCheerLength * 60) + 1;
            var frames = new Quaternion[count, 8];
            var normal = (Skeleton.GetBoneGlobalRest(BoneOf("rightUpperArm")).Origin
                        - Skeleton.GetBoneGlobalRest(BoneOf("leftUpperArm")).Origin).Normalized();
            if (!normal.IsFinite() || normal.LengthSquared() < .9f) return false;
            var reflection = new Basis(Vector3.Right - 2 * normal * normal.X,
                                       Vector3.Up - 2 * normal * normal.Y,
                                       Vector3.Back - 2 * normal * normal.Z);
            for (int sample = 0; sample < count; sample++) {
                double time = _concertCheerLength * sample / (count - 1);
                capture.SampleShowClip("Victory", time);
                for (int j = 0; j < 4; j++) {
                    int right = _concertCheerBones[j], left = _concertCheerBones[j + 4];
                    if (right < 0 || left < 0) { frames[sample,j] = frames[sample,j+4] = Quaternion.Identity; continue; }
                    var pose = Skeleton.GetBonePoseRotation(right);
                    frames[sample, j] = pose;
                    // Mirror the sampled rotational delta in the avatar's anatomical
                    // rest frame, then express it in the left bone's own parent axes.
                    if (right < 0 || left < 0) continue;
                    int rp = Skeleton.GetBoneParent(right), lp = Skeleton.GetBoneParent(left);
                    var rightParent = rp >= 0 ? Skeleton.GetBoneGlobalRest(rp).Basis.Orthonormalized() : Basis.Identity;
                    var leftParent = lp >= 0 ? Skeleton.GetBoneGlobalRest(lp).Basis.Orthonormalized() : Basis.Identity;
                    var rightRest = Skeleton.GetBoneRest(right).Basis.Orthonormalized();
                    var leftRest = Skeleton.GetBoneRest(left).Basis.Orthonormalized();
                    var globalDelta = rightParent * new Basis(pose) * rightRest.Inverse() * rightParent.Inverse();
                    var mirrored = leftParent.Inverse() * reflection * globalDelta * reflection * leftParent * leftRest;
                    frames[sample, j + 4] = mirrored.Orthonormalized().GetRotationQuaternion();
                }
            }
            // Keep the mirrored pump clear of the head and neck. Preserve the source
            // height/rhythm while solving a modest outward, forward hand reach.
            for (int sample = 0; sample < count; sample++) {
                var pose = CheerArmTransforms(frames, sample);
                int upper = _concertCheerBones[1], lower = _concertCheerBones[2], hand = _concertCheerBones[3];
                var h = pose[upper].Origin;
                float a = h.DistanceTo(pose[lower].Origin), b = pose[lower].Origin.DistanceTo(pose[hand].Origin);
                // `normal` runs left shoulder -> right shoulder, i.e. the avatar's RIGHT (+X on a
                // humanoid rig). `normal x Up` is then +Z — which is BEHIND a rig that faces -Z.
                // So the reach solve pushed both hands 16 cm out through the back on every pump.
                // `Up x normal` is the actual forward.
                var forward = Vector3.Up.Cross(normal).Normalized();
                int chest = BoneOf("chest"); if (chest < 0) chest = BoneOf("spine");
                var center = chest >= 0 ? pose[chest].Origin : pose[upper].Origin - normal * .15f;
                var goal = pose[hand].Origin;
                float lateral = (goal - center).Dot(normal), front = (goal - center).Dot(forward);
                goal += normal * Math.Max(0, (a + b) * .62f - lateral);
                goal += forward * Math.Max(0, (a + b) * .28f - front);
                var d = goal - h; float length = Math.Clamp(d.Length(), Math.Abs(a-b)+.005f, (a+b)*.975f);
                var direction = d.Normalized(); goal = h + direction * length;
                var pole = normal; pole = (pole - direction * pole.Dot(direction)).Normalized();
                float along = (a*a-b*b+length*length)/(2*length);
                var elbow = h + direction * along + pole * Mathf.Sqrt(Math.Max(0,a*a-along*along));
                foreach (int j in new[]{1,2}) {
                    int bone = _concertCheerBones[j], child = _concertCheerBones[j+1], parent = Skeleton.GetBoneParent(bone);
                    pose = CheerArmTransforms(frames,sample);
                    var current = (pose[child].Origin-pose[bone].Origin).Normalized();
                    var wanted = j==1 ? (elbow-h).Normalized() : (goal-elbow).Normalized();
                    var desired = new Basis(CheerFromTo(current,wanted)) * pose[bone].Basis;
                    frames[sample,j]=(pose[parent].Basis.Inverse()*desired).Orthonormalized().GetRotationQuaternion();
                }
            }
            // Direction-only retargeting can flip forearm roll when a raised limb
            // crosses the antipodal rest direction. Parallel-transport its orientation
            // along the sampled direction path, preserving the sampled hand trajectory.
            var transported = new Basis[4];
            var previousDirection = new Vector3[4];
            for (int sample = 0; sample < count; sample++) {
                var globals = CheerArmGlobals(frames, sample);
                var desired = new Basis[4];
                for (int j = 1; j <= 2; j++) {
                    int bone = _concertCheerBones[j], child = _concertCheerBones[j + 1];
                    var localDirection = Skeleton.GetBoneGlobalRest(bone).Basis.Inverse()
                        * (Skeleton.GetBoneGlobalRest(child).Origin - Skeleton.GetBoneGlobalRest(bone).Origin);
                    var direction = (globals[bone] * localDirection).Normalized();
                    if (sample == 0) transported[j] = globals[bone];
                    else transported[j] = new Basis(CheerFromTo(previousDirection[j], direction)) * transported[j];
                    desired[j] = transported[j]; previousDirection[j] = direction;
                }
                for (int j = 1; j <= 2; j++) {
                    int bone = _concertCheerBones[j], parent = Skeleton.GetBoneParent(bone);
                    globals = CheerArmGlobals(frames, sample);
                    frames[sample, j] = (globals[parent].Inverse() * desired[j]).Orthonormalized().GetRotationQuaternion();
                }
                // A stable neutral wrist carries the grip through the arm pump without
                // inheriting the generic source retargeter's unconstrained wrist roll.
                frames[sample, 3] = Skeleton.GetBoneRest(_concertCheerBones[3]).Basis.Orthonormalized().GetRotationQuaternion();
                for (int j = 0; j < 4; j++) {
                    int right = _concertCheerBones[j], left = _concertCheerBones[j + 4];
                    if (right < 0 || left < 0) continue;
                    int rp = Skeleton.GetBoneParent(right), lp = Skeleton.GetBoneParent(left);
                    var rightParent = rp >= 0 ? Skeleton.GetBoneGlobalRest(rp).Basis.Orthonormalized() : Basis.Identity;
                    var leftParent = lp >= 0 ? Skeleton.GetBoneGlobalRest(lp).Basis.Orthonormalized() : Basis.Identity;
                    var delta = rightParent * new Basis(frames[sample,j]) * Skeleton.GetBoneRest(right).Basis.Orthonormalized().Inverse() * rightParent.Inverse();
                    frames[sample,j+4] = (leftParent.Inverse() * reflection * delta * reflection * leftParent * Skeleton.GetBoneRest(left).Basis.Orthonormalized()).Orthonormalized().GetRotationQuaternion();
                }
            }
            // Aim each actual palm grip upward, with a small outward cant. Neutral
            // local wrist axes differ between avatars and can point a prop backwards.
            for (int sample = 0; sample < count; sample++) {
                foreach (bool left in new[]{false,true}) {
                    int j = left ? 7 : 3, bone = _concertCheerBones[j];
                    if (!EventShowPlayer.TryGetLightStickGrip(this,left,out var grip)) continue;
                        var forward = Vector3.Up.Cross(normal).Normalized();
                    var shaft = (Vector3.Up + normal * (left ? -.13f : .13f)).Normalized();
                    var across = shaft.Cross(forward).Normalized();
                    var desiredGrip = new Basis(across,shaft,across.Cross(shaft).Normalized());
                    var globals = CheerArmGlobals(frames,sample, true);
                    int parent = Skeleton.GetBoneParent(bone);
                    frames[sample,j]=(globals[parent].Inverse()*desiredGrip*grip.Basis.Inverse()).Orthonormalized().GetRotationQuaternion();
                }
            }
            // Symmetric five-frame quaternion filtering removes the sharp return
            // acceleration without a frame-dependent spring or delayed phase clock.
            // Nine binomial passes have sigma3 source frames (50ms at60Hz).
            for (int pass=0;pass<9;pass++) {
                var filtered=(Quaternion[,])frames.Clone();
                int[] weights={1,4,6,4,1};
                for(int sample=0;sample<count;sample++)for(int j=0;j<8;j++) {
                    var reference=frames[sample,j];var total=new Vector4();
                    for(int k=-2;k<=2;k++) {
                        var q=frames[Math.Clamp(sample+k,0,count-1),j];
                        float sign=reference.Dot(q)<0?-1:1;
                        total+=new Vector4(q.X,q.Y,q.Z,q.W)*(sign*weights[k+2]);
                    }
                    filtered[sample,j]=new Quaternion(total.X,total.Y,total.Z,total.W).Normalized();
                }
                frames=filtered;
            }
            // The source pump returns close to its start. Ease the final180ms to that
            // exact pose so shared-clock looping cannot create a wrist seam.
            for (int sample = 0; sample < count; sample++) {
                double time = _concertCheerLength * sample / (count - 1);
                float seam = CheerEase((float)((time - (_concertCheerLength - .18)) / .18));
                for (int j = 0; j < 8; j++) frames[sample, j] = frames[sample, j].Slerp(frames[0, j], seam).Normalized();
            }
            _concertCheerFrames = frames;
            return true;
        } catch (Exception error) {
            GD.PushWarning($"Concert cheer unavailable for this avatar: {error.Message}");
            return false;
        } finally {
            for (int i = 0; i < saved.Length; i++) Skeleton.SetBonePose(i, saved[i]);
            if (IsInstanceValid(capture)) capture.Free();
        }
    }

    private Basis[] CheerArmGlobals(Quaternion[,] frames, int sample, bool both=false)
    {
        var globals = new Basis[Skeleton.GetBoneCount()];
        for (int bone = 0; bone < globals.Length; bone++) {
            int role = Array.IndexOf(_concertCheerBones, bone);
            var local = role >= 0 && (both || role < 4) ? new Basis(frames[sample, role]) : Skeleton.GetBoneRest(bone).Basis.Orthonormalized();
            int parent = Skeleton.GetBoneParent(bone);
            globals[bone] = parent >= 0 ? globals[parent] * local : local;
        }
        return globals;
    }

    private Transform3D[] CheerArmTransforms(Quaternion[,] frames,int sample)
    {
        var globals=new Transform3D[Skeleton.GetBoneCount()];
        for(int bone=0;bone<globals.Length;bone++) {
            var local=Skeleton.GetBoneRest(bone);int role=Array.IndexOf(_concertCheerBones,bone);
            if(role>=0&&role<4)local.Basis=new Basis(frames[sample,role]);
            int parent=Skeleton.GetBoneParent(bone);globals[bone]=parent>=0?globals[parent]*local:local;
        }
        return globals;
    }

    private static Quaternion CheerFromTo(Vector3 from, Vector3 to)
    {
        float dot = from.Dot(to);
        if (dot > .99999f) return Quaternion.Identity;
        if (dot < -.99999f) {
            var axis = from.Cross(Vector3.Up);
            if (axis.LengthSquared() < 1e-8f) axis = from.Cross(Vector3.Right);
            return new Quaternion(axis.Normalized(), Mathf.Pi);
        }
        var cross = from.Cross(to);
        return new Quaternion(cross.X, cross.Y, cross.Z, 1 + dot).Normalized();
    }

    // Called after ordinary Animate, before the owner's network pose collection.
    // This overlay never writes pelvis, legs, torso, head or tracked VR controllers.
    private void ApplyConcertCheer(float dt, float speed, bool onFloor, bool crouching)
    {
        bool eligible = _concertCheerRequested && speed < .5f && onFloor && !crouching
            && _emote == Emote.None && !_customClipActive;
        float target = eligible && CacheConcertCheer() ? 1 : 0;
        _concertCheerBlend = Mathf.MoveToward(_concertCheerBlend, target, Math.Max(0, dt) / .42f);
        StepConcertPropGrip(dt);
        if (_concertCheerBlend <= 0 || !ConcertCheerReady) { DriveConcertGrip(_concertPropGripBlend); return; }
        double phase = _concertCheerClock % _concertCheerLength;
        double x = phase / _concertCheerLength * (_concertCheerFrames.GetLength(0) - 1);
        int a = (int)Math.Floor(x), b = Math.Min(a + 1, _concertCheerFrames.GetLength(0) - 1);
        float blend = CheerEase(_concertCheerBlend);
        _concertArmBase=new Quaternion[_concertCheerBones.Length];_concertArmLast=new Quaternion[_concertCheerBones.Length];
        for (int j = 0; j < _concertCheerBones.Length; j++) {
            int bone = _concertCheerBones[j]; if(bone<0)continue;
            var pose = _concertCheerFrames[a, j].Slerp(_concertCheerFrames[b, j], (float)(x - a));
            _concertArmBase[j]=Skeleton.GetBonePoseRotation(bone);
            _concertArmLast[j]=_concertArmBase[j].Slerp(pose, blend);
            Skeleton.SetBonePoseRotation(bone, _concertArmLast[j]);
        }
        // A hand holding a light stick stays closed whether or not the cheer is running, so the
        // grip takes whichever driver wants it harder.
        DriveConcertGrip(Math.Max(blend, _concertPropGripBlend));
    }

    private void RestoreConcertCheerArmBase()
    {
        if(_concertArmBase==null||Skeleton==null)return;
        for(int j=0;j<_concertCheerBones.Length;j++) {
            int bone=_concertCheerBones[j];if(bone<0)continue;
            // Generic locomotion does not rewrite every wrist. Remove our previous
            // overlay before evaluating the next base pose, preventing accumulation.
            if(Skeleton.GetBonePoseRotation(bone).AngleTo(_concertArmLast[j])<.002f)
                Skeleton.SetBonePoseRotation(bone,_concertArmBase[j]);
        }
        _concertArmBase=null;_concertArmLast=null;
    }

    /// Ask this rig to close its hands around a held concert prop.
    ///
    /// The grip used to be a side effect of the cheer animation, so only a local desktop player
    /// who was actively cheering ever closed their fingers. Everyone else — every peer, every VR
    /// player, and the local player the moment they stopped cheering — carried a light stick
    /// through a flat open palm, which is what "the sticks aren't properly held" is. Holding a
    /// prop is its own state and outlives the cheer.
    ///
    /// For the local player this reaches other people for free: `HumanoidBones.Full` carries all
    /// 30 finger bones and `CaptureBonePose` samples the posed skeleton, so a near peer receiving
    /// LOD0 replays the real closed hand. `StepConcertPropGrip` covers the rest — a peer far
    /// enough out to be sending LOD1 has no finger data on the wire at all.
    public void SetConcertPropGrip(bool holding) => _concertPropGripRequested = holding;
    public bool ConcertPropGripEngaged => _concertPropGripBlend > 0;

    /// Ease the prop grip toward its requested state. Separate from writing it, because the two
    /// callers differ: `ApplyConcertCheer` steps then writes as part of `Animate`, while a peer
    /// replaying streamed bones never runs `Animate` at all and needs `DriveStreamedPropGrip`.
    private void StepConcertPropGrip(float dt)
    {
        float target = _concertPropGripRequested && !_customClipActive ? 1 : 0;
        _concertPropGripBlend = Mathf.MoveToward(_concertPropGripBlend, target, Math.Max(0, dt) / .28f);
    }

    /// Step and write the grip for a rig whose `Animate` is not running.
    ///
    /// `RemoteAvatar` returns before `Animate` whenever the peer is streaming bones, so nothing
    /// on that path would ever close the hand. Writing here is safe precisely because the wire
    /// is not delivering fingers: the caller passes `streamingFingers` and we stand down when it
    /// is true, rather than fighting `BlendBonePose` for the same joints every frame.
    public void DriveStreamedPropGrip(double delta, bool streamingFingers)
    {
        if (Skeleton == null) return;
        if (streamingFingers) { _concertPropGripBlend = 0; ReleaseConcertCheerGrip(); return; }
        StepConcertPropGrip((float)delta);
        DriveConcertGrip(_concertPropGripBlend);
    }

    /// Drop the prop grip now rather than easing it out. A peer replaying streamed bones has no
    /// per-frame driver left once its stick is taken away, so an eased release would freeze the
    /// hand half closed forever.
    public void ReleaseConcertPropGrip()
    {
        _concertPropGripRequested = false;
        _concertPropGripBlend = 0;
        if (_concertCheerBlend <= 0) ReleaseConcertCheerGrip();
    }

    private void DriveConcertGrip(float weight)
    {
        if (weight <= 0) { ReleaseConcertCheerGrip(); return; }
        ApplyConcertCheerGrip(weight);
    }

    private void ApplyConcertCheerGrip(float weight)
    {
        _concertCheerHands ??= new[]{new HandPoser(this,false),new HandPoser(this,true)};
        if(_concertGripBaseline==null) {
            var bones=new List<int>();
            foreach(var role in _roleToBone)if(role.Key.Contains("Thumb")||role.Key.Contains("Index")||role.Key.Contains("Middle")||role.Key.Contains("Ring")||role.Key.Contains("Little"))bones.Add(role.Value);
            _concertGripBones=bones.ToArray();_concertGripBaseline=new Quaternion[bones.Count];_concertGripLast=new Quaternion[bones.Count];
            for(int i=0;i<bones.Count;i++)_concertGripBaseline[i]=Skeleton.GetBonePoseRotation(bones[i]);
        }
        _concertGripCurl[0]=.95f*weight;_concertGripCurl[1]=.67f*weight;_concertGripCurl[2]=.75f*weight;_concertGripCurl[3]=.80f*weight;_concertGripCurl[4]=.84f*weight;
        foreach(var hand in _concertCheerHands)hand.Apply(_concertGripCurl,null,.8f*weight);
        for(int i=0;i<_concertGripBones.Length;i++)_concertGripLast[i]=Skeleton.GetBonePoseRotation(_concertGripBones[i]);
    }

    private void ReleaseConcertCheerGrip()
    {
        if(_concertGripBaseline==null||Skeleton==null)return;
        for(int i=0;i<_concertGripBones.Length;i++) {
            // A custom animation or another hand driver may already own this joint.
            // Restore only rotations that are still the last value this overlay wrote.
            if(Skeleton.GetBonePoseRotation(_concertGripBones[i]).AngleTo(_concertGripLast[i])<.002f)
                Skeleton.SetBonePoseRotation(_concertGripBones[i],_concertGripBaseline[i]);
        }
        _concertGripBaseline=null;_concertGripLast=null;
    }
}
