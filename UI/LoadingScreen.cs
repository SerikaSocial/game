using Godot;

namespace SerikaSocial;

/// A polished, full-screen loading overlay shown while signing in and connecting to a world.
///
/// It's a real 3D scene, not a spinner GIF: a floating faceted crystal (Serika's mark) turns and
/// bobs in an isolated SubViewport over a violet gradient, with an indeterminate progress bar and
/// rotating tips. Purely presentational — Main calls Show / SetStatus / HideWithFade.
public partial class LoadingScreen : CanvasLayer
{
    private static readonly string[] Tips =
    {
        "Tip: press V to switch between first and third person.",
        "Tip: press T to chat — everyone in the world sees it.",
        "Tip: walk into a glowing portal to travel between worlds.",
        "Tip: there's a mirror at home — check out your avatar.",
        "Tip: upload your own VRM avatar on the website.",
        "Serika Social — social VR for everyone.",
    };

    private Control _root;
    private Node3D _crystal;
    private Node3D _ring;
    private Node3D _orbits;
    private Label _status;
    private Label _tip;
    private Control _bar;
    private float _phase;
    private double _tipTimer;
    private int _tipIndex;
    private float _fade = 1f;
    private bool _hiding;

    public override void _Ready()
    {
        Layer = 105; // above the login/home HUD (100)
        BuildUi();
        Build3D();
        _status.Text = "Loading…";
        _tip.Text = Tips[0];
    }

    public void SetStatus(string text)
    {
        if (_status != null) _status.Text = text;
    }

    public void Present()
    {
        _hiding = false;
        _fade = 1f;
        Visible = true;
        if (_root != null) _root.Modulate = new Color(1, 1, 1, 1);
    }

    /// Fade out over ~0.35s, then hide.
    public void HideWithFade() => _hiding = true;

    private void BuildUi()
    {
        _root = new Control { AnchorRight = 1, AnchorBottom = 1, MouseFilter = Control.MouseFilterEnum.Stop };
        AddChild(_root);

        // Violet gradient backdrop.
        var grad = new GradientTexture2D
        {
            Gradient = Brand.BackdropGradient(),
            Fill = GradientTexture2D.FillEnum.Linear,
            FillFrom = new Vector2(0.5f, 0f),
            FillTo = new Vector2(0.5f, 1f),
            Width = 8, Height = 256,
        };
        var bg = new TextureRect
        {
            Texture = grad,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            AnchorRight = 1, AnchorBottom = 1,
        };
        _root.AddChild(bg);

        // Centre column.
        var col = new VBoxContainer
        {
            AnchorLeft = 0.5f, AnchorTop = 0.5f, AnchorRight = 0.5f, AnchorBottom = 0.5f,
            OffsetLeft = -260, OffsetRight = 260, OffsetTop = -220, OffsetBottom = 220,
            Alignment = BoxContainer.AlignmentMode.Center,
        };
        col.AddThemeConstantOverride("separation", 10);
        _root.AddChild(col);

        // 3D crystal viewport, shown as a texture.
        var vp = new SubViewport
        {
            Size = new Vector2I(340, 340),
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
            OwnWorld3D = true,
        };
        _viewport = vp;
        var vpc = new SubViewportContainer { Stretch = false, CustomMinimumSize = new Vector2(340, 340) };
        vpc.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        vpc.AddChild(vp);
        col.AddChild(vpc);

        var title = new Label { Text = "Serika Social", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 30);
        title.AddThemeColorOverride("font_color", Brand.TextHi);
        col.AddChild(title);

        _status = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _status.AddThemeFontSizeOverride("font_size", 15);
        _status.AddThemeColorOverride("font_color", Brand.AccentSoft);
        col.AddChild(_status);

        // Indeterminate bar (custom-drawn moving highlight).
        _bar = new Control { CustomMinimumSize = new Vector2(320, 6) };
        _bar.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        _bar.Draw += () => DrawBar(_bar);
        col.AddChild(_bar);

        col.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });

        _tip = new Label { HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _tip.AddThemeFontSizeOverride("font_size", 13);
        _tip.AddThemeColorOverride("font_color", Brand.TextDim);
        col.AddChild(_tip);
    }

    private SubViewport _viewport;

    private void Build3D()
    {
        var cam = new Camera3D { Position = new Vector3(0, 0, 3.2f) };
        _viewport.AddChild(cam);

        _viewport.AddChild(new OmniLight3D { Position = new Vector3(2, 2, 3), LightColor = Brand.Accent, LightEnergy = 2.5f, OmniRange = 12 });
        _viewport.AddChild(new OmniLight3D { Position = new Vector3(-3, -1, 2), LightColor = Brand.Primary, LightEnergy = 2.0f, OmniRange = 12 });

        // Faceted crystal = two cones base-to-base (a bipyramid), emissive violet.
        _crystal = new Node3D();
        _viewport.AddChild(_crystal);
        var mat = new StandardMaterial3D
        {
            AlbedoColor = Brand.Primary,
            Metallic = 0.6f, Roughness = 0.15f,
            Emission = Brand.PrimaryHi, EmissionEnergyMultiplier = 0.7f,
        };
        var top = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.6f, Height = 0.9f, RadialSegments = 6 },
            Position = new Vector3(0, 0.45f, 0),
        };
        top.MaterialOverride = mat;
        _crystal.AddChild(top);
        var bottom = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.6f, Height = 0.9f, RadialSegments = 6 },
            Position = new Vector3(0, -0.45f, 0),
            RotationDegrees = new Vector3(180, 0, 0),
        };
        bottom.MaterialOverride = mat;
        _crystal.AddChild(bottom);

        // A thin orbiting ring.
        _ring = new Node3D { RotationDegrees = new Vector3(70, 0, 0) };
        _viewport.AddChild(_ring);
        var ringMesh = new MeshInstance3D
        {
            Mesh = new TorusMesh { InnerRadius = 1.05f, OuterRadius = 1.15f },
        };
        ringMesh.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = Brand.Accent, Emission = Brand.Accent, EmissionEnergyMultiplier = 1.2f,
            Metallic = 0.4f, Roughness = 0.3f,
        };
        _ring.AddChild(ringMesh);

        // Two small orbiting spheres.
        _orbits = new Node3D();
        _viewport.AddChild(_orbits);
        for (int i = 0; i < 2; i++)
        {
            var orb = new Node3D { RotationDegrees = new Vector3(0, i * 180, 0) };
            var s = new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = 0.09f, Height = 0.18f },
                Position = new Vector3(1.35f, 0, 0),
            };
            s.MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = Brand.AccentSoft, Emission = Brand.AccentSoft, EmissionEnergyMultiplier = 2f,
            };
            orb.AddChild(s);
            _orbits.AddChild(orb);
        }
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _phase += dt;

        if (_crystal != null)
        {
            _crystal.Rotation = new Vector3(0, _phase * 0.9f, 0);
            _crystal.Position = new Vector3(0, Mathf.Sin(_phase * 1.6f) * 0.08f, 0);
            _ring.Rotation = new Vector3(Mathf.DegToRad(70), _phase * 1.5f, 0);
            _orbits.Rotation = new Vector3(0, -_phase * 1.2f, 0);
        }

        _bar?.QueueRedraw();

        // Rotate tips.
        _tipTimer += delta;
        if (_tipTimer >= 3.8)
        {
            _tipTimer = 0;
            _tipIndex = (_tipIndex + 1) % Tips.Length;
            if (_tip != null) _tip.Text = Tips[_tipIndex];
        }

        // Fade out and hide when requested.
        if (_hiding)
        {
            _fade = Mathf.Max(0f, _fade - dt / 0.35f);
            if (_root != null) _root.Modulate = new Color(1, 1, 1, _fade);
            if (_fade <= 0f) { Visible = false; _hiding = false; }
        }
    }

    private void DrawBar(Control c)
    {
        var size = c.Size;
        // Track.
        DrawRoundedRect(c, new Rect2(0, 0, size.X, size.Y), new Color(Brand.Bg0.R, Brand.Bg0.G, Brand.Bg0.B, 0.9f));
        // Moving highlight (indeterminate).
        float w = size.X * 0.32f;
        float travel = size.X + w;
        float x = ((_phase * 0.55f) % 1f) * travel - w;
        var seg = new Rect2(Mathf.Max(0, x), 0, Mathf.Min(w, size.X - Mathf.Max(0, x)), size.Y);
        if (seg.Size.X > 0) DrawRoundedRect(c, seg, Brand.Primary);
    }

    private static void DrawRoundedRect(Control c, Rect2 r, Color color)
    {
        // Simple filled rect with rounded caps approximated by radius = height/2.
        float rad = r.Size.Y * 0.5f;
        var style = new StyleBoxFlat
        {
            BgColor = color,
            CornerRadiusTopLeft = (int)rad, CornerRadiusTopRight = (int)rad,
            CornerRadiusBottomLeft = (int)rad, CornerRadiusBottomRight = (int)rad,
        };
        style.Draw(c.GetCanvasItem(), r);
    }
}
