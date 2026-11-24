using MahjongXiv.Engine;
using Xunit;

namespace MahjongXiv.Engine.Tests;

public class TableMathTests
{
    [Fact]
    public void FreshHandHasSeventyDraws()
    {
        Assert.Equal(70, TableMath.LiveDrawsLeft(0, 0, false));
        Assert.Equal(69, TableMath.LiveDrawsLeft(0, 0, true)); // dealer holds the first draw
    }

    [Fact]
    public void PonDoesNotChangeTheCount()
    {
        // 9 normal turns, then a pon of the 9th discard and the caller's discard (no draw).
        // Draws consumed: 9. River: 9 - 1 (claimed) + 1 (caller's discard) = 9.
        Assert.Equal(61, TableMath.LiveDrawsLeft(9, 0, false));
    }

    [Fact]
    public void KansCostOneExtraDrawEach()
    {
        // Ankan after the 10th draw: draws consumed 10 + 1 rinshan replenish = 11; river has 9 normal
        // discards + 1 rinshan discard = 10.
        Assert.Equal(70 - 11, TableMath.LiveDrawsLeft(10, 1, false));
        // Daiminkan of the 6th discard: consumed 6 + 1 = 7; river 6 - 1 + 1 = 6.
        Assert.Equal(70 - 7, TableMath.LiveDrawsLeft(6, 1, false));
    }

    [Fact]
    public void NeverNegative()
        => Assert.Equal(0, TableMath.LiveDrawsLeft(80, 2, true));
}
