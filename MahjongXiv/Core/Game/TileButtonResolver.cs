namespace MahjongXiv.Core.Game;

internal interface ITileNodeAccess
{
    bool IsVisible(nint node);
    nint ChildAt(nint node, int index);
    bool IsEnabledButton(nint node, out string reason);
}

/// <summary>The hand tile is a container; validate the button inside it, never the container as a button.</summary>
internal static class TileButtonResolver
{
    public static nint Find<T>(nint tile, int childIndex, ref T nodes, out string reason)
        where T : struct, ITileNodeAccess
    {
        reason = "tile container missing or hidden";
        if (tile == 0 || !nodes.IsVisible(tile)) return 0;
        nint button = nodes.ChildAt(tile, childIndex);
        reason = "tile button child missing or hidden";
        if (button == 0 || !nodes.IsVisible(button)) return 0;
        if (!nodes.IsEnabledButton(button, out reason)) return 0;
        reason = "";
        return button;
    }
}
