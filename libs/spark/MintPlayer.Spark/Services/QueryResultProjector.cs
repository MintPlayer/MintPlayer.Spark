using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Reflection;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Turns the mapped rows of a query into the wire shape: columns once, then id + values per row.
/// </summary>
/// <remarks>
/// The mapping pipeline still builds a <see cref="PersistentObject"/> per row internally, and this
/// projects it. That is deliberate rather than transitional: redaction
/// (<c>IRowSecurity.RedactAsync</c>), breadcrumb resolution and reference display all operate on
/// attribute-shaped rows, and re-expressing them over a second row type would mean two
/// implementations of the same security-relevant logic. Projecting at the boundary keeps one, and
/// the saving that mattered — the wire — is realised either way.
/// </remarks>
internal static class QueryResultProjector
{
    /// <summary>
    /// The columns of a query over <paramref name="definition"/>: the visible attributes flagged for
    /// the query surface, in declared order.
    /// </summary>
    /// <remarks>
    /// This rule used to live in the client (<c>visibleGridAttributes</c>), which meant the server
    /// sent every attribute of every row and the client decided what to draw. Moving it here is not
    /// only payload: <c>ShowedOn.Query</c> is what the sort-column allow-list is checked against, so
    /// server and client now derive the query surface from the same place.
    /// </remarks>
    /// <param name="query">
    /// The query being executed, for its sparse per-column capability overrides (#431). Null resolves
    /// every capability from the attribute alone, which is what a caller with no query in hand wants.
    /// </param>
    /// <param name="sortType">
    /// The type the query's rows are shaped by — an index's projection when one is bound, otherwise
    /// the entity. When supplied, a column with no property on it is reported as non-sortable,
    /// non-filterable and non-listable (#431 M7b).
    /// <para>
    /// This closes a gap between what the client is told and what the server will do. The executor
    /// already skips a sort or a filter on a column absent from the projection — with a warning, and
    /// correctly, since a projection may legitimately be narrower than the model — but the column
    /// metadata did not say so, so the grid drew a sort arrow and a filter cell that silently did
    /// nothing. The capability is a per-<em>query</em> fact, which is exactly why it cannot live on
    /// the model's per-attribute flags: one attribute can be backed by two indexes with different
    /// field sets, and this repository already has that case.
    /// </para>
    /// <para>
    /// Only ever narrows. An absent <paramref name="sortType"/>, or a column that resolves, leaves
    /// the model's answer untouched.
    /// </para>
    /// </param>
    public static IReadOnlyList<QueryColumn> BuildColumns(
        EntityTypeDefinition definition, SparkQuery? query = null, Type? sortType = null,
        IReadOnlySet<string>? indexedFields = null)
        => [.. definition.Attributes
            // ⚠️ ShowedOn ALONE decides what ships; IsVisible only decides what is drawn, and is
            // carried to the client rather than applied here.
            //
            // Two reasons. First, this is the same predicate the sort allow-list uses — filtering
            // on IsVisible here as well made an attribute marked `Query, isVisible:false`
            // *sortable with no column*, which is incoherent. Second, an app has legitimate reason
            // to ship a value it does not draw: a renderer showing a lock glyph beside a name needs
            // that row's IsPrivate without giving it a column of its own. Under the pre-#327 wire
            // every attribute rode along and that was free; narrowing on both flags took it away
            // with no way to ask for it back, since making it visible is the layout decision the
            // app was avoiding.
            //
            // No disclosure either way: rows used to carry EVERY attribute regardless of both
            // flags, so this is still strictly narrower than what shipped before. Per-caller
            // redaction is unaffected — it nulls values on the row, never on this definition.
            .Where(a => a.ShowedOn.HasFlag(EShowedOn.Query))
            .OrderBy(a => a.Order)
            .Select(a => new QueryColumn
            {
                Name = a.Name,
                IsVisible = a.IsVisible,
                Label = a.Label,
                Description = a.Description,
                DataType = a.DataType,
                Order = a.Order,
                IsArray = a.IsArray,
                CanSort = ColumnCapabilities.CanSort(a, query) && IsBackedByShape(a, sortType, indexedFields),
                CanFilter = ColumnCapabilities.CanFilter(a, query) && IsBackedByShape(a, sortType, indexedFields),
                CanListDistincts = ColumnCapabilities.CanListDistincts(a, query) && IsBackedByShape(a, sortType, indexedFields),
                Query = a.Query,
                ReferenceType = a.ReferenceType,
                LookupReferenceType = a.LookupReferenceType,
                AsDetailType = a.AsDetailType,
                Renderer = a.Renderer,
                RendererOptions = a.RendererOptions,
            })];

    /// <summary>
    /// Whether the query's row shape actually carries this attribute, resolved exactly as the sort
    /// and filter paths resolve it — through the <c>{Name}Sort</c> companion when one exists.
    /// </summary>
    /// <remarks>
    /// Deliberately the same lookup the enforcement sites use, rather than a second implementation of
    /// "is this column usable". Two implementations would be free to disagree, and the failure that
    /// causes is the one this is here to remove: the client being told one thing and the server doing
    /// another, silently.
    /// <para>
    /// True whenever there is no shape to check against, so this can only ever narrow.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// ⚠️ <paramref name="indexedFields"/> is the half the CLR shape cannot answer. A custom query may
    /// return <c>IRavenQueryable&lt;Commit&gt;</c> over a static index — <c>OfType&lt;T&gt;()</c> back
    /// to the documents is the idiomatic shape — and then the row type is the <b>entity</b>, which
    /// carries every property, while the index map carries only what it selected. The CLR check passes
    /// for a field the index never emits, the column ships sortable, and ordering by it is an
    /// <c>ArgumentException</c>: HTTP 500 for the whole grid.
    /// <para>
    /// Null means "no static index is in play, so nothing to restrict" — a dynamic index is built per
    /// query shape and can never reject a field the query names.
    /// </para>
    /// </remarks>
    private static bool IsBackedByShape(
        EntityAttributeDefinition attribute, Type? sortType, IReadOnlySet<string>? indexedFields)
    {
        if (sortType is null) return true;

        var resolved = QueryExecutor.ResolveSortProperty(sortType, attribute.Name);
        if (sortType.GetCachedProperty(resolved) is null) return false;

        return indexedFields is null || indexedFields.Contains(resolved);
    }

    /// <summary>
    /// Projects mapped rows onto <paramref name="columns"/>, in order.
    /// </summary>
    /// <param name="queryName">Named in the diagnostics below, because the author's question is
    /// always "which query?".</param>
    /// <exception cref="InvalidOperationException">
    /// A row has no id, or two rows share one. Both used to be silent: a null id collapsed the grid
    /// to a single row (<c>DistinctBy</c> treats every null key as equal), and duplicates collided
    /// in a client selection dictionary keyed by id. Neither is recoverable at runtime — a row the
    /// framework cannot name is a row nothing can be done with — so both are authoring errors.
    /// </exception>
    /// <param name="rows">
    /// Rows that have been through <c>RowSecurityGate</c>. The parameter type is the enforcement:
    /// only the gate can produce a <c>SecuredRows</c>, and every framework path that puts rows on
    /// the wire comes through here — so a new row-returning path cannot skip row security without
    /// changing this signature, which is a visible edit rather than a missing line.
    /// </param>
    public static IReadOnlyList<QueryResultItem> ToItems(
        RowSecurityGate.SecuredRows rows, IReadOnlyList<QueryColumn> columns, string queryName)
    {
        var items = new List<QueryResultItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows.Rows)
        {
            if (string.IsNullOrEmpty(row.Id))
            {
                throw new InvalidOperationException(
                    $"Query '{queryName}' produced a row with no id. Every row must be identifiable: the " +
                    $"grid links, selects and re-loads rows by id, and a row that cannot be named cannot be " +
                    $"acted on. Give the row type a readable 'Id' property — for an index projection, project " +
                    $"the source document's id into it.");
            }

            if (!seen.Add(row.Id))
            {
                throw new InvalidOperationException(
                    $"Query '{queryName}' produced two rows with the id '{row.Id}'. Row ids must be unique " +
                    $"within a result — client-side selection is keyed by id, so duplicates silently collide. " +
                    $"If the rows come from a fan-out index this is a framework bug; if they are computed, the " +
                    $"row type's identity is wrong.");
            }

            items.Add(new QueryResultItem
            {
                Id = row.Id,
                Breadcrumb = row.Breadcrumb ?? row.Name,
                Values = [.. columns.Select(column => ToValue(row, column))],
            });
        }

        return items;
    }

    private static QueryResultItemValue ToValue(PersistentObject row, QueryColumn column)
    {
        // Matched by name, as the client's cell lookup did. An attribute absent from this row —
        // projection-only, or dropped by redaction — yields an empty cell rather than a missing key,
        // so a renderer always receives a value object for every declared column.
        var attribute = row.Attributes
            .FirstOrDefault(a => string.Equals(a.Name, column.Name, StringComparison.Ordinal));

        if (attribute is null)
            return new QueryResultItemValue { Key = column.Name };

        // An AsDetail column has no flat value by construction — the mapper nulls it and puts the
        // nested object graph on Object/Objects. An ARRAY column projects to the one fact a grid can
        // use without carrying every child: how many (data, so the client owns the wording and its
        // pluralisation). A SINGLE child carries the child itself, because a custom renderer on such
        // a column is documented to receive the nested PersistentObject — the same value it gets on
        // a detail page — and nothing else can stand in for it. Sending null there (#329) painted
        // every such column blank with no error. A rendererless cell is unaffected: it prints
        // Breadcrumb below, which the client's cell pipe tests before it ever looks at Value.
        if (attribute is PersistentObjectAttributeAsDetail asDetail)
        {
            return new QueryResultItemValue
            {
                Key = column.Name,
                Value = column.IsArray ? asDetail.Objects?.Count ?? 0 : asDetail.Object,
                Breadcrumb = asDetail.Object?.Breadcrumb ?? asDetail.Breadcrumb,
            };
        }

        return new QueryResultItemValue
        {
            Key = column.Name,
            // A single Reference carries the target id as its value; surfacing it separately lets a
            // cell link without knowing that convention.
            Value = attribute.Value,
            ObjectId = column.ReferenceType is not null && !column.IsArray
                ? attribute.Value?.ToString()
                : null,
            Breadcrumb = attribute.Breadcrumb,
            Breadcrumbs = attribute.Breadcrumbs,
        };
    }
}
