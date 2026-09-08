using System;
using Godot;
namespace SerikaSocial.UI;

/// The three things a player actually wants at a crowded live event, put where the join button
/// was. Offering "Join event" to somebody already standing in the venue is noise; a packed hall
/// on a mid-range machine is not, and every one of these is a way to keep the show watchable.
///
/// State lives with the owner (Main), not here — this control only reports intent and is told
/// what to display, so it stays correct when a value is restored from settings or changed while
/// the menu is closed.
public partial class EventOptions : VBoxContainer
{
    public event Action<bool> HidePlayersToggled;
    public event Action<bool> MutePlayersToggled;
    public event Action<bool> EffectsToggled;
    private CheckButton _hide, _mute, _effects;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 8);
        var title = new Label { Text = "EVENT" };
        title.AddThemeFontSizeOverride("font_size", 11);
        title.AddThemeColorOverride("font_color", Brand.Accent);
        AddChild(title);

        _hide = Toggle("Hide all players", "Show only the stage. Other attendees' avatars stop drawing.",
            on => HidePlayersToggled?.Invoke(on));
        _mute = Toggle("Mute all players", "Silence attendee voice chat. The show's own audio is unaffected.",
            on => MutePlayersToggled?.Invoke(on));
        _effects = Toggle("Disable effects", "Turn off pyro, lasers, crowd penlights and light sticks. The performer, stage lighting, screens and audio stay.",
            on => EffectsToggled?.Invoke(on));

        // Without a rule these three sit flush against the quick menu's own header and read as
        // part of it rather than as a block belonging to the venue.
        AddChild(new HSeparator());
    }

    private CheckButton Toggle(string text, string tooltip, Action<bool> handler)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        AddChild(row);
        var label = new Label { Text = text, SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = tooltip };
        label.AddThemeFontSizeOverride("font_size", 14);
        label.AddThemeColorOverride("font_color", Brand.TextMid);
        row.AddChild(label);
        var check = new CheckButton { TooltipText = tooltip };
        check.Toggled += on => handler(on);
        row.AddChild(check);
        return check;
    }

    /// Reflect the owner's state without firing the change handlers back at it.
    public void SetState(bool hidePlayers, bool mutePlayers, bool effectsDisabled)
    {
        if (_hide == null) return;
        _hide.SetPressedNoSignal(hidePlayers);
        _mute.SetPressedNoSignal(mutePlayers);
        _effects.SetPressedNoSignal(effectsDisabled);
    }
}
