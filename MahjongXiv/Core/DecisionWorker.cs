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

    /// <summary>A compact fingerprint of everything the policy reads.</summary>
    public static string KeyFor(TurnInput input)
    {
        var sb = new System.Text.StringBuilder(200);
        foreach (int c in input.Counts14)
            sb.Append((char)('0' + c));
        sb.Append('|');
        if (input.Seen is { } seen)
            foreach (int c in seen)
                sb.Append((char)('0' + c));
        sb.Append('|').Append(input.Melds.Count).Append('|').Append(input.Context.SeatWind).Append(input.RiichiAvailable ? 'R' : '-');
        if (input.Context.DoraIndicators is { } dora)
            foreach (int t in dora)
                sb.Append(',').Append(t);
        if (input.Context.AkaByType is { } aka)
            foreach (int a in aka)
                sb.Append((char)('0' + a));
        sb.Append('|');
        foreach (IReadOnlyList<int> river in input.ThreatRivers)
            sb.Append(river.Count).Append(';');
        if (input.Opponents is { } opps)
            foreach (OpponentView o in opps)
                sb.Append(o.Riichi ? 'r' : '.').Append(o.River.Count);
        sb.Append('|').Append(input.OwnRiver.Count).Append(input.Options.AnyStrategic ? 'S' : 'L');
        if (input.Game is { } game)
            foreach (int score in game.Scores)
                sb.Append(',').Append(score);
        return sb.ToString();
    }
}
