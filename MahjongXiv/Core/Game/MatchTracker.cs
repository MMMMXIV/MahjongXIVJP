namespace MahjongXiv.Core.Game;

/// <summary>One look at the table: seat-indexed (panel order, 0 = us) scores, riichi badges, river sizes and the dealer.</summary>
internal readonly record struct TableObservation(int[] Scores, bool[] Riichi, int[] RiverCounts, int DealerSeat);

/// <summary>
/// Follows a match from what the table shows, so the engine gets the hand number, the riichi sticks on
/// the table, the honba and when each opponent declared riichi. Nothing here reads the game; the plugin
/// feeds it <see cref="TableObservation"/>s and the tests feed it scripted ones.
/// <list type="bullet">
/// <item>Hand number: counted from a fresh match (every score at the start value) by dealer changes.
/// Joining mid-match leaves it unknown.</item>
/// <item>Sticks: a riichi badge lighting up adds one; a won hand clears them, a drawn hand carries them.</item>
/// <item>Honba: +1 when the dealer stays or the hand is drawn, back to 0 when a non-dealer wins.</item>
/// <item>A new hand is seen when all rivers empty out (twice in a row, to ignore a momentary misread)
/// after holding tiles, or when the dealer changes.</item>
/// </list>
/// Win or draw is told from the score changes of the finished hand (riichi deposits removed): a draw
/// moves points only as tenpai payments (equal gains, equal losses, 3000 in total) or not at all.
/// </summary>
internal sealed class MatchTracker
{
    public const int StartScore = 25000;

    private int[]? handStartScores;
    private bool[] lastRiichi = new bool[4];
    private readonly int[] declaredThisHand = new int[4];
    private int lastDealer = -1;
    private int lastRiverTotal;
    private int emptyStreak;
    private bool awaitingClear; // a hand was closed by a dealer change before its rivers emptied
    private int sticksAtHandStart;

    /// <summary>Dealer rotations completed since the first hand (0 = East 1), or -1 when unknown.</summary>
    public int HandNumber { get; private set; } = -1;

    public bool HandNumberKnown => HandNumber >= 0;

    public int Honba { get; private set; }

    /// <summary>Riichi sticks on the table now (carried from draws plus this hand's declarations).</summary>
    public int RiichiSticks => sticksAtHandStart + declaredThisHand.Sum();

    /// <summary>Whether sticks and honba were followed from the first hand (otherwise they are lower bounds).</summary>
    public bool CountersKnown { get; private set; }

    /// <summary>Per seat: the river index treated as the riichi declaration tile, or -1.</summary>
    public int[] RiichiRiverIndex { get; } = [-1, -1, -1, -1];

    public int HandsSeen { get; private set; }

    public void Reset()
    {
        handStartScores = null;
        lastRiichi = new bool[4];
        Array.Clear(declaredThisHand);
        lastDealer = -1;
        lastRiverTotal = 0;
        emptyStreak = 0;
        awaitingClear = false;
        sticksAtHandStart = 0;
        HandNumber = -1;
        Honba = 0;
        CountersKnown = false;
        HandsSeen = 0;
        Array.Fill(RiichiRiverIndex, -1);
    }

    public void Observe(TableObservation o)
    {
        if (o.Scores.Length != 4 || o.Riichi.Length != 4 || o.RiverCounts.Length != 4)
            return;

        if (handStartScores is null)
        {
            Start(o);
            return;
        }

        int riverTotal = o.RiverCounts.Sum();
        emptyStreak = riverTotal == 0 ? emptyStreak + 1 : 0;
        if (awaitingClear)
        {
            // The finished hand's rivers are still on screen: do not let them count as the new hand.
            if (riverTotal == 0)
                awaitingClear = false;
            riverTotal = 0;
        }
        bool riversCleared = lastRiverTotal >= 4 && emptyStreak >= 2;
        bool dealerChanged = o.DealerSeat >= 0 && lastDealer >= 0 && o.DealerSeat != lastDealer;

        if (riversCleared || dealerChanged)
        {
            FinishHand(o);
            lastRiverTotal = 0;
            awaitingClear = o.RiverCounts.Sum() > 0;
        }
        else if (riverTotal > 0)
        {
            lastRiverTotal = Math.Max(lastRiverTotal, riverTotal);
        }

        // Riichi badges lighting up: a stick, and where the declaration sits in that river. The badge can
        // appear before the declaration tile is drawn on the table, so the next index is taken: at worst
        // one genuinely safe tile is not counted as safe.
        for (int s = 0; s < 4; s++)
        {
            if (o.Riichi[s] && !lastRiichi[s])
            {
                declaredThisHand[s]++;
                RiichiRiverIndex[s] = o.RiverCounts[s];
            }
        }

        lastRiichi = (bool[])o.Riichi.Clone();
        if (o.DealerSeat >= 0)
            lastDealer = o.DealerSeat;
    }

    private void Start(TableObservation o)
    {
        handStartScores = (int[])o.Scores.Clone();
        lastDealer = o.DealerSeat;
        lastRiverTotal = o.RiverCounts.Sum();
        bool fresh = o.Scores.All(s => s == StartScore) && !o.Riichi.Any(r => r);
        HandNumber = fresh ? 0 : -1;
        CountersKnown = fresh;
        Honba = 0;
        sticksAtHandStart = 0;
        Array.Clear(declaredThisHand);
        for (int s = 0; s < 4; s++)
        {
            if (o.Riichi[s])
            {
                declaredThisHand[s] = 1; // already in riichi when we arrived: at least one stick
                RiichiRiverIndex[s] = Math.Max(0, o.RiverCounts[s] - 1);
            }
        }
        lastRiichi = (bool[])o.Riichi.Clone();
    }

    private void FinishHand(TableObservation o)
    {
        int[] before = handStartScores!;
        var change = new int[4];
        for (int s = 0; s < 4; s++)
            change[s] = o.Scores[s] - before[s] + 1000 * declaredThisHand[s]; // riichi deposits are not payments

        bool drawn = IsDrawPattern(change);
        bool dealerKept = o.DealerSeat >= 0 && o.DealerSeat == lastDealer;

        if (drawn)
        {
            sticksAtHandStart = RiichiSticks;
            Honba++;
        }
        else
        {
            sticksAtHandStart = 0;
            Honba = dealerKept ? Honba + 1 : 0;
        }

        if (!dealerKept && HandNumber >= 0)
            HandNumber++;

        HandsSeen++;
        handStartScores = (int[])o.Scores.Clone();
        Array.Clear(declaredThisHand);
        Array.Fill(RiichiRiverIndex, -1);
        lastRiichi = new bool[4];
        emptyStreak = 0;
    }

    /// <summary>
    /// A draw moves points only as tenpai payments: nothing at all, or gainers all equal, payers all
    /// equal and 3000 changing hands. Any other pattern is a win. (A dealer's 1000-all tsumo looks like
    /// a one-tenpai draw, but both keep the dealer and add a honba, and there were no sticks to award,
    /// so the counters come out the same either way.)
    /// </summary>
    public static bool IsDrawPattern(IReadOnlyList<int> change)
    {
        var gains = change.Where(c => c > 0).ToList();
        var losses = change.Where(c => c < 0).ToList();
        if (gains.Count == 0 && losses.Count == 0)
            return true;
        if (gains.Count == 0 || losses.Count == 0)
            return false;
        return gains.Distinct().Count() == 1 && losses.Distinct().Count() == 1
               && gains.Sum() == 3000 && -losses.Sum() == 3000;
    }
}
