using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.SourceGenerators.Attributes;

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
    public Type? Resolve(string? clrType)
    {
        if (clrType is null) return null;

        // Cached because the miss path walks every loaded assembly, and a miss is the common
        // case for the first lookup of each type in an app with many assemblies.
        return ReflectionCache.GetOrAdd<Type?>(
            $"resolveType|{clrType}",
            () =>
            {
                var type = Type.GetType(clrType);
                if (type != null) return type;

                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = assembly.GetType(clrType);
                    if (type != null) return type;
                }

                return null;
            });
    }
}
