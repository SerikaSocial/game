using System;
using System.Threading.Tasks;
using Godot;
using SerikaSocial.Events;
namespace SerikaSocial.UI;

public partial class EventBanner : VBoxContainer
{
    public event Action<string> JoinRequested;
    private string _signature = "";
    private bool _suppressed;
    /// Set while the player is already standing in this event's venue. Offering them "Join
    /// event" there is at best noise and at worst a second join, so the banner gives its slot
    /// up to the in-event options instead. Kept as state rather than a plain `Visible` write
    /// because `SetEvents` re-decides visibility on every poll and would undo that.
    public bool Suppressed
    {
        get => _suppressed;
        set { _suppressed = value; if (value) Visible = false; else Visible = GetChildCount() > 0; }
    }
    public void SetEvents(LiveEvent[] events, Func<string,Task<byte[]>> load)
    {
        string signature = string.Join("|", System.Array.ConvertAll(events, e => e.Id + e.Status + e.BannerUrl));
        if (signature == _signature) return; _signature = signature;
        foreach (Node child in GetChildren()) { RemoveChild(child); child.QueueFree(); }
        Visible = events.Length > 0 && !_suppressed;
        foreach (var item in events) {
            var button = Brand.Ghost_(new Button { Name = "JoinEvent", CustomMinimumSize = new Vector2(0, 88), TooltipText = "Join " + item.Title });
            var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            button.AddChild(row); row.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, Control.LayoutPresetMode.Minsize, 8);
            var image = new TextureRect { CustomMinimumSize = new Vector2(160, 72), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, MouseFilter = Control.MouseFilterEnum.Ignore };
            row.AddChild(image);
            row.AddChild(new Label { Text = item.Title + "\n" + (item.Status == "live" ? "Show live · Join event" : "Doors open · Join event"),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore, AutowrapMode = TextServer.AutowrapMode.WordSmart });
            button.Pressed += () => JoinRequested?.Invoke(item.Id); AddChild(button); _ = LoadBanner(image, item.BannerUrl, load);
        }
    }
    private static async Task LoadBanner(TextureRect target, string url, Func<string,Task<byte[]>> load)
    {
        try { var bytes = await load(url); if (!IsInstanceValid(target) || bytes == null) return;
            var image = new Image(); if (image.LoadPngFromBuffer(bytes) == Error.Ok) target.Texture = ImageTexture.CreateFromImage(image);
        } catch (Exception e) { GD.PrintErr("Event banner: " + e.Message); }
    }
}
