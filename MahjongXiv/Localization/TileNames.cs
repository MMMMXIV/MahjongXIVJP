using MahjongXiv.Engine;

namespace MahjongXiv.Localization;

/// <summary>
/// Player-facing tile names. English keeps the compact mpsz notation ("5m", "1z" shown as "East");
/// Japanese uses the usual kanji forms ("5萬", "3筒", "7索", "東", "白", "發", "中"). No Dalamud
/// dependency, so the test project can compile it directly.
/// </summary>
internal static class TileNames
{
    private static readonly string[] HonorsEn = ["East", "South", "West", "North", "White", "Green", "Red"];
    private static readonly string[] HonorsJa = ["東", "南", "西", "北", "白", "發", "中"];

    public static string Display(int tile, bool japanese)
    {
        if (tile is < 0 or >= Tiles.TypeCount)
            return "?";

        if (Tiles.IsHonor(tile))
            return japanese ? HonorsJa[tile - 27] : HonorsEn[tile - 27];

        if (!japanese)
            return Tiles.Name(tile);

        string suit = Tiles.SuitOf(tile) switch
        {
            Suit.Man => "萬",
            Suit.Pin => "筒",
            _ => "索",
        };
        return $"{Tiles.Rank(tile)}{suit}";
    }

    public static string Join(IEnumerable<int> tiles, bool japanese)
        => string.Join(" ", tiles.Select(t => Display(t, japanese)));
}
