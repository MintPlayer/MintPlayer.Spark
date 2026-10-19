using Microsoft.Extensions.Options;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>A contract this module publishes, translated for the ETL transport.</summary>
/// <param name="Contract">The declaration.</param>
/// <param name="SourceCollection">The owner's collection the contract reads, from the store's conventions.</param>
/// <param name="Fields">The fields the projection selects.</param>
internal sealed record PublishedContract(ReplicationContract Contract, string SourceCollection, IReadOnlyList<EtlField> Fields)
{
    /// <summary>The ETL transform loading this contract into a consumer's <paramref name="targetCollection"/>.</summary>
    public string ScriptFor(string targetCollection) => EtlTransformGenerator.Script(Fields, targetCollection);
}

/// <summary>
/// The contracts this module publishes as an owner (warehouse WX2). Built once, eagerly: resolving
/// it translates every projection, so a projection the ETL transform cannot express refuses startup
/// (<see cref="SparkReplicationExtensions.UseSparkReplication"/> resolves it before anything runs).
/// </summary>
internal sealed class ReplicationContractCatalog
{
    private readonly Dictionary<(string Name, int Version), PublishedContract> contracts = new();

    public ReplicationContractCatalog(IOptions<SparkReplicationOptions> options, IDocumentStore documentStore)
    {
        foreach (var contract in options.Value.Contracts)
        {
            var fields = EtlTransformGenerator.Translate(contract);
            var sourceCollection = documentStore.Conventions.FindCollectionName(contract.SourceType);
            contracts[(contract.Name, contract.Version)] = new PublishedContract(contract, sourceCollection, fields);
        }
    }

    /// <summary>Every published contract.</summary>
    public IEnumerable<PublishedContract> All => contracts.Values;

    /// <summary>The published contract <paramref name="name"/> at <paramref name="version"/>, or <see langword="null"/>.</summary>
    public PublishedContract? Find(string name, int version)
        => contracts.TryGetValue((name, version), out var contract) ? contract : null;
}
