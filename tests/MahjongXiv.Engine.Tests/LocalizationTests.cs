using MahjongXiv.Core.Game;
using MahjongXiv.Engine;
using MahjongXiv.Localization;
using Xunit;

namespace MahjongXiv.Engine.Tests;

public class LocalizationTests
{
    [Theory]
    [InlineData("Ron", "Ron")]
    [InlineData("ロン", "Ron")]
    [InlineData("Tsumo", "Tsumo")]
    [InlineData("ツモ", "Tsumo")]
    [InlineData("Riichi", "Riichi")]
    [InlineData("リーチ", "Riichi")]
    [InlineData("Pon", "Pon")]
    [InlineData("ポン", "Pon")]
    [InlineData("Chi", "Chi")]
    [InlineData("チー", "Chi")]
    [InlineData("Kan", "Kan")]
    [InlineData("カン", "Kan")]
    [InlineData("Pass", "Pass")]
    [InlineData("パス", "Pass")]
    [InlineData("Nine Terminals", "NineTerminals")]
    [InlineData("九種九牌", "NineTerminals")]
    [InlineData("", "Unknown")]
    [InlineData("???", "Unknown")]
    public void ClassifiesButtonLabels(string label, string expected)
        => Assert.Equal(Enum.Parse<EmjAction>(expected), EmjStrings.Classify(label));

    [Fact]
    public void FindsActionByPosition()
    {
        string[] labels = ["カン", "ポン", "パス"];
        Assert.Equal(1, EmjStrings.IndexOf(labels, EmjAction.Pon));
        Assert.Equal(2, EmjStrings.IndexOf(labels, EmjAction.Pass));
        Assert.Equal(-1, EmjStrings.IndexOf(labels, EmjAction.Ron));
    }

    [Fact]
    public void PassFallsBackToTheSingleUnrecognizedButton()
    {
        string[] labels = ["ポン", "見合わせる?"];
        // "見合わせる?" matches no term; it is the only unknown next to a recognized action.
        Assert.Equal(EmjAction.Unknown, EmjStrings.Classify(labels[1]));
        Assert.Equal(1, EmjStrings.IndexOf(labels, EmjAction.Pass));
    }

    [Fact]
    public void PassFallbackRefusesToGuessBetweenSeveralUnknowns()
    {
        string[] labels = ["ポン", "???", "!!!"];
        Assert.Equal(-1, EmjStrings.IndexOf(labels, EmjAction.Pass));
        Assert.Equal(-1, EmjStrings.IndexOf(["???"], EmjAction.Pass));
    }

    [Theory]
    [InlineData("East", "East")]
    [InlineData("南", "South")]
    [InlineData("西家", "West")]
    [InlineData(" 北 ", "North")]
    [InlineData("Player", null)]
    public void NormalizesSeatWinds(string text, string? expected)
        => Assert.Equal(expected, EmjStrings.CanonicalWind(text));

    [Fact]
    public void WindIndexAcceptsCanonicalAndJapanese()
    {
        Assert.Equal(1, EmjStrings.WindToIndex("South"));
        Assert.Equal(3, EmjStrings.WindToIndex("北"));
        Assert.Equal(0, EmjStrings.WindToIndex(null));
    }

    [Theory]
    [InlineData("Riichi!", true)]
    [InlineData("リーチ！", true)]
    [InlineData("Alphinaud", false)]
    public void DetectsRiichiBadges(string text, bool expected)
        => Assert.Equal(expected, EmjStrings.IsRiichi(text));

    [Theory]
    [InlineData("ポン！", true)]
    [InlineData("Pon!", true)]
    [InlineData("+8000", true)]
    [InlineData("光の戦士", false)]
    public void DetectsPanelBadges(string text, bool expected)
        => Assert.Equal(expected, EmjStrings.IsBadge(text));

    [Theory]
    [InlineData("5m", false, "5m")]
    [InlineData("5m", true, "5萬")]
    [InlineData("3p", true, "3筒")]
    [InlineData("7s", true, "7索")]
    [InlineData("1z", true, "東")]
    [InlineData("6z", true, "發")]
    [InlineData("7z", false, "Red")]
    public void NamesTiles(string tile, bool japanese, string expected)
        => Assert.Equal(expected, TileNames.Display(Tiles.Index(tile), japanese));

    [Fact]
    public void FindsUiButtonsByLabel()
    {
        Assert.Equal(1, EmjStrings.IndexOfTerm(["詳細", "次へ"], EmjStrings.AdvanceButtonTerms));
        Assert.Equal(0, EmjStrings.IndexOfTerm(["Next"], EmjStrings.AdvanceButtonTerms));
        Assert.Equal(1, EmjStrings.IndexOfTerm(["Clear Selection", "参加申請"], EmjStrings.JoinButtonTerms));
        Assert.Equal(0, EmjStrings.IndexOfTerm(["突入する"], EmjStrings.CommenceButtonTerms));
        Assert.Equal(-1, EmjStrings.IndexOfTerm(["退室"], EmjStrings.AdvanceButtonTerms));
    }

    [Fact]
    public void TurnPolicyReportsReasonKind()
    {
        int[] hand = Tiles.ParseCounts("123m456p789s1122z3z");
        TurnDecision d = TurnPolicy.Decide(new TurnInput { Counts14 = hand, Context = new EvalContext() }, out var ranked);
        Assert.Equal(TurnReason.BestValue, d.Kind);
        Assert.Equal(Tiles.Index("3z"), d.Discard);
        Assert.NotEmpty(ranked);
    }
}
