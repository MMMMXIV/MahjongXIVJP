using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MahjongXiv.Core.Game;

/// <summary>Dispatches only an addon-owned ButtonClick on a visible, enabled tile button.</summary>
internal static unsafe class EmjClicker
{
    // Turning this off disables discards; it no longer enables raw callbacks.
    public static bool Enabled { get; set; } = true;

    public static string? LastFailure { get; private set; }
    private static bool Reject(string reason)
    {
        LastFailure = reason;
        MjLog.Warn($"click: {reason}");
        return false;
    }

    private static bool resultButtonsLogged;

    /// <summary>Dump the result screen's controls once per session, to find the hand-change button.</summary>
    public static void DumpButtonsOnce()
    {
        if (resultButtonsLogged)
            return;
        resultButtonsLogged = true;
        DumpButtons();
    }

    /// <summary>Click hand slot 0-13 (13 = drawn tile). False when the slot or its click event can't be found.</summary>
    public static bool TryClickSlot(int slot)
    {
        LastFailure = null;
        if (!Enabled) return Reject("click-discard disabled for this session");
        if (slot is < 0 or > 13) return Reject($"slot {slot} out of range");
        if (!EmjBoardReader.TryGetAddon(out AtkUnitBase* addon) || !addon->IsReady
            || addon->AtkValues == null || addon->AtkValuesCount == 0)
            return Reject("table not ready");
        if (addon->AtkValues[0].Int != 30) return Reject($"state changed to {addon->AtkValues[0].Int}");

        AtkResNode* button = SlotButton(addon, slot, out string reason);
        if (button == null) return Reject($"slot {slot}: {reason}");

        AtkEvent* evt = PickClickEvent(addon, button);
        if (evt == null)
        {
            return Reject($"slot {slot} node {button->NodeId}: no addon-owned ButtonClick event");
        }

        // ReceiveEvent can rebuild the event list: capture scalars and never read evt afterwards.
        var type = evt->State.EventType;
        int param = (int)evt->Param;
        var data = new AtkEventData();
        MjLog.Info($"click: begin slot={slot} type={type} param={param} addon={(nint)addon:X}");
        addon->ReceiveEvent(type, param, evt, &data);
        MjLog.Info($"click: returned slot={slot} type={type} param={param}");
        return true;
    }

    /// <summary>Log every event registered on each hand slot's component node and button node.</summary>
    public static void DumpEvents()
    {
        if (!EmjBoardReader.TryGetAddon(out AtkUnitBase* addon))
        {
            MjLog.Info("events: no table addon.");
            return;
        }

        for (int slot = 0; slot <= 13; slot++)
        {
            AtkResNode* node = SlotNode(addon, slot);
            AtkResNode* button = SlotButton(addon, slot, out _);
            MjLog.Info($"events: slot {slot} node {Describe(addon, node)} | button {Describe(addon, button)}");
        }
    }

    /// <summary>
    /// Log every visible node in the table addon that carries an event registered by the addon — the
    /// clickable controls (result-screen buttons, call buttons). Used to find what to click instead of
    /// firing the raw advance/call callbacks.
    /// </summary>
    public static void DumpButtons()
    {
        if (!EmjBoardReader.TryGetAddon(out AtkUnitBase* addon))
        {
            MjLog.Info("buttons: no table addon.");
            return;
        }

        int state = addon->AtkValues != null ? addon->AtkValues[0].Int : -1;
        MjLog.Info($"buttons: addon state {state}, {addon->UldManager.NodeListCount} top-level nodes.");
        int found = 0;
        WalkUld(addon, &addon->UldManager, "", ref found, depth: 0);
        MjLog.Info($"buttons: {found} visible node(s) with addon events.");
    }

    private static void WalkUld(AtkUnitBase* addon, AtkUldManager* uld, string path, ref int found, int depth)
    {
        if (uld == null || depth > 6)
            return;

        for (int i = 0; i < uld->NodeListCount; i++)
        {
            AtkResNode* node = uld->NodeList[i];
            if (node == null || !node->IsVisible())
                continue;

            string here = $"{path}/{node->NodeId}";
            bool hasAddonEvent = false;
            for (AtkEvent* evt = node->AtkEventManager.Event; evt != null; evt = evt->NextEvent)
                if (evt->Listener == (AtkEventListener*)addon)
                    hasAddonEvent = true;

            if (hasAddonEvent && found < 80)
            {
                found++;
                MjLog.Info($"buttons: {here} {Describe(addon, node)}");
            }

            if ((int)node->Type >= 1000)
            {
                AtkComponentBase* component = ((AtkComponentNode*)node)->Component;
                if (component != null)
                    WalkUld(addon, &component->UldManager, here, ref found, depth + 1);
            }
        }
    }

    private static string Describe(AtkUnitBase* addon, AtkResNode* node)
    {
        if (node == null)
            return "null";

        var parts = new List<string>();
        for (AtkEvent* evt = node->AtkEventManager.Event; evt != null && parts.Count < 16; evt = evt->NextEvent)
        {
            string owner = evt->Listener == (AtkEventListener*)addon ? "addon" : $"0x{(nint)evt->Listener:X}";
            parts.Add($"{evt->State.EventType}/param {evt->Param}/{owner}");
        }

        return $"id {node->NodeId} [{(parts.Count == 0 ? "no events" : string.Join(", ", parts))}]";
    }

    private static AtkEvent* PickClickEvent(AtkUnitBase* addon, AtkResNode* node)
    {
        int visited = 0;
        for (AtkEvent* evt = node->AtkEventManager.Event; evt != null && visited++ < 64; evt = evt->NextEvent)
            if (evt->Listener == (AtkEventListener*)addon && evt->State.EventType == AtkEventType.ButtonClick)
                return evt;
        return null;
    }

    private static AtkResNode* SlotNode(AtkUnitBase* addon, int slot)
    {
        if (slot is < 0 or > 13)
            return null;
        uint id = slot < MjConstants.HandTileNodeIds.Length
            ? MjConstants.HandTileNodeIds[slot]
            : MjConstants.DrawnTileNodeId;
        return addon->GetNodeById(id);
    }

    /// <summary>
    /// Which hand slots (0-13) currently have a visible, enabled tile button; null when no slot is
    /// enabled at all (between states / animating), which says nothing about individual tiles.
    /// </summary>
    public static bool[]? ReadEnabledSlots()
    {
        if (!EmjBoardReader.TryGetAddon(out AtkUnitBase* addon))
            return null;
        var enabled = new bool[14];
        bool any = false;
        for (int slot = 0; slot <= 13; slot++)
        {
            enabled[slot] = SlotButton(addon, slot, out _) != null;
            any |= enabled[slot];
        }
        return any ? enabled : null;
    }

    /// <summary>Hand tile component node -> its button node (Uld NodeList[HandButtonNodeListIndex]).</summary>
    private static AtkResNode* SlotButton(AtkUnitBase* addon, int slot, out string reason)
    {
        var nodes = new NativeTileNodes();
        return (AtkResNode*)TileButtonResolver.Find((nint)SlotNode(addon, slot),
            MjConstants.HandButtonNodeListIndex, ref nodes, out reason);
    }

    private struct NativeTileNodes : ITileNodeAccess
    {
        public bool IsVisible(nint address) => ((AtkResNode*)address)->IsVisible();

        public nint ChildAt(nint address, int index)
        {
            var node = (AtkResNode*)address;
            if ((int)node->Type < 1000) return 0;
            AtkComponentBase* container = ((AtkComponentNode*)node)->Component;
            if (container == null) return 0;
            AtkUldManager* uld = &container->UldManager;
            if (uld->NodeList == null || index < 0 || index >= uld->NodeListCount) return 0;
            return (nint)uld->NodeList[index];
        }

        public bool IsEnabledButton(nint address, out string reason)
        {
            var node = (AtkResNode*)address;
            reason = $"child node {node->NodeId} is not a component (type={(int)node->Type})";
            if ((int)node->Type < 1000) return false;
            AtkComponentBase* button = ((AtkComponentNode*)node)->Component;
            reason = $"child node {node->NodeId} has no component or owner";
            if (button == null || button->OwnerNode == null) return false;
            ComponentType type = button->GetComponentType();
            reason = $"child node {node->NodeId} is {type}, not Button";
            if (type != ComponentType.Button) return false;
            reason = $"child button {node->NodeId} is disabled";
            return ((AtkComponentButton*)button)->IsEnabled;
        }
    }
}
