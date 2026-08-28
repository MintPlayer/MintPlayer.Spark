using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.SourceGenerators.Attributes;
using System.Reflection;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Resolves a model file's <c>clrType</c> string to the runtime <see cref="Type"/>.
/// </summary>
/// <remarks>
/// A model concern, not a data-access one. It lived as a private helper inside
/// <c>DatabaseAccess</c> only because that class happened to be where a <see cref="Type"/> was
/// first needed — which is how every caller that needs one ended up having to go through the
/// data-access API to get it, whether or not it wanted to touch the database.
/// </remarks>
internal interface ISparkTypeResolver
{
    /// <returns>The type; <c>null</c> when no loaded assembly declares it, or when
    /// <paramref name="clrType"/> itself is null — a JSON-only virtual type, which by definition
    /// resolves to nothing.</returns>
    Type? Resolve(string? clrType);
}

[Register(typeof(ISparkTypeResolver), ServiceLifetime.Singleton)]
internal partial class SparkTypeResolver : ISparkTypeResolver
{
    public Type? Resolve(string? clrType) => ResolveClrType(clrType);

    /// <summary>
    /// The one clrType-string → <see cref="Type"/> resolution in the framework — the static form
    /// exists so classes that already resolve reflectively (QueryExecutor, SyncActionHandler,
    /// EntityMapper, the streaming executor) share it without a DI edge; everything else takes
    /// <see cref="ISparkTypeResolver"/>. Union of the semantics the former private copies had:
    /// assembly-qualified, then namespace-qualified per assembly, then a full-or-bare-name scan
    /// (excluding abstract/interface — a model type is always concrete).
    /// </summary>
    internal static Type? ResolveClrType(string? clrType)
    {
        if (clrType is null) return null; // JSON-only virtual type: resolves to nothing

        // Cached (nulls too) because the miss path walks every loaded assembly, and a miss is
        // the common case for the first lookup of each type in an app with many assemblies.
        return ReflectionCache.GetOrAdd<Type?>(
            $"resolveType|{clrType}",
            () =>
            {
                var type = Type.GetType(clrType);
                if (type != null) return type;

                var assemblies = AppDomain.CurrentDomain.GetAssemblies();

                foreach (var assembly in assemblies)
                {
                    type = assembly.GetType(clrType);
                    if (type != null) return type;
                }

                foreach (var assembly in assemblies)
                {
                    try
                    {
                        type = assembly.GetTypes().FirstOrDefault(t =>
                            (t.FullName == clrType || t.Name == clrType) && !t.IsAbstract && !t.IsInterface);
                        if (type != null) return type;
                    }
                    catch (ReflectionTypeLoadException)
                    {
                        continue; // skip assemblies that can't be loaded
                    }
                }

                return null;
            });
    }
}
