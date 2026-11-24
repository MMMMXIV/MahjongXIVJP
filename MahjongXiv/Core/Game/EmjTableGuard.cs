using MahjongXiv.Localization;

namespace MahjongXiv.Core.Game;

public enum TableKind
{
    /// <summary>A casual Duty Finder table; auto-play may act.</summary>
    Casual,

    /// <summary>A ranked table (rating at stake against humans); auto-play acts only when opted in.</summary>
    Ranked,

    /// <summary>The current duty is not a known casual table (unread, custom, or added by a patch).</summary>
    Unknown,
}

/// <summary>
/// Decides whether auto-play may send inputs at the current table. Ranked tables are allowed only
/// when the player opted in (<see cref="Configuration.AllowRankedAutoPlay"/>). Every Doman Mahjong duty shares
/// territory 831 (The Manderville Tables), so the ContentFinderCondition row is the only reliable
/// discriminator. Fails closed: only the known casual rows are allowed, so a ranked row added by a
/// later patch is blocked rather than played. Row ids read from the game's ContentFinderCondition
/// sheet (2026-10-14).
/// </summary>
internal static class EmjTableGuard
{
    // Novice/Advanced Mahjong (Full Ranked Match) = 643/644; (Quick Ranked Match) = 766/767.
    private static readonly HashSet<uint> RankedCfc = [643, 644, 766, 767];

    // Four-player Mahjong (Full Match, Kuitan Enabled/Disabled) = 645/650; (Quick Match, ...) = 768/769.
    private static readonly HashSet<uint> CasualCfc = [645, 650, 768, 769];

    /// <summary>The current ContentFinderCondition row id, or 0 when none is active.</summary>
    public static uint CurrentCfc => Plugin.DutyState.ContentFinderCondition.RowId;

    public static TableKind Current => Classify(CurrentCfc);

    /// <summary>
    /// Dealer rotations in the match: 4 for the quick (east-only) duties 766-769, 8 for the full
    /// (east+south) duties 643-645 and 650. Unknown duties are treated as full matches.
    /// </summary>
    public static int TotalHandsFor(uint cfc) => cfc is 766 or 767 or 768 or 769 ? 4 : 8;

    public static TableKind Classify(uint cfc)
        => CasualCfc.Contains(cfc) ? TableKind.Casual
            : RankedCfc.Contains(cfc) ? TableKind.Ranked
            : TableKind.Unknown;

    /// <summary>Player-facing reason auto-play refuses this table, or null when it may act.</summary>
    public static string? BlockReason(TableKind kind) => kind switch
    {
        TableKind.Casual => null,
        TableKind.Ranked when Plugin.Cfg.AllowRankedAutoPlay => null,
        TableKind.Ranked => Loc.T("block.ranked"),
        _ => Loc.T("block.unknown"),
    };
}
