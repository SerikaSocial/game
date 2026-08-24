using Godot;

namespace SerikaSocial.Player;

/// A floating name tag with an optional round profile picture, billboarded above an avatar's
/// head. Used by both LocalPlayer (shown in third person / mirror) and RemoteAvatar.
///
/// Before this, the tag was a bare `Label3D`. The design asked for a profile picture on the
/// card and for the local player's head to show in mirrors and shadows — the pfp chip lives
/// here so both rigs render an identical card, and visibility of the name and the picture can
/// be toggled independently from settings.
public partial class NameTag3D : Node3D
{
    private Label3D _label;
    private Sprite3D _pfp;
    private bool _wantTags = true;
    private bool _wantPfp = true;
    private bool _hasPfp;

    public override void _Ready()
    {
        _label = new Label3D
        {
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            FontSize = 64,
            PixelSize = 0.0016f,
            OutlineSize = 12,
            OutlineModulate = new Color(0, 0, 0, 0.7f),
            // Draw on top so a name isn't lost behind hair/geometry, but keep it from z-fighting.
            NoDepthTest = false,
        };
        AddChild(_label);

        _pfp = new Sprite3D
        {
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            PixelSize = 0.0016f,
            // Sits just left of the name; hidden until a picture arrives.
            Position = new Vector3(-0.16f, 0.02f, 0),
            Visible = false,
        };
        AddChild(_pfp);

        Reflow();
    }

    public void SetLabel(string name)
    {
        if (_label != null) _label.Text = name;
    }

    /// Apply a downloaded profile picture (jpg/png/webp bytes). A null/undecodable buffer hides
    /// the chip rather than showing a broken quad.
    public void SetProfilePicture(byte[] bytes)
    {
        if (_pfp == null) return;
        if (bytes == null || bytes.Length == 0) { _hasPfp = false; Reflow(); return; }

        var img = new Image();
        if (img.LoadJpgFromBuffer(bytes) != Error.Ok
            && img.LoadPngFromBuffer(bytes) != Error.Ok
            && img.LoadWebpFromBuffer(bytes) != Error.Ok)
        {
            _hasPfp = false; Reflow(); return;
        }
        // Square-crop to a small chip so wide/tall avatars aren't stretched on the card.
        int side = Mathf.Min(img.GetWidth(), img.GetHeight());
        if (side > 0)
        {
            img.Crop(side, side);
            img.Resize(96, 96, Image.Interpolation.Bilinear);
        }
        _pfp.Texture = ImageTexture.CreateFromImage(img);
        _hasPfp = true;
        Reflow();
    }

    /// Independently toggle the name and the picture (from settings).
    public void SetPrefs(bool showTags, bool showPfp)
    {
        _wantTags = showTags;
        _wantPfp = showPfp;
        Reflow();
    }

    /// Hide/show the whole tag (e.g. first-person hides your own).
    public void SetShown(bool shown)
    {
        Visible = shown && _wantTags;
    }

    private void Reflow()
    {
        if (_label == null) return;
        Visible = _wantTags;
        bool pfpVisible = _wantTags && _wantPfp && _hasPfp;
        _pfp.Visible = pfpVisible;
        // Nudge the name right when a chip is present so they don't overlap.
        _label.Position = new Vector3(pfpVisible ? 0.10f : 0f, 0, 0);
    }
}
