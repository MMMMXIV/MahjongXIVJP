namespace MahjongXiv.Core.Game;

internal enum DiscardStep { Wait, Highlight, Click, Stop }

/// <summary>One input per observation, with no timeout fallback or replay after a click.</summary>
internal sealed class DiscardHandshake
{
    private string? identity;
    private long started;
    private long readySince = -1;
    private bool sent;
    public string? Failure { get; private set; }

    public void Reset()
    {
        identity = null;
        started = 0;
        readySince = -1;
        sent = false;
        Failure = null;
    }

    public DiscardStep Next(int state, string key, long now)
    {
        if (Failure != null) return DiscardStep.Stop;
        if (state is not (6 or 28 or 30)) return DiscardStep.Wait;
        if (identity != null && identity != key)
            return Fail("hand, target or addon changed during discard");
        if (identity == null)
        {
            identity = key;
            started = now;
            if (state == 30) readySince = now;
            return DiscardStep.Highlight;
        }
        if (sent) return DiscardStep.Wait;
        if (now - started >= 3000) return Fail("discard surface did not settle within 3000ms");
        if (state != 30)
        {
            if (readySince >= 0) return Fail("discard surface regressed before click");
            return DiscardStep.Wait;
        }
        if (readySince < 0) readySince = now;
        if (now - readySince < 120) return DiscardStep.Wait;
        sent = true; // reserve before invoking native code; never retry an ambiguous send
        return DiscardStep.Click;
    }

    public DiscardStep Fail(string reason)
    {
        Failure ??= reason;
        return DiscardStep.Stop;
    }

    public static bool SameTurn(int previous, int current)
        => previous == current || (previous is 6 or 28 or 30 && current is 6 or 28 or 30);

    public static bool ShouldRearm(int previous, int current, bool newOpponentDiscard)
        => !SameTurn(previous, current) || (previous == 15 && current == 15 && newOpponentDiscard);
}
