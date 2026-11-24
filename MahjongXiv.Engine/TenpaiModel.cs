namespace MahjongXiv.Engine;

/// <summary>
/// Chance that an opponent who has not declared riichi is already tenpai, as a logistic model over
/// public features: how far into the hand they are, how many melds they show and what kind (a yakuhai
/// set, a one-suit pattern, all triplets), and how much of the wall is gone. The coefficients are fitted
/// in the simulator, where every player's true shanten is known (<c>MahjongXiv.Simulator fit</c>).
/// Closed hands without riichi get no estimate: the simulated players always declare when they can,
/// so a silent closed tenpai cannot be learned there.
/// </summary>
public static class TenpaiModel
{
    public const int FeatureCount = 8;

    public static readonly string[] FeatureNames =
        ["bias", "turn", "melds", "melds*turn", "yakuhai set", "one-suit melds", "all triplets", "wall gone"];

    /// <summary>
    /// Fitted with <c>fit 300 909</c> (east+south, shipped agents; 31,941 samples, 27.5% ron-ready).
    /// Log loss 0.53 against 2.99 for the old meld-count rule, which put every one-meld hand at 0 while
    /// 23.7% of them were ready. The negative weight on all-triplet melds reflects the simulated players
    /// (they pon only, mostly for yakuhai); humans may differ.
    /// </summary>
    public static readonly double[] Coefficients = [-7.692, 3.413, 5.185, -0.242, -0.090, 0.005, -3.816, -0.326];

    /// <summary>
    /// Opponents below this chance are not treated as threats (keeps the offence path for quiet tables).
    /// The shipped default lives in <see cref="EvalOptions.TenpaiThreshold"/> (0.3): with 0.15 and the
    /// old 0.7 open-hand loss the strategic AI over-folded (−0.061 vs −0.091 avg place, 400 matches);
    /// 0.3 with a 0.4 open-hand loss recovered it (−0.089).
    /// </summary>
    public const double ThreatThreshold = 0.3;

    public static double[] Features(OpponentView o, int wallRemaining, GameState? game, int roundWind)
    {
        var f = new double[FeatureCount];
        int melds = o.Melds.Count;
        double turn = o.River.Count / 18.0;
        f[0] = 1;
        f[1] = turn;
        f[2] = melds;
        f[3] = melds * turn;

        int seatWind = game is { DealerSeat: >= 0 } g && o.Seat >= 0 ? (o.Seat - g.DealerSeat + 4) % 4 : -1;
        bool yakuhai = false, allTriplets = melds > 0;
        int suit = -2; // -2 none yet, -1 mixed, 0..2 one number suit, 3 honors only
        foreach (Meld m in o.Melds)
        {
            int t = m.Tiles[0];
            if (m.Kind != MeldKind.Chii && (t >= 31 || t == 27 + seatWind || t == 27 + roundWind))
                yakuhai = true;
            if (m.Kind == MeldKind.Chii)
                allTriplets = false;
            if (Tiles.IsNumber(t))
            {
                int s = (int)Tiles.SuitOf(t);
                suit = suit == -2 || suit == s ? s : -1;
            }
        }

        f[4] = yakuhai ? 1 : 0;
        if (suit is >= 0 and <= 2 && o.River.Count > 0)
        {
            // One number suit in the melds; stronger when that suit is also missing from the river.
            int inSuit = o.River.Count(t => Tiles.IsNumber(t) && (int)Tiles.SuitOf(t) == suit);
            f[5] = 1 - (double)inSuit / o.River.Count;
        }
        f[6] = allTriplets && melds >= 2 ? 1 : 0;
        f[7] = wallRemaining >= 0 ? 1 - wallRemaining / 70.0 : 0.5;
        return f;
    }

    public static double Chance(double[] features, IReadOnlyList<double>? coefficients = null)
    {
        coefficients ??= Coefficients;
        double z = 0;
        for (int i = 0; i < features.Length && i < coefficients.Count; i++)
            z += features[i] * coefficients[i];
        return 1 / (1 + Math.Exp(-z));
    }

    /// <summary>The model's chance for a non-riichi opponent with melds; 1 in riichi; 0 for a closed silent hand.</summary>
    public static double Chance(OpponentView o, int wallRemaining, GameState? game, int roundWind)
    {
        if (o.Riichi)
            return 1.0;
        if (o.Melds.Count == 0)
            return 0;
        return Chance(Features(o, wallRemaining, game, roundWind));
    }
}
