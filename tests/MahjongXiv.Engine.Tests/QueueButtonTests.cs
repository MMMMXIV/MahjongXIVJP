using MahjongXiv.Core.Game;
using Xunit;

namespace MahjongXiv.Engine.Tests;

public class QueueButtonTests
{
    [Fact]
    public void FindsJoinInReportedJapaneseFinderLabels()
        => Assert.Equal(0, EmjStrings.FindJoinButton(["参加", "選択解除", "", "", ""]));

    [Theory]
    [InlineData("参加申請")]
    [InlineData(" 参加 ")]
    [InlineData("Join")]
    [InlineData("join")]
    [InlineData("Beitreten")]
    [InlineData("S'inscrire")]
    [InlineData("Participer")]
    public void PreservesSupportedJoinLabels(string label)
        => Assert.Equal(1, EmjStrings.FindJoinButton(["選択解除", label]));

    [Theory]
    [InlineData("参加辞退")]
    [InlineData("参加申請を取り消す")]
    [InlineData("参加申請取消")]
    [InlineData("Cancel Join")]
    [InlineData("退出")]
    [InlineData("")]
    public void DoesNotGuessFromSubstringsOrUnrelatedLabels(string label)
        => Assert.Equal(-1, EmjStrings.FindJoinButton([label]));

    [Fact]
    public void RefusesAmbiguousButtons()
        => Assert.Equal(-1, EmjStrings.FindJoinButton(["参加", "Join"]));
}
