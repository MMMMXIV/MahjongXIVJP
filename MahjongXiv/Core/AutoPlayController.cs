using MahjongXiv.Core.Game;
using MahjongXiv.Engine;
using MahjongXiv.Localization;
using Dalamud.Plugin.Services;

namespace MahjongXiv.Core;

/// <summary>
/// Live cockpit driver with two modes (see <see cref="PlayMode"/>). In Auto it runs the autonomous play
/// loop: each of our turns it discards the engine's best tile, declares riichi or tsumo when live, and on
/// call popups accepts Ron, takes a pon when <see cref="CallPolicy"/> approves, or passes; it advances
    /// leaves result / new-hand screens to the game. In Suggest it sends no inputs — the cockpit shows the move and
/// the player makes it. Casual tables only; unattended automation of a PvP mode.
/// </summary>
internal sealed class AutoPlayController : IDisposable
{
    private const long TickIntervalMs = 120;

    /// <summary>Pause between firing the riichi option and cutting the riichi tile, letting the prompt close.</summary>
    private const int RiichiFollowupDelayMs = 450;

    // A sent popup is never retried while the same offer remains visible.
    private const int PopupRetryDelayMs = 700;
    private const int MaxPopupAttempts = 1;

    /// <summary>
    /// Hold all actuation this long after the Emj addon first appears (or reappears). Acting while
    /// the table is still dealing/animating wedged a match live: the state codes are readable well
    /// before the table actually accepts inputs.
    /// </summary>
    private const int MatchStartGraceMs = 6000;

    /// <summary>Warn once if an actionable state sits unresolved this long — surfaces silent stalls.</summary>
    private const int StuckWarnMs = 12000;

    private readonly Random rng = new();
    private readonly DecisionWorker worker = new();

    private bool running;
    private bool hooked;
    private long nextTickAllowed;
    private int prevState = int.MinValue;
    private long actAtTick;
    private bool acted;
    private bool riichiFired;
    private int riichiTile = -1; // the tile chosen with the riichi call; cut it once the prompt closes
    private int popupAttempts;
    private bool addonAbsent = true;
    private long graceUntil;
    private long stateSince;
    private bool stuckWarned;
    private string waitReason = "waiting for ready hand";
    private string? stableHand;
    private long stableHandSince;
    private nint tableAddress;
    private bool postCallTurn;

    private readonly RiverProgress riverProgress = new();
    private int lastDiscardTile = -1;

    /// <summary>The cockpit is engaged (showing live reads), in either mode.</summary>
    public bool Running => running;

    public PlayMode Mode => Plugin.Cfg.Mode;

    /// <summary>What the bot has done since the cockpit was last engaged.</summary>
    public SessionStats Stats { get; } = new();

    /// <summary>Why auto-play last disengaged itself at a non-casual table; cleared on the next engage.</summary>
    public string? BlockedReason { get; private set; }

    public void SetRunning(bool on)
    {
        if (on == running)
            return;
        running = on;

        if (on)
        {
            BlockedReason = null;
            Stats.Reset();
            MjLog.Info(Mode == PlayMode.Auto
                ? "Auto-play ON: guarded discards/calls; result screens and new-hand acknowledgement are left to the game."
                : "Suggestions ON: showing the best move each turn. No inputs are sent for you.");
        }
        else
        {
            MjLog.Info("Cockpit OFF.");
        }

        UpdateHook();
    }

    public void SetMode(PlayMode mode)
    {
        if (Plugin.Cfg.Mode == mode)
            return;
        Plugin.Cfg.Mode = mode;
        Plugin.Cfg.Save();

        if (running)
            MjLog.Info(mode == PlayMode.Auto ? "Switched to Auto-play." : "Switched to Suggestions only.");

        UpdateHook();
    }

    /// <summary>The actuation loop only runs while engaged in Auto; Suggest mode leaves it unhooked.</summary>
    private void UpdateHook()
    {
        bool shouldHook = running && Mode == PlayMode.Auto;
        if (shouldHook == hooked)
            return;
        hooked = shouldHook;
        EmjActuator.Reset();
        stableHand = null;
        tableAddress = 0;
        riverProgress.Reset();
        lastDiscardTile = -1;

        if (hooked)
        {
            prevState = int.MinValue;
            acted = false;
            addonAbsent = true; // even mid-match, take the settle grace before the first action
            graceUntil = 0;
            Plugin.Framework.Update += OnUpdate;
        }
        else
        {
            Plugin.Framework.Update -= OnUpdate;
        }
    }

    private void OnUpdate(IFramework framework)
    {
        try { Tick(); }
        catch (Exception ex) { StopUnsafe($"managed update error: {ex.GetType().Name}: {ex.Message}"); }
    }

    private void Tick()
    {
        long now = Environment.TickCount64;
        if (now < nextTickAllowed)
            return;
        nextTickAllowed = now + TickIntervalMs;

        int state = EmjBoardReader.ReadStateCode();
        nint address = EmjBoardReader.CurrentAddress;
        if (tableAddress != 0 && address != tableAddress)
        {
            EmjActuator.Reset();
            prevState = int.MinValue;
            addonAbsent = true;
            riverProgress.Reset();
            stableHand = null;
        }
        tableAddress = address;

        if (state < 0)
        {
            if (!addonAbsent)
            {
                addonAbsent = true;
                prevState = int.MinValue;
                riverProgress.Reset();
                lastDiscardTile = -1;
            }

            return;
        }

        if (addonAbsent)
        {
            TableKind table = EmjTableGuard.Current;
            if (EmjTableGuard.BlockReason(table) is { } reason)
            {
                Block(reason, table);
                return;
            }

            addonAbsent = false;
            graceUntil = now + MatchStartGraceMs;
            MjLog.Info($"auto: table detected (state {state}, addon {EmjBoardReader.CurrentAddonName}, {table}, ContentFinderCondition {EmjTableGuard.CurrentCfc}); settling for {MatchStartGraceMs / 1000}s before acting.");
        }

        // prevState stays untouched during the grace so the first live tick re-arms cleanly.
        if (now < graceUntil)
            return;

        if (EmjActuator.Failure is { } failure)
        {
            StopUnsafe(failure);
            return;
        }
        bool newDiscard = TrackDiscards();

        // Never reset a pending handshake merely because its own highlight changed the state.
        if (state != prevState)
            MjLog.Info($"auto: observed state {prevState}->{state}, acted={acted}, waiting={waitReason}");

        // Back-to-back call offers can stay in state 15 throughout; a fresh opponent discard means a
        // fresh popup, so it gets its own delay and retry budget instead of inheriting the last one's.
        bool newOffer = state == 15 && prevState == 15 && newDiscard;

        if (DiscardHandshake.ShouldRearm(prevState, state, newDiscard))
        {
            EmjActuator.Reset();
            stableHand = null;
            postCallTurn = state == 28;
            if (state == 2) { riverProgress.Reset(); lastDiscardTile = -1; }
            waitReason = "waiting for ready hand/options";
            MjLog.Info($"auto: state {prevState}->{state}, newOffer={newOffer}; starting observation");
            acted = false;
            riichiFired = false;
            riichiTile = -1;
            popupAttempts = 0;
            actAtTick = now + DelayFor(state);
            stateSince = now;
            stuckWarned = false;
        }
        prevState = state;

        if (!stuckWarned && state is 2 or 6 or 15 or 28 or 30 && now - stateSince > StuckWarnMs)
        {
            stuckWarned = true;
            StopUnsafe($"state={state} unresolved for {now - stateSince}ms; {waitReason}; acted={acted}, popupAttempts={popupAttempts}");
            return;
        }

        if (acted || now < actAtTick)
            return;

        switch (state)
        {
            case 6 or 30:
                DoTurn(afterCall: postCallTurn);
                break;
            case 28:
                // Post-call discard surface: a tile must be cut, but no win/riichi is possible here.
                DoTurn(afterCall: true);
                break;
            case 15:
                DoCall();
                break;
            // State 2 (new-hand acknowledge) is intentionally NOT handled: the game fires its own
            // [9] when the deal animation finishes. Sending ours too caused a duplicate ack that
            // desynced the table (two live freezes both traced to a stray [9] ~3-4s after ours).
            case 29 or 32:
                AdvanceResult(state);
                break;
            case 41 or 42:
                // Final standings: no synthesized events, including Next/Leave/confirmation.
                AdvanceFinal(state);
                break;
        }
    }

    private void AdvanceFinal(int state) => LeaveResultToGame(state);
    private void AdvanceResult(int state) => LeaveResultToGame(state);

    private void LeaveResultToGame(int state)
    {
        MjLog.Info($"auto: result state={state}; waiting for manual advance or native timer (no injected input)");
        acted = true;
    }

    /// <summary>
    /// Refuse to act at a non-casual table: disengage, and tell the player in chat and in the cockpit
    /// (the plugin log alone goes unseen). Suggest mode is unaffected — it never hooks this loop.
    /// </summary>
    private void StopUnsafe(string detail)
    {
        BlockedReason = "自動操作を安全のため停止しました。手動で続行してください。";
        MjLog.Warn($"auto: FAIL-CLOSED {detail}");
        Plugin.ChatGui.PrintError($"{MjConstants.LogPrefix} {BlockedReason} ({detail})");
        SetRunning(false);
    }

    private void Block(string reason, TableKind table)
    {
        BlockedReason = reason;
        MjLog.Warn($"auto: refused table ({table}, ContentFinderCondition {EmjTableGuard.CurrentCfc}).");
        Plugin.ChatGui.PrintError($"{MjConstants.LogPrefix} {reason}");
        SetRunning(false);
    }

    /// <summary>
    /// Watch the opponents' rivers grow to learn the most recent discard — the call popup itself
    /// does not say which tile is being offered. The first read after the addon appears only seeds
    /// the lengths (mid-hand engages would otherwise mistake the whole river for one discard).
    /// </summary>
    /// <returns>Whether an opponent's river grew since the last read.</returns>
    private bool TrackDiscards()
    {
        BoardSnapshot? board = EmjBoardReader.TryReadBoard();
        if (board is null)
            return false;

        bool grew = riverProgress.Observe(board.Rivers, MjConstants.SelfIndex, out int tile);
        if (grew) lastDiscardTile = tile;
        return grew;
    }

    private void DoTurn(bool afterCall)
    {
        if (!EmjBoardReader.TryReadHand(out HandSnapshot snap))
        {
            stableHand = null;
            waitReason = "hand unreadable";
            return;
        }
        if (snap.TilesRead != 14 - 3 * snap.MeldCount)
        {
            stableHand = null;
            waitReason = $"hand incomplete: read={snap.TilesRead}, melds={snap.MeldCount}, expected={14 - 3 * snap.MeldCount}";
            return;
        }
        string handKey = string.Join(",", snap.SlotIcons) + ":" + snap.MeldCount;
        long now = Environment.TickCount64;
        if (stableHand != handKey)
        {
            stableHand = handKey;
            stableHandSince = now;
            waitReason = "waiting for hand animation to settle";
            return;
        }
        if (now - stableHandSince < 240) return;

        if (EmjActuator.HasPendingDiscard)
        {
            waitReason = "discard handshake/acknowledgement pending";
            if (EmjActuator.ContinueDiscard())
            {
                acted = true;
                Stats.Record(AutoActionKind.Discard, "打牌入力送信");
            }
            return;
        }

        if (!afterCall && ShantenCalculator.Calculate(snap.Counts, snap.MeldCount) == ShantenCalculator.Complete)
        {
            if (popupAttempts >= MaxPopupAttempts)
            {
                MjLog.Warn("turn: win panel did not accept the fire after retries; yielding to the game timer.");
                acted = true;
                return;
            }

            // A complete shape alone does not identify a live, actionable Tsumo popup.
            var actions = EmjBoardReader.ReadActionOptions();
            int tsumo = FindOption(actions, EmjAction.Tsumo);
            int option = tsumo; // Unknown labels never authorize a guessed option.
            if (option < 0) { waitReason = "winning hand but no Tsumo option readable"; return; }

            if (EmjActuator.Call(option))
            {
                popupAttempts++;
                MjLog.Info($"turn: tsumo -> [11, {option}]{(tsumo < 0 ? " (unlabeled panel)" : "")} (attempt {popupAttempts}).");
                if (popupAttempts == 1)
                    Stats.Record(AutoActionKind.Tsumo, Loc.T("act.tsumo"));
                actAtTick = Environment.TickCount64 + PopupRetryDelayMs; // observe on later ticks; never re-send the same offer
            }

            return;
        }

        if (TryResolveNineTerminals(snap))
            return;

        BoardSnapshot? board = EmjBoardReader.TryReadBoard();

        // In riichi the hand is locked: the win/kan cases were handled above, so tsumogiri the draw.
        if (board?.OwnRiichi == true)
        {
            if (EmjActuator.DiscardSlot(13))
            {
                acted = true;
                Stats.Record(AutoActionKind.Discard, Loc.T("act.tsumogiri"));
            }
            return;
        }

        // After declaring riichi, cut exactly the tile the declaration was decided with.
        if (riichiFired && riichiTile >= 0)
        {
            if (EmjActuator.DiscardTile(riichiTile))
            {
                acted = true;
                Stats.Record(AutoActionKind.Discard, Loc.F("act.discard", Loc.Tile(riichiTile)));
            }
            return;
        }

        int riichiOption = afterCall ? -1 : FindOption(EmjBoardReader.ReadActionOptions(), EmjAction.Riichi);

        TurnInput input = TableContext.BuildTurnInput(snap, board, riichiAvailable: riichiOption >= 0 && !riichiFired);
        if (!worker.TryGet(DecisionWorker.KeyFor(input), input, out TurnDecision decision, out _))
        {
            waitReason = "decision worker pending";
            return;
        }

        if (decision.Discard < 0)
        {
            StopUnsafe($"decision unavailable: {decision.Reason}");
            return;
        }
        waitReason = "discard handshake/acknowledgement pending";

        if (decision.Reason != "best value" && decision.Reason != "best value + riichi")
            MjLog.Info($"turn: {decision.Reason}.");

        if (decision.DeclareRiichi && EmjActuator.Call(riichiOption))
        {
            // The riichi tile is cut on the next eligible tick, once the prompt has closed.
            riichiFired = true;
            riichiTile = decision.Discard;
            actAtTick = Environment.TickCount64 + RiichiFollowupDelayMs;
            MjLog.Info($"turn: riichi declared -> [11, {riichiOption}]; cutting {Tiles.Name(decision.Discard)} next tick.");
            Stats.Record(AutoActionKind.Riichi, Loc.T("act.riichi"));
            return;
        }

        if (EmjActuator.DiscardTile(decision.Discard))
        {
            acted = true;
            Stats.Record(AutoActionKind.Discard, Loc.F("act.discard", Loc.Tile(decision.Discard)));
        }
    }

    /// <summary>
    /// Answer the nine-terminals-and-honors (kyuushu kyuuhai) abort prompt. Default is to take the
    /// redeal — nine scattered terminals/honors is a 4-5 shanten hand. The exception is a strong
    /// kokushi start (thirteen-orphans shanten ≤ 2, i.e. 11+ types or 10 with a pair), which is a
    /// real yakuman attempt and worth keeping. Returns true while the prompt is being handled.
    /// </summary>
    private bool TryResolveNineTerminals(in HandSnapshot snap)
    {
        var actions = EmjBoardReader.ReadActionOptions();
        int abort = FindOption(actions, EmjAction.NineTerminals);
        if (abort < 0)
            return false;

        if (popupAttempts >= MaxPopupAttempts)
        {
            MjLog.Warn("turn: nine-terminals prompt did not accept the fire after retries; yielding to the game timer.");
            acted = true;
            return true;
        }

        int kokushiShanten = ShantenCalculator.ThirteenOrphans(snap.Counts);
        int pass = FindOption(actions, EmjAction.Pass);
        bool keepHand = kokushiShanten <= 2 && pass >= 0;
        int option = keepHand ? pass : abort;

        if (EmjActuator.Call(option))
        {
            popupAttempts++;
            MjLog.Info(keepHand
                ? $"turn: nine terminals offered, keeping the hand (kokushi shanten {kokushiShanten}) -> [11, {option}] (attempt {popupAttempts})."
                : $"turn: nine terminals -> taking the redeal (kokushi shanten {kokushiShanten}) -> [11, {option}] (attempt {popupAttempts}).");
            actAtTick = Environment.TickCount64 + PopupRetryDelayMs; // observe on later ticks; never re-send the same offer
        }

        return true;
    }

    private void DoCall()
    {
        if (popupAttempts >= MaxPopupAttempts) return;

        var options = EmjBoardReader.ReadActionOptions();
        string optionKey = string.Join("|", options.Select(o => $"{o.Option}:{o.Label}"));
        long now = Environment.TickCount64;
        if (stableHand != optionKey)
        {
            stableHand = optionKey;
            stableHandSince = now;
            waitReason = "waiting for call options to settle";
            return;
        }
        if (now - stableHandSince < 240) return;
        CallDecision decision = DecideCall(options);
        if (decision.Option < 0)
            return; // popup not readable yet, or nothing safe to click — wait for the next tick

        if (decision.Reason == "pass")
            decision = MaybeUpgradeToPon(options, decision);

        if (EmjActuator.Call(decision.Option))
        {
            popupAttempts++;
            acted = true;
            waitReason = "call sent once; waiting for a new state or opponent discard";
            MjLog.Info($"call: {decision.Reason} -> [11, {decision.Option}] (attempt {popupAttempts}).");
            if (popupAttempts == 1)
            {
                bool win = decision.Reason is "accept Ron" or "accept Tsumo";
                bool meld = decision.Reason.StartsWith("pon", StringComparison.Ordinal);
                Stats.Record(
                    win ? AutoActionKind.Ron : meld ? AutoActionKind.Meld : AutoActionKind.Pass,
                    win ? Loc.T("act.win") : meld ? Loc.F("act.pon", Loc.Tile(lastDiscardTile)) : Loc.T("act.pass"));
            }

            actAtTick = Environment.TickCount64 + PopupRetryDelayMs; // observe on later ticks; never re-send the same offer
        }
    }

    /// <summary>
    /// When the safe default is to pass but the popup offers a pon, ask <see cref="CallPolicy"/>
    /// whether opening the hand on the tracked last discard is worth it. Everything must read
    /// cleanly (offered tile known, full 13-tile concealed hand) or the pass stands — a wrong pon
    /// is far worse than a missed one. Chi/kan stay declined: chi has an unverified
    /// variant-selection UI and kan flips dora the reader cannot see.
    /// </summary>
    private CallDecision MaybeUpgradeToPon(IReadOnlyList<ActionOption> options, CallDecision pass)
    {
        int pon = FindOption(options, EmjAction.Pon);
        if (pon < 0 || lastDiscardTile < 0)
            return pass;

        if (!EmjBoardReader.TryReadHand(out HandSnapshot snap) || snap.TilesRead != 13 - 3 * snap.MeldCount)
            return pass;

        BoardSnapshot? board = EmjBoardReader.TryReadBoard();
        bool take;
        if (Plugin.Cfg.StrategicAi)
        {
            // Priced pon: a one-off evaluation (tens of milliseconds at most) when a call is offered.
            TurnInput view = TableContext.BuildTurnInput(snap, board, riichiAvailable: false);
            take = CallPolicy.ShouldPon(new CallInput
            {
                Counts13 = snap.Counts,
                Melds = snap.Melds,
                Tile = lastDiscardTile,
                Context = view.Context,
                OpponentRiichi = board?.AnyOpponentRiichi ?? false,
                Seen = view.Seen,
                Opponents = view.Opponents,
                Game = view.Game,
            }, EvalOptions.Strategic);
        }
        else
        {
            take = CallPolicy.ShouldPon(
                snap.Counts, snap.Melds, lastDiscardTile,
                TableContext.BuildEvalContext(snap, board), board?.AnyOpponentRiichi ?? false);
        }

        return take ? new CallDecision(pon, $"pon {Tiles.Name(lastDiscardTile)}") : pass;
    }

    /// <summary>
    /// Accept a win (Ron or Tsumo — the game only offers valid wins) if offered, otherwise pass.
    /// The pass may be upgraded to a pon afterwards (<see cref="MaybeUpgradeToPon"/>); chi and kan
    /// are never taken. The option to fire is resolved from the button labels, not a fixed index.
    /// Option -1 means "do nothing" — better to wait than to mis-click a call we didn't intend.
    /// </summary>
    internal static CallDecision DecideCall(IReadOnlyList<ActionOption> options)
    {
        if (options.Count == 0)
            return new CallDecision(-1, "no options readable");

        int ron = FindOption(options, EmjAction.Ron);
        if (ron >= 0)
            return new CallDecision(ron, "accept Ron");

        int tsumo = FindOption(options, EmjAction.Tsumo);
        if (tsumo >= 0)
            return new CallDecision(tsumo, "accept Tsumo");

        int pass = FindOption(options, EmjAction.Pass);
        if (pass >= 0)
            return new CallDecision(pass, "pass");

        return new CallDecision(-1, $"no Ron/Tsumo/Pass among [{Labels(options)}]");
    }

    /// <summary>Non-destructively log the current popup's options and the choice the bot would make.</summary>
    public static void LogOptions()
    {
        var options = EmjBoardReader.ReadActionOptions();
        if (options.Count == 0)
        {
            MjLog.Info("options: no call/self-action buttons in the current popup.");
            return;
        }

        foreach (ActionOption o in options)
            MjLog.Info($"  [opt {o.Option}] '{o.Label}'");

        CallDecision d = DecideCall(options);
        MjLog.Info(d.Option >= 0
            ? $"options: bot would {d.Reason} -> [11, {d.Option}]."
            : $"options: bot would not act ({d.Reason}).");
    }

    /// <summary>The fire value of the button doing <paramref name="action"/>, in any recognized client language, or -1.</summary>
    private static int FindOption(IReadOnlyList<ActionOption> options, EmjAction action)
    {
        // Automatic input must never guess that an unrecognized label means Pass.
        foreach (ActionOption option in options)
            if (EmjStrings.Classify(option.Label) == action)
                return option.Option;
        return -1;
    }

    private static string Labels(IReadOnlyList<ActionOption> options)
        => string.Join(", ", options.Select(o => $"'{o.Label}'"));

    internal readonly record struct CallDecision(int Option, string Reason);

    private int DelayFor(int state)
    {
        Configuration cfg = Plugin.Cfg;
        return state switch
        {
            6 or 28 or 30 => Roll(cfg.TurnDelayMinMs, cfg.TurnDelayMaxMs),   // human think time before discarding
            15 => Roll(cfg.CallDelayMinMs, cfg.CallDelayMaxMs),        // call-decision reaction
            29 or 32 or 41 or 42 => Roll(cfg.ResultDelayMinMs, cfg.ResultDelayMaxMs), // let the result screen settle
            _ => 0,
        };
    }

    private int Roll(int min, int max) => max <= min ? min : rng.Next(min, max);

    public void Dispose()
    {
        if (hooked)
            Plugin.Framework.Update -= OnUpdate;
    }
}
