namespace MahjongXiv.Engine;

/// <summary>Arithmetic about the wall that only needs what is visible on the table.</summary>
public static class TableMath
{
    /// <summary>Live-wall draws in a hand: 136 tiles − 14 dead wall − 4 × 13 dealt.</summary>
    public const int LiveWallDraws = 70;

    /// <summary>
    /// Live draws still to come. Every live draw ends in exactly one tile reaching a river, except that
    /// a kan also pulls one replacement from the live wall (the dead wall is topped up), and an
    /// ankan/kakan turn's draw goes into the meld while its rinshan discard reaches the river. Calls
    /// (pon/chi) move a river tile into a meld but are paid for by a discard without a draw, so they
    /// cancel out. Hence: draws consumed = river tiles + kans (any kind) + 1 while a drawn tile has not
    /// been discarded yet.
    /// </summary>
    /// <param name="riverTiles">Tiles currently in all four rivers (called tiles have left them).</param>
    /// <param name="kans">Kans of any kind visible on the table.</param>
    /// <param name="drawPending">A player holds a drawn tile not yet discarded (e.g. our own turn).</param>
    public static int LiveDrawsLeft(int riverTiles, int kans, bool drawPending)
        => Math.Clamp(LiveWallDraws - riverTiles - kans - (drawPending ? 1 : 0), 0, LiveWallDraws);
}
