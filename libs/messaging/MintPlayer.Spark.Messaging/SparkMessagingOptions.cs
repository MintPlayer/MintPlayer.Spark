namespace MintPlayer.Spark.Messaging;

/// <summary>
/// Durable-messaging settings, bound from the <c>Spark:Messaging</c> configuration section and then
/// overridable in code.
/// </summary>
/// <remarks>
/// <b>Never give a collection property a non-empty initializer here.</b> The rule is about
/// <i>collections</i>, not about initializers in general — the binder treats the two kinds
/// differently:
/// <list type="bullet">
/// <item>
/// <b>Scalars</b> (<c>int</c>, <c>TimeSpan</c>, <c>string</c>) — the binder <b>replaces</b> the value.
/// Initialize them freely; <c>MaxAttempts = 5</c> below is correct and a configured <c>1</c> wins.
/// </item>
/// <item>
/// <b>Collections</b> (<c>T[]</c>, <c>List&lt;T&gt;</c>) — the binder <b>appends</b> to whatever is
/// already there. A non-empty initializer therefore survives binding and stays <b>first</b>, which is
/// the position that usually decides behaviour.
/// </item>
/// </list>
/// Note that <c>= []</c> on a collection is not optional politeness: without it the property is
/// <see langword="null"/> and the <c>Resolved*</c> member below would throw.
/// <para>
/// That is not a theoretical concern. It shipped once already, as <b>F14</b>: a configured
/// <c>SparkModulesUrls</c> landed behind the hardcoded <c>http://localhost:8080</c>, and since
/// <c>DocumentStore</c> connects to the first URL, every deployment that configured where its module
/// registry lived was still talking to localhost — with no error and a config file that said
/// otherwise. It was found from the far end, days later, by two processes that could not see each
/// other's data.
/// </para>
/// <para>
/// The pattern to follow instead is <see cref="BackoffDelays"/>: default the property to empty, put
/// the real default in a separate <c>Default*</c> constant, and expose a <c>Resolved*</c> member that
/// picks one. Consumers read the resolved member; nothing reads the raw property.
/// </para>
/// </remarks>
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
