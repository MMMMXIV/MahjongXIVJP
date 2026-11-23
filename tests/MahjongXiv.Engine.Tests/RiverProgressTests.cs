using MahjongXiv.Core.Game;
using Xunit;

namespace MahjongXiv.Engine.Tests;

public class RiverProgressTests
{
    private static IReadOnlyList<IReadOnlyList<int>> Rivers(int[] opponent, int[]? own = null)
        => new IReadOnlyList<int>[] { own ?? [], opponent, [], [] };

    [Fact]
    public void PartialReadAndRecoveryDoNotReplayTheSamePass()
    {
        var tracker = new RiverProgress();
        Assert.False(tracker.Observe(Rivers([1]), 0, out _));
        Assert.True(tracker.Observe(Rivers([1, 2]), 0, out int tile));
        Assert.Equal(2, tile);
        Assert.False(tracker.Observe(Rivers([]), 0, out _));
        Assert.False(tracker.Observe(Rivers([1, 2]), 0, out _));
        Assert.True(tracker.Observe(Rivers([1, 2, 3]), 0, out tile));
        Assert.Equal(3, tile);
    }

    [Fact]
    public void NewHandResetAllowsItsFirstOpponentDiscard()
    {
        var tracker = new RiverProgress();
        tracker.Observe(Rivers([1, 2]), 0, out _);
        tracker.Reset();
        Assert.False(tracker.Observe(Rivers([]), 0, out _));
        Assert.True(tracker.Observe(Rivers([4]), 0, out int tile));
        Assert.Equal(4, tile);
    }

    [Fact]
    public void OwnDiscardNeverRearmsAnOpponentOffer()
    {
        var tracker = new RiverProgress();
        tracker.Observe(Rivers([]), 0, out _);
        Assert.False(tracker.Observe(Rivers([], [5]), 0, out _));
    }
}
