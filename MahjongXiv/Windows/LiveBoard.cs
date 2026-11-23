using MahjongXiv.Core;
using MahjongXiv.Core.Game;
using MahjongXiv.Engine;

namespace MahjongXiv.Windows;

internal enum BoardPhase
{
    NoMatch,
    Watching,
    OurTurn,
    CallOffered,
    BetweenHands,
}

/// <summary>
/// Throttled read of the live Doman Mahjong board, shared by the main window and the in-game overlay
/// (one instance, owned by <see cref="Plugin"/>). On our turn it runs the same <see cref="TurnPolicy"/>
/// the <see cref="AutoPlayController"/> plays with — table-aware tile counts, dora, folding against a
/// riichi, furiten avoidance and the riichi call — so Suggest mode recommends exactly what auto-play
/// would do.
/// </summary>
internal sealed class LiveBoard
{
    private const int UnreadableGraceMs = 1500; // ignore transient decode gaps (deals, animations)
    private const int ReadIntervalMs = 300;
    private readonly HandAnalyzer analyzer = new();
    private readonly DecisionWorker worker = new();
    private long lastTick;
    private long unreadableSince;
    private string? policyKey;

    public bool Present { get; private set; }
    public int State { get; private set; } = -1;
    public BoardPhase Phase { get; private set; } = BoardPhase.NoMatch;

    public bool HandRead { get; private set; }

    /// <summary>Hand tiles are on screen but none decode to a known texture (persisted) — an incompatible tile design or a patch.</summary>
    public bool TilesUnreadable { get; private set; }
    public HandSnapshot Snapshot { get; private set; }
    public HandReport Report { get; private set; } = new();

    /// <summary>The table read from the last refresh, or null when it could not be read.</summary>
    public BoardSnapshot? Board { get; private set; }

    public bool HandComplete { get; private set; }

    /// <summary>The tile the turn policy would cut now, or -1 when it is not our discard.</summary>
    public int BestDiscard { get; private set; } = -1;

    /// <summary>The full turn decision behind <see cref="BestDiscard"/> (reason, riichi), when there is one.</summary>
    public TurnDecision? Decision { get; private set; }

    /// <summary>Value-ranked discards (table-aware acceptance), best first; empty when no decision was made.</summary>
    public IReadOnlyList<ScoredDiscard> RankedDiscards { get; private set; } = Array.Empty<ScoredDiscard>();

    /// <summary>We are in riichi: the only move is to cut the drawn tile.</summary>
    public bool InRiichi { get; private set; }

    public bool RonOffered { get; private set; }

    public void Refresh()
    {
        long now = Environment.TickCount64;
        if (now - lastTick < ReadIntervalMs)
            return;
        lastTick = now;

        Present = AddonPresent();
        State = Present ? EmjBoardReader.ReadStateCode() : -1;
        Phase = MapPhase(Present, State);

        HandComplete = false;
        BestDiscard = -1;
        RonOffered = false;
        InRiichi = false;

        HandSnapshot snap = default;
        bool read = Present && EmjBoardReader.TryReadHand(out snap);
        HandRead = read;
        Snapshot = read ? snap : default;
        Board = Present ? EmjBoardReader.TryReadBoard() : null;

        bool unreadableNow = Present && !read && snap.VisibleNodes > 0;
        if (unreadableNow)
        {
            if (unreadableSince == 0)
                unreadableSince = now;
            TilesUnreadable = now - unreadableSince >= UnreadableGraceMs;
        }
        else
        {
            unreadableSince = 0;
            TilesUnreadable = false;
        }

        if (HandRead)
        {
            Report = analyzer.Analyze(Snapshot.Counts, Snapshot.MeldCount);
            if (Phase == BoardPhase.OurTurn)
            {
                HandComplete = Report.Shanten == ShantenCalculator.Complete;
                if (!HandComplete && Report.Shape == HandShape.Discard)
                    Decide(Snapshot);
            }
        }
        else
        {
            Report = new HandReport();
        }

        if (BestDiscard < 0)
            ClearDecision();

        if (Phase == BoardPhase.CallOffered)
        {
            IReadOnlyList<ActionOption> options = EmjBoardReader.ReadActionOptions();
            RonOffered = options.Any(o => EmjStrings.Classify(o.Label) == EmjAction.Ron);
        }
    }

    /// <summary>Run the turn policy on a full post-draw hand, reusing the last result while nothing on the table changed.</summary>
    private void Decide(in HandSnapshot snap)
    {
        if (snap.TilesRead != 14 - 3 * snap.MeldCount)
        {
            // Hand still animating in: fall back to plain efficiency until the full draw reads.
            ClearDecision();
            if (Report.Discards.Count > 0)
                BestDiscard = Report.Discards[0].Tile;
            return;
        }

        BoardSnapshot? board = Board;
        if (board?.OwnRiichi == true)
        {
            InRiichi = true;
            ClearDecision();
            int drawn = snap.SlotTiles is { Length: > 13 } slots ? slots[13] : -1;
            BestDiscard = drawn;
            return;
        }

        bool riichiAvailable = EmjBoardReader.ReadActionOptions().Any(o => EmjStrings.Classify(o.Label) == EmjAction.Riichi);
        TurnInput input = TableContext.BuildTurnInput(snap, board, riichiAvailable);

        string key = DecisionWorker.KeyFor(input);
        if (key != policyKey || Decision is null)
        {
            if (!worker.TryGet(key, input, out TurnDecision decision, out IReadOnlyList<ScoredDiscard> ranked))
            {
                // Still computing off-thread: show plain efficiency meanwhile.
                Decision = null;
                RankedDiscards = Array.Empty<ScoredDiscard>();
                if (Report.Discards.Count > 0)
                    BestDiscard = Report.Discards[0].Tile;
                return;
            }

            Decision = decision;
            RankedDiscards = ranked;
            policyKey = key;
        }

        BestDiscard = Decision.Value.Discard;
    }

    private void ClearDecision()
    {
        Decision = null;
        RankedDiscards = Array.Empty<ScoredDiscard>();
        policyKey = null;
    }

    private static BoardPhase MapPhase(bool present, int state)
    {
        if (!present)
            return BoardPhase.NoMatch;

        // 6 and 30 are the same discard turn; 15 is a call decision; 2/29/32 are result / new-hand screens.
        return state switch
        {
            6 or 30 => BoardPhase.OurTurn,
            15 => BoardPhase.CallOffered,
            2 or 29 or 32 => BoardPhase.BetweenHands,
            _ => BoardPhase.Watching,
        };
    }

    private static bool AddonPresent()
    {
        unsafe
        {
            return EmjBoardReader.TryGetAddon(out _);
        }
    }
}
