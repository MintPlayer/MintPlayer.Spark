using Microsoft.Extensions.DependencyInjection;

namespace MintPlayer.Spark.Abstractions.Storage;

/// <summary>
/// The entity types the application stores (#536 D36). A schemaless store (RavenDB, InMemory) never asks; a store with a
/// schema (the relational providers) builds its tables from this list before it opens its first session.
/// </summary>
/// <remarks>
/// Core's implementation lists the Spark model's entity types that resolve to a class with an <c>Id</c>, plus every type
/// added through <see cref="SparkStoredTypeServiceCollectionExtensions.AddSparkStoredType{T}"/>: a feature library adds
/// the documents it keeps for itself (outboxes, settings, keys), which are not in the app's model.
/// </remarks>
public interface ISparkStoredTypes
{
    /// <summary>The stored types, each once. A type and a stored base type of it share the base type's table.</summary>
    IReadOnlyCollection<Type> Types { get; }
}

/// <summary>The types added in code, beside the app model's (<see cref="ISparkStoredTypes"/>).</summary>
public sealed class SparkStoredTypeOptions
{
    public HashSet<Type> Types { get; } = [];
}

public static class SparkStoredTypeServiceCollectionExtensions
{
    /// <summary>Declares that the application stores <typeparamref name="T"/>, though the app model doesn't list it.</summary>
    public static IServiceCollection AddSparkStoredType<T>(this IServiceCollection services) where T : class
        => services.Configure<SparkStoredTypeOptions>(options => options.Types.Add(typeof(T)));
}
