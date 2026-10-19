using System.Reflection;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Models;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>
/// Scans assemblies for <c>[Replicated]</c> replica types and groups the contracts they read by
/// source module. What it collects is all a consumer ever sends an owner: contract, version and
/// the replica's collection. No script (F16).
/// </summary>
internal class ReplicaContractCollector
{
    /// <summary>
    /// The contracts the <c>[Replicated]</c> types in <paramref name="assemblies"/> read, grouped by
    /// source module. <paramref name="collectionName"/> names each replica type's collection (the
    /// consumer store's conventions).
    /// </summary>
    public Dictionary<string, List<EtlContractRequest>> Collect(Func<Type, string> collectionName, params Assembly[] assemblies)
    {
        var result = new Dictionary<string, List<EtlContractRequest>>(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in assemblies)
        {
            // Cache the [Replicated]-annotated types per assembly so repeat calls (e.g.
            // collecting from the same assembly more than once during testing or
            // module re-init) don't re-walk the metadata tables.
            var replicaTypes = ReflectionCache.GetOrAdd<(string Op, Assembly Asm), IReadOnlyList<(Type Type, ReplicatedAttribute Attribute)>>(
                ("ReplicaContractCollector.ReplicaTypes", assembly),
                static k => k.Asm.GetExportedTypes()
                    .Where(t => t is { IsClass: true, IsAbstract: false })
                    .Select(t => (Type: t, Attribute: t.GetCachedCustomAttribute<ReplicatedAttribute>()))
                    .Where(x => x.Attribute != null)
                    .Select(x => (x.Type, x.Attribute!))
                    .ToArray());

            foreach (var (type, attr) in replicaTypes)
            {
                if (!ReplicationContract.IsValidName(attr.Contract))
                    throw new InvalidOperationException(
                        $"[Replicated] on {type.FullName} names contract '{attr.Contract}', which is not lower-kebab (a-z, digits, single hyphens).");

                if (!result.TryGetValue(attr.SourceModule, out var contracts))
                {
                    contracts = [];
                    result[attr.SourceModule] = contracts;
                }

                contracts.Add(new EtlContractRequest
                {
                    Contract = attr.Contract,
                    Version = attr.Version,
                    TargetCollection = collectionName(type),
                });
            }
        }

        return result;
    }
}
