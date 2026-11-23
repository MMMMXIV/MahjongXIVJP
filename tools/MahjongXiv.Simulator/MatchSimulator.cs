using System.Diagnostics;
using MahjongXiv.Engine;

namespace MahjongXiv.Simulator;

internal sealed class SimStats
{
    public int Hands;
    public int Draws;
    public int Tsumos;
    public int Rons;
    public long WinPointsTotal;
    public long WinHanTotal;
    public readonly int[] Wins = new int[4];
    public readonly long[] WinPoints = new long[4];
    public readonly int[] DealIns = new int[4];
    public readonly long[] DealInPoints = new long[4];
    public readonly int[] Riichis = new int[4];
    public readonly int[] PlacementSum = new int[4];
    public readonly int[,] PlaceCounts = new int[4, 4];
    public readonly long[] ScoreSum = new long[4];
    public int Matches;

    // Decision timing of the timed agent.
    public long Decisions;
    public long DecisionTicks;
    public long MaxDecisionTicks;
    public readonly long[] DecisionBuckets = new long[8]; // <1ms, <2, <5, <10, <20, <50, <100, >=100

    // Calibration of the riichi/dama win model: first judged tenpai of a hand, predicted vs actual.
    public readonly long[] CalibCount = new long[2];      // [0] dama, [1] riichi
    public readonly double[] CalibPredicted = new double[2];
    public readonly long[] CalibWins = new long[2];

    public void RecordDecision(long ticks)
    {
        Decisions++;
        DecisionTicks += ticks;
        MaxDecisionTicks = Math.Max(MaxDecisionTicks, ticks);
        double ms = ticks * 1000.0 / Stopwatch.Frequency;
        int b = ms < 1 ? 0 : ms < 2 ? 1 : ms < 5 ? 2 : ms < 10 ? 3 : ms < 20 ? 4 : ms < 50 ? 5 : ms < 100 ? 6 : 7;
        DecisionBuckets[b]++;
    }

    public void Add(SimStats other)
    {
        Hands += other.Hands;
        Draws += other.Draws;
        Tsumos += other.Tsumos;
        Rons += other.Rons;
        WinPointsTotal += other.WinPointsTotal;
        WinHanTotal += other.WinHanTotal;
        Matches += other.Matches;
        Decisions += other.Decisions;
        DecisionTicks += other.DecisionTicks;
        MaxDecisionTicks = Math.Max(MaxDecisionTicks, other.MaxDecisionTicks);
        for (int b = 0; b < DecisionBuckets.Length; b++)
            DecisionBuckets[b] += other.DecisionBuckets[b];
        for (int k = 0; k < 2; k++)
        {
            CalibCount[k] += other.CalibCount[k];
            CalibPredicted[k] += other.CalibPredicted[k];
            CalibWins[k] += other.CalibWins[k];
        }
        for (int s = 0; s < 4; s++)
        {
            Wins[s] += other.Wins[s];
            WinPoints[s] += other.WinPoints[s];
            DealIns[s] += other.DealIns[s];
            DealInPoints[s] += other.DealInPoints[s];
            Riichis[s] += other.Riichis[s];
            PlacementSum[s] += other.PlacementSum[s];
            ScoreSum[s] += other.ScoreSum[s];
            for (int p = 0; p < 4; p++)
                PlaceCounts[s, p] += other.PlaceCounts[s, p];
        }
    }
}

/// <summary>Final standing of one match, by seat.</summary>
internal readonly record struct MatchResult(int[] Scores, int[] Places);

/// <summary>
/// A match of <see cref="SimRules.Rounds"/> rounds. The dealer repeats (with a honba) on a dealer
/// win or a dealer-tenpai draw; a non-dealer win resets the honba. The match ends after the last
/// dealer passes, or at once when someone drops below zero. Ties are broken by seat order from the
/// first dealer. Not modelled: agari-yame, west-round extension, abortive draws, chi and kan.
/// </summary>
internal static class MatchSimulator
{
    public static MatchResult Play(Agent[] agents, Random rng, SimStats stats, SimRules rules)
    {
        var scores = new int[4];
        Array.Fill(scores, rules.StartScore);

        int totalHands = 4 * rules.Rounds;
        int maxHands = totalHands * 4;
        int handNumber = 0;
        int dealer = 0;
        int honba = 0;
        int pot = 0;
        int played = 0;

        while (handNumber < totalHands && played < maxHands)
        {
            var setup = new HandSetup(dealer, honba, pot, (int[])scores.Clone(), handNumber, totalHands, rules.Scoring);
            HandOutcome outcome = HandSimulator.Play(setup, agents, rng, rules, stats);
            played++;
            stats.Hands++;

            for (int s = 0; s < 4; s++)
            {
                scores[s] += outcome.Deltas[s];
                if (outcome.RiichiDeclared[s])
                    stats.Riichis[s]++;
            }

            if (outcome.ExhaustiveDraw)
            {
                stats.Draws++;
            }
            else
            {
                stats.Wins[outcome.Winner]++;
                stats.WinPoints[outcome.Winner] += outcome.WinPoints;
                stats.WinPointsTotal += outcome.WinPoints;
                stats.WinHanTotal += outcome.WinHan;
                if (outcome.DealIn >= 0)
                {
                    stats.Rons++;
                    stats.DealIns[outcome.DealIn]++;
                    stats.DealInPoints[outcome.DealIn] += outcome.WinPoints;
                }
                else
                {
                    stats.Tsumos++;
                }
            }

            pot = outcome.SticksLeft;

            if (scores.Any(s => s < 0))
                break; // busted: the match ends immediately

            if (outcome.DealerKeeps)
            {
                honba++;
            }
            else
            {
                honba = outcome.ExhaustiveDraw ? honba + 1 : 0;
                dealer = (dealer + 1) % 4;
                handNumber++;
            }
        }

        // Riichi sticks left on the table at the end go to the top player.
        int[] bySeat = [0, 1, 2, 3];
        Array.Sort(bySeat, (a, b) => scores[b] != scores[a] ? scores[b].CompareTo(scores[a]) : a.CompareTo(b));
        scores[bySeat[0]] += pot * 1000;

        var places = new int[4];
        for (int place = 0; place < 4; place++)
            places[bySeat[place]] = place + 1;
        for (int s = 0; s < 4; s++)
        {
            stats.PlacementSum[s] += places[s];
            stats.PlaceCounts[s, places[s] - 1]++;
            stats.ScoreSum[s] += scores[s];
        }

        stats.Matches++;
        return new MatchResult(scores, places);
    }
}
