namespace SerikaSocial;

/// One travel owns the scene until it has entered Home or finished its relay handshake.
/// The generation also protects deferred work from a previous, completed trip.
internal sealed class WorldTravelOperation
{
    internal enum Destination { None, Home, Instance }
    public Destination Target { get; private set; }
    public int Version { get; private set; }
    public bool Active => Target != Destination.None;
    public bool TryBegin(Destination target, out int version)
    {
        version = Version;
        if (Active || target == Destination.None) return false;
        Target = target;
        version = ++Version;
        return true;
    }
    public bool IsCurrent(int version, Destination target) => Version == version && Target == target;
    public bool Complete(int version)
    {
        if (!Active || Version != version) return false;
        Target = Destination.None;
        return true;
    }
}
