namespace MintPlayer.Spark.Messaging;

public class SparkMessagingOptions
{
    public int MaxAttempts { get; set; } = 5;
    public TimeSpan FallbackPollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long to wait before each retry, indexed by attempt. Read it through
    /// <see cref="ResolvedBackoffDelays"/>, never directly.
    /// </summary>
    /// <remarks>
    /// Empty by default <b>on purpose</b>, and the reason is the same one that produced F14 in the
    /// replication options: .NET's configuration binder does not replace a collection that already has
    /// elements, it <i>appends</i> to it. A hardcoded initializer here would survive binding and stay
    /// first, so an app configuring a faster schedule would still wait the default five seconds on its
    /// first retry — silently, with a config file that said otherwise. Applying the default at the
    /// point of use is what keeps a configured value from queueing up behind it.
    /// </remarks>
    public TimeSpan[] BackoffDelays { get; set; } = [];

    /// <summary>The schedule used when nothing is configured: 5s, 30s, 2m, 10m, 1h.</summary>
    public static readonly TimeSpan[] DefaultBackoffDelays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromHours(1),
    ];

    /// <summary>The delays to actually use: what was configured, or the default when nothing was.</summary>
    public TimeSpan[] ResolvedBackoffDelays =>
        BackoffDelays.Length > 0 ? BackoffDelays : DefaultBackoffDelays;

    public int RetentionDays { get; set; } = 7;
}
