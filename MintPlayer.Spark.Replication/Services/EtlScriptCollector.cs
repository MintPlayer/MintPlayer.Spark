using System.Reflection;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Models;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>
/// Scans assemblies for [Replicated] attributes and groups the ETL scripts by source module.
/// </summary>
internal class EtlScriptCollector
{
    /// <summary>
    /// Scans the given assemblies for classes decorated with [Replicated] and returns
    /// ETL scripts grouped by source module name.
    /// </summary>
    public Dictionary<string, List<EtlScriptItem>> CollectScripts(params Assembly[] assemblies)
    {
        var result = new Dictionary<string, List<EtlScriptItem>>(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in assemblies)
        {
            var replicatedTypes = assembly.GetExportedTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false })
                .Select(t => new
                {
                    Type = t,
                    Attribute = t.GetCustomAttribute<ReplicatedAttribute>()
                })
                .Where(x => x.Attribute != null);

            foreach (var item in replicatedTypes)
            {
                var attr = item.Attribute!;
                var sourceCollection = attr.SourceCollection
                    ?? InferCollectionName(attr.OriginalType ?? item.Type);

                if (!result.TryGetValue(attr.SourceModule, out var scripts))
                {
                    scripts = new List<EtlScriptItem>();
                    result[attr.SourceModule] = scripts;
                }

                scripts.Add(new EtlScriptItem
                {
                    SourceCollection = sourceCollection,
                    Script = attr.EtlScript,
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Infers RavenDB collection name from a CLR type using the default pluralization convention.
    /// </summary>
    private static string InferCollectionName(Type type)
    {
        var name = type.Name;

        // Simple English pluralization matching RavenDB defaults
        if (name.EndsWith("y", StringComparison.Ordinal) && !name.EndsWith("ey", StringComparison.Ordinal) && !name.EndsWith("ay", StringComparison.Ordinal) && !name.EndsWith("oy", StringComparison.Ordinal))
            return name[..^1] + "ies";
        if (name.EndsWith("s", StringComparison.Ordinal) || name.EndsWith("x", StringComparison.Ordinal) || name.EndsWith("sh", StringComparison.Ordinal) || name.EndsWith("ch", StringComparison.Ordinal))
            return name + "es";

        return name + "s";
    }
}
