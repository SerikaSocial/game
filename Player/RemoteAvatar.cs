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
            FontSize = 48,
            PixelSize = 0.005f,
        };
        AddChild(_nameTag);

        // Equip the current default avatar. Failure falls back to the bean so nobody is a capsule.
        EquipAvatar(AvatarLibrary.CurrentDefaultPath);
    }

    /// Equip an avatar from a `.ska` path; hides the capsule and floats the name tag at head height.
    /// If the path is null or fails to load, the bean fallback is used instead.
    public void EquipAvatar(string skaPath)
    {
        var avatar = AvatarLibrary.InstantiateOrDefault(skaPath);
        _avatar?.QueueFree();
        _avatar = avatar;
        AddChild(avatar);
        _capsule.Visible = false;
        _nameTag.Position = new Vector3(0, avatar.Height + 0.25f, 0);
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

        // Feed the procedural walk/idle cycle with the observed planar speed.
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
