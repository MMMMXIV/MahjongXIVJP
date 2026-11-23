using Dalamud.Game;

namespace MahjongXiv.Localization;

/// <summary>
/// Player-facing UI strings in English and Japanese. Keys missing from the Japanese table fall back
/// to English. Diagnostic log lines (<c>/xllog</c>) stay in English on purpose: they are meant to be
/// pasted into bug reports.
/// </summary>
internal static class Loc
{
    private static readonly Dictionary<string, string> En = new(StringComparer.Ordinal)
    {
        // Status line
        ["status.ourTurn"] = "Your turn",
        ["status.callOffered"] = "Call offered",
        ["status.betweenHands"] = "Between hands",
        ["status.watching"] = "Watching the table",
        ["status.noMatch"] = "No match open",
        ["engaged.stopped"] = "stopped",
        ["engaged.auto"] = "auto-play on",
        ["engaged.suggest"] = "suggesting",

        // Controls
        ["mode.suggest"] = "Suggest",
        ["mode.auto"] = "Auto-play",
        ["button.start"] = "Start",
        ["button.stop"] = "Stop",

        // Warnings
        ["warn.tilesUnreadable"] = "Tiles not recognized: use the default tile design.",
        ["warn.clientLanguage"] = "This client language is not verified: seat wind and riichi may be misread.",
        ["warn.japaneseBeta"] = "Japanese client support is new. If something is misread, report the output of /mjjp state and /mjjp options.",

        // Suggest mode
        ["suggest.openTable"] = "Open a Doman Mahjong table to see suggestions.",
        ["suggest.reading"] = "Reading your hand…",
        ["suggest.hand"] = "Hand: {0}   ({1})",
        ["suggest.complete"] = "Complete hand: declare the win.",
        ["suggest.discard"] = "Discard",
        ["suggest.discardDetail"] = "→ {0}, {1} tiles accept",
        ["suggest.accepts"] = "Accepts: {0}",
        ["suggest.alt"] = "   or {0}: {1}, {2} tiles",
        ["suggest.waiting"] = "Waiting on {0} ({1} tiles)",
        ["suggest.riichi"] = "Declare riichi with this discard.",
        ["suggest.fold"] = "Opponent in riichi: folding to the safest tile.",
        ["suggest.furiten"] = "Avoiding a furiten wait.",
        ["suggest.inRiichi"] = "In riichi: discard the drawn tile (tsumogiri).",
        ["suggest.ronOffered"] = "Ron is available: take the win.",
        ["suggest.callOffered"] = "A call is offered.",
        ["suggest.threat"] = "Riichi declared by: {0}",

        // Shanten
        ["shanten.complete"] = "complete",
        ["shanten.tenpai"] = "tenpai",
        ["shanten.n"] = "{0}-shanten",

        // Auto mode
        ["auto.declaring"] = "Declaring the win",
        ["auto.playing"] = "Playing the turn",
        ["auto.ron"] = "Taking the ron",
        ["auto.call"] = "Answering the call",
        ["auto.waitingHand"] = "Waiting for the next hand",
        ["auto.watching"] = "Watching the table",
        ["auto.waitingMatch"] = "Waiting for a match",
        ["auto.stats"] = "Wins {0}   Discards {1}   Riichi {2}   {3}",
        ["auto.last"] = "Last: {0}",

        // Action trail
        ["act.advance"] = "Advanced the table",
        ["act.tsumogiri"] = "Tsumogiri (riichi)",
        ["act.tsumo"] = "Declared tsumo",
        ["act.riichi"] = "Declared riichi",
        ["act.discard"] = "Discarded {0}",
        ["act.win"] = "Called the win",
        ["act.pass"] = "Passed a call",
        ["act.pon"] = "Pon {0}",

        // Settings
        ["settings.header"] = "Settings",
        ["settings.language"] = "Language",
        ["settings.ranked"] = "Auto-play on ranked tables",
        ["settings.highlight"] = "Highlight the suggested tile",
        ["settings.style"] = "Style",
        ["settings.colour"] = "Colour",
        ["settings.delays"] = "Auto-play delays (ms, random within range)",
        ["settings.delay.discard"] = "Discard",
        ["settings.delay.call"] = "Call",
        ["settings.delay.results"] = "Results",
        ["settings.resetDelays"] = "Reset delays",
        ["settings.strategicAi"] = "Strategic AI (push/fold, riichi vs dama, placement)",
        ["settings.strategicAiNote"] = "Experimental. Weighs deal-in risk against win chance instead of always folding or always riichi.",
        ["suggest.push"] = "Pushing despite the threat: the win is worth the risk.",
        ["suggest.dama"] = "Stay dama (no riichi) with this discard.",
        ["settings.autoRequeue"] = "Queue again after a match (auto-play)",
        ["settings.autoRequeueNote"] = "Re-joins the same table through the Duty Finder and commences when it is ready.",
        ["settings.delay.requeue"] = "Re-queue",
        ["queue.queuing"] = "Queuing for the next match…",
        ["queue.waiting"] = "In queue for the next match",
        ["queue.failed"] = "Could not queue for the next match automatically. Please queue by hand.",

        ["lang.Auto"] = "Auto (follow Dalamud)",
        ["lang.English"] = "English",
        ["lang.Japanese"] = "日本語 (Japanese)",

        ["style.Brackets"] = "Brackets",
        ["style.Arrow"] = "Arrow",
        ["style.Glow"] = "Glow",

        ["color.Violet"] = "Violet",
        ["color.Cyan"] = "Cyan",
        ["color.Green"] = "Green",
        ["color.Amber"] = "Amber",
        ["color.Rose"] = "Rose",
        ["color.Magenta"] = "Magenta",
        ["color.White"] = "White",

        ["footer"] = "Fork of FFXIV-AutoMahjongSolver by Xeldar Alz · AGPL-3.0",

        // Table guard (also printed to chat)
        ["block.ranked"] = "Ranked table: auto-play is off here (enable it under Settings in /mjjp). Suggestions still work.",
        ["block.unknown"] = "Not a recognized casual table: auto-play is disabled here. Suggestions still work.",

        // Commands
        ["cmd.help"] = "Toggle the window. /mjjp auto | suggest [on|off]. Debug: dump|tree|board|state|melds|options|capture|probe|diag|events|buttons|click-discard.",
        ["cmd.alias"] = "Alias for /mjjp.",
    };

    private static readonly Dictionary<string, string> Ja = new(StringComparer.Ordinal)
    {
        ["status.ourTurn"] = "あなたの手番",
        ["status.callOffered"] = "鳴き/和了の選択中",
        ["status.betweenHands"] = "局の合間",
        ["status.watching"] = "卓を観察中",
        ["status.noMatch"] = "対局していません",
        ["engaged.stopped"] = "停止中",
        ["engaged.auto"] = "自動打牌中",
        ["engaged.suggest"] = "推奨表示中",

        ["mode.suggest"] = "推奨表示",
        ["mode.auto"] = "自動打牌",
        ["button.start"] = "開始",
        ["button.stop"] = "停止",

        ["warn.tilesUnreadable"] = "牌を認識できません。牌のデザインを標準に戻してください。",
        ["warn.clientLanguage"] = "このクライアント言語は未検証です。自風やリーチ状態を誤認識する場合があります。",
        ["warn.japaneseBeta"] = "日本語クライアント対応は新しい機能です。誤認識があれば /mjjp state と /mjjp options の結果を報告してください。",

        ["suggest.openTable"] = "ドマ式麻雀の卓に着くと推奨が表示されます。",
        ["suggest.reading"] = "手牌を読み取り中…",
        ["suggest.hand"] = "手牌: {0}   ({1})",
        ["suggest.complete"] = "和了形です。和了を宣言してください。",
        ["suggest.discard"] = "打",
        ["suggest.discardDetail"] = "→ {0}、受け入れ {1}枚",
        ["suggest.accepts"] = "有効牌: {0}",
        ["suggest.alt"] = "   次点 {0}: {1}、{2}枚",
        ["suggest.waiting"] = "待ち: {0}（残り{1}枚）",
        ["suggest.riichi"] = "この打牌でリーチ宣言を推奨します。",
        ["suggest.fold"] = "他家リーチ中：最も安全な牌でベタオリします。",
        ["suggest.furiten"] = "フリテンにならない待ちを選んでいます。",
        ["suggest.inRiichi"] = "リーチ中：ツモ切りしてください。",
        ["suggest.ronOffered"] = "ロンできます。和了してください。",
        ["suggest.callOffered"] = "鳴きの選択肢が出ています。",
        ["suggest.threat"] = "リーチ者: {0}",

        ["shanten.complete"] = "和了形",
        ["shanten.tenpai"] = "聴牌",
        ["shanten.n"] = "{0}向聴",

        ["auto.declaring"] = "和了を宣言中",
        ["auto.playing"] = "打牌中",
        ["auto.ron"] = "ロン和了中",
        ["auto.call"] = "鳴きに応答中",
        ["auto.waitingHand"] = "次の局を待機中",
        ["auto.watching"] = "卓を観察中",
        ["auto.waitingMatch"] = "対局を待機中",
        ["auto.stats"] = "和了 {0}   打牌 {1}   リーチ {2}   {3}",
        ["auto.last"] = "直前: {0}",

        ["act.advance"] = "結果画面を進めました",
        ["act.tsumogiri"] = "ツモ切り（リーチ中）",
        ["act.tsumo"] = "ツモ和了を宣言",
        ["act.riichi"] = "リーチを宣言",
        ["act.discard"] = "{0}を打牌",
        ["act.win"] = "ロン和了を宣言",
        ["act.pass"] = "鳴きを見送り",
        ["act.pon"] = "{0}をポン",

        ["settings.header"] = "設定",
        ["settings.language"] = "表示言語",
        ["settings.ranked"] = "ランクマッチでも自動打牌する",
        ["settings.highlight"] = "推奨牌をハイライト表示",
        ["settings.style"] = "表示形式",
        ["settings.colour"] = "色",
        ["settings.delays"] = "自動打牌の待ち時間（ミリ秒、範囲内でランダム）",
        ["settings.delay.discard"] = "打牌",
        ["settings.delay.call"] = "鳴き",
        ["settings.delay.results"] = "結果画面",
        ["settings.resetDelays"] = "待ち時間を初期値に戻す",
        ["settings.strategicAi"] = "戦略AI（押し引き・リーチ判断・着順考慮）",
        ["settings.strategicAiNote"] = "試験的機能です。常にオリる／常にリーチするのではなく、放銃リスクと和了期待値を比較して判断します。",
        ["suggest.push"] = "他家の攻撃に対して押します（和了の価値がリスクを上回る判断）。",
        ["suggest.dama"] = "この打牌でダマテン（リーチしない）を推奨します。",
        ["settings.autoRequeue"] = "対局終了後に自動で再申請（自動打牌時）",
        ["settings.autoRequeueNote"] = "コンテンツファインダーで同じ卓に参加申請し、マッチしたら自動で突入します。",
        ["settings.delay.requeue"] = "再申請",
        ["queue.queuing"] = "次の対局に参加申請中…",
        ["queue.waiting"] = "次の対局のマッチング待ち",
        ["queue.failed"] = "次の対局への自動参加申請に失敗しました。手動で申請してください。",

        ["lang.Auto"] = "自動（Dalamudの設定に従う）",
        ["lang.English"] = "English",
        ["lang.Japanese"] = "日本語",

        ["style.Brackets"] = "枠",
        ["style.Arrow"] = "矢印",
        ["style.Glow"] = "発光",

        ["color.Violet"] = "紫",
        ["color.Cyan"] = "水色",
        ["color.Green"] = "緑",
        ["color.Amber"] = "琥珀",
        ["color.Rose"] = "ローズ",
        ["color.Magenta"] = "マゼンタ",
        ["color.White"] = "白",

        ["footer"] = "Xeldar Alz 氏の FFXIV-AutoMahjongSolver のフォーク · AGPL-3.0",

        ["block.ranked"] = "ランクマッチの卓です。自動打牌は無効です（/mjjp の設定で有効化できます）。推奨表示は使えます。",
        ["block.unknown"] = "認識済みのカジュアル卓ではないため、自動打牌は無効です。推奨表示は使えます。",

        ["cmd.help"] = "ウィンドウの表示切替。/mjjp auto | suggest [on|off]。デバッグ: dump|tree|board|state|melds|options|capture|probe|diag|events|buttons|click-discard",
        ["cmd.alias"] = "/mjjp の別名です。",
    };

    /// <summary>Whether the UI currently renders in Japanese (explicit setting, else Dalamud's UI language or the client language).</summary>
    public static bool IsJapanese
    {
        get
        {
            return Plugin.Cfg.Language switch
            {
                UiLanguage.Japanese => true,
                UiLanguage.English => false,
                _ => IsJapaneseEnvironment(),
            };
        }
    }

    private static bool IsJapaneseEnvironment()
    {
        string ui = Plugin.PluginInterface.UiLanguage ?? string.Empty;
        if (ui.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
            return true;
        return Plugin.ClientState.ClientLanguage == ClientLanguage.Japanese && ui.Length == 0;
    }

    /// <summary>The string for <paramref name="key"/> in the current UI language.</summary>
    public static string T(string key)
    {
        if (IsJapanese && Ja.TryGetValue(key, out string? ja))
            return ja;
        return En.TryGetValue(key, out string? en) ? en : key;
    }

    /// <summary>The formatted string for <paramref name="key"/> in the current UI language.</summary>
    public static string F(string key, params object[] args) => string.Format(T(key), args);

    public static string Tile(int tile) => TileNames.Display(tile, IsJapanese);

    public static string Tiles(IEnumerable<int> tiles) => TileNames.Join(tiles, IsJapanese);

    public static string Shanten(int shanten) => shanten switch
    {
        Engine.ShantenCalculator.Complete => T("shanten.complete"),
        0 => T("shanten.tenpai"),
        _ => F("shanten.n", shanten),
    };
}
