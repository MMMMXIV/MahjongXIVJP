namespace MahjongXiv.Engine;

/// <summary>
/// Decides whether to call an opponent's discard. V1 calls pon only: chi has an unverified
/// variant-selection UI and kan would flip dora the reader cannot see yet. A pon must strictly
/// advance the hand, never open up into a declared riichi, and must keep a yaku path alive —
/// yakuhai is always live; otherwise an already-secured yakuhai set, or a dora-rich tanyao
/// route, is required (an open hand without a yaku can never win).
/// </summary>
/// <summary>Everything a pon decision may use. Optional fields are null when the caller cannot see them.</summary>
public sealed record CallInput
{
    public required int[] Counts13 { get; init; }
    public IReadOnlyList<Meld> Melds { get; init; } = Array.Empty<Meld>();
    public required int Tile { get; init; }
    public required EvalContext Context { get; init; }
    public bool OpponentRiichi { get; init; }
    public int[]? Seen { get; init; }
    public IReadOnlyList<OpponentView>? Opponents { get; init; }
    public GameState? Game { get; init; }
    public int WallRemaining { get; init; } = -1;
}

public static class CallPolicy
{
    /// <summary>The shipped pon rule, over a <see cref="CallInput"/>.</summary>
    public static bool ShouldPon(CallInput input)
        => ShouldPon(input.Counts13, input.Melds, input.Tile, input.Context, input.OpponentRiichi);

    /// <summary>
    /// The pon rule under <paramref name="options"/>. With <see cref="EvalOptions.SmartPon"/> the call is
    /// priced instead of rule-checked: the value of keeping the closed hand (riichi still possible)
    /// against the value of the open hand after the best discard, minus that discard's deal-in risk. An
    /// open hand is only worth anything through a yaku, which the tenpai values already require; far
    /// from tenpai, where yaku are not evaluated, the shipped yaku-path rule must also pass.
    /// </summary>
    public static bool ShouldPon(CallInput input, EvalOptions options)
    {
        // Never pon out of a concealed triplet: it breaks a finished set for nothing, and with a red
        // five among the copies the table asks a second question (which copies to use) that the live
        // driver does not answer.
        if (input.Tile >= 0 && input.Counts13[input.Tile] >= 3)
            return false;

        bool shipped = ShouldPon(input) || (options.EagerYakuhaiPon && EagerYakuhai(input));
        if (!options.SmartPon || input.Tile < 0 || input.Counts13[input.Tile] < 2)
            return shipped;

        var baseInput = new TurnInput
        {
            Counts14 = input.Counts13,
            Melds = input.Melds,
            Seen = input.Seen,
            Context = input.Context,
            Opponents = input.Opponents,
            Game = input.Game,
            WallRemaining = input.WallRemaining,
            Options = options,
            ThreatRivers = input.Opponents?.Where(o => o.Riichi).Select(o => o.River).ToList()
                           ?? (IReadOnlyList<IReadOnlyList<int>>)Array.Empty<IReadOnlyList<int>>(),
        };
        double pass = StrategicPolicy.PositionValue(baseInput);

        int[] after = (int[])input.Counts13.Clone();
        after[input.Tile] -= 2;
        var melds = new List<Meld>(input.Melds) { new Meld(MeldKind.Pon, [input.Tile, input.Tile, input.Tile]) };
        int[]? seen = input.Seen is null ? null : (int[])input.Seen.Clone();
        if (seen is not null)
            seen[input.Tile] = Math.Min(4, seen[input.Tile] + 1); // the called tile joins our meld

        var openCtx = input.Context with { Closed = false };
        List<ScoredDiscard> ranked = DiscardEvaluator.Rank(after, melds, seen, openCtx, options);
        if (ranked.Count == 0)
            return false;

        int bestShanten = ranked[0].Option.Shanten;
        if (bestShanten >= 2 && !shipped)
            return false;

        double best = double.NegativeInfinity;
        foreach (ScoredDiscard s in ranked.Take(4))
        {
            int[] hand = (int[])after.Clone();
            hand[s.Option.Tile]--;
            var ponInput = baseInput with { Counts14 = hand, Melds = melds, Seen = seen, Context = openCtx };
            double v = StrategicPolicy.PositionValue(ponInput) - StrategicPolicy.DiscardRisk(baseInput with { Seen = seen }, s.Option.Tile);
            best = Math.Max(best, v);
        }

        // A margin: opening the hand also gives up defensive flexibility the model does not price.
        return best > pass * 1.1 + 30;
    }

    /// <summary>
    /// Yakuhai pon that does not worsen the hand. The shipped rule needs the pon to cut the shanten,
    /// which fails whenever the yakuhai pair is the hand's only pair (the pair becomes the set, the head
    /// is lost). A strong player still calls it: it fixes the yaku and keeps the speed. Allowed up to
    /// 3-shanten and not into a riichi.
    /// </summary>
    public static bool EagerYakuhai(CallInput input)
    {
        int tile = input.Tile;
        if (tile < 0 || input.Counts13[tile] < 2 || input.OpponentRiichi || !IsYakuhai(tile, input.Context))
            return false;

        IReadOnlyList<Meld> melds = input.Melds;
        int current = ShantenCalculator.Calculate(input.Counts13, melds.Count);
        if (current > 3)
            return false;

        Span<int> after = stackalloc int[Tiles.TypeCount];
        input.Counts13.CopyTo(after);
        after[tile] -= 2;
        int best = int.MaxValue;
        for (int d = 0; d < Tiles.TypeCount; d++)
        {
            if (after[d] == 0)
                continue;
            after[d]--;
            best = Math.Min(best, ShantenCalculator.Calculate(after, melds.Count + 1));
            after[d]++;
        }

        return best <= current;
    }

    public static bool ShouldPon(
        ReadOnlySpan<int> counts13, IReadOnlyList<Meld>? melds, int tile, EvalContext ctx, bool opponentRiichi)
    {
        if (tile < 0 || counts13[tile] < 2 || opponentRiichi)
            return false;

        melds ??= Array.Empty<Meld>();
        int current = ShantenCalculator.Calculate(counts13, melds.Count);

        Span<int> after = stackalloc int[Tiles.TypeCount];
        counts13.CopyTo(after);
        after[tile] -= 2;

        int best = int.MaxValue;
        for (int d = 0; d < Tiles.TypeCount; d++)
        {
            if (after[d] == 0)
                continue;
            after[d]--;
            best = Math.Min(best, ShantenCalculator.Calculate(after, melds.Count + 1));
            after[d]++;
        }

        if (best >= current)
            return false;

        if (IsYakuhai(tile, ctx))
            return true;

        if (HasYakuhaiSet(counts13, melds, ctx))
            return true;

        // Dora-rich tanyao route: the called tile is a simple and the off-plan terminals/honors
        // are few enough to shed on the way to tenpai.
        if (!Tiles.IsNumber(tile) || Tiles.IsTerminal(tile))
            return false;

        int offPlan = 0;
        for (int t = 0; t < Tiles.TypeCount; t++)
            if (Tiles.IsTerminalOrHonor(t))
                offPlan += after[t];
        if (offPlan > Math.Max(1, best))
            return false;

        return DoraValue(counts13, melds, ctx) >= 2;
    }

    private static bool IsYakuhai(int tile, EvalContext ctx)
        => tile >= 31 || tile == 27 + ctx.SeatWind || tile == 27 + ctx.RoundWind;

    private static bool HasYakuhaiSet(ReadOnlySpan<int> counts13, IReadOnlyList<Meld> melds, EvalContext ctx)
    {
        foreach (Meld meld in melds)
            if (meld.Kind != MeldKind.Chii && IsYakuhai(meld.Tiles[0], ctx))
                return true;

        for (int t = 27; t < Tiles.TypeCount; t++)
            if (counts13[t] >= 3 && IsYakuhai(t, ctx))
                return true;

        return false;
    }

    private static int DoraValue(ReadOnlySpan<int> counts13, IReadOnlyList<Meld> melds, EvalContext ctx)
    {
        int value = 0;

        if (ctx.AkaByType is { } aka)
            for (int t = 0; t < Tiles.TypeCount && t < aka.Count; t++)
                value += aka[t];

        if (ctx.DoraIndicators is { } indicators)
        {
            foreach (int indicator in indicators)
            {
                int dora = YakuCalculator.DoraFromIndicator(indicator);
                value += counts13[dora];
                foreach (Meld meld in melds)
                    foreach (int t in meld.Tiles)
                        if (t == dora)
                            value++;
            }
        }

        return value;
    }
}
