using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;

using static MintPlayer.Spark.Services.SparkHookInvocation;

namespace MintPlayer.Spark.Services;

public interface IQueryExecutor
{
    /// <param name="restrictToIds">
    /// When set, the run is narrowed to these row ids and paging is ignored — the caller wants
    /// exactly these rows, not a page. This is how a custom action re-materializes a selection: it
    /// re-runs the query the rows came from, so they arrive with the query's own projection
    /// (index-computed columns included) rather than being re-derived from documents.
    /// </param>
    Task<QueryResult> ExecuteQueryAsync(SparkQuery query, PersistentObject? parent = null, int skip = 0, int take = 50, string? search = null, IReadOnlyCollection<string>? restrictToIds = null, IReadOnlyList<QueryColumnFilter>? columnFilters = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether this query's method returns its own page (<see cref="SparkQueryPage{T}"/>) and
    /// therefore owns filtering, search, sorting, counting and paging.
    /// </summary>
    /// <remarks>
    /// Answered from the declared return type without invoking anything, so a caller that cannot
    /// work with an author-paged query — re-materializing a selection, which has no way to ask for
    /// "the page containing these ids" — can branch rather than try and fail.
    /// </remarks>
    /// <summary>
    /// The distinct values of one column, for a filter panel (#431). Empty when the column may not
    /// be enumerated -- indistinguishable from "nothing to list", on purpose.
    /// </summary>
    Task<DistinctValuesResult> GetDistinctValuesAsync(SparkQuery query, string column,
        PersistentObject? parent = null, string? search = null,
        IReadOnlyList<QueryColumnFilter>? columnFilters = null,
        CancellationToken cancellationToken = default);

    bool OwnsItsOwnPaging(SparkQuery query);
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
    [Inject] private readonly IRowSecurityGate gate;

    /// <summary>
    /// Nullable so the distinct pass (#431) can recognise the breadcrumb redaction placeholder
    /// without making options mandatory for every other path through this class.
    /// </summary>
    [Inject] private readonly Microsoft.Extensions.Options.IOptions<Configuration.SparkOptions>? breadcrumbOptions = null;

    /// <summary>Optional, so no existing construction path is forced to supply one.</summary>
    [Inject] private readonly Microsoft.Extensions.Logging.ILogger<QueryExecutor>? logger = null;

    /// <summary>
    /// Queries whose paging decision has been reported, so the log states it once rather than per
    /// request. Same shape as RowSecurity's announcement dictionary, and for the same reason: this is
    /// a fact about a query's shape, not an event.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Query, bool Pushed), bool> pagingAnnounced = new();

    /// <summary>
    /// The distinct values of one column, for a filter panel (#431).
    /// </summary>
    /// <remarks>
    /// <b>Computed in memory, over rows the pipeline has already secured</b> — not by a RavenDB
    /// facet. A facet aggregates in the database, where the row filter frequently is not: it refuses
    /// to compose into a projection query, which is the default shape for an indexed query, and the
    /// gate that actually filters those runs after materialization. A facet on such a query would
    /// publish values drawn from rows the caller may not read — the same oracle class as the sort
    /// hardening, but returning the values instead of leaking their order.
    /// <para>
    /// Running here also makes reference columns possible at all: breadcrumb text is resolved after
    /// materialization and is not an index term, so there is nothing for a facet to aggregate.
    /// </para>
    /// <para>
    /// The cost is one extra pass over rows the request has already materialized, bounded by the cap.
    /// </para>
    /// </remarks>
    public async Task<DistinctValuesResult> GetDistinctValuesAsync(SparkQuery query, string column,
        PersistentObject? parent = null, string? search = null,
        IReadOnlyList<QueryColumnFilter>? columnFilters = null,
        CancellationToken cancellationToken = default)
    {
        // Refused before anything loads. A streaming query's rows arrive over a socket from an
        // IAsyncEnumerable method, and resolving it the ordinary way throws — ResolveCustomQueryMethod
        // accepts zero parameters or one CustomQueryArgs, never a streaming method's
        // (StreamingQueryArgs, CancellationToken) — which escaped this endpoint as a 500 for any
        // caller who got past authorization. The CanListDistincts check below cannot save it: it
        // happens after the rows load, which is where the throw was.
        //
        // Empty rather than an error, matching every other refusal here: indistinguishable from a
        // column that simply has nothing to list.
        if (query.IsStreamingQuery) return DistinctValuesResult.Empty;

        // The whole result set, not a page: a distinct list describes the query, not the page the
        // grid happens to be on. Paging is applied to rows, never to this.
        var rows = await LoadSecuredRowsAsync(query, parent, columnFilters, cancellationToken);
        if (rows.Definition is null) return DistinctValuesResult.Empty;

        var attribute = ColumnCapabilities.FindQuerySurfaceAttribute(rows.Definition, column);

        // Indistinguishable from "nothing to list", deliberately — see DistinctValuesResult.Empty.
        if (attribute is null || !ColumnCapabilities.CanListDistincts(attribute, query))
            return DistinctValuesResult.Empty;

        return ProjectDistincts(rows.Rows, attribute.Name, search);
    }

    /// <summary>The cap on a distinct bucket. Matches the protocol this follows.</summary>
    private const int MaxDistinctValues = 100;

    /// <summary>
    /// One pass over secured rows, collecting <c>{ value, label }</c> pairs.
    /// </summary>
    /// <remarks>
    /// Identity is the <b>value</b>, never the label: two rows may legitimately render the same text,
    /// and collapsing on text would drop a genuinely selectable value. Ordering is by label, because
    /// that is what the reader scans.
    /// <para>
    /// A reference the caller may not read arrives carrying the redaction placeholder rather than a
    /// name, and is <b>dropped</b> — offering it would confirm the row exists while showing a value
    /// that cannot be chosen meaningfully, and a list of placeholders is worse than a short list.
    /// </para>
    /// </remarks>
    private DistinctValuesResult ProjectDistincts(
        IReadOnlyList<PersistentObject> rows, string attributeName, string? search)
    {
        var placeholder = breadcrumbOptions?.Value.Breadcrumb.RedactedPlaceholder;
        var term = search?.Trim();
        var seen = new HashSet<object?>();
        var values = new List<DistinctValue>();
        var hasMore = false;

        foreach (var row in rows)
        {
            var attribute = row.Attributes
                .FirstOrDefault(a => string.Equals(a.Name, attributeName, StringComparison.OrdinalIgnoreCase));
            if (attribute is null) continue;

            var value = attribute.Value;
            var label = attribute.Breadcrumb ?? value?.ToString() ?? NullDistinctLabel;

            if (placeholder is not null && string.Equals(label, placeholder, StringComparison.Ordinal))
                continue;

            if (term is { Length: > 0 }
                && label.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            if (!seen.Add(value)) continue;

            // Counted, not collected, once the cap is reached: the caller needs to know the list is
            // truncated or its search box silently stops fetching.
            if (values.Count >= MaxDistinctValues)
            {
                hasMore = true;
                break;
            }

            values.Add(new DistinctValue { Value = value, Label = label });
        }

        values.Sort(static (a, b) => string.Compare(a.Label, b.Label, StringComparison.CurrentCulture));

        return new DistinctValuesResult { Matching = values, HasMore = hasMore };
    }

    /// <summary>What a row with no value for the column is listed as.</summary>
    private const string NullDistinctLabel = "< none >";

    /// <summary>
    /// Runs the query as far as the row-security gate and hands back the secured rows.
    /// </summary>
    /// <remarks>
    /// The same two branches <see cref="ExecuteQueryAsync"/> takes, stopping before paging: a
    /// distinct list describes the whole result set, not the page the grid is showing.
    /// <para>
    /// The query hook still runs, because it is the presentation funnel every execution passes
    /// through and skipping it here would let a distinct request see a query the hook reshaped for
    /// everyone else.
    /// </para>
    /// </remarks>
    private async Task<(IReadOnlyList<PersistentObject> Rows, EntityTypeDefinition? Definition)> LoadSecuredRowsAsync(
        SparkQuery query, PersistentObject? parent, IReadOnlyList<QueryColumnFilter>? columnFilters,
        CancellationToken cancellationToken)
    {
        var (isCustom, name) = ResolveSource(query);
        await InvokeQueryHookAsync(query, parent);

        var source = isCustom
            ? await ExecuteCustomQueryAsync(query, name, parent, null, 0, int.MaxValue, null, null, columnFilters, cancellationToken)
            : await ExecuteDatabaseQueryAsync(query, name, parent, null, null, columnFilters,
                // Skip/take of zero/max: a distinct list describes the whole result set, never a page.
                skip: 0, take: int.MaxValue, cancellationToken);

        return (source.Rows.Rows, source.Definition);
    }

    public async Task<QueryResult> ExecuteQueryAsync(SparkQuery query, PersistentObject? parent = null, int skip = 0, int take = 50, string? search = null, IReadOnlyCollection<string>? restrictToIds = null, IReadOnlyList<QueryColumnFilter>? columnFilters = null, CancellationToken cancellationToken = default)
    {
        var (isCustom, name) = ResolveSource(query);

        // Null/whitespace collapses to null here, so every path below tests one thing.
        var searchTerm = BuildSearchTerm(search);

        // The presentation hook, called once for BOTH sources at the single funnel every query and
        // every bulk-action re-materialization passes through.
        //
        // It runs HERE — before either branch — and the ordering is structural, not a policy
        // preference: at this point no rows have been produced and no queryable has been built, so
        // there is nothing in scope that could be handed to the context. A hook that runs before the
        // data exists CANNOT filter it, however hard someone tries. Running it after row security
        // would put mapped rows one refactor away from the context signature, and the first request
        // for "hide the action when the result is empty" would answer itself by passing them in.
        var queryContext = await InvokeQueryHookAsync(query, parent);

        QuerySourceResult source;
        if (isCustom)
        {
            source = await ExecuteCustomQueryAsync(query, name, parent, searchTerm, skip, take, search, restrictToIds, columnFilters, cancellationToken);

            // UNION, not last-writer-wins. Both mechanisms are legitimate and a query may use both:
            // the hook is the only channel a Database.* query has, and the custom method is the only
            // place with rows in hand, so a data-dependent withhold can only happen there. Letting
            // either overwrite the other would silently drop a withhold and leave an action offered.
            source = source with
            {
                DisabledActions = MergeDisabledActions(queryContext.DisabledActions, source.DisabledActions),
            };
        }
        else
        {
            source = await ExecuteDatabaseQueryAsync(query, name, parent, searchTerm, restrictToIds, columnFilters, skip, take, cancellationToken)
                with { DisabledActions = queryContext.DisabledActions };
        }

        var (allResults, definition, searchPushedDown, authorTotalItems, _, _, _) = source;

        // The author's page is returned as it stands. Search, sort, count and paging were all
        // transferred with it (the binary authority rule on SparkQueryPage), so applying any of
        // them here would trim, reorder or recount a result that is already final — and every one
        // of those failures is invisible in the grid.
        if (authorTotalItems is int authorTotal)
        {
            // F1. The author's total counts the rows THEY produced; row security removed rows after
            // that, inside ExecuteCustomQueryAsync. So on a row-scoped type the rows were filtered
            // and the count was not, and TotalItems became a cardinality oracle for rows the caller
            // may not see — three lines from the comment asserting row security is not transferable.
            //
            // It cannot be repaired by counting: the framework holds one page, so it cannot know how
            // many of the author's other rows would survive. The combination is refused instead,
            // which is the only way the count is never wrong. Nothing in the repository pairs
            // SparkQueryPage with a row-ruled type today, so this costs no working query.
            if (definition?.ClrType is { Length: > 0 } authorClrType
                && SparkTypeResolver.ResolveClrType(authorClrType) is { } authorEntityType
                && rowSecurity.HasRowRule(authorEntityType))
            {
                throw new InvalidOperationException(
                    $"Query '{query.Name}' returns SparkQueryPage<T>, which transfers paging and the row " +
                    $"count to the author, but '{definition.Name}' declares a row rule. The framework " +
                    $"filters the returned page afterwards and cannot recount the rest, so TotalItems " +
                    $"would report rows this caller may not see. Either return an IQueryable and let the " +
                    $"framework page it, or remove the row rule from '{definition.Name}Actions' and scope " +
                    $"the rows inside the query method itself.");
            }

            // ⚠️ sortType matters here for the same reason it matters on the ordinary path below, and
            // leaving it out was worse on this one. An author-paged query already skips the
            // framework's own column filtering (the filters are handed to the author instead), so if
            // the column metadata ALSO claims every column is sortable and filterable, the grid draws
            // a sort arrow and a filter cell on columns the author's row shape does not carry — and
            // nothing downstream refuses them. Passing it lets IsBackedByShape narrow the claim to
            // what the returned rows can actually answer.
            var authorColumns = definition is not null
                ? QueryResultProjector.BuildColumns(definition, query, source.SortType)
                : [];
            return new QueryResult
            {
                Columns = authorColumns,
                Items = QueryResultProjector.ToItems(allResults, authorColumns, query.Name),
                TotalItems = authorTotal,
                Skip = skip,
                Take = take,
                DisabledActions = source.DisabledActions,
            };
        }

        // Fallback for shapes that cannot push down: a Custom. query returning a non-Raven
        // IQueryable, or a type with no searchable field. Also the only path that still matches
        // Breadcrumb — resolved reference display text, which exists only after mapping and is
        // therefore not an index term. See the query guide.
        // Everything below NARROWS the secured set. Narrowing is the one transformation that cannot
        // break the gate's invariant — it only ever removes rows that already passed — which is why
        // SecuredRows.Narrow is an instance method: you must already hold a secured set to get
        // another one.
        if (searchTerm != null && !searchPushedDown)
        {
            var term = search!.ToLowerInvariant();
            allResults = allResults.Narrow(rows => rows.Where(po =>
                (po.Name != null && po.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                (po.Breadcrumb != null && po.Breadcrumb.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                po.Attributes.Any(attr =>
                {
                    var value = attr.Breadcrumb ?? attr.Value?.ToString();
                    return value != null && value.Contains(term, StringComparison.OrdinalIgnoreCase);
                })));
        }

        // Counted after filtering and before paging, either way — which is what keeps
        // TotalItems search-aware now that the filter may have run in the database.
        //
        // On the pushdown path (#431 M14) the rows in hand ARE the page, so counting them would
        // report the page size as the total. The database's count stands in, and it is only ever
        // taken from a path where nothing removes rows after the query answers.
        var totalItems = source.Page?.TotalItems ?? allResults.Count;

        // A restricted run returns exactly the rows asked for. Paging it would serve "the first
        // `take` of the selection", which is how a bulk action silently acts on a subset.
        var paged = restrictToIds is { Count: > 0 } || source.Page is not null
            ? allResults
            : allResults.Narrow(rows => rows.Skip(skip).Take(take));

        // Columns ship once per result, not once per row. A definition-less result cannot describe
        // its own columns, and the client renders from them, so an empty column set is the honest
        // answer rather than a guess reconstructed from whichever attributes the first row happens
        // to carry.
        var columns = definition is not null
            ? QueryResultProjector.BuildColumns(definition, query, source.SortType)
            : [];

        return new QueryResult
        {
            Columns = columns,
            Items = QueryResultProjector.ToItems(paged, columns, query.Name),
            TotalItems = totalItems,
            Skip = skip,
            Take = take,
            DisabledActions = source.DisabledActions,
        };
    }

    /// <summary>
    /// What a query source produced, and how much of the pipeline it already applied.
    /// </summary>
    /// <param name="Rows">The mapped rows, before paging unless <paramref name="AuthorTotalItems"/> says otherwise.</param>
    /// <param name="Definition">The entity type the rows were mapped against; <see langword="null"/> when the source produced nothing to describe.</param>
    /// <param name="SearchPushedDown">Whether the search term was applied in the database, so the in-memory fallback must not run again.</param>
    /// <param name="AuthorTotalItems">
    /// Non-null when the custom method returned a <see cref="SparkQueryPage{T}"/> and therefore owns
    /// filtering, search, sorting, counting and paging. The value is the pre-paging total.
    /// </param>
    /// <summary>
    /// Builds the per-request context and gives the entity's actions class its say.
    /// </summary>
    /// <remarks>
    /// Resolved by type where there is one and by entity name otherwise: a composed query has no
    /// CLR entity type, and that is exactly where this hook earns its keep, because row security is
    /// documented as not running for composed queries at all.
    /// <para>
    /// A missing actions class is not an error — most types never override this — so resolution
    /// failure yields a context nobody wrote to rather than throwing.
    /// </para>
    /// </remarks>
    private async Task<SparkQueryContext> InvokeQueryHookAsync(SparkQuery query, PersistentObject? parent)
    {
        var context = new SparkQueryContext
        {
            Query = SparkQueryInfo.From(query),
            Parent = parent,
            ParentType = parent is not null ? modelLoader.GetEntityType(parent.ObjectTypeId)?.Name : null,
        };

        object? actionsInstance = null;
        try
        {
            var definition = string.IsNullOrEmpty(query.EntityType)
                ? ResolveDefinitionFromContextProperty(query)
                : modelLoader.GetEntityTypeByName(query.EntityType);

            var clrType = string.IsNullOrEmpty(definition?.ClrType)
                ? null
                : SparkTypeResolver.ResolveClrType(definition!.ClrType!);

            actionsInstance = clrType is not null
                ? actionsResolver.ResolveForType(clrType)
                : (definition is not null ? actionsResolver.ResolveByEntityName(definition.Name) : null);
        }
        catch
        {
            // No actions class, or one that cannot be constructed. Neither is this method's problem:
            // the query itself still runs, and a type with no override has nothing to say here.
        }

        if (actionsInstance is null)
            return context;

        // ⚠️ `DoNotWrapExceptions`, for the same reason as `DatabaseAccess` and the three invokers:
        // without it a hook that throws before returning its Task arrives as
        // `TargetInvocationException` and no typed `catch` in the endpoint matches. That now matters
        // here — since the query endpoint became a POST, `OnQueryAsync` can raise a retry, and a
        // wrapped one would leave the pipeline unhandled instead of reaching the caller as a 449.
        var method = actionsInstance.GetType().GetMethod("OnQueryAsync", [typeof(SparkQueryContext)]);
        if (method is not null &&
            method.Invoke(actionsInstance, HookInvoke, binder: null, parameters: [context], culture: null) is Task task)
        {
            await task;
        }

        return context;
    }

    /// <summary>
    /// The entity type of a <c>Database.*</c> query that declares no <c>entityType</c>, read from the
    /// context property's declared type.
    /// </summary>
    /// <remarks>
    /// Such queries are supported — <see cref="ExecuteDatabaseQueryAsync"/> derives the definition
    /// from the property's element type — but the hook runs before that resolution, so it used to see
    /// no definition, find no actions class, and never fire. Silent hook omission is precisely the
    /// failure its own documentation warns about, and the type is knowable here.
    /// <para>
    /// Reads the property's <b>declared</b> type rather than invoking its getter: this runs before
    /// authorization, and a getter can execute application code.
    /// </para>
    /// </remarks>
    private EntityTypeDefinition? ResolveDefinitionFromContextProperty(SparkQuery query)
    {
        if (!query.Source.StartsWith("Database.", StringComparison.OrdinalIgnoreCase))
            return null;

        var sparkContext = sparkContextResolver.ResolveContext(session);
        if (sparkContext is null)
            return null;

        var property = sparkContext.GetType().GetCachedProperty(query.Source[9..]);
        if (property is null || !property.CanRead)
            return null;

        var elementType = property.PropertyType.GetGenericArguments().FirstOrDefault();
        return elementType is null ? null : modelLoader.GetEntityTypeByClrType(elementType.FullName!);
    }

    /// <summary>Union of two withheld-action lists, case-insensitive, order-preserving.</summary>
    private static IReadOnlyList<string>? MergeDisabledActions(IReadOnlyList<string>? first, IReadOnlyList<string>? second)
    {
        if (first is null || first.Count == 0) return second;
        if (second is null || second.Count == 0) return first;

        var merged = new List<string>(first);
        foreach (var name in second)
        {
            if (!merged.Contains(name, StringComparer.OrdinalIgnoreCase))
                merged.Add(name);
        }

        return merged;
    }

    private sealed record QuerySourceResult(
        RowSecurityGate.SecuredRows Rows,
        EntityTypeDefinition? Definition,
        bool SearchPushedDown,
        int? AuthorTotalItems = null,
        /// <summary>
        /// Actions the custom query withheld via <c>CustomQueryArgs.DisableActions</c>. Carried
        /// here because the source is produced in one method and the QueryResult is assembled in
        /// another — the alternative was a field, which would leak across concurrent executions.
        /// </summary>
        IReadOnlyList<string>? DisabledActions = null,

        /// <summary>
        /// Set when the database applied <c>Skip</c>/<c>Take</c> and counted the matches, so the rows
        /// already ARE the page and must not be paged again in memory (#431 M14).
        /// </summary>
        DatabasePage? Page = null,

        /// <summary>
        /// The type the query's rows were shaped by — an index's <c>[FromIndex]</c> projection when one
        /// is bound, otherwise null. Carried so the columns sent to the client can say what this
        /// query can actually do (#431 M7b), which is a per-query fact and therefore cannot live on
        /// the model's per-attribute flags.
        /// </summary>
        Type? SortType = null)
;

/// <summary>The page the database produced, when paging was safe to push down (#431 M14).</summary>
/// <param name="TotalItems">
/// The database's count of matching rows. Trustworthy only on this path, where nothing removes rows
/// after the query answers — which is exactly what makes it not a cardinality oracle.
/// </param>
internal sealed record DatabasePage(int TotalItems);

    /// <summary>
    /// Narrows a query source to a set of row ids, for a custom action re-materializing a selection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three ways, in order. <b>An actions class may declare</b>
    /// <c>object RestrictToIds(object source, IReadOnlyCollection&lt;string&gt; ids)</c> — duck-typed,
    /// like the virtual-type load hook — and it wins. Otherwise the default composes
    /// <c>Where(x =&gt; ids.Contains(x.Id))</c> onto the queryable. Failing both, this <b>throws</b>.
    /// </para>
    /// <para>
    /// ⚠️ It throws rather than falling back to filtering after materialization, and that is the
    /// whole point. A restricted run ignores paging, so a fallback would have to materialize the
    /// entire result to find a handful of rows — and if it instead kept the page, the selected rows
    /// would usually not be in it, the result would come back empty, and the caller's all-or-nothing
    /// check would report that as a refusal. An action would simply stop working, with an error that
    /// reads like a permission problem. Loud is the only honest option.
    /// </para>
    /// <para>
    /// The hook exists because <c>Id</c> is not always a document id. A composed query mints its own
    /// identity, and a composite key (<c>"{a}|{b}"</c>) is a normal shape — neither is something the
    /// default can express.
    /// </para>
    /// </remarks>
    private static object RestrictToIds(
        object source,
        Type elementType,
        IReadOnlyCollection<string> ids,
        SparkQuery query,
        EntityTypeDefinition definition,
        object? actions)
    {
        if (actions is not null && ResolveRestrictHook(actions.GetType()) is { } hook)
            return hook.Invoke(actions, HookInvoke, binder: null, parameters: [source, ids], culture: null)!;

        var declaredId = elementType.GetCachedProperty("Id");

        // ── The queryable branch pushes the filter into the provider, and stays string-only.
        // Not a limitation: RavenDB cannot translate ToString() into RQL, and a document id is a
        // string by definition, so nothing reachable here needs more.
        if (declaredId is { CanRead: true } && declaredId.PropertyType == typeof(string)
            && source is IQueryable)
        {
            var parameter = Expression.Parameter(elementType, "x");
            var idAccess = Expression.Property(parameter, declaredId);

            // RavenDB and LINQ-to-objects need DIFFERENT expressions for the same idea, and neither
            // one works on the other:
            //
            //   Raven      -> x.Id.In(ids)         — the provider's own marker method
            //   in-memory  -> ids.Contains(x.Id)   — an ordinary instance call
            //
            // Handing Raven the Contains form fails at query translation; .In() outside a Raven
            // query is a marker with no runtime meaning. Both land at the first restricted run.
            //
            // Contains is an INSTANCE method, so the list is the receiver and not the first
            // argument — the other way round throws "Static method requires null instance".
            var idList = ids.ToList();
            var isRavenBacked = typeof(IRavenQueryable<>).MakeGenericType(elementType).IsInstanceOfType(source);

            Expression predicate = isRavenBacked
                ? Expression.Call(
                    ReflectionCache.GetOrAdd<(string Op, Type Element), MethodInfo>(
                        ("QueryExecutor.RavenIn", typeof(string)),
                        static _ => typeof(Raven.Client.Documents.Linq.RavenQueryableExtensions)
                            .GetMethods()
                            .First(m => m.Name == "In"
                                     && m.GetParameters().Length == 2
                                     && m.GetParameters()[1].ParameterType.IsGenericType)
                            .MakeGenericMethod(typeof(string))),
                    idAccess,
                    Expression.Constant(idList))
                : Expression.Call(Expression.Constant(idList), ContainsMethod, idAccess);

            var lambda = Expression.Lambda(predicate, parameter);

            var whereMethod = ReflectionCache.GetOrAdd<(string Op, Type Element), MethodInfo>(
                ("QueryExecutor.QueryableWhere", elementType),
                static k => typeof(Queryable).GetMethods()
                    .First(m => m.Name == nameof(Queryable.Where)
                             && m.GetParameters().Length == 2
                             && m.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2)
                    .MakeGenericMethod(k.Element));

            return whereMethod.Invoke(null, [source, lambda])!;
        }

        // ── The in-memory branch matches on the row's id AS A STRING, read off the row's RUNTIME
        // type. Both halves of that matter.
        //
        // ToString(), because that is literally how the id on the wire was minted: the mapper does
        // `Id?.ToString()` and the projector copies it onto the row. So a row keyed by an int or a
        // Guid renders a perfectly good grid, and comparing the same way it was serialised makes
        // narrowing agree with it by construction rather than by luck. Requiring `string` here was
        // an arbitrary narrowing of a path that had no reason to care.
        //
        // The RUNTIME type, because the declared one can be weaker: a method declared
        // `IEnumerable<IRow>` returning concrete rows rendered a flawless grid — the mapper reads
        // the instance — and then threw on narrowing, which read the interface. The two now read
        // the same place.
        //
        // Filtering after the source produced everything is also the honest semantic and not a
        // fallback: it is what proves the submitted ids were in the query's result. Narrowing at
        // the source would let a caller name rows the query never returned, and the all-or-nothing
        // count could not tell.
        if (source is not IQueryable && source is System.Collections.IEnumerable rows)
        {
            var wanted = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
            var kept = (System.Collections.IList)Activator.CreateInstance(
                typeof(List<>).MakeGenericType(elementType))!;

            foreach (var row in rows)
            {
                if (row is null) continue;

                var rowId = row.GetType().GetCachedProperty("Id");
                if (rowId is null || !rowId.CanRead)
                    throw NoIdToMatchOn(query, row.GetType(), definition, actions);

                if (AccessorCache.GetGetter(rowId)(row)?.ToString() is { } id && wanted.Contains(id))
                    kept.Add(row);
            }

            return kept;
        }

        throw NoIdToMatchOn(query, elementType, definition, actions);
    }

    /// <summary>
    /// The one message for "this query's rows cannot be matched against the ids the client posted".
    /// </summary>
    /// <remarks>
    /// Reachable only for a row type with no readable <c>Id</c> at all — and such a type is already
    /// refused, by name, the first time its grid renders (<c>QueryResultProjector.ToItems</c>), so
    /// in practice nothing gets this far without having been told once already.
    /// </remarks>
    private static InvalidOperationException NoIdToMatchOn(
        SparkQuery query, Type rowType, EntityTypeDefinition definition, object? actions)
        => new(
            $"Query '{query.Name}' cannot be narrowed to a selection. Its rows are '{rowType.Name}', " +
            $"which has no readable 'Id' for the framework to match the submitted ids against. That is " +
            $"normal for a row type that computes its own identity — a composite key, or an id minted " +
            $"per row — so say how to find those rows again by declaring the hook on " +
            $"'{HookHost(definition, actions)}':\n" +
            $"    public object RestrictToIds(object source, IReadOnlyCollection<string> ids)\n" +
            $"Without it, a custom action over a selection from this query cannot resolve its rows and " +
            $"would refuse every invocation.");

    /// <summary>
    /// The actions class the resolver ACTUALLY consults, for the message that tells an author where
    /// to put the hook.
    /// </summary>
    /// <remarks>
    /// An entity-backed query resolves <c>{ClrTypeName}Actions</c> and a composed one
    /// <c>{ModelTypeName}Actions</c>. Those coincide until a model file renames its type — and
    /// naming the wrong one sends the author to edit a class the framework never looks at.
    /// </remarks>
    private static string HookHost(EntityTypeDefinition definition, object? actions)
        => actions?.GetType().Name ?? $"{definition.Name}Actions";

    /// <summary>The <c>List&lt;string&gt;.Contains</c> the default restriction composes.</summary>
    private static readonly MethodInfo ContainsMethod = typeof(List<string>)
        .GetMethod(nameof(List<string>.Contains), [typeof(string)])!;

    /// <summary>
    /// An actions class's optional <c>RestrictToIds</c>, duck-typed and cached (nulls too).
    /// </summary>
    private static MethodInfo? ResolveRestrictHook(Type actionsType)
        => ReflectionCache.GetOrAdd<(string Op, Type Type), MethodInfo?>(
            ("QueryExecutor.RestrictToIdsHook", actionsType),
            static k =>
            {
                var method = k.Type.GetMethod("RestrictToIds", [typeof(object), typeof(IReadOnlyCollection<string>)]);
                if (method is not null && method.ReturnType != typeof(object))
                    throw new InvalidOperationException(
                        $"'{k.Type.FullName}.RestrictToIds' must return object. Expected: " +
                        $"'object RestrictToIds(object source, IReadOnlyCollection<string> ids)'.");
                return method;
            });

    public bool OwnsItsOwnPaging(SparkQuery query)
    {
        var (isCustom, methodName) = ResolveSource(query);
        if (!isCustom) return false;

        var definition = ResolveEntityTypeDefinition(query, methodName);
        if (definition is null) return false;

        var entityType = string.IsNullOrEmpty(definition.ClrType)
            ? null
            : SparkTypeResolver.ResolveClrType(definition.ClrType);

        var actions = entityType is not null
            ? actionsResolver.ResolveForType(entityType)
            : actionsResolver.ResolveByEntityName(definition.Name);
        if (actions is null) return false;

        var method = actions.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
        if (method is null) return false;

        var returnType = method.ReturnType;
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            returnType = returnType.GetGenericArguments()[0];

        return typeof(ISparkQueryPage).IsAssignableFrom(returnType);
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

    private async Task<QuerySourceResult> ExecuteDatabaseQueryAsync(
        SparkQuery query, string propertyName, PersistentObject? parent, string? searchTerm,
        IReadOnlyCollection<string>? restrictToIds, IReadOnlyList<QueryColumnFilter>? columnFilters,
        int skip, int take, CancellationToken cancellationToken)
    {
        // Authorization comes FIRST, from the query's declared entity type (F1). Everything below
        // is resolution work — reflecting over the context, reading a property, matching a CLR type
        // to a model file — and every step of it used to run for a caller with no Query right at
        // all, because the only check sat after the last of them. That is backwards on its own, and
        // it also meant a misconfigured query answered a denied caller with an empty grid instead
        // of a denial. The check below on the resolved definition stays and remains authoritative;
        // permission decisions memoize per request, so asking twice costs nothing.
        if (!string.IsNullOrEmpty(query.EntityType))
            await permissionService.EnsureAuthorizedAsync("Query", query.EntityType);

        // AFTER authorization, deliberately — this refusal names the query's source and entity type,
        // and handing that to a caller who has no Query right is the same disclosure the sortColumns
        // parser above was reordered to close.
        //
        // The parent used to stop here: this method did not take one, while the custom branch three
        // lines away did. The client sent parentId/parentType, the endpoint resolved AND authorized
        // the parent, and then this branch dropped it and served the WHOLE child collection under
        // that parent's detail page. No error, no warning, nothing at startup — the tab simply showed
        // every row in the collection. It went unnoticed only because every sub-query in the
        // repository happens to use a Custom.* source.
        //
        // A Database.* source is a queryable property on the SparkContext. It cannot express
        // "belonging to this parent" — that scoping lives in an actions method, which is why a
        // sub-query must route through one. So the parent is not something this branch can honour;
        // its presence proves the query was configured somewhere it cannot serve. Refuse, and name
        // the fix.
        //
        // But only for an actual sub-query, which is what the parent's type DECLARES, not merely
        // what a request carries. This condition used to be `parent is not null`, and that was too
        // broad by one whole use of the parent: the edit form sends the object being edited as the
        // parent when it fetches every Reference attribute's OPTION LIST, so a picker pointed at a
        // plain Database.* query — the ordinary way to offer "any Account" — failed the form with a
        // 500 and no options. The refusal's own premise does not hold there either: "serving it
        // would list every row" is the defect for a child grid and the entire point of a picker.
        //
        // Declared sub-queries are the parent type's Queries, by alias. That is exactly the
        // configuration the original guard was written to catch, so it still fires where it was
        // aimed; an undeclared pairing means the parent is context rather than a filter, and is
        // served unscoped below. This concedes nothing to a caller: dropping parentId from the
        // request already returns these rows, and the Query right was enforced above either way.
        var declaresAsSubQuery = parent is not null
            && (modelLoader.GetEntityType(parent.ObjectTypeId)?.Queries ?? [])
                .Contains(query.Alias ?? SparkQueryAliases.Derive(query.Name), StringComparer.OrdinalIgnoreCase);

        if (declaresAsSubQuery)
        {
            throw new InvalidOperationException(
                $"Query '{query.Name}' is used as a sub-query (it was executed with a parent), but its " +
                $"source '{query.Source}' reads a SparkContext property directly and cannot be scoped to " +
                $"that parent. Serving it would list every row of '{query.EntityType}' under the parent's " +
                $"page. Change the source to 'Custom.<Method>' on '{query.EntityType}Actions' and scope the " +
                $"rows with the parent, e.g. '.Where(x => x.ParentId == args.Parent!.Id)'.");
        }

        var sparkContext = sparkContextResolver.ResolveContext(session)
            ?? throw new InvalidOperationException(
                $"Query '{query.Name}' reads 'Database.{propertyName}', but this application registers no " +
                $"SparkContext. A Database.* query resolves its rows from a context property; without a " +
                $"context there is nothing to resolve against. Register one, or give the query a Custom.* " +
                $"source served by an actions class.");

        var contextType = sparkContext.GetType();
        var property = contextType.GetCachedProperty(propertyName);

        if (property == null || !property.CanRead)
        {
            throw new InvalidOperationException(
                $"Query '{query.Name}' reads 'Database.{propertyName}', but '{contextType.Name}' has no " +
                $"readable property named '{propertyName}'. Check the query's source in its model file " +
                $"against the context's properties — the name is matched exactly, and a mismatch used to " +
                $"render as an empty grid.");
        }

        var queryable = AccessorCache.GetGetter(property)(sparkContext)
            ?? throw new InvalidOperationException(
                $"Query '{query.Name}' reads '{contextType.Name}.{propertyName}', which returned null. A " +
                $"context property is expected to hand back a queryable over its collection; a null one " +
                $"cannot be distinguished from an empty collection at the grid.");

        var queryableType = property.PropertyType;
        var entityType = queryableType.GetGenericArguments().FirstOrDefault();
        if (entityType == null)
        {
            throw new InvalidOperationException(
                $"Query '{query.Name}' reads '{contextType.Name}.{propertyName}', typed " +
                $"'{queryableType.Name}', which names no element type. A context property must be an " +
                $"IRavenQueryable<T> so the framework knows what a row is.");
        }

        var entityTypeDefinition = modelLoader.GetEntityTypeByClrType(entityType.FullName ?? entityType.Name)
            ?? throw new InvalidOperationException(
                $"Query '{query.Name}' returns rows of '{entityType.Name}', which has no model file in " +
                $"App_Data/Model. Run '--spark-synchronize-model' and commit the result — without a " +
                $"definition there are no columns to render and no attributes to map into.");

        // One answer to "which type is this query about", rather than two that happen to agree.
        //
        // Two Query checks run on this path: the declared query.EntityType above, and the type
        // resolved here from the context property's element type. When they disagreed the effective
        // grant became their intersection — safe, but not something anyone had decided, and
        // SubQueryPruner had to gate on the declared name to match getQuery while recording that the
        // executor gated on the resolved one. Both halves of that divergence were correct and the
        // pair was still confusing.
        //
        // Rather than pick a winner and weaken a check, make disagreement impossible: a query whose
        // declared entityType is not the type its source actually yields is a model error, and only
        // the author can say which they meant.
        if (!string.IsNullOrEmpty(query.EntityType)
            && !string.Equals(query.EntityType, entityTypeDefinition.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Query '{query.Name}' declares entityType '{query.EntityType}', but its source " +
                $"'{query.Source}' yields rows of '{entityTypeDefinition.Name}'. Those name different types, " +
                $"so the query is authorized against one while its columns come from the other, and the " +
                $"caller silently needs the Query right on both. Set \"entityType\" to " +
                $"'{entityTypeDefinition.Name}', or point the source at a context property yielding " +
                $"'{query.EntityType}'.");
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
        var rowFilter = await rowSecurity.ComposeRowFilterAsync(queryable, entityType, resultType, "Query", cancellationToken);
        queryable = rowFilter.Queryable;

        var sortType = (indexType != null && resultType != entityType) ? resultType : entityType;

        // Column filters sit between the row filter and the search group (#431). Not merely "before
        // search": they are plain Equal comparisons, so they cannot be swept into RavenDB's
        // consecutive-Search grouping, and keeping them here leaves the security predicate and the
        // search group adjacent exactly as the comment below requires.
        if (columnFilters is { Count: > 0 })
        {
            queryable = ApplyColumnFilters(queryable, sortType, columnFilters, entityTypeDefinition, query);
        }

        // After the row filter and before sorting. The position matters for one reason: RavenDB
        // groups consecutive Search clauses and ANDs that group with its neighbours, so keeping the
        // security predicate ahead of the search group yields `(predicate) and (search or search)`.
        // Reversing them, or passing SearchOptions explicitly, ORs the predicate in instead.
        var searchPushedDown = false;
        if (searchTerm != null)
        {
            (queryable, searchPushedDown) = ApplySearch(queryable, sortType, searchTerm, ResolveIndexedSearchFields(queryable));
        }

        if (restrictToIds is { Count: > 0 })
        {
            // The actions instance, not null: the hook is documented as THE way to say how a query's
            // rows are found again, and passing null here made it silently inert on the framework's
            // most common query source — while the throw below still told the author to write it.
            // Costs nothing: actions classes are scoped and row security already resolved this one
            // earlier in the same request, so this is a dictionary lookup behind a guard that only
            // a custom action's selection reaches.
            queryable = RestrictToIds(
                queryable, sortType, restrictToIds, query, entityTypeDefinition,
                actionsResolver.ResolveForType(entityType));
        }

        if (query.SortColumns.Length > 0)
        {
            queryable = ApplySorting(queryable, sortType, query.SortColumns, entityTypeDefinition, query);
        }

        // Paging pushdown (#431 M14). Four conditions, and every one of them is about the same
        // question: can anything still remove rows after the database answers? If something can, the
        // database's page and the caller's page are different sets — pages come back short, offsets
        // drift, and the count describes rows the caller may not see.
        //
        //   1. The row filter left nothing to remove (see RowFilterComposition.CanPageInDatabase).
        //   2. No restrictToIds — that path returns exactly the rows asked for and ignores paging.
        //   3. No in-memory search fallback, which narrows AFTER materialization.
        //   4. resultType == entityType, i.e. no index projection.
        //
        // The fourth is the subtle one and it is NOT about the row filter. The gate dedupes by id,
        // and an index may fan out — one document producing several entries. Skip(n) then skips n
        // ENTRIES while the caller is counting documents, so offsets drift by however many entries
        // the skipped documents happened to produce. Restricting to the non-projecting shape keeps
        // one document to one row, which is the only case where the two agree.
        var mayPageInDatabase = rowFilter.CanPageInDatabase
            && restrictToIds is not { Count: > 0 }
            && (searchTerm is null || searchPushedDown)
            && resultType == entityType;

        // Two very different cost profiles behind one query, chosen per request. Reported once per
        // (query, outcome) so a slow grid can be explained rather than guessed at — and so the
        // ANSWER is visible, not just the fact that a choice exists.
        if (pagingAnnounced.TryAdd((query.Name, mayPageInDatabase), true))
        {
            logger?.LogInformation(
                "Query {Query}: paging {Outcome}. Row filter: {RowFilterMode}{Refinement}.",
                query.Name,
                mayPageInDatabase
                    ? "is pushed into the database"
                    : "runs in memory over the whole secured result set",
                rowFilter.Mode,
                rowFilter.HasPerRowRefinement ? ", refined per row by IsAllowedAsync" : "");
        }

        DatabasePage? databasePage = null;
        if (mayPageInDatabase)
        {
            var total = await CountQueryableAsync(queryable, resultType, cancellationToken);
            databasePage = new DatabasePage(total);
            queryable = ApplyPaging(queryable, resultType, skip, take);
        }

        var materialized = (await ExecuteQueryableAsync(queryable, resultType, cancellationToken)).ToList();

        // Row-level authorization. The type-level check above answers "may this principal query
        // this type at all"; it says nothing about which rows. Without this, an entity whose
        // Actions class scopes rows to their owner was filtered correctly when opened and listed
        // in full here — and the list screen is the one that shows every row at once.
        // Filter, breadcrumb, map, redact and the fan-out dedupe below all happen inside the gate,
        // in that fixed order, so this path can no longer disagree with the other three about it.
        // Referenced docs were primed into the session cache by .Include() above, so the resolver's
        // first batched load is a cache hit; deeper breadcrumb levels cost one request each.
        var secured = await gate.ApplyAsync(materialized, new RowSecurityContext
        {
            Session = session,
            Definition = entityTypeDefinition,
            EntityType = entityType,
            ResultType = resultType,
            Action = "Query",
            DedupeById = true,
            CancellationToken = cancellationToken,
        });

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
        //
        // It now travels as DedupeById on the context above rather than as a call here, so the
        // decision is made where the difference between the two paths is visible.
        return new QuerySourceResult(secured, entityTypeDefinition, searchPushedDown, Page: databasePage, SortType: sortType);
    }

    #endregion

    #region Custom Queries

    private async Task<QuerySourceResult> ExecuteCustomQueryAsync(
        SparkQuery query, string methodName, PersistentObject? parent, string? searchTerm,
        int skip, int take, string? search, IReadOnlyCollection<string>? restrictToIds,
        IReadOnlyList<QueryColumnFilter>? columnFilters,
        CancellationToken cancellationToken)
    {
        // Resolve the entity type for this query
        var entityTypeDefinition = ResolveEntityTypeDefinition(query, methodName)
            ?? throw new InvalidOperationException(
                string.IsNullOrEmpty(query.EntityType)
                    ? $"Query '{query.Name}' has source 'Custom.{methodName}' but names no entityType. A "
                      + $"custom query's rows are mapped against a declared type — that is where the columns "
                      + $"come from — so the executor cannot infer one from the method's return type. Set "
                      + $"\"entityType\" in the query's model file."
                    : $"Query '{query.Name}' names entityType '{query.EntityType}', which has no model file in "
                      + $"App_Data/Model. Check the spelling against the type's \"name\", or run "
                      + $"'--spark-synchronize-model' if the type is new.");

        await permissionService.EnsureAuthorizedAsync("Query", entityTypeDefinition.Name);

        // A composed query: the model type declares no clrType, so there is no entity class to
        // resolve actions over, no document behind a row, and nothing for row security to judge.
        // The actions class is found by the type's NAME instead — the same seam the virtual-type
        // page path uses. A clrType that IS declared but resolves to nothing is a different
        // failure and stays loud: that is a broken binding, not a composed type.
        Type? entityType = null;
        if (!string.IsNullOrEmpty(entityTypeDefinition.ClrType))
        {
            entityType = SparkTypeResolver.ResolveClrType(entityTypeDefinition.ClrType)
                ?? throw new InvalidOperationException(
                    $"Query '{query.Name}' maps rows to entity type '{entityTypeDefinition.Name}', whose " +
                    $"clrType '{entityTypeDefinition.ClrType}' is not declared by any loaded assembly. Either " +
                    $"the class was renamed or removed without re-running '--spark-synchronize-model', or its " +
                    $"assembly is not referenced by the host. A type that is meant to have no class at all " +
                    $"should omit clrType entirely — that is a composed type, and it is served by " +
                    $"'{entityTypeDefinition.Name}Actions'.");
        }

        // Resolve the Actions class — by type where there is one, by name where there is not.
        var actionsInstance = entityType is not null
            ? actionsResolver.ResolveForType(entityType)
            : actionsResolver.ResolveByEntityName(entityTypeDefinition.Name)
                ?? throw new InvalidOperationException(
                    $"Query '{query.Name}' is a composed query on '{entityTypeDefinition.Name}', a type with no " +
                    $"clrType, but no '{entityTypeDefinition.Name}Actions' class exists to serve it. A composed " +
                    $"type has no document behind it, so its actions class is the only thing that can produce " +
                    $"rows; without one the query has no source at all.");

        // M4. A composed type gets no row filtering and no redaction — see the block above
        // FilterAsync for why that is correct rather than an omission. What was NOT correct is that
        // skipping deliberately and forgetting entirely produced identical silence: both were
        // "entityType is null, so no enforcement", and nothing could tell them apart.
        //
        // So the type must say so. This is a declaration, not a mechanism — implementing it enforces
        // nothing — but it makes the author's intent reviewable, and the required rationale makes it
        // a sentence someone had to write rather than an interface anyone can paste on. The bar is
        // deliberately "name where the scoping lives": in practice it is often two layers below the
        // actions class, in a service that starts from the caller's own identity, and that service is
        // then the single line of defence with no framework backstop behind it.
        if (entityType is null)
        {
            if (actionsInstance is not ISparkOwnsRowSecurity { RowSecurityRationale.Length: > 0 })
            {
                throw new InvalidOperationException(
                    $"'{entityTypeDefinition.Name}' declares no clrType, so its rows are computed rather than " +
                    $"stored and the framework cannot filter or redact them — only the type-level Query right " +
                    $"applies. That is allowed, but it must be stated: make " +
                    $"'{entityTypeDefinition.Name}Actions' implement ISparkOwnsRowSecurity and use " +
                    $"RowSecurityRationale to say how it returns only rows this caller may see, naming the " +
                    $"file or service that does the scoping. Without the declaration, a type that forgot to " +
                    $"scope its rows is indistinguishable from one that deliberately owns the job.");
            }
        }

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
            Skip = skip,
            Take = take,
            Search = search,
            Columns = columnFilters,
        };

        object? result;
        if (methodInfo.AcceptsArgs)
        {
            result = methodInfo.Method.Invoke(actionsInstance, HookInvoke, binder: null, parameters: [args], culture: null);
        }
        else
        {
            result = methodInfo.Method.Invoke(actionsInstance, HookInvoke, binder: null, parameters: null, culture: null);
        }

        // Await async methods (Task<IEnumerable<T>>, Task<IQueryable<T>>, etc.)
        if (methodInfo.IsAsync && result is Task task)
        {
            await task;
            result = task.GetCompletedTaskResult();
        }

        if (result == null)
        {
            throw new InvalidOperationException(
                $"Custom query method '{methodName}' on '{actionsInstance.GetType().Name}' returned null. " +
                $"Return an empty sequence to say there are no rows — a null one is indistinguishable from " +
                $"that at the grid, and hides a method that fell through without returning.");
        }

        // The author's own page. Everything below that would filter, search, sort or trim is
        // already skipped for free — a SparkQueryPage is neither IQueryable nor Raven-queryable, so
        // projection, includes, search pushdown and provider sorting all no-op — except the
        // in-memory sort at the end, which this flag suppresses.
        //
        // Row security is NOT part of what the author takes over. The binary rule is about the five
        // presentation concerns (filter, search, sort, count, page); whether this caller may see a
        // row is a different question, and one an author cannot opt out of by choosing a return
        // type.
        var authorPage = result as ISparkQueryPage;

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
        if (isRavenQueryable && entityType is not null)
        {
            var includePaths = referenceResolver.ResolveIncludePaths(methodInfo.ResultElementType, entityType);
            if (includePaths.Count > 0)
            {
                result = referenceResolver.ApplyIncludes(result, methodInfo.ResultElementType, includePaths);
            }
        }

        // Push the row filter into the custom query too — a custom query says where rows come
        // from, not which of them this caller may see. No-op when the method yields projections.
        if (isQueryable && entityType is not null)
        {
            result = (await rowSecurity.ComposeRowFilterAsync(result, entityType, methodInfo.ResultElementType, "Query", cancellationToken)).Queryable;
        }

        // Column filters (#431) compose immediately after the row filter, exactly as on the database
        // branch and for the same reason: position is load-bearing, because an added clause must
        // never end up adjacent to the security predicate in a way that lets an operator leak onto
        // it. See the remarks on ApplyColumnFilters.
        //
        // Where it runs depends on what the author returned, and BOTH answers are correct. A Raven
        // queryable pushes the filter into RQL. A custom query that returns a fixed set — a computed
        // dashboard, a constant list, an API response — has no database to push into, so the same
        // predicate runs in process. That is not a degraded pushdown; it is the only thing filtering
        // a fixed set can mean, and refusing it would punish a legitimate, supported shape.
        //
        // What must never happen is the filter quietly disappearing, which is exactly what this
        // whole branch did before: every other refinement was wired here and this one was not.
        // An author's page is exempt, and it must be checked before the IEnumerable branch below:
        // SparkQueryPage<T> IS an IEnumerable<T>, so without this it would be narrowed in memory
        // while TotalItems stayed the author's — the framework filtering a page whose total it did
        // not compute. That is precisely the half-delegated failure the binary authority rule on
        // SparkQueryPage exists to prevent, and it fails invisibly: the grid shows fewer rows than
        // the pager claims and nothing says why. The author receives the filters through
        // CustomQueryArgs.Columns and honours them, exactly as they already do for Search.
        if (columnFilters is { Count: > 0 } && authorPage is null)
        {
            if (isQueryable)
            {
                result = ApplyColumnFilters(
                    result, methodInfo.ResultElementType, columnFilters, entityTypeDefinition, query);
            }
            else if (result is System.Collections.IEnumerable sequence)
            {
                // AsQueryable so the SAME expression evaluates over a fixed set as over a Raven
                // queryable. A second, hand-written in-memory implementation could disagree with the
                // first about what a value equals — comparing rendered text where the other compares
                // the raw property, say — and then the same filter would narrow differently depending
                // on a shape the caller cannot see. One predicate, one semantic, every shape.
                result = ApplyColumnFilters(
                    AsQueryable(sequence, methodInfo.ResultElementType), methodInfo.ResultElementType,
                    columnFilters, entityTypeDefinition, query);

                // isQueryable stays false on purpose. EnumerableQuery<T> is also IEnumerable, so
                // materialization still takes the sequence branch, and sorting keeps behaving as it
                // did for this shape. Filtering is the only thing that changes here.
            }
        }

        // Narrow to a selection before anything else touches the shape. Paging is deliberately not
        // applied when this is set — the caller asked for these rows, not for a page of them.
        if (restrictToIds is { Count: > 0 })
        {
            result = RestrictToIds(
                result, methodInfo.ResultElementType, restrictToIds, query, entityTypeDefinition, actionsInstance);
            isQueryable = result is IQueryable;
            isRavenQueryable = typeof(IRavenQueryable<>)
                .MakeGenericType(methodInfo.ResultElementType)
                .IsInstanceOfType(result);
        }

        // Only a RavenDB-backed queryable can push the search into the database; an in-memory
        // IQueryable has no Search, and the caller's own filtering may already have materialized.
        var searchPushedDown = false;
        if (searchTerm != null && isRavenQueryable)
        {
            (result, searchPushedDown) = ApplySearch(result, methodInfo.ResultElementType, searchTerm, ResolveIndexedSearchFields(result));
        }

        // Apply sorting if the result is IQueryable
        if (isQueryable && query.SortColumns.Length > 0)
        {
            result = ApplySorting(result, methodInfo.ResultElementType, query.SortColumns, entityTypeDefinition, query);
        }

        // Materialize results
        IEnumerable<object> entities;
        if (isRavenQueryable)
        {
            // Raven-backed: enumerate asynchronously. Reaching MaterializeQueryable with one of these
            // is what made Task<IRavenQueryable<T>> throw before #294 — a blocking ToList() over an
            // async session, which RavenDB rejects. Both branches now ask the object the same
            // question, so they can no longer disagree.
            entities = await ExecuteQueryableAsync(result, methodInfo.ResultElementType, cancellationToken);
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
            throw new InvalidOperationException(
                $"Custom query method '{methodName}' on '{actionsInstance.GetType().Name}' returned a " +
                $"'{result.GetType().Name}', which is not a sequence. It must return IQueryable<T>, " +
                $"IRavenQueryable<T>, IEnumerable<T> or SparkQueryPage<T> (or a Task<> of one of those).");
        }

        // Row-level authorization, as on the database path. A custom query is a developer saying
        // *where* rows come from; it is not permission to skip *whether* the caller may see them.
        //
        // ⚠️ SKIPPED ENTIRELY FOR A COMPOSED QUERY, and this is the one place that says so.
        // Not a policy choice and not a gap to close later: row security judges a *document* —
        // FilterAsync re-reads each row's collection type, re-evaluates the type's row rule against
        // the stored entity, and RedactAsync nulls the attributes this caller may not read. A
        // composed row is computed, not stored. There is no document to re-judge, no collection to
        // resolve a rule from, and no stored value to compare against; every one of those steps
        // would be asking a question about an object that never came from the database.
        //
        // What follows from that, and what an author of a composed query is therefore responsible
        // for: filtering rows to what the caller may see, omitting values the caller may not read,
        // and gating anything the rows can be acted upon with. The framework still enforces the
        // TYPE-level "Query" right above (that check is not row-shaped), and the per-row envelope
        // is forced closed below — but between those two, the actions class is the only thing
        // standing between a caller and every row it computes. Every composed query announces this
        // at startup for exactly that reason (see QueryLoader).
        var rawRows = entities as IReadOnlyList<object> ?? entities.ToList();

        // Nothing here squares the per-row envelope closed, and nothing needs to: M4 made a row a
        // projection, and QueryResultItem carries no `can` block at all — for exactly this reason.
        // A composed row names nothing that can be saved or deleted, and neither does any other
        // row; the affordance was removed from the shape rather than forced to false per path.

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
        // was S1 in #327. A duplicate id on a composed path is an authoring bug and throws in the
        // projector (QueryResultItem.Id is non-nullable and unique); it is never something to
        // quietly collapse.

        // Sorting has to happen somewhere. ApplySorting above runs only when the result is IQueryable,
        // so a method returning a plain IEnumerable silently ignored both the query's declared sort
        // columns and the caller's ?sortColumns= override. Sort the mapped rows instead — and do it
        // AFTER redaction, because ordering by a value the caller may not read is the same comparison
        // oracle ApplySorting's ShowedOn gate exists to close, and by now a protected value is null.
        //
        // Not when the author returned their own page: that sort authority went with it, and
        // reordering a page in memory would present a page-local ordering as a global one.
        var needsInMemorySort = !isQueryable && authorPage is null && query.SortColumns.Length > 0;

        var secured = await gate.ApplyAsync(rawRows, new RowSecurityContext
        {
            Session = session,
            Definition = entityTypeDefinition,
            EntityType = entityType,
            ResultType = entityType is null ? null : methodInfo.ResultElementType,
            Action = "Query",
            DedupeById = isRavenQueryable,
            OrderRows = needsInMemorySort
                ? rows => SortMappedRows(rows, query.SortColumns, entityTypeDefinition, query)
                : null,
            CancellationToken = cancellationToken,
        });

        return new QuerySourceResult(
            secured, entityTypeDefinition, searchPushedDown, authorPage?.TotalItems, args.DisabledActions, SortType: methodInfo.ResultElementType);
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
    private IEnumerable<PersistentObject> SortMappedRows(
        IEnumerable<PersistentObject> rows, SortColumn[] sortColumns, EntityTypeDefinition definition,
        SparkQuery? query)
    {
        IOrderedEnumerable<PersistentObject>? ordered = null;

        foreach (var col in sortColumns)
        {
            if (!IsSortableAttribute(definition, query, col.Property))
            {
                logger?.LogWarning(
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
        EntityTypeDefinition definition, SparkQuery? query)
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
            if (!IsSortableAttribute(definition, query, col.Property))
            {
                logger?.LogWarning(
                    $"Warning: sort column '{col.Property}' is not an attribute of {definition.Name}'s query " +
                    $"surface; the column is refused and rows keep their index order.");
                continue;
            }

            var propertyInfo = entityType.GetCachedProperty(ResolveSortProperty(entityType, col.Property));
            if (propertyInfo == null)
            {
                // Not an error: a model attribute can legitimately be absent from a narrower
                // projection. But dropping the column silently reads as broken ordering (#279).
                logger?.LogWarning(
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
    /// generator never emits <c>Exact</c> at all now — the one case that used to,
    /// <c>DateTimeOffset</c>, was measured to gain nothing from it — so this is reachable only from a
    /// hand-written index.
    /// </para>
    /// </summary>
    /// <summary>
    /// Which field names a search may name on <paramref name="queryable"/>, or <see langword="null"/>
    /// when there is nothing to restrict to.
    /// </summary>
    /// <remarks>
    /// Three answers, and the difference between the last two is the whole point:
    /// <list type="bullet">
    /// <item><b>null</b> — no static index is bound. A dynamic query's auto-index is derived from the
    /// query's own shape (RavenDB creates <c>Auto/X/ByNote</c> and <c>Auto/X/BySearch(Note)</c>
    /// separately), so it cannot name a field it does not index. Search unrestricted.</item>
    /// <item><b>a set of names</b> — a static index is bound and declares a <c>[FromIndex]</c>
    /// projection, whose properties are its field set. Search those, keep the pushdown.</item>
    /// <item><b>empty</b> — a static index is bound with no projection to describe it. The emitted
    /// field set then exists only inside the Map expression, as a rendered string, so it is genuinely
    /// unknowable here. Pushing down would be a guess that 500s when wrong; the empty set makes
    /// <see cref="ApplySearch"/> decline and the in-memory fallback take over. Six of the ten
    /// hand-written indexes in this repository are in this shape.</item>
    /// </list>
    /// <para>
    /// Read off the queryable rather than threaded from the caller so that both branches ask the same
    /// question of the same object — the custom branch has no index binding of its own, only whatever
    /// the author's method happened to return.
    /// </para>
    /// </remarks>
    private IReadOnlySet<string>? ResolveIndexedSearchFields(object queryable)
    {
        if (queryable is not IRavenQueryInspector inspector) return null;

        var indexName = inspector.IndexName;
        if (string.IsNullOrEmpty(indexName)) return null;

        // An auto-index is RavenDB's own, built per query shape; it is never in our catalog and never
        // rejects a field the query names.
        if (indexName.StartsWith("Auto/", StringComparison.Ordinal)) return null;

        // The catalog keys on the CLR class name while RavenDB reports the deployed name, which
        // replaces '_' with '/'. Unambiguous to reverse: a CLR type name cannot contain '/'.
        var entry = indexCatalog.GetByIndexName(indexName.Replace('/', '_'));
        if (entry?.ProjectionType is null) return EmptyFieldSet;

        return ReflectionCache.GetOrAdd<(string Op, Type Projection), IReadOnlySet<string>>(
            ("QueryExecutor.IndexedSearchFields", entry.ProjectionType),
            static k => k.Projection.GetCachedProperties()
                .Select(p => p.Name)
                .ToHashSet(StringComparer.Ordinal));
    }

    private static readonly IReadOnlySet<string> EmptyFieldSet = new HashSet<string>(StringComparer.Ordinal);

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
    private static (object Queryable, bool Applied) ApplySearch(
        object queryable, Type elementType, string term, IReadOnlySet<string>? indexedFields)
    {
        var properties = ResolveSearchableProperties(elementType);

        // Restricted to what the bound index actually declares. RavenDB rejects a query naming a
        // field a static index does not emit — "The field 'X' is not indexed in 'Idx', cannot
        // query/sort on fields that are not indexed" — and it rejects the WHOLE query, so one
        // unmapped field among ten takes the other nine down with it. That was a live 500 on every
        // search over an indexed query (#431 SP4).
        //
        // Null means there is nothing to restrict to: a dynamic query's auto-index is built from the
        // query's own shape, so it cannot name a field it does not index. An EMPTY set means a static
        // index is bound but its field list is unknowable, and the intersection below then empties
        // the property list, which reports Applied: false and hands the search to the in-memory
        // fallback. Slower, never wrong.
        if (indexedFields is not null)
            properties = [.. properties.Where(p => indexedFields.Contains(p.Name))];

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
    /// Narrows <paramref name="queryable"/> by the caller's per-column value filters (#431).
    /// </summary>
    /// <remarks>
    /// <b>Position is load-bearing.</b> This composes after the row-security predicate and before the
    /// search group, for the same reason the search group sits where it does: an added clause must
    /// never end up adjacent to the security filter in a way that lets an operator leak onto it. And
    /// it deliberately builds plain <c>Equal</c> comparisons rather than going through
    /// <c>LinqExtensions.Search</c> — no <c>SearchOptions</c> is constructed here at all, because a
    /// measured RavenDB behaviour lets an explicit one leak forward onto the adjacent clause, and the
    /// adjacent clause is the row filter.
    /// <para>
    /// <b>Refusals are silent</b>, matching the sort gate: a column that is off the query surface or
    /// resolves <c>canFilter: false</c> is skipped with a console warning and the rows come back
    /// unnarrowed. A distinguishable refusal would answer "does this column exist" for a caller who
    /// may not see it.
    /// </para>
    /// <para>
    /// <b>⚠️ A filter compares the stored value, which redaction does not hide.</b>
    /// <see cref="IRowSecurity.RedactAsync"/> nulls a protected attribute in the <em>response</em>;
    /// this comparison runs against the value in the database. So filtering
    /// <c>Salary Includes [100000]</c> returns the row with <c>Salary: null</c>, and its presence —
    /// and <c>TotalItems</c> — confirms the value. An equality oracle on something the caller may
    /// never read.
    /// <para>
    /// It is gated on <c>ShowedOn</c> and <c>canFilter</c> rather than on the redaction hook, for the
    /// reason the sort path already states: <c>GetProtectedAttributesAsync</c> takes an entity and may
    /// answer differently per row, so it cannot decide a query-level operation — by the time rows
    /// exist the filtering has already happened. The mitigation is therefore static while the hazard
    /// is dynamic, and that asymmetry cannot be closed here.
    /// </para>
    /// <para>
    /// <b>An app that protects an attribute per-row must also set <c>canFilter: false</c> on it</b>
    /// (and <c>canListDistincts: false</c>, which is a stronger disclosure again). Both default to
    /// <see langword="true"/>, so this is an obligation, not a default. See
    /// <c>docs/guide-authorization.md</c>.
    /// </para>
    /// <b>The property is resolved through the sort companion</b>, exactly as ordering is. A
    /// <c>[Search]</c>-analyzed field is indexed as separate lower-cased terms — <c>Volkswagen Golf
    /// GTI</c> becomes three — so an equality comparison against the display field matches nothing
    /// for any multi-word value, silently. <c>{Name}Sort</c> carries the un-analyzed single term and
    /// is what equality must target.
    /// </para>
    /// </remarks>
    private object ApplyColumnFilters(object queryable, Type sortType,
        IReadOnlyList<QueryColumnFilter> filters, EntityTypeDefinition definition, SparkQuery? query)
    {
        foreach (var filter in filters)
        {
            if (filter.IsEmpty) continue;

            var attribute = ColumnCapabilities.FindQuerySurfaceAttribute(definition, filter.Name);
            if (attribute is null || !ColumnCapabilities.CanFilter(attribute, query))
            {
                logger?.LogWarning(
                    $"Warning: filter column '{filter.Name}' is not a filterable attribute of " +
                    $"{definition.Name}'s query surface; the filter is refused and the rows are not narrowed.");
                continue;
            }

            var property = sortType.GetCachedProperty(ResolveSortProperty(sortType, attribute.Name));
            if (property is null)
            {
                // Same shape as the sort path: a model attribute can legitimately be absent from a
                // narrower projection, and dropping it silently reads as a broken filter.
                logger?.LogWarning(
                    $"Warning: filter column '{filter.Name}' has no property on {sortType.Name}; " +
                    $"the filter is skipped.");
                continue;
            }

            var parameter = Expression.Parameter(sortType, "x");
            var member = Expression.Property(parameter, property);

            Expression? predicate = null;
            var matchesNothing = false;

            if (filter.Includes is { Length: > 0 } includes)
            {
                predicate = AnyEquals(member, includes, property.PropertyType);

                // Not one requested value is representable on this column, so no stored value can
                // equal any of them. That is an empty result, NOT an absent filter — treating it as
                // absent is what returned the whole unnarrowed set for a nonsense value.
                matchesNothing = predicate is null;
            }

            if (!matchesNothing && filter.Excludes is { Length: > 0 } excludes)
            {
                // The mirror image: a value that cannot exist on this column excludes nothing, so an
                // empty chain here means "exclude nothing" rather than "exclude everything".
                if (AnyEquals(member, excludes, property.PropertyType) is { } any)
                {
                    var none = Expression.Not(any);
                    predicate = predicate is null ? none : Expression.AndAlso(predicate, none);
                }
            }

            if (matchesNothing) predicate = Impossible(member, property.PropertyType);

            if (predicate is null) continue;

            var lambda = Expression.Lambda(predicate, parameter);
            queryable = QueryableWhere(sortType).Invoke(null, [queryable, lambda])!;
        }

        return queryable;
    }

    /// <summary>
    /// A predicate on <paramref name="member"/> that no row can satisfy, for the case where not one
    /// requested value is representable on the column.
    /// </summary>
    /// <remarks>
    /// ⚠️ It has to be a real comparison. RavenDB rejects a constant predicate outright ("Constants
    /// expressions such as Where(x => true) are not allowed in the RavenDB queries"), and capping the
    /// queryable instead does not work either: <c>Take(0)</c> bounds the page but <b>not</b>
    /// <c>Count()</c>, so the rows vanish while <c>TotalItems</c> keeps reporting the unfiltered
    /// total. Measured — that is how this method came to exist.
    /// <para>
    /// A non-nullable column holds no nulls, so lifting it and comparing to null is already
    /// impossible. Anything that can hold a null needs the contradiction, because <c>== null</c> is a
    /// legitimate match there — which was the original defect.
    /// </para>
    /// </remarks>
    private static Expression Impossible(MemberExpression member, Type propertyType)
    {
        if (propertyType.IsValueType && Nullable.GetUnderlyingType(propertyType) is null)
        {
            var lifted = typeof(Nullable<>).MakeGenericType(propertyType);
            return Expression.Equal(
                Expression.Convert(member, lifted), Expression.Constant(null, lifted));
        }

        var isNull = Expression.Equal(member, Expression.Constant(null, propertyType));
        var isNotNull = Expression.NotEqual(member, Expression.Constant(null, propertyType));
        return Expression.AndAlso(isNull, isNotNull);
    }

    /// <summary>
    /// An OR-chain of equality comparisons — the "one of these values" half of a filter, or
    /// <see langword="null"/> when not one requested value can be represented on this column.
    /// </summary>
    /// <remarks>
    /// ⚠️ A <see langword="null"/> return means <b>"no row can match"</b>, not "no filter". The caller
    /// must narrow the result to nothing for an <c>includes</c> chain, and ignore it for an
    /// <c>excludes</c> chain. Conflating the two is what made an unconvertible value on a nullable
    /// column return the rows with <em>no</em> value — the exact complement of the request.
    /// </remarks>
    private static Expression? AnyEquals(MemberExpression member, object?[] values, Type propertyType)
    {
        // A collection column holds SEVERAL values per row, so "one of these values" has to mean the
        // collection CONTAINS one of them — never that the collection EQUALS one of them. RavenDB
        // indexes such a field as multi-valued terms, so Any(e => e == v) is meaningful and renders
        // as `Field = 'v'` in RQL.
        //
        // Comparing the collection itself is what shipped, and it failed silently: a scalar wire value
        // cannot be coerced onto string[], so the conversion answered null and the comparison became
        // `x.Flags == null` — matching nothing on a non-nullable column and, worse, exactly the rows
        // with NO value on a nullable one.
        if (SparkModelShape.GetCollectionElementType(propertyType) is { } elementType)
            return AnyContains(member, values, elementType);

        Expression? any = null;

        foreach (var raw in values)
        {
            // ⚠️ "Did not convert" and "the caller asked for null" are DIFFERENT, and collapsing them
            // was a disclosure oracle as well as a correctness bug. Measured on DemoApp's ECarStatus?
            // column, anonymously: a valid-but-absent member returned 0 rows while a non-member
            // returned 3 — the rows whose Status is null. So an unauthenticated caller could
            // enumerate a server-side enum by watching 0-vs-N, and every one of those 3 rows was
            // wrong besides. A value that does not convert is simply dropped here; the caller turns
            // an empty chain into an empty result.
            if (!TryConvertFilterValue(raw, propertyType, out var value))
                continue;

            var equals = Expression.Equal(member, Expression.Constant(value, propertyType));
            any = any is null ? equals : Expression.OrElse(any, equals);
        }

        return any;
    }

    /// <summary>
    /// The collection form of <see cref="AnyEquals"/>: the row matches when the collection holds any
    /// of the values. Values are coerced to the <paramref name="elementType"/>, not to the collection
    /// type — coercing to the collection is what produced <c>x.Flags == null</c>.
    /// </summary>
    private static Expression? AnyContains(MemberExpression member, object?[] values, Type elementType)
    {
        var element = Expression.Parameter(elementType, "e");
        Expression? any = null;

        foreach (var raw in values)
        {
            // Same rule as AnyEquals: an element value that does not convert is dropped, and an empty
            // chain means "no row can match" rather than "no filter".
            if (!TryConvertFilterValue(raw, elementType, out var value))
                continue;

            var equals = Expression.Equal(element, Expression.Constant(value, elementType));

            any = any is null ? equals : Expression.OrElse(any, equals);
        }

        if (any is null) return null;

        var contains = Expression.Call(
            typeof(Enumerable), nameof(Enumerable.Any), [elementType],
            member, Expression.Lambda(any, element));

        // A document that never wrote the field projects as a null collection, and Enumerable.Any
        // throws on it. That is not hypothetical here: a custom query is allowed to materialize with
        // ToList() and return the set, in which case this expression runs in-process over objects
        // rather than in RavenDB. The guard costs nothing on the database side, where an absent field
        // simply matches no term.
        return Expression.AndAlso(
            Expression.NotEqual(member, Expression.Constant(null, member.Type)), contains);
    }

    /// <summary>
    /// Coerces one wire value onto the property's CLR type, reporting whether it is representable
    /// there at all.
    /// </summary>
    /// <remarks>
    /// The body arrives through <c>System.Text.Json</c> as <see cref="JsonElement"/>, so every value
    /// needs converting before it can be compared.
    /// <para>
    /// ⚠️ <b>The boolean is the whole point of this method.</b> It returns <see langword="false"/> for
    /// a value that cannot exist on this column, and <see langword="true"/> with a
    /// <see langword="null"/> <paramref name="value"/> when the caller genuinely asked for "no value"
    /// — a real, selectable distinct. The previous signature returned <see langword="null"/> for both,
    /// and every caller then emitted <c>column == null</c>, so an unparseable value silently selected
    /// the rows with no value: the exact complement of the request on a nullable column, and an enum
    /// oracle on any column (a valid-but-absent member returns nothing, a non-member returns the
    /// nulls). A filter is caller input, so a malformed one must narrow to nothing rather than 500 —
    /// but "narrow to nothing" has to mean nothing, not "match the nulls".
    /// </para>
    /// <para>
    /// A <see langword="null"/> request is representable only where the column can hold one: a
    /// reference type or a <see cref="Nullable{T}"/>. Asking for null on a non-nullable value type is
    /// unrepresentable, so it returns <see langword="false"/> and the row set narrows to nothing,
    /// which is also what such a column would honestly answer.
    /// </para>
    /// </remarks>
    private static bool TryConvertFilterValue(object? raw, Type propertyType, out object? value)
    {
        value = null;
        var target = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        var nullable = !propertyType.IsValueType || Nullable.GetUnderlyingType(propertyType) is not null;

        if (raw is null) return nullable;

        try
        {
            if (raw is JsonElement element)
            {
                if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return nullable;
                if (target == typeof(string)) { value = element.ToString(); return true; }
                if (target == typeof(Guid))
                {
                    if (!element.TryGetGuid(out var g)) return false;
                    value = g;
                    return true;
                }
                if (target.IsEnum) { value = Enum.Parse(target, element.ToString(), ignoreCase: true); return true; }

                value = JsonSerializer.Deserialize(element.GetRawText(), target);

                // Deserialize answers null for a JSON literal that is well-formed but not a member of
                // the target type. That is a failure, not a request for null.
                return value is not null;
            }

            if (target.IsInstanceOfType(raw)) { value = raw; return true; }
            if (target.IsEnum) { value = Enum.Parse(target, raw.ToString() ?? "", ignoreCase: true); return true; }

            value = Convert.ChangeType(raw, target);
            return value is not null;
        }
        catch
        {
            value = null;
            return false;
        }
    }

    /// <summary>Applies <c>Skip</c>/<c>Take</c> to an untyped queryable (#431 M14).</summary>
    private static object ApplyPaging(object queryable, Type elementType, int skip, int take)
    {
        if (skip > 0)
            queryable = InvokeQueryableInt(nameof(Queryable.Skip), queryable, elementType, skip);

        return InvokeQueryableInt(nameof(Queryable.Take), queryable, elementType, take);
    }

    /// <summary>The <c>Skip</c>/<c>Take</c> shape: one source, one int.</summary>
    private static object InvokeQueryableInt(string name, object queryable, Type elementType, int value)
    {
        var method = ReflectionCache.GetOrAdd<(string Op, Type Entity), MethodInfo>(
            ($"QueryExecutor.Queryable{name}", elementType),
            k => typeof(Queryable).GetMethods()
                .First(m => m.Name == name
                    && m.GetParameters().Length == 2
                    && m.GetParameters()[1].ParameterType == typeof(int))
                .MakeGenericMethod(k.Entity));

        return method.Invoke(null, [queryable, value])!;
    }

    /// <summary>
    /// The database's count of matching rows, for the paging-pushdown path only.
    /// </summary>
    /// <remarks>
    /// A second round trip, deliberately. The alternative is RavenDB's query statistics, which would
    /// avoid it — but the statistics out-parameter has to be threaded through the same reflection that
    /// builds this queryable, and the win here is not the round trip: it is not materializing the
    /// whole collection to hand back fifty rows.
    /// <para>
    /// Counting <b>before</b> paging and only where nothing can remove rows afterwards is what keeps
    /// this from becoming the cardinality oracle an author-supplied total already was.
    /// </para>
    /// </remarks>
    private static async Task<int> CountQueryableAsync(object queryable, Type elementType, CancellationToken cancellationToken)
    {
        var method = ReflectionCache.GetOrAdd<(string Op, Type Entity), MethodInfo?>(
            ("QueryExecutor.LinqCountAsync", elementType),
            static k => typeof(LinqExtensions).GetMethods()
                .FirstOrDefault(m => m.Name == nameof(LinqExtensions.CountAsync)
                    && m.GetParameters().Length == 2)
                ?.MakeGenericMethod(k.Entity));

        if (method is null)
            throw new InvalidOperationException("RavenDB's CountAsync could not be resolved.");

        var task = (Task<int>)method.Invoke(null, [queryable, cancellationToken])!;
        return await task;
    }

    /// <summary>The open <c>Queryable.Where(source, predicate)</c> overload, closed over <paramref name="entityType"/>.</summary>
    /// <summary>
    /// Lifts a sequence to <see cref="IQueryable"/> so one expression can serve every shape.
    /// </summary>
    /// <remarks>
    /// Goes through <see cref="Enumerable.Cast{TResult}"/> first because a method may legitimately
    /// declare a non-generic <see cref="System.Collections.IEnumerable"/>, which
    /// <see cref="Queryable.AsQueryable{TElement}"/> cannot accept. Both steps are lazy, so nothing
    /// is materialized here — the filter still composes before enumeration.
    /// </remarks>
    private static object AsQueryable(System.Collections.IEnumerable sequence, Type elementType)
    {
        var cast = ReflectionCache.GetOrAdd<(string Op, Type Entity), MethodInfo>(
            ("QueryExecutor.EnumerableCast", elementType),
            static k => typeof(Enumerable).GetMethods()
                .First(m => m.Name == nameof(Enumerable.Cast) && m.IsGenericMethod)
                .MakeGenericMethod(k.Entity));

        var asQueryable = ReflectionCache.GetOrAdd<(string Op, Type Entity), MethodInfo>(
            ("QueryExecutor.AsQueryable", elementType),
            static k => typeof(Queryable).GetMethods()
                .First(m => m.Name == nameof(Queryable.AsQueryable)
                    && m.IsGenericMethod
                    && m.GetParameters().Length == 1)
                .MakeGenericMethod(k.Entity));

        return asQueryable.Invoke(null, [cast.Invoke(null, [sequence])!])!;
    }

    private static MethodInfo QueryableWhere(Type entityType)
        => ReflectionCache.GetOrAdd<(string Op, Type Entity), MethodInfo>(
            ("QueryExecutor.QueryableWhere", entityType),
            static k => typeof(Queryable).GetMethods()
                .First(m => m.Name == nameof(Queryable.Where)
                    && m.GetParameters().Length == 2
                    // Expression<Func<T,bool>>, not the indexed Expression<Func<T,int,bool>> overload.
                    && m.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2)
                .MakeGenericMethod(k.Entity));

    /// <summary>
    /// Whether <paramref name="requested"/> names an attribute the caller may order by: it must exist
    /// in the model, be part of the query surface, and — when the sort is caller-supplied — resolve
    /// <c>canSort</c> to true.
    /// </summary>
    /// <remarks>
    /// Two gates, and they are not the same kind of thing.
    /// <para>
    /// <c>ShowedOn.Query</c> is the authorization boundary and always applies: ordering by a field is
    /// a comparison oracle regardless of who asked.
    /// </para>
    /// <para>
    /// <c>canSort</c> (#431) is a capability the model author declares, and it gates the
    /// <b>caller</b>, not the model. A column the query declares its own default order by is exempt:
    /// the server chose that ordering. So <c>canSort: false</c> on a declared sort column yields a
    /// grid that arrives ordered by it and cannot be re-ordered by it — deliberate, and checked by
    /// <see cref="SparkQuery.SortColumnsAreCallerSupplied"/> rather than by comparing name lists,
    /// because <see cref="SparkQuery.WithSortColumns"/> has already replaced them by this point.
    /// </para>
    /// </remarks>
    private static bool IsSortableAttribute(EntityTypeDefinition definition, SparkQuery? query, string requested)
    {
        var attribute = ColumnCapabilities.FindQuerySurfaceAttribute(definition, requested);
        if (attribute is null) return false;

        // Model-declared order: the caller did not ask for this one.
        if (query is { SortColumnsAreCallerSupplied: false }) return true;

        return ColumnCapabilities.CanSort(attribute, query);
    }

    internal static string ResolveSortProperty(Type sortType, string requested)
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
    private async Task<IEnumerable<object>> ExecuteQueryableAsync(object queryable, Type entityType, CancellationToken cancellationToken)
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

        // Was hardcoded to CancellationToken.None, which is where a cancelled request stopped
        // mattering: the socket was gone, and the database kept materializing the result anyway.
        var task = genericToListMethod.Invoke(null, [queryable, cancellationToken]) as Task;

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
