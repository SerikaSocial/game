namespace SerikaSocial.UI;

/// A physical press may begin only after neutral input on an active, tracked UI ray.
/// This state survives a miss: squeezing off-panel then aiming in must never click.
internal sealed class VrPointerPress
{
    internal enum Change { None, Press, Release, Cancel }
    public bool Captured { get; private set; }
    private bool _active, _armed, _pressed;

    public Change Step(bool active, bool hit, float value, float pressAt = .7f, float releaseAt = .4f)
    {
        if (!active)
        {
            bool cancel = Captured;
            _active = _armed = _pressed = Captured = false;
            return cancel ? Change.Cancel : Change.None;
        }
        if (!_active) { _active = true; _armed = false; _pressed = false; }
        if (value <= releaseAt) _armed = true;
        bool pressed = _pressed ? value > releaseAt : value > pressAt;
        bool rising = pressed && !_pressed;
        _pressed = pressed;
        if (Captured && !hit) { Captured = false; return Change.Cancel; }
        if (Captured && !pressed) { Captured = false; return Change.Release; }
        if (_armed && rising && hit) { Captured = true; return Change.Press; }
        return Change.None;
    }
}
