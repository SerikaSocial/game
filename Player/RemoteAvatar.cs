using Godot;
using Serika.Net.Codec;
using SerikaSocial.Avatar;

namespace SerikaSocial.Player;

/// Another player's avatar, driven by SNAPSHOT INTERPOLATION.
///
/// Every wire pose is buffered with its arrival time; each rendered frame we reconstruct the
/// peer's state as of `now − delay` by blending the two snapshots that bracket that moment —
/// position lerp, rotation slerp, and per-bone quaternion slerp onto the rig. The delay tracks
/// ~2.5× the measured pose interval (≈125 ms at the 20 Hz pose rate), which hides network
/// jitter by rendering slightly in the past: a pose that arrives late is still in the buffer's
/// future, so playback never hitches. When the render time runs past the newest snapshot
/// (packet gap), motion coasts on the last measured velocity for a bounded window instead of
/// freezing or rubber-banding.
///
/// This replaces an exponential lerp toward whatever frame arrived last — the M1 stopgap that
/// steered every joint at a speed proportional to its error, snapped bones between raw
/// quantized poses at 20 Hz, and turned a burst of packets into a twitch.
public partial class RemoteAvatar : Node3D
{
    private bool _streamingBones;   // peer sends real bone rotations → don't animate locally
    private bool _streamingFingers; // ...and that pose reached LOD0, so it carries the fingers
    private bool _hasRealAvatar;

    public uint PeerId { get; private set; }
    public AvatarInstance Avatar => _avatar;
    /// True when this peer's own hands are arriving on the wire, so no local hand overlay
    /// should write to them. `HumanoidBones.Full` puts the 30 finger bones above index 22, so
    /// only a LOD0 frame carries them — a peer far enough out to be sending LOD1 leaves this
    /// rig's fingers wherever they were, which is what lets a concert prop close them locally.
    public bool StreamingFingers => _streamingBones && _streamingFingers;
    /// `Animate` is skipped entirely while a peer streams its pose, so anything that normally
    /// rides along with it has to be driven by whoever owns it instead.
    public bool LocalAnimationRunning => !_streamingBones;

    private MeshInstance3D _capsule;
    private AvatarInstance _avatar;
    private NameTag3D _nameTag;
    private StaticBody3D _body;
    private CapsuleShape3D _bodyShape;

    public static RemoteAvatar Create(uint peerId, string name)
    {
        var a = new RemoteAvatar { PeerId = peerId, Name = $"Remote_{peerId}" };
        a.BuildBody(name);
        return a;
    }

    private void BuildBody(string displayName)
    {
        // Capsule stand-in, shown only until the avatar or bean is equipped.
        _capsule = new MeshInstance3D
        {
            Mesh = new CapsuleMesh { Height = 1.8f, Radius = 0.3f },
            Position = new Vector3(0, 0.9f, 0),
        };
        _capsule.MaterialOverride = new StandardMaterial3D { AlbedoColor = ColorFromId(PeerId) };
        AddChild(_capsule);

        // Remote avatars had no physics body at all, so players walked straight through each
        // other. A static body is enough: it blocks, and because remotes are moved by
        // interpolation in _Process rather than by the physics step, anything that tried to
        // *push* would fight the lerp and jitter.
        _body = new StaticBody3D
        {
            Name = "Body",
            CollisionLayer = PhysicsLayers.RemotePlayer,
            CollisionMask = 0,
        };
        _bodyShape = new CapsuleShape3D { Height = 1.8f, Radius = 0.3f };
        _body.AddChild(new CollisionShape3D { Shape = _bodyShape, Position = new Vector3(0, 0.9f, 0) });
        AddChild(_body);

        _nameTag = new NameTag3D { Position = new Vector3(0, 2.1f, 0) };
        AddChild(_nameTag);
        _nameTag.SetLabel(displayName);

        // Start on the shared default outfit (NOT the local player's avatar — that's what made
        // every remote look like the viewer). Main swaps in this peer's real avatar once it
        // resolves; failure falls back to the bean so nobody is left a capsule.
        EquipAvatar(AvatarLibrary.DefaultOutfitPath);
    }

    private AvatarLoadingIndicator _loading;

    /// Show/hide a spinner over this peer while their own avatar downloads.
    public void SetLoading(bool on)
    {
        if (on)
        {
            if (_loading != null) return;
            _loading = AvatarLoadingIndicator.Create(_avatar?.Height ?? 1.7f, "Loading…");
            AddChild(_loading);
        }
        else if (_loading != null)
        {
            _loading.QueueFree();
            _loading = null;
        }
    }

    /// Equip an avatar from a `.ska` path; hides the capsule and floats the name tag at head height.
    /// If the path is null or fails to load, the bean fallback is used instead.
    ///
    /// `isReal` marks whether this is the peer's *own* resolved avatar (vs the shared default
    /// outfit shown until it resolves). Streamed bone poses are only applied to the peer's real
    /// avatar: replaying one avatar's bones on a different skeleton is what produced the
    /// "arms stuck up" pose while everyone was still showing as the default outfit.
    public void EquipAvatar(string skaPath, bool isReal = false)
    {
        var avatar = AvatarLibrary.InstantiateOrDefault(skaPath);
        _avatar?.QueueFree();
        _avatar = avatar;
        AddChild(avatar);
        _capsule.Visible = false;
        _nameTag.Position = new Vector3(0, avatar.Height + 0.25f, 0);
        FitBody(avatar.Height);
        _hasRealAvatar = isReal;
        _streamingBones = false; // re-decide against the new skeleton
        avatar.ResetStreamedHipsDrop();
        ClearBufferedBones();
    }

    /// Replace this remote's avatar with the bean — used when the user has blocked them,
    /// so their real model is never loaded/rendered.
    public void ShowBean()
    {
        var avatar = AvatarLibrary.InstantiateBean();
        _avatar?.QueueFree();
        _avatar = avatar;
        AddChild(avatar);
        _capsule.Visible = false;
        _nameTag.Position = new Vector3(0, avatar.Height + 0.25f, 0);
        FitBody(avatar.Height);
        // The peer's bones must stop driving this rig too, not merely stop being collected:
        // leaving `_streamingBones` set kept replaying a blocked peer's pose onto the bean.
        _hasRealAvatar = false;
        _streamingBones = false;
        avatar.ResetStreamedHipsDrop();
        ClearBufferedBones();
    }

    /// Match the blocking capsule to the equipped avatar, so a short avatar isn't surrounded by
    /// a 1.8 m invisible wall and a tall one isn't walkable-through above the shoulders.
    private void FitBody(float height)
    {
        if (_bodyShape == null) return;
        float h = Mathf.Max(0.6f, height);
        _bodyShape.Height = h;
        if (_body.GetChild(0) is CollisionShape3D cs) cs.Position = new Vector3(0, h * 0.5f, 0);
    }

    /// Apply a downloaded profile picture to this peer's name card.
    public void SetProfilePicture(byte[] bytes) => _nameTag?.SetProfilePicture(bytes);

    /// Toggle name-tag / profile-picture visibility from settings.
    public void SetTagPrefs(bool tags, bool pfp) => _nameTag?.SetPrefs(tags, pfp);

    /// Light this peer's nameplate while their voice is arriving, or grey it when locally muted.
    public void SetSpeaking(bool speaking, bool muted) => _nameTag?.SetVoiceState(speaking, muted);

    // ── Snapshot buffer ──────────────────────────────────────────────────────────────

    private sealed class Snapshot
    {
        public double Time;             // local receive time, seconds
        public Vector3 Pos;
        public Quaternion Rot = Quaternion.Identity;
        public Vector3 Vel;             // measured between the previous snapshot and this one
        public int BoneCount;           // 0 = this frame carried no usable bone pose
        public readonly Quat[] Bones = new Quat[LodExt.HumanoidBoneCount];
    }

    private const int MaxSnapshots = 24;
    private const float TeleportDistance = 3.0f;

    private readonly Snapshot[] _ring = new Snapshot[MaxSnapshots];
    private int _newest = -1;
    private int _count;
    private bool _hasTarget;

    // Jitter-adaptive render delay and the state it is measured from.
    private double _avgInterval = 0.05;   // seed = the nominal 20 Hz pose interval
    private double _lastArrival = -1;
    private double _interpDelay = 0.125;

    private static double Now() => Time.GetTicksMsec() / 1000.0;

    private void ClearBufferedBones()
    {
        for (int i = 0; i < MaxSnapshots; i++)
            if (_ring[i] != null) _ring[i].BoneCount = 0;
    }

    private void ClearSnapshots()
    {
        _newest = -1;
        _count = 0;
        _lastArrival = -1;
    }

    /// Buffer one wire pose. Called from the transport poll on the game thread.
    public void ApplyPose(PoseFrame f)
    {
        var (pos, rot) = AvatarPose.ToTransform(f);
        double now = Now();

        bool hasBones = _hasRealAvatar && f.Bones != null && f.Bones.Count > 0 && !AllIdentity(f.Bones);

        // A jump larger than conversation range is a teleport/respawn, not motion —
        // interpolating across it plays the peer flying across the map in slow motion.
        if (_count > 0 && _ring[_newest].Pos.DistanceTo(pos) > TeleportDistance)
        {
            ClearSnapshots();
            Position = pos;
            _hasTarget = true;
        }
        if (!_hasTarget)
        {
            Position = pos; // snap on first frame
            _hasTarget = true;
        }

        // Interval EMA → render delay. Gaps outside (5 ms, 1 s) are teleports/drops, not
        // jitter, and would poison the estimate.
        if (_lastArrival > 0)
        {
            double gap = now - _lastArrival;
            if (gap > 0.005 && gap < 1.0) _avgInterval += (gap - _avgInterval) * 0.1;
        }
        _lastArrival = now;
        _interpDelay = System.Math.Clamp(_avgInterval * 2.5 + 0.02, 0.10, 0.35);

        int slot = (_newest + 1) % MaxSnapshots;
        var s = _ring[slot] ??= new Snapshot();

        // Velocity between arrivals is what extrapolation coasts on during packet gaps.
        // Measured against the CURRENT newest, never the slot being reused — when the ring is
        // full that slot holds the oldest snapshot, and against a recycled slot's timestamp
        // the velocity collapses to near zero, killing the coast entirely.
        float dt = _count > 0 ? (float)System.Math.Max(1e-3, now - _ring[_newest].Time) : 0f;
        Vector3 vel = _count > 0 ? (pos - _ring[_newest].Pos) / dt : Vector3.Zero;

        _newest = slot;
        _count = System.Math.Min(_count + 1, MaxSnapshots);

        s.Time = now;
        s.Pos = pos;
        s.Rot = rot;
        s.Vel = vel;
        if (hasBones)
        {
            int n = System.Math.Min(f.Bones.Count, s.Bones.Length);
            for (int i = 0; i < n; i++) s.Bones[i] = f.Bones[i];
            s.BoneCount = n;
        }
        else
        {
            s.BoneCount = 0;
        }
        _streamingFingers = s.BoneCount > HumanoidBones.Lod1.Length;
        if (_streamingBones != hasBones)
        {
            _streamingBones = hasBones;
            _avatar?.ResetStreamedHipsDrop();
        }
    }

    private static bool AllIdentity(System.Collections.Generic.List<Quat> bones)
    {
        foreach (var q in bones)
            if (Mathf.Abs(q.W) < 0.9999f || Mathf.Abs(q.X) > 1e-4f ||
                Mathf.Abs(q.Y) > 1e-4f || Mathf.Abs(q.Z) > 1e-4f) return false;
        return true;
    }

    public override void _Process(double delta)
    {
        var cam = GetViewport()?.GetCamera3D();
        if (cam != null && _nameTag != null && _nameTag.Visible)
        {
            var tagPos = GlobalPosition + new Vector3(0, (_avatar?.Height ?? 1.8f) + 0.25f, 0);
            var toTag = tagPos - cam.GlobalPosition;
            float dist = toTag.Length();
            if (dist > 0.2f && dist < 22.0f)
            {
                float dot = (-cam.GlobalTransform.Basis.Z).Dot(toTag / dist);
                _nameTag.SetFocused(dot > 0.95f);
            }
            else
            {
                _nameTag.SetFocused(false);
            }
        }

        if (!_hasTarget || _count == 0) return;

        float dt = (float)delta;
        var prev = Position;

        double renderT = Now() - _interpDelay;
        var newest = _ring[_newest];

        if (renderT >= newest.Time)
        {
            // Past the newest snapshot — a packet gap. Coast on the last measured velocity
            // for a bounded window so motion degrades into a glide instead of a freeze;
            // beyond that, hold. Never extrapolate bones — joints freeze, which reads fine,
            // while guessed rotations read as twitching.
            double over = renderT - newest.Time;
            double coast = System.Math.Min(over, _avgInterval * 4.0 + 0.20);
            SetRoot(newest.Pos + newest.Vel * (float)coast, newest.Rot);
            ApplyBones(newest, newest, 0f);
        }
        else
        {
            // Find the bracketing pair: newest→oldest scan, buffer is ≤ 24 deep.
            Snapshot s1 = newest;
            Snapshot s0 = newest;
            for (int i = 1; i < _count; i++)
            {
                int idx = (_newest - i + MaxSnapshots * 4) % MaxSnapshots;
                var cand = _ring[idx];
                if (cand.Time <= renderT) { s0 = cand; break; }
                s0 = s1 = cand;
            }

            if (ReferenceEquals(s0, s1) || s1.Time <= s0.Time)
            {
                // Render time is older than everything buffered (delay just shrank) — clamp.
                SetRoot(s0.Pos, s0.Rot);
                ApplyBones(s0, s0, 0f);
            }
            else
            {
                float a = (float)((renderT - s0.Time) / (s1.Time - s0.Time));
                SetRoot(s0.Pos.Lerp(s1.Pos, a), s0.Rot.Slerp(s1.Rot, a).Normalized());
                ApplyBones(s0, s1, a);
            }
        }

        // Hips height is not on the wire; a crouching peer's bent legs would otherwise hover
        // at standing height. Reconstructed from the streamed leg pose, crouch-gated.
        if (_streamingBones) _avatar?.ApplyStreamedHipsDrop(dt);

        // Only guess an animation from observed motion when the peer isn't streaming bones;
        // otherwise the local procedural cycle would fight the pose we just applied.
        if (_streamingBones) return;
        float speed = dt > 0 ? (new Vector2(Position.X, Position.Z) - new Vector2(prev.X, prev.Z)).Length() / dt : 0f;
        _avatar?.Animate(delta, speed, true);
    }

    private void SetRoot(Vector3 pos, Quaternion rot)
    {
        Position = pos;
        Quaternion = rot;
    }

    private void ApplyBones(Snapshot a, Snapshot b, float alpha)
    {
        if (!_streamingBones || a.BoneCount == 0) return;
        _avatar?.BlendBonePose(a.Bones, a.BoneCount, b.Bones, b.BoneCount, alpha);
    }

    /// Stable per-peer colour so avatars are visually distinguishable without textures.
    private static Color ColorFromId(uint id)
    {
        float hue = (id * 0.61803398875f) % 1.0f; // golden-ratio hue spacing
        return Color.FromHsv(hue, 0.6f, 0.9f);
    }
}
