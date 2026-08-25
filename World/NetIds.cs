namespace SerikaSocial.World;

/// NetId reservation for the ObjectSync channel.
///
/// The relay treats ObjectSync bodies as opaque — it only reads the 2-byte obj_id to fan them
/// out. That means we can alias part of the 16-bit id space for stroke data without any proto
/// or Rust changes, as long as the client routes by id range on receive.
///
/// <list type="bullet">
/// <item>0x0000 — invalid / unused</item>
/// <item>0x0001–0x7FFF — physics props (the existing range)</item>
/// <item>0x8000–0xFFFE — stroke segments (aliased)</item>
/// <item>0xFFFF — reserved (broadcast sentinel)</item>
/// </list>
///
/// Stroke segments reuse the ObjectSync layout's 42 content bytes to carry a single stroke
/// point: position = world-space point, and the rotation/velocity floats are reinterpreted to
/// carry stroke metadata (stroke id, point index, colour, flags). See <c>StrokeNetwork</c>.
public static class NetIds
{
    public const ushort Invalid = 0x0000;
    public const ushort PropRangeStart = 0x0001;
    public const ushort PropRangeEnd = 0x7FFF;
    public const ushort StrokeRangeStart = 0x8000;
    public const ushort StrokeRangeEnd = 0xFFFE;
    public const ushort Broadcast = 0xFFFF;

    /// True if this id is a physics prop (not a stroke alias).
    public static bool IsProp(ushort id) => id >= PropRangeStart && id <= PropRangeEnd;

    /// True if this id is a stroke segment alias.
    public static bool IsStroke(ushort id) => id >= StrokeRangeStart && id <= StrokeRangeEnd;
}
