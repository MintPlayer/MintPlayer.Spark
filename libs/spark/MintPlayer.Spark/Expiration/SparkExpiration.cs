namespace MintPlayer.Spark.Expiration;

/// <summary>
/// Facts about RavenDB document expiration that every Spark library shares
/// (<c>docs/seo_analyzer_PRD.md</c> X1).
/// </summary>
public static class SparkExpiration
{
    /// <summary>
    /// The most often RavenDB may sweep expired documents: every 36 hours.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A licence <b>constraint</b>, not a tuning choice. Measured against production on 2026-09-07:
    /// <c>/license/status</c> reports <c>Type: Community</c> with <c>MinPeriodForExpirationInHours: 36</c>,
    /// and the live database record carries <c>DeleteFrequencyInSec: 129600</c>.
    /// </para>
    /// <para>
    /// Anything smaller does not degrade, it <b>fails the server</b>: the cluster command is rejected by
    /// <c>ClusterStateMachine.AssertExpirationConfiguration</c> and the RavenDB process dies, which
    /// presents as "the server exited before becoming healthy". CI and local runs hold a Developer
    /// licence, which imposes no minimum, so the mistake is invisible there — which is why
    /// <see cref="ValidateDeleteFrequency"/> refuses it on every licence. (Messaging once took the 60 s
    /// server default on the strength of a comment that read the limit backwards; it would have taken
    /// production's database down on the next deploy.)
    /// </para>
    /// <para>
    /// The same 36 h floor applies to <c>@refresh</c> (<c>MinPeriodForRefreshInHours</c>), which is why
    /// refresh cannot carry message retries on Community.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan MinimumDeleteFrequency = TimeSpan.FromHours(36);

    /// <summary>
    /// Refuses a delete frequency more often than <see cref="MinimumDeleteFrequency"/>, whatever the
    /// licence. Returns the frequency in whole seconds, as RavenDB takes it.
    /// </summary>
    /// <exception cref="InvalidOperationException">Below the floor.</exception>
    public static long ValidateDeleteFrequency(TimeSpan frequency)
    {
        if (frequency < MinimumDeleteFrequency)
            throw new InvalidOperationException(
                $"Spark:Expiration:DeleteFrequency is {frequency}, more often than every {MinimumDeleteFrequency.TotalHours:0} hours. " +
                "The RavenDB Community licence refuses that and the server dies on the configuration change; a Developer " +
                "licence accepts it, so it would pass every test and fail in production. Use 36 hours or more. Expired " +
                "documents are hidden from every Spark read regardless of when the sweep deletes them.");
        return (long)Math.Ceiling(frequency.TotalSeconds);
    }

    /// <summary>
    /// <paramref name="value"/> as UTC: a <see cref="DateTimeKind.Local"/> value is converted, an
    /// unspecified one is taken to be UTC already (how RavenDB round-trips a UTC value without a
    /// <c>Z</c>).
    /// </summary>
    public static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
