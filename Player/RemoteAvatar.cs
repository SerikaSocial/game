using Godot;
using Serika.Net.Codec;
using SerikaSocial.Avatar;

namespace SerikaSocial.Player;

/// Another player's avatar. Buffers the latest wire pose and interpolates toward it, so
/// remote motion stays smooth at a 20Hz pose rate. The 100ms buffer matches the plan's
/// snapshot-interpolation delay — we render slightly in the past to hide jitter.
public partial class RemoteAvatar : Node3D
{
    private Vector3 _targetPos;
    private Quaternion _targetRot = Quaternion.Identity;
    private bool _hasTarget;
    private bool _streamingBones;   // peer sends real bone rotations → don't animate locally
    private bool _hasRealAvatar;

    public uint PeerId { get; private set; }

    private MeshInstance3D _capsule;
    private AvatarInstance _avatar;
    private Label3D _nameTag;

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

        _nameTag = new Label3D
        {
            Text = displayName,
            Position = new Vector3(0, 2.1f, 0),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            // Rendered at a high font size for crispness, then scaled down small in world
            // space via PixelSize so the tag reads as a compact label, not a giant banner.
            FontSize = 64,
            PixelSize = 0.0016f,
            OutlineSize = 12,
            OutlineModulate = new Color(0, 0, 0, 0.7f),
        };
        AddChild(_nameTag);

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
        _hasRealAvatar = isReal;
        _streamingBones = false; // re-decide against the new skeleton
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
    }

    public void ApplyPose(PoseFrame f)
    {
        var (pos, rot) = AvatarPose.ToTransform(f);
        _targetPos = pos;
        _targetRot = rot;
        if (!_hasTarget) { Position = pos; _hasTarget = true; } // snap on first frame

        // Replay the sender's actual rig — but only onto the peer's OWN resolved avatar, since
        // bone rotations are meaningless on a different skeleton. While the peer is still on the
        // shared default outfit, animate them procedurally from observed motion instead.
        if (_hasRealAvatar && f.Bones != null && f.Bones.Count > 0 && !AllIdentity(f.Bones))
        {
            _streamingBones = true;
            _avatar?.ApplyBonePose(f.Bones);
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
        if (!_hasTarget) return;
        // Critically-damped-ish lerp; good enough for M1, replaced by proper snapshot
        // interpolation with a timestamp buffer in M2.
        var prev = Position;
        float t = (float)Mathf.Min(1.0, delta * 12.0);
        Position = Position.Lerp(_targetPos, t);
        Quaternion current = Quaternion;
        Quaternion = current.Slerp(_targetRot, t).Normalized();

        // Only guess an animation from observed motion when the peer isn't streaming bones;
        // otherwise the local procedural cycle would fight the pose we just applied.
        if (_streamingBones) return;
        float speed = delta > 0 ? (new Vector2(Position.X, Position.Z) - new Vector2(prev.X, prev.Z)).Length() / (float)delta : 0f;
        _avatar?.Animate(delta, speed, true);
    }

    /// Stable per-peer colour so avatars are visually distinguishable without textures.
    private static Color ColorFromId(uint id)
    {
        float hue = (id * 0.61803398875f) % 1.0f; // golden-ratio hue spacing
        return Color.FromHsv(hue, 0.6f, 0.9f);
    }
}
