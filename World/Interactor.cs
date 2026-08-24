using Godot;
using SerikaSocial.Player;
using SerikaSocial.UI;

namespace SerikaSocial.World;

/// Drives "walk up to a thing and press E".
///
/// Every `IInteractable` joins `Interactable.Group` when it enters the tree, so this scans the
/// group rather than needing worlds to register anything. The scan runs at 10 Hz, not per
/// frame: `GetNodesInGroup` allocates a Godot array, and allocation in the frame path is what
/// puts the GC in frame time.
public partial class Interactor : Node
{
    /// How often the candidate scan runs. Fast enough that the prompt feels instant, slow
    /// enough that the array churn is negligible.
    private const double ScanInterval = 0.1;

    /// Interactables more than this far behind where you're looking are ignored, so standing
    /// between two chairs picks the one you're facing rather than the marginally closer one.
    private const float MinFacingDot = -0.3f;

    private LocalPlayer _player;
    private InteractionPrompt _prompt;
    private double _scanTimer;
    private IInteractable _target;

    public static Interactor Create(LocalPlayer player, InteractionPrompt prompt) =>
        new() { Name = "Interactor", _player = player, _prompt = prompt };

    /// The interactable E would act on right now, or null. Null while seated — there the only
    /// available action is standing back up.
    public IInteractable Target => _target;

    public override void _Process(double delta)
    {
        if (_player == null || !IsInstanceValid(_player))
        {
            _prompt?.Clear();
            return;
        }

        // Seated: the only offer is to get up, and there's no point scanning.
        if (_player.Occupying != null)
        {
            _target = null;
            _prompt?.Show("Stand up");
            return;
        }

        _scanTimer -= delta;
        if (_scanTimer > 0) return;
        _scanTimer = ScanInterval;

        _target = FindBest();
        if (_target == null) _prompt?.Clear();
        else _prompt?.Show(_target.PromptText);
    }

    /// Nearest interactable that's in range, available, and roughly in front of the player.
    private IInteractable FindBest()
    {
        var eye = _player.EyePosition;
        var aim = _player.AimForward;

        IInteractable best = null;
        float bestScore = float.MaxValue;

        foreach (var node in GetTree().GetNodesInGroup(Interactable.Group))
        {
            if (node is not IInteractable candidate || !candidate.CanInteract) continue;

            var offset = candidate.FocusPoint - eye;
            float dist = offset.Length();
            if (dist > candidate.Range) continue;

            // Straight up/down (standing on the seat) has no meaningful facing, so treat a
            // degenerate offset as dead ahead rather than dividing by ~zero.
            float dot = dist > 0.01f ? aim.Dot(offset / dist) : 1f;
            if (dot < MinFacingDot) continue;

            // Distance decides, with a nudge toward whatever you're actually looking at.
            float score = dist - dot * 0.5f;
            if (score >= bestScore) continue;
            bestScore = score;
            best = candidate;
        }

        return best;
    }

    /// Act on the current target. Returns true if something happened, so the caller knows
    /// whether to mark the key event handled.
    public bool TryInteract()
    {
        if (_player == null || !IsInstanceValid(_player)) return false;

        if (_player.Occupying != null)
        {
            _player.StandUp();
            _scanTimer = 0;   // re-scan immediately so the prompt updates this frame
            return true;
        }

        // The cached target can go stale between scans — another player may have taken the
        // seat — so re-check availability at the moment of use rather than trusting the scan.
        if (_target == null || !_target.CanInteract) return false;

        _target.Interact(_player);
        _target = null;
        _scanTimer = 0;
        return true;
    }
}
