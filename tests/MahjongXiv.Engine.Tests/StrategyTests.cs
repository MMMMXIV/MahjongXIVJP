using MahjongXiv.Engine;
using Xunit;

namespace MahjongXiv.Engine.Tests;

public class StrategyTests
{
    private static int[] Seen(int[] hand, params IReadOnlyList<int>[] rivers)
    {
        var seen = (int[])hand.Clone();
        foreach (var r in rivers)
            foreach (int t in r)
                seen[t]++;
        return seen;
    }

    private static List<int> River(string tiles)
    {
        var list = new List<int>();
        int[] c = Tiles.ParseCounts(tiles);
        for (int t = 0; t < Tiles.TypeCount; t++)
            for (int k = 0; k < c[t]; k++)
                list.Add(t);
        return list;
    }

    // ---- Defense model ---------------------------------------------------------------------

    [Fact]
    public void GenbutsuIsSafe()
    {
        var opp = new OpponentView { Seat = 1, River = River("5m"), Riichi = true };
        Assert.Equal(0, DefenseModel.DealIn(Tiles.Index("5m"), opp, new int[34]));
    }

    [Fact]
    public void DealInRatesFollowTheUsualOrder()
    {
        var opp = new OpponentView { Seat = 1, River = River("1z"), Riichi = true };
        int[] seen = new int[34];
        double mid = DefenseModel.DealIn(Tiles.Index("5p"), opp, seen);
        double three = DefenseModel.DealIn(Tiles.Index("3p"), opp, seen);
        double two = DefenseModel.DealIn(Tiles.Index("2p"), opp, seen);
        double one = DefenseModel.DealIn(Tiles.Index("1p"), opp, seen);
        double honor = DefenseModel.DealIn(Tiles.Index("6z"), opp, seen);

        Assert.InRange(mid, 0.11, 0.14);
        Assert.True(mid > three && three > two && two > one && one > honor * 0.9);

        var suji = new OpponentView { Seat = 1, River = River("2p8p"), Riichi = true };
        double ryouSuji = DefenseModel.DealIn(Tiles.Index("5p"), suji, seen);
        Assert.InRange(ryouSuji, 0.02, 0.05);

        var kataSuji = new OpponentView { Seat = 1, River = River("2p"), Riichi = true };
        double kata = DefenseModel.DealIn(Tiles.Index("5p"), kataSuji, seen);
        Assert.True(kata > ryouSuji && kata < mid);
    }

    [Fact]
    public void NoChanceKillsTheRyanmenThroughTheWall()
    {
        // All four 4p visible: no 4-5p... wait for 3p via 4-5 is impossible, and 2-4 kanchan too.
        var opp = new OpponentView { Seat = 1, River = River("1z"), Riichi = true };
        int[] seen = new int[34];
        double before = DefenseModel.DealIn(Tiles.Index("3p"), opp, seen);
        seen[Tiles.Index("4p")] = 4;
        double after = DefenseModel.DealIn(Tiles.Index("3p"), opp, seen);
        Assert.True(after < before * 0.5);
    }

    [Fact]
    public void LateDiscardsAfterRiichiAreSafe()
    {
        var threat = new OpponentView { Seat = 1, River = River("1z2z3z"), Riichi = true, RiichiDiscardIndex = 2 };
        var other = new OpponentView { Seat = 2, River = new List<int> { 0, 1, 2, 3, Tiles.Index("5s") } };
        Assert.True(DefenseModel.IsSafe(Tiles.Index("5s"), threat, [threat, other]));
        Assert.False(DefenseModel.IsSafe(Tiles.Index("6s"), threat, [threat, other]));
    }

    // ---- Placement model --------------------------------------------------------------------

    [Fact]
    public void PlacementUtilityRewardsOvertakingAtAllLast()
    {
        int[] scores = [30000, 26000, 24000, 20000];
        double now = PlacementModel.Utility(scores, 1, handsAfter: 0);
        double passTop = PlacementModel.Utility([24900, 31100, 24000, 20000], 1, 0);
        double small = PlacementModel.Utility([29000, 27000, 24000, 20000], 1, 0);
        // Taking first with a 5100-point swing is worth far more than its points; a 1000-point win barely moves.
        Assert.True(passTop - now > 15000);
        Assert.True(small - now < 2000);
    }

    [Fact]
    public void PlacementUtilityIsNearlyLinearEarly()
    {
        int[] scores = [25000, 25000, 25000, 25000];
        double now = PlacementModel.Utility(scores, 0, handsAfter: 7);
        double plus = PlacementModel.Utility([29000, 23000, 24000, 24000], 0, 7);
        double gain = plus - now;
        Assert.InRange(gain, 2000, 16000);
    }

    // ---- Decisions ---------------------------------------------------------------------------

    private static TurnInput Input(string hand, bool dealer, OpponentView[] opps, int wall, bool riichiAvailable = true,
        EvalOptions? options = null, GameState? game = null, int[]? dora = null)
    {
        int[] counts = Tiles.ParseCounts(hand);
        var rivers = opps.Select(o => o.River).ToArray();
        return new TurnInput
        {
            Counts14 = counts,
            Seen = Seen(counts, rivers),
            Context = new EvalContext { SeatWind = dealer ? 0 : 1, Dealer = dealer, DoraIndicators = dora ?? [Tiles.Index("5z")] },
            ThreatRivers = opps.Where(o => o.Riichi).Select(o => o.River).ToList(),
            Opponents = opps,
            RiichiAvailable = riichiAvailable,
            WallRemaining = wall,
            Options = options ?? EvalOptions.Strategic,
            Game = game,
        };
    }

    [Fact]
    public void CheapBadTenpaiFoldsAgainstDealerRiichi()
    {
        // Case B: non-dealer, 1000-point kanchan tenpai (needs to cut a live 5p to stay tenpai) vs a dealer riichi.
        // Genbutsu 9s is available. The legacy policy keeps tenpai; the strategic one folds.
        var dealerRiichi = new OpponentView { Seat = 0, River = River("1z2z9s3z"), Riichi = true, RiichiDiscardIndex = 3, IsDealer = true };
        TurnInput input = Input("123m789m13p99s456s5p", dealer: false, [dealerRiichi,
            new OpponentView { Seat = 2, River = River("3z4z") }, new OpponentView { Seat = 3, River = River("5z6z") }], wall: 30,
            riichiAvailable: false);
        TurnDecision legacy = TurnPolicy.Decide(input with { Options = EvalOptions.Default });
        TurnDecision strategic = TurnPolicy.Decide(input);
        Assert.Equal(TurnReason.Fold, strategic.Kind);
        Assert.Equal(Tiles.Index("9s"), strategic.Discard); // genbutsu
        Assert.Contains(legacy.Discard, new[] { Tiles.Index("5p"), Tiles.Index("1p") }); // legacy never leaves tenpai
    }

    [Fact]
    public void ValuableGoodShapeOneShantenPushesEarly()
    {
        // Case A: dealer, 1-shanten with tanyao + red-five + dora (mangan range), against a non-dealer riichi
        // early in the hand. The legacy policy always folds from 1-shanten; the strategic one keeps going
        // when the discard is only moderately dangerous.
        var riichi = new OpponentView { Seat = 1, River = River("1z9m1p"), Riichi = true, RiichiDiscardIndex = 2 };
        TurnInput input = Input("234m456m3467p5577s", dealer: true, [riichi,
            new OpponentView { Seat = 2, River = River("2z") }, new OpponentView { Seat = 3, River = River("3z") }], wall: 55,
            riichiAvailable: false, dora: [Tiles.Index("4s")]) with
        {
            Context = new EvalContext { SeatWind = 0, Dealer = true, DoraIndicators = [Tiles.Index("4s")] },
        };
        TurnDecision legacy = TurnPolicy.Decide(input with { Options = EvalOptions.Default });
        TurnDecision strategic = TurnPolicy.Decide(input);
        Assert.Equal(TurnReason.FoldVsRiichi, legacy.Kind);
        Assert.Equal(TurnReason.Push, strategic.Kind);
    }

    [Fact]
    public void YakulessTenpaiRiichis()
    {
        TurnInput input = Input("123m456p789s23s99m1z", dealer: false,
            [new OpponentView { Seat = 1, River = River("2z") }, new OpponentView { Seat = 2, River = River("3z") }, new OpponentView { Seat = 3, River = River("4z") }],
            wall: 50);
        TurnDecision d = TurnPolicy.Decide(input);
        Assert.Equal(Tiles.Index("1z"), d.Discard);
        Assert.True(d.DeclareRiichi);
    }

    private static readonly GameState SecondAtAllLast =
        new() { Scores = [31000, 24000, 23000, 22000], SelfSeat = 1, DealerSeat = 3, HandNumber = 7, TotalHands = 8 };

    private static OpponentView[] QuietTable() =>
    [
        new OpponentView { Seat = 0, River = River("2z") },
        new OpponentView { Seat = 2, River = River("3z") },
        new OpponentView { Seat = 3, River = River("4z"), IsDealer = true },
    ];

    [Fact]
    public void AllLastSecondRiichisWhenDamaCannotOvertake()
    {
        // Case "need 7000 at all-last": tanyao + dora 2 is 3900 dama (still 2nd); riichi makes it mangan.
        TurnInput input = Input("234m567m34p55s666s9p", dealer: false, QuietTable(), wall: 40,
            game: SecondAtAllLast, dora: [Tiles.Index("4s")]);
        TurnDecision d = TurnPolicy.Decide(input);
        Assert.Equal(Tiles.Index("9p"), d.Discard);
        Assert.True(d.DeclareRiichi, d.Reason);
    }

    [Fact]
    public void AllLastSecondStaysDamaWhenDamaAlreadyOvertakes()
    {
        // Same standings, tanyao + dora 3 = mangan dama already takes first; riichi only lowers the win rate.
        TurnInput input = Input("234m567m34p555s66s9p", dealer: false, QuietTable(), wall: 40,
            game: SecondAtAllLast, dora: [Tiles.Index("4s")]);
        TurnDecision d = TurnPolicy.Decide(input);
        Assert.Equal(Tiles.Index("9p"), d.Discard);
        Assert.False(d.DeclareRiichi, d.Reason);
        Assert.True(TurnPolicy.Decide(input with { Options = EvalOptions.Default }).DeclareRiichi);
    }

    [Fact]
    public void WinModelIsMonotone()
    {
        var wide = WinModel.Run(0, 8, 8, 80, 10, 0.6, true, 0.04, 0);
        var narrow = WinModel.Run(0, 2, 2, 80, 10, 0.6, true, 0.04, 0);
        var late = WinModel.Run(0, 8, 8, 80, 2, 0.6, true, 0.04, 0);
        var tsumoOnly = WinModel.Run(0, 8, 8, 80, 10, 0.6, false, 0.04, 0);
        Assert.True(wide.Win > narrow.Win);
        Assert.True(wide.Win > late.Win);
        Assert.True(wide.Win > tsumoOnly.Win);
        Assert.InRange(wide.Win, 0.3, 0.9);
    }
}
