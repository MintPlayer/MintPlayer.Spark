namespace MintPlayer.Spark.Messaging.Abstractions;

/// <summary>
/// Decides, for one failed attempt, whether to try again and when.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately narrow. It takes an attempt number and nothing else:
/// </para>
/// <list type="bullet">
/// <item><b>No clock.</b> Returning a <i>delay</i> rather than an instant keeps a schedule a pure
/// value — constructible from configuration, comparable, printable in the startup table, and
/// testable with no server and no fake clock. The single call site adds the clock.</item>
/// <item><b>No exception.</b> Non-retryability is already a separate working concern
/// (<see cref="NonRetryableException"/>). Passing the failure in would invite schedules that branch
/// on exception type, duplicating that decision in a second place.</item>
/// <item><b>No queue name.</b> The instance is already resolved per lane; passing the lane back in
/// would be redundant.</item>
/// </list>
/// </remarks>
public interface IRetrySchedule
{
    /// <param name="attempt">
    /// Attempts already made, <b>including</b> the one that just failed. One-based.
    /// </param>
    RetryDecision Next(int attempt);
}

/// <summary>What should happen after a failed attempt.</summary>
public abstract record RetryDecision
{
    private RetryDecision() { }

    /// <summary>Try again after <paramref name="Delay"/>.</summary>
    public sealed record RetryAfter(TimeSpan Delay) : RetryDecision;

    /// <summary>Stop trying. The handler is dead-lettered with this reason.</summary>
    public sealed record DeadLetter(string Reason) : RetryDecision;
}
