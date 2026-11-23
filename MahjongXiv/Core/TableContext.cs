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
            Options = Plugin.Cfg.StrategicAi ? EvalOptions.Strategic : EvalOptions.Default,
            Opponents = BuildOpponents(board),
            Game = BuildGame(board),
            Counts14 = snap.Counts,
            Melds = snap.Melds,
            Seen = board?.BuildSeen(snap.Counts, snap.Melds),
            Context = BuildEvalContext(snap, board),
            ThreatRivers = (IReadOnlyList<IReadOnlyList<int>>?)board?.ThreatRivers ?? Array.Empty<IReadOnlyList<int>>(),
            OwnRiver = board?.OwnRiver ?? Array.Empty<int>(),
            RiichiAvailable = riichiAvailable,
        };

    /// <summary>
    /// Opponents in panel order (panel index = seat number for the engine). The live reader sees each
    /// river and riichi badge, and which panel shows East (the dealer). It cannot yet attribute melds
    /// to a seat or see when riichi was declared, so those stay empty / unknown.
    /// </summary>
    private static IReadOnlyList<OpponentView>? BuildOpponents(BoardSnapshot? board)
    {
        if (board is null)
            return null;

        var list = new List<OpponentView>(3);
        for (int i = 0; i < board.Panels.Count && i < board.Rivers.Count; i++)
        {
            if (i == MjConstants.SelfIndex)
                continue;
            list.Add(new OpponentView
            {
                Seat = i,
                River = board.Rivers[i],
                Riichi = board.Panels[i].Riichi,
                IsDealer = EmjStrings.WindToIndex(board.Panels[i].Wind) == 0 && board.Panels[i].Wind.Length > 0,
            });
        }

        return list;
    }

    /// <summary>
    /// Scores from the seat panels. The round and hand number are not read yet, so the match is
    /// assumed to be mid-way (three dealer turns left); placement awareness is therefore weaker live
    /// than in the simulator. Null when the scores could not be read.
    /// </summary>
    private static GameState? BuildGame(BoardSnapshot? board)
    {
        if (board is null || board.Panels.Count != 4 || board.Panels.All(p => p.Score == 0))
            return null;

        int dealer = 0;
        for (int i = 0; i < 4; i++)
            if (board.Panels[i].Wind.Length > 0 && EmjStrings.WindToIndex(board.Panels[i].Wind) == 0)
                dealer = i;

        return new GameState
        {
            Scores = board.Panels.Select(p => p.Score).ToArray(),
            SelfSeat = MjConstants.SelfIndex,
            DealerSeat = dealer,
            HandNumber = 1,
            TotalHands = 4,
        };
    }
}
