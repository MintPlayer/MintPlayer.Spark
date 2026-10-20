using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Licensing;
using MintPlayer.Spark.Replication.Abstractions.Configuration;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>
/// Which transport this module delivers with as an owner (warehouse WD17): chosen once, by licence capability (WX13).
/// Every generated manifest fits ETL (the grammar is null-safe member paths only), so the licence alone decides.
/// </summary>
internal interface IReplicationTransportResolver
{
    /// <summary><see cref="ReplicationTransport.Http"/> or <see cref="ReplicationTransport.Etl"/>, never Auto.</summary>
    /// <exception cref="InvalidOperationException">The transport is pinned to ETL and the licence has none (F18).</exception>
    Task<ReplicationTransport> ResolveAsync(CancellationToken cancellationToken = default);
}

internal sealed partial class ReplicationTransportResolver : IReplicationTransportResolver
{
    [Inject] private readonly IOptions<SparkReplicationOptions> options;
    [Inject] private readonly ISparkLicenseCapabilities licence;
    [Inject] private readonly ILogger<ReplicationTransportResolver> logger;

    private readonly SemaphoreSlim gate = new(1, 1);
    private ReplicationTransport? resolved;

    public async Task<ReplicationTransport> ResolveAsync(CancellationToken cancellationToken = default)
    {
        if (resolved is { } known)
            return known;

        await gate.WaitAsync(cancellationToken);
        try
        {
            resolved ??= await DecideAsync(cancellationToken);
            return resolved.Value;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ReplicationTransport> DecideAsync(CancellationToken cancellationToken)
    {
        var configured = options.Value.Transport;
        if (configured == ReplicationTransport.Http)
            return ReplicationTransport.Http;

        var status = await licence.GetAsync(cancellationToken);
        if (configured == ReplicationTransport.Etl)
        {
            if (status is { HasRavenEtl: false })
                throw new InvalidOperationException(
                    $"Spark:Replication:Transport is 'Etl', but this RavenDB licence ({status.Type ?? "unknown type"}) has no RavenDB ETL. " +
                    "Set Spark:Replication:Transport to 'Http' (or 'Auto'), or use a licence with ETL.");
            if (status is null)
                logger.LogWarning("The RavenDB licence could not be read; ETL replication was pinned, so it is used anyway.");
            return ReplicationTransport.Etl;
        }

        if (status is { HasRavenEtl: true })
        {
            logger.LogInformation("Replication transport: ETL (the {Licence} licence has RavenDB ETL).", status.Type);
            return ReplicationTransport.Etl;
        }

        logger.LogInformation("Replication transport: HTTP ({Reason}).", status is null ? "the licence could not be read" : "the licence has no ETL");
        return ReplicationTransport.Http;
    }
}
