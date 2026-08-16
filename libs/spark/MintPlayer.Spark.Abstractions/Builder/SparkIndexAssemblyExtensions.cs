using System.Reflection;

namespace MintPlayer.Spark.Abstractions.Builder;

/// <summary>
/// Declares which assemblies contribute RavenDB indexes and <c>[FromIndex]</c> projection types.
/// <para>
/// Lives in Abstractions so a module can declare its own indexes without referencing Spark's core
/// package.
/// </para>
/// </summary>
public static class SparkIndexAssemblyExtensions
{
    /// <summary>
    /// Declares an assembly containing indexes and/or projections.
    /// <para>
    /// Call this from a module's own <c>AddXxx(...)</c> so consuming applications need write nothing.
    /// An application can also call it for a shared class library of its own.
    /// </para>
    /// </summary>
    public static ISparkBuilder AddIndexesFrom(this ISparkBuilder builder, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Registry.AddIndexAssembly(assembly);
        return builder;
    }

    /// <summary>Declares several assemblies at once.</summary>
    public static ISparkBuilder AddIndexesFrom(this ISparkBuilder builder, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(assemblies);

        foreach (var assembly in assemblies)
            builder.Registry.AddIndexAssembly(assembly);

        return builder;
    }

    /// <summary>
    /// Declares the assembly containing <typeparamref name="TMarker"/> — usually an index or an
    /// entity from the module, which keeps the declaration honest when the assembly is renamed.
    /// </summary>
    public static ISparkBuilder AddIndexesFromAssemblyContaining<TMarker>(this ISparkBuilder builder)
        => builder.AddIndexesFrom(typeof(TMarker).Assembly);
}
