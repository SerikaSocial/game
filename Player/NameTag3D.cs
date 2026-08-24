using System;
using Godot;

namespace SerikaSocial.Player;

/// A floating billboarded nameplate rendered via SubViewport to match the Serika card design:
/// - Rounded card with custom orange/red border (dim in default state, bright neon in focused state)
/// - Left: Circular profile picture with a crisp white border ring
/// - Right: Username (muted light slate in default state, bright solid white in focused state)
public partial class NameTag3D : Node3D
{
    private SubViewport _subViewport;
    private Sprite3D _sprite;
    private PanelContainer _card;
    private StyleBoxFlat _panelStyle;
    private TextureRect _pfpRect;
    private Label _nameLabel;

    private bool _wantTags = true;
    private bool _wantPfp = true;
    private bool _hasPfp;
    private bool _focused;
    private string _currentName = "";

    private static readonly Color DefaultBorderColor = new(0.72f, 0.28f, 0.16f, 0.85f);
    private static readonly Color FocusedBorderColor = new(1.0f, 0.38f, 0.14f, 1.0f);
    private static readonly Color DefaultTextColor = new(0.68f, 0.74f, 0.82f, 0.80f);
    private static readonly Color FocusedTextColor = new(1.0f, 1.0f, 1.0f, 1.0f);

    public override void _Ready()
    {
        // SubViewport for crisp off-screen 2D UI rendering
        _subViewport = new SubViewport
        {
            Name = "NameTagViewport",
            Size = new Vector2I(420, 116),
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        AddChild(_subViewport);

        var rootControl = new Control
        {
            CustomMinimumSize = new Vector2(420, 116),
            Size = new Vector2(420, 116),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _subViewport.AddChild(rootControl);

        _panelStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.07f, 0.09f, 0.12f, 0.90f),
            CornerRadiusTopLeft = 20,
            CornerRadiusTopRight = 20,
            CornerRadiusBottomLeft = 20,
            CornerRadiusBottomRight = 20,
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            BorderColor = DefaultBorderColor,
            AntiAliasing = true,
            AntiAliasingSize = 2.0f,
        };

        _card = new PanelContainer
        {
            CustomMinimumSize = new Vector2(420, 116),
            Size = new Vector2(420, 116),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _card.AddThemeStyleboxOverride("panel", _panelStyle);
        rootControl.AddChild(_card);

        var margin = new MarginContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        margin.AddThemeConstantOverride("margin_left", 16);
        margin.AddThemeConstantOverride("margin_right", 24);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_bottom", 10);
        _card.AddChild(margin);

        var hbox = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Begin,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        hbox.AddThemeConstantOverride("separation", 18);
        margin.AddChild(hbox);

        // Circular profile picture
        _pfpRect = new TextureRect
        {
            CustomMinimumSize = new Vector2(82, 82),
            Size = new Vector2(82, 82),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        hbox.AddChild(_pfpRect);

        // Username text
        _nameLabel = new Label
        {
            Text = _currentName,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            AutowrapMode = TextServer.AutowrapMode.Off,
            ClipText = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _nameLabel.AddThemeFontSizeOverride("font_size", 34);
        _nameLabel.AddThemeColorOverride("font_color", DefaultTextColor);
        hbox.AddChild(_nameLabel);

        // Billboard Sprite3D mapping the SubViewport texture
        _sprite = new Sprite3D
        {
            Name = "NameTagSprite",
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            PixelSize = 0.0017f,
            NoDepthTest = false,
            AlphaCut = Sprite3D.AlphaCutMode.OpaquePrepass,
            DoubleSided = true,
            Texture = _subViewport.GetTexture(),
        };
        AddChild(_sprite);

        ApplyDefaultAvatar();
        ApplyFocusStyle();
    }

    public void SetLabel(string name)
    {
        _currentName = name ?? "";
        if (_nameLabel != null) _nameLabel.Text = _currentName;
        if (!_hasPfp) ApplyDefaultAvatar();
    }

    public void SetFocused(bool focused)
    {
        if (_focused == focused) return;
        _focused = focused;
        ApplyFocusStyle();
    }

    private void ApplyFocusStyle()
    {
        if (_panelStyle == null || _nameLabel == null) return;

        if (_focused)
        {
            _panelStyle.BorderColor = FocusedBorderColor;
            _panelStyle.BorderWidthLeft = 4;
            _panelStyle.BorderWidthTop = 4;
            _panelStyle.BorderWidthRight = 4;
            _panelStyle.BorderWidthBottom = 4;
            _panelStyle.BgColor = new Color(0.09f, 0.11f, 0.15f, 0.94f);
            _nameLabel.AddThemeColorOverride("font_color", FocusedTextColor);
        }
        else
        {
            _panelStyle.BorderColor = DefaultBorderColor;
            _panelStyle.BorderWidthLeft = 2;
            _panelStyle.BorderWidthTop = 2;
            _panelStyle.BorderWidthRight = 2;
            _panelStyle.BorderWidthBottom = 2;
            _panelStyle.BgColor = new Color(0.07f, 0.09f, 0.12f, 0.90f);
            _nameLabel.AddThemeColorOverride("font_color", DefaultTextColor);
        }
    }

    /// Apply a downloaded profile picture (jpg/png/webp bytes). A null buffer applies default avatar.
    public void SetProfilePicture(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 4)
        {
            _hasPfp = false;
            ApplyDefaultAvatar();
            return;
        }

        var img = LoadImageSafely(bytes);
        if (img == null)
        {
            _hasPfp = false;
            ApplyDefaultAvatar();
            return;
        }

        var circular = MakeCircularAvatarWithRing(img, 128);
        if (_pfpRect != null)
        {
            _pfpRect.Texture = ImageTexture.CreateFromImage(circular);
            _hasPfp = true;
            _pfpRect.Visible = _wantPfp;
        }
    }

    /// Independently toggle the name and the picture (from settings).
    public void SetPrefs(bool showTags, bool showPfp)
    {
        _wantTags = showTags;
        _wantPfp = showPfp;
        if (_pfpRect != null) _pfpRect.Visible = _wantPfp;
        Visible = _wantTags;
    }

    /// Hide/show the whole tag (e.g. first-person hides your own).
    public void SetShown(bool shown)
    {
        Visible = shown && _wantTags;
    }

    private void ApplyDefaultAvatar()
    {
        if (_pfpRect == null) return;
        var defaultImg = CreateDefaultAvatarImage(128, _currentName);
        var circular = MakeCircularAvatarWithRing(defaultImg, 128);
        _pfpRect.Texture = ImageTexture.CreateFromImage(circular);
        _pfpRect.Visible = _wantPfp;
    }

    private static Image LoadImageSafely(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 4) return null;
        var img = new Image();
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            if (img.LoadJpgFromBuffer(bytes) == Error.Ok) return img;
        }
        else if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            if (img.LoadPngFromBuffer(bytes) == Error.Ok) return img;
        }
        else if (bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
                 && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        {
            if (img.LoadWebpFromBuffer(bytes) == Error.Ok) return img;
        }
        else
        {
            if (img.LoadPngFromBuffer(bytes) == Error.Ok) return img;
            if (img.LoadJpgFromBuffer(bytes) == Error.Ok) return img;
            if (img.LoadWebpFromBuffer(bytes) == Error.Ok) return img;
        }
        return null;
    }

    private static Image CreateDefaultAvatarImage(int size, string seedName = "")
    {
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        float hue = string.IsNullOrEmpty(seedName) ? 0.75f : ((Mathf.Abs(seedName.GetHashCode()) % 100) / 100f);
        Color baseCol = Color.FromHsv(hue, 0.65f, 0.45f);
        Color lightCol = Color.FromHsv(hue, 0.50f, 0.70f);

        for (int y = 0; y < size; y++)
        {
            float t = (float)y / size;
            Color rowCol = baseCol.Lerp(lightCol, 1.0f - t);
            for (int x = 0; x < size; x++)
            {
                img.SetPixel(x, y, rowCol);
            }
        }
        return img;
    }

    private static Image MakeCircularAvatarWithRing(Image src, int size = 128)
    {
        var dst = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        if (src == null || src.IsEmpty())
        {
            src = CreateDefaultAvatarImage(size);
        }
        else
        {
            src = (Image)src.Duplicate();
            int side = Mathf.Min(src.GetWidth(), src.GetHeight());
            if (side > 0)
            {
                int srcX = (src.GetWidth() - side) / 2;
                int srcY = (src.GetHeight() - side) / 2;
                var cropped = Image.CreateEmpty(side, side, false, Image.Format.Rgba8);
                cropped.BlitRect(src, new Rect2I(srcX, srcY, side, side), Vector2I.Zero);
                cropped.Resize(size, size, Image.Interpolation.Bilinear);
                src = cropped;
            }
        }

        float center = size / 2.0f;
        float radius = center - 1.5f;
        float ringInner = radius - 3.5f;

        src.Convert(Image.Format.Rgba8);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - center;
                float dy = y + 0.5f - center;
                float d = Mathf.Sqrt(dx * dx + dy * dy);

                if (d > radius + 1f)
                {
                    dst.SetPixel(x, y, new Color(0, 0, 0, 0));
                }
                else if (d > radius)
                {
                    float alpha = 1.0f - (d - radius);
                    dst.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
                else if (d >= ringInner)
                {
                    dst.SetPixel(x, y, new Color(0.96f, 0.96f, 0.98f, 1.0f));
                }
                else if (d >= ringInner - 1f)
                {
                    float t = d - (ringInner - 1f);
                    Color avatarCol = src.GetPixel(x, y);
                    Color ringCol = new Color(0.96f, 0.96f, 0.98f, 1.0f);
                    dst.SetPixel(x, y, avatarCol.Lerp(ringCol, t));
                }
                else
                {
                    dst.SetPixel(x, y, src.GetPixel(x, y));
                }
            }
        }

        return dst;
    }
}
