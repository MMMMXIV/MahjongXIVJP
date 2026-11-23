namespace MahjongXiv.Engine;

/// <summary>Table context the value ranking needs beyond the tiles: winds, dealer, dora, held aka.</summary>
public sealed record EvalContext
{
    public int SeatWind { get; init; }
    public int RoundWind { get; init; }
    public bool Dealer { get; init; }
    public bool Closed { get; init; } = true;
    public IReadOnlyList<int>? DoraIndicators { get; init; }

    /// <summary>Red-five copies held, per tile type (only the 5m/5p/5s entries can be non-zero).</summary>
    public IReadOnlyList<int>? AkaByType { get; init; }

    /// <summary>Point table the value estimates use. Null keeps the shipped han-only Doman table.</summary>
    public ScoringOptions? Scoring { get; init; }
}

public readonly record struct ScoredDiscard(DiscardOption Option, long Value);

/// <summary>
/// Tunable knobs of the discard ranking, so the simulator can A/B a variant against the shipped policy.
/// Defaults are the shipped policy; <see cref="Legacy"/> is the one-step ranking the lookaheads replaced.
/// Both lookaheads together measured +362 ± 61 points per match over Legacy (simulator ab, 4000 matches,
/// seed 11, 2026-10-14); the 1-shanten EV lookahead alone +198 ± 45.
/// </summary>
public sealed record EvalOptions
{
    public static readonly EvalOptions Default = new();

    public static readonly EvalOptions Legacy = new() { ShapeLookahead = false, TenpaiLookahead = false };

    /// <summary>
    /// Above tenpai, weight each accepted draw by the acceptance it leads to (after the best follow-up
    /// discard) instead of counting every draw the same: prefers shapes whose next step is wide, e.g.
    /// a 1-shanten that becomes a two-sided wait over one that becomes a closed wait.
    /// </summary>
    public bool ShapeLookahead { get; init; } = true;

    /// <summary>The follow-up acceptance that counts as "average": a draw leading to this much scores like one plain ukeire tile.</summary>
    public int ShapeReference { get; init; } = 6;

    /// <summary>
    /// At 1-shanten, value each discard by Σ over accepted draws of live copies × the exact tenpai EV
    /// (see <see cref="DiscardEvaluator"/>) of the best follow-up discard. Prices wait width, wait
    /// value and yaku together: a draw that only reaches a yakuless open tenpai is worth nothing.
    /// </summary>
    public bool TenpaiLookahead { get; init; } = true;

    // ---- Strategic layer (StrategicPolicy). All off in Default, so the shipped policy is unchanged. ----

    /// <summary>Everything in the strategic layer on: the candidate configuration for A/B runs.</summary>
    public static readonly EvalOptions Strategic = new()
    {
        PushFold = true,
        RiichiJudgement = true,
        PlacementAware = true,
    };

    /// <summary>
    /// Against a riichi or a well-advanced open hand, compare pushing (win chance × value − deal-in
    /// chance × loss over the remaining draws) with folding, per candidate discard, instead of always
    /// folding from 1-shanten and always pushing at tenpai.
    /// </summary>
    public bool PushFold { get; init; }

    /// <summary>Choose riichi or dama by comparing their expected values instead of riichi on every live tenpai.</summary>
    public bool RiichiJudgement { get; init; }

    /// <summary>Value outcomes by the expected final placement (score plus uma) instead of raw points.</summary>
    public bool PlacementAware { get; init; }

    /// <summary>Use the strategic deal-in estimate in pon decisions too (reserved; see CallPolicy).</summary>
    public bool SmartPon { get; init; }

    /// <summary>Multiplier on the modelled deal-in probabilities (tuning knob).</summary>
    public double DealInScale { get; init; } = 1.0;

    public bool AnyStrategic => PushFold || RiichiJudgement || PlacementAware;
}

/// <summary>
/// Ranks discards by shanten first, then by expected value instead of raw acceptance. At tenpai the
/// value is exact: for every winning tile, remaining copies × the points the completed hand pays
/// (riichi assumed for a closed hand, since the bot declares it; a yakuless open wait is worth 0 —
/// it can never win). At 1-shanten it looks one draw ahead to that tenpai value (see
/// <see cref="EvalOptions.TenpaiLookahead"/>). Further out the value is two-step acceptance
/// weighted by what the hand keeps: dora, aka, and yakuhai pairs.
/// </summary>
public static class DiscardEvaluator
{
    private const int UkeireWeight = 16;
    private const int DoraWeight = 40;
    private const int YakuhaiPairWeight = 25;

    public static List<ScoredDiscard> Rank(
        ReadOnlySpan<int> counts14, IReadOnlyList<Meld>? melds, int[]? seen, EvalContext ctx, EvalOptions? opts = null)
    {
        melds ??= Array.Empty<Meld>();
        opts ??= EvalOptions.Default;
        List<DiscardOption> options = UkeireCalculator.BestDiscards(counts14, melds.Count, seen);
        int bestShanten = options.Count > 0 ? options[0].Shanten : 0;

        var ranked = new List<ScoredDiscard>(options.Count);
        Span<int> hand13 = stackalloc int[Tiles.TypeCount];

        // Discarding A then drawing X then cutting B reaches the same tenpai as cutting B first: the
        // 1-shanten lookahead revisits each tenpai many times, so its value is memoized per call.
        Dictionary<UInt128, long>? tenpaiMemo = null;

        foreach (DiscardOption option in options)
        {
            counts14.CopyTo(hand13);
            hand13[option.Tile]--;

            int akaRemaining = AkaAfterDiscard(counts14, option.Tile, ctx.AkaByType);
            // Lookahead only matters among the best-shanten discards: the sort puts the rest below them anyway.
            long acceptance = opts.ShapeLookahead && option.Shanten >= 1 && option.Shanten == bestShanten
                ? ShapeAcceptance(hand13, melds.Count, option, seen, opts.ShapeReference)
                : option.UkeireCount * (long)UkeireWeight;

            long value = option.Shanten == 0
                ? TenpaiValue(hand13, melds, option, akaRemaining, ctx)
                : ProgressValue(hand13, acceptance, akaRemaining, ctx);

            // The EV term dwarfs the progress value, which stays only as a tie-break (e.g. every draw yakuless).
            if (opts.TenpaiLookahead && option.Shanten == 1 && bestShanten == 1)
                value += OneShantenValue(hand13, melds, option, seen, akaRemaining, ctx, tenpaiMemo ??= new());

            ranked.Add(new ScoredDiscard(option, value));
        }

        ranked.Sort(static (a, b) => a.Option.Shanten != b.Option.Shanten
            ? a.Option.Shanten - b.Option.Shanten
            : b.Value.CompareTo(a.Value));

        return ranked;
    }

    /// <summary>Σ over winning tiles: live copies × points paid. Exact EV ranking for tenpai hands.</summary>
    private static long TenpaiValue(
        ReadOnlySpan<int> hand13, IReadOnlyList<Meld> melds, in DiscardOption option, int akaRemaining, EvalContext ctx)
    {
        Span<int> hand14 = stackalloc int[Tiles.TypeCount];
        long value = 0;

        foreach (AcceptTile accept in option.Accepts)
        {
            hand13.CopyTo(hand14);
            hand14[accept.Tile]++;

            var winCtx = new WinContext
            {
                WinTile = accept.Tile,
                Tsumo = false,
                Riichi = ctx.Closed,
                SeatWind = ctx.SeatWind,
                RoundWind = ctx.RoundWind,
                AkaCount = akaRemaining,
                DoraIndicators = ctx.DoraIndicators,
            };

            WinResult? result = YakuCalculator.Evaluate(hand14, melds, winCtx);
            if (result is null || !result.HasYaku)
                continue; // a yakuless wait can never win

            value += accept.Remaining * (long)DomanScoring.Points(result.TotalHan, ctx.Dealer, result.IsYakuman);
        }

        return value;
    }

    /// <summary>
    /// Two-step acceptance on the <see cref="UkeireWeight"/> scale: Σ over accepted draws of live copies ×
    /// the acceptance left after the best follow-up discard, normalized so a follow-up of
    /// <paramref name="reference"/> tiles scores like one plain ukeire tile.
    /// </summary>
    private static long ShapeAcceptance(ReadOnlySpan<int> hand13, int meldCount, in DiscardOption option, int[]? seen, int reference)
    {
        Span<int> hand14 = stackalloc int[Tiles.TypeCount];
        long total = 0;

        foreach (AcceptTile accept in option.Accepts)
        {
            hand13.CopyTo(hand14);
            hand14[accept.Tile]++;
            int next = UkeireCalculator.BestFollowUpCount(hand14, meldCount, option.Shanten - 1, seen, accept.Tile);
            total += accept.Remaining * (long)next;
        }

        return total * UkeireWeight / Math.Max(1, reference);
    }

    /// <summary>Σ over accepted draws: live copies × the best tenpai EV one follow-up discard reaches.</summary>
    private static long OneShantenValue(
        ReadOnlySpan<int> hand13, IReadOnlyList<Meld> melds, in DiscardOption option, int[]? seen, int akaRemaining, EvalContext ctx,
        Dictionary<UInt128, long> memo)
    {
        Span<int> hand = stackalloc int[Tiles.TypeCount];
        int[]? seenAfter = seen is null ? null : (int[])seen.Clone();
        long total = 0;

        foreach (AcceptTile accept in option.Accepts)
        {
            hand13.CopyTo(hand);
            hand[accept.Tile]++;
            if (seenAfter is not null)
                seenAfter[accept.Tile]++;

            long best = 0;
            for (int d = 0; d < Tiles.TypeCount; d++)
            {
                if (hand[d] == 0)
                    continue;

                hand[d]--;
                // The value depends on the hand, the extra seen copy (the draw) and the aka held.
                UInt128 key = HandKey.Pack(hand, accept.Tile, akaRemaining);
                if (!memo.TryGetValue(key, out long tenpaiValue))
                {
                    tenpaiValue = -1;
                    if (ShantenCalculator.Calculate(hand, melds.Count) == 0)
                    {
                        Acceptance waits = UkeireCalculator.Accept(hand, melds.Count, seenAfter);
                        var tenpai = new DiscardOption(d, 0, waits.Types, waits.Count, waits.Tiles);
                        tenpaiValue = TenpaiValue(hand, melds, tenpai, akaRemaining, ctx);
                    }
                    memo[key] = tenpaiValue;
                }
                best = Math.Max(best, tenpaiValue);
                hand[d]++;
            }

            if (seenAfter is not null)
                seenAfter[accept.Tile]--;
            total += accept.Remaining * best;
        }

        return total;
    }

    /// <summary>Acceptance plus what the kept tiles are worth: dora, aka, and yakuhai pairs.</summary>
    private static long ProgressValue(ReadOnlySpan<int> hand13, long acceptance, int akaRemaining, EvalContext ctx)
    {
        long held = akaRemaining;

        if (ctx.DoraIndicators is { } indicators)
            foreach (int indicator in indicators)
                held += hand13[YakuCalculator.DoraFromIndicator(indicator)];

        long pairs = 0;
        for (int t = 31; t <= 33; t++)
            if (hand13[t] >= 2)
                pairs++;
        if (hand13[27 + ctx.SeatWind] >= 2)
            pairs++;
        if (ctx.RoundWind != ctx.SeatWind && hand13[27 + ctx.RoundWind] >= 2)
            pairs++;

        return acceptance + held * DoraWeight + pairs * YakuhaiPairWeight;
    }

    /// <summary>
    /// Red fives surviving the discard. The actuator discards a plain copy when one exists, so an
    /// aka is only lost when every remaining copy of that type is red.
    /// </summary>
    private static int AkaAfterDiscard(ReadOnlySpan<int> counts14, int discard, IReadOnlyList<int>? akaByType)
    {
        if (akaByType is null)
            return 0;

        int total = 0;
        for (int t = 0; t < Tiles.TypeCount && t < akaByType.Count; t++)
            total += akaByType[t];

        int akaOfType = discard < akaByType.Count ? akaByType[discard] : 0;
        int plainCopies = counts14[discard] - akaOfType;
        return plainCopies >= 1 ? total : total - 1;
    }
}
