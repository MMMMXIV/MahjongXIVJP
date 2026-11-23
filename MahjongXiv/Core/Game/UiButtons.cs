using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MahjongXiv.Core.Game;

/// <summary>A visible, enabled button inside an addon, with its label and the click event the addon registered on it.</summary>
internal readonly unsafe struct UiButton
{
    private readonly nint eventAddress;

    public UiButton(uint nodeId, string text, AtkEvent* evt)
    {
        NodeId = nodeId;
        Text = text;
        eventAddress = (nint)evt;
    }

    public uint NodeId { get; }
    public string Text { get; }
    public AtkEvent* Event => (AtkEvent*)eventAddress;
}

/// <summary>
/// Finds and presses buttons in any addon the way a mouse click would: by replaying the click event
/// the addon registered on the button, so the addon runs its own handler (callbacks, timers, sounds)
/// instead of receiving a raw callback. Used for the result-screen "Next" button and the Duty Finder.
/// </summary>
internal static unsafe class UiButtons
{
    private const int MaxDepth = 8;

    /// <summary>The named addon if it is loaded, visible and ready for input; otherwise null.</summary>
    public static AtkUnitBase* GetReadyAddon(string name)
    {
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(name, 1).Address;
        if (addon == null || !addon->IsVisible || !addon->IsReady)
            return null;
        return addon;
    }

    /// <summary>
    /// Every visible addon whose name starts with <paramref name="prefix"/> (e.g. the table "Emj"/"EmjL"
    /// and any separate result window the mahjong UI opens), with its name.
    /// </summary>
    public static List<(string Name, nint Addon)> VisibleAddons(string prefix)
    {
        var found = new List<(string, nint)>();
        var manager = FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkUnitManager.Instance();
        if (manager == null)
            return found;

        ref AtkUnitList list = ref manager->AtkUnitManager.AllLoadedUnitsList;
        for (int i = 0; i < list.Count && i < list.Entries.Length; i++)
        {
            AtkUnitBase* unit = list.Entries[i].Value;
            if (unit == null || !unit->IsVisible || !unit->IsReady)
                continue;
            string name = unit->NameString;
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                found.Add((name, (nint)unit));
        }

        return found;
    }

    /// <summary>Every visible, enabled button in the addon that has a click event the addon itself listens to.</summary>
    public static List<UiButton> Find(AtkUnitBase* addon)
    {
        var found = new List<UiButton>();
        if (addon != null)
            Walk(addon, &addon->UldManager, found, 0);
        return found;
    }

    /// <summary>The first button whose label contains one of <paramref name="terms"/> (case-insensitive), or null.</summary>
    public static UiButton? FindByText(AtkUnitBase* addon, IReadOnlyList<string> terms)
    {
        foreach (UiButton button in Find(addon))
            foreach (string term in terms)
                if (button.Text.Contains(term, StringComparison.OrdinalIgnoreCase))
                    return button;
        return null;
    }

    /// <summary>Simulate a click on <paramref name="button"/>.</summary>
    public static void Click(AtkUnitBase* addon, in UiButton button)
    {
        var data = new AtkEventData();
        addon->ReceiveEvent(button.Event->State.EventType, (int)button.Event->Param, button.Event, &data);
    }

    /// <summary>Fire a raw addon callback made of int values (fallback when no button can be found).</summary>
    public static void FireCallback(AtkUnitBase* addon, params int[] args)
    {
        AtkValue* values = stackalloc AtkValue[args.Length];
        for (int i = 0; i < args.Length; i++)
            values[i].SetInt(args[i]);
        addon->FireCallback((uint)args.Length, values, true);
    }

    public static string Describe(IEnumerable<UiButton> buttons)
        => string.Join(", ", buttons.Select(b => $"#{b.NodeId} '{b.Text}'"));

    private static void Walk(AtkUnitBase* addon, AtkUldManager* uld, List<UiButton> found, int depth)
    {
        if (uld == null || depth > MaxDepth)
            return;

        for (int i = 0; i < uld->NodeListCount; i++)
        {
            AtkResNode* node = uld->NodeList[i];
            if (node == null || !node->IsVisible() || (int)node->Type < 1000)
                continue;

            AtkComponentBase* component = ((AtkComponentNode*)node)->Component;
            if (component == null)
                continue;

            if (component->GetComponentType() == ComponentType.Button)
            {
                var button = (AtkComponentButton*)component;
                AtkEvent* evt = ClickEvent(addon, node);
                if (button->IsEnabled && evt != null)
                {
                    string text = button->ButtonTextNode != null ? button->ButtonTextNode->NodeText.ToString() : string.Empty;
                    found.Add(new UiButton(node->NodeId, text.Trim(), evt));
                }
            }

            Walk(addon, &component->UldManager, found, depth + 1);
        }
    }

    /// <summary>The addon's ButtonClick event on the node, else any event the addon registered there.</summary>
    private static AtkEvent* ClickEvent(AtkUnitBase* addon, AtkResNode* node)
    {
        AtkEvent* any = null;
        for (AtkEvent* evt = node->AtkEventManager.Event; evt != null; evt = evt->NextEvent)
        {
            if (evt->Listener != (AtkEventListener*)addon)
                continue;
            if (evt->State.EventType == AtkEventType.ButtonClick)
                return evt;
            if (any == null)
                any = evt;
        }

        return any;
    }
}
