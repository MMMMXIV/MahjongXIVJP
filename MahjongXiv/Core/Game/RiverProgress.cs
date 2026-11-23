namespace MahjongXiv.Core.Game;

/// <summary>Monotonic per-hand river observations; partial UI reads cannot invent a fresh offer.</summary>
internal sealed class RiverProgress
{
    private readonly int[] lengths = new int[4];
    private bool seeded;
    public void Reset() => seeded = false;

    public bool Observe(IReadOnlyList<IReadOnlyList<int>> rivers, int self, out int tile)
    {
        tile = -1;
        if (rivers.Count != lengths.Length) return false;
        bool grew = false;
        for (int i = 0; i < lengths.Length; i++)
        {
            var river = rivers[i];
            if (seeded && i != self && river.Count > lengths[i])
            {
                tile = river[river.Count - 1];
                grew = true;
            }
            lengths[i] = seeded ? Math.Max(lengths[i], river.Count) : river.Count;
        }
        seeded = true;
        return grew;
    }
}
