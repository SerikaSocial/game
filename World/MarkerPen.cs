using Godot;

namespace SerikaSocial.World;

/// A marker pen: a physics prop you can pick up and draw with.
///
/// Extends <see cref="PhysicsProp"/> so it inherits grab/release/sync, and adds
/// <see cref="IHoldable"/> (snap-to-hand at a fixed offset) and <see cref="IUsable"/>
/// (trigger/click = draw on the <see cref="StrokeCanvas"/>).
///
/// On VR: grip to pick up, trigger to draw. On desktop: E to pick up, left-click to draw.
/// On touch: interact button to pick up, interact button again to draw (the controller
/// detects the IUsable and routes the press to UseBegin instead of dropping).
[GlobalClass]
public partial class MarkerPen : PhysicsProp, IHoldable, IUsable
{
    [Export] public float PenLength { get; set; } = 0.12f;
    [Export] public float PenRadius { get; set; } = 0.006f;
    [Export] public float DrawRadius { get; set; } = 0.008f;
    [Export] public float DrawHue { get; set; } = 0.78f; // purple-ish

    /// Where the tip is relative to the pen origin (pen is oriented along +Y, tip at top).
    private float TipOffset => PenLength * 0.5f;

    /// Grip offset: pen held pointing forward from the hand (-Z), tip leading.
    public Transform3D HoldOffset => new Transform3D(
        Basis.FromEuler(new Vector3(Mathf.DegToRad(-90f), 0, 0)),
        new Vector3(0, 0, -0.08f));

    public string UseVerb => "Draw";
    public bool ContinuousUse => true;

    private StrokeCanvas _canvas;
    private ushort _activeStrokeId;

    public override void _Ready()
    {
        PropName = "Marker";
        InteractionRange = 2.0f;
        base._Ready();

        // Visual: a slim cylinder for the pen body.
        var mesh = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = PenRadius, BottomRadius = PenRadius, Height = PenLength },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = Color.FromHsv(DrawHue, 0.7f, 0.9f),
                Roughness = 0.4f,
            },
        };
        AddChild(mesh);

        // Tip marker: a small sphere at the nib so you can see where you're drawing.
        var tip = new MeshInstance3D
        {
            Name = "Tip",
            Mesh = new SphereMesh { Radius = DrawRadius, Height = DrawRadius * 2 },
            Position = new Vector3(0, TipOffset, 0),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = Color.FromHsv(DrawHue, 0.9f, 1f),
                EmissionEnabled = true,
                Emission = Color.FromHsv(DrawHue, 0.8f, 0.5f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
        };
        AddChild(tip);
    }

    /// Set the canvas this pen draws on. Called by Main.cs when the world is loaded.
    public void SetCanvas(StrokeCanvas canvas) => _canvas = canvas;

    public override void Interact(in InteractionContext ctx)
    {
        // Same grab/drop toggle as PhysicsProp, but equip/unequip hooks fire for IHoldable.
        if (_held && _heldByLocal)
        {
            OnUnequip();
            Release();
            return;
        }
        if (_held) return;
        GrabAt(ctx.HandPosition);
        OnEquip(in ctx);
    }

    public void OnEquip(in InteractionContext ctx)
    {
        // PhysicsProp.GrabAt already froze the body and set _held. Nothing extra needed here
        // beyond marking the prop name for the prompt.
    }

    public void OnUnequip()
    {
        // If mid-stroke when dropped, close it out.
        if (_activeStrokeId != 0 && _canvas != null)
        {
            _canvas.EndStroke(_activeStrokeId);
            _activeStrokeId = 0;
        }
    }

    // ── IUsable ────────────────────────────────────────────────────────────────────────

    public void UseBegin(in UseContext ctx)
    {
        if (_canvas == null) return;
        var tip = TipWorldPosition();
        _activeStrokeId = _canvas.BeginStroke(tip, DrawHue, DrawRadius);
    }

    public void UseTick(in UseContext ctx, float dt)
    {
        if (_canvas == null || _activeStrokeId == 0) return;
        _canvas.ContinueStroke(_activeStrokeId, TipWorldPosition());
    }

    public void UseEnd(in UseContext ctx)
    {
        if (_canvas == null || _activeStrokeId == 0) return;
        _canvas.EndStroke(_activeStrokeId);
        _activeStrokeId = 0;
    }

    private Vector3 TipWorldPosition() => GlobalPosition + GlobalTransform.Basis.Y * TipOffset;
}
