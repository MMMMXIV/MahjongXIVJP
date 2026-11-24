using Dalamud.Plugin.Services;
using MahjongXiv.Core.Game;

namespace MahjongXiv.Core;

/// <summary>
/// Feeds <see cref="MatchTracker"/> from the live table a few times a second, whatever the play mode,
/// so the engine sees the hand number, sticks, honba and riichi declaration points. Starts a fresh
/// track when a new duty (ContentFinderCondition) begins or the table has been gone for a while.
/// </summary>
internal sealed class MatchObserver : IDisposable
{
    private const long TickIntervalMs = 250;
    private const long GoneResetMs = 20000;

    private readonly object gate = new();
    private long nextTick;
    private long lastSeen;
    private uint cfc;

    public MatchTracker Tracker { get; } = new();

    /// <summary>Dealer rotations in the current match type (8 full, 4 quick), from the duty.</summary>
    public int TotalHands { get; private set; } = 8;

    public MatchObserver() => Plugin.Framework.Update += OnUpdate;

    /// <summary>A consistent copy of what the tracker knows, safe to read from any thread.</summary>
    public MatchInfo Snapshot()
    {
        lock (gate)
        {
            return new MatchInfo(
                Tracker.HandNumber, TotalHands, Tracker.Honba, Tracker.RiichiSticks, Tracker.CountersKnown,
                (int[])Tracker.RiichiRiverIndex.Clone());
        }
    }

    private void OnUpdate(IFramework framework)
    {
        long now = Environment.TickCount64;
        if (now < nextTick)
            return;
        nextTick = now + TickIntervalMs;

        BoardSnapshot? board = EmjBoardReader.TryReadBoard();
        lock (gate)
        {
            uint current = EmjTableGuard.CurrentCfc;
            if (board is null)
            {
                if (lastSeen > 0 && now - lastSeen > GoneResetMs)
                {
                    Tracker.Reset();
                    lastSeen = 0;
                }
                return;
            }

            if (current != cfc || lastSeen == 0)
            {
                Tracker.Reset();
                cfc = current;
                TotalHands = EmjTableGuard.TotalHandsFor(current);
            }
            lastSeen = now;

            if (board.Panels.Count != 4 || board.Rivers.Count != 4 || board.Panels.All(p => p.Score == 0))
                return;

            int dealer = -1;
            for (int s = 0; s < 4; s++)
                if (board.Panels[s].Wind.Length > 0 && EmjStrings.WindToIndex(board.Panels[s].Wind) == 0)
                    dealer = s;

            Tracker.Observe(new TableObservation(
                board.Panels.Select(p => p.Score).ToArray(),
                board.Panels.Select(p => p.Riichi).ToArray(),
                board.Rivers.Select(r => r.Count).ToArray(),
                dealer));
        }
    }

    public void Dispose() => Plugin.Framework.Update -= OnUpdate;
}

/// <summary>What the match tracker knows at one moment.</summary>
internal readonly record struct MatchInfo(int HandNumber, int TotalHands, int Honba, int RiichiSticks, bool CountersKnown, int[] RiichiRiverIndex);
