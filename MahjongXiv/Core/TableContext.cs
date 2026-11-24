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
    /// Table context for the value ranking. Seat wind and dealer status come from our own panel; the
    /// round wind from the tracked hand number (East when it is not known, e.g. joined mid-match).
    /// </summary>
    public static EvalContext BuildEvalContext(in HandSnapshot snap, BoardSnapshot? board)
    {
        int seatWind = EmjStrings.WindToIndex(board?.Panels[MjConstants.SelfIndex].Wind);
        MatchInfo match = MatchNow();

        return new EvalContext
        {
            SeatWind = seatWind,
            RoundWind = match.HandNumber >= 0 ? Math.Min(3, match.HandNumber / 4) : 0,
            Dealer = seatWind == 0,
            Closed = snap.MeldCount == 0,
            DoraIndicators = board?.DoraIndicators,
            AkaByType = snap.AkaByType,
        };
    }

    /// <summary>The turn-policy input for a full 14-tile (post-draw) hand.</summary>
    public static TurnInput BuildTurnInput(in HandSnapshot snap, BoardSnapshot? board, bool riichiAvailable)
    {
        MatchInfo match = MatchNow();
        return new TurnInput
        {
            Options = Plugin.Cfg.StrategicAi ? EvalOptions.Strategic : EvalOptions.Default,
            Opponents = BuildOpponents(board, match),
            Game = BuildGame(board, match),
            WallRemaining = WallRemaining(board, drawPending: snap.TilesRead == 14 - 3 * snap.MeldCount),
            Counts14 = snap.Counts,
            Melds = snap.Melds,
            Seen = board?.BuildSeen(snap.Counts, snap.Melds),
            Context = BuildEvalContext(snap, board),
            ThreatRivers = (IReadOnlyList<IReadOnlyList<int>>?)board?.ThreatRivers ?? Array.Empty<IReadOnlyList<int>>(),
            OwnRiver = board?.OwnRiver ?? Array.Empty<int>(),
            RiichiAvailable = riichiAvailable,
        };
    }

    private static MatchInfo MatchNow()
        => Plugin.Instance?.Match is { } m ? m.Snapshot() : new MatchInfo(-1, 8, 0, 0, false, [-1, -1, -1, -1]);

    /// <summary>Live draws still to come, from the visible rivers and kans (see <see cref="TableMath.LiveDrawsLeft"/>); -1 without a board.</summary>
    public static int WallRemaining(BoardSnapshot? board, bool drawPending)
    {
        if (board is null)
            return -1;
        int river = 0;
        foreach (IReadOnlyList<int> r in board.Rivers)
            river += r.Count;
        return TableMath.LiveDrawsLeft(river, board.VisibleKans, drawPending);
    }

    /// <summary>
    /// Opponents in panel order (panel index = seat number for the engine): river, riichi badge and the
    /// tracked declaration point, the melds read from that seat's strip, and whether the panel shows East.
    /// </summary>
    private static IReadOnlyList<OpponentView>? BuildOpponents(BoardSnapshot? board, MatchInfo match)
    {
        if (board is null)
            return null;

        var list = new List<OpponentView>(3);
        for (int i = 0; i < board.Panels.Count && i < board.Rivers.Count; i++)
        {
            if (i == MjConstants.SelfIndex)
                continue;
            bool riichi = board.Panels[i].Riichi;
            list.Add(new OpponentView
            {
                Seat = i,
                River = board.Rivers[i],
                Riichi = riichi,
                RiichiDiscardIndex = riichi && i < match.RiichiRiverIndex.Length ? match.RiichiRiverIndex[i] : -1,
                Melds = i < board.SeatMelds.Count ? board.SeatMelds[i] : Array.Empty<Meld>(),
                IsDealer = EmjStrings.WindToIndex(board.Panels[i].Wind) == 0 && board.Panels[i].Wind.Length > 0,
            });
        }

        return list;
    }

    /// <summary>
    /// Scores from the seat panels, the hand number / sticks / honba from the match tracker, and the
    /// match length from the duty. When the hand number is unknown (joined mid-match) the match is
    /// treated as half-way through. Null when the scores could not be read.
    /// </summary>
    private static GameState? BuildGame(BoardSnapshot? board, MatchInfo match)
    {
        if (board is null || board.Panels.Count != 4 || board.Panels.All(p => p.Score == 0))
            return null;

        int dealer = 0;
        for (int i = 0; i < 4; i++)
            if (board.Panels[i].Wind.Length > 0 && EmjStrings.WindToIndex(board.Panels[i].Wind) == 0)
                dealer = i;

        int handNumber = match.HandNumber >= 0
            ? Math.Min(match.HandNumber, match.TotalHands - 1)
            : match.TotalHands / 2 - 1;

        return new GameState
        {
            Scores = board.Panels.Select(p => p.Score).ToArray(),
            SelfSeat = MjConstants.SelfIndex,
            DealerSeat = dealer,
            HandNumber = handNumber,
            TotalHands = match.TotalHands,
            Honba = match.Honba,
            RiichiSticks = match.RiichiSticks,
        };
    }
}
