namespace MahjongXiv.Core.Game;

/// <summary>What a call / self-action popup button does, independent of the client language.</summary>
internal enum EmjAction
{
    Unknown,
    Ron,
    Tsumo,
    Riichi,
    Pon,
    Chi,
    Kan,
    NineTerminals,
    Pass,
}

/// <summary>
/// Localized Emj panel- and button-string matching. This file has no Dalamud dependency so the test
/// project can compile it directly.
/// <para>
/// English strings were verified on a live English client. Japanese strings are the standard riichi
/// terms the Japanese client uses; if a Japanese table misreads, run <c>/mj state</c> and
/// <c>/mj options</c> on that client and add the captured text here. Matched winds normalize to the
/// canonical English word the rest of the code expects.
/// </para>
/// </summary>
internal static class EmjStrings
{
    private static readonly string[] Canonical = ["East", "South", "West", "North"];

    // One localized seat-wind word -> wind index 0..3 (E/S/W/N).
    private static readonly Dictionary<string, int> WindIndex = new(StringComparer.Ordinal)
    {
        // English (verified: self panel read "South").
        ["East"] = 0,
        ["South"] = 1,
        ["West"] = 2,
        ["North"] = 3,

        // Japanese: bare kanji, the "-家" seat form and the "-家" short reading.
        ["東"] = 0,
        ["南"] = 1,
        ["西"] = 2,
        ["北"] = 3,
        ["東家"] = 0,
        ["南家"] = 1,
        ["西家"] = 2,
        ["北家"] = 3,
    };

    // Riichi badge substrings. English verified; Japanese katakana and kanji forms.
    private static readonly string[] RiichiTerms = ["Riichi", "リーチ", "立直"];

    /// <summary>
    /// Button-label substrings per action, checked in this order (more specific first, so e.g. a
    /// nine-terminals label is never mistaken for a plain pass).
    /// </summary>
    private static readonly (EmjAction Action, string[] Terms)[] ActionTerms =
    [
        (EmjAction.NineTerminals, ["Nine", "九種", "九種九牌"]),
        (EmjAction.Riichi, ["Riichi", "リーチ", "立直"]),
        (EmjAction.Ron, ["Ron", "ロン", "栄和"]),
        (EmjAction.Tsumo, ["Tsumo", "ツモ", "自摸"]),
        (EmjAction.Pon, ["Pon", "ポン", "碰"]),
        (EmjAction.Chi, ["Chi", "チー", "吃"]),
        (EmjAction.Kan, ["Kan", "カン", "槓"]),
        (EmjAction.Pass, ["Pass", "Skip", "パス", "スキップ", "スルー", "見送", "キャンセル", "しない"]),
    ];

    /// <summary>Labels of the result-screen button that moves on to the next hand.</summary>
    public static readonly string[] AdvanceButtonTerms = ["次へ", "Next", "Continue", "Proceed", "OK", "確認", "Weiter", "Suivant"];

    /// <summary>The unambiguous subset of <see cref="AdvanceButtonTerms"/>, for screens whose state is not mapped.</summary>
    public static readonly string[] StrictAdvanceButtonTerms = ["次へ", "Next", "Continue", "Proceed", "Weiter", "Suivant"];

    /// <summary>Labels of the button that leaves the table on the final-results screen.</summary>
    public static readonly string[] LeaveButtonTerms = ["退室", "Leave", "Exit", "Quit", "Verlassen", "Quitter"];

    /// <summary>Labels of the affirmative button of a yes/no confirmation dialog.</summary>
    public static readonly string[] YesButtonTerms = ["はい", "Yes", "OK", "Ja", "Oui"];

    /// <summary>Labels of the Duty Finder's join button.</summary>
    public static readonly string[] JoinButtonTerms = ["参加", "参加申請", "Join", "Beitreten", "S'inscrire", "Participer"];

    /// <summary>A unique exact join label; never match withdrawal/cancel text by substring.</summary>
    public static int FindJoinButton(IReadOnlyList<string> labels)
    {
        int match = -1;
        for (int i = 0; i < labels.Count; i++)
        {
            bool known = false;
            foreach (string term in JoinButtonTerms)
                if (string.Equals(labels[i].Trim(), term, StringComparison.OrdinalIgnoreCase))
                    known = true;
            if (!known) continue;
            if (match >= 0) return -1; // ambiguous UI: leave it alone
            match = i;
        }
        return match;
    }

    /// <summary>Labels of the duty-ready popup's commence button.</summary>
    public static readonly string[] CommenceButtonTerms = ["突入", "Commence", "Betreten", "Entrer"];

    /// <summary>The first label containing one of <paramref name="terms"/> (case-insensitive), or -1.</summary>
    public static int IndexOfTerm(IReadOnlyList<string> labels, IReadOnlyList<string> terms)
    {
        for (int i = 0; i < labels.Count; i++)
            foreach (string term in terms)
                if (labels[i].Contains(term, StringComparison.OrdinalIgnoreCase))
                    return i;
        return -1;
    }

    /// <summary>The canonical English seat wind for a localized panel word, or null if unrecognized.</summary>
    public static string? CanonicalWind(string? text)
        => text is not null && WindIndex.TryGetValue(text.Trim(), out int i) ? Canonical[i] : null;

    /// <summary>Wind index 0..3 (E/S/W/N) of a canonical or localized wind word; East when unknown.</summary>
    public static int WindToIndex(string? text)
        => text is not null && WindIndex.TryGetValue(text.Trim(), out int i) ? i : 0;

    /// <summary>Whether a panel text is a riichi badge in a recognized client language.</summary>
    public static bool IsRiichi(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        foreach (string term in RiichiTerms)
            if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Whether a panel text looks like a transient badge ("Pon!", "ポン！") rather than a name.</summary>
    public static bool IsBadge(string text)
        => text.Contains('!') || text.Contains('！') || text.StartsWith('+') || text.StartsWith('-');

    /// <summary>What a popup button label does, in any recognized client language.</summary>
    public static EmjAction Classify(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return EmjAction.Unknown;

        foreach ((EmjAction action, string[] terms) in ActionTerms)
            foreach (string term in terms)
                if (label.Contains(term, StringComparison.OrdinalIgnoreCase))
                    return action;

        return EmjAction.Unknown;
    }

    /// <summary>
    /// Position of the button doing <paramref name="action"/> among <paramref name="labels"/>, or -1.
    /// For <see cref="EmjAction.Pass"/> only, when no label reads as a pass but every other label is
    /// a recognized action and exactly one is not, that one is taken as the pass button: the decline
    /// button is the one label whose wording varies most between client languages, and the
    /// alternative is never answering the popup at all.
    /// </summary>
    public static int IndexOf(IReadOnlyList<string> labels, EmjAction action)
    {
        for (int i = 0; i < labels.Count; i++)
            if (Classify(labels[i]) == action)
                return i;

        if (action != EmjAction.Pass || labels.Count < 2)
            return -1;

        int unknown = -1;
        for (int i = 0; i < labels.Count; i++)
        {
            if (Classify(labels[i]) != EmjAction.Unknown)
                continue;
            if (unknown >= 0)
                return -1; // more than one unrecognized button: refuse to guess
            unknown = i;
        }

        return unknown;
    }
}
