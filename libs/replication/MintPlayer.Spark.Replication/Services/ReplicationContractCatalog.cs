using Microsoft.Extensions.Options;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>A contract this module publishes, translated once (warehouse WX1/WX2).</summary>
/// <param name="Contract">The declaration.</param>
/// <param name="SourceCollection">The owner's collection the contract reads, from the store's conventions.</param>
/// <param name="Fields">The fields the ETL transform selects; <see langword="null"/> when the projection needs code (HTTP-only, Q-W17).</param>
/// <param name="EtlRefusal">Why the contract cannot go over ETL, when <see cref="Fields"/> is null.</param>
/// <param name="Writable">Contract field → source property, for write-back (only plain top-level selections).</param>
/// <param name="Project">The compiled projection, for the HTTP transport.</param>
internal sealed record PublishedContract(
    ReplicationContract Contract,
    string SourceCollection,
    IReadOnlyList<EtlField>? Fields,
    string? EtlRefusal,
    IReadOnlyDictionary<string, string> Writable,
    Func<object, object?> Project)
{
    public string Name => Contract.Name;
    public int Version => Contract.Version;

    /// <summary>Whether the contract can be delivered over ETL.</summary>
    public bool EtlExpressible => Fields is not null;

    /// <summary>The ETL transform loading this contract into a consumer's <paramref name="targetCollection"/>.</summary>
    /// <exception cref="InvalidOperationException">The contract is HTTP-only.</exception>
    public string ScriptFor(string targetCollection)
        => Fields is { } fields
            ? EtlTransformGenerator.Script(fields, targetCollection)
            : throw new InvalidOperationException(EtlRefusal);
}

/// <summary>
/// The contracts this module publishes as an owner. Built once, eagerly at startup
/// (<see cref="SparkReplicationExtensions.UseSparkReplication"/>), so a projection problem shows then.
/// </summary>
internal sealed class ReplicationContractCatalog
{
    private readonly Dictionary<(string Name, int Version), PublishedContract> contracts = new();

    public ReplicationContractCatalog(IOptions<SparkReplicationOptions> options, IDocumentStore documentStore)
        : this(options.Value, documentStore.Conventions.FindCollectionName)
    {
    }

    internal ReplicationContractCatalog(SparkReplicationOptions options, Func<Type, string> collectionName)
    {
        foreach (var contract in options.Contracts)
        {
            var fields = EtlTransformGenerator.TryTranslate(contract, out var refusal);
            var compiled = contract.Projection.Compile();
            contracts[(contract.Name, contract.Version)] = new PublishedContract(
                contract,
                collectionName(contract.SourceType),
                fields,
                refusal,
                EtlTransformGenerator.WritableFields(contract),
                source => compiled.DynamicInvoke(source));
        }
    }

    /// <summary>Every published contract.</summary>
    public IEnumerable<PublishedContract> All => contracts.Values;

    /// <summary>The published contract <paramref name="name"/> at <paramref name="version"/>, or <see langword="null"/>.</summary>
    public PublishedContract? Find(string name, int version)
        => contracts.TryGetValue((name, version), out var contract) ? contract : null;

    /// <summary>Whether any version of <paramref name="name"/> is published.</summary>
    public bool Publishes(string name) => contracts.Keys.Any(k => k.Name == name);

    /// <summary>The highest version of <paramref name="name"/> among <paramref name="versions"/> that this module publishes.</summary>
    public PublishedContract? Highest(string name, IEnumerable<int> versions)
        => versions.OrderDescending().Select(v => Find(name, v)).FirstOrDefault(c => c is not null);

    /// <summary>The contracts reading <paramref name="sourceType"/> (or a base of it).</summary>
    public IEnumerable<PublishedContract> ForSource(Type sourceType)
        => contracts.Values.Where(c => c.Contract.SourceType.IsAssignableFrom(sourceType));

    /// <summary>Whether <paramref name="type"/> is the source of any published contract.</summary>
    public bool IsSource(Type type) => contracts.Values.Any(c => c.Contract.SourceType.IsAssignableFrom(type));
}
