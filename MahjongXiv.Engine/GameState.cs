namespace MahjongXiv.Engine;

/// <summary>
/// What the policy knows about one opponent. Seats are absolute (0-3). Anything the live reader
/// cannot see is left at its default, and the strategic layer only uses what is present.
/// </summary>
public sealed record OpponentView
{
    public required int Seat { get; init; }
    public required IReadOnlyList<int> River { get; init; }
    public bool Riichi { get; init; }

    /// <summary>Index in <see cref="River"/> of the riichi declaration tile, or -1 when unknown / not in riichi.</summary>
    public int RiichiDiscardIndex { get; init; } = -1;

    public IReadOnlyList<Meld> Melds { get; init; } = Array.Empty<Meld>();
    public bool IsDealer { get; init; }
}

/// <summary>
/// Match-level state for placement-aware decisions. Seats are absolute; <see cref="Scores"/> is
/// indexed by seat. <see cref="HandNumber"/> counts dealer rotations from 0 (East 1) and
/// <see cref="TotalHands"/> is 4 for an east-only match and 8 for east+south.
/// </summary>
public sealed record GameState
{
    public required IReadOnlyList<int> Scores { get; init; }
    public required int SelfSeat { get; init; }
    public required int DealerSeat { get; init; }
    public int HandNumber { get; init; }
    public int TotalHands { get; init; } = 4;
    public int Honba { get; init; }
    public int RiichiSticks { get; init; }

    /// <summary>Dealer rotations left after the current hand (0 in the last hand).</summary>
    public int HandsAfterThis => Math.Max(0, TotalHands - 1 - HandNumber);

    public bool IsAllLast => HandsAfterThis == 0;
}
