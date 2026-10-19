using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Licensing;
using MintPlayer.Spark.Replication.Abstractions.Configuration;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>
/// Which transport this module delivers with as an owner (warehouse WD1 refined): chosen once, by licence
/// capability (WX13).
/// </summary>
internal interface IReplicationTransportResolver
{
    /// <summary>
    /// <see cref="ReplicationTransport.Http"/> or <see cref="ReplicationTransport.Etl"/>, never Auto.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The transport is pinned to ETL and the licence has none (F18), or an ETL contract needs code.
    /// The message says what to change.
    /// </exception>
    Task<ReplicationTransport> ResolveAsync(CancellationToken cancellationToken = default);
}

internal sealed partial class ReplicationTransportResolver : IReplicationTransportResolver
{
    [Inject] private readonly IOptions<SparkReplicationOptions> options;
    [Inject] private readonly ISparkLicenseCapabilities licence;
    [Inject] private readonly ReplicationContractCatalog catalog;
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
        var httpOnly = catalog.All.Where(c => !c.EtlExpressible).ToList();

        if (configured == ReplicationTransport.Http)
            return ReplicationTransport.Http;

        // Only an owner reads the licence: a module that publishes nothing never delivers.
        var status = catalog.All.Any() ? await licence.GetAsync(cancellationToken) : null;

        if (configured == ReplicationTransport.Etl)
        {
            if (status is { HasRavenEtl: false })
                throw new InvalidOperationException(
                    $"Spark:Replication:Transport is 'Etl', but this RavenDB licence ({status.Type ?? "unknown type"}) has no RavenDB ETL. " +
                    "Set Spark:Replication:Transport to 'Http' (or 'Auto'), or use a licence with ETL.");
            if (httpOnly.Count > 0)
                throw new InvalidOperationException(
                    $"Spark:Replication:Transport is 'Etl', but contract '{httpOnly[0].Name}' v{httpOnly[0].Version} cannot be expressed as an ETL transform: {httpOnly[0].EtlRefusal}");
            if (status is null)
                logger.LogWarning("The RavenDB licence could not be read; ETL replication was pinned, so it is used anyway.");
            return ReplicationTransport.Etl;
        }

        // Auto: ETL only when the licence has it AND every contract can go over it; HTTP otherwise.
        if (status is { HasRavenEtl: true } && httpOnly.Count == 0)
        {
            logger.LogInformation("Replication transport: ETL (the {Licence} licence has RavenDB ETL).", status.Type);
            return ReplicationTransport.Etl;
        }

        foreach (var contract in httpOnly)
            logger.LogInformation("Replication contract '{Contract}' v{Version} is HTTP-only: {Reason}", contract.Name, contract.Version, contract.EtlRefusal);
        logger.LogInformation("Replication transport: HTTP ({Reason}).",
            status is null ? "the licence could not be read" : status.HasRavenEtl ? "a contract needs code" : "the licence has no ETL");
        return ReplicationTransport.Http;
    }
}
