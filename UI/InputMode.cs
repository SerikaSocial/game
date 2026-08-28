using System;
using System.Collections.Generic;
using Godot;

namespace SerikaSocial.UI;

/// The single owner of `Input.MouseMode` and of whether gameplay controls are live.
///
/// Before this existed, nine different call sites (Main, LocalPlayer and six UI screens) each
/// set `Input.MouseMode` directly and each guessed what to restore it to on close. Closing one
/// overlay while another was still up recaptured the mouse underneath it, which is what made
/// the cursor vanish while a menu was visibly on screen. Ownership is now counted, not guessed:
/// every screen takes a named hold while it is open and releases it when it closes, and the
/// cursor is free while *any* hold is outstanding.
///
/// Two distinct states, deliberately:
///   - a **hold** (a UI layer is up) frees the cursor AND suspends movement;
///   - **manual free** (the player pressed Tab) frees the cursor but leaves movement live, so
///     you can walk while clicking on world UI. Mouse-look stops on its own, because
///     `LocalPlayer` only consumes motion events while the mouse is captured.
public static class InputMode
{
    /// Outstanding holds, keyed by reason so a screen that double-releases (or never opened)
    /// can't drive a counter negative and strand the cursor. The value is whether that hold
    /// wants the cursor freed: the photo viewfinder suspends movement but keeps the mouse
    /// captured, because there the mouse aims the camera instead of driving a pointer.
    private static readonly Dictionary<string, bool> Holds = new();

    private static bool _manualFree;
    private static bool _playable;
    private static bool _headless;
    private static bool _initialised;

    /// Installed by Main to push the controls-live flag onto whichever rig is active. Kept as a
    /// callback rather than a typed reference because the VR rig has no `ControlsEnabled`.
    public static Action<bool> ControlsSink;

    /// Raised whenever the cursor-free state changes, so HUD hints can follow it.
    public static event Action<bool> CursorFreeChanged;

    // Hold names. Constants rather than literals so a typo can't leak a permanent hold.
    public const string Menu = "menu";
    public const string Chat = "chat";
    public const string Tutorial = "tutorial";
    public const string WorldList = "worldlist";
    public const string AvatarSelector = "avatar";
    public const string Camera = "camera";
    public const string Settings = "settings";
    public const string Video = "video";
    public const string Loading = "loading";

    /// True while the player has a rig they could be controlling (in Home or a world).
    public static bool Playable => _playable;

    /// Any UI layer outstanding — the "is a menu up" question, without each caller having to
    /// null-check six screens.
    public static bool AnyHold => Holds.Count > 0;

    public static bool HasHold(string reason) => Holds.ContainsKey(reason);

    public static bool CursorFree => !_playable || _manualFree || AnyHoldWantsCursor;

    public static bool ControlsLive => _playable && Holds.Count == 0;

    private static bool AnyHoldWantsCursor
    {
        get
        {
            foreach (var wantsCursor in Holds.Values)
                if (wantsCursor) return true;
            return false;
        }
    }

    /// Headless smoke runs have no window to capture a mouse in; every mutation is a no-op
    /// against `Input.MouseMode` there, but the hold bookkeeping still runs so `ControlsLive`
    /// stays meaningful in CI.
    private static void EnsureInit()
    {
        if (_initialised) return;
        _initialised = true;
        _headless = DisplayServer.GetName().Equals("headless");
    }

    /// Take a hold: a UI layer opened. Idempotent, and re-taking with a different `freeCursor`
    /// updates it — that's how the menu hold flips when the viewfinder opens over another screen.
    public static void Hold(string reason, bool freeCursor = true)
    {
        EnsureInit();
        if (Holds.TryGetValue(reason, out var existing) && existing == freeCursor) return;
        Holds[reason] = freeCursor;
        Apply();
    }

    /// Release a hold: a UI layer closed. Idempotent, and safe to call for a screen that never
    /// took one.
    public static void Release(string reason)
    {
        EnsureInit();
        if (Holds.Remove(reason)) Apply();
    }

    /// Drop every hold — used when leaving a world, where the screens are torn down rather than
    /// closed and would otherwise never release.
    public static void ReleaseAll()
    {
        EnsureInit();
        if (Holds.Count == 0) return;
        Holds.Clear();
        Apply();
    }

    /// Enter/leave a state where there is something to control. Leaving clears the manual-free
    /// toggle so re-entering a world always starts captured.
    public static void SetPlayable(bool playable)
    {
        EnsureInit();
        if (_playable == playable) return;
        _playable = playable;
        if (!playable) _manualFree = false;
        Apply();
    }

    /// Tab: free the cursor without giving up movement, or recapture it. Ignored while a UI
    /// layer is up — there the cursor is already free and Tab would only desync the toggle,
    /// so the caller gets false and can leave the event unhandled.
    public static bool ToggleManualCursor()
    {
        EnsureInit();
        if (!_playable || Holds.Count > 0) return false;
        _manualFree = !_manualFree;
        Apply();
        return true;
    }

    /// Push the current state onto the engine and the active rig.
    public static void Apply()
    {
        EnsureInit();
        bool free = CursorFree;

        if (!_headless)
        {
            var want = free ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
            if (Input.MouseMode != want) Input.MouseMode = want;
        }

        ControlsSink?.Invoke(ControlsLive);
        LogHoldsIfChanged();

        if (free != _lastFree)
        {
            _lastFree = free;
            CursorFreeChanged?.Invoke(free);
        }
    }

    private static bool _lastFree = true;

    /// Say out loud which holds are outstanding whenever the set changes.
    ///
    /// `ControlsLive` requires *zero* holds, so a single screen that forgets to release one
    /// silently takes away the player's ability to move — with nothing on screen to explain it
    /// and no way to recover. That has now happened twice (the tutorial hold, and menus that
    /// could not be closed in VR because the menu button did not toggle), and both times the only
    /// way to find it was to reason backwards from "I can't walk".
    ///
    /// One line per change, so the log answers "why can't I move" directly.
    private static string _lastHoldSignature = "";

    private static void LogHoldsIfChanged()
    {
        string signature;
        if (Holds.Count == 0) signature = "(none)";
        else
        {
            var names = new List<string>(Holds.Keys);
            names.Sort();
            signature = string.Join(",", names);
        }

        if (signature == _lastHoldSignature) return;
        _lastHoldSignature = signature;
        GD.Print($"InputMode: holds={signature} playable={_playable} controlsLive={ControlsLive}");
    }
}
