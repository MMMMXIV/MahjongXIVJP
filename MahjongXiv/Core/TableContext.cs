using MahjongXiv.Core.Game;
using MahjongXiv.Engine;

namespace MahjongXiv.Core;

/// <summary>
/// Builds the engine inputs from a live read, shared by auto-play and Suggest mode so both show and
/// play exactly the same move.
/// </summary>
internal static class TableContext
{
    /// <summary>
    /// Table context for the value ranking. Seat wind and dealer status come from our own panel;
    /// the round wind is not readable yet, so East is assumed (correct for quick matches; a wrong
    /// guess only misweights one yakuhai pair).
    /// </summary>
    public static EvalContext BuildEvalContext(in HandSnapshot snap, BoardSnapshot? board)
    {
        int seatWind = EmjStrings.WindToIndex(board?.Panels[MjConstants.SelfIndex].Wind);

        return new EvalContext
        {
            SeatWind = seatWind,
            RoundWind = 0,
            Dealer = seatWind == 0,
            Closed = snap.MeldCount == 0,
            DoraIndicators = board?.DoraIndicators,
            AkaByType = snap.AkaByType,
        };
    }

    /// <summary>The turn-policy input for a full 14-tile (post-draw) hand.</summary>
    public static TurnInput BuildTurnInput(in HandSnapshot snap, BoardSnapshot? board, bool riichiAvailable)
        => new()
        {
            Counts14 = snap.Counts,
            Melds = snap.Melds,
            Seen = board?.BuildSeen(snap.Counts, snap.Melds),
            Context = BuildEvalContext(snap, board),
            ThreatRivers = (IReadOnlyList<IReadOnlyList<int>>?)board?.ThreatRivers ?? Array.Empty<IReadOnlyList<int>>(),
            OwnRiver = board?.OwnRiver ?? Array.Empty<int>(),
            RiichiAvailable = riichiAvailable,
        };
}
