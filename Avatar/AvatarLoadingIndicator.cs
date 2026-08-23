using Godot;

namespace SerikaSocial.Avatar;

/// A small spinning ring that hovers where an avatar is about to appear, shown while its
/// `.ska` downloads and imports. Avatar files run to tens of megabytes, so without this the
/// swap looks like a freeze — the old model just stands there until the new one pops in.
///
/// Drawn as a torus with an unshaded brand-coloured material so it reads at any distance and
/// is never affected by world lighting.
public sealed partial class AvatarLoadingIndicator : Node3D
{
    private MeshInstance3D _ring;
    private Label3D _label;
    private float _spin;

    public static AvatarLoadingIndicator Create(float height, string text = "Loading avatar…")
    {
        var n = new AvatarLoadingIndicator { Name = "AvatarLoading" };
        n.Build(height, text);
        return n;
    }

    private void Build(float height, string text)
    {
        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.62f, 0.42f, 0.98f),      // brand purple (hue 262)
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        };

        _ring = new MeshInstance3D
        {
            Mesh = new TorusMesh { InnerRadius = 0.16f, OuterRadius = 0.22f, RingSegments = 6 },
            MaterialOverride = mat,
            Position = new Vector3(0, height + 0.35f, 0),
        };
        AddChild(_ring);

        _label = new Label3D
        {
            Text = text,
            Position = new Vector3(0, height + 0.68f, 0),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            FontSize = 64,
            PixelSize = 0.0014f,
            OutlineSize = 12,
            OutlineModulate = new Color(0, 0, 0, 0.7f),
        };
        AddChild(_label);
    }

    public override void _Process(double delta)
    {
        _spin += (float)delta * 3.2f;
        if (_ring != null) _ring.Rotation = new Vector3(Mathf.Pi / 2f, _spin, 0);
    }
}
