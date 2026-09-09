using Godot;

namespace SerikaSocial.Events;

public partial class EventShowPlayer
{
    private Label3D _thankYou;
    private double _thankYouStart = double.PositiveInfinity;
    public bool ThankYouVisible => IsInstanceValid(_thankYou) && _thankYou.Visible;

    private void SetupEndCard(Node3D world)
    {
        var segments = _state.Config.Segments;
        if (segments is not { Count: > 0 } || segments[^1].Title != "Thank you") return;
        _thankYouStart = segments[^1].Start;
        _thankYou = new Label3D {
            Name = "ConcertThankYou", Text = "THANK YOU\nFOR SHINING WITH US",
            FontSize = 128, PixelSize = .010f, OutlineSize = 12,
            Modulate = new Color(.75f, .9f, 1), OutlineModulate = new Color(.01f, .025f, .07f),
            NoDepthTest = false, Shaded = false, Visible = false,
        };
        world.AddChild(_thankYou);
        var art = world.FindChild("Artist English name", true, false) as Node3D;
        _thankYou.GlobalPosition = art != null ? art.GlobalPosition + new Vector3(0, -3.5f, .25f)
            : ShowTimeline.Vector(_state.Config.Performer) + new Vector3(0, 5, -12);
    }

    private void UpdateEndCard(double seconds, bool performance)
    {
        if (IsInstanceValid(_thankYou)) _thankYou.Visible = performance && seconds >= _thankYouStart;
    }

    private void RestoreEndCard()
    {
        if (IsInstanceValid(_thankYou)) _thankYou.QueueFree();
        _thankYou = null;
        _thankYouStart = double.PositiveInfinity;
    }
}
