using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MahjongXiv.Core.Game;

/// <summary>Fail-closed, observed-state discard handshake. Never commits at state 6/28.</summary>
internal static unsafe class EmjActuator
{
    private const int OpHighlight = 15;
    private const int OpCallOption = 11;
    private static readonly DiscardHandshake handshake = new();
    public static string? Failure => handshake.Failure;
    private static int pendingSlot = -1;
    private static int pendingIcon;
    public static bool HasPendingDiscard => pendingSlot >= 0;
    public static bool ContinueDiscard() => HasPendingDiscard && Commit(pendingSlot, pendingIcon);
    public static void Reset()
    {
        handshake.Reset();
        pendingSlot = -1;
    }

    /// <summary>Discard the given hand slot (0-12 hand position, 13 = drawn tile).</summary>
    public static bool DiscardSlot(int slot)
    {
        if (slot is < 0 or > 13)
        {
            MjLog.Warn($"discard: slot {slot} out of range.");
            return false;
        }

        if (!EmjBoardReader.TryReadHand(out HandSnapshot snap) || snap.SlotTiles[slot] < 0)
        {
            MjLog.Warn($"discard: slot {slot} is empty or hand unreadable.");
            return false;
        }

        return Commit(slot, snap.SlotIcons[slot]);
    }

    /// <summary>
    /// Find the given tile in the current hand and discard it, preferring a plain copy over a red
    /// five so the aka's extra han stays in the hand whenever possible.
    /// </summary>
    public static bool DiscardTile(int tile)
    {
        if (!EmjBoardReader.TryReadHand(out HandSnapshot snap))
        {
            MjLog.Warn("discard: could not read hand.");
            return false;
        }

        int akaSlot = -1;
        for (int slot = 0; slot < snap.SlotTiles.Length; slot++)
        {
            if (snap.SlotTiles[slot] != tile)
                continue;
            if (!EmjTiles.IsAkaIcon(snap.SlotIcons[slot]))
                return Commit(slot, snap.SlotIcons[slot]);
            akaSlot = slot;
        }

        if (akaSlot >= 0)
            return Commit(akaSlot, snap.SlotIcons[akaSlot]);

        MjLog.Warn($"discard: tile {Engine.Tiles.Name(tile)} not in hand.");
        return false;
    }

    private static bool Commit(int slot, int iconId)
    {
        if (Failure != null) return false;
        if (!EmjBoardReader.TryGetAddon(out AtkUnitBase* addon) || !addon->IsReady
            || addon->AtkValues == null || addon->AtkValuesCount == 0)
            return false;
        int state = addon->AtkValues[0].Int;
        if (!EmjBoardReader.TryReadHand(out HandSnapshot hand)
            || slot < 0 || slot >= hand.SlotIcons.Length || hand.SlotIcons[slot] != iconId
            || !EmjTiles.TryFromIconId(iconId, out _, out _))
        {
            handshake.Fail("discard target is no longer readable");
            return false;
        }
        string key = $"{(nint)addon:X}:{slot}:{string.Join(",", hand.SlotIcons)}:{hand.MeldCount}";
        DiscardStep step = handshake.Next(state, key, Environment.TickCount64);
        switch (step)
        {
            case DiscardStep.Highlight:
                pendingSlot = slot;
                pendingIcon = iconId;
                MjLog.Info($"discard: begin highlight slot={slot} icon={iconId} state={state} addon={(nint)addon:X}");
                FireInts(addon, OpHighlight, iconId);
                return false;
            case DiscardStep.Click:
                if (!EmjClicker.TryClickSlot(slot))
                {
                    handshake.Fail($"tile click unavailable: {EmjClicker.LastFailure}; raw callback fallback disabled");
                    return false;
                }
                MjLog.Info($"discard: input sent slot={slot} icon={iconId}; awaiting state acknowledgement");
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Pick a call / self-action option ([11, option]) in a call (15) or self-action (6/30) popup.
    /// State 30 carries the Riichi/Pass prompt (captured live: manual riichi fired [11, 0] at state 30).
    /// </summary>
    public static bool Call(int option)
    {
        if (Failure != null || !EmjBoardReader.TryGetAddon(out AtkUnitBase* addon) || !addon->IsReady)
        {
            MjLog.Warn("call: Emj addon not present.");
            return false;
        }

        int state = addon->AtkValues != null && addon->AtkValuesCount > 0 ? addon->AtkValues[0].Int : -1;
        if (state is not (6 or 15 or 30))
        {
            MjLog.Warn($"call: not a call/self-action state (state={state}).");
            return false;
        }

        if (option < 0 || !EmjBoardReader.ReadActionOptions().Any(o => o.Option == option))
            return false;
        MjLog.Info($"call: begin [11, {option}] state={state} addon={(nint)addon:X}");
        FireInts(addon, OpCallOption, option);
        MjLog.Info($"call: fired [11, {option}] (state {state}).");
        return true;
    }

    /// <summary>Legacy diagnostic command: raw result/acknowledgement injection is disabled.</summary>
    public static bool Advance()
    {
        MjLog.Warn("advance: disabled; use the game's Next button or wait for its timer.");
        return false;
    }

    private static void FireInts(AtkUnitBase* addon, int op, int arg)
    {
        AtkValue* values = stackalloc AtkValue[2];
        values[0] = default;
        values[1] = default;
        values[0].SetInt(op);
        values[1].SetInt(arg);
        addon->FireCallback(2, values, true);
    }

}
