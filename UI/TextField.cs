using Godot;

namespace SerikaSocial;

/// Virtual-keyboard policy for every text field in the client, in one place. Every `LineEdit`
/// that takes user input goes through one of these, so a field's OS keyboard matches what the
/// field is for.
///
/// On touch platforms (`Secret` alone is NOT enough there) the OS keyboard is driven entirely
/// by `LineEdit.VirtualKeyboardType`, and a field that never declares a type gets the default
/// word keyboard: full suggestion strip, autocorrect and swipe typing. For the login password
/// that means the OS keyboard offers to complete — and learns — the secret it must never see,
/// while the long-press context menu cuts or copies the masked text into the clipboard, where
/// any other app can read it. Passwords therefore get the password keyboard (no suggestions,
/// no autocorrect, no keyboard learning on either Android or iOS) and lose the context menu
/// outright. Email and URL fields get the matching keyboard layouts (@ / / and .com on the
/// front page, with the input-type variation that turns suggestions off). Free-text fields —
/// chat, titles, search, report details — deliberately keep the default keyboard, because
/// suggestions genuinely help there.
public static class TextField
{
    /// A secret (login password): masked, password keyboard, no suggestions, no copy/cut.
    public static void AsPassword(LineEdit field)
    {
        field.Secret = true;
        field.VirtualKeyboardType = LineEdit.VirtualKeyboardTypeEnum.Password;
        // Long-press on Android/iOS (right-click on desktop) opens cut/copy/paste with the
        // text still masked. Copy is pure exfiltration and cut is a self-own; paste still
        // works through OS password autofill and a hardware keyboard.
        field.ContextMenuEnabled = false;
        field.MiddleMousePasteEnabled = false;
    }

    /// An email address: the @ keyboard layout, no suggestions, nothing rewriting the address.
    public static void AsEmail(LineEdit field)
    {
        field.VirtualKeyboardType = LineEdit.VirtualKeyboardTypeEnum.EmailAddress;
    }

    /// A URL (video links): the / and .com keyboard layout, no suggestions.
    public static void AsUrl(LineEdit field)
    {
        field.VirtualKeyboardType = LineEdit.VirtualKeyboardTypeEnum.Url;
    }
}
