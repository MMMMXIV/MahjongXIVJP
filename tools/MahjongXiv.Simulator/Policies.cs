using MahjongXiv.Engine;

namespace MahjongXiv.Simulator;

/// <summary>
/// The two sides of an <c>ab</c> run. The baseline is the shipped policy (existing TurnPolicy and
/// CallPolicy with <see cref="EvalOptions.Default"/>); the candidate starts identical and is changed
/// by <c>key=value</c> arguments. Identical sides give exactly zero differences.
/// </summary>
internal static class Policies
{
    public static Agent BaselineAgent { get; } = new("baseline (shipped)", TurnPolicy.Decide, CallPolicy.ShouldPon);

    public static Agent CandidateAgent { get; private set; } = BaselineAgent with { Name = "candidate = baseline" };

    /// <summary>Table rules from <c>rounds=</c>, <c>scoring=</c>, <c>kiriage=</c>.</summary>
    public static SimRules ConfigureTable(IEnumerable<string> pairs)
    {
        SimRules rules = SimRules.Default;
        foreach (string pair in pairs)
        {
            string[] kv = pair.Split('=', 2);
            switch (kv[0].ToLowerInvariant())
            {
                case "rounds":
                    rules = rules with { Rounds = int.Parse(kv[1]) };
                    break;
                case "scoring":
                    rules = rules with { Scoring = rules.Scoring with { Rules = kv[1].ToLowerInvariant() == "standard" ? ScoringRules.Standard : ScoringRules.DomanFlat } };
                    break;
                case "kiriage":
                    rules = rules with { Scoring = rules.Scoring with { KiriageMangan = On(kv[1]) } };
                    break;
            }
        }

        return rules;
    }

    /// <summary>
    /// Candidate overrides: <c>key=value</c> pairs over <see cref="EvalOptions"/>, starting from the
    /// shipped defaults. <c>legacy=on</c> starts from <see cref="EvalOptions.Legacy"/>;
    /// <c>strategic=on</c> starts from <see cref="EvalOptions.Strategic"/>.
    /// </summary>
    public static void Configure(IEnumerable<string> overrides)
    {
        EvalOptions opts = EvalOptions.Default;
        bool any = false;
        foreach (string pair in overrides)
        {
            string[] kv = pair.Split('=', 2);
            if (kv.Length != 2)
                continue;
            string key = kv[0].ToLowerInvariant();
            if (key is "rounds" or "scoring" or "kiriage")
                continue;
            any = true;
            opts = key switch
            {
                "legacy" => On(kv[1]) ? EvalOptions.Legacy : opts,
                "strategic" => On(kv[1]) ? EvalOptions.Strategic : opts,
                "shape" => opts with { ShapeLookahead = On(kv[1]) },
                "shaperef" => opts with { ShapeReference = int.Parse(kv[1]) },
                "tenpai" => opts with { TenpaiLookahead = On(kv[1]) },
                "pushfold" => opts with { PushFold = On(kv[1]) },
                "riichijudge" => opts with { RiichiJudgement = On(kv[1]) },
                "placement" => opts with { PlacementAware = On(kv[1]) },
                "smartpon" => opts with { SmartPon = On(kv[1]) },
                "riichiron" => opts with { RiichiRonRate = double.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture) },
                "damaron" => opts with { DamaRonRate = double.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture) },
                "yakuhaipon" => opts with { EagerYakuhaiPon = On(kv[1]) },
                "otherend" => opts with { OtherEndScale = double.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture) },
                "dealin" => opts with { DealInScale = double.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture) },
                _ => throw new ArgumentException($"unknown candidate option '{kv[0]}'"),
            };
        }

        if (any)
        {
            EvalOptions chosen = opts;
            CandidateAgent = new Agent(
                $"candidate {chosen}",
                input => TurnPolicy.Decide(input with { Options = chosen }),
                call => CallPolicy.ShouldPon(call, chosen));
        }
    }

    private static bool On(string v) => v is "1" or "true" or "on";
}
