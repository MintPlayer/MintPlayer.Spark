using System.Reflection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>One <c>[Replica]</c> type of this module, resolved (warehouse WX2).</summary>
/// <param name="Type">The replica's CLR type.</param>
/// <param name="Contract">The contract it reads.</param>
/// <param name="Versions">The contract versions it reads, ascending.</param>
/// <param name="WriteBack">Whether its edits become write-back requests.</param>
/// <param name="Collection">Its collection in this module's database.</param>
/// <param name="Sources">The modules configured to feed it (<c>Consume:{contract}:Sources</c>).</param>
internal sealed record ReplicaBinding(Type Type, string Contract, int[] Versions, bool WriteBack, string Collection, string[] Sources)
{
    /// <summary>Whether <paramref name="module"/> is a configured source of this contract.</summary>
    public bool IsSource(string module) => Sources.Contains(module, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The replica types this module declares: scanned from <see cref="SparkReplicationOptions.AssembliesToScan"/>
/// (the entry assembly by default) once. Also the framework's <see cref="ISparkReplicaTypes"/>.
/// </summary>
internal sealed class ReplicaCatalog : ISparkReplicaTypes
{
    private readonly Dictionary<Type, ReplicaBinding> byType = new();
    private readonly Dictionary<string, ReplicaBinding> byContract = new(StringComparer.Ordinal);

    public ReplicaCatalog(IOptions<SparkReplicationOptions> options, IDocumentStore documentStore)
        : this(options.Value, documentStore.Conventions.FindCollectionName)
    {
    }

    internal ReplicaCatalog(SparkReplicationOptions options, Func<Type, string> collectionName)
    {
        var assemblies = options.AssembliesToScan.Length > 0
            ? options.AssembliesToScan
            : Assembly.GetEntryAssembly() is { } entry ? [entry] : [];

        foreach (var type in assemblies.SelectMany(ExportedTypes).Distinct())
        {
            if (Resolve(type) is not { } resolved)
                continue;

            var (contract, versions, writeBack) = resolved;
            var sources = options.Consume.TryGetValue(contract, out var consume) ? consume.Sources : [];
            var binding = new ReplicaBinding(type, contract, versions, writeBack, collectionName(type), sources);

            if (byContract.TryGetValue(contract, out var other))
                throw new InvalidOperationException(
                    $"[Replica] types {other.Type.FullName} and {type.FullName} both read contract '{contract}'. One replica type per contract.");

            byType[type] = binding;
            byContract[contract] = binding;
        }
    }

    /// <summary>Every replica type.</summary>
    public IReadOnlyCollection<ReplicaBinding> All => byType.Values;

    /// <summary>Whether any replica type sends write-back requests (the SyncAction worker is needed, WX3).</summary>
    public bool AnyWriteBack => byType.Values.Any(b => b.WriteBack);

    public ReplicaBinding? Find(Type type) => byType.GetValueOrDefault(type);

    public ReplicaBinding? Find(string contract) => byContract.GetValueOrDefault(contract);

    public bool IsReplica(Type entityType) => byType.ContainsKey(entityType);

    /// <summary>
    /// The contract, versions and write-back flag of <paramref name="type"/>, or <see langword="null"/> when it is no
    /// <c>[Replica]</c>. Throws on a declaration that cannot be resolved, at startup.
    /// </summary>
    internal static (string Contract, int[] Versions, bool WriteBack)? Resolve(Type type)
    {
        if (type.GetCustomAttribute<ReplicaAttribute>(inherit: false) is not { } replica)
            return null;

        if (type.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance) is not { CanWrite: true } id || id.PropertyType != typeof(string))
            throw new InvalidOperationException($"[Replica] {type.FullName} needs a public settable 'string? Id': the source's document id.");

        // The base record names the contract unless the attribute does.
        ReplicationContractAttribute? declared = null;
        for (var current = type.BaseType; current is not null && declared is null; current = current.BaseType)
            declared = ReplicationContractAttribute.Of(current);

        var contract = replica.Contract ?? declared?.Name
            ?? throw new InvalidOperationException(
                $"[Replica] {type.FullName} neither names a contract nor derives from a [ReplicationContract] record.");
        if (!ReplicationContract.IsValidName(contract))
            throw new InvalidOperationException(
                $"[Replica] {type.FullName} names contract '{contract}', which is not lower-kebab (a-z, digits, single hyphens).");

        var versions = replica.Versions.Length > 0
            ? replica.Versions
            : declared is not null && declared.Name == contract
                ? [declared.Version]
                : [1];
        if (versions.Any(v => v < 1))
            throw new InvalidOperationException($"[Replica] {type.FullName} reads a contract version below 1.");

        return (contract, [.. versions.Distinct().Order()], replica.WriteBack);
    }

    private static IEnumerable<Type> ExportedTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetExportedTypes().Where(t => t is { IsClass: true, IsAbstract: false });
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true })!;
        }
    }
}
