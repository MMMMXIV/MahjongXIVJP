using MahjongXiv.Core.Game;
using Xunit;

namespace MahjongXiv.Engine.Tests;

public class DiscardHandshakeTests
{
    [Theory]
    [InlineData(6)]
    [InlineData(28)]
    public void UnacknowledgedHighlightNeverFallsBackToCommit(int state)
    {
        var guard = new DiscardHandshake();
        Assert.Equal(DiscardStep.Highlight, guard.Next(state, "table:8:76066", 0));
        foreach (int tick in new[] { 0, 120, 400, 1200, 1600, 2999 })
            Assert.Equal(DiscardStep.Wait, guard.Next(state, "table:8:76066", tick));
        Assert.Equal(DiscardStep.Stop, guard.Next(state, "table:8:76066", 3000));
        Assert.Equal(DiscardStep.Stop, guard.Next(30, "table:8:76066", 4000));
    }

    // A replay of the reported 07:05 session's state6 -> state30 slot8 sequence.
    // This checks the input protocol, not reproduction of the unrelated 07:03 native crash.
    [Theory]
    [InlineData(6)]
    [InlineData(28)]
    [InlineData(30)]
    public void SettledSurfaceClicksOnceAndNeverInHighlightFrame(int initial)
    {
        var guard = new DiscardHandshake();
        const string key = "table:8:76066";
        Assert.Equal(DiscardStep.Highlight, guard.Next(initial, key, 1000));
        Assert.Equal(DiscardStep.Wait, guard.Next(30, key, 1000));
        Assert.Equal(DiscardStep.Wait, guard.Next(30, key, 1119));
        Assert.Equal(DiscardStep.Click, guard.Next(30, key, 1120));
        Assert.Equal(DiscardStep.Wait, guard.Next(30, key, 1120));
        Assert.Equal(DiscardStep.Wait, guard.Next(30, key, 9000));
    }

    [Theory]
    [InlineData("new-addon:8:76066")]
    [InlineData("table:9:76066")]
    [InlineData("table:8:76067")]
    public void ReplacedAddonHandOrTargetStopsInsteadOfClickingStaleSlot(string replacement)
    {
        var guard = new DiscardHandshake();
        guard.Next(6, "table:8:76066", 0);
        Assert.Equal(DiscardStep.Stop, guard.Next(30, replacement, 500));
        Assert.NotNull(guard.Failure);
    }

    [Fact]
    public void SurfaceRegressionStops()
    {
        var guard = new DiscardHandshake();
        guard.Next(6, "a", 0);
        guard.Next(30, "a", 120);
        Assert.Equal(DiscardStep.Stop, guard.Next(6, "a", 240));
    }

    [Fact]
    public void ResetClearsOldTableAndPendingHighlight()
    {
        var guard = new DiscardHandshake();
        guard.Next(6, "old", 0);
        guard.Fail("missing event");
        guard.Reset();
        Assert.Null(guard.Failure);
        Assert.Equal(DiscardStep.Highlight, guard.Next(30, "new", 6000));
        Assert.Equal(DiscardStep.Wait, guard.Next(30, "new", 6000));
        Assert.Equal(DiscardStep.Click, guard.Next(30, "new", 6120));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(15)]
    [InlineData(29)]
    [InlineData(32)]
    [InlineData(-1)]
    public void NonDiscardStatesNeverSendInput(int state)
        => Assert.Equal(DiscardStep.Wait, new DiscardHandshake().Next(state, "a", 0));

    [Theory]
    [InlineData(15, 15, false, false)] // same pass popup: never rearm/replay
    [InlineData(15, 15, true, true)]   // genuinely new opponent discard
    [InlineData(6, 30, false, false)]  // our highlight does not create a second turn
    [InlineData(28, 30, false, false)]
    [InlineData(30, 6, false, false)]  // regression cannot reset the safety guard
    [InlineData(2, 6, false, true)]    // first hand after deal re-arms
    [InlineData(29, 2, false, true)]
    [InlineData(-1, 6, false, true)]   // reconnect re-arms
    public void RearmingRequiresNewStateOrNewOffer(int previous, int current, bool discard, bool expected)
        => Assert.Equal(expected, DiscardHandshake.ShouldRearm(previous, current, discard));
}
