using MahjongXiv.Engine;

namespace MahjongXiv.Simulator;

/// <summary>Prints the shipped and the strategic decision side by side for a few textbook positions.</summary>
internal static class Examples
{
    private static List<int> River(string tiles)
    {
        var list = new List<int>();
        int[] c = Tiles.ParseCounts(tiles);
        for (int t = 0; t < Tiles.TypeCount; t++)
            for (int k = 0; k < c[t]; k++)
                list.Add(t);
        return list;
    }

    private static TurnInput Input(string hand, bool dealer, OpponentView[] opps, int wall, bool riichiOk, string dora, GameState? game = null)
    {
        int[] counts = Tiles.ParseCounts(hand);
        int[] seen = (int[])counts.Clone();
        foreach (var o in opps)
            foreach (int t in o.River)
                seen[t]++;
        seen[Tiles.Index(dora)]++;
        return new TurnInput
        {
            Counts14 = counts,
            Seen = seen,
            Context = new EvalContext { SeatWind = dealer ? 0 : 1, Dealer = dealer, DoraIndicators = [Tiles.Index(dora)] },
            ThreatRivers = opps.Where(o => o.Riichi).Select(o => o.River).ToList(),
            Opponents = opps,
            RiichiAvailable = riichiOk,
            WallRemaining = wall,
            Game = game,
        };
    }

    private static void Show(string title, TurnInput input)
    {
        TurnDecision a = TurnPolicy.Decide(input with { Options = EvalOptions.Default });
        TurnDecision b = TurnPolicy.Decide(input with { Options = EvalOptions.Strategic });
        string Hand() => string.Join("", Enumerable.Range(0, 34).SelectMany(t => Enumerable.Repeat(Tiles.Name(t), input.Counts14[t])));
        Console.WriteLine($"## {title}");
        Console.WriteLine($"  hand: {Hand()}  wall left {input.WallRemaining}");
        Console.WriteLine($"  shipped:   cut {Tiles.Name(a.Discard)}{(a.DeclareRiichi ? " + riichi" : "")}  ({a.Reason})");
        Console.WriteLine($"  strategic: cut {Tiles.Name(b.Discard)}{(b.DeclareRiichi ? " + riichi" : "")}  ({b.Reason})");
        Console.WriteLine();
    }

    public static void Run()
    {
        var quiet = new[]
        {
            new OpponentView { Seat = 1, River = River("2z9m") },
            new OpponentView { Seat = 2, River = River("3z1p") },
            new OpponentView { Seat = 3, River = River("4z9s") },
        };

        // A: dealer, valuable good-shape 1-shanten vs a non-dealer riichi, early.
        var riichiA = new OpponentView { Seat = 1, River = River("1z9m1p"), Riichi = true, RiichiDiscardIndex = 2 };
        Show("A: dealer mangan-class 1-shanten vs non-dealer riichi (early)",
            Input("234m456m3467p5577s", true, [riichiA, quiet[1], quiet[2]], 55, false, "4s"));

        // B: non-dealer cheap kanchan tenpai vs dealer riichi; staying tenpai needs a live 5p.
        var riichiB = new OpponentView { Seat = 0, River = River("1z2z9s3z"), Riichi = true, RiichiDiscardIndex = 3, IsDealer = true };
        Show("B: non-dealer cheap closed-wait tenpai vs dealer riichi",
            Input("123m789m13p99s456s5p", false, [riichiB, quiet[1], quiet[2]], 30, false, "5z"));

        // C1: all-last, 2nd by 7000: dama 3900 does not overtake; riichi (mangan) does.
        var second = new GameState { Scores = [31000, 24000, 23000, 22000], SelfSeat = 1, DealerSeat = 3, HandNumber = 7, TotalHands = 8 };
        var table = new[]
        {
            new OpponentView { Seat = 0, River = River("2z") },
            new OpponentView { Seat = 2, River = River("3z") },
            new OpponentView { Seat = 3, River = River("4z"), IsDealer = true },
        };
        Show("C1: all-last, 2nd by 7000, tanyao+dora2 tenpai (3900 dama)",
            Input("234m567m34p55s666s9p", false, table, 40, true, "4s", second));
        Show("C2: same standings, tanyao+dora3 tenpai (8000 dama already overtakes)",
            Input("234m567m34p555s66s9p", false, table, 40, true, "4s", second));

        // C3: all-last, top by 12000 vs a riichi from 2nd, hand 2-shanten: no reason to push.
        var top = new GameState { Scores = [36000, 24000, 21000, 19000], SelfSeat = 0, DealerSeat = 2, HandNumber = 7, TotalHands = 8 };
        var riichiC = new OpponentView { Seat = 1, River = River("1z5z2p8s"), Riichi = true, RiichiDiscardIndex = 3 };
        Show("C3: all-last top by 12000, 2-shanten vs riichi from 2nd",
            Input("23m57m46p13s5689s2z7z", false, [riichiC, new OpponentView { Seat = 2, River = River("3z"), IsDealer = true }, new OpponentView { Seat = 3, River = River("4z") }], 45, false, "1z", top));

        // D: two riichi at once, 1-shanten cheap hand.
        var r1 = new OpponentView { Seat = 1, River = River("1z4m7p"), Riichi = true, RiichiDiscardIndex = 2 };
        var r2 = new OpponentView { Seat = 2, River = River("2z6s3p"), Riichi = true, RiichiDiscardIndex = 2, IsDealer = true };
        Show("D: double riichi (one dealer), cheap 1-shanten",
            Input("345m67m24p678s11z5s9p", false, [r1, r2, quiet[2]], 35, false, "5z"));

        // E: yakuless good-wait tenpai, quiet table: riichi.
        Show("E: yakuless ryanmen tenpai, quiet table",
            Input("123m456p789s23s99m1z", false, quiet, 50, true, "5z"));
    }
}
