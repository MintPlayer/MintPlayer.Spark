namespace MintPlayer.Spark.Abstractions.Licensing;

/// <summary>
/// What the RavenDB server's licence allows (warehouse WX13, commerce S18): read from
/// <c>GET /license/status</c>, never assumed. The Community caps on indexes that ravendb.net/buy still
/// lists were lifted on 2024-05-22 (#18564), and a licence refreshes them when the server reaches
/// api.ravendb.net — so the numbers come from the server or not at all.
/// </summary>
/// <remarks>
/// Field names are those of <c>Raven.Server.Commercial.LicenseStatus</c> (RavenDB 7.2.6, read by reflection
/// on 2026-10-19 for spike W6/W19): <c>HasRavenEtl</c>, <c>MaxNumberOfStaticIndexesPerDatabase</c>,
/// <c>MaxNumberOfAutoIndexesPerDatabase</c>, <c>MaxNumberOfSubscriptionsPerDatabase</c> (the limits are
/// nullable: null means unlimited).
/// </remarks>
public interface ISparkLicenseCapabilities
{
    /// <summary>
    /// The licence, read once per process and cached; <see langword="null"/> when the status could not be read
    /// (an unreachable server, or a certificate without the right to read it). Callers decide what unknown means.
    /// </summary>
    Task<SparkLicenseStatus?> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The database's index usage against the licence's limits. Startup logs a warning when the static
    /// indexes exceed the limit; a health check reports auto indexes against theirs.
    /// </summary>
    Task<SparkIndexBudget?> GetIndexBudgetAsync(CancellationToken cancellationToken = default);
}

/// <summary>What <c>/license/status</c> says (see <see cref="ISparkLicenseCapabilities"/>).</summary>
public sealed record SparkLicenseStatus
{
    /// <summary>The licence type as the server names it (<c>Community</c>, <c>Developer</c>, <c>Professional</c>, …).</summary>
    public string? Type { get; init; }

    /// <summary>Whether RavenDB-to-RavenDB ETL is available.</summary>
    public bool HasRavenEtl { get; init; }

    /// <summary>Static indexes allowed per database; null = unlimited.</summary>
    public int? MaxStaticIndexesPerDatabase { get; init; }

    /// <summary>Auto indexes allowed per database; null = unlimited.</summary>
    public int? MaxAutoIndexesPerDatabase { get; init; }

    /// <summary>Data subscriptions allowed per database; null = unlimited.</summary>
    public int? MaxSubscriptionsPerDatabase { get; init; }
}

/// <summary>Indexes in use against the licence's limits.</summary>
public sealed record SparkIndexBudget(int StaticIndexes, int AutoIndexes, int? MaxStaticIndexes, int? MaxAutoIndexes)
{
    /// <summary>Whether the static indexes exceed the limit.</summary>
    public bool StaticOverLimit => MaxStaticIndexes is { } max && StaticIndexes > max;

    /// <summary>Whether the auto indexes exceed the limit.</summary>
    public bool AutoOverLimit => MaxAutoIndexes is { } max && AutoIndexes > max;
}
