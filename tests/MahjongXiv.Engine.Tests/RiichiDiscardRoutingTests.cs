using MahjongXiv.Core.Game;
using Xunit;

namespace MahjongXiv.Engine.Tests;

public class RiichiDiscardRoutingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PendingEightManDeclarationTakesPriorityOverRiichiBadge(bool badge)
        => Assert.Equal(RiichiDiscardRoute.DeclarationTile, RiichiDiscardRouting.Select(true, 7, badge));

    [Fact]
    public void LaterRiichiTurnsDiscardTheDraw()
        => Assert.Equal(RiichiDiscardRoute.DrawnTile, RiichiDiscardRouting.Select(false, -1, true));

    [Fact]
    public void NormalTurnUsesSolver()
        => Assert.Equal(RiichiDiscardRoute.Normal, RiichiDiscardRouting.Select(false, -1, false));

    [Theory]
    [InlineData(-1)]
    [InlineData(34)]
    public void LostDeclarationTargetCannotFallThroughToDraw(int tile)
        => Assert.Equal(RiichiDiscardRoute.InvalidDeclaration, RiichiDiscardRouting.Select(true, tile, true));
}
