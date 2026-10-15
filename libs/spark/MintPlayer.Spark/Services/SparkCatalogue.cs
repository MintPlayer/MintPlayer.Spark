using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Services;

/// <summary>
/// The entity types and queries one caller may see, shaped for that caller: the checks
/// <c>/spark/types</c> and <c>/spark/queries</c> used to make inline, in one place both the HTTP
/// endpoints and the MCP tools call (MCP PRD D9).
/// </summary>
/// <remarks>
/// A refusal and an unknown name are the same <see langword="null"/>, so no caller can turn one of
/// these into an existence oracle by mapping them to different answers.
/// </remarks>
internal interface ISparkCatalogue
{
    /// <summary>The types the caller may query, each pruned, with detail types embedded and per-caller attribute rights.</summary>
    Task<IReadOnlyList<EntityTypeDefinition>> ListEntityTypesAsync(CancellationToken cancellationToken = default);

    /// <summary>One type shaped for the form <paramref name="formVerb"/>; <see langword="null"/> when unknown or not queryable.</summary>
    Task<EntityTypeDefinition?> GetEntityTypeAsync(string id, string formVerb, CancellationToken cancellationToken = default);

    /// <summary>The queries whose entity type the caller may query, as query metadata.</summary>
    Task<IReadOnlyList<SparkQuery>> ListQueriesAsync(CancellationToken cancellationToken = default);

    /// <summary>One query's metadata; <see langword="null"/> when unknown, untyped or not queryable.</summary>
    Task<SparkQuery?> GetQueryAsync(string id, CancellationToken cancellationToken = default);
}

[Register(typeof(ISparkCatalogue), ServiceLifetime.Scoped)]
internal sealed partial class SparkCatalogue : ISparkCatalogue
{
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IQueryLoader queryLoader;
    [Inject] private readonly IAttributeRightsEnforcement attributeRights;
    [Inject] private readonly ILogger<SparkCatalogue> logger;

    public async Task<IReadOnlyList<EntityTypeDefinition>> ListEntityTypesAsync(CancellationToken cancellationToken = default)
    {
        var entityTypes = modelLoader.GetEntityTypes();
        var visible = new List<EntityTypeDefinition>(entityTypes.Count());
        foreach (var entityType in entityTypes)
        {
            // The catalogue is list-scoped: Query gates it, deliberately NOT Read. A type the
            // caller may only Read (a virtual start page, a Read-without-Query grant) is absent
            // here and resolved individually via GET /spark/types/{id}, which Read unlocks —
            // single-object metadata belongs to the single-object right.
            if (!await permissionService.IsAllowedAsync("Query", entityType.Name, cancellationToken))
                continue;

            // This is the load-bearing one for sub-query pruning: spark-po-detail reads the array
            // from here and never calls getEntityType(id).
            var pruned = await SubQueryPruner.PruneAsync(
                entityType, queryLoader, permissionService, logger, cancellationToken);

            // Row-type definitions, so an AsDetail table can render its columns even when the row
            // type is absent from this catalogue — which is the common case, since a row edited
            // through its parent rarely has a Query grant of its own (#385).
            pruned = SubQueryPruner.EmbedDetailTypes(pruned, modelLoader);

            // Per-caller attribute rights (M2c-2a), as in GetEntityTypeAsync. Returns the same reference
            // when it changes nothing, which the copy below still accounts for.
            pruned = await attributeRights.ForFormAsync(pruned, cancellationToken: cancellationToken);

            // ⚠️ Copy if neither step did. Both return the SAME reference when they change nothing
            // — that is their documented contract, and the reason they are safe — so the comment
            // that used to sit here ("PruneAsync already copied the definition") was false for
            // every type with no sub-queries. Writing the per-caller CanRead onto that reference
            // mutated ModelLoader's singleton graph process-wide, so two concurrent callers with
            // different rights could each serialize the other's answer.
            if (ReferenceEquals(pruned, entityType))
                pruned = entityType.ShallowCopy();

            pruned.CanRead = await permissionService.IsAllowedAsync("Read", entityType.Name, cancellationToken);
            pruned.IsValueObject = IsValueObject(entityType);

            visible.Add(pruned);
        }
        return visible;
    }

    public async Task<EntityTypeDefinition?> GetEntityTypeAsync(string id, string formVerb, CancellationToken cancellationToken = default)
    {
        var entityType = modelLoader.ResolveEntityType(id);
        if (entityType is null)
            return null;

        // Null rather than a refusal — so existence isn't leaked to callers without Query rights.
        // (A Read grant passes too: Read implies Query at rights-expansion time, #324.)
        if (!await permissionService.IsAllowedAsync("Query", entityType.Name, cancellationToken))
            return null;

        var pruned = await SubQueryPruner.PruneAsync(
            entityType, queryLoader, permissionService, logger, cancellationToken);

        // Same reason as the list: the row types an AsDetail attribute names are usually absent from
        // the catalogue, so the client cannot resolve their columns from it (#385).
        // Per caller (contributions M2c-2a): Read-denied attributes absent, Edit-denied read-only, on
        // the type and on each embedded detail type by its own rights. Always a copy when it changes
        // anything — the loader's definitions are shared process-wide.
        // A `new` form is shaped for the create form instead (#264, G1/G2): New-denied attributes absent
        // too, and no Edit deny applied. It only ever narrows what is seen: Read-denied stays absent.
        var shaped = await attributeRights.ForFormAsync(
            SubQueryPruner.EmbedDetailTypes(pruned, modelLoader), formVerb, cancellationToken);

        // As in the list. Copy first when nothing above did: the loader's definition is shared.
        var isValueObject = IsValueObject(entityType);
        if (shaped.IsValueObject == isValueObject)
            return shaped;
        if (ReferenceEquals(shaped, entityType))
            shaped = entityType.ShallowCopy();
        shaped.IsValueObject = isValueObject;
        return shaped;
    }

    /// <summary>
    /// <see langword="true"/> when the type's CLR type carries <c>[ValueObject]</c> — stored embedded in
    /// its owner, so it has no page of its own; otherwise <see langword="null"/>, which keeps the
    /// property off the wire. The marker, not a heuristic: "has no <c>Database.*</c> query" would also
    /// catch real documents that are only listed through custom queries (the identity package's
    /// <c>SparkUser</c>, for one), and their references would lose a working link.
    /// </summary>
    private static bool? IsValueObject(EntityTypeDefinition entityType)
        => SparkTypeResolver.ResolveClrType(entityType.ClrType)?.IsDefined(typeof(ValueObjectAttribute), inherit: false) == true
            ? true
            : null;

    public async Task<IReadOnlyList<SparkQuery>> ListQueriesAsync(CancellationToken cancellationToken = default)
    {
        var queries = queryLoader.GetQueries();
        var visible = new List<SparkQuery>(queries.Count());
        foreach (var query in queries)
        {
            // Queries without a target entity type cannot be permission-checked and are
            // conservatively hidden. When EntityType is set, the caller needs at least
            // Query rights on that entity to see the query in the list.
            if (query.EntityType is null)
                continue;
            if (await permissionService.IsAllowedAsync("Query", query.EntityType, cancellationToken))
                visible.Add(await attributeRights.ForQueryMetadataAsync(query, cancellationToken));
        }
        return visible;
    }

    public async Task<SparkQuery?> GetQueryAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(id))
            return null;

        var query = queryLoader.ResolveQuery(id);

        // Null for an unauthorized caller too (the endpoint's 404, not 403) — so existence isn't leaked.
        if (query?.EntityType is null ||
            !await permissionService.IsAllowedAsync("Query", query.EntityType, cancellationToken))
        {
            return null;
        }

        // Sort columns, column overrides and the parent reference name attributes of the row type; one
        // the caller may not query is left out, so the metadata does not say it exists (S7).
        return await attributeRights.ForQueryMetadataAsync(query, cancellationToken);
    }
}
