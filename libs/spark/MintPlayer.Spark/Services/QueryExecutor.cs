using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using System.Linq.Expressions;
using System.Reflection;

namespace MintPlayer.Spark.Services;

public interface IQueryExecutor
{
    Task<QueryResult> ExecuteQueryAsync(SparkQuery query, PersistentObject? parent = null, int skip = 0, int take = 50, string? search = null);
}

[Register(typeof(IQueryExecutor), ServiceLifetime.Scoped)]
internal partial class QueryExecutor : IQueryExecutor
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IEntityMapper entityMapper;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly ISparkContextResolver sparkContextResolver;
    [Inject] private readonly IIndexCatalog indexCatalog;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IActionsResolver actionsResolver;
    [Inject] private readonly IReferenceResolver referenceResolver;
    [Inject] private readonly Breadcrumb.IBreadcrumbResolver breadcrumbResolver;
    [Inject] private readonly IRowSecurity rowSecurity;

    public async Task<QueryResult> ExecuteQueryAsync(SparkQuery query, PersistentObject? parent = null, int skip = 0, int take = 50, string? search = null)
    {
        var (isCustom, name) = ResolveSource(query);

        // Null/whitespace collapses to null here, so every path below tests one thing.
        var searchTerm = BuildSearchTerm(search);

        IEnumerable<PersistentObject> allResults;
        bool searchPushedDown;
        EntityTypeDefinition? definition;
        if (isCustom)
        {
            (allResults, searchPushedDown, definition) = await ExecuteCustomQueryAsync(query, name, parent, searchTerm);
        }
        else
        {
            (allResults, searchPushedDown, definition) = await ExecuteDatabaseQueryAsync(query, name, searchTerm);
        }

        // Fallback for shapes that cannot push down: a Custom. query returning a non-Raven
        // IQueryable, or a type with no searchable field. Also the only path that still matches
        // Breadcrumb — resolved reference display text, which exists only after mapping and is
        // therefore not an index term. See the query guide.
        if (searchTerm != null && !searchPushedDown)
        {
            var term = search!.ToLowerInvariant();
            allResults = allResults.Where(po =>
                (po.Name != null && po.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                (po.Breadcrumb != null && po.Breadcrumb.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                po.Attributes.Any(attr =>
                {
                    var value = attr.Breadcrumb ?? attr.Value?.ToString();
                    return value != null && value.Contains(term, StringComparison.OrdinalIgnoreCase);
                })
            ).ToList();
        }

        // Counted after filtering and before paging, either way — which is what keeps
        // TotalItems search-aware now that the filter may have run in the database.
        var materialized = allResults as IList<PersistentObject> ?? allResults.ToList();
        var totalItems = materialized.Count;

        var paged = materialized.Skip(skip).Take(take);

        // Columns ship once per result, not once per row. A definition-less result cannot describe
        // its own columns, and the client renders from them, so an empty column set is the honest
        // answer rather than a guess reconstructed from whichever attributes the first row happens
        // to carry.
        var columns = definition is not null
            ? QueryResultProjector.BuildColumns(definition)
            : [];

        return new QueryResult
        {
            Columns = columns,
            Items = QueryResultProjector.ToItems(paged, columns, query.Name),
            TotalItems = totalItems,
            Skip = skip,
            Take = take,
        };
    }

    private static (bool IsCustom, string Name) ResolveSource(SparkQuery query)
    {
        var source = query.Source;

        if (source.StartsWith("Custom.", StringComparison.OrdinalIgnoreCase))
            return (true, source[7..]);

        if (source.StartsWith("Database.", StringComparison.OrdinalIgnoreCase))
            return (false, source[9..]);

        throw new InvalidOperationException(
            $"Query '{query.Name}' has invalid Source '{query.Source}'. " +
            "Expected 'Database.PropertyName' or 'Custom.MethodName'.");
    }

    #region Database Queries

    private async Task<(IEnumerable<PersistentObject> Results, bool SearchPushedDown, EntityTypeDefinition? Definition)> ExecuteDatabaseQueryAsync(
        SparkQuery query, string propertyName, string? searchTerm)
    {
        var sparkContext = sparkContextResolver.ResolveContext(session);
        if (sparkContext == null)
        {
            return ([], false, null);
        }

        var contextType = sparkContext.GetType();
        var property = contextType.GetCachedProperty(propertyName);

        if (property == null || !property.CanRead)
        {
            return ([], false, null);
        }

        var queryable = AccessorCache.GetGetter(property)(sparkContext);
        if (queryable == null)
        {
            return ([], false, null);
        }

        var queryableType = property.PropertyType;
        var entityType = queryableType.GetGenericArguments().FirstOrDefault();
        if (entityType == null)
        {
            return ([], false, null);
        }

        var entityTypeDefinition = modelLoader.GetEntityTypeByClrType(entityType.FullName ?? entityType.Name);
        if (entityTypeDefinition == null)
        {
            return ([], false, null);
        }

        await permissionService.EnsureAuthorizedAsync("Query", entityTypeDefinition.Name);

        Type resultType = entityType;

        // Declared-only resolution (issue #279): the query names its index; a query without one
        // falls back to the entity file's model-declared default binding; an empty binding queries
        // the raw collection. Nothing resolves by collection type — a declared name is
        // authoritative, and an unknown one is an error rather than a silent null-field grid.
        var indexName = !string.IsNullOrEmpty(query.IndexName)
            ? query.IndexName
            : entityTypeDefinition.IndexName;

        Type? indexType = null;
        if (!string.IsNullOrEmpty(indexName))
        {
            var entry = indexCatalog.GetByIndexName(indexName)
                ?? throw new InvalidOperationException(
                    $"Query '{query.Name}' resolves to index '{indexName}', but no deployed index has that " +
                    $"name. Fix the query's indexName in the model, or register the assembly declaring the " +
                    $"index via AddIndexesFrom(...).");

            indexType = entry.IndexType;
            if (entry.ProjectionType != null)
            {
                resultType = entry.ProjectionType;
            }

            // Re-root rather than replace: a context property may have composed a predicate onto its
            // query (a user-scoped grid), and building the index query from scratch silently dropped
            // it. A bare property short-circuits to exactly the query built here.
            queryable = RerootOntoIndexQuery(queryable, ApplyIndexWithType(session, entityType, indexType));
            if (resultType != entityType)
            {
                queryable = ApplyProjection(queryable, resultType);
            }
        }

        // Chain .Include() before executing: [Reference] property names + the type's
        // GetDefaultIncludes() paths (#239), so referenced docs arrive in the same round-trip.
        var includePaths = referenceResolver.ResolveIncludePaths(resultType, entityType);
        if (includePaths.Count > 0)
        {
            queryable = referenceResolver.ApplyIncludes(queryable, resultType, includePaths);
        }

        // Push the row filter into the Raven query where shapes allow (no projection in play);
        // otherwise this no-ops and FilterAsync below stays the gate. Composing before
        // materialization is what keeps a row-scoped type from reading its whole collection.
        queryable = await rowSecurity.ComposeRowFilterAsync(queryable, entityType, resultType, "Query");

        var sortType = (indexType != null && resultType != entityType) ? resultType : entityType;

        // After the row filter and before sorting. The position matters for one reason: RavenDB
        // groups consecutive Search clauses and ANDs that group with its neighbours, so keeping the
        // security predicate ahead of the search group yields `(predicate) and (search or search)`.
        // Reversing them, or passing SearchOptions explicitly, ORs the predicate in instead.
        var searchPushedDown = false;
        if (searchTerm != null)
        {
            (queryable, searchPushedDown) = ApplySearch(queryable, sortType, searchTerm);
        }

        if (query.SortColumns.Length > 0)
        {
            queryable = ApplySorting(queryable, sortType, query.SortColumns, entityTypeDefinition);
        }

        var materialized = (await ExecuteQueryableAsync(queryable, resultType)).ToList();

        // Row-level authorization. The type-level check above answers "may this principal query
        // this type at all"; it says nothing about which rows. Without this, an entity whose
        // Actions class scopes rows to their owner was filtered correctly when opened and listed
        // in full here — and the list screen is the one that shows every row at once.
        var entities = (await rowSecurity.FilterAsync(
            session, materialized, entityType, resultType, "Query")).ToList();

        // Referenced docs were primed into the session cache by .Include() above; the resolver's
        // first batched load is a cache hit, deeper breadcrumb levels cost one request each.
        var breadcrumbs = await breadcrumbResolver.ResolveAsync(session, entities, entityTypeDefinition);

        var mapped = entities
            .Select(e => (Po: entityMapper.ToPersistentObject(e, entityTypeDefinition.Id, breadcrumbs), Row: e))
            .ToList();
        await rowSecurity.RedactAsync(session, mapped, entityType, resultType, "Query");

        // ⚠️ DO NOT REMOVE THIS DistinctBy. It is not defensive, and it is not about the analyzer.
        //
        // WHY IT IS HERE: this path queries a RavenDB *index*, and a fan-out index emits one entry
        // per element of a collection the map projects over. Given
        //
        //     from car in cars from tag in car.Tags select new { car.Id, tag }
        //
        // a car with three tags produces THREE index entries, all pointing at the same document.
        // The query returns three results, they map to three PersistentObjects with the same Id,
        // and the grid shows the same row three times with a TotalRecords to match. Deduping by Id
        // is what makes one document one row.
        //
        // WHY IT LOOKS UNNECESSARY: nothing here says "fan-out" — whether the bound index fans out
        // is a property of the index definition, which lives in the consuming application, so no
        // amount of reading this file reveals a duplicate-producing case. The repo's own docs once
        // attributed this call to the search analyzer, which was wrong and was corrected in place
        // (issue_210_PRD.md); the guard is still correct, it just guards a different hazard than
        // that note claimed. If you are here because it "seems redundant", it is not: write a
        // fan-out index over a collection property and watch the row count multiply.
        //
        // WHY IT IS NOT ON THE CUSTOM PATH: see the sibling comment at the end of
        // ExecuteCustomQueryAsync. In memory there is no fan-out, and DistinctBy is destructive
        // there — it treats every null Id as equal and collapses the grid to a single row.
        return (mapped.Select(m => m.Po).DistinctBy(po => po.Id), searchPushedDown, entityTypeDefinition);
    }

    #endregion

    #region Custom Queries

    private async Task<(IEnumerable<PersistentObject> Results, bool SearchPushedDown, EntityTypeDefinition? Definition)> ExecuteCustomQueryAsync(
        SparkQuery query, string methodName, PersistentObject? parent, string? searchTerm)
    {
        // Resolve the entity type for this query
        var entityTypeDefinition = ResolveEntityTypeDefinition(query, methodName);
        if (entityTypeDefinition == null)
        {
            return ([], false, null);
        }

        await permissionService.EnsureAuthorizedAsync("Query", entityTypeDefinition.Name);

        // Resolve the entity CLR type
        var entityType = SparkTypeResolver.ResolveClrType(entityTypeDefinition.ClrType);
        if (entityType == null)
        {
            return ([], false, null);
        }

        // Resolve the Actions class for this entity type
        var actionsInstance = actionsResolver.ResolveForType(entityType);

        // Find the custom query method
        var methodInfo = ResolveCustomQueryMethod(actionsInstance.GetType(), methodName);
        if (methodInfo == null)
        {
            throw new InvalidOperationException(DescribeUnusableCustomQuery(actionsInstance.GetType(), methodName));
        }

        // Build args and invoke
        var parentTypeName = parent != null
            ? modelLoader.GetEntityType(parent.ObjectTypeId)?.Name
            : null;
        var args = new CustomQueryArgs
        {
            Parent = parent,
            ParentType = parentTypeName,
            Query = query,
        };

        object? result;
        if (methodInfo.AcceptsArgs)
        {
            result = methodInfo.Method.Invoke(actionsInstance, [args]);
        }
        else
        {
            result = methodInfo.Method.Invoke(actionsInstance, []);
        }

        // Await async methods (Task<IEnumerable<T>>, Task<IQueryable<T>>, etc.)
        if (methodInfo.IsAsync && result is Task task)
        {
            await task;
            result = task.GetCompletedTaskResult();
        }

        if (result == null)
        {
            return ([], false, null);
        }

        // Capabilities come from the object, not from the signature (#294). A method declared
        // IQueryable<T> whose body is session.Query<T>() returns a Raven queryable, and asking the
        // declared type would deny it projection, includes and search pushdown for no reason. Asking
        // the object cannot over-claim: it either is a Raven queryable or it is not.
        //
        // This is also why the two must be computed here rather than cached alongside the MethodInfo:
        // the same method can only be resolved once, but what it returns is a per-invocation fact.
        var isRavenQueryable = typeof(IRavenQueryable<>)
            .MakeGenericType(methodInfo.ResultElementType)
            .IsInstanceOfType(result);
        var isQueryable = result is IQueryable;

        // Apply index projection for computed/stored fields (e.g., FullName from People_Overview).
        // Without this, RavenDB loads full documents which lack computed index fields.
        if (isRavenQueryable && methodInfo.ResultElementType.IsSparkProjection())
        {
            result = ApplyProjection(result, methodInfo.ResultElementType);
        }

        // Chain .Include() on the custom query too (#239) — custom queries applied no includes
        // before. Only for RavenDB-backed queryables (an in-memory IQueryable has no .Include).
        if (isRavenQueryable)
        {
            var includePaths = referenceResolver.ResolveIncludePaths(methodInfo.ResultElementType, entityType);
            if (includePaths.Count > 0)
            {
                result = referenceResolver.ApplyIncludes(result, methodInfo.ResultElementType, includePaths);
            }
        }

        // Push the row filter into the custom query too — a custom query says where rows come
        // from, not which of them this caller may see. No-op when the method yields projections.
        if (isQueryable)
        {
            result = await rowSecurity.ComposeRowFilterAsync(result, entityType, methodInfo.ResultElementType, "Query");
        }

        // Only a RavenDB-backed queryable can push the search into the database; an in-memory
        // IQueryable has no Search, and the caller's own filtering may already have materialized.
        var searchPushedDown = false;
        if (searchTerm != null && isRavenQueryable)
        {
            (result, searchPushedDown) = ApplySearch(result, methodInfo.ResultElementType, searchTerm);
        }

        // Apply sorting if the result is IQueryable
        if (isQueryable && query.SortColumns.Length > 0)
        {
            result = ApplySorting(result, methodInfo.ResultElementType, query.SortColumns, entityTypeDefinition);
        }

        // Materialize results
        IEnumerable<object> entities;
        if (isRavenQueryable)
        {
            // Raven-backed: enumerate asynchronously. Reaching MaterializeQueryable with one of these
            // is what made Task<IRavenQueryable<T>> throw before #294 — a blocking ToList() over an
            // async session, which RavenDB rejects. Both branches now ask the object the same
            // question, so they can no longer disagree.
            entities = await ExecuteQueryableAsync(result, methodInfo.ResultElementType);
        }
        else if (isQueryable)
        {
            // In-memory IQueryable — materialize via LINQ ToList
            entities = MaterializeQueryable(result, methodInfo.ResultElementType);
        }
        else if (result is System.Collections.IEnumerable enumerable)
        {
            entities = enumerable.Cast<object>().ToList();
        }
        else
        {
            return ([], false, null);
        }

        // Row-level authorization, as on the database path. A custom query is a developer saying
        // *where* rows come from; it is not permission to skip *whether* the caller may see them.
        var entityList = await rowSecurity.FilterAsync(
            session,
            entities as IReadOnlyList<object> ?? entities.ToList(),
            entityType,
            methodInfo.ResultElementType,
            "Query");

        // Resolve breadcrumbs (recursive, batched) for the custom query's results.
        var breadcrumbs = await breadcrumbResolver.ResolveAsync(session, entityList, entityTypeDefinition);

        var mapped = entityList
            .Select(e => (Po: entityMapper.ToPersistentObject(e, entityTypeDefinition.Id, breadcrumbs), Row: e))
            .ToList();
        await rowSecurity.RedactAsync(session, mapped, entityType, methodInfo.ResultElementType, "Query");

        // ⚠️ DO NOT REMOVE THIS DistinctBy, and do not make it unconditional. Both halves matter.
        //
        // WHY IT IS HERE (the isRavenQueryable case): a custom query may hand back a Raven queryable
        // over a *fan-out index* — one that projects over a collection and therefore emits one entry
        // per element. Three tags on one car means three index entries naming the same document,
        // which map to three PersistentObjects with the same Id and render as the same row three
        // times. Deduping by Id makes one document one row. Whether the bound index fans out is a
        // property of the index definition in the consuming application, so nothing in this file
        // will ever look like it needs this — that is exactly why it is spelled out here. (The
        // repo's docs once attributed this to the search analyzer; that was wrong and was corrected
        // in issue_210_PRD.md. The guard is right, the old explanation was not.)
        //
        // WHY IT IS CONDITIONAL: this single return is shared by all three custom shapes — Raven
        // queryable, in-memory IQueryable, and plain IEnumerable. Off the index there is no fan-out,
        // and DistinctBy is actively DESTRUCTIVE there: Enumerable.DistinctBy uses the default
        // comparer, which treats every null key as equal, so a computed row type with no readable
        // Id collapses the entire grid to one row — silently, with a matching TotalRecords. That
        // was S1 in #327. A duplicate id on a composed path is an authoring bug and will be made to
        // throw (M5); it is never something to quietly collapse.
        IEnumerable<PersistentObject> rows = mapped.Select(m => m.Po);
        if (isRavenQueryable)
            rows = rows.DistinctBy(po => po.Id);

        // Sorting has to happen somewhere. ApplySorting above runs only when the result is IQueryable,
        // so a method returning a plain IEnumerable silently ignored both the query's declared sort
        // columns and the caller's ?sortColumns= override. Sort the mapped rows instead — and do it
        // AFTER redaction, because ordering by a value the caller may not read is the same comparison
        // oracle ApplySorting's ShowedOn gate exists to close, and by now a protected value is null.
        if (!isQueryable && query.SortColumns.Length > 0)
            rows = SortMappedRows(rows, query.SortColumns, entityTypeDefinition);

        return (rows.ToList(), searchPushedDown, entityTypeDefinition);
    }

    /// <summary>
    /// Orders already-mapped rows by their attribute values, for a result that never was an
    /// <see cref="IQueryable"/> and so could not be ordered by a provider.
    /// </summary>
    /// <remarks>
    /// The comparer is pinned rather than inherited from the machine: ordinal case-insensitive for
    /// strings, nulls after values. This does NOT match RavenDB's index-term ordering, and the
    /// divergence is deliberate — an in-memory result has no index terms to order by, and a
    /// culture-sensitive default would sort differently per machine. Documented in the query guide.
    /// <para>
    /// The same <c>IsSortableAttribute</c> gate as <c>ApplySorting</c> applies, for the same reason:
    /// a sort column is a comparison oracle over a value the caller may never read.
    /// </para>
    /// </remarks>
    private static IEnumerable<PersistentObject> SortMappedRows(
        IEnumerable<PersistentObject> rows, SortColumn[] sortColumns, EntityTypeDefinition definition)
    {
        IOrderedEnumerable<PersistentObject>? ordered = null;

        foreach (var col in sortColumns)
        {
            if (!IsSortableAttribute(definition, col.Property))
            {
                Console.WriteLine(
                    $"Warning: sort column '{col.Property}' is not an attribute of {definition.Name}'s query " +
                    $"surface; the column is refused and rows keep their index order.");
                continue;
            }

            var property = col.Property;
            var descending = string.Equals(col.Direction, "desc", StringComparison.OrdinalIgnoreCase);

            ordered = ordered is null
                ? descending
                    ? rows.OrderByDescending(po => SortKeyFor(po, property), RowSortComparer.Instance)
                    : rows.OrderBy(po => SortKeyFor(po, property), RowSortComparer.Instance)
                : descending
                    ? ordered.ThenByDescending(po => SortKeyFor(po, property), RowSortComparer.Instance)
                    : ordered.ThenBy(po => SortKeyFor(po, property), RowSortComparer.Instance);
        }

        return ordered ?? rows;
    }

    private static object? SortKeyFor(PersistentObject po, string attributeName)
        => po.Attributes
            .FirstOrDefault(a => string.Equals(a.Name, attributeName, StringComparison.OrdinalIgnoreCase))
            ?.Value;

    private EntityTypeDefinition? ResolveEntityTypeDefinition(SparkQuery query, string methodName)
    {
        // If EntityType is explicitly set, use it
        if (!string.IsNullOrEmpty(query.EntityType))
        {
            return modelLoader.GetEntityTypeByName(query.EntityType);
        }

        // Otherwise, we need to infer from the method return type — but we need the Actions class first.
        // For now, return null if not set (EntityType should be set for Custom queries).
        return null;
    }

    /// <summary>
    /// Explains why a custom query could not be bound, distinguishing a genuinely missing method from
    /// one that exists but whose shape the executor cannot use.
    /// </summary>
    /// <remarks>
    /// Worth the extra reflection because the two failures look identical to a caller but need
    /// opposite fixes. Before #294 a method returning <c>ValueTask&lt;IQueryable&lt;T&gt;&gt;</c> was
    /// reported as "not found" — sending the author looking for a typo in a name that was correct.
    /// </remarks>
    private static string DescribeUnusableCustomQuery(Type actionsType, string methodName)
    {
        const string expected =
            "Expected a public method returning IQueryable<T>, IRavenQueryable<T>, IEnumerable<T>, " +
            "or a Task<> of one of those, with zero parameters or one CustomQueryArgs parameter.";

        var method = actionsType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
        if (method == null)
        {
            return $"Custom query method '{methodName}' not found on actions class '{actionsType.Name}'. {expected}";
        }

        var parameters = method.GetParameters();
        var signature = $"{method.ReturnType} {methodName}(" +
            string.Join(", ", parameters.Select(p => p.ParameterType.Name)) + ")";

        if (parameters.Length > 1 || (parameters.Length == 1 && parameters[0].ParameterType != typeof(CustomQueryArgs)))
        {
            return $"Custom query method '{methodName}' on actions class '{actionsType.Name}' takes parameters " +
                   $"the executor cannot supply: {signature}. {expected}";
        }

        // A usable shape carrying an unusable ROW type needs the opposite fix from a wrong shape, and
        // saying "returns a shape the executor cannot use" about IEnumerable<PersistentObject> sends the
        // author to rewrite a signature that was already right.
        var returnType = method.ReturnType;
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            returnType = returnType.GetGenericArguments()[0];

        var elementType = ExtractQueryableElementType(returnType);
        if (elementType is not null && IsUnusableRowType(elementType))
        {
            var why = elementType == typeof(PersistentObject)
                ? "a PersistentObject is mapped AS an entity — every declared attribute is looked up as a CLR " +
                  "property, none is found, and the grid renders the right number of rows with every cell blank"
                : "an object/dynamic row has nothing to reflect, so every cell renders blank";

            return $"Custom query method '{methodName}' on actions class '{actionsType.Name}' returns rows of type " +
                   $"'{elementType.Name}', which cannot be mapped: {why}. Return a sequence of a concrete row type " +
                   $"whose property names match the attributes declared on the query's entity type — an anonymous " +
                   $"type, a record or an ad-hoc class all work.";
        }

        return $"Custom query method '{methodName}' on actions class '{actionsType.Name}' returns a shape the " +
               $"executor cannot use: {signature}. Note that ValueTask is not supported — use Task. {expected}";
    }

    /// <summary>
    /// Resolves the custom query method info from the given actions type and method name, with caching for performance.
    /// </summary>
    /// <param name="actionsType"></param>
    /// <param name="methodName"></param>
    /// <returns></returns>
    private static CustomQueryMethodInfo? ResolveCustomQueryMethod(Type actionsType, string methodName)
    {
        return ReflectionCache.GetOrAdd<(string Op, Type Type, string Method), CustomQueryMethodInfo?>(
            ("QueryExecutor.CustomQueryMethod", actionsType, methodName),
            static k =>
        {
            var method = k.Type.GetMethod(k.Method, BindingFlags.Public | BindingFlags.Instance);
            if (method == null)
                return null;

            var returnType = method.ReturnType;
            var parameters = method.GetParameters();

            // Validate parameter: zero params or one CustomQueryArgs param
            bool acceptsArgs;
            if (parameters.Length == 0)
            {
                acceptsArgs = false;
            }
            else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(CustomQueryArgs))
            {
                acceptsArgs = true;
            }
            else
            {
                return null; // Invalid signature
            }

            // Unwrap Task<T> for async methods
            var isAsync = false;
            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                isAsync = true;
                returnType = returnType.GetGenericArguments()[0];
            }

            // Extract the element type from IQueryable<T> or IRavenQueryable<T>
            var elementType = ExtractQueryableElementType(returnType);
            if (elementType == null || IsUnusableRowType(elementType))
                return null;

            return new CustomQueryMethodInfo
            {
                Method = method,
                AcceptsArgs = acceptsArgs,
                ResultElementType = elementType,
                IsAsync = isAsync,
            };
        });
    }

    private static Type? ExtractQueryableElementType(Type type)
    {
        return ReflectionCache.GetOrAdd<(string Op, Type Type), Type?>(
            ("QueryExecutor.QueryableElement", type),
            static k =>
            {
                var t = k.Type;
                // Check if the type itself is IQueryable<T>
                if (t.IsGenericType)
                {
                    var genericDef = t.GetGenericTypeDefinition();
                    if (genericDef == typeof(IQueryable<>) || genericDef == typeof(IEnumerable<>))
                        return t.GetGenericArguments()[0];
                }

                // Check implemented interfaces for IQueryable<T>
                foreach (var iface in t.GetInterfaces())
                {
                    if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IQueryable<>))
                        return iface.GetGenericArguments()[0];
                }

                // Check for IEnumerable<T> as fallback
                foreach (var iface in t.GetInterfaces())
                {
                    if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                        return iface.GetGenericArguments()[0];
                }

                return null;
            });
    }

    /// <summary>
    /// Whether an element type names rows the mapper cannot populate, and which are therefore refused
    /// rather than mapped into a grid of blanks.
    /// </summary>
    /// <remarks>
    /// Both cases used to produce the same silent wrong answer: the right number of rows, every cell
    /// empty, no error and no log.
    /// <list type="bullet">
    /// <item><description><c>PersistentObject</c> — the mapper treats each row AS an entity, reflecting a
    /// CLR property per declared attribute and finding none, so it skips them all.</description></item>
    /// <item><description><c>object</c>/<c>dynamic</c> — nothing to reflect at all. The old guard lived only
    /// in the interface-scan branch, so a method DECLARED <c>IEnumerable&lt;object&gt;</c> matched the
    /// generic-definition branch first and slipped past it entirely.</description></item>
    /// </list>
    /// The check is one method so the rejection and the message that explains it cannot disagree.
    /// </remarks>
    private static bool IsUnusableRowType(Type elementType)
        => elementType == typeof(object) || elementType == typeof(PersistentObject);

    private static IEnumerable<object> MaterializeQueryable(object queryable, Type elementType)
    {
        // Call Queryable.ToList() on an in-memory IQueryable<T>
        var toListMethod = ReflectionCache.GetOrAdd<(string Op, Type Type), MethodInfo>(
            ("QueryExecutor.EnumerableToList", elementType),
            static k => typeof(Enumerable).GetMethods()
                .First(m => m.Name == nameof(Enumerable.ToList) && m.GetGenericArguments().Length == 1)
                .MakeGenericMethod(k.Type));

        var result = toListMethod.Invoke(null, [queryable]);
        if (result is System.Collections.IEnumerable enumerable)
        {
            return enumerable.Cast<object>().ToList();
        }
        return [];
    }


    #endregion

    #region Shared Helpers

    /// <summary>
    /// Returns session.Query&lt;resultType, indexType&gt;()
    /// </summary>
    /// <param name="session">The asynchronous document session to execute the query on.</param>
    /// <param name="resultType">The type of the query result.</param>
    /// <param name="indexType">The type of the index to use for the query.</param>
    /// <returns>The result of the invoked generic query.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the required generic Query&lt;T, TIndexCreator&gt; method cannot be found on the session.</exception>
    private object ApplyIndexWithType(IAsyncDocumentSession session, Type resultType, Type indexType)
    {
        var genericMethod = ReflectionCache.GetOrAdd<(string Op, Type Result, Type Index), MethodInfo>(
            ("QueryExecutor.SessionQueryByIndexCreator", resultType, indexType),
            static k =>
            {
                var sessionQueryMethod = typeof(IAsyncDocumentSession).GetMethods()
                    .FirstOrDefault(m => m.Name == "Query"
                        && m.IsGenericMethod
                        && m.GetGenericArguments().Length == 2
                        && m.GetParameters().Length == 0)
                    ?? throw new InvalidOperationException("Could not find Query<T, TIndexCreator> method on IAsyncDocumentSession");
                return sessionQueryMethod.MakeGenericMethod(k.Result, k.Index);
            });
        return genericMethod.Invoke(session, [])!;
    }

    /// <summary>
    /// Replays whatever the context property composed onto its query — a <c>Where</c>, an
    /// <c>OrderBy</c> — on top of the index-backed query, instead of discarding it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A context property is free to return more than a bare root:
    /// <c>MyAccounts =&gt; Session.Query&lt;Account&gt;().Where(a =&gt; a.OwnerId == currentUser.Id)</c>.
    /// Building the index query from scratch threw that predicate away, so the grid returned every
    /// row — fail-open, with no error and no log. Re-rooting keeps the author's intent.
    /// </para>
    /// <para>
    /// A bare root short-circuits to the index query itself, so the RQL for every property that does
    /// not compose anything is byte-identical to before. That is every context property in the repo
    /// today, including the ones the index generator emits.
    /// </para>
    /// <para>
    /// Note the scope: this makes a scoped property honest for the <em>grid</em>. It is not an
    /// authorization boundary — a by-id GET, PUT or DELETE never consults the context property, so a
    /// row rule is still what enforces access.
    /// </para>
    /// </remarks>
    private static object RerootOntoIndexQuery(object propertyQueryable, object indexQueryable)
    {
        if (propertyQueryable is not IQueryable composed || indexQueryable is not IQueryable indexed)
            return indexQueryable;

        // `session.Query<T>()` surfaces as a constant holding the provider's own inspector; anything
        // composed on top of it is a method call over that constant.
        if (composed.Expression is ConstantExpression)
            return indexQueryable;

        if (composed.ElementType != indexed.ElementType)
            return indexQueryable;

        return indexed.Provider.CreateQuery(new QueryRootSwapper(indexed.Expression).Visit(composed.Expression));
    }

    /// <summary>Swaps the root queryable of an expression tree for another query's expression.</summary>
    private sealed class QueryRootSwapper(Expression replacement) : ExpressionVisitor
    {
        protected override Expression VisitConstant(ConstantExpression node)
            => typeof(IQueryable).IsAssignableFrom(node.Type) ? replacement : node;
    }

    /// <summary>
    /// Returns queryable.ProjectInto&lt;resultType&gt;() to apply index projections for computed/stored fields.
    /// </summary>
    /// <param name="queryable"></param>
    /// <param name="resultType"></param>
    /// <returns></returns>
    private object ApplyProjection(object queryable, Type resultType)
    {
        var genericProjectMethod = ReflectionCache.GetOrAdd<(string Op, Type Type), MethodInfo?>(
            ("QueryExecutor.LinqProjectInto", resultType),
            static k =>
            {
                var projectIntoMethod = typeof(LinqExtensions).GetMethods()
                    .FirstOrDefault(m => m.Name == "ProjectInto"
                        && m.IsGenericMethod
                        && m.GetGenericArguments().Length == 1
                        && m.GetParameters().Length == 1
                        && m.GetParameters()[0].ParameterType == typeof(IQueryable));
                return projectIntoMethod?.MakeGenericMethod(k.Type);
            });

        if (genericProjectMethod == null)
        {
            return queryable;
        }

        return genericProjectMethod.Invoke(null, [queryable])!;
    }

    /// <summary>
    /// Applies sorting to the queryable based on the provided sort columns.
    /// </summary>
    /// <param name="queryable"></param>
    /// <param name="entityType"></param>
    /// <param name="sortColumns"></param>
    /// <returns></returns>
    /// <summary>
    /// Orders <paramref name="queryable"/> by the requested columns, redirecting each to its sort companion
    /// when the model declares one.
    /// <para>
    /// Callers, query JSON <c>sortBy</c> and the <c>?sortBy=</c> override all name the <em>display</em>
    /// attribute. A field indexed <c>FieldIndexing.Search</c> is analyzed and tokenized, so ordering on it is
    /// meaningless — <c>Volkswagen Golf GTI</c> is stored as the three terms <c>volkswagen</c>, <c>golf</c>,
    /// <c>gti</c>, and ordering documents by "their" term is then arbitrary. Its sort companion holds the same
    /// value as a single un-analyzed term, which is what ordering must actually use.
    /// </para>
    /// <para>
    /// This also explains why the problem is invisible until it bites: a space is the tokenization boundary,
    /// so a single-word value yields one term either way and an analyzed field <em>accidentally</em> sorts
    /// correctly. Without this redirect a generated companion is correctly indexed, correctly stored, and
    /// never used.
    /// </para>
    /// </summary>
    private object ApplySorting(object queryable, Type entityType, SortColumn[] sortColumns,
        EntityTypeDefinition definition)
    {
        for (int i = 0; i < sortColumns.Length; i++)
        {
            var col = sortColumns[i];

            // The sort column names an attribute of the query surface, or it is refused (#295).
            //
            // Ordering is a comparison oracle: sorting by a field and observing where a row lands
            // binary-searches its value. Redaction nulls the value in the response but does nothing
            // to the ORDER BY, so without this check an attribute the caller may never read is still
            // fully readable one comparison at a time.
            //
            // Gating on ShowedOn rather than on the redaction hook is deliberate. GetProtectedAttributesAsync
            // takes an entity and may answer differently per row, so it cannot decide a query-level
            // operation — and by the time rows exist the ordering has already happened. "Not on the
            // query surface" is static, already synchronized, and already how an app hides a column.
            //
            // Checked against the DECLARED name, before ResolveSortProperty redirects: a sort
            // companion is only ever used when it IsIgnoredForSparkModel, so it is never a model
            // attribute and would fail this check itself.
            if (!IsSortableAttribute(definition, col.Property))
            {
                Console.WriteLine(
                    $"Warning: sort column '{col.Property}' is not an attribute of {definition.Name}'s query " +
                    $"surface; the column is refused and rows keep their index order.");
                continue;
            }

            var propertyInfo = entityType.GetCachedProperty(ResolveSortProperty(entityType, col.Property));
            if (propertyInfo == null)
            {
                // Not an error: a model attribute can legitimately be absent from a narrower
                // projection. But dropping the column silently reads as broken ordering (#279).
                Console.WriteLine(
                    $"Warning: sort column '{col.Property}' has no matching property on {entityType.Name}; " +
                    $"the column is skipped and rows keep their index order.");
                continue;
            }

            var isDescending = string.Equals(col.Direction, "desc", StringComparison.OrdinalIgnoreCase);
            var methodName = i == 0
                ? (isDescending ? "OrderByDescending" : "OrderBy")
                : (isDescending ? "ThenByDescending" : "ThenBy");

            var parameter = System.Linq.Expressions.Expression.Parameter(entityType, "x");
            var propertyAccess = System.Linq.Expressions.Expression.Property(parameter, propertyInfo);
            var lambda = System.Linq.Expressions.Expression.Lambda(propertyAccess, parameter);

            var orderMethod = ReflectionCache.GetOrAdd<(string Op, string Method, Type Entity, Type Prop), MethodInfo>(
                ("QueryExecutor.QueryableOrder", methodName, entityType, propertyInfo.PropertyType),
                static k => typeof(Queryable).GetMethods()
                    .First(m => m.Name == k.Method && m.GetParameters().Length == 2)
                    .MakeGenericMethod(k.Entity, k.Prop));

            queryable = orderMethod.Invoke(null, [queryable, lambda])!;
        }
        return queryable;
    }

    /// <summary>
    /// The RavenDB search term for a raw user input, or <c>null</c> when there is nothing to search for.
    /// <para>
    /// Each whitespace-separated word is wrapped as <c>*word*</c> and the whole thing is matched with
    /// <see cref="Raven.Client.Documents.Queries.SearchOperator.And"/>, so every word must appear somewhere in the field. That is what
    /// preserves the substring semantics this replaced: before the pushdown, search was an in-memory
    /// <c>Contains</c> over already-materialized rows, and a term-based query alone would silently stop
    /// matching <c>olkswag</c> against <c>Volkswagen</c>.
    /// </para>
    /// <para>
    /// <c>*</c> and <c>?</c> are stripped from the caller's words rather than honoured. A bare <c>*</c>
    /// matches every document, and measured on RavenDB 7.2.5 a <c>?</c> never matches while a mid-word
    /// <c>*</c> matches nothing either — so passing them through could only surprise. The term is not
    /// lower-cased: RavenDB lower-cases it for us.
    /// </para>
    /// <para>
    /// Wrapping each word separately, rather than the whole input, is what lets a substring span a space:
    /// <c>*olf* *gt*</c> matches <c>Volkswagen Golf GTI</c> because the words are matched independently. The
    /// side effect is that this is slightly <em>wider</em> than <c>Contains</c> — the words need not be adjacent
    /// or in order, so <c>gti golf</c> matches where <c>Contains</c> would not. A widening, so no caller loses a
    /// result.
    /// </para>
    /// </summary>
    internal static string? BuildSearchTerm(string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return null;

        var words = search
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(static word => word.Replace("*", string.Empty).Replace("?", string.Empty))
            .Where(static word => word.Length > 0)
            .Select(static word => $"*{word}*");

        var term = string.Join(' ', words);
        return term.Length == 0 ? null : term;
    }

    /// <summary>
    /// The fields a search term is matched against on <paramref name="sortType"/>: its readable
    /// <see cref="string"/> properties, excluding the document id and anything <c>[IgnoreProperty]</c>.
    /// <para>
    /// Deliberately *not* scoped to <c>[Search]</c>. Measured on RavenDB 7.2.5: a wildcard term matches on a
    /// plain default-indexed field (the whole value is one lower-cased term), while a bare word does not. So
    /// wildcard search works with or without <c>FieldIndexing.Search</c>, and scoping to declared fields would
    /// narrow what users can find for no gain. <c>[Search]</c> keeps its own job — token matching, analyzer
    /// behaviour, and forcing the sort companion.
    /// </para>
    /// <para>
    /// <c>[IgnoreProperty]</c> excludes the sort companions, which hold the same text as the field they
    /// shadow; searching both would double the clauses to find the same rows. The document id is excluded
    /// because <c>search(id(), …)</c> was measured to match nothing — a dead clause.
    /// </para>
    /// <para>
    /// A <c>TranslatedString</c> needs no special handling: it fans out to <c>{Prop}_{lang}</c> string fields
    /// on the projection, so every language is searched, with no dependency on the request's culture.
    /// </para>
    /// <para>
    /// Known gap: a string field a hand-written index declares <c>FieldIndexing.Exact</c> is included and will
    /// match case-sensitively, because the CLR property carries no trace of the index's field options. The
    /// generator only ever applies <c>Exact</c> to <c>DateTimeOffset</c>, which is excluded by type.
    /// </para>
    /// </summary>
    private static PropertyInfo[] ResolveSearchableProperties(Type sortType)
        => ReflectionCache.GetOrAdd<(string Op, Type Type), PropertyInfo[]>(
            ("QueryExecutor.SearchableProperties", sortType),
            static k => k.Type.GetCachedProperties()
                .Where(static p => p.PropertyType == typeof(string)
                    && p.CanRead
                    && p.GetIndexParameters().Length == 0
                    && !string.Equals(p.Name, "Id", StringComparison.Ordinal)
                    && !p.IsIgnoredForSparkModel())
                .ToArray());

    /// <summary>
    /// Adds one <c>Search</c> clause per searchable field, and reports whether anything was added.
    /// <para>
    /// <see cref="SearchOptions"/> is never passed. That is a safety requirement rather than a style
    /// preference: measured on RavenDB 7.2.5, an explicit <see cref="SearchOptions.Or"/> leaks onto the
    /// <em>adjacent</em> clause and ORs it in. The adjacent clause here is the row-security predicate, so an
    /// explicit option would turn a security filter into an alternative — silently, returning plausible rows.
    /// The default <see cref="SearchOptions.Guess"/> instead groups the consecutive clauses and ANDs the group
    /// with its neighbours in both directions, which is exactly what a multi-field search wants.
    /// </para>
    /// <para>Every argument is passed explicitly because <see cref="MethodInfo.Invoke"/> does not apply
    /// optional parameter defaults.</para>
    /// </summary>
    private static (object Queryable, bool Applied) ApplySearch(object queryable, Type elementType, string term)
    {
        var properties = ResolveSearchableProperties(elementType);
        if (properties.Length == 0)
        {
            return (queryable, false);
        }

        var searchMethod = ReflectionCache.GetOrAdd<(string Op, Type Element), MethodInfo>(
            ("QueryExecutor.LinqSearch", elementType),
            static k => typeof(LinqExtensions).GetMethods()
                .First(m => m.Name == nameof(LinqExtensions.Search)
                    && m.IsGenericMethod
                    && m.GetGenericArguments().Length == 1
                    && m.GetParameters().Length == 6
                    // The string overload, not the IEnumerable<string> one.
                    && m.GetParameters()[2].ParameterType == typeof(string))
                .MakeGenericMethod(k.Element));

        var selectorType = typeof(Func<,>).MakeGenericType(elementType, typeof(object));

        foreach (var property in properties)
        {
            var parameter = System.Linq.Expressions.Expression.Parameter(elementType, "x");
            // Expression<Func<T, object>>, so the property access needs boxing even for a string.
            var propertyAccess = System.Linq.Expressions.Expression.Convert(
                System.Linq.Expressions.Expression.Property(parameter, property), typeof(object));
            var lambda = System.Linq.Expressions.Expression.Lambda(selectorType, propertyAccess, parameter);

            queryable = searchMethod.Invoke(
                null,
                [queryable, lambda, term, 1m, SearchOptions.Guess, Raven.Client.Documents.Queries.SearchOperator.And])!;
        }

        return (queryable, true);
    }

    /// <summary>
    /// The property to order by for a requested attribute name: its sort companion when one exists on the
    /// sort type, otherwise the requested name unchanged.
    /// <para>
    /// Derived by convention rather than read from the model. The companion is always
    /// <c>{Name}Sort</c> — measured across every hand-written index in the reference corpus, with no
    /// exceptions — so persisting the name per attribute would add a model field, and matching model-hash
    /// churn on every existing file, to restate something already derivable. It would also be one more thing
    /// able to go stale: a persisted name outliving the property it points at.
    /// </para>
    /// <para>
    /// The companion must be <c>[IgnoreProperty]</c> to qualify. That is not decoration — it is the signal
    /// that distinguishes a real sort companion from a coincidence, so an ordinary domain property that
    /// happens to be named <c>FooSort</c> cannot silently hijack ordering on <c>Foo</c>. Every companion,
    /// generated or hand-written, carries it.
    /// </para>
    /// <para>If a per-attribute override is ever needed, an optional model field can be added later without
    /// breaking anything: absent would continue to mean "use the convention".</para>
    /// </summary>
    /// <summary>
    /// Whether <paramref name="requested"/> names an attribute the caller may order by: it must exist
    /// in the model and be part of the query surface.
    /// </summary>
    private static bool IsSortableAttribute(EntityTypeDefinition definition, string requested)
    {
        var attribute = definition.Attributes
            .FirstOrDefault(a => string.Equals(a.Name, requested, StringComparison.OrdinalIgnoreCase));

        return attribute is not null && attribute.ShowedOn.HasFlag(EShowedOn.Query);
    }

    private static string ResolveSortProperty(Type sortType, string requested)
    {
        var companion = sortType.GetCachedProperty(requested + "Sort");
        if (companion is null) return requested;
        if (!companion.IsIgnoredForSparkModel()) return requested;

        return companion.Name;
    }

    /// <summary>
    /// Materializes an IRavenQueryable<T> by calling ToListAsync via reflection.
    /// </summary>
    /// <param name="queryable"></param>
    /// <param name="entityType"></param>
    /// <returns></returns>
    private async Task<IEnumerable<object>> ExecuteQueryableAsync(object queryable, Type entityType)
    {
        var genericToListMethod = ReflectionCache.GetOrAdd<(string Op, Type Type), MethodInfo?>(
            ("QueryExecutor.LinqToListAsync", entityType),
            static k =>
            {
                var toListMethod = typeof(LinqExtensions).GetMethods()
                    .FirstOrDefault(m => m.Name == nameof(LinqExtensions.ToListAsync)
                        && m.GetGenericArguments().Length == 1
                        && m.GetParameters().Length == 2);
                return toListMethod?.MakeGenericMethod(k.Type);
            });

        if (genericToListMethod == null)
        {
            return [];
        }

        var task = genericToListMethod.Invoke(null, [queryable, CancellationToken.None]) as Task;

        if (task == null)
        {
            return [];
        }

        await task;

        var result = task.GetCompletedTaskResult();

        if (result is System.Collections.IEnumerable enumerable)
        {
            return enumerable.Cast<object>().ToList();
        }

        return [];
    }

    #endregion
}

/// <summary>
/// What <c>ResolveCustomQueryMethod</c> can know from a signature alone: how to invoke the method,
/// what it yields elements of, and whether the call must be awaited.
/// </summary>
/// <remarks>
/// Deliberately carries no capability flags. Whether the result can be sorted, filtered, searched or
/// projected is a property of the <em>object</em> the method returns, not of its declared type — a
/// method declared <c>IQueryable&lt;T&gt;</c> commonly returns a Raven queryable, and inferring from
/// the signature under-serves it (#294). The flags are therefore computed per invocation, after the
/// await, in <c>ExecuteCustomQueryAsync</c>.
/// </remarks>
internal sealed class CustomQueryMethodInfo
{
    public required MethodInfo Method { get; init; }
    public required bool AcceptsArgs { get; init; }
    public required Type ResultElementType { get; init; }
    public required bool IsAsync { get; init; }
}

/// <summary>
/// The ordering rule for rows sorted in memory: nulls after values, strings ordinal case-insensitive,
/// everything else by its own <see cref="IComparable"/>.
/// </summary>
/// <remarks>
/// Mixed or non-comparable values compare EQUAL rather than throwing. A grid that cannot order one
/// column is a smaller failure than a request that 500s, and the alternative — letting
/// <c>Comparer&lt;object&gt;.Default</c> throw <c>InvalidOperationException</c> mid-enumeration — would
/// surface as an unexplained error on a query that renders fine unsorted.
/// <para>
/// Nulls-after-values is expressed in the comparer, so a descending sort reverses it and nulls lead.
/// That is the same asymmetry a database gives you without an explicit NULLS LAST.
/// </para>
/// </remarks>
internal sealed class RowSortComparer : IComparer<object?>
{
    public static readonly RowSortComparer Instance = new();

    public int Compare(object? x, object? y)
    {
        if (x is null) return y is null ? 0 : 1;
        if (y is null) return -1;

        if (x is string sx && y is string sy)
            return string.Compare(sx, sy, StringComparison.OrdinalIgnoreCase);

        if (x is IComparable comparable && x.GetType() == y.GetType())
            return comparable.CompareTo(y);

        return 0;
    }
}
