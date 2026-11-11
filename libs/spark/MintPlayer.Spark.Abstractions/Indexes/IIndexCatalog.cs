namespace MintPlayer.Spark.Services;

// #551 D20/D21: the catalog's neutral surface. The implementation, which classifies RavenDB index types, is in
// MintPlayer.Spark.RavenDB; the namespace is unchanged so callers keep their usings.

/// <summary>
/// The application's RavenDB indexes, keyed by index name (issue #279).
/// <para>
/// Runtime resolution is declared, never ambient: a Spark query names its index (<c>indexName</c>),
/// the entity's model file names the default binding, and this catalog turns either name into the
/// CLR index and projection types. The only per-type question left is the synchronizer's —
/// "which index is this entity type's default" — answered by <c>[DefaultIndex]</c> via
/// <see cref="GetDefaultFor"/>, and validated at <see cref="Freeze"/> so runtime startup and the
/// offline model commands reject an ambiguous default identically.
/// </para>
/// </summary>
public interface IIndexCatalog
{
    /// <summary>
    /// Adds an index type, deriving its collection type from the <c>AbstractIndexCreationTask&lt;T&gt;</c>
    /// generic argument. Duplicate index names throw: two CLR types would deploy to the same RavenDB
    /// index, silently overwriting each other's definition.
    /// </summary>
    void RegisterIndex(Type indexType);

    /// <summary>Attaches a <c>[FromIndex]</c> projection type to its index's entry.</summary>
    void RegisterProjection(Type projectionType, Type indexType);

    /// <summary>
    /// Seals the catalog and elects each entity type's default index (<c>docs/default_index_PRD.md</c>,
    /// D3/D4): a type claimed by exactly one <c>[DefaultIndex]</c> has that index, with or without a view;
    /// an unclaimed type has its sole mapping index that has a view, if there is exactly one. Two claims,
    /// or several viewed indexes and no claim, are errors naming the candidates. So is a marker the
    /// catalog cannot honour: types on a single-map that are not its mapped type, a multi-map that lists
    /// none or does not derive from <c>SparkMultiMapIndexCreationTask</c>, an index whose collection
    /// Spark cannot read.
    /// </summary>
    void Freeze();

    /// <summary>
    /// Registers every index and <c>[FromIndex]</c> projection declared in <paramref name="assemblies"/>: the indexes of
    /// all of them first, then the projections, because a projection resolves its index by name. Pure reflection, so the
    /// offline paths (model synchronization, the model-hash check) can populate a catalog without a database.
    /// </summary>
    void RegisterAssemblies(IReadOnlyList<System.Reflection.Assembly> assemblies);

    /// <summary>
    /// The fields a viewless default index's maps emit, or <see langword="null"/> when they cannot be read; then nothing
    /// is restricted and the author sets <c>canSort</c>/<c>canFilter</c> by hand.
    /// </summary>
    IReadOnlySet<string>? GetQuerySurface(IndexCatalogEntry entry);

    /// <summary>
    /// Builds <paramref name="entry"/>'s index definition client-side, exactly as the deployment does at startup, and
    /// throws what that build throws (the offline <c>--spark-verify</c> gate). A fresh instance per call: the
    /// declaration dictionaries live on the instance, and a reused one would throw on the second build.
    /// </summary>
    void BuildDefinition(IndexCatalogEntry entry);

    /// <summary>The entry deployed under <paramref name="indexName"/> (case-insensitive), or <c>null</c>.</summary>
    IndexCatalogEntry? GetByIndexName(string indexName);

    /// <summary>
    /// The default index of <paramref name="entityType"/>, or <c>null</c> when it has none. It shapes the
    /// type's model file (through its view, when it has one) and is stamped on the type's minted
    /// <c>Database.*</c> queries. Synchronizer/model-shape concern only — runtime query paths resolve by
    /// name.
    /// </summary>
    IndexCatalogEntry? GetDefaultFor(Type entityType);

    /// <summary>Every entry, unordered.</summary>
    IEnumerable<IndexCatalogEntry> GetAllEntries();
}

/// <summary>One deployed index: its name, CLR type, mapped collection, optional projection, and default claims.</summary>
public sealed class IndexCatalogEntry
{
    public required string IndexName { get; init; }
    public required Type IndexType { get; init; }

    /// <summary>
    /// The mapped collection, or <see langword="null"/> when the index names none — a multi-map (which
    /// maps several and names none in its type arguments) or an otherwise opaque index.
    /// </summary>
    /// <remarks>
    /// ⚠️ Nullable on purpose. While this was <c>required Type</c>, an index with no derivable
    /// collection could not be entered in the catalog at all and was dropped, so an explicit
    /// <c>indexName</c> binding to it could never resolve. It is now registered and reachable by name,
    /// and a multi-map becomes a default only for the types its marker lists (<see cref="ClaimedTypes"/>).
    /// </remarks>
    public required Type? CollectionType { get; init; }
    public Type? ProjectionType { get; internal set; }

    /// <summary>
    /// The entity types this index's <c>[DefaultIndex]</c> marker claims: the mapped type for a bare marker
    /// on a single-map, the listed types for a multi-map, empty without a marker. Resolved and validated at
    /// freeze.
    /// </summary>
    public IReadOnlyList<Type> ClaimedTypes { get; internal set; } = [];

    /// <summary>The entity types this index was elected the default of. Computed at freeze.</summary>
    public IReadOnlyList<Type> DefaultFor { get; internal set; } = [];

    /// <summary>Whether this entry is the default index of at least one entity type. Computed at freeze.</summary>
    public bool IsDefault => DefaultFor.Count > 0;

    /// <summary>
    /// The entity types whose rows this index serves: its mapped collection type, plus every type its marker
    /// claims (a multi-map's). Startup checks that inspect "indexes over type X" iterate this, so a multi-map
    /// default is not skipped for having no single collection.
    /// </summary>
    public IEnumerable<Type> EntityTypes
        => CollectionType is null ? ClaimedTypes : ClaimedTypes.Contains(CollectionType) ? ClaimedTypes : [CollectionType, .. ClaimedTypes];

    /// <summary>
    /// Whether a query for <paramref name="entityType"/> on this index must be narrowed to that type's
    /// collection: the index is a multi-map default of the type, so its entries also hold the other mapped
    /// collections' rows. The narrowing reads the injected <c>SparkCollectionRow.FieldName</c> (MintPlayer.Spark.RavenDB) field.
    /// </summary>
    public bool FiltersByCollection(Type entityType)
        => CollectionType is null && DefaultFor.Contains(entityType);
}
