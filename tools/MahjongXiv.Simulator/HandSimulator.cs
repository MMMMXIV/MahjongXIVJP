using System.Diagnostics;
using MahjongXiv.Engine;

namespace MahjongXiv.Simulator;

/// <summary>One seat's decision makers: the discard turn and the pon decision.</summary>
internal sealed record Agent(string Name, Func<TurnInput, TurnDecision> Turn, Func<CallInput, bool> Pon)
{
    /// <summary>When set, the time spent in <see cref="Turn"/> is accumulated into the stats under this seat.</summary>
    public bool Timed { get; init; }
}

/// <summary>Rules of the simulated table that are not settled for Doman Mahjong.</summary>
internal sealed record SimRules
{
    public static readonly SimRules Default = new();

    /// <summary>1 = east-only (quick match), 2 = east+south (full match).</summary>
    public int Rounds { get; init; } = 2;

    public ScoringOptions Scoring { get; init; } = ScoringOptions.Default;

    public int StartScore { get; init; } = 25000;

    /// <summary>Placement bonus (uma) used for the rank-point metric only, 1st..4th.</summary>
    public int[] Uma { get; init; } = [30000, 10000, -10000, -30000];
}

internal sealed class SimPlayer
{
    public int[] Counts { get; } = new int[Tiles.TypeCount];
    public int[] AkaByType { get; } = new int[Tiles.TypeCount];
    public List<int> River { get; } = [];
    public List<Meld> Melds { get; } = [];
    public int MeldAka;
    public bool Riichi;
    public bool Ippatsu;
    public int RiichiDiscardIndex = -1;

    /// <summary>Passed a winning tile this go-around (no yaku): ron is off until our next discard.</summary>
    public bool TempFuriten;

    public void Add(int tileId)
    {
        Counts[TileId.Type(tileId)]++;
        if (TileId.IsAka(tileId))
            AkaByType[TileId.Type(tileId)]++;
    }

    /// <summary>Remove one copy for a discard, preferring a plain copy (mirrors the live actuator). Returns true if the red one left.</summary>
    public bool RemoveForDiscard(int type)
    {
        Counts[type]--;
        if (AkaByType[type] > Counts[type])
        {
            AkaByType[type]--;
            return true;
        }

        return false;
    }

    /// <summary>Move two hand copies plus the claimed discard into a pon. Aka stay scorable via <see cref="MeldAka"/>.</summary>
    public void TakePon(int type, bool calledAka)
    {
        for (int n = 0; n < 2; n++)
        {
            Counts[type]--;
            if (AkaByType[type] > Counts[type])
            {
                AkaByType[type]--;
                MeldAka++;
            }
        }

        if (calledAka)
            MeldAka++;
        Melds.Add(new Meld(MeldKind.Pon, [type, type, type]));
    }
}

/// <summary>The table entering a hand.</summary>
internal sealed record HandSetup(int Dealer, int Honba, int Sticks, int[] Scores, int HandNumber, int TotalHands, ScoringOptions Scoring)
{
    public int RoundWind => Math.Min(3, HandNumber / 4);
}

internal sealed record HandOutcome
{
    public int Winner { get; init; } = -1;
    public int DealIn { get; init; } = -1;
    public required int[] Deltas { get; init; }
    public bool ExhaustiveDraw { get; init; }
    public required bool DealerKeeps { get; init; }
    public required int SticksLeft { get; init; }
    public required bool[] RiichiDeclared { get; init; }
    public int WinHan { get; init; }
    public int WinPoints { get; init; }
}

/// <summary>
/// Plays one hand: riichi (needs 1000 points and four live tiles) with ippatsu/ura, haitei/houtei,
/// river, riichi and temporary furiten, pon calls through each seat's agent (chi/kan are never
/// taken, mirroring the live bot), honba and riichi sticks, and exhaustive-draw tenpai payments.
/// Every seat sees the public information exactly.
/// </summary>
internal static class HandSimulator
{
    public static HandOutcome Play(HandSetup setup, Agent[] agents, Random rng, SimRules rules, SimStats stats)
    {
        int dealer = setup.Dealer;
        var wall = new Wall(rng);
        var players = new SimPlayer[4];
        for (int s = 0; s < 4; s++)
            players[s] = new SimPlayer();
        for (int n = 0; n < 13; n++)
            for (int s = 0; s < 4; s++)
                players[(dealer + s) % 4].Add(wall.Draw());

        var deltas = new int[4];
        int pot = setup.Sticks;
        var declared = new bool[4];
        int turn = dealer;

        int[] Scores()
        {
            var now = new int[4];
            for (int s = 0; s < 4; s++)
                now[s] = setup.Scores[s] + deltas[s];
            return now;
        }

        while (wall.HasDraws)
        {
            SimPlayer p = players[turn];
            int drawId = wall.Draw();
            p.Add(drawId);
            bool haitei = !wall.HasDraws;

            if (ShantenCalculator.Calculate(p.Counts, p.Melds.Count) == ShantenCalculator.Complete)
            {
                HandScore? score = ScoreCalculator.Score(
                    p.Counts, p.Melds, WinCtx(p, turn, setup, TileId.Type(drawId), tsumo: true, lastTile: haitei, wall, extraAka: 0),
                    turn == dealer, setup.Scoring);
                if (score is not null)
                    return Settle(score, turn, -1, setup, deltas, pot, declared, rules);
            }

            // The ippatsu window closes with this draw (the tsumo check above still counted it).
            p.Ippatsu = false;
            p.TempFuriten = false;

            int discardType;
            if (p.Riichi)
            {
                discardType = TileId.Type(drawId);
            }
            else
            {
                bool riichiLegal = p.Melds.Count == 0 && setup.Scores[turn] + deltas[turn] >= 1000 && wall.Remaining >= 4;
                TurnDecision decision = Decide(agents, turn, BuildInput(players, turn, setup, wall, Scores(), pot, riichiLegal), stats);
                if (decision.Discard < 0 || p.Counts[decision.Discard] == 0)
                {
                    discardType = TileId.Type(drawId); // defensive fallback; should not happen
                }
                else
                {
                    discardType = decision.Discard;
                    if (decision.DeclareRiichi && riichiLegal)
                    {
                        p.Riichi = true;
                        p.Ippatsu = true;
                        p.RiichiDiscardIndex = p.River.Count;
                        declared[turn] = true;
                        deltas[turn] -= 1000;
                        pot++;
                    }
                }
            }

            // A pon claims the discard, jumps the turn, and forces an immediate discard from the
            // caller — which can itself be ronned or ponned, hence the inner loop.
            while (true)
            {
                bool akaLeft = p.RemoveForDiscard(discardType);
                p.River.Add(discardType);

                for (int k = 1; k < 4; k++)
                {
                    int seat = (turn + k) % 4;
                    SimPlayer opp = players[seat];

                    opp.Counts[discardType]++;
                    bool complete = ShantenCalculator.Calculate(opp.Counts, opp.Melds.Count) == ShantenCalculator.Complete;
                    if (complete && !opp.TempFuriten && !IsRiverFuriten(opp, discardType))
                    {
                        HandScore? score = ScoreCalculator.Score(
                            opp.Counts, opp.Melds,
                            WinCtx(opp, seat, setup, discardType, tsumo: false, lastTile: !wall.HasDraws, wall, extraAka: akaLeft ? 1 : 0),
                            seat == dealer, setup.Scoring);
                        opp.Counts[discardType]--;
                        if (score is not null)
                            return Settle(score, seat, turn, setup, deltas, pot, declared, rules);

                        opp.TempFuriten = true; // passed a winning tile (no yaku)
                    }
                    else
                    {
                        opp.Counts[discardType]--;
                    }
                }

                int caller = FindPonCaller(agents, players, turn, setup, wall, discardType, Scores(), pot);
                if (caller < 0)
                    break;

                p.River.RemoveAt(p.River.Count - 1);
                players[caller].TakePon(discardType, akaLeft);
                foreach (SimPlayer pl in players)
                    pl.Ippatsu = false;

                turn = caller;
                p = players[turn];
                p.TempFuriten = false;

                TurnDecision after = Decide(agents, turn, BuildInput(players, turn, setup, wall, Scores(), pot, riichiLegal: false), stats);
                discardType = after.Discard >= 0 && p.Counts[after.Discard] > 0 ? after.Discard : FirstHeld(p);
            }

            turn = (turn + 1) % 4;
        }

        return ScoreExhaustiveDraw(dealer, deltas, pot, declared, players);
    }

    private static TurnDecision Decide(Agent[] agents, int seat, TurnInput input, SimStats stats)
    {
        Agent agent = agents[seat];
        if (!agent.Timed)
            return agent.Turn(input);

        long start = Stopwatch.GetTimestamp();
        TurnDecision decision = agent.Turn(input);
        long ticks = Stopwatch.GetTimestamp() - start;
        stats.RecordDecision(ticks);
        return decision;
    }

    /// <summary>At most one seat can hold two copies of a discarded tile; returns it if its agent pons.</summary>
    private static int FindPonCaller(Agent[] agents, SimPlayer[] players, int discarder, HandSetup setup, Wall wall, int discardType, int[] scores, int pot)
    {
        for (int k = 1; k < 4; k++)
        {
            int seat = (discarder + k) % 4;
            SimPlayer opp = players[seat];
            if (opp.Riichi || opp.Counts[discardType] < 2)
                continue;

            bool anyRiichi = false;
            for (int s = 0; s < 4; s++)
                if (s != seat && players[s].Riichi)
                    anyRiichi = true;

            TurnInput view = BuildInput(players, seat, setup, wall, scores, pot, riichiLegal: false);
            var input = new CallInput
            {
                Counts13 = opp.Counts,
                Melds = opp.Melds,
                Tile = discardType,
                Context = view.Context,
                OpponentRiichi = anyRiichi,
                Seen = view.Seen,
                Opponents = view.Opponents,
                Game = view.Game,
                WallRemaining = view.WallRemaining,
            };

            if (agents[seat].Pon(input))
                return seat;
        }

        return -1;
    }

    private static int FirstHeld(SimPlayer p)
    {
        for (int t = 0; t < Tiles.TypeCount; t++)
            if (p.Counts[t] > 0)
                return t;
        return 0;
    }

    private static EvalContext EvalCtx(SimPlayer p, int seat, HandSetup setup, Wall wall, ScoringOptions scoring)
        => new()
        {
            SeatWind = (seat - setup.Dealer + 4) % 4,
            RoundWind = setup.RoundWind,
            Dealer = seat == setup.Dealer,
            Closed = p.Melds.Count == 0,
            DoraIndicators = new[] { wall.DoraIndicatorType },
            AkaByType = p.AkaByType,
            Scoring = scoring,
        };

    private static TurnInput BuildInput(SimPlayer[] players, int turn, HandSetup setup, Wall wall, int[] scores, int pot, bool riichiLegal)
    {
        SimPlayer p = players[turn];

        var seen = new int[Tiles.TypeCount];
        Array.Copy(p.Counts, seen, Tiles.TypeCount);
        foreach (SimPlayer other in players)
        {
            foreach (int t in other.River)
                seen[t]++;
            foreach (Meld meld in other.Melds)
                foreach (int t in meld.Tiles)
                    seen[t]++;
        }

        seen[wall.DoraIndicatorType] = Math.Min(4, seen[wall.DoraIndicatorType] + 1);

        var threats = new List<IReadOnlyList<int>>();
        var opponents = new List<OpponentView>(3);
        for (int k = 1; k < 4; k++)
        {
            int s = (turn + k) % 4;
            SimPlayer o = players[s];
            if (o.Riichi)
                threats.Add(o.River);
            opponents.Add(new OpponentView
            {
                Seat = s,
                River = o.River,
                Riichi = o.Riichi,
                RiichiDiscardIndex = o.RiichiDiscardIndex,
                Melds = o.Melds,
                IsDealer = s == setup.Dealer,
            });
        }

        return new TurnInput
        {
            Counts14 = p.Counts,
            Melds = p.Melds,
            Seen = seen,
            Context = EvalCtx(p, turn, setup, wall, setup.Scoring),
            ThreatRivers = threats,
            OwnRiver = p.River,
            RiichiAvailable = riichiLegal,
            Opponents = opponents,
            WallRemaining = wall.Remaining,
            Game = new GameState
            {
                Scores = scores,
                SelfSeat = turn,
                DealerSeat = setup.Dealer,
                HandNumber = setup.HandNumber,
                TotalHands = setup.TotalHands,
                Honba = setup.Honba,
                RiichiSticks = pot,
            },
        };
    }

    private static WinContext WinCtx(
        SimPlayer p, int seat, HandSetup setup, int winTile, bool tsumo, bool lastTile, Wall wall, int extraAka)
        => new()
        {
            WinTile = winTile,
            Tsumo = tsumo,
            Riichi = p.Riichi,
            Ippatsu = p.Ippatsu,
            Haitei = tsumo && lastTile,
            Houtei = !tsumo && lastTile,
            SeatWind = (seat - setup.Dealer + 4) % 4,
            RoundWind = setup.RoundWind,
            AkaCount = p.AkaByType.Sum() + p.MeldAka + extraAka,
            DoraIndicators = new[] { wall.DoraIndicatorType },
            UraIndicators = new[] { wall.UraIndicatorType },
        };

    /// <summary>River furiten: ron is dead while any tile of the wait set sits in the player's own river.</summary>
    private static bool IsRiverFuriten(SimPlayer p, int justAdded)
    {
        p.Counts[justAdded]--;
        try
        {
            for (int t = 0; t < Tiles.TypeCount; t++)
            {
                if (p.Counts[t] >= 4)
                    continue;
                p.Counts[t]++;
                bool wins = ShantenCalculator.Calculate(p.Counts, p.Melds.Count) == ShantenCalculator.Complete;
                p.Counts[t]--;
                if (wins && p.River.Contains(t))
                    return true;
            }

            return false;
        }
        finally
        {
            p.Counts[justAdded]++;
        }
    }

    private static HandOutcome Settle(
        HandScore score, int winner, int discarder, HandSetup setup, int[] deltas, int pot, bool[] declared, SimRules rules)
    {
        int[] pay = ScoreCalculator.Payments(score, winner, setup.Dealer, discarder, setup.Honba, pot, setup.Scoring);
        for (int s = 0; s < 4; s++)
            deltas[s] += pay[s];

        return new HandOutcome
        {
            Winner = winner,
            DealIn = discarder,
            Deltas = deltas,
            DealerKeeps = winner == setup.Dealer,
            SticksLeft = 0,
            RiichiDeclared = declared,
            WinHan = score.Han,
            WinPoints = score.Points,
        };
    }

    private static HandOutcome ScoreExhaustiveDraw(int dealer, int[] deltas, int pot, bool[] declared, SimPlayer[] players)
    {
        var tenpai = new bool[4];
        int tenpaiCount = 0;
        for (int s = 0; s < 4; s++)
        {
            tenpai[s] = ShantenCalculator.Calculate(players[s].Counts, players[s].Melds.Count) <= 0;
            if (tenpai[s])
                tenpaiCount++;
        }

        if (tenpaiCount is > 0 and < 4)
        {
            int gain = 3000 / tenpaiCount;
            int loss = 3000 / (4 - tenpaiCount);
            for (int s = 0; s < 4; s++)
                deltas[s] += tenpai[s] ? gain : -loss;
        }

        return new HandOutcome
        {
            Deltas = deltas,
            ExhaustiveDraw = true,
            DealerKeeps = tenpai[dealer],
            SticksLeft = pot,
            RiichiDeclared = declared,
        };
    }
}
