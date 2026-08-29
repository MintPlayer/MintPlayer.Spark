using MintPlayer.Spark.Abstractions;

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
    public static IReadOnlyList<QueryColumn> BuildColumns(EntityTypeDefinition definition)
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
                DataType = a.DataType,
                Order = a.Order,
                IsArray = a.IsArray,
                IsSortable = a.IsSortable ?? false,
                Query = a.Query,
                ReferenceType = a.ReferenceType,
                LookupReferenceType = a.LookupReferenceType,
                AsDetailType = a.AsDetailType,
                Renderer = a.Renderer,
                RendererOptions = a.RendererOptions,
            })];

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
    public static IReadOnlyList<QueryResultItem> ToItems(
        IEnumerable<PersistentObject> rows, IReadOnlyList<QueryColumn> columns, string queryName)
    {
        var items = new List<QueryResultItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
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
        // nested object graph on Object/Objects, which a projection deliberately does not carry.
        // Rather than render an empty cell where a count used to be, project the two facts a grid
        // can actually use: how many children (data, so the client owns the wording and its
        // pluralisation) and, for a single child, the breadcrumb the server already resolved.
        if (attribute is PersistentObjectAttributeAsDetail asDetail)
        {
            return new QueryResultItemValue
            {
                Key = column.Name,
                Value = column.IsArray ? asDetail.Objects?.Count ?? 0 : null,
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
