using MahjongXiv.Engine;

namespace MahjongXiv.Simulator;

/// <summary>
/// Fits <see cref="TenpaiModel"/> on simulated play: at every ordinary decision, each non-riichi opponent
/// with at least one meld is a sample (features from public information, label = could ron right now).
/// Logistic regression by Newton's method; prints the coefficients and a calibration table next to the
/// shipped meld-count rule.
/// </summary>
internal static class TenpaiFit
{
    public static void Run(int matches, int seed, SimRules rules, Func<int, Random> rngFor)
    {
        var samples = new List<(double[] X, bool Y, double Old)>();
        var gate = new object();
        var agents = new Agent[4];
        Array.Fill(agents, Policies.BaselineAgent);

        Parallel.For(0, matches, i =>
        {
            var local = new List<(double[], bool, double)>();
            HandSimulator.Collector = (input, ready) =>
            {
                if (input.Opponents is null)
                    return;
                foreach (OpponentView o in input.Opponents)
                {
                    if (o.Riichi || o.Melds.Count == 0)
                        continue;
                    double[] x = TenpaiModel.Features(o, input.WallRemaining, input.Game, input.Context.RoundWind);
                    local.Add((x, ready[o.Seat], DefenseModel.TenpaiChance(o, input.WallRemaining)));
                }
            };
            MatchSimulator.Play(agents, rngFor(i), new SimStats(), rules);
            HandSimulator.Collector = null;
            lock (gate)
                samples.AddRange(local);
        });

        int positives = samples.Count(s => s.Y);
        Console.WriteLine($"fit: {matches} matches, {samples.Count} samples (non-riichi opponents with melds), {positives} ron-ready ({100.0 * positives / Math.Max(1, samples.Count):F1}%)");

        double[] w = Fit(samples.Select(s => s.X).ToList(), samples.Select(s => s.Y).ToList());
        Console.WriteLine("coefficients:");
        for (int k = 0; k < w.Length; k++)
            Console.WriteLine($"  {TenpaiModel.FeatureNames[k],-16} {w[k],8:F3}");
        Console.WriteLine($"  C#: [{string.Join(", ", w.Select(v => v.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)))}]");

        double llNew = 0, llOld = 0;
        foreach ((double[] x, bool y, double old) in samples)
        {
            double p = TenpaiModel.Chance(x, w);
            llNew += LogLoss(p, y);
            llOld += LogLoss(old, y);
        }
        Console.WriteLine($"log loss: fitted {llNew / samples.Count:F4}, shipped rule {llOld / samples.Count:F4} (lower is better)");

        Console.WriteLine("calibration (predicted bin -> actual ron-ready rate, n):");
        Console.WriteLine("  bin        fitted model            shipped rule");
        for (int b = 0; b < 10; b++)
        {
            double lo = b / 10.0, hi = (b + 1) / 10.0;
            var fit = samples.Where(s => Bin(TenpaiModel.Chance(s.X, w), lo, hi)).ToList();
            var old = samples.Where(s => Bin(s.Old, lo, hi)).ToList();
            Console.WriteLine($"  {lo:F1}-{hi:F1}    {Rate(fit),-22}  {Rate(old)}");
        }
    }

    private static bool Bin(double p, double lo, double hi) => p >= lo && (p < hi || (hi >= 1 && p <= 1));

    private static string Rate(List<(double[] X, bool Y, double Old)> list)
        => list.Count == 0 ? "-" : $"{100.0 * list.Count(s => s.Y) / list.Count:F1}% (n={list.Count})";

    private static double LogLoss(double p, bool y)
    {
        p = Math.Clamp(p, 1e-6, 1 - 1e-6);
        return y ? -Math.Log(p) : -Math.Log(1 - p);
    }

    /// <summary>Logistic regression by Newton's method with a small ridge term.</summary>
    private static double[] Fit(List<double[]> xs, List<bool> ys)
    {
        int k = TenpaiModel.FeatureCount;
        var w = new double[k];
        for (int iter = 0; iter < 25; iter++)
        {
            var grad = new double[k];
            var hess = new double[k, k];
            for (int n = 0; n < xs.Count; n++)
            {
                double[] x = xs[n];
                double p = TenpaiModel.Chance(x, w);
                double r = (ys[n] ? 1 : 0) - p;
                double s = p * (1 - p);
                for (int a = 0; a < k; a++)
                {
                    grad[a] += r * x[a];
                    for (int b = 0; b < k; b++)
                        hess[a, b] += s * x[a] * x[b];
                }
            }
            for (int a = 0; a < k; a++)
            {
                hess[a, a] += 1e-3 * xs.Count / 1000.0 + 1e-6;
                grad[a] -= 1e-3 * xs.Count / 1000.0 * w[a];
            }
            double[] step = Solve(hess, grad);
            double norm = 0;
            for (int a = 0; a < k; a++)
            {
                w[a] += step[a];
                norm += step[a] * step[a];
            }
            if (norm < 1e-12)
                break;
        }
        return w;
    }

    private static double[] Solve(double[,] a, double[] b)
    {
        int n = b.Length;
        var m = (double[,])a.Clone();
        var x = (double[])b.Clone();
        for (int c = 0; c < n; c++)
        {
            int piv = c;
            for (int r = c + 1; r < n; r++)
                if (Math.Abs(m[r, c]) > Math.Abs(m[piv, c]))
                    piv = r;
            for (int j = 0; j < n; j++)
                (m[c, j], m[piv, j]) = (m[piv, j], m[c, j]);
            (x[c], x[piv]) = (x[piv], x[c]);
            for (int r = 0; r < n; r++)
            {
                if (r == c || m[c, c] == 0)
                    continue;
                double f = m[r, c] / m[c, c];
                for (int j = 0; j < n; j++)
                    m[r, j] -= f * m[c, j];
                x[r] -= f * x[c];
            }
        }
        for (int c = 0; c < n; c++)
            x[c] = m[c, c] == 0 ? 0 : x[c] / m[c, c];
        return x;
    }
}
