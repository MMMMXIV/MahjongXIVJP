using MahjongXiv.Engine;
using Xunit;

namespace MahjongXiv.Engine.Tests;

public class TenpaiModelTests
{
    private static List<int> Discards(int n) => Enumerable.Range(0, n).Select(i => 27 + i % 4).ToList();

    private static Meld Pon(string tile)
    {
        int t = Tiles.Index(tile);
        return new Meld(MeldKind.Pon, [t, t, t]);
    }

    [Fact]
    public void RiichiIsCertainAndClosedSilentHandsAreIgnored()
    {
        Assert.Equal(1.0, TenpaiModel.Chance(new OpponentView { Seat = 1, River = Discards(5), Riichi = true }, 40, null, 0));
        Assert.Equal(0.0, TenpaiModel.Chance(new OpponentView { Seat = 1, River = Discards(12) }, 20, null, 0));
    }

    [Fact]
    public void MoreMeldsAndLaterTurnsRaiseTheChance()
    {
        var oneEarly = new OpponentView { Seat = 1, River = Discards(3), Melds = [Pon("7z")] };
        var oneLate = new OpponentView { Seat = 1, River = Discards(12), Melds = [Pon("7z")] };
        var twoLate = new OpponentView { Seat = 1, River = Discards(12), Melds = [Pon("7z"), new Meld(MeldKind.Chii, [1, 2, 3])] };
        double a = TenpaiModel.Chance(oneEarly, 55, null, 0);
        double b = TenpaiModel.Chance(oneLate, 25, null, 0);
        double c = TenpaiModel.Chance(twoLate, 25, null, 0);
        Assert.True(a < b && b < c, $"{a} {b} {c}");
    }

    [Fact]
    public void OneMeldHandIsNoLongerZero()
    {
        // The old rule put every one-meld hand at 0; in simulation about a quarter of them could ron.
        var o = new OpponentView { Seat = 2, River = Discards(9), Melds = [Pon("5z")] };
        Assert.Equal(0, DefenseModel.TenpaiChance(o, 35));
        Assert.True(TenpaiModel.Chance(o, 35, null, 0) > 0.1);
    }

    [Fact]
    public void FeaturesSeeYakuhaiOneSuitAndTriplets()
    {
        var game = new GameState { Scores = [25000, 25000, 25000, 25000], SelfSeat = 0, DealerSeat = 1 };
        // Seat 2 with dealer 1 sits South; a pon of South (2z) is their seat-wind yakuhai.
        var o = new OpponentView
        {
            Seat = 2,
            River = [Tiles.Index("1m"), Tiles.Index("9p"), Tiles.Index("5p")],
            Melds = [Pon("2z"), Pon("3s")],
        };
        double[] f = TenpaiModel.Features(o, 40, game, 0);
        Assert.Equal(1, f[4]);           // yakuhai set
        Assert.Equal(1, f[5], 6);        // all number melds in souzu, no souzu in the river
        Assert.Equal(1, f[6]);           // all triplets
    }
}
