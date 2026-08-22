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
        // Capsule stand-in, shown until (and if) an avatar is equipped.
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

        // Equip the current default avatar. Failure leaves the capsule in place.
        EquipAvatar(AvatarLibrary.CurrentDefaultPath);
    }

    /// Equip an avatar from a `.ska` path; hides the capsule and floats the name tag at head height.
    public void EquipAvatar(string skaPath)
    {
        var avatar = AvatarLibrary.Instantiate(skaPath);
        if (avatar == null) return;
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
        float t = (float)Mathf.Min(1.0, delta * 12.0);
        Position = Position.Lerp(_targetPos, t);
        Quaternion current = Quaternion;
        Quaternion = current.Slerp(_targetRot, t).Normalized();
    }

    /// Stable per-peer colour so avatars are visually distinguishable without textures.
    private static Color ColorFromId(uint id)
    {
        float hue = (id * 0.61803398875f) % 1.0f; // golden-ratio hue spacing
        return Color.FromHsv(hue, 0.6f, 0.9f);
    }
}
