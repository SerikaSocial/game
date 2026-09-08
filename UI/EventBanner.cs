using System;
using System.Threading.Tasks;
using Godot;
using SerikaSocial.Events;
namespace SerikaSocial.UI;

public partial class EventBanner : VBoxContainer
{
    public event Action<string> JoinRequested;
    private string _signature = "";
    public void SetEvents(LiveEvent[] events, Func<string,Task<byte[]>> load)
    {
        string signature = string.Join("|", System.Array.ConvertAll(events, e => e.Id + e.Status + e.BannerUrl));
        if (signature == _signature) return; _signature = signature;
        foreach (Node child in GetChildren()) { RemoveChild(child); child.QueueFree(); }
        Visible = events.Length > 0;
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
