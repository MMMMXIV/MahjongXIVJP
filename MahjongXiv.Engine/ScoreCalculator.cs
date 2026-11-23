namespace MahjongXiv.Engine;

/// <summary>
/// Which point table to use. <see cref="DomanFlat"/> is the han-only table this project has always
/// used (it assumes Doman Mahjong scores without fu). <see cref="Standard"/> is the usual Japanese
/// riichi table (fu × 2^(han+2) with the mangan limits). Which one FFXIV really uses has not been
/// confirmed against live result screens yet, so both are kept and every caller picks explicitly.
/// </summary>
public enum ScoringRules
{
    DomanFlat,
    Standard,
}

/// <summary>Rule switches whose Doman Mahjong behaviour is not confirmed; defaults are the common online-riichi choices.</summary>
public sealed record ScoringOptions
{
    public static readonly ScoringOptions Default = new();

    public ScoringRules Rules { get; init; } = ScoringRules.DomanFlat;

    /// <summary>Round 4 han 30 fu and 3 han 60 fu up to mangan.</summary>
    public bool KiriageMangan { get; init; }

    /// <summary>Count two yakuman in one hand as a double yakuman.</summary>
    public bool StackYakuman { get; init; }

    /// <summary>A pair of the double wind (seat = round) is worth 4 fu instead of 2.</summary>
    public bool DoubleWindPairFourFu { get; init; } = true;

    public int HonbaPoints { get; init; } = 300;
    public int RiichiStickPoints { get; init; } = 1000;
}

/// <summary>Score of one winning hand, before honba and riichi sticks.</summary>
public sealed record HandScore
{
    public required HandReading Reading { get; init; }
    public required int Han { get; init; }
    public required int Fu { get; init; }

    /// <summary>Basic points (fu × 2^(han+2), capped by the limit hands).</summary>
    public required int BasePoints { get; init; }

    /// <summary>Total the winner receives from the hand itself (no honba, no sticks).</summary>
    public required int Points { get; init; }

    public required bool Dealer { get; init; }
    public required bool Tsumo { get; init; }

    /// <summary>On tsumo: what the dealer pays (non-dealer win) and what each non-dealer pays.</summary>
    public int TsumoDealerPays { get; init; }
    public int TsumoNonDealerPays { get; init; }

    public string Limit { get; init; } = string.Empty;
}

/// <summary>Fu of one hand reading, under the standard rules.</summary>
public static class FuCalculator
{
    public static int Fu(HandReading reading, in WinContext ctx, ScoringOptions? options = null)
    {
        options ??= ScoringOptions.Default;

        if (reading.Form == HandForm.Chiitoitsu)
            return 25;
        if (reading.Form == HandForm.Kokushi)
            return 30; // irrelevant: yakuman

        bool pinfu = reading.IsPinfu;
        if (pinfu && ctx.Tsumo)
            return 20;

        int fu = 20;
        if (reading.Closed && !ctx.Tsumo)
            fu += 10; // menzen ron
        if (ctx.Tsumo && !pinfu)
            fu += 2;

        foreach (SetBlock b in reading.Blocks)
        {
            if (b.Kind == BlockKind.Run)
                continue;
            bool edge = Tiles.IsTerminalOrHonor(b.Tile);
            int setFu = b.Kind == BlockKind.Quad ? 8 : 2;
            if (edge)
                setFu *= 2;
            if (b.Concealed)
                setFu *= 2;
            fu += setFu;
        }

        fu += PairFu(reading.HeadTile, ctx, options);

        if (reading.Wait is YakuCalculator.WaitShape.Kanchan or YakuCalculator.WaitShape.Penchan or YakuCalculator.WaitShape.Tanki)
            fu += 2;

        fu = (fu + 9) / 10 * 10;

        // An open hand with no fu beyond the base (open "pinfu" shape) is still scored at 30.
        if (!reading.Closed && fu == 20)
            fu = 30;

        return fu;
    }

    private static int PairFu(int head, in WinContext ctx, ScoringOptions options)
    {
        if (head < 27)
            return 0;
        if (head >= 31)
            return 2;

        int fu = 0;
        if (head == 27 + ctx.SeatWind)
            fu += 2;
        if (head == 27 + ctx.RoundWind)
            fu += 2;
        if (fu == 4 && !options.DoubleWindPairFourFu)
            fu = 2;
        return fu;
    }
}

/// <summary>
/// Points for a winning hand and the payments of a hand result, under <see cref="ScoringOptions"/>.
/// Evaluates every reading of the hand and keeps the one paying the most (the highest-han reading
/// is not always it once fu count).
/// </summary>
public static class ScoreCalculator
{
    public static HandScore? Score(
        ReadOnlySpan<int> concealed, IReadOnlyList<Meld>? melds, in WinContext ctx, bool dealer, ScoringOptions? options = null)
    {
        options ??= ScoringOptions.Default;
        HandScore? best = null;
        foreach (HandReading reading in YakuCalculator.Interpretations(concealed, melds, ctx))
        {
            if (!reading.HasYaku)
                continue;
            HandScore score = ScoreReading(reading, ctx, dealer, options);
            if (best is null || score.Points > best.Points || (score.Points == best.Points && score.Han > best.Han))
                best = score;
        }

        return best;
    }

    public static HandScore ScoreReading(HandReading reading, in WinContext ctx, bool dealer, ScoringOptions? options = null)
    {
        options ??= ScoringOptions.Default;

        int han;
        if (reading.IsYakuman)
        {
            int count = options.StackYakuman ? Math.Max(1, reading.Yaku.Count) : 1;
            han = YakuCalculator.YakumanHan * count;
        }
        else
        {
            han = reading.YakuHan + reading.DoraHan;
        }

        int fu = FuCalculator.Fu(reading, ctx, options);
        (int basePoints, string limit) = options.Rules == ScoringRules.DomanFlat
            ? FlatBase(han, reading.IsYakuman, options)
            : StandardBase(han, fu, reading.IsYakuman, options);

        int points;
        int dealerPays = 0, nonDealerPays = 0;
        if (ctx.Tsumo)
        {
            if (dealer)
            {
                nonDealerPays = RoundUp100(basePoints * 2);
                points = nonDealerPays * 3;
            }
            else
            {
                dealerPays = RoundUp100(basePoints * 2);
                nonDealerPays = RoundUp100(basePoints);
                points = dealerPays + nonDealerPays * 2;
            }
        }
        else
        {
            points = RoundUp100(basePoints * (dealer ? 6 : 4));
        }

        return new HandScore
        {
            Reading = reading,
            Han = han,
            Fu = fu,
            BasePoints = basePoints,
            Points = points,
            Dealer = dealer,
            Tsumo = ctx.Tsumo,
            TsumoDealerPays = dealerPays,
            TsumoNonDealerPays = nonDealerPays,
            Limit = limit,
        };
    }

    /// <summary>
    /// Point changes per seat for a win: hand value, honba (300 per honba, split on tsumo) and the
    /// riichi sticks on the table, which all go to the winner.
    /// </summary>
    public static int[] Payments(HandScore score, int winner, int dealerSeat, int discarder, int honba, int sticks, ScoringOptions? options = null)
    {
        options ??= ScoringOptions.Default;
        var deltas = new int[4];

        if (!score.Tsumo)
        {
            int pay = score.Points + honba * options.HonbaPoints;
            deltas[discarder] -= pay;
            deltas[winner] += pay;
        }
        else
        {
            int honbaEach = honba * options.HonbaPoints / 3;
            for (int s = 0; s < 4; s++)
            {
                if (s == winner)
                    continue;
                int pay = (score.Dealer || s != dealerSeat ? score.TsumoNonDealerPays : score.TsumoDealerPays) + honbaEach;
                deltas[s] -= pay;
                deltas[winner] += pay;
            }
        }

        deltas[winner] += sticks * options.RiichiStickPoints;
        return deltas;
    }

    /// <summary>Standard basic points: fu × 2^(han+2), capped at mangan (2000) and the higher limits.</summary>
    public static (int Base, string Limit) StandardBase(int han, int fu, bool yakuman, ScoringOptions options)
    {
        if (yakuman || han >= 13)
            return (8000 * Math.Max(1, han / 13), han >= 26 ? "double yakuman" : "yakuman");
        if (han >= 11)
            return (6000, "sanbaiman");
        if (han >= 8)
            return (4000, "baiman");
        if (han >= 6)
            return (3000, "haneman");
        if (han >= 5)
            return (2000, "mangan");

        int basic = fu * (1 << (han + 2));
        if (basic >= 2000 || (options.KiriageMangan && ((han == 4 && fu == 30) || (han == 3 && fu == 60))))
            return (2000, "mangan");
        return (basic, string.Empty);
    }

    /// <summary>The han-only table of <see cref="DomanScoring"/>, expressed as basic points.</summary>
    private static (int Base, string Limit) FlatBase(int han, bool yakuman, ScoringOptions options)
    {
        int nonDealerRon = DomanScoring.Points(han, dealer: false, yakuman);
        int basePoints = nonDealerRon / 4;
        string limit = yakuman || han >= 13 ? "yakuman" : han >= 11 ? "sanbaiman" : han >= 8 ? "baiman" : han >= 6 ? "haneman" : han >= 4 ? "mangan" : string.Empty;
        return (basePoints, limit);
    }

    private static int RoundUp100(int points) => (points + 99) / 100 * 100;
}
