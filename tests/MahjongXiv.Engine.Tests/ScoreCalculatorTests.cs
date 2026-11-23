using MahjongXiv.Engine;
using Xunit;

namespace MahjongXiv.Engine.Tests;

public class ScoreCalculatorTests
{
    private static readonly ScoringOptions Standard = new() { Rules = ScoringRules.Standard };

    private static HandScore Score(string hand, string win, bool tsumo, bool dealer = false, bool riichi = false,
        int[]? dora = null, ScoringOptions? options = null, int seatWind = 1)
    {
        int[] counts = Tiles.ParseCounts(hand);
        var ctx = new WinContext
        {
            WinTile = Tiles.Index(win),
            Tsumo = tsumo,
            Riichi = riichi,
            SeatWind = dealer ? 0 : seatWind,
            RoundWind = 0,
            DoraIndicators = dora,
        };
        HandScore? score = ScoreCalculator.Score(counts, null, ctx, dealer, options ?? Standard);
        Assert.NotNull(score);
        return score!;
    }

    [Fact]
    public void PinfuTsumoIs20Fu()
    {
        HandScore s = Score("123m456p789s234s55m", "4s", tsumo: true);
        Assert.Equal(20, s.Fu);
        Assert.Equal(2, s.Han);
        Assert.Equal(700, s.TsumoDealerPays);
        Assert.Equal(400, s.TsumoNonDealerPays);
        Assert.Equal(1500, s.Points);
    }

    [Fact]
    public void PinfuRonIs30Fu1000()
    {
        HandScore s = Score("123m456p789s234s55m", "4s", tsumo: false);
        Assert.Equal(30, s.Fu);
        Assert.Equal(1, s.Han);
        Assert.Equal(1000, s.Points);
    }

    [Fact]
    public void ChiitoitsuIs25Fu()
    {
        HandScore s = Score("1133m5577p2288s66z", "6z", tsumo: false);
        Assert.Equal(25, s.Fu);
        Assert.Equal(2, s.Han);
        Assert.Equal(1600, s.Points);
    }

    [Fact]
    public void FourHanThirtyFuIs7700OrKiriageMangan()
    {
        // riichi + pinfu + tanyao + one dora (indicator 2m -> dora 3m)
        HandScore s = Score("234m567m345p678s22p", "8s", tsumo: false, riichi: true, dora: [Tiles.Index("2m")]);
        Assert.Equal(4, s.Han);
        Assert.Equal(30, s.Fu);
        Assert.Equal(7700, s.Points);

        HandScore k = Score("234m567m345p678s22p", "8s", tsumo: false, riichi: true, dora: [Tiles.Index("2m")],
            options: Standard with { KiriageMangan = true });
        Assert.Equal(8000, k.Points);
    }

    [Fact]
    public void DealerTsumoSplitsEvenly()
    {
        // riichi + tsumo + dora 1, closed triplet of 9p (8 fu) + kanchan (2) -> 20+2+8+2 = 32 -> 40 fu
        HandScore s = Score("123m999p456s68s11m7s", "7s", tsumo: true, dealer: true, riichi: true, dora: [Tiles.Index("2m")]);
        Assert.Equal(40, s.Fu);
        Assert.Equal(3, s.Han);
        Assert.Equal(2600, s.TsumoNonDealerPays); // 40*32 = 1280 -> 2560 -> 2600 all
        Assert.Equal(7800, s.Points);
    }

    [Fact]
    public void YakuhaiTripletAndClosedWaitAddFu()
    {
        // chun triplet concealed (8) + kanchan (2) + menzen ron (10) + base 20 = 40 fu, 1 han
        HandScore s = Score("123m777z456p13s99s2s", "2s", tsumo: false);
        Assert.Equal(40, s.Fu);
        Assert.Equal(1, s.Han);
        Assert.Equal(1300, s.Points);
    }

    [Fact]
    public void ManganAndLimitsCapBasicPoints()
    {
        Assert.Equal(2000, ScoreCalculator.StandardBase(5, 30, false, Standard).Base);
        Assert.Equal(3000, ScoreCalculator.StandardBase(6, 30, false, Standard).Base);
        Assert.Equal(4000, ScoreCalculator.StandardBase(8, 30, false, Standard).Base);
        Assert.Equal(6000, ScoreCalculator.StandardBase(11, 30, false, Standard).Base);
        Assert.Equal(8000, ScoreCalculator.StandardBase(13, 30, false, Standard).Base);
        Assert.Equal(2000, ScoreCalculator.StandardBase(3, 70, false, Standard).Base); // 70*32 = 2240 -> mangan
        Assert.Equal(1920, ScoreCalculator.StandardBase(3, 60, false, Standard).Base);
    }

    [Fact]
    public void PaymentsIncludeHonbaAndSticks()
    {
        HandScore ron = Score("123m456p789s234s55m", "4s", tsumo: false); // 1000
        int[] d = ScoreCalculator.Payments(ron, winner: 1, dealerSeat: 0, discarder: 2, honba: 2, sticks: 1, Standard);
        Assert.Equal(-1600, d[2]);
        Assert.Equal(2600, d[1]);
        Assert.Equal(0, d[0] + d[3]);

        HandScore tsumo = Score("123m456p789s234s55m", "4s", tsumo: true); // 700/400
        int[] t = ScoreCalculator.Payments(tsumo, winner: 1, dealerSeat: 0, discarder: -1, honba: 1, sticks: 0, Standard);
        Assert.Equal(-800, t[0]);
        Assert.Equal(-500, t[2]);
        Assert.Equal(-500, t[3]);
        Assert.Equal(1800, t[1]);
    }

    [Fact]
    public void FlatRulesMatchDomanTable()
    {
        HandScore s = Score("234m567m345p678s22p", "8s", tsumo: false, riichi: true, dora: [Tiles.Index("2m")],
            options: new ScoringOptions { Rules = ScoringRules.DomanFlat });
        Assert.Equal(DomanScoring.Points(4, dealer: false), s.Points);
    }

    [Fact]
    public void BestReadingIsChosenByPoints()
    {
        // 111222333m: three concealed triplets (sanankou) vs three identical runs (iipeiko-like);
        // the scorer must pick the higher-paying reading.
        HandScore s = Score("111222333m456p77s", "7s", tsumo: true);
        Assert.Contains(s.Reading.Yaku, y => y.Yaku == Yaku.Sanankou);
    }

    [Fact]
    public void YakulessOpenHandHasNoScore()
    {
        // Open chii of 789p, an East pair that is neither seat (South) nor round (West) wind, a terminal: no yaku.
        int[] counts = Tiles.ParseCounts("123m456p11z234s");
        var ctx = new WinContext { WinTile = Tiles.Index("4s"), Tsumo = false, SeatWind = 1, RoundWind = 2 };
        Meld chii = new(MeldKind.Chii, [Tiles.Index("7p"), Tiles.Index("8p"), Tiles.Index("9p")]);
        Assert.Null(ScoreCalculator.Score(counts, [chii], ctx, false, Standard));
    }

    [Fact]
    public void OpenPinfuShapeIs30Fu()
    {
        // Open tanyao, all runs, ryanmen ron: 20 fu rounds to the open-hand minimum of 30.
        int[] counts = Tiles.ParseCounts("234m567p345s66s");
        var ctx = new WinContext { WinTile = Tiles.Index("5s"), Tsumo = false, SeatWind = 1 };
        Meld chii = new(MeldKind.Chii, [Tiles.Index("4p"), Tiles.Index("5p"), Tiles.Index("6p")]);
        HandScore? s = ScoreCalculator.Score(counts, [chii], ctx, false, Standard);
        Assert.NotNull(s);
        Assert.Equal(30, s!.Fu);
        Assert.Equal(1000, s.Points);
    }
}
