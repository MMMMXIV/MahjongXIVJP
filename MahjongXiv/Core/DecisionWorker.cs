using System.Threading.Tasks;
using MahjongXiv.Engine;

namespace MahjongXiv.Core;

/// <summary>
/// Runs turn decisions off the game's threads. The live UI (Draw) and the auto-play loop
/// (Framework.Update) must never stall on a solver call that can take tens of milliseconds, so they
/// ask for a decision by key and get it once a background task has computed it. Inputs are snapshots
/// built from fresh arrays, so the engine never touches game memory from the worker.
/// </summary>
internal sealed class DecisionWorker
{
    private readonly object gate = new();
    private string? pendingKey;
    private string? readyKey;
    private TurnDecision readyDecision;
    private IReadOnlyList<ScoredDiscard> readyRanked = Array.Empty<ScoredDiscard>();

    /// <summary>
    /// The decision for <paramref name="key"/> if it is ready; otherwise starts computing it (once) and
    /// returns false. A new key supersedes an older pending one; its result is discarded when it lands.
    /// </summary>
    public bool TryGet(string key, TurnInput input, out TurnDecision decision, out IReadOnlyList<ScoredDiscard> ranked)
    {
        lock (gate)
        {
            if (readyKey == key)
            {
                decision = readyDecision;
                ranked = readyRanked;
                return true;
            }

            decision = default;
            ranked = Array.Empty<ScoredDiscard>();
            if (pendingKey == key)
                return false;
            pendingKey = key;
        }

        Task.Run(() =>
        {
            TurnDecision d;
            IReadOnlyList<ScoredDiscard> r;
            try
            {
                d = TurnPolicy.Decide(input, out r);
            }
            catch (Exception ex)
            {
                MjLog.Error($"decision worker: {ex.Message}");
                d = new TurnDecision(-1, false, "error");
                r = Array.Empty<ScoredDiscard>();
            }

            lock (gate)
            {
                if (pendingKey != key)
                    return; // superseded while computing
                readyKey = key;
                readyDecision = d;
                readyRanked = r;
                pendingKey = null;
            }
        });

        return false;
    }

    /// <summary>
    /// A fingerprint of everything the policy reads: our tiles and melds, every river's contents in
    /// order (who discarded what decides genbutsu), each opponent's riichi state, declaration point and
    /// melds, dora and red fives, winds, the riichi option, the wall count, the match state and the
    /// options. Two inputs with the same key always get the same decision.
    /// </summary>
    public static string KeyFor(TurnInput input)
    {
        var sb = new System.Text.StringBuilder(512);
        foreach (int c in input.Counts14)
            sb.Append((char)('0' + c));
        sb.Append("|m");
        AppendMelds(sb, input.Melds);
        sb.Append("|r");
        AppendTiles(sb, input.OwnRiver);
        sb.Append("|s");
        if (input.Seen is { } seen)
            foreach (int c in seen)
                sb.Append((char)('0' + c));
        sb.Append("|w").Append(input.Context.SeatWind).Append(input.Context.RoundWind)
          .Append(input.Context.Dealer ? 'D' : 'N').Append(input.Context.Closed ? 'C' : 'O')
          .Append(input.RiichiAvailable ? 'R' : '-').Append(input.WallRemaining);
        sb.Append("|d");
        if (input.Context.DoraIndicators is { } dora)
            AppendTiles(sb, dora);
        sb.Append("|a");
        if (input.Context.AkaByType is { } aka)
            foreach (int a in aka)
                sb.Append((char)('0' + a));
        sb.Append("|t");
        foreach (IReadOnlyList<int> river in input.ThreatRivers)
        {
            AppendTiles(sb, river);
            sb.Append(';');
        }
        sb.Append("|o");
        if (input.Opponents is { } opps)
        {
            foreach (OpponentView o in opps)
            {
                sb.Append(o.Seat).Append(o.Riichi ? 'r' : '.').Append(o.RiichiDiscardIndex).Append(o.IsDealer ? 'D' : 'N').Append(':');
                AppendTiles(sb, o.River);
                sb.Append('/');
                AppendMelds(sb, o.Melds);
                sb.Append(';');
            }
        }
        sb.Append("|g");
        if (input.Game is { } g)
        {
            foreach (int score in g.Scores)
                sb.Append(score).Append(',');
            sb.Append(g.SelfSeat).Append(g.DealerSeat).Append('h').Append(g.HandNumber).Append('/').Append(g.TotalHands)
              .Append('b').Append(g.Honba).Append('k').Append(g.RiichiSticks);
        }
        sb.Append("|x").Append(input.Options);
        return sb.ToString();
    }

    private static void AppendTiles(System.Text.StringBuilder sb, IReadOnlyList<int> tiles)
    {
        foreach (int t in tiles)
            sb.Append((char)('A' + t));
    }

    private static void AppendMelds(System.Text.StringBuilder sb, IReadOnlyList<Meld> melds)
    {
        foreach (Meld m in melds)
        {
            sb.Append((char)('0' + (int)m.Kind)).Append(m.IsOpen ? 'o' : 'c');
            AppendTiles(sb, m.Tiles);
            sb.Append(',');
        }
    }
}
