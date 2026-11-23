using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game;
using Dalamud.Interface.Windowing;
using MahjongXiv.Core;
using MahjongXiv.Engine;
using MahjongXiv.Localization;

namespace MahjongXiv.Windows;

/// <summary>
/// The single compact window: table status, mode, start/stop, what the solver suggests or the bot is
/// doing, the session tally, and the settings in a collapsible section.
/// </summary>
public sealed class MainWindow : Window, IDisposable
{
    private const int AlternativeCount = 3;

    private readonly Plugin plugin;

    public MainWindow(Plugin plugin) : base("MahjongXIV###MahjongXivMain")
    {
        this.plugin = plugin;
        Size = new Vector2(340, 300);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(300, 180),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        AutoPlayController auto = plugin.AutoPlay;
        Configuration cfg = Plugin.Cfg;
        LiveBoard board = plugin.Board;

        board.Refresh();

        DrawStatus(board, auto);
        DrawControls(auto);
        ImGui.Separator();

        if (auto.BlockedReason is { } blocked)
            ImGui.TextColored(Styling.AccentAmber, blocked);
        DrawWarnings(board);

        if (auto.Running && auto.Mode == PlayMode.Auto)
            DrawAutoActivity(board, auto);
        else
            DrawSuggestion(board);

        ImGui.Spacing();
        if (ImGui.CollapsingHeader($"{Loc.T("settings.header")}###mjSettings"))
            DrawSettings(cfg);

        ImGui.Spacing();
        ImGui.TextColored(Styling.TextMuted, Loc.T("footer"));
    }

    // ---- Status and controls ---------------------------------------------

    private static void DrawStatus(LiveBoard board, AutoPlayController auto)
    {
        (string key, Vector4 color) = board.Phase switch
        {
            BoardPhase.OurTurn => ("status.ourTurn", Styling.AccentSoft),
            BoardPhase.CallOffered => ("status.callOffered", Styling.AccentAmberSoft),
            BoardPhase.BetweenHands => ("status.betweenHands", Styling.AccentBlueSoft),
            BoardPhase.Watching => ("status.watching", Styling.TextDim),
            _ => ("status.noMatch", Styling.TextMuted),
        };

        ImGui.TextColored(color, $"● {Loc.T(key)}");
        ImGui.SameLine();
        string engaged = !auto.Running ? "engaged.stopped" : auto.Mode == PlayMode.Auto ? "engaged.auto" : "engaged.suggest";
        ImGui.TextColored(auto.Running ? Styling.AccentMint : Styling.TextMuted, $"· {Loc.T(engaged)}");
    }

    private static void DrawControls(AutoPlayController auto)
    {
        if (ImGui.RadioButton($"{Loc.T("mode.suggest")}###mjModeSuggest", auto.Mode == PlayMode.Suggest))
            auto.SetMode(PlayMode.Suggest);
        ImGui.SameLine();
        if (ImGui.RadioButton($"{Loc.T("mode.auto")}###mjModeAuto", auto.Mode == PlayMode.Auto))
            auto.SetMode(PlayMode.Auto);

        ImGui.SameLine();
        string label = Loc.T(auto.Running ? "button.stop" : "button.start");
        float width = Math.Max(80f * ImGui.GetIO().FontGlobalScale, ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 4);
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - width));
        if (ImGui.Button($"{label}###mjStartStop", new Vector2(width, 0)))
            auto.SetRunning(!auto.Running);
    }

    private static void DrawWarnings(LiveBoard board)
    {
        if (board.TilesUnreadable)
            ImGui.TextColored(Styling.AccentRose, Loc.T("warn.tilesUnreadable"));

        ClientLanguage lang = Plugin.ClientState.ClientLanguage;
        if (lang != ClientLanguage.English && lang != ClientLanguage.Japanese)
            ImGui.TextColored(Styling.AccentAmber, Loc.T("warn.clientLanguage"));
    }

    // ---- Suggest mode -----------------------------------------------------

    private static void DrawSuggestion(LiveBoard board)
    {
        if (!board.Present)
        {
            ImGui.TextColored(Styling.TextDim, Loc.T("suggest.openTable"));
            return;
        }

        if (!board.HandRead)
        {
            ImGui.TextColored(Styling.TextDim, Loc.T("suggest.reading"));
            return;
        }

        HandReport report = board.Report;
        ImGui.TextColored(Styling.TextSecondary, Loc.F("suggest.hand", board.Snapshot.Notation, Loc.Shanten(report.Shanten)));
        DrawThreats(board);

        if (board.Phase == BoardPhase.CallOffered)
        {
            ImGui.TextColored(board.RonOffered ? Styling.AccentAmber : Styling.TextDim,
                Loc.T(board.RonOffered ? "suggest.ronOffered" : "suggest.callOffered"));
        }

        if (board.HandComplete)
        {
            ImGui.TextColored(Styling.AccentAmber, Loc.T("suggest.complete"));
            return;
        }

        if (board.InRiichi && board.BestDiscard >= 0)
        {
            DrawDiscardLine(board.BestDiscard, null);
            ImGui.TextColored(Styling.TextDim, Loc.T("suggest.inRiichi"));
            return;
        }

        if (board.Decision is { } decision && decision.Discard >= 0)
        {
            DrawPolicySuggestion(board, decision);
            return;
        }

        if (report.Shape == HandShape.Discard && report.Discards.Count > 0)
        {
            DiscardOption best = report.Discards[0];
            DrawDiscardLine(best.Tile, best);
            for (int i = 1; i <= AlternativeCount && i < report.Discards.Count; i++)
            {
                DiscardOption alt = report.Discards[i];
                ImGui.TextColored(Styling.TextMuted, Loc.F("suggest.alt", Loc.Tile(alt.Tile), Loc.Shanten(alt.Shanten), alt.UkeireCount));
            }
        }
        else if (report.Shape == HandShape.Wait && report.Wait.Count > 0)
        {
            string waits = Loc.Tiles(report.Wait.Tiles.Select(t => t.Tile));
            ImGui.TextColored(Styling.TextSecondary, Loc.F("suggest.waiting", waits, report.Wait.Count));
        }
    }

    /// <summary>The turn policy's pick (same as auto-play), why it was picked, and the runner-up discards.</summary>
    private static void DrawPolicySuggestion(LiveBoard board, TurnDecision decision)
    {
        DiscardOption? chosen = null;
        foreach (ScoredDiscard scored in board.RankedDiscards)
        {
            if (scored.Option.Tile == decision.Discard)
            {
                chosen = scored.Option;
                break;
            }
        }

        DrawDiscardLine(decision.Discard, chosen);

        if (chosen is { Accepts.Count: > 0 } c)
            ImGui.TextColored(Styling.TextDim, Loc.F("suggest.accepts", Loc.Tiles(c.Accepts.Select(a => a.Tile))));

        if (decision.DeclareRiichi)
            ImGui.TextColored(Styling.AccentAmber, Loc.T("suggest.riichi"));
        if (decision.Kind == TurnReason.FoldVsRiichi)
            ImGui.TextColored(Styling.AccentRose, Loc.T("suggest.fold"));
        else if (decision.Kind == TurnReason.FuritenDodge)
            ImGui.TextColored(Styling.AccentBlueSoft, Loc.T("suggest.furiten"));

        int shown = 0;
        foreach (ScoredDiscard scored in board.RankedDiscards)
        {
            if (shown >= AlternativeCount)
                break;
            if (scored.Option.Tile == decision.Discard)
                continue;
            DiscardOption alt = scored.Option;
            ImGui.TextColored(Styling.TextMuted, Loc.F("suggest.alt", Loc.Tile(alt.Tile), Loc.Shanten(alt.Shanten), alt.UkeireCount));
            shown++;
        }
    }

    private static void DrawDiscardLine(int tile, DiscardOption? option)
    {
        ImGui.Text(Loc.T("suggest.discard"));
        ImGui.SameLine();
        ImGui.TextColored(Styling.AccentSoft, Loc.Tile(tile));
        if (option is { } o)
        {
            ImGui.SameLine();
            ImGui.TextColored(Styling.TextDim, Loc.F("suggest.discardDetail", Loc.Shanten(o.Shanten), o.UkeireCount));
        }
    }

    /// <summary>Names of the opponents currently in riichi, so the player sees why the advice turns defensive.</summary>
    private static void DrawThreats(LiveBoard board)
    {
        if (board.Board is not { } table || !table.AnyOpponentRiichi)
            return;

        var names = new List<string>();
        for (int i = 0; i < table.Panels.Count; i++)
        {
            if (i == MjConstants.SelfIndex || !table.Panels[i].Riichi)
                continue;
            string name = table.Panels[i].Name;
            names.Add(name.Length > 0 ? name : $"#{i}");
        }

        ImGui.TextColored(Styling.AccentRose, Loc.F("suggest.threat", string.Join(", ", names)));
    }

    // ---- Auto mode --------------------------------------------------------

    private static void DrawAutoActivity(LiveBoard board, AutoPlayController auto)
    {
        string now = board.Phase switch
        {
            BoardPhase.OurTurn when board.HandComplete => "auto.declaring",
            BoardPhase.OurTurn => "auto.playing",
            BoardPhase.CallOffered => board.RonOffered ? "auto.ron" : "auto.call",
            BoardPhase.BetweenHands => "auto.waitingHand",
            BoardPhase.Watching => "auto.watching",
            _ => "auto.waitingMatch",
        };
        ImGui.TextColored(Styling.AccentSoft, Loc.T(now));

        SessionStats stats = auto.Stats;
        TimeSpan t = stats.Elapsed;
        string elapsed = t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        ImGui.TextColored(Styling.TextSecondary, Loc.F("auto.stats", stats.Wins, stats.Discards, stats.Riichis, elapsed));

        if (stats.Recent.Count > 0)
            ImGui.TextColored(Styling.TextMuted, Loc.F("auto.last", stats.Recent[0].Label));
    }

    // ---- Settings ---------------------------------------------------------

    private static void DrawSettings(Configuration cfg)
    {
        EnumCombo("settings.language", "lang", cfg.Language, v => cfg.Language = v);
        if (Plugin.ClientState.ClientLanguage == ClientLanguage.Japanese)
            ImGui.TextColored(Styling.TextMuted, Loc.T("warn.japaneseBeta"));

        bool ranked = cfg.AllowRankedAutoPlay;
        if (ImGui.Checkbox($"{Loc.T("settings.ranked")}###mjRanked", ref ranked))
        {
            cfg.AllowRankedAutoPlay = ranked;
            cfg.Save();
        }

        bool highlight = cfg.HighlightSuggestedTile;
        if (ImGui.Checkbox($"{Loc.T("settings.highlight")}###mjHighlight", ref highlight))
        {
            cfg.HighlightSuggestedTile = highlight;
            cfg.Save();
        }

        if (cfg.HighlightSuggestedTile)
        {
            EnumCombo("settings.style", "style", cfg.HighlightStyle, v => cfg.HighlightStyle = v);
            EnumCombo("settings.colour", "color", cfg.HighlightColor, v => cfg.HighlightColor = v);
        }

        ImGui.TextColored(Styling.TextDim, Loc.T("settings.delays"));
        DelayRange("discard", () => cfg.TurnDelayMinMs, v => cfg.TurnDelayMinMs = v, () => cfg.TurnDelayMaxMs, v => cfg.TurnDelayMaxMs = v, cfg);
        DelayRange("call", () => cfg.CallDelayMinMs, v => cfg.CallDelayMinMs = v, () => cfg.CallDelayMaxMs, v => cfg.CallDelayMaxMs = v, cfg);
        DelayRange("results", () => cfg.ResultDelayMinMs, v => cfg.ResultDelayMinMs = v, () => cfg.ResultDelayMaxMs, v => cfg.ResultDelayMaxMs = v, cfg);

        if (ImGui.SmallButton($"{Loc.T("settings.resetDelays")}###mjResetDelays"))
        {
            cfg.ResetDelays();
            cfg.Save();
        }
    }

    /// <summary>A combo over an enum whose item labels come from <c>{prefix}.{Value}</c> localization keys.</summary>
    private static void EnumCombo<T>(string labelKey, string prefix, T current, Action<T> set) where T : struct, Enum
    {
        ImGui.SetNextItemWidth(160f * ImGui.GetIO().FontGlobalScale);
        if (!ImGui.BeginCombo($"{Loc.T(labelKey)}###mj_{labelKey}", Loc.T($"{prefix}.{current}")))
            return;

        foreach (T value in Enum.GetValues<T>())
        {
            if (ImGui.Selectable($"{Loc.T($"{prefix}.{value}")}###mj_{prefix}_{value}", value.Equals(current)))
            {
                set(value);
                Plugin.Cfg.Save();
            }
        }

        ImGui.EndCombo();
    }

    private static void DelayRange(string label, Func<int> getMin, Action<int> setMin, Func<int> getMax, Action<int> setMax, Configuration cfg)
    {
        float width = 110f * ImGui.GetIO().FontGlobalScale;

        int min = getMin();
        ImGui.SetNextItemWidth(width);
        if (ImGui.SliderInt($"##{label}_min", ref min, 0, 6000))
        {
            setMin(min);
            if (getMax() < min)
                setMax(min);
            cfg.SaveDebounced();
        }

        ImGui.SameLine();
        int max = getMax();
        ImGui.SetNextItemWidth(width);
        if (ImGui.SliderInt($"{Loc.T($"settings.delay.{label}")}###{label}_max", ref max, 0, 6000))
        {
            setMax(max);
            if (getMin() > max)
                setMin(max);
            cfg.SaveDebounced();
        }
    }
}
