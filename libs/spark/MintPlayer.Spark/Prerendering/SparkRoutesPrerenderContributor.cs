using System.Text;
using MintPlayer.Spark.Abstractions.Prerendering;

namespace MintPlayer.Spark.Prerendering;

/// <summary>
/// The requests every Spark page makes: the shell's (culture, translations, program units) and those of
/// the generic routes in <c>sparkRoutes()</c> (ng-spark/routes/src/spark-routes.ts). No per-entity code:
/// the routes name the type and id, which is the PRD's core insight (§2.4).
/// <para>
/// ⚠️ Each request here mirrors one a component sends; a change to the component's request (URL or
/// body) must change it here too, or the page misses it (logged as <c>[spark-ssr] miss</c>).
/// </para>
/// </summary>
internal sealed class SparkRoutesPrerenderContributor : ISparkPrerenderContributor
{
    private const string Api = "/spark";

    public Task ContributeAsync(ISparkPrerenderContext context)
    {
        var supplies = new List<Task>
        {
            // The shell: SparkLanguageService and the program units menu.
            context.GetAsync($"{Api}/culture"),
            context.GetAsync($"{Api}/translations"),
            context.GetAsync($"{Api}/program-units"),
        };

        var segments = Segments(context.Path);
        switch (segments)
        {
            // po/:type/new
            case ["po", _, "new"]:
                supplies.Add(context.GetAsync($"{Api}/types"));
                break;
            // po/:type/:id, po/:type/:id/edit, po/:type/:id/:slug
            case ["po", var type, var id, ..] when segments.Length <= 4:
                supplies.Add(SupplyObjectAsync(context, type, id));
                break;
            // po/:type
            case ["po", var type]:
                supplies.Add(SupplyTypeListAsync(context, type));
                break;
            // query/:queryId
            case ["query", var queryId]:
                supplies.Add(SupplyGridAsync(context, queryId));
                break;
        }
        return Task.WhenAll(supplies);
    }

    /// <summary>spark-po-detail.component.ts <c>load()</c>: the object and the types, then the type's rights and actions.</summary>
    /// <param name="primary">Whether the object is the page's primary read, whose status becomes the page's (D4); a board tile's is not.</param>
    internal static async Task SupplyObjectAsync(ISparkPrerenderContext context, string type, string id, bool primary = true)
    {
        var objectTask = context.PostAsync($"{Api}/po/load", new { objectTypeId = type, id });
        var typesTask = context.GetAsync($"{Api}/types");

        // The page's status is the object's (D4): a 401 for an anonymous visitor and a 404 for a signed-in
        // one, whether the object is missing or denied.
        var item = await objectTask;
        if (primary)
            context.SetPageStatus(item.Status);

        var types = (await typesTask).Read<List<EntityTypeReference>>() ?? [];
        var entityType = types.FirstOrDefault(t => t.Id == type || t.Alias == type);
        if (entityType?.Id is null || !item.IsSuccess)
            return;

        await Task.WhenAll(
            context.GetAsync($"{Api}/permissions/{EncodeUriComponent(entityType.Id)}"),
            context.PostAsync($"{Api}/actions/list", new { objectTypeId = entityType.Id }),
            SupplyLookupsAsync(context, entityType),
            SupplyAsDetailReferencesAsync(context, types, entityType, type, id));
    }

    /// <summary>
    /// <c>loadLookupReferenceOptions()</c>: one lookup per name. The page asks only for the attributes it
    /// shows; supplying the others too costs server time only, since only a requested response reaches the
    /// browser (TransferState records what the page asked for).
    /// </summary>
    private static Task SupplyLookupsAsync(ISparkPrerenderContext context, EntityTypeReference entityType)
        => Task.WhenAll((entityType.Attributes ?? [])
            .Select(a => a.LookupReferenceType)
            .OfType<string>()
            .Distinct()
            .Select(name => context.GetAsync($"{Api}/lookupref/{EncodeUriComponent(name)}")));

    /// <summary>
    /// <c>loadAsDetailTypes()</c>: each AsDetail array's reference columns run their query by name, with the
    /// page's object as the parent. By name means the query catalogue first (<c>getQueryByName</c>).
    /// </summary>
    private static async Task SupplyAsDetailReferencesAsync(ISparkPrerenderContext context, List<EntityTypeReference> types, EntityTypeReference entityType, string type, string id)
    {
        var queryNames = (entityType.Attributes ?? [])
            .Where(a => a.DataType == "AsDetail" && a.IsArray && a.AsDetailType is not null)
            .Select(a => types.FirstOrDefault(t => t.ClrType == a.AsDetailType)
                ?? (entityType.DetailTypes ?? []).FirstOrDefault(t => t.ClrType == a.AsDetailType))
            .OfType<EntityTypeReference>()
            .SelectMany(t => (t.Attributes ?? []).Where(a => a.DataType == "Reference" && a.Query is not null).Select(a => a.Query!))
            .Distinct()
            .ToList();
        if (queryNames.Count == 0)
            return;

        var queries = (await context.GetAsync($"{Api}/queries")).Read<List<QueryReference>>() ?? [];
        await Task.WhenAll(queryNames
            .Select(name => queries.FirstOrDefault(q => q.Name == name)?.Id)
            .OfType<string>()
            .Select(queryId => context.PostAsync($"{Api}/queries/execute", new { queryId, parentId = id, parentType = type })));
    }

    /// <summary>
    /// spark-query-list.component.ts on <c>po/:type</c>: the type's list query, found as the page finds it,
    /// then that grid. No list query is a 404, as the page reports it.
    /// </summary>
    private static async Task SupplyTypeListAsync(ISparkPrerenderContext context, string type)
    {
        var typesTask = context.GetAsync($"{Api}/types");
        var queries = (await context.GetAsync($"{Api}/queries")).Read<List<QueryReference>>() ?? [];
        var entityType = ((await typesTask).Read<List<EntityTypeReference>>() ?? [])
            .FirstOrDefault(t => t.Id == type || t.Alias == type);
        var match = entityType?.Name is null ? null : queries.FirstOrDefault(q =>
        {
            if (q.EntityType == entityType.Name) return true;
            var source = q.Source ?? "";
            var sourceName = source.Contains('.') ? source[(source.IndexOf('.') + 1)..] : source;
            return sourceName == entityType.Name || sourceName == entityType.Name + "s";
        });
        if (match is null)
        {
            context.SetPageStatus(StatusCodes.Status404NotFound);
            return;
        }
        await SupplyGridAsync(context, string.IsNullOrEmpty(match.Alias) ? match.Id! : match.Alias);
    }

    /// <summary>
    /// spark-query-grid.component.ts <c>loadQuery()</c> for a root grid: the query and the types, the type's
    /// rights and actions, then the first page as <c>executeQuery</c> sends it. The query's status is the
    /// page's (D4).
    /// </summary>
    /// <param name="primary">Whether the query is the page's primary read, whose status becomes the page's (D4); a board tile's is not.</param>
    internal static async Task SupplyGridAsync(ISparkPrerenderContext context, string queryId, bool primary = true)
    {
        var queryTask = context.PostAsync($"{Api}/queries/get", new { queryId });
        var typesTask = context.GetAsync($"{Api}/types");
        var response = await queryTask;
        if (primary)
            context.SetPageStatus(response.Status);
        var query = response.Read<QueryReference>();
        if (query?.Id is null)
            return;

        var supplies = new List<Task>();
        var entityType = ResolveEntityType(query, (await typesTask).Read<List<EntityTypeReference>>() ?? []);
        if (entityType?.Id is not null)
        {
            supplies.Add(context.GetAsync($"{Api}/permissions/{EncodeUriComponent(entityType.Id)}"));
            supplies.Add(context.PostAsync($"{Api}/actions/list", new { objectTypeId = entityType.Id }));
            supplies.Add(SupplyLookupsAsync(context, entityType));
        }

        // A live grid fetches over the socket, which opens in the browser only (D9), and a virtual-scrolling one
        // fetches in blocks of its own size: both render their rows after hydration.
        if (query.Live != true && query.RenderMode != "VirtualScrolling")
        {
            // initialGridSettings(): page 1 of the first page size, the query's own sort. A root grid's parent
            // is "" (an input default), which the body keeps; an empty filter list is left out.
            var sortColumns = query.SortColumns is { Count: > 0 } sorts
                ? sorts.Select(s => new { property = s.Property, direction = s.Direction == "desc" ? "desc" : "asc" }).ToList()
                : null;
            supplies.Add(context.PostAsync($"{Api}/queries/execute", new
            {
                queryId = query.Id,
                sortColumns,
                skip = 0,
                take = FirstPageSize,
                parentId = "",
                parentType = "",
            }));
        }
        await Task.WhenAll(supplies);
    }

    /// <summary><c>SPARK_GRID_PAGE_SIZES[0]</c> (grid/src/spark-grid-columns.ts).</summary>
    private const int FirstPageSize = 10;

    /// <summary>The grid's <c>resolveEntityType()</c>: the declared type, else the type the source names.</summary>
    private static EntityTypeReference? ResolveEntityType(QueryReference query, List<EntityTypeReference> types)
    {
        if (query.EntityType is { } declared)
            return types.FirstOrDefault(t => t.Name == declared || t.Alias == declared.ToLowerInvariant());
        var source = query.Source ?? "";
        var sourceName = source.Contains('.') ? source[(source.IndexOf('.') + 1)..] : source;
        return types.FirstOrDefault(t => t.Name == sourceName || t.Name + "s" == sourceName
            || (sourceName.EndsWith('s') && (t.Name == sourceName[..^1] || t.ClrType?.EndsWith(sourceName[..^1]) == true)));
    }

    /// <summary>The path's segments, each decoded once (a route parameter can hold an encoded <c>/</c>).</summary>
    internal static string[] Segments(PathString path)
        => (path.Value ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();

    /// <summary>JavaScript's <c>encodeURIComponent</c>, which the page's requests use: it leaves <c>!'()*</c> as they are.</summary>
    internal static string EncodeUriComponent(string value)
    {
        var escaped = new StringBuilder(Uri.EscapeDataString(value));
        return escaped.Replace("%21", "!").Replace("%27", "'").Replace("%28", "(").Replace("%29", ")").Replace("%2A", "*").ToString();
    }

    // The members of GET /spark/types the predictions need.
    private sealed class EntityTypeReference
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Alias { get; set; }
        public string? ClrType { get; set; }
        public List<AttributeReference>? Attributes { get; set; }
        public List<EntityTypeReference>? DetailTypes { get; set; }
    }

    private sealed class AttributeReference
    {
        public string? DataType { get; set; }
        public bool IsArray { get; set; }
        public string? AsDetailType { get; set; }
        public string? Query { get; set; }
        public string? LookupReferenceType { get; set; }
    }

    // The members of a query (GET /spark/queries, POST /spark/queries/get) the predictions need.
    private sealed class QueryReference
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Alias { get; set; }
        public string? EntityType { get; set; }
        public string? Source { get; set; }
        public string? RenderMode { get; set; }
        public bool? Live { get; set; }
        public List<SortReference>? SortColumns { get; set; }
    }

    private sealed class SortReference
    {
        public string? Property { get; set; }
        public string? Direction { get; set; }
    }
}
