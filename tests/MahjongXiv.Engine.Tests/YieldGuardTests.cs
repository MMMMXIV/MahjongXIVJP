using MahjongXiv.Core.Game;
using Xunit;

namespace MahjongXiv.Engine.Tests;

public class YieldGuardTests
{
    [Fact]
    public void IsolatedYieldsNeverStop()
    {
        var g = new YieldGuard();
        for (int i = 0; i < 10; i++)
        {
            Assert.False(g.RecordYield());
            g.RecordSuccess();
        }
        Assert.Equal(10, g.Total);
    }

    [Fact]
    public void ConsecutiveYieldsStopAtTheLimit()
    {
        var g = new YieldGuard();
        Assert.False(g.RecordYield());
        Assert.False(g.RecordYield());
        Assert.True(g.RecordYield());
        Assert.Equal(YieldGuard.Limit, g.Consecutive);
    }

    [Fact]
    public void SuccessBreaksTheRun()
    {
        var g = new YieldGuard();
        g.RecordYield();
        g.RecordYield();
        g.RecordSuccess();
        Assert.False(g.RecordYield());
        Assert.Equal(1, g.Consecutive);
    }
}
