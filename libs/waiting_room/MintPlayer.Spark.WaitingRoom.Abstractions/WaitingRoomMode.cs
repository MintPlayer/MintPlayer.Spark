namespace MintPlayer.Spark.WaitingRoom;

/// <summary>
/// How a waiting room orders its queue (D10). The one type for the mode: the waiting room runs it
/// (<c>WaitingRoomDefinition.Mode</c>) and the commerce configuration allows it per channel
/// (<c>Spark:Commerce:Channels:{name}:WaitingRoom:Modes</c>), so neither restates the other's enum.
/// </summary>
public enum WaitingRoomMode
{
    /// <summary>
    /// The default. Everyone who joins before the sale starts is shuffled at the start with a committed seed; later
    /// arrivals queue first-come behind them. Arriving early buys nothing, so a script that floods at T−1 s gains nothing.
    /// Needs sign-in (one entry per identity) unless overridden.
    /// </summary>
    RandomizedPreQueue,

    /// <summary>
    /// Registration, then a provably fair draw (seed committed before registration closes, revealed after), then personal
    /// single-use purchase links in staged windows; unused places roll to the next group. Needs sign-in unless overridden.
    /// </summary>
    Ballot,

    /// <summary>First come, first served, ordered by the server's commit time (<c>@last-modified</c>, captured once: 0 inversions measured, research §3.6).</summary>
    Fifo,
}
