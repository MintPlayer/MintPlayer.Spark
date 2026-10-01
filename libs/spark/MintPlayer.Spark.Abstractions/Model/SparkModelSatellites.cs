using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace MintPlayer.Spark.Abstractions.Model;

/// <summary>
/// A document type that belongs to a model entity without being a property of the
/// <c>SparkContext</c> — a generated contribution or current-version document of a target, say —
/// and that synchronization gives a model file of its own (contributions M5b).
/// </summary>
/// <param name="OwnerType">The context entity the satellite belongs to (<c>Song</c>).</param>
/// <param name="ModelType">The satellite document type (<c>SongLyricsContribution</c>).</param>
/// <param name="Query">The query synchronization mints for it when its model file has none of that name; <see langword="null"/> for none.</param>
public sealed record SparkSatelliteModelType(Type OwnerType, Type ModelType, SparkSatelliteQuery? Query = null);

/// <summary>The query minted for a <see cref="SparkSatelliteModelType"/> (once; the model file owns it afterwards).</summary>
/// <param name="Name">The query name (<c>SongLyricsContributions</c>).</param>
/// <param name="Source">The source, <c>Custom.{Method}</c> served by the type's actions.</param>
/// <param name="SortProperty">The default sort column, or <see langword="null"/>.</param>
/// <param name="SortDirection"><c>asc</c> or <c>desc</c>.</param>
public sealed record SparkSatelliteQuery(string Name, string Source, string? SortProperty = null, string SortDirection = "asc");

/// <summary>
/// A renderer synchronization writes on an attribute that has none yet (and never overwrites): how a
/// library marks attributes of a type it does not own for a client renderer.
/// </summary>
/// <param name="DeclaringType">The type whose model file holds the attribute (<c>Lyrics</c>).</param>
/// <param name="AttributeName">The attribute (<c>ContributorName</c>).</param>
/// <param name="Renderer">The renderer name (<c>contributionAttribution</c>).</param>
/// <param name="Options">The renderer options, written as they are (string, string-array or number values).</param>
public sealed record SparkRendererSeed(Type DeclaringType, string AttributeName, string Renderer, IReadOnlyDictionary<string, object>? Options = null);

/// <summary>
/// Satellite model types and renderer seeds, registered by libraries and generated module initializers
/// and read by model synchronization and the model-shape discovery behind the model hash.
/// </summary>
/// <remarks>
/// <para>
/// Keyed by the <b>owner</b> (and the declaring type), never global: a context that does not expose
/// <c>Song</c> sees nothing registered for it, so the model a context type produces stays a function of
/// that type graph. Every read first runs the module initializer of the type asked about, which is
/// where generated registrations live.
/// </para>
/// <para>Idempotent: registering the same satellite or seed twice keeps one.</para>
/// </remarks>
public static class SparkModelSatellites
{
    private static readonly ConcurrentDictionary<Type, ConcurrentDictionary<Type, SparkSatelliteModelType>> Satellites = new();
    private static readonly ConcurrentDictionary<Type, ConcurrentDictionary<string, SparkRendererSeed>> Seeds = new();

    /// <summary>Declares a satellite of <see cref="SparkSatelliteModelType.OwnerType"/>.</summary>
    public static void Register(SparkSatelliteModelType satellite)
    {
        ArgumentNullException.ThrowIfNull(satellite);
        Satellites.GetOrAdd(satellite.OwnerType, static _ => new())[satellite.ModelType] = satellite;
    }

    /// <summary>Declares a renderer for an attribute that has none.</summary>
    public static void SeedRenderer(SparkRendererSeed seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        Seeds.GetOrAdd(seed.DeclaringType, static _ => new(StringComparer.Ordinal))[seed.AttributeName] = seed;
    }

    /// <summary>The satellites of <paramref name="ownerType"/>, ordered by full type name.</summary>
    public static IReadOnlyList<SparkSatelliteModelType> For(Type ownerType)
    {
        ArgumentNullException.ThrowIfNull(ownerType);
        RuntimeHelpers.RunModuleConstructor(ownerType.Module.ModuleHandle);
        return Satellites.TryGetValue(ownerType, out var map)
            ? [.. map.Values.OrderBy(s => s.ModelType.FullName ?? s.ModelType.Name, StringComparer.Ordinal)]
            : [];
    }

    /// <summary>The renderer seeds of <paramref name="declaringType"/>'s attributes, by attribute name.</summary>
    public static IReadOnlyDictionary<string, SparkRendererSeed> RendererSeedsFor(Type declaringType)
    {
        ArgumentNullException.ThrowIfNull(declaringType);
        RuntimeHelpers.RunModuleConstructor(declaringType.Module.ModuleHandle);
        return Seeds.TryGetValue(declaringType, out var map)
            ? new Dictionary<string, SparkRendererSeed>(map, StringComparer.Ordinal)
            : new Dictionary<string, SparkRendererSeed>(StringComparer.Ordinal);
    }
}
