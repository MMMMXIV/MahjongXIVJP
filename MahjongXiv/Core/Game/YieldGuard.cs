namespace MahjongXiv.Core.Game;

/// <summary>
/// Decides when "could not act" turns into "stop auto-play". Not knowing what to do is not unsafe:
/// the game's own timer plays the turn (tsumogiri) or passes the call, so the driver simply yields that
/// one turn or popup and carries on. Only a run of consecutive yields, with no successful action in
/// between, suggests the reading itself is broken, and then auto-play stops.
/// </summary>
internal sealed class YieldGuard
{
    public const int Limit = 3;

    public int Consecutive { get; private set; }

    public int Total { get; private set; }

    /// <summary>Records a yield; true when this one reaches the limit and auto-play should stop.</summary>
    public bool RecordYield()
    {
        Consecutive++;
        Total++;
        return Consecutive >= Limit;
    }

    /// <summary>An action went through (a discard or a call answer was sent): the run is broken.</summary>
    public void RecordSuccess() => Consecutive = 0;

    public void Reset()
    {
        Consecutive = 0;
        Total = 0;
    }
}
