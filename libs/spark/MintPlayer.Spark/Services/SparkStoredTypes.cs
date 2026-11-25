using System.Reflection;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Services;

/// <summary>
/// The stored types (#536 D36): the app model's entity types that resolve to a class with an <c>Id</c> property, and the
/// types feature libraries add in code. Read once, on first use: a store with a schema builds its tables from it.
/// </summary>
[Register(typeof(ISparkStoredTypes), ServiceLifetime.Singleton)]
internal sealed partial class SparkStoredTypes : ISparkStoredTypes
{
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly ISparkTypeResolver typeResolver;
    [Inject] private readonly IOptions<SparkStoredTypeOptions> options;

    private IReadOnlyCollection<Type>? types;

    public IReadOnlyCollection<Type> Types => types ??= Collect();

    private List<Type> Collect()
    {
        var fromModel = modelLoader.GetEntityTypes()
            .Select(definition => typeResolver.Resolve(definition.ClrType))
            .OfType<Type>()
            // A [ValueObject] has a generated row Id but no document of its own: it lives inside its owner.
            .Where(t => t.IsClass && !t.IsAbstract && t.GetCustomAttribute<global::MintPlayer.Spark.Abstractions.ValueObjectAttribute>() is null
                && t.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance) is { CanWrite: true });
        return [.. fromModel.Concat(options.Value.Types).Distinct()];
    }
}
