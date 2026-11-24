using MahjongXiv.Engine;
using Xunit;

namespace MahjongXiv.Engine.Tests;

public class EdgeCaseTests
{
    [Fact]
    public void NineSidedWaitIsFound()
    {
        // Pure nine gates shape waits on all nine man tiles.
        Acceptance a = UkeireCalculator.Accept(Tiles.ParseCounts("1112345678999m"));
        Assert.Equal(0, a.Shanten);
        Assert.Equal(9, a.Types);
    }

    [Fact]
    public void ThreeSidedWaitCounts()
    {
        // 23456p waits 1p/4p/7p.
        Acceptance a = UkeireCalculator.Accept(Tiles.ParseCounts("123m456s99s23456p"));
        Assert.Equal(0, a.Shanten);
        Assert.Equal(new[] { Tiles.Index("1p"), Tiles.Index("4p"), Tiles.Index("7p") }, a.Tiles.Select(t => t.Tile).ToArray());
    }

    [Fact]
    public void MayImproveNeverDropsAUsefulTile()
    {
        // Exhaustive check on random hands: the pruned ukeire equals the brute-force one.
        var rng = new Random(5);
        for (int n = 0; n < 300; n++)
        {
            var counts = new int[34];
            int placed = 0;
            while (placed < 13)
            {
                int t = rng.Next(34);
                if (counts[t] < 4) { counts[t]++; placed++; }
            }

            int current = ShantenCalculator.Calculate(counts);
            var brute = new List<int>();
            for (int t = 0; t < 34; t++)
            {
                if (counts[t] >= 4) continue;
                counts[t]++;
                if (ShantenCalculator.Calculate(counts) < current) brute.Add(t);
                counts[t]--;
            }

            Acceptance a = UkeireCalculator.Accept(counts);
            Assert.Equal(brute, a.Tiles.Select(x => x.Tile).ToList());
        }
    }

    [Fact]
    public void RiverFuritenIsDetected()
    {
        var option = new DiscardOption(Tiles.Index("9m"), 0, 2, 8, [new AcceptTile(Tiles.Index("1s"), 4), new AcceptTile(Tiles.Index("4s"), 4)]);
        Assert.True(TurnPolicy.IsFuriten(option, [Tiles.Index("4s")]));
        Assert.False(TurnPolicy.IsFuriten(option, [Tiles.Index("5s")]));
    }

    [Fact]
    public void NoDrawsLeftMeansNoWin()
    {
        var o = WinModel.Run(0, 8, 8, 60, 0, 0.6, true, 0.03, 0.05);
        Assert.Equal(0, o.Win);
        Assert.Equal(0, o.DealIn);
        Assert.Equal(1, o.TenpaiAtEnd, 6);
    }

    [Fact]
    public void FoldsToTheTileSafeAgainstBothRiichi()
    {
        // Two riichi; 7z is genbutsu for both, 1z only for one. A cheap 2-shanten hand folds with 7z.
        List<int> R(string s) { var l = new List<int>(); var c = Tiles.ParseCounts(s); for (int t = 0; t < 34; t++) for (int k = 0; k < c[t]; k++) l.Add(t); return l; }
        var r1 = new OpponentView { Seat = 1, River = R("7z1z4m"), Riichi = true, RiichiDiscardIndex = 2 };
        var r2 = new OpponentView { Seat = 2, River = R("7z6s3p"), Riichi = true, RiichiDiscardIndex = 2 };
        var q = new OpponentView { Seat = 3, River = R("2z") };
        int[] hand = Tiles.ParseCounts("13m57p24s689s1z7z5m9p");
        int[] seen = (int[])hand.Clone();
        foreach (var o in new[] { r1, r2, q }) foreach (int t in o.River) seen[t]++;
        var input = new TurnInput
        {
            Counts14 = hand,
            Seen = seen,
            Context = new EvalContext { SeatWind = 1 },
            ThreatRivers = [r1.River, r2.River],
            Opponents = [r1, r2, q],
            WallRemaining = 30,
            RiichiAvailable = false,
            Options = EvalOptions.Strategic,
        };
        TurnDecision d = TurnPolicy.Decide(input);
        Assert.Equal(Tiles.Index("7z"), d.Discard);
        Assert.Equal(TurnReason.Fold, d.Kind);
    }

    [Fact]
    public void RankingValuesKeepingTheRedFive()
    {
        // The same hand ranked with and without the 5m being red: cutting the red copy must lose value
        // relative to cutting the plain 5p.
        int[] hand = Tiles.ParseCounts("123m5m456s78s5p11z22z");
        Assert.Equal(14, hand.Sum());
        var aka = new int[34];
        aka[Tiles.Index("5m")] = 1;
        long Gap(int[]? akaByType)
        {
            var ranked = DiscardEvaluator.Rank(hand, null, null, new EvalContext { AkaByType = akaByType });
            long v5m = ranked.First(r => r.Option.Tile == Tiles.Index("5m")).Value;
            long v5p = ranked.First(r => r.Option.Tile == Tiles.Index("5p")).Value;
            return v5p - v5m;
        }

        Assert.True(Gap(aka) > Gap(new int[34]));
    }
}

public class DeadTileTests
{
    [Fact]
    public void CutsTheFourthCopyOfAnHonorWhenTheOthersAreVisible()
    {
        // Three haku already visible; the drawn 4th one can never pair or set: cut it.
        foreach (string h in new[] { "234m67m345p3478s5z", "1m234m67m345p347s5z", "9p234m67m345p347s5z" })
        {
            int[] hand = Tiles.ParseCounts(h);
            int[] seen = (int[])hand.Clone();
            seen[Tiles.Index("5z")] = 4;
            var d = TurnPolicy.Decide(new TurnInput { Counts14 = hand, Seen = seen, Context = new EvalContext { SeatWind = 1 } });
            Assert.Equal(Tiles.Index("5z"), d.Discard);
        }
    }

    [Fact]
    public void ExactTiesPreferTheDeaderTile()
    {
        // Two isolated tiles with the same value: the one with fewer live copies goes first.
        int[] hand = Tiles.ParseCounts("234m67m345p347s9p5z");
        int[] seen = (int[])hand.Clone();
        seen[Tiles.Index("9p")] = 3; // two more 9p visible
        var ranked = DiscardEvaluator.Rank(hand, null, seen, new EvalContext { SeatWind = 1 });
        int i9p = ranked.FindIndex(r => r.Option.Tile == Tiles.Index("9p"));
        int i5z = ranked.FindIndex(r => r.Option.Tile == Tiles.Index("5z"));
        if (ranked[i9p].Value == ranked[i5z].Value)
            Assert.True(i9p < i5z);
    }
}
