namespace MahjongXiv.Engine;

/// <summary>
/// Packs a 34-type count array (0-4 copies each, 3 bits per type) into a 128-bit key for memo tables.
/// 102 bits hold the counts; the top bits are free for small extra fields (drawn tile, aka count).
/// </summary>
public static class HandKey
{
    public static UInt128 Pack(ReadOnlySpan<int> counts)
    {
        UInt128 key = 0;
        for (int t = 0; t < Tiles.TypeCount; t++)
            key |= (UInt128)(uint)(counts[t] & 7) << (3 * t);
        return key;
    }

    /// <summary>Pack counts plus two small extra fields (each &lt; 64) into the spare high bits.</summary>
    public static UInt128 Pack(ReadOnlySpan<int> counts, int extraA, int extraB)
        => Pack(counts) | ((UInt128)(uint)(extraA & 63) << 104) | ((UInt128)(uint)(extraB & 63) << 112);
}
