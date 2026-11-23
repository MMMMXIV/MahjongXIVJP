namespace MahjongXiv.Engine;

/// <summary>
/// Deal-in probability of one discard against one opponent who is (or may be) tenpai. The estimate is
/// a sum over the wait shapes that could take the tile — ryanmen from either side, kanchan, penchan,
/// shanpon, tanki — each scaled by whether the tiles that shape needs are still live, and removed when
/// the opponent's own river makes it furiten (suji, genbutsu). With every shape live the sums land on
/// the usual published deal-in rates against a riichi: about 12-13% for a non-suji 4-6, 10% for 3/7,
/// 8% for 2/8, 6% for 1/9, around 4% for a two-sided suji 4-6, 2% for a suji terminal, 5% for a live
/// honor. Only public information is used.
/// </summary>
public static class DefenseModel
{
    private const double Ryanmen = 0.045;   // per side
    private const double Kanchan = 0.020;
    private const double Penchan = 0.015;
    private const double ShanponNumber = 0.012;
    private const double TankiNumber = 0.005;
    private const double ShanponHonor = 0.035;
    private const double TankiHonor = 0.015;

    /// <summary>Probability that discarding <paramref name="tile"/> deals into <paramref name="opponent"/>, given that they are tenpai.</summary>
    public static double DealIn(int tile, OpponentView opponent, ReadOnlySpan<int> seen, IReadOnlyList<OpponentView>? everyone = null)
    {
        if (IsSafe(tile, opponent, everyone))
            return 0;

        IReadOnlyList<int> river = opponent.River;
        int live = 4 - seen[tile]; // copies the opponent could hold (ours are in seen)

        if (Tiles.IsHonor(tile))
        {
            double p = 0;
            if (live >= 2)
                p += ShanponHonor * (live == 2 ? 0.45 : 1.0);
            if (live >= 1)
                p += TankiHonor;
            return p;
        }

        int rank = Tiles.Rank(tile);
        double total = 0;

        // Ryanmen with the block above (t+1, t+2): waits t and t+3; void if t+3 is in their river.
        if (rank <= 6 && !river.Contains(tile + 3))
            total += Ryanmen * Avail(seen, tile + 1) * Avail(seen, tile + 2);
        // Ryanmen with the block below (t-2, t-1): waits t-3 and t.
        if (rank >= 4 && !river.Contains(tile - 3))
            total += Ryanmen * Avail(seen, tile - 1) * Avail(seen, tile - 2);
        // Penchan: 1-2 waits 3, 8-9 waits 7.
        if (rank == 3)
            total += Penchan * Avail(seen, tile - 1) * Avail(seen, tile - 2);
        if (rank == 7)
            total += Penchan * Avail(seen, tile + 1) * Avail(seen, tile + 2);
        // Kanchan: t-1 and t+1.
        if (rank is >= 2 and <= 8)
            total += Kanchan * Avail(seen, tile - 1) * Avail(seen, tile + 1);
        if (live >= 2)
            total += ShanponNumber * (live == 2 ? 0.45 : 1.0);
        if (live >= 1)
            total += TankiNumber;

        return total;
    }

    /// <summary>How likely a block tile is to be in the opponent's hand, from how many copies are still unseen.</summary>
    private static double Avail(ReadOnlySpan<int> seen, int tile)
        => (4 - seen[tile]) switch
        {
            <= 0 => 0,
            1 => 0.35,
            2 => 0.7,
            _ => 1.0,
        };

    /// <summary>
    /// Genbutsu: the tile is in their river (furiten), or — for a riichi player — someone discarded it
    /// after the declaration and they did not win on it. Turn order across rivers is approximated by
    /// river length: a discard at another river's index beyond the declaration index came later.
    /// </summary>
    public static bool IsSafe(int tile, OpponentView opponent, IReadOnlyList<OpponentView>? everyone)
    {
        if (opponent.River.Contains(tile))
            return true;

        if (!opponent.Riichi || opponent.RiichiDiscardIndex < 0 || everyone is null)
            return false;

        foreach (OpponentView other in everyone)
        {
            if (other.Seat == opponent.Seat)
                continue;
            for (int i = opponent.RiichiDiscardIndex + 1; i < other.River.Count; i++)
                if (other.River[i] == tile)
                    return true;
        }

        return false;
    }

    /// <summary>
    /// Chance that a non-riichi opponent is tenpai. Riichi = 1; an open hand grows with its melds and
    /// with the turn; a closed silent hand is ignored (its dama chance is not observable).
    /// </summary>
    public static double TenpaiChance(OpponentView opponent, int wallRemaining)
    {
        if (opponent.Riichi)
            return 1.0;

        int melds = opponent.Melds.Count;
        if (melds < 2)
            return 0;

        double late = wallRemaining is >= 0 and < 30 ? 0.15 : 0;
        return Math.Min(0.8, 0.25 + 0.15 * (melds - 2) + late);
    }
}

/// <summary>
/// Placement-aware value of a score change. The utility of a final position is the player's score plus
/// the expected placement bonus (uma); with hands still to play, every pairwise gap is blurred by a normal
/// distribution whose spread grows with the hands left, so the bonus only bites near the end and when gaps
/// are small. Without placement awareness the utility is just the score.
/// </summary>
public static class PlacementModel
{
    /// <summary>Value of each placement, 1st..4th: linear in rank, so maximizing it minimizes the average placement.</summary>
    public static readonly int[] Uma = [30000, 10000, -10000, -30000];

    /// <summary>
    /// Weight of the raw score next to the placement term. The blurred placement term already carries
    /// what points are worth for future placement; the small direct weight keeps decisions sensible
    /// when the placement is settled (e.g. a large lead in the last hand).
    /// </summary>
    public const double ScoreWeight = 0.25;

    /// <summary>Standard deviation of one pairwise score gap over one more hand.</summary>
    private const double GapSigmaPerHand = 5500;

    public static double Utility(ReadOnlySpan<int> scores, int self, int handsAfter)
    {
        // P(opponent j finishes ahead of self) for each opponent, then the Poisson-binomial rank distribution.
        // The outcome being valued is the score after the current hand, so with no hands after it the
        // standings are final; a small floor keeps the function smooth.
        double sigma = GapSigmaPerHand * Math.Sqrt(handsAfter) + 400;
        Span<double> ahead = stackalloc double[3];
        int k = 0;
        for (int j = 0; j < 4; j++)
        {
            if (j == self)
                continue;
            // Ties go to the lower seat index (seat order from the first dealer).
            double gap = scores[j] - scores[self] + (j < self ? 0.5 : -0.5);
            ahead[k++] = Phi(gap / sigma);
        }

        Span<double> dist = stackalloc double[4];
        dist[0] = 1;
        for (int i = 0; i < 3; i++)
        {
            for (int r = i + 1; r >= 1; r--)
                dist[r] = dist[r] * (1 - ahead[i]) + dist[r - 1] * ahead[i];
            dist[0] *= 1 - ahead[i];
        }

        double uma = 0;
        for (int r = 0; r < 4; r++)
            uma += dist[r] * Uma[r];
        return ScoreWeight * scores[self] + uma;
    }

    /// <summary>Standard normal CDF (Abramowitz-Stegun 7.1.26 erf approximation).</summary>
    public static double Phi(double x)
    {
        double z = Math.Abs(x) / Math.Sqrt(2);
        double t = 1 / (1 + 0.3275911 * z);
        double erf = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-z * z);
        return x >= 0 ? 0.5 * (1 + erf) : 0.5 * (1 - erf);
    }
}

/// <summary>
/// Chance of winning the hand from a given position, and the chance of the hand ending some other way,
/// as a turn-by-turn process over the draws left. The per-turn rates are simple and documented so the
/// simulator can check them: a draw advances the hand with probability (useful tiles)/(unseen tiles);
/// at tenpai a turn wins on our draw or on one of three opponent discards (scaled by how freely they
/// discard into us); the hand ends otherwise at a hazard that rises with every riichi on the table.
/// </summary>
public static class WinModel
{
    public readonly record struct Outcome(double Win, double DealIn, double TenpaiAtEnd);

    /// <param name="shanten">Shanten after the discard (0 = tenpai).</param>
    /// <param name="useful">Live tiles that reduce the shanten now (the waits at tenpai).</param>
    /// <param name="tenpaiWaits">Expected live wait tiles once tenpai (used from 1-shanten on).</param>
    /// <param name="unseen">Tiles not visible to us (live wall, dead wall and opponents' hands).</param>
    /// <param name="draws">Our own draws left.</param>
    /// <param name="ronRate">How freely opponents discard our waits (0..1); lower after we riichi.</param>
    /// <param name="ronAllowed">False for a yakuless dama wait or furiten: only our own draws can win.</param>
    /// <param name="otherEndHazard">Per-turn chance the hand ends by someone else's win.</param>
    /// <param name="dealInHazard">Per-turn chance our future discards deal in while we keep pushing.</param>
    public static Outcome Run(
        int shanten, double useful, double tenpaiWaits, double unseen, int draws,
        double ronRate, bool ronAllowed, double otherEndHazard, double dealInHazard)
    {
        unseen = Math.Max(unseen, 1);
        double alive = 1;
        double win = 0, dealIn = 0;

        // Probability mass by shanten state, starting at the given shanten.
        int states = Math.Max(0, shanten) + 1;
        Span<double> at = stackalloc double[states];
        at[states - 1] = 1;

        for (int turn = 0; turn < draws && alive > 1e-4; turn++)
        {
            Span<double> next = stackalloc double[states];
            for (int s = 0; s < states; s++)
            {
                double mass = at[s];
                if (mass <= 0)
                    continue;

                if (s == 0)
                {
                    double waits = shanten == 0 ? useful : tenpaiWaits;
                    double tsumo = waits / unseen;
                    double ron = ronAllowed ? Math.Min(0.9, 3 * waits / unseen * ronRate) : 0;
                    double h = 1 - (1 - tsumo) * (1 - ron);
                    win += mass * h;
                    dealIn += mass * (1 - h) * dealInHazard;
                    next[0] += mass * (1 - h) * (1 - dealInHazard) * (1 - otherEndHazard);
                }
                else
                {
                    double tiles = s == shanten ? useful : DefaultUseful(s);
                    double progress = Math.Min(0.95, tiles / unseen);
                    dealIn += mass * dealInHazard;
                    double stay = mass * (1 - dealInHazard) * (1 - otherEndHazard);
                    next[s - 1] += stay * progress;
                    next[s] += stay * (1 - progress);
                }
            }

            at = next.ToArray();
            alive = 0;
            foreach (double v in at)
                alive += v;
        }

        return new Outcome(win, dealIn, at[0]);
    }

    /// <summary>Typical useful-tile count further from tenpai, where the exact acceptance is not computed.</summary>
    private static double DefaultUseful(int shanten) => shanten == 1 ? 18 : 30;
}

/// <summary>What a tenpai wait is worth: live winning tiles, and the average points with and without riichi.</summary>
public readonly record struct TenpaiInfo(
    double Waits, double RiichiRon, double RiichiTsumo, double DamaRon, double DamaTsumo, bool DamaRonPossible, bool Furiten);

/// <summary>
/// The strategic layer on top of <see cref="DiscardEvaluator"/>. The value ranking still proposes the
/// offensive discards; this layer prices each candidate against the table: the chance and cost of
/// dealing in now and later, the chance and value of winning before the hand ends, riichi against dama,
/// and (optionally) what the outcomes do to the expected final placement.
/// </summary>
public static class StrategicPolicy
{
    private const int OffenseCandidates = 5;
    private const int SafeCandidates = 4;

    /// <summary>Average value of a deal-in, by scoring table: riichi hands are mostly 3+ han.</summary>
    private static double ThreatValue(OpponentView opp, ScoringRules rules)
    {
        double child = rules == ScoringRules.Standard ? 5800 : 6800;
        double v = opp.IsDealer ? child * 1.5 : child;
        return opp.Riichi ? v : v * 0.7;
    }

    public static TurnDecision Decide(TurnInput input, List<ScoredDiscard> ranked)
    {
        EvalOptions opts = input.Options;
        int[] seen = input.Seen ?? input.Counts14;
        int wall = input.WallRemaining >= 0 ? input.WallRemaining : 40;
        int draws = Math.Max(0, (wall + 3) / 4);
        double unseen = Math.Max(1, 136 - seen.Sum());
        ScoringOptions scoring = input.Context.Scoring ?? ScoringOptions.Default;

        // Threats with their tenpai chance and the utility lost when dealing into them.
        var util = new Utility(input, opts.PlacementAware);
        var threats = Threats(input, util, scoring, wall);
        (double otherEnd, double futureDanger) = Hazards(threats, opts);

        // Nobody to defend against (or push/fold off): the value ranking's discard stands; only the
        // riichi call is re-judged.
        if (threats.Count == 0 || !opts.PushFold)
        {
            TurnDecision legacy = TurnPolicy.Legacy(input, ranked);
            if (!opts.RiichiJudgement || legacy.Discard < 0 || !input.RiichiAvailable || !input.Context.Closed)
                return legacy;
            DiscardOption pick = ranked.First(r => r.Option.Tile == legacy.Discard).Option;
            if (pick.Shanten != 0)
                return legacy;
            var memo0 = new Dictionary<UInt128, TenpaiInfo>();
            TenpaiInfo info0 = Tenpai(input, pick.Tile, seen, scoring, memo0);
            (double dama0, double riichi0) = TenpaiValues(info0, input, util, threats, scoring, unseen, draws, otherEnd, futureDanger, opts);
            bool declare = info0.Waits > 0 && riichi0 >= dama0;
            return legacy with
            {
                WinChance = declare ? lastRiichiWin : lastDamaWin,
                DeclareRiichi = declare,
                Reason = $"{legacy.Reason.Replace(" + riichi", string.Empty)}{(declare ? " + riichi" : ", dama")} (ev dama {dama0:F0} / riichi {riichi0:F0})",
                Kind = declare ? legacy.Kind : TurnReason.Dama,
            };
        }

        // Candidate discards: the best offensive ones, plus the safest ones when someone is a threat.
        var candidates = new List<ScoredDiscard>();
        foreach (ScoredDiscard s in ranked)
        {
            if (candidates.Count >= OffenseCandidates)
                break;
            candidates.Add(s);
        }

        double DangerLoss(int tile)
        {
            double loss = 0;
            foreach (var t in threats)
                loss += t.Tenpai * DefenseModel.DealIn(tile, t.Opp, seen, input.Opponents) * opts.DealInScale * t.Loss;
            return loss;
        }

        if (threats.Count > 0 && opts.PushFold)
        {
            foreach (ScoredDiscard s in ranked.OrderBy(s => DangerLoss(s.Option.Tile)).Take(SafeCandidates))
                if (!candidates.Any(c => c.Option.Tile == s.Option.Tile))
                    candidates.Add(s);
        }

        bool canRiichi = input.RiichiAvailable && input.Context.Closed && opts.RiichiJudgement;
        bool legacyRiichi = input.RiichiAvailable && input.Context.Closed && !opts.RiichiJudgement;
        int baseHan = 1 + HeldDora(input);
        var memo = new Dictionary<UInt128, TenpaiInfo>();

        TurnDecision best = default;
        double bestValue = double.NegativeInfinity;

        foreach (ScoredDiscard cand in candidates)
        {
            DiscardOption opt = cand.Option;
            double nowLoss = threats.Count > 0 ? DangerLoss(opt.Tile) : 0;

            // Furiten-aware tenpai info for this discard.
            double pushValue;
            bool riichi = false;
            TurnReason kind = TurnReason.Push;

            if (opt.Shanten == 0)
            {
                TenpaiInfo info = Tenpai(input, opt.Tile, seen, scoring, memo);
                (double damaWin, double riichiValue) = TenpaiValues(info, input, util, threats, scoring, unseen, draws, otherEnd, futureDanger, opts);

                if (legacyRiichi)
                {
                    riichi = info.Waits > 0;
                    pushValue = riichi ? riichiValue : damaWin;
                }
                else if (canRiichi && info.Waits > 0 && riichiValue >= damaWin)
                {
                    riichi = true;
                    pushValue = riichiValue;
                }
                else
                {
                    pushValue = damaWin;
                    if (canRiichi)
                        kind = TurnReason.Dama;
                }
            }
            else
            {
                double tenpaiWaits = opt.Shanten == 1 ? 5.5 : 5;
                double avgValue = opt.Shanten == 1
                    ? OneShantenValue(input, opt, seen, scoring, memo, out tenpaiWaits)
                    : EstimatePoints(baseHan, input.Context.Dealer, scoring);
                var o = WinModel.Run(opt.Shanten, opt.UkeireCount, tenpaiWaits, unseen, draws, 0.6, true, otherEnd, futureDanger);
                pushValue = o.Win * util.Gain(avgValue + Sticks(input)) - o.DealIn * AvgLoss(threats, util, scoring);
            }

            double total = pushValue - nowLoss;

            // Folding with this tile: no win; the remaining risk is a few more discards from a shrinking safe set.
            if (threats.Count > 0 && opts.PushFold && !riichi)
            {
                double foldRisk = Math.Min(0.25, draws * 0.004 * threats.Sum(t => t.Tenpai)) * AvgLoss(threats, util, scoring);
                double fold = -nowLoss - foldRisk;
                if (fold > total)
                {
                    total = fold;
                    kind = TurnReason.Fold;
                }
            }

            // Ties go to the value ranking's order (earlier candidates first).
            if (total > bestValue + 1e-6)
            {
                bestValue = total;
                best = new TurnDecision(opt.Tile, riichi, Describe(kind, riichi, total)) { Kind = riichi ? TurnReason.BestValue : kind };
            }
        }

        // Never leave tenpai for a furiten wait when an equal non-furiten tenpai exists (the legacy dodge).
        if (best.Discard >= 0 && best.Kind != TurnReason.Fold)
        {
            DiscardOption chosen = ranked.First(r => r.Option.Tile == best.Discard).Option;
            if (chosen.Shanten == 0 && TurnPolicy.IsFuriten(chosen, input.OwnRiver))
            {
                foreach (ScoredDiscard s in ranked)
                {
                    if (s.Option.Shanten != 0)
                        break;
                    if (!TurnPolicy.IsFuriten(s.Option, input.OwnRiver))
                        return best with { Discard = s.Option.Tile, Reason = "furiten dodge", Kind = TurnReason.FuritenDodge };
                }
            }
        }

        return best;
    }

    /// <summary>Expected utility of a tenpai played dama (can still fold later) and with riichi (cannot).</summary>
    [ThreadStatic] private static double lastDamaWin, lastRiichiWin;

    private static (double Dama, double Riichi) TenpaiValues(
        TenpaiInfo info, TurnInput input, Utility util, List<(OpponentView Opp, double Tenpai, double Loss)> threats,
        ScoringOptions scoring, double unseen, int draws, double otherEnd, double futureDanger, EvalOptions opts)
    {
        double avgLoss = AvgLoss(threats, util, scoring);
        bool damaRon = info.DamaRonPossible && !info.Furiten;

        var d = WinModel.Run(0, info.Waits, info.Waits, unseen, draws, opts.DamaRonRate, damaRon, otherEnd, futureDanger * 0.6);
        lastDamaWin = d.Win;
        lastRiichiWin = -1;
        double dama = d.Win * util.Gain(MixedValue(info.DamaRon, info.DamaTsumo, damaRon) + Sticks(input))
                      - d.DealIn * avgLoss
                      + d.TenpaiAtEnd * util.Gain(1000) * 0.6;

        double riichi = double.NegativeInfinity;
        if (input.Context.Closed && input.RiichiAvailable)
        {
            double hazard = Math.Max(futureDanger, 0.012 * opts.DealInScale); // cannot fold once declared
            // A riichi makes opponents defend: fewer rons for us, but also fewer of their wins.
            var r = WinModel.Run(0, info.Waits, info.Waits, unseen, draws, opts.RiichiRonRate, !info.Furiten, otherEnd * 0.8, hazard);
            lastRiichiWin = r.Win;
            double value = MixedValue(info.RiichiRon, info.RiichiTsumo, !info.Furiten) * UraFactor(info.RiichiRon);
            // The stick comes back on a win, so it only costs when the hand is not won.
            riichi = r.Win * util.Gain(value + Sticks(input))
                     - (1 - r.Win) * util.Loss(1000)
                     - r.DealIn * avgLoss
                     + r.TenpaiAtEnd * util.Gain(1000) * 0.6;
        }

        return (dama, riichi);
    }

    private static string Describe(TurnReason kind, bool riichi, double value)
        => $"{kind switch { TurnReason.Fold => "fold", TurnReason.Dama => "dama", _ => "push" }}{(riichi ? " + riichi" : "")} (ev {value:F0})";

    private static double Sticks(TurnInput input)
        => input.Game is { } g ? g.RiichiSticks * 1000 + g.Honba * 300 : 0;

    private static double AvgLoss(List<(OpponentView Opp, double Tenpai, double Loss)> threats, Utility util, ScoringOptions scoring)
    {
        if (threats.Count == 0)
            return util.Loss(scoring.Rules == ScoringRules.Standard ? 5000 : 6000) * 0.5; // a future attacker
        double sum = 0, w = 0;
        foreach (var t in threats)
        {
            sum += t.Tenpai * t.Loss;
            w += t.Tenpai;
        }
        return sum / Math.Max(w, 1e-9);
    }

    /// <summary>Average of ron and tsumo value; tsumo is about 40% of wins.</summary>
    private static double MixedValue(double ron, double tsumo, bool ronAllowed)
        => ronAllowed ? 0.6 * ron + 0.4 * tsumo : tsumo;

    /// <summary>Expected ura dora: roughly one in three riichi wins gains a han, which matters most below mangan.</summary>
    private static double UraFactor(double value) => value < 7000 ? 1.25 : 1.08;

    private static int HeldDora(TurnInput input)
    {
        int n = 0;
        if (input.Context.AkaByType is { } aka)
            foreach (int a in aka)
                n += a;
        if (input.Context.DoraIndicators is { } ind)
            foreach (int i in ind)
                n += input.Counts14[YakuCalculator.DoraFromIndicator(i)];
        return n;
    }

    /// <summary>A rough ron value for a hand of <paramref name="han"/> han (30 fu under the standard table).</summary>
    public static double EstimatePoints(int han, bool dealer, ScoringOptions scoring)
    {
        han = Math.Clamp(han, 1, 13);
        if (scoring.Rules == ScoringRules.DomanFlat)
            return DomanScoring.Points(han, dealer);
        (int basePoints, _) = ScoreCalculator.StandardBase(han, 30, false, scoring);
        int points = basePoints * (dealer ? 6 : 4);
        return (points + 99) / 100 * 100;
    }

    /// <summary>Waits and values of the tenpai left after discarding <paramref name="discard"/>.</summary>
    private static TenpaiInfo Tenpai(TurnInput input, int discard, int[] seen, ScoringOptions scoring, Dictionary<UInt128, TenpaiInfo> memo)
    {
        Span<int> hand = stackalloc int[Tiles.TypeCount];
        input.Counts14.CopyTo(hand);
        hand[discard]--;
        int aka = AkaAfter(input, discard);
        return TenpaiOf(hand, input, seen, scoring, memo, aka, discard, extraSeen: -1);
    }

    private static TenpaiInfo TenpaiOf(
        ReadOnlySpan<int> hand13, TurnInput input, int[] seen, ScoringOptions scoring, Dictionary<UInt128, TenpaiInfo> memo,
        int aka, int discard, int extraSeen)
    {
        UInt128 key = HandKey.Pack(hand13, discard + 1, extraSeen + 1) ^ ((UInt128)(uint)aka << 120);
        if (memo.TryGetValue(key, out TenpaiInfo cached))
            return cached;

        int melds = input.Melds.Count;
        Span<int> hand = stackalloc int[Tiles.TypeCount];
        hand13.CopyTo(hand);
        bool dealer = input.Context.Dealer;

        double waits = 0, rr = 0, rt = 0, dr = 0, dt = 0, damaRonWaits = 0;
        bool furiten = false;
        for (int t = 0; t < Tiles.TypeCount; t++)
        {
            if (hand[t] >= 4)
                continue;
            hand[t]++;
            bool complete = ShantenCalculator.Calculate(hand, melds) == ShantenCalculator.Complete;
            hand[t]--;
            if (!complete)
                continue;

            if (t == discard || input.OwnRiver.Contains(t))
                furiten = true;

            int live = 4 - seen[t] - (t == extraSeen ? 1 : 0);
            if (live <= 0)
                continue;

            hand[t]++;
            double pRr = Points(hand, input, t, tsumo: false, riichi: true, aka, scoring, dealer);
            double pRt = Points(hand, input, t, tsumo: true, riichi: true, aka, scoring, dealer);
            double pDr = Points(hand, input, t, tsumo: false, riichi: false, aka, scoring, dealer);
            double pDt = Points(hand, input, t, tsumo: true, riichi: false, aka, scoring, dealer);
            hand[t]--;

            if (!input.Context.Closed)
            {
                // Open hand: no riichi; values are the dama ones.
                pRr = pDr;
                pRt = pDt;
            }

            waits += live;
            rr += live * pRr;
            rt += live * pRt;
            dr += live * pDr;
            dt += live * pDt;
            if (pDr > 0)
                damaRonWaits += live;
        }

        TenpaiInfo info = waits <= 0
            ? new TenpaiInfo(0, 0, 0, 0, 0, false, furiten)
            : new TenpaiInfo(waits, rr / waits, rt / waits, dr / waits, dt / waits, damaRonWaits >= waits * 0.5, furiten);
        memo[key] = info;
        return info;
    }

    private static double Points(Span<int> hand14, TurnInput input, int win, bool tsumo, bool riichi, int aka, ScoringOptions scoring, bool dealer)
    {
        var ctx = new WinContext
        {
            WinTile = win,
            Tsumo = tsumo,
            Riichi = riichi && input.Context.Closed,
            SeatWind = input.Context.SeatWind,
            RoundWind = input.Context.RoundWind,
            AkaCount = aka,
            DoraIndicators = input.Context.DoraIndicators,
        };
        HandScore? score = ScoreCalculator.Score(hand14, input.Melds, ctx, dealer, scoring);
        return score?.Points ?? 0;
    }

    /// <summary>
    /// From a 1-shanten: average over the accepted draws of the best tenpai value one discard reaches.
    /// Also returns the expected number of live waits once tenpai.
    /// </summary>
    private static double OneShantenValue(
        TurnInput input, DiscardOption opt, int[] seen, ScoringOptions scoring, Dictionary<UInt128, TenpaiInfo> memo, out double avgWaits)
    {
        Span<int> hand = stackalloc int[Tiles.TypeCount];
        input.Counts14.CopyTo(hand);
        hand[opt.Tile]--;
        return OneShantenValue(hand, AkaAfter(input, opt.Tile), opt.Accepts, input, seen, scoring, memo, out avgWaits);
    }

    private static double OneShantenValue(
        ReadOnlySpan<int> hand13, int aka, IReadOnlyList<AcceptTile> accepts, TurnInput input, int[] seen, ScoringOptions scoring,
        Dictionary<UInt128, TenpaiInfo> memo, out double avgWaits)
    {
        Span<int> hand = stackalloc int[Tiles.TypeCount];
        hand13.CopyTo(hand);
        int melds = input.Melds.Count;

        double weight = 0, value = 0, waitsSum = 0;
        foreach (AcceptTile acc in accepts.OrderByDescending(a => a.Remaining).Take(6))
        {
            hand[acc.Tile]++;
            double bestVal = 0, bestWaits = 0;
            for (int d = 0; d < Tiles.TypeCount; d++)
            {
                if (hand[d] == 0)
                    continue;
                hand[d]--;
                if (ShantenCalculator.Calculate(hand, melds) == 0)
                {
                    TenpaiInfo info = TenpaiOf(hand, input, seen, scoring, memo, aka, d, acc.Tile);
                    double v = input.Context.Closed
                        ? info.RiichiRon * 1.15
                        : MixedValue(info.DamaRon, info.DamaTsumo, info.DamaRonPossible && !info.Furiten);
                    if (info.Waits > 0 && info.Waits * v > bestWaits * bestVal)
                    {
                        bestVal = v;
                        bestWaits = info.Waits;
                    }
                }
                hand[d]++;
            }
            hand[acc.Tile]--;

            weight += acc.Remaining;
            value += acc.Remaining * bestVal;
            waitsSum += acc.Remaining * bestWaits;
        }

        avgWaits = weight > 0 ? Math.Max(1, waitsSum / weight) : 4;
        return weight > 0 ? value / weight : EstimatePoints(1, input.Context.Dealer, scoring);
    }

    /// <summary>
    /// Expected utility of holding <paramref name="input"/>.Counts14 as a finished turn (a 13-tile
    /// equivalent hand waiting for its next draw): win chance × value − future deal-in, against the
    /// table in <paramref name="input"/>. Used to compare passing a call with taking it.
    /// </summary>
    public static double PositionValue(TurnInput input)
    {
        EvalOptions opts = input.Options;
        int[] hand = input.Counts14;
        int[] seen = input.Seen ?? hand;
        int wall = input.WallRemaining >= 0 ? input.WallRemaining : 40;
        int draws = Math.Max(0, (wall + 3) / 4);
        double unseen = Math.Max(1, 136 - seen.Sum());
        ScoringOptions scoring = input.Context.Scoring ?? ScoringOptions.Default;
        var util = new Utility(input, opts.PlacementAware);
        var threats = Threats(input, util, scoring, wall);
        (double otherEnd, double futureDanger) = Hazards(threats, opts);
        var memo = new Dictionary<UInt128, TenpaiInfo>();

        int melds = input.Melds.Count;
        Acceptance acc = UkeireCalculator.Accept(hand, melds, seen);
        int aka = 0;
        if (input.Context.AkaByType is { } a)
            foreach (int x in a)
                aka += x;

        if (acc.Shanten == 0)
        {
            TenpaiInfo info = TenpaiOf(hand, input, seen, scoring, memo, aka, -1, -1);
            var probe = input with { RiichiAvailable = input.Context.Closed };
            (double dama, double riichi) = TenpaiValues(info, probe, util, threats, scoring, unseen, draws, otherEnd, futureDanger, opts);
            return Math.Max(dama, riichi);
        }

        double tenpaiWaits = 5;
        double avgValue = acc.Shanten == 1
            ? OneShantenValue(hand, aka, acc.Tiles, input, seen, scoring, memo, out tenpaiWaits)
            : EstimatePoints(1 + HeldDora(input), input.Context.Dealer, scoring);
        var o = WinModel.Run(acc.Shanten, acc.Count, tenpaiWaits, unseen, draws, 0.6, true, otherEnd, futureDanger);
        return o.Win * util.Gain(avgValue + Sticks(input)) - o.DealIn * AvgLoss(threats, util, scoring);
    }

    /// <summary>Utility lost by discarding <paramref name="tile"/> now, against every threat.</summary>
    public static double DiscardRisk(TurnInput input, int tile)
    {
        ScoringOptions scoring = input.Context.Scoring ?? ScoringOptions.Default;
        int wall = input.WallRemaining >= 0 ? input.WallRemaining : 40;
        var util = new Utility(input, input.Options.PlacementAware);
        int[] seen = input.Seen ?? input.Counts14;
        double loss = 0;
        foreach (var t in Threats(input, util, scoring, wall))
            loss += t.Tenpai * DefenseModel.DealIn(tile, t.Opp, seen, input.Opponents) * input.Options.DealInScale * t.Loss;
        return loss;
    }

    private static List<(OpponentView Opp, double Tenpai, double Loss)> Threats(TurnInput input, Utility util, ScoringOptions scoring, int wall)
    {
        var threats = new List<(OpponentView Opp, double Tenpai, double Loss)>();
        if (input.Opponents is { } opponents)
        {
            foreach (OpponentView o in opponents)
            {
                double tenpai = DefenseModel.TenpaiChance(o, wall);
                if (tenpai > 0)
                    threats.Add((o, tenpai, util.LossTo(o.Seat, ThreatValue(o, scoring.Rules))));
            }
        }
        else
        {
            int seat = 0;
            foreach (IReadOnlyList<int> river in input.ThreatRivers)
            {
                var o = new OpponentView { Seat = -1 - seat++, River = river, Riichi = true };
                threats.Add((o, 1.0, util.LossTo(-1, ThreatValue(o, scoring.Rules))));
            }
        }

        return threats;
    }

    private static (double OtherEnd, double FutureDanger) Hazards(List<(OpponentView Opp, double Tenpai, double Loss)> threats, EvalOptions opts)
    {
        int riichiThreats = threats.Count(t => t.Opp.Riichi);
        double otherEnd = 0.03 + 0.045 * riichiThreats + 0.02 * (threats.Count - riichiThreats);
        double futureDanger = 0;
        foreach (var t in threats)
            futureDanger += t.Tenpai * 0.045 * opts.DealInScale;
        return (otherEnd, Math.Min(0.4, futureDanger));
    }

    private static int AkaAfter(TurnInput input, int discard)
    {
        if (input.Context.AkaByType is not { } aka)
            return 0;
        int total = 0;
        for (int t = 0; t < aka.Count; t++)
            total += aka[t];
        int akaOfType = discard < aka.Count ? aka[discard] : 0;
        return input.Counts14[discard] - akaOfType >= 1 ? total : total - 1;
    }

    /// <summary>Converts point changes into utility: raw points, or the placement-aware utility when enabled.</summary>
    private sealed class Utility
    {
        private readonly bool placement;
        private readonly int[] scores = new int[4];
        private readonly int self;
        private readonly int handsAfter;
        private readonly double baseline;

        public Utility(TurnInput input, bool placementAware)
        {
            placement = placementAware && input.Game is { Scores.Count: 4 };
            if (!placement)
                return;

            GameState g = input.Game!;
            for (int i = 0; i < 4; i++)
                scores[i] = g.Scores[i];
            self = g.SelfSeat;
            handsAfter = g.HandsAfterThis;
            baseline = PlacementModel.Utility(scores, self, handsAfter);
        }

        /// <summary>Utility gained by winning <paramref name="points"/>, paid on average equally by the opponents.</summary>
        public double Gain(double points)
        {
            if (!placement)
                return points;
            double total = 0;
            for (int payer = 0; payer < 4; payer++)
            {
                if (payer == self)
                    continue;
                Span<int> s = stackalloc int[4];
                scores.CopyTo(s);
                s[self] += (int)points;
                s[payer] -= (int)points;
                total += PlacementModel.Utility(s, self, handsAfter);
            }
            return total / 3 - baseline;
        }

        /// <summary>Utility lost by paying <paramref name="points"/> to nobody in particular (riichi stick, spread payment).</summary>
        public double Loss(double points)
        {
            if (!placement)
                return points;
            Span<int> s = stackalloc int[4];
            scores.CopyTo(s);
            s[self] -= (int)points;
            return baseline - PlacementModel.Utility(s, self, handsAfter);
        }

        /// <summary>Utility lost by dealing <paramref name="points"/> into the player in <paramref name="seat"/>.</summary>
        public double LossTo(int seat, double points)
        {
            if (!placement || seat < 0 || seat > 3)
                return points;
            Span<int> s = stackalloc int[4];
            scores.CopyTo(s);
            s[self] -= (int)points;
            s[seat] += (int)points;
            return baseline - PlacementModel.Utility(s, self, handsAfter);
        }
    }
}
