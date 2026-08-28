using Godot;
using Godot.Collections;

namespace SerikaSocial.UI;

/// Build the Dialogue Manager example balloon in code.
///
/// The ExampleBalloon.tscn scene silently exports as a 0-byte .scn on Android/Quest
/// because Godot's C# scene converter fails for addon scenes that reference C# scripts
/// via UID. This factory builds the same node tree in C# so the tutorial works on
/// every platform without depending on scene conversion.
public static class TutorialBalloon
{
    public static DialogueManagerRuntime.ExampleBalloon Create()
    {
        var balloon = new DialogueManagerRuntime.ExampleBalloon
        {
            Name = "ExampleBalloon",
            Layer = 100,
        };

        // ── Balloon (full-screen Control) ──────────────────────────────────────
        var ui = new Control { Name = "Balloon" };
        ui.UniqueNameInOwner = true;
        ui.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        ui.GrowHorizontal = Control.GrowDirection.Both;
        ui.GrowVertical = Control.GrowDirection.Both;
        var theme = MakeTheme();
        ui.Theme = theme;
        balloon.AddChild(ui);

        // Bottom panel: MarginContainer → PanelContainer → MarginContainer → HBox → VBox
        var margin = new MarginContainer { Name = "MarginContainer" };
        margin.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        margin.OffsetTop = -219f;
        ui.AddChild(margin);

        var panel = new PanelContainer { Name = "PanelContainer" };
        panel.ClipChildren = CanvasItem.ClipChildrenMode.Only;
        panel.MouseFilter = Control.MouseFilterEnum.Pass;
        margin.AddChild(panel);

        var innerMargin = new MarginContainer { Name = "MarginContainer" };
        panel.AddChild(innerMargin);

        var hbox = new HBoxContainer { Name = "HBoxContainer" };
        innerMargin.AddChild(hbox);

        var vbox = new VBoxContainer { Name = "VBoxContainer" };
        vbox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        hbox.AddChild(vbox);

        // CharacterLabel
        var characterLabel = new RichTextLabel { Name = "CharacterLabel" };
        characterLabel.UniqueNameInOwner = true;
        characterLabel.Modulate = new Color(1, 1, 1, 0.5f);
        characterLabel.MouseFilter = Control.MouseFilterEnum.Pass;
        characterLabel.BbcodeEnabled = true;
        characterLabel.Text = "Character";
        characterLabel.FitContent = true;
        characterLabel.ScrollActive = false;
        vbox.AddChild(characterLabel);

        // DialogueLabel (custom type from addon)
        var dialogueLabel = new DialogueManagerRuntime.DialogueLabel { Name = "DialogueLabel" };
        dialogueLabel.UniqueNameInOwner = true;
        dialogueLabel.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        dialogueLabel.MouseFilter = Control.MouseFilterEnum.Pass;
        dialogueLabel.BbcodeEnabled = true;
        dialogueLabel.FitContent = true;
        dialogueLabel.ScrollActive = false;
        dialogueLabel.ShortcutKeysEnabled = false;
        dialogueLabel.MetaUnderlined = false;
        dialogueLabel.HintUnderlined = false;
        dialogueLabel.DeselectOnFocusLossEnabled = false;
        dialogueLabel.Set("visible_characters_behavior", 1);
        dialogueLabel.Text = "Dialogue...";
        vbox.AddChild(dialogueLabel);

        // Spacer Control with Progress indicator
        var spacer = new Control { Name = "Control" };
        spacer.CustomMinimumSize = new Vector2(20, 10);
        spacer.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
        hbox.AddChild(spacer);

        var progress = new Polygon2D { Name = "Progress" };
        progress.UniqueNameInOwner = true;
        progress.Polygon = new Vector2[] { new(0, 0), new(10, 10), new(20, 0) };
        spacer.AddChild(progress);

        // CenterContainer with ResponsesMenu
        var center = new CenterContainer { Name = "CenterContainer" };
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        center.GrowHorizontal = Control.GrowDirection.Both;
        center.GrowVertical = Control.GrowDirection.Both;
        ui.AddChild(center);

        var responses = new DialogueManagerRuntime.DialogueResponsesMenu { Name = "ResponsesMenu" };
        responses.UniqueNameInOwner = true;
        responses.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
        responses.AddThemeConstantOverride("separation", 2);
        responses.Set("alignment", 1);
        center.AddChild(responses);

        var responseExample = new Button { Name = "ResponseExample", Text = "Response example" };
        responses.AddChild(responseExample);
        responses.ResponseTemplate = responseExample;

        // AudioStreamPlayer
        var audio = new AudioStreamPlayer { Name = "AudioStreamPlayer" };
        audio.UniqueNameInOwner = true;
        balloon.AddChild(audio);

        // Every node needs an OWNER as well as `UniqueNameInOwner`, or `%Name` resolves to null.
        //
        // This subtree is built in code, and a code-built node has no owner unless you assign one
        // — `UniqueNameInOwner = true` on its own registers nothing. `ExampleBalloon._Ready()`
        // looks all five of its children up as `%Balloon`, `%CharacterLabel`, `%DialogueLabel`,
        // `%ResponsesMenu` and `%Progress`, so every one of them came back null and its
        // `_Process` threw a NullReferenceException on every single frame.
        //
        // That was not cosmetic: `Main.MaybeStartTutorial` takes an InputMode hold and only
        // releases it when the tutorial finishes, so a balloon that could never advance left the
        // player unable to move at all, with no way out.
        SetOwnerRecursive(balloon, balloon);
        return balloon;
    }

    /// Give every descendant the same owner, the way the scene loader would.
    private static void SetOwnerRecursive(Node node, Node owner)
    {
        foreach (var child in node.GetChildren())
        {
            child.Owner = owner;
            SetOwnerRecursive(child, owner);
        }
    }

    private static Theme MakeTheme()
    {
        var t = new Theme { DefaultFontSize = 20 };

        var disabled = MakeStyleBox(new Color(0, 0, 0, 1), new Color(0.329f, 0.329f, 0.329f, 1));
        var focus = MakeStyleBox(new Color(0.122f, 0.122f, 0.122f, 1), new Color(1, 1, 1, 1));
        var normal = MakeStyleBox(new Color(0, 0, 0, 1), new Color(0.6f, 0.6f, 0.6f, 1));
        t.SetStylebox("disabled", "Button", disabled);
        t.SetStylebox("focus", "Button", focus);
        t.SetStylebox("hover", "Button", normal);
        t.SetStylebox("normal", "Button", normal);

        var panelStyle = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 1) };
        panelStyle.SetBorderWidthAll(3);
        panelStyle.SetCornerRadiusAll(5);
        t.SetStylebox("panel", "PanelContainer", panelStyle);

        t.SetConstant("margin_bottom", "MarginContainer", 15);
        t.SetConstant("margin_left", "MarginContainer", 30);
        t.SetConstant("margin_right", "MarginContainer", 30);
        t.SetConstant("margin_top", "MarginContainer", 15);

        return t;
    }

    private static StyleBoxFlat MakeStyleBox(Color bg, Color border)
    {
        var s = new StyleBoxFlat
        {
            BgColor = bg,
            BorderColor = border,
        };
        s.SetBorderWidthAll(3);
        s.SetCornerRadiusAll(5);
        s.ContentMarginLeft = 20;
        s.ContentMarginTop = 10;
        s.ContentMarginRight = 20;
        s.ContentMarginBottom = 10;
        return s;
    }
}
