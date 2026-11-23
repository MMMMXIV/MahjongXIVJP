using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MahjongXiv.Core.Game;
using MahjongXiv.Localization;

namespace MahjongXiv.Core;

/// <summary>
/// Re-queues for the same Doman Mahjong table after a match ends, while auto-play is engaged and
/// <see cref="Configuration.AutoRequeue"/> is on. It drives the Duty Finder the way a player would:
/// open it on the table's duty, press "Join", and when the duty is ready press "Commence". Only
/// casual tables are re-queued (ranked ones too when ranked auto-play is allowed), and only a
/// queue the bot itself entered is commenced; a queue the player starts by hand is left alone.
/// </summary>
internal sealed unsafe class AutoQueue : IDisposable
{
    private const long TickIntervalMs = 250;

    /// <summary>How long the Duty Finder may take to open, or the join to register, before trying again.</summary>
    private const int StepTimeoutMs = 8000;

    /// <summary>Give up (and tell the player) after this many failed queue attempts in a row.</summary>
    private const int MaxAttempts = 3;

    private enum Phase
    {
        Idle,
        WaitingToQueue,
        OpeningFinder,
        Joining,
        Queued,
    }

    private readonly AutoPlayController auto;
    private readonly Random rng = new();

    private Phase phase = Phase.Idle;
    private long nextTick;
    private long phaseUntil;
    private long actAt;
    private int attempts;
    private bool commenceArmed;

    /// <summary>The last table we sat at that may be re-queued (ContentFinderCondition row), or 0.</summary>
    private uint lastCfc;

    public AutoQueue(AutoPlayController auto)
    {
        this.auto = auto;
        Plugin.Framework.Update += OnUpdate;
    }

    /// <summary>Player-facing description of what the re-queue is doing, or null when idle.</summary>
    public string? StatusKey => phase switch
    {
        Phase.WaitingToQueue or Phase.OpeningFinder or Phase.Joining => "queue.queuing",
        Phase.Queued => "queue.waiting",
        _ => null,
    };

    private bool Enabled => Plugin.Cfg.AutoRequeue && auto.Running && auto.Mode == PlayMode.Auto;

    private void OnUpdate(IFramework framework)
    {
        long now = Environment.TickCount64;
        if (now < nextTick)
            return;
        nextTick = now + TickIntervalMs;

        if (!Enabled)
        {
            Reset();
            return;
        }

        // At a table: remember it, and stand down until the match is over.
        if (EmjBoardReader.TryGetAddon(out _))
        {
            TableKind kind = EmjTableGuard.Current;
            if (kind == TableKind.Casual || (kind == TableKind.Ranked && Plugin.Cfg.AllowRankedAutoPlay))
                lastCfc = EmjTableGuard.CurrentCfc;
            Reset();
            return;
        }

        if (lastCfc == 0)
            return;

        ContentsFinderQueueState queue = QueueState();

        switch (phase)
        {
            case Phase.Idle:
                if (queue == ContentsFinderQueueState.None && IsFree())
                {
                    phase = Phase.WaitingToQueue;
                    actAt = now + Roll(Plugin.Cfg.RequeueDelayMinMs, Plugin.Cfg.RequeueDelayMaxMs);
                }
                break;

            case Phase.WaitingToQueue:
                if (queue != ContentsFinderQueueState.None)
                {
                    phase = Phase.Idle; // the player queued for something by hand: leave it to them
                    break;
                }

                if (!IsFree())
                {
                    actAt = now + 1000; // still leaving the table / zoning: try again in a moment
                    break;
                }

                if (now >= actAt)
                    OpenFinder(now);
                break;

            case Phase.OpeningFinder:
                if (queue != ContentsFinderQueueState.None)
                {
                    phase = Phase.Queued;
                    break;
                }

                AtkUnitBase* finder = UiButtons.GetReadyAddon("ContentsFinder");
                if (finder != null && now >= actAt)
                {
                    PressJoin(finder);
                    phase = Phase.Joining;
                    phaseUntil = now + StepTimeoutMs;
                }
                else if (now > phaseUntil)
                {
                    Retry("the Duty Finder did not open");
                }
                break;

            case Phase.Joining:
                if (queue != ContentsFinderQueueState.None)
                {
                    MjLog.Info($"requeue: queued for ContentFinderCondition {lastCfc}.");
                    phase = Phase.Queued;
                    attempts = 0;
                }
                else if (now > phaseUntil)
                {
                    Retry("the join did not register");
                }
                break;

            case Phase.Queued:
                HandleQueued(queue, now);
                break;
        }
    }

    private void HandleQueued(ContentsFinderQueueState queue, long now)
    {
        if (queue == ContentsFinderQueueState.None)
        {
            // Withdrawn (by the player, or the pop was declined/expired): queue again.
            commenceArmed = false;
            phase = Phase.Idle;
            return;
        }

        AtkUnitBase* confirm = UiButtons.GetReadyAddon("ContentsFinderConfirm");
        if (confirm == null)
        {
            commenceArmed = false;
            return;
        }

        if (!commenceArmed)
        {
            commenceArmed = true;
            actAt = now + Roll(Plugin.Cfg.CallDelayMinMs + 500, Plugin.Cfg.CallDelayMaxMs + 1500);
            return;
        }

        if (now < actAt)
            return;

        UiButton? commence = UiButtons.FindByText(confirm, EmjStrings.CommenceButtonTerms);
        if (commence is { } button)
        {
            UiButtons.Click(confirm, button);
            MjLog.Info($"requeue: pressed '{button.Text}' on the duty-ready popup.");
        }
        else
        {
            UiButtons.FireCallback(confirm, 8);
            MjLog.Info("requeue: commence button not identified; fired [8] on the duty-ready popup.");
        }

        actAt = now + 3000; // if the popup is still up later, press again
    }

    private void OpenFinder(long now)
    {
        AgentContentsFinder* agent = AgentContentsFinder.Instance();
        if (agent == null)
        {
            Retry("the Duty Finder agent is unavailable");
            return;
        }

        agent->OpenRegularDuty(lastCfc, false);
        MjLog.Info($"requeue: opened the Duty Finder on ContentFinderCondition {lastCfc}.");
        phase = Phase.OpeningFinder;
        phaseUntil = now + StepTimeoutMs;
        actAt = now + 1200; // let the list select the duty before joining
    }

    private static void PressJoin(AtkUnitBase* finder)
    {
        UiButton? join = UiButtons.FindByText(finder, EmjStrings.JoinButtonTerms);
        if (join is { } button)
        {
            UiButtons.Click(finder, button);
            MjLog.Info($"requeue: pressed '{button.Text}' in the Duty Finder.");
            return;
        }

        List<UiButton> buttons = UiButtons.Find(finder);
        MjLog.Info($"requeue: join button not identified among [{UiButtons.Describe(buttons)}]; firing [12, 0].");
        UiButtons.FireCallback(finder, 12, 0);
    }

    private void Retry(string why)
    {
        attempts++;
        MjLog.Warn($"requeue: {why} (attempt {attempts}/{MaxAttempts}).");
        if (attempts >= MaxAttempts)
        {
            Plugin.ChatGui.PrintError($"{MjConstants.LogPrefix} {Loc.T("queue.failed")}");
            lastCfc = 0; // stop until the next table is seen
            attempts = 0;
            phase = Phase.Idle;
            return;
        }

        phase = Phase.WaitingToQueue;
        actAt = Environment.TickCount64 + 3000;
    }

    private void Reset()
    {
        phase = Phase.Idle;
        commenceArmed = false;
        attempts = 0;
    }

    private static ContentsFinderQueueState QueueState()
    {
        ContentsFinder* cf = ContentsFinder.Instance();
        return cf == null ? ContentsFinderQueueState.None : cf->QueueInfo.QueueState;
    }

    /// <summary>The character is out of the duty, not zoning, not busy with another prompt or cutscene.</summary>
    private static bool IsFree()
    {
        if (Plugin.ObjectTable.LocalPlayer == null)
            return false;

        ICondition c = Plugin.Condition;
        return !c[ConditionFlag.BoundByDuty]
               && !c[ConditionFlag.BoundByDuty56]
               && !c[ConditionFlag.BoundByDuty95]
               && !c[ConditionFlag.BetweenAreas]
               && !c[ConditionFlag.BetweenAreas51]
               && !c[ConditionFlag.WatchingCutscene]
               && !c[ConditionFlag.OccupiedInQuestEvent]
               && !c[ConditionFlag.Occupied]
               && !c[ConditionFlag.InCombat]
               && !c[ConditionFlag.LoggingOut];
    }

    private int Roll(int min, int max) => max <= min ? min : rng.Next(min, max);

    public void Dispose() => Plugin.Framework.Update -= OnUpdate;
}
