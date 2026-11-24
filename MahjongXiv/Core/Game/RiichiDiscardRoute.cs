namespace MahjongXiv.Core.Game;

internal enum RiichiDiscardRoute { Normal, DeclarationTile, DrawnTile, InvalidDeclaration }

internal static class RiichiDiscardRouting
{
    // The badge can appear before the declaration discard. It cannot override that pending tile.
    public static RiichiDiscardRoute Select(bool declarationPending, int declarationTile, bool ownRiichi)
        => declarationPending
            ? declarationTile is >= 0 and < 34 ? RiichiDiscardRoute.DeclarationTile : RiichiDiscardRoute.InvalidDeclaration
            : ownRiichi ? RiichiDiscardRoute.DrawnTile : RiichiDiscardRoute.Normal;
}
