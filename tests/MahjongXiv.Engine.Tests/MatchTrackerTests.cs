using MahjongXiv.Core.Game;
using Xunit;

namespace MahjongXiv.Engine.Tests;

public class MatchTrackerTests
{
    private static TableObservation O(int[] scores, int dealer, int[] rivers, params int[] riichiSeats)
    {
        var riichi = new bool[4];
        foreach (int s in riichiSeats)
            riichi[s] = true;
        return new TableObservation(scores, riichi, rivers, dealer);
    }

    private static readonly int[] Start = [25000, 25000, 25000, 25000];
    private static readonly int[] Empty = [0, 0, 0, 0];

    /// <summary>Plays the table showing a hand in progress, then the next deal (rivers empty twice).</summary>
    private static void NextDeal(MatchTracker t, int[] scores, int dealer)
    {
        t.Observe(O(scores, dealer, Empty));
        t.Observe(O(scores, dealer, Empty));
    }

    [Fact]
    public void FreshMatchStartsAtEastOne()
    {
        var t = new MatchTracker();
        t.Observe(O(Start, 0, Empty));
        Assert.Equal(0, t.HandNumber);
        Assert.True(t.CountersKnown);
    }

    [Fact]
    public void MidMatchJoinLeavesHandNumberUnknown()
    {
        var t = new MatchTracker();
        t.Observe(O([31000, 22000, 24000, 23000], 2, [5, 5, 4, 4]));
        Assert.False(t.HandNumberKnown);
    }

    [Fact]
    public void NonDealerWinAdvancesHandAndResetsHonba()
    {
        var t = new MatchTracker();
        t.Observe(O(Start, 0, Empty));
        t.Observe(O(Start, 0, [6, 6, 5, 5]));
        NextDeal(t, [25000, 33000, 17000, 25000], dealer: 1); // seat 1 ron 8000 from seat 2
        Assert.Equal(1, t.HandNumber);
        Assert.Equal(0, t.Honba);
        Assert.Equal(0, t.RiichiSticks);
    }

    [Fact]
    public void DealerTenpaiDrawKeepsDealerAddsHonbaAndCarriesSticks()
    {
        var t = new MatchTracker();
        t.Observe(O(Start, 0, Empty));
        t.Observe(O([24000, 25000, 25000, 25000], 0, [10, 9, 9, 9], 0)); // dealer riichi (stick paid)
        Assert.Equal(1, t.RiichiSticks);
        Assert.Equal(10, t.RiichiRiverIndex[0]);
        // Exhaustive draw, only the dealer tenpai: +3000 / -1000 x3 (on top of the stick already paid).
        NextDeal(t, [27000, 24000, 24000, 24000], dealer: 0);
        Assert.Equal(0, t.HandNumber);
        Assert.Equal(1, t.Honba);
        Assert.Equal(1, t.RiichiSticks);
        Assert.Equal(-1, t.RiichiRiverIndex[0]);
    }

    [Fact]
    public void WinCollectsTheSticks()
    {
        var t = new MatchTracker();
        t.Observe(O(Start, 0, Empty));
        t.Observe(O([25000, 24000, 25000, 25000], 0, [7, 7, 6, 6], 1));
        // Seat 1 (riichi) rons seat 3 for 3900 and takes back its own stick: +3900 +1000 on top of -1000.
        NextDeal(t, [25000, 28900, 25000, 21100], dealer: 1);
        Assert.Equal(0, t.RiichiSticks);
        Assert.Equal(1, t.HandNumber);
        Assert.Equal(0, t.Honba);
    }

    [Fact]
    public void DealerChangeBeforeRiversEmptyCountsOnce()
    {
        var t = new MatchTracker();
        t.Observe(O(Start, 0, Empty));
        t.Observe(O(Start, 0, [8, 8, 8, 7]));
        int[] after = [25000, 30000, 20000, 25000];
        t.Observe(O(after, 1, [8, 8, 8, 7])); // new dealer shown while the old rivers linger
        t.Observe(O(after, 1, [8, 8, 8, 7]));
        NextDeal(t, after, dealer: 1);
        t.Observe(O(after, 1, [1, 0, 0, 0]));
        Assert.Equal(1, t.HandNumber);
        Assert.Equal(1, t.HandsSeen);
    }

    [Fact]
    public void MomentaryEmptyReadIsIgnored()
    {
        var t = new MatchTracker();
        t.Observe(O(Start, 0, Empty));
        t.Observe(O(Start, 0, [6, 6, 5, 5]));
        t.Observe(O(Start, 0, Empty)); // one misread
        t.Observe(O(Start, 0, [6, 6, 6, 5]));
        Assert.Equal(0, t.HandsSeen);
    }

    [Theory]
    [InlineData(new[] { 0, 0, 0, 0 }, true)]
    [InlineData(new[] { 3000, -1000, -1000, -1000 }, true)]
    [InlineData(new[] { 1500, 1500, -1500, -1500 }, true)]
    [InlineData(new[] { 1000, 1000, 1000, -3000 }, true)]
    [InlineData(new[] { 8000, -8000, 0, 0 }, false)]
    [InlineData(new[] { 4000, -2000, -1000, -1000 }, false)]
    [InlineData(new[] { 1000, -1000, 0, 0 }, false)]
    public void DrawPattern(int[] change, bool drawn)
        => Assert.Equal(drawn, MatchTracker.IsDrawPattern(change));
}
