using System.Reflection;
using MintPlayer.SourceGenerators.Attributes;

namespace MintPlayer.Spark.Services;

/// <summary>
/// The catalog of a store without indexes (#551 D20): it knows none, so every type is queried over its own collection.
/// A provider with the <c>Indexes</c> capability registers its own catalog after core's, and that registration wins.
/// </summary>
/// <remarks>
/// A model that names an index on a provider without them is refused at startup by the capability check (M8), not here:
/// <see cref="RegisterIndex"/> throws only for a caller that hands it an index type directly.
/// </remarks>
[Register(typeof(IIndexCatalog), ServiceLifetime.Singleton)]
internal sealed partial class EmptyIndexCatalog : IIndexCatalog
{
    public void RegisterIndex(Type indexType)
        => throw new NotSupportedException($"This store provider has no indexes, so index {indexType.Name} cannot be registered.");

    public void RegisterProjection(Type projectionType, Type indexType)
        => throw new NotSupportedException($"This store provider has no indexes, so [FromIndex] view {projectionType.Name} cannot be registered.");

    public void RegisterAssemblies(IReadOnlyList<Assembly> assemblies) { }

    public void Freeze() { }

    public IndexCatalogEntry? GetByIndexName(string indexName) => null;

    public IndexCatalogEntry? GetDefaultFor(Type entityType) => null;

    public IEnumerable<IndexCatalogEntry> GetAllEntries() => [];

    public IReadOnlySet<string>? GetQuerySurface(IndexCatalogEntry entry) => null;

    public void BuildDefinition(IndexCatalogEntry entry) { }
}
