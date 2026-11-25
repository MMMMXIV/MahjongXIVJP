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
            yieldGuard.Reset();
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
            // The previous turn/popup ended with an action of ours (not a yield): the yield run is broken.
            if (acted && !yieldedThisState)
                yieldGuard.RecordSuccess();
            yieldedThisState = false;
            EmjActuator.Reset();
            stableHand = null;
            frozenForbidden = null;
            tsumoButtonPressed = false;
            noWinLogged = false;
            meldSupplyLogged = false;
            incompleteLogged = false;
            if (state is 2 or 6)
                ponnedTile = -1; // a new hand, or a normal draw: no pon is pending any more
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

        // Remember the last complete hand read outside our own turn. While a call popup is up the hand
        // can read incomplete (seen live), and between our discard and our next draw the hand cannot
        // change, so this copy is what the pon decision uses then.
        if (state is 2 or 6 or 28 or 30)
        {
            handBeforePopup = null;
        }
        else if (EmjBoardReader.TryReadHand(out HandSnapshot outside) && outside.TilesRead == 13 - 3 * outside.MeldCount)
        {
            handBeforePopup = outside;
        }

        // State 15 is also the plain "waiting for the other players" state (it alternates with 7/9 while
        // opponents act). Only a visible call popup is something for us to resolve, so the stall guard
        // for 15 counts from when a popup is on screen, not from when the state began: a long-thinking
        // opponent must not stop auto-play.
        if (state == 15)
        {
            bool offerVisible = EmjBoardReader.ReadActionOptions().Count > 0;
            if (!offerVisible)
                offerSince = 0;
            else if (offerSince == 0)
                offerSince = now;
        }
        else
        {
            offerSince = 0;
        }

        // Once our single answer is sent, nothing more is sent to this popup, so there is nothing unsafe
        // left to stop: the game's own timer moves the table on. A different popup appearing meanwhile
        // (e.g. a second question after a pon) is recorded so it can be supported.
        if (state == 15 && acted)
        {
            var now15 = EmjBoardReader.ReadActionOptions();
            string key15 = string.Join("|", now15.Select(o => $"{o.Option}:{o.Label}"));
            if (now15.Count > 0 && key15 != answeredOfferKey && key15 != loggedFollowUpKey)
            {
                loggedFollowUpKey = key15;
                MjLog.Warn($"call: another popup after our answer (left to the game): [{string.Join(" | ", now15.Select(o => $"#{o.Option} '{o.Label}'"))}].");
                EmjAddonDumper.DumpValues();
            }
        }

        long unresolvedSince = state == 15
            ? (offerSince == 0 || acted ? now : offerSince)
            : stateSince;
        if (!stuckWarned && state is 2 or 6 or 15 or 28 or 30 && now - unresolvedSince > StuckWarnMs)
        {
            stuckWarned = true;
            YieldToGame(state, $"state={state} unresolved for {now - unresolvedSince}ms; {waitReason}; acted={acted}, popupAttempts={popupAttempts}");
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
    private string? unreadableOfferKey;

    /// <summary>The label's characters as code points, to tell an unknown word from unreadable text.</summary>
    private static string Escape(string label)
        => string.Join(" ", label.Take(12).Select(c => ((int)c).ToString("X4")));

    private readonly YieldGuard yieldGuard = new();
    private bool yieldedThisState;

    /// <summary>Times the driver left a turn or popup to the game's timer in this session.</summary>
    public int Yields => yieldGuard.Total;

    /// <summary>
    /// Not knowing what to do is not unsafe: the game's timer plays this turn (tsumogiri) or passes this
    /// call. So the driver sends nothing more for this state, records a one-off snapshot for diagnosis,
    /// and resumes at the next state. Only <see cref="YieldGuard.Limit"/> yields in a row (no action of
    /// ours going through in between) mean the reading is broken, and then auto-play stops.
    /// </summary>
    private void YieldToGame(int state, string detail)
    {
        acted = true;
        yieldedThisState = true;
        waitReason = "left to the game timer";
        Stats.Record(AutoActionKind.Yield, Loc.T("act.yield"));
        MjLog.Warn($"auto: YIELD (left to the game timer, auto-play continues): {detail}");
        LogSnapshot(state);
        if (yieldGuard.RecordYield())
            StopUnsafe($"{YieldGuard.Limit} turns in a row could not be played: {detail}");
    }

    /// <summary>One block with everything needed to understand a new case from a single log.</summary>
    private static void LogSnapshot(int state)
    {
        try
        {
            var options = EmjBoardReader.ReadActionOptions();
            MjLog.Info($"snapshot: state={EmjBoardReader.ReadStateCode()} (yielded at {state}), addon={EmjBoardReader.CurrentAddonName}, options=[{string.Join(" | ", options.Select(o => $"#{o.Option} '{o.Label}'"))}]");
            if (EmjBoardReader.TryReadHand(out HandSnapshot hand))
                MjLog.Info($"snapshot: hand {hand.Notation} (read {hand.TilesRead}, melds {hand.MeldCount}), shanten {ShantenCalculator.Calculate(hand.Counts, hand.MeldCount)}");
            bool[]? enabled = EmjClicker.ReadEnabledSlots();
            MjLog.Info($"snapshot: enabled tile slots: {(enabled is null ? "none" : string.Join("", enabled.Select(e => e ? '1' : '0')))}");
            EmjClicker.DumpButtons();
            EmjAddonDumper.DumpValues();
        }
        catch (Exception ex)
        {
            MjLog.Warn($"snapshot failed: {ex.Message}");
        }
    }

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

    /// <summary>
    /// Tiles the table will refuse: after a pon the called tile (kuikae; the game disables it, and the
    /// handshake pins its target at the first highlight, so this must be known before choosing), plus
    /// any tile type whose every copy in hand shows a disabled button while other tiles are enabled.
    /// </summary>
    private IReadOnlyList<int> ForbiddenDiscards(in HandSnapshot snap, bool afterCall)
    {
        var forbidden = new List<int>();
        if (afterCall && snap.Melds.Count > 0 && snap.Melds[^1].Kind == MeldKind.Pon)
            forbidden.Add(snap.Melds[^1].Tiles[0]);

        bool[]? enabled = EmjClicker.ReadEnabledSlots();
        if (enabled is not null && snap.SlotTiles is { } slots)
        {
            for (int t = 0; t < Tiles.TypeCount; t++)
            {
                bool held = false, anyEnabled = false;
                for (int s = 0; s < slots.Length && s < enabled.Length; s++)
                {
                    if (slots[s] != t)
                        continue;
                    held = true;
                    anyEnabled |= enabled[s];
                }
                if (held && !anyEnabled && !forbidden.Contains(t))
                    forbidden.Add(t);
            }
        }

        if (forbidden.Count > 0 && forbidden.Count != lastForbiddenCount)
            MjLog.Info($"turn: not selectable now: {string.Join(" ", forbidden.Select(Tiles.Name))}{(afterCall ? " (after call)" : "")}.");
        lastForbiddenCount = forbidden.Count;
        return forbidden;
    }

    private int lastForbiddenCount;

    /// <summary>How long the post-pon turn waits for our new meld to read before supplying it.</summary>
    private const int MeldReadGraceMs = 1500;

    /// <summary>The tile of the pon we sent and that has not been discarded after yet (-1 none).</summary>
    private int ponnedTile = -1;
    private bool meldSupplyLogged;
    private bool incompleteLogged;

    /// <summary>How long a complete shape waits for a Tsumo option before the turn is played normally.</summary>
    private const int NoWinGraceMs = 1500;

    private bool tsumoButtonPressed;
    private bool noWinLogged;

    /// <summary>
    /// The forbidden set is read once per turn and kept: a changing set would change the decision key
    /// while the discard handshake has already pinned its target. Cleared whenever a turn re-arms.
    /// </summary>
    private IReadOnlyList<int>? frozenForbidden;

    private void DoTurn(bool afterCall)
    {
        if (!EmjBoardReader.TryReadHand(out HandSnapshot snap))
        {
            stableHand = null;
            waitReason = "hand unreadable";
            return;
        }
        // Right after our pon the hand already shows three tiles fewer while the new meld can still be
        // animating in (or not decode). Give the meld read a moment, then supply the pon we know we made.
        if (afterCall && ponnedTile >= 0 && snap.TilesRead == 14 - 3 * (snap.MeldCount + 1)
            && !snap.Melds.Any(m => m.Kind == MeldKind.Pon && m.Tiles[0] == ponnedTile))
        {
            if (Environment.TickCount64 - stateSince < MeldReadGraceMs)
            {
                stableHand = null;
                waitReason = $"waiting for our pon of {Tiles.Name(ponnedTile)} to show";
                return;
            }

            var melds = new List<Meld>(snap.Melds) { new Meld(MeldKind.Pon, [ponnedTile, ponnedTile, ponnedTile]) };
            snap = new HandSnapshot(snap.Counts, snap.AkaByType, snap.TilesRead, snap.Notation, snap.SlotTiles, snap.SlotIcons, melds, snap.VisibleNodes);
            if (!meldSupplyLogged)
            {
                meldSupplyLogged = true;
                MjLog.Warn($"turn: our pon of {Tiles.Name(ponnedTile)} did not read from the meld strip; using it from the call (read {snap.TilesRead} tiles).");
            }
        }

        if (snap.TilesRead != 14 - 3 * snap.MeldCount)
        {
            stableHand = null;
            waitReason = $"hand incomplete: read={snap.TilesRead}, melds={snap.MeldCount}, expected={14 - 3 * snap.MeldCount}";
            if (!incompleteLogged && Environment.TickCount64 - stateSince > 3000)
            {
                incompleteLogged = true;
                MjLog.Warn($"turn: {waitReason}; melds read [{string.Join(" ", snap.Melds.Select(m => m.ToString()))}], after call={afterCall}, ponned={(ponnedTile >= 0 ? Tiles.Name(ponnedTile) : "-")}.");
            }
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
            if (option < 0)
            {
                // The drawn-win panel can carry no labels in AtkValues. A visible, enabled button whose
                // own text reads "ツモ"/"Tsumo" is still label-verified: press it like a mouse click, once.
                if (!tsumoButtonPressed && EmjActuator.ClickLabelledButton(EmjStrings.TsumoButtonTerms, out string pressed))
                {
                    tsumoButtonPressed = true;
                    popupAttempts++;
                    MjLog.Info($"turn: tsumo via the '{pressed}' button (no labelled option).");
                    Stats.Record(AutoActionKind.Tsumo, Loc.T("act.tsumo"));
                    actAtTick = Environment.TickCount64 + PopupRetryDelayMs;
                    return;
                }

                if (tsumoButtonPressed)
                {
                    waitReason = "tsumo button pressed; waiting for the result";
                    return;
                }

                // No win is offered (a complete shape without a yaku cannot win, e.g. an open hand
                // whose yaku is gone). Give the panel a moment, then play the turn as usual instead
                // of stalling.
                if (Environment.TickCount64 - stateSince < NoWinGraceMs)
                {
                    waitReason = "winning shape: waiting for a Tsumo option";
                    return;
                }

                if (!noWinLogged)
                {
                    noWinLogged = true;
                    MjLog.Warn($"turn: complete shape {snap.Notation} but no Tsumo offered (options [{string.Join(" | ", actions.Select(a => $"#{a.Option} '{a.Label}'"))}]); playing the turn normally.");
                    EmjClicker.DumpButtons();
                }
                goto NormalTurn;
            }

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

        NormalTurn:
        if (TryResolveNineTerminals(snap))
            return;

        BoardSnapshot? board = EmjBoardReader.TryReadBoard();

        RiichiDiscardRoute route = RiichiDiscardRouting.Select(riichiFired, riichiTile, board?.OwnRiichi == true);
        if (route == RiichiDiscardRoute.InvalidDeclaration)
        {
            StopUnsafe("riichi declaration discard target is missing");
            return;
        }
        // The badge can light up immediately after [11, riichi], before the declaration tile is cut.
        // Finish that discard first; only subsequent turns may use automatic tsumogiri.
        if (route == RiichiDiscardRoute.DeclarationTile)
        {
            waitReason = $"riichi declaration discard pending: {Tiles.Name(riichiTile)}";
            if (EmjActuator.DiscardTile(riichiTile))
            {
                acted = true;
                Stats.Record(AutoActionKind.Discard, Loc.F("act.discard", Loc.Tile(riichiTile)));
            }
            return;
        }
        if (route == RiichiDiscardRoute.DrawnTile)
        {
            if (EmjActuator.DiscardSlot(13))
            {
                acted = true;
                Stats.Record(AutoActionKind.Discard, Loc.T("act.tsumogiri"));
            }
            return;
        }

        int riichiOption = afterCall ? -1 : FindOption(EmjBoardReader.ReadActionOptions(), EmjAction.Riichi);

        TurnInput input = TableContext.BuildTurnInput(snap, board, riichiAvailable: riichiOption >= 0 && !riichiFired)
            with { ForbiddenDiscards = frozenForbidden ??= ForbiddenDiscards(snap, afterCall) };
        if (!worker.TryGet(DecisionWorker.KeyFor(input), input, out TurnDecision decision, out _))
        {
            waitReason = "decision worker pending";
            return;
        }

        if (decision.Discard < 0)
        {
            YieldToGame(30, $"decision unavailable: {decision.Reason}");
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

    /// <summary>Last complete 13-tile hand read outside our turn (cleared when our turn or a new hand starts).</summary>
    private HandSnapshot? handBeforePopup;

    /// <summary>When the current call popup first showed options in this state (0 = none on screen).</summary>
    private long offerSince;

    /// <summary>The option set we answered, and the last follow-up popup already logged.</summary>
    private string? answeredOfferKey;
    private string? loggedFollowUpKey;

    private void DoCall()
    {
        if (popupAttempts >= MaxPopupAttempts) return;

        var options = EmjBoardReader.ReadActionOptions();
        if (options.Count == 0)
        {
            // No popup: state 15 is just waiting for the other players. Nothing to do or report.
            waitReason = "no call offered (other players' turn)";
            stableHand = null;
            return;
        }

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
        {
            // Nothing recognizable to click (no Ron/Tsumo/Pass label): wait, never guess. Record what
            // the popup shows once per offer, so a label missing from EmjStrings can be added.
            waitReason = decision.Reason;
            if (unreadableOfferKey != optionKey)
            {
                unreadableOfferKey = optionKey;
                MjLog.Warn($"call: no recognizable button ({decision.Reason}); labels [{string.Join(" | ", options.Select(o => $"#{o.Option} '{o.Label}' ({Escape(o.Label)})"))}].");
                EmjAddonDumper.DumpValues();
            }
            return;
        }

        if (decision.Reason == "pass")
            decision = MaybeUpgradeToPon(options, decision);

        if (EmjActuator.Call(decision.Option))
        {
            answeredOfferKey = optionKey;
            if (decision.Reason.StartsWith("pon", StringComparison.Ordinal))
                ponnedTile = lastDiscardTile;
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
        if (pon < 0)
            return pass;
        if (lastDiscardTile < 0)
        {
            MjLog.Info("pon: offered, but the discarded tile is not known yet; passing.");
            return pass;
        }

        if (!EmjBoardReader.TryReadHand(out HandSnapshot snap) || snap.TilesRead != 13 - 3 * snap.MeldCount)
        {
            if (handBeforePopup is not { } before)
            {
                MjLog.Info($"pon: offered on {Tiles.Name(lastDiscardTile)}, but the hand did not read fully; passing.");
                return pass;
            }
            snap = before;
            MjLog.Info($"pon: hand read incomplete under the popup; using the hand read before it ({snap.Notation}).");
        }

        if (snap.Counts[lastDiscardTile] < 2)
        {
            // We hold fewer than two of the tracked discard: the tracked tile is probably not the one offered.
            MjLog.Warn($"pon: offered, but we hold {snap.Counts[lastDiscardTile]} of the tracked discard {Tiles.Name(lastDiscardTile)}; passing.");
            return pass;
        }

        BoardSnapshot? board = EmjBoardReader.TryReadBoard();

        // One decision path for both AIs; the options pick the rule (Default includes the yakuhai pon
        // that only keeps the shanten; Strategic also prices the call). Priced pons cost at most tens of
        // milliseconds, once per offer.
        TurnInput view = TableContext.BuildTurnInput(snap, board, riichiAvailable: false);
        bool take = CallPolicy.ShouldPon(new CallInput
        {
            Counts13 = snap.Counts,
            Melds = snap.Melds,
            Tile = lastDiscardTile,
            Context = view.Context,
            OpponentRiichi = board?.AnyOpponentRiichi ?? false,
            Seen = view.Seen,
            Opponents = view.Opponents,
            Game = view.Game,
        }, Plugin.Cfg.StrategicAi ? EvalOptions.Strategic : EvalOptions.Default);

        MjLog.Info($"pon: {(take ? "taking" : "passing")} {Tiles.Name(lastDiscardTile)} (hand {snap.Notation}, shanten {ShantenCalculator.Calculate(snap.Counts, snap.MeldCount)}).");
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
