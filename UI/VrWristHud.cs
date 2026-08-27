using Godot;

namespace SerikaSocial.UI;

/// A small panel on the back of the left wrist that carries the in-world HUD — world name,
/// player count, toasts — the way a watch carries the time.
///
/// **Why this exists at all.** Every screen in this project is a `CanvasLayer`, and in VR
/// `Main.AddUi` routes those onto `VrUiSurface`, the floating menu panel. `InWorldHud` is visible
/// for as long as the player is in a world, so mounting it there meant the menu panel was never
/// idle: a two-metre slab hung permanently in front of the player's face showing a mic icon and a
/// world label, and because the pointer was gated on "is the panel up", a laser pointer was lit
/// permanently alongside it. Neither was clickable. Moving persistent chrome onto the wrist fixes
/// the cause rather than the symptom — the menu panel now appears only for actual menus, which is
/// what makes `VrUiSurface.HasInteractiveUi` a meaningful signal.
///
/// **It hides until you look at it.** A HUD welded into your view is the thing VR players
/// complain about most, and information you cannot dismiss is worse in a headset than on a
/// monitor because you cannot look away from it. The panel fades in only when the wrist is turned
/// toward the face, which is the gesture people already make to check a watch.
public partial class VrWristHud : Node3D
{
    /// Backing resolution and the logical size the `Control`s lay out against, same split as
    /// `VrUiSurface` — render big enough to stay legible, lay out small enough that a HUD written
    /// for a 1920-px monitor is not a row of ants on a 14 cm panel.
    private static readonly Vector2I Resolution = new(768, 480);
    private static readonly Vector2I Logical = new(384, 240);

    private const float PanelWidth = 0.15f; // metres, about the width of a wristwatch face
    /// How squarely the wrist must face the player before the panel appears. cos(50°) — generous
    /// enough that a casual glance works, tight enough that it stays dark while your arm hangs.
    private const float FaceThreshold = 0.64f;
    private const float FadeRate = 9f;

    public SubViewport Viewport { get; private set; }
    private MeshInstance3D _panel;
    private StandardMaterial3D _material;
    private float _opacity;

    /// The camera the panel decides "is the player looking at this?" against. Set by `Main`; with
    /// no camera the panel stays hidden rather than hanging visible in the world.
    public Camera3D HeadCamera { get; set; }

    public override void _Ready()
    {
        Viewport = new SubViewport
        {
            Name = "WristViewport",
            Size = Resolution,
            Size2DOverride = Logical,
            Size2DOverrideStretch = true,
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            UseXR = false,
            // Nothing here is clickable — it is a readout. Refusing input also means the wrist
            // panel can never steal a synthetic click aimed at the menu panel.
            GuiDisableInput = true,
        };
        AddChild(Viewport);

        float aspect = (float)Logical.Y / Logical.X;
        _material = new StandardMaterial3D
        {
            AlbedoTexture = Viewport.GetTexture(),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            RenderPriority = 90,
        };

        _panel = new MeshInstance3D
        {
            Name = "WristPanel",
            Mesh = new QuadMesh { Size = new Vector2(PanelWidth, PanelWidth * aspect) },
            MaterialOverride = _material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_panel);

        // Sit above and slightly behind the controller's origin — roughly where the back of the
        // wrist is when the controller is held — and tip the face up toward the player's eyes.
        Position = new Vector3(0.0f, 0.035f, 0.055f);
        RotationDegrees = new Vector3(-60f, 0f, 0f);
    }

    public override void _Process(double delta)
    {
        if (_panel == null) return;

        float target = ShouldShow() ? 1f : 0f;
        _opacity = Mathf.Lerp(_opacity, target, 1f - Mathf.Exp(-FadeRate * (float)delta));

        bool visible = _opacity > 0.01f;
        _panel.Visible = visible;
        if (visible) _material.AlbedoColor = new Color(1, 1, 1, _opacity);

        // A panel nobody can see does not need re-rendering. This is the same tax `VrUiSurface`
        // pays and the same reason to stop paying it: a mobile GPU has no spare render targets.
        Viewport.RenderTargetUpdateMode = visible
            ? SubViewport.UpdateMode.Always
            : SubViewport.UpdateMode.Disabled;
    }

    /// Visible only when the panel is turned toward the player's eyes and the setting is on.
    private bool ShouldShow()
    {
        if (!DeviceProfile.Settings.VrWristHud) return false;
        if (HeadCamera == null || !GodotObject.IsInstanceValid(HeadCamera)) return false;
        if (!IsInsideTree()) return false;

        var toCam = HeadCamera.GlobalPosition - GlobalPosition;
        if (toCam.LengthSquared() < 1e-6f) return false;

        // A QuadMesh faces +Z, so the panel's own +Z is its normal.
        var normal = GlobalTransform.Basis.Z.Normalized();
        return normal.Dot(toCam.Normalized()) > FaceThreshold;
    }
}
