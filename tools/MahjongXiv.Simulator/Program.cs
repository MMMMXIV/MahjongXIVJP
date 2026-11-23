using System.Diagnostics;
using MahjongXiv.Engine;
using MahjongXiv.Simulator;

// Usage:
//   [matches] [seed] [key=value ...]       self-play: every seat on the baseline agent
//   ab [matches] [seed] [key=value ...]    candidate vs baseline (paired; see RunAb)
//
// Table keys:     rounds=1|2 (default 2: east+south), scoring=flat|standard, kiriage=on
// Candidate keys: see Policies.Configure (e.g. strategic=on, legacy=on, shape=off ...)
string[] all = args;
bool ab = all.Length > 0 && all[0] == "ab";
string[] rest = ab ? all[1..] : all;
var pairs = rest.Where(a => a.Contains('=')).ToList();
rest = rest.Where(a => !a.Contains('=')).ToArray();
int matches = rest.Length > 0 && int.TryParse(rest[0], out int m) ? m : 100;
int seed = rest.Length > 1 && int.TryParse(rest[1], out int s) ? s : 42;

SimRules rules = Policies.ConfigureTable(pairs);
Policies.Configure(pairs);
Console.WriteLine($"table: {(rules.Rounds == 1 ? "east-only" : "east+south")}, scoring {rules.Scoring.Rules}{(rules.Scoring.KiriageMangan ? " (kiriage)" : "")}");

if (ab)
    RunAb(matches, seed, rules);
else
    RunSelfPlay(matches, seed, rules);

static void RunSelfPlay(int matches, int seed, SimRules rules)
{
    var agents = new Agent[4];
    for (int i = 0; i < 4; i++)
        agents[i] = Policies.BaselineAgent with { Timed = i == 0 };

    var stats = new SimStats();
    var gate = new object();

    var sw = Stopwatch.StartNew();
    Parallel.For(0, matches, i =>
    {
        var local = new SimStats();
        MatchSimulator.Play(agents, MatchRng(seed, i), local, rules);
        lock (gate)
            stats.Add(local);
    });
    sw.Stop();

    int wins = stats.Tsumos + stats.Rons;

    Console.WriteLine($"self-play: {stats.Matches} matches, {stats.Hands} hands, seed {seed}, {sw.Elapsed.TotalSeconds:F1}s ({stats.Hands / Math.Max(sw.Elapsed.TotalSeconds, 0.001):F1} hands/s)");
    Console.WriteLine($"  outcomes: {wins} wins ({stats.Tsumos} tsumo / {stats.Rons} ron), {stats.Draws} exhaustive draws ({100.0 * stats.Draws / Math.Max(stats.Hands, 1):F1}%)");
    if (wins > 0)
        Console.WriteLine($"  avg win: {stats.WinPointsTotal / wins} points, {(double)stats.WinHanTotal / wins:F2} han");
    Console.WriteLine();
    Console.WriteLine("  seat  win%   deal-in%  riichi%  avg place  top%   last%  avg score");
    for (int seat = 0; seat < 4; seat++)
    {
        double hands = Math.Max(stats.Hands, 1);
        double n = Math.Max(stats.Matches, 1);
        Console.WriteLine($"  {seat,4}  {100.0 * stats.Wins[seat] / hands,4:F1}%  {100.0 * stats.DealIns[seat] / hands,7:F1}%  {100.0 * stats.Riichis[seat] / hands,6:F1}%  {stats.PlacementSum[seat] / n,9:F3}  {100.0 * stats.PlaceCounts[seat, 0] / n,4:F1}%  {100.0 * stats.PlaceCounts[seat, 3] / n,5:F1}%  {stats.ScoreSum[seat] / n,9:F0}");
    }
    PrintTiming("seat 0", stats);
}

// Paired comparison. For every match index i the same wall stream is played five times:
//   - four times with the candidate in seat 0..3 against three baseline agents,
//   - once with four baseline agents (the control, which gives the baseline's own rates).
// Seat and wall luck cancel in the per-match candidate average, and the opponents are always the
// baseline, so a difference can only come from the candidate's own decisions.
static void RunAb(int matches, int seed, SimRules rules)
{
    var place = new double[matches];
    var top = new double[matches];
    var last = new double[matches];
    var rankPts = new double[matches];
    var delta = new double[matches];
    var basePlace = new double[matches];
    var baseTop = new double[matches];
    var baseLast = new double[matches];
    var baseRankPts = new double[matches];

    var cand = new SimStats();
    var ctrl = new SimStats();
    var gate = new object();
    int done = 0;

    var sw = Stopwatch.StartNew();
    Parallel.For(0, matches, i =>
    {
        var localCand = new SimStats();
        double p = 0, t = 0, l = 0, r = 0, d = 0;
        for (int seat = 0; seat < 4; seat++)
        {
            var agents = new Agent[4];
            for (int k = 0; k < 4; k++)
                agents[k] = Policies.BaselineAgent;
            agents[seat] = Policies.CandidateAgent with { Timed = true };

            var match = new SimStats();
            MatchResult result = MatchSimulator.Play(agents, MatchRng(seed, i), match, rules);
            int pl = result.Places[seat];
            p += pl;
            t += pl == 1 ? 1 : 0;
            l += pl == 4 ? 1 : 0;
            r += RankPoints(result.Scores[seat], pl, rules);
            d += result.Scores[seat] - result.Scores.Average();
            Fold(localCand, match, seat);
        }

        place[i] = p / 4;
        top[i] = t / 4;
        last[i] = l / 4;
        rankPts[i] = r / 4;
        delta[i] = d / 4;

        // Control: all baseline on the same walls; seat statistics folded together.
        var controlAgents = new Agent[4];
        for (int k = 0; k < 4; k++)
            controlAgents[k] = Policies.BaselineAgent with { Timed = true };
        var control = new SimStats();
        MatchResult cr = MatchSimulator.Play(controlAgents, MatchRng(seed, i), control, rules);
        var localCtrl = new SimStats();
        double bp = 0, bt = 0, bl = 0, br = 0;
        for (int seat = 0; seat < 4; seat++)
        {
            Fold(localCtrl, control, seat);
            bp += cr.Places[seat];
            bt += cr.Places[seat] == 1 ? 1 : 0;
            bl += cr.Places[seat] == 4 ? 1 : 0;
            br += RankPoints(cr.Scores[seat], cr.Places[seat], rules);
        }
        localCtrl.Hands = control.Hands * 4; // per-seat hand count, to match the candidate's denominator
        localCtrl.Decisions = control.Decisions;
        localCtrl.DecisionTicks = control.DecisionTicks;
        localCtrl.MaxDecisionTicks = control.MaxDecisionTicks;
        Array.Copy(control.DecisionBuckets, localCtrl.DecisionBuckets, control.DecisionBuckets.Length);
        basePlace[i] = bp / 4;
        baseTop[i] = bt / 4;
        baseLast[i] = bl / 4;
        baseRankPts[i] = br / 4;

        lock (gate)
        {
            cand.Add(localCand);
            ctrl.Add(localCtrl);
            done++;
            if (done % Math.Max(1, matches / 10) == 0)
                Console.Error.WriteLine($"  ... {done}/{matches} ({sw.Elapsed.TotalSeconds:F0}s)");
        }
    });
    sw.Stop();

    Console.WriteLine($"ab: {matches} matches x (4 candidate seats + 1 control), seed {seed}, {sw.Elapsed.TotalSeconds:F1}s");
    Console.WriteLine($"candidate: {Policies.CandidateAgent.Name}   baseline: {Policies.BaselineAgent.Name}");
    Console.WriteLine();
    Console.WriteLine("  metric                     baseline (control)        candidate               difference (paired)");
    Row("avg place", basePlace, place, "F3", 1);
    Row("top rate %", baseTop, top, "F2", 100);
    Row("last rate %", baseLast, last, "F2", 100);
    Row("rank points (k)", baseRankPts, rankPts, "F2", 0.001);
    Console.WriteLine($"  {"score vs table avg",-26} {"0 (by definition)",-25} {Ci(delta, 1, "F0"),-23}");
    Console.WriteLine();
    Rates("baseline", ctrl);
    Rates("candidate", cand);
    PrintTiming("baseline", ctrl);
    PrintTiming("candidate", cand);
    Console.WriteLine("  (± is the 95% confidence interval, 1.96 standard errors over matches; differences use the per-match pairing)");
}

static void Row(string name, double[] baseline, double[] candidate, string fmt, double scale)
{
    var diff = new double[baseline.Length];
    for (int i = 0; i < diff.Length; i++)
        diff[i] = candidate[i] - baseline[i];
    Console.WriteLine($"  {name,-26} {Ci(baseline, scale, fmt),-25} {Ci(candidate, scale, fmt),-23} {Ci(diff, scale, fmt)}");
}

static string Ci(double[] values, double scale, string fmt)
{
    (double mean, double se) = MeanAndError(values);
    return $"{(mean * scale).ToString(fmt)} ± {(1.96 * se * Math.Abs(scale)).ToString(fmt)}";
}

static void Rates(string label, SimStats st)
{
    double hands = Math.Max(st.Hands, 1);
    int w = st.Wins[0], di = st.DealIns[0];
    Console.WriteLine($"  {label,-9} win {100.0 * w / hands:F2}%  deal-in {100.0 * di / hands:F2}%  riichi {100.0 * st.Riichis[0] / hands:F2}%  " +
                      $"avg win {(w > 0 ? st.WinPoints[0] / w : 0)}  avg deal-in {(di > 0 ? st.DealInPoints[0] / di : 0)}  ({st.Hands} seat-hands)");
}

static void PrintTiming(string label, SimStats st)
{
    if (st.Decisions == 0)
        return;
    double mean = st.DecisionTicks * 1000.0 / Stopwatch.Frequency / st.Decisions;
    double max = st.MaxDecisionTicks * 1000.0 / Stopwatch.Frequency;
    long under5 = st.DecisionBuckets[0] + st.DecisionBuckets[1] + st.DecisionBuckets[2];
    long under20 = under5 + st.DecisionBuckets[3] + st.DecisionBuckets[4];
    Console.WriteLine($"  {label,-9} decision time: mean {mean:F2} ms, max {max:F1} ms, <5ms {100.0 * under5 / st.Decisions:F1}%, <20ms {100.0 * under20 / st.Decisions:F1}% ({st.Decisions} decisions)");
}

static double RankPoints(int score, int place, SimRules rules)
    => score - rules.StartScore + rules.Uma[place - 1];

// Folds one seat's counters of a match into slot 0 of an accumulator.
static void Fold(SimStats into, SimStats match, int seat)
{
    into.Hands += match.Hands;
    into.Wins[0] += match.Wins[seat];
    into.WinPoints[0] += match.WinPoints[seat];
    into.DealIns[0] += match.DealIns[seat];
    into.DealInPoints[0] += match.DealInPoints[seat];
    into.Riichis[0] += match.Riichis[seat];
    // Timing is only recorded for timed agents; the caller fixes up the all-timed control.
    into.Decisions += match.Decisions;
    into.DecisionTicks += match.DecisionTicks;
    into.MaxDecisionTicks = Math.Max(into.MaxDecisionTicks, match.MaxDecisionTicks);
    for (int b = 0; b < into.DecisionBuckets.Length; b++)
        into.DecisionBuckets[b] += match.DecisionBuckets[b];
}

// Each match gets its own wall stream, so results do not depend on thread scheduling.
static Random MatchRng(int seed, int match)
{
    ulong z = (ulong)(uint)seed * 0x9E3779B97F4A7C15UL + (ulong)match + 1;
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
    z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
    return new Random((int)(z ^ (z >> 31)));
}

static (double Mean, double Error) MeanAndError(double[] values)
{
    double mean = values.Average();
    if (values.Length < 2)
        return (mean, 0);
    double variance = values.Sum(v => (v - mean) * (v - mean)) / (values.Length - 1);
    return (mean, Math.Sqrt(variance / values.Length));
}
