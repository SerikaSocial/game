using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace SerikaSocial.UI;

/// Render and exercise the shipping menus at desktop, small-window and VR logical sizes.
public partial class NavigationDiagnostic : Node
{
    private int _failures;
    private string _output;
    private readonly List<object> _checks = new();
    public override async void _Ready()
    {
        _output = System.Environment.GetEnvironmentVariable("SERIKA_UI_SHOTS") ?? "/tmp/serika-ui";
        Directory.CreateDirectory(_output);
        GetTree().Root.Theme = Brand.Theme;
        try
        {
            foreach (var (name, size, vr) in new[]
            {
                ("desktop", new Vector2I(1280, 800), false),
                ("small-window", new Vector2I(640, 480), false),
                ("vr-panel", new Vector2I(1000, 650), true),
            }) await Exercise(name, size, vr);
        }
        catch (Exception exception) { Check("diagnostic exception", false, exception.ToString()); }
        File.WriteAllText(Path.Combine(_output, "navigation-validation.json"),
            System.Text.Json.JsonSerializer.Serialize(new { passed = _failures == 0, failures = _failures, checks = _checks },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        GD.Print($"NAVIGATION: {_checks.Count - _failures}/{_checks.Count} checks passed");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
    private async Task Exercise(string name, Vector2I size, bool vr)
    {
        VrUiSurface.Active = vr;
        var viewport = new SubViewport { Size = size, TransparentBg = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        AddChild(viewport);
        var menu = new MainMenu(); viewport.AddChild(menu);
        menu.SetWorlds(new()
        {
            ("hub", "Serika Social Hub", "A cozy conservatory with an aquarium, quiet corners and places to spend time together.", 32, "pikachubolk", ""),
            ("shrine", "Komorebi Shrine Gardens", "Forest paths, warm lanterns and a peaceful shrine courtyard.", 24, "pikachubolk", ""),
            ("commons", "The Commons", "Meet people in the community's shared social space.", 32, "Serika", ""),
            ("cinema", "Cinema", "Watch videos together on the big screen.", 24, "Serika", ""),
            ("mirror", "Mirror Gallery", "Try a new look and catch up with friends.", 16, "Serika", ""),
            ("home", "Serika Home", "An easy place to settle in.", 16, "Serika", ""),
        });
        menu.SetAvatars(new() { ("avatar", "Hoshimachi Suisei", "Community", "", "") });
        menu.Open("pikachubolk"); await Settle();
        var menuCard = menu.FindChild("CatalogueCard", true, false) as Control;
        Check(name + " catalogue stays inside viewport", Contains(size, menuCard), menuCard.GetGlobalRect().ToString());
        Check(name + " actual catalogue count", Descendants(menu).OfType<Button>().Count(button => button.GetParent() is GridContainer) == 6);
        Check(name + " focus remains visible", viewport.GuiGetFocusOwner() is Button);
        Check(name + " no fake catalogue sections", !Descendants(menu).OfType<Button>().Any(button => new[] { "Live Now", "Shop", "Groups", "Popular Worlds" }.Contains(button.Text)));
        Capture(viewport, name + "-worlds");
        var search = Descendants(menu).OfType<LineEdit>().Single();
        search.Text = "shrine"; search.EmitSignal(LineEdit.SignalName.TextChanged, search.Text); await Settle();
        Check(name + " live search", Descendants(menu).OfType<Button>().Count(button => button.GetParent() is GridContainer) == 1);
        string chosen = null; menu.JoinWorldPressed += id => chosen = id;
        var result = Descendants(menu).OfType<Button>().Single(button => button.GetParent() is GridContainer);
        await Click(viewport, result);
        Check(name + " card opens exact world", chosen == "shrine" && !menu.IsOpen, chosen);
        Check(name + " closed catalogue releases input focus", viewport.GuiGetFocusOwner() == null || !menu.IsAncestorOf(viewport.GuiGetFocusOwner()));
        menu.Open("pikachubolk"); search.Text = "no-matching-world"; search.EmitSignal(LineEdit.SignalName.TextChanged, search.Text); await Settle();
        Check(name + " search empty state", Descendants(menu).OfType<Label>().Any(label => label.IsVisibleInTree() && label.Text == "No matches yet"));
        Capture(viewport, name + "-empty");
        menu.SetWorlds(new()); menu.SetLoadError(1, "Connection unavailable."); search.Text = ""; search.EmitSignal(LineEdit.SignalName.TextChanged, search.Text); await Settle();
        Check(name + " actionable load error", Descendants(menu).OfType<Label>().Any(label => label.IsVisibleInTree() && label.Text.Contains("Use Refresh")));
        Capture(viewport, name + "-error");
        menu.Hide();
        var quick = new QuickMenu(); viewport.AddChild(quick);
        quick.SetLocation("Serika Social Hub", true);
        quick.SetWorldActions(true, false, true, "Private · invite only");
        quick.SetHomeResetAvailable(true);
        quick.SetPlayers("pikachubolk", new List<(string, string)> { ("friend-a", "Mika"), ("friend-b", "Ari") });
        quick.SetTrust("Trusted"); quick.SetMic(false); quick.SetNotificationCount(2);
        quick.Open("pikachubolk"); await Settle();
        var quickCard = quick.FindChild("QuickMenuCard", true, false) as Control;
        Check(name + " quick menu stays inside viewport", Contains(size, quickCard), quickCard.GetGlobalRect().ToString());
        Check(name + " private invite has truthful label", Descendants(quick).OfType<Button>().Any(button => button.Text == "Invite friends"));
        Check(name + " keyboard resume focus", viewport.GuiGetFocusOwner() is Button focus && focus.Text == "Resume");
        // Return scroll to the top for the overview, after testing focus scrolls to Resume.
        var outerScroll = Descendants(quick).OfType<ScrollContainer>().First();
        outerScroll.ScrollVertical = 0; await Settle();
        Capture(viewport, name + "-quick");
        bool setHome = false; quick.SetHomePressed += () => setHome = true;
        await Click(viewport, Descendants(quick).OfType<Button>().Single(button => button.Text == "Set as Home"));
        Check(name + " set Home action", setHome);
        quick.SetWorldActions(true, true, true, "Personal Home · only you", true);
        Check(name + " pending world actions disabled", Descendants(quick).OfType<Button>().Where(button => new[] { "Your Home", "New private instance", "Use default Home" }.Contains(button.Text)).All(button => button.Disabled));
        quick.Hide(); viewport.QueueFree(); await Settle();
    }
    private async Task Click(SubViewport viewport, Control control)
    {
        var visible = control.GetGlobalRect();
        for (Node ancestor = control.GetParent(); ancestor != null; ancestor = ancestor.GetParent())
            if (ancestor is Control { ClipContents: true } clip) visible = visible.Intersection(clip.GetGlobalRect());
        var point = visible.GetCenter();
        viewport.PushInput(new InputEventMouseMotion { Position = point, GlobalPosition = point });
        viewport.PushInput(new InputEventMouseButton { Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left, Pressed = true });
        viewport.PushInput(new InputEventMouseButton { Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left, Pressed = false });
        await Settle();
    }
    private async Task Settle() { for (int frame = 0; frame < 5; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    private void Capture(SubViewport viewport, string name)
    {
        if (DisplayServer.GetName() == "headless") return;
        viewport.GetTexture().GetImage().SavePng(Path.Combine(_output, name + ".png"));
    }
    private static bool Contains(Vector2I size, Control control)
    {
        var rect = control.GetGlobalRect();
        return rect.Position.X >= 0 && rect.Position.Y >= 0 && rect.End.X <= size.X + 1 && rect.End.Y <= size.Y + 1;
    }
    private static IEnumerable<Node> Descendants(Node root)
    {
        foreach (Node child in root.GetChildren()) { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
    private void Check(string name, bool passed, string detail = null)
    {
        _checks.Add(new { name, passed, detail });
        if (!passed) _failures++;
        GD.Print($"NAVIGATION {(passed ? "PASS" : "FAIL")}: {name} {detail}");
    }
}
