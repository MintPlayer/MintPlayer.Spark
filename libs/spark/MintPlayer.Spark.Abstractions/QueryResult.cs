namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// What a query returns: the column metadata once, then one lightweight row per result.
/// </summary>
/// <remarks>
/// Rows used to be full <see cref="PersistentObject"/>s, which meant every row carried a complete
/// copy of the attribute metadata — label, dataType, rules, renderer options, and for an AsDetail
/// attribute the whole nested object graph — that the client already held from
/// <c>GET /spark/types</c> and never read off the row. Beyond the payload, it conflated two things
/// that are not the same: a <b>row is a projection</b>, and a <b>persistent object is a document</b>.
/// <para>
/// The separation is what makes composed rows safe. A row was never claimed to be a document, so
/// nothing is entitled to treat a posted row id as verified: every mutating path re-materializes
/// from the id through the same load path a detail page uses, and re-applies security there. That
/// is why row ids can be treated as hostile input without an integrity token on the wire.
/// </para>
/// </remarks>
public sealed class QueryResult
{
    /// <summary>The column metadata for every row in <see cref="Items"/>, sent once.</summary>
    public required IReadOnlyList<QueryColumn> Columns { get; init; }

    public required IReadOnlyList<QueryResultItem> Items { get; init; }

    /// <summary>Rows matching the query after filtering, before paging.</summary>
    public required int TotalItems { get; init; }

    public required int Skip { get; init; }
    public required int Take { get; init; }

    /// <summary>Presentation hints for the result as a whole. See <see cref="QueryResultItem"/>.</summary>
    public IReadOnlyDictionary<string, string>? TypeHints { get; init; }
}

/// <summary>
/// One column of a query result — the metadata the client needs to draw a cell, hoisted out of the
/// per-row attributes it used to be repeated on.
/// </summary>
public sealed class QueryColumn
{
    public required string Name { get; init; }

    /// <summary>
    /// Whether the grid should draw this column. <see langword="false"/> means <b>ship the value,
    /// do not draw it</b> — the row carries it for a renderer to read, and the grid skips it.
    /// </summary>
    /// <remarks>
    /// A presentation decision, so it is carried rather than applied server-side. What belongs on
    /// the query surface at all is <c>showedOn</c>, which is the flag the sort allow-list is checked
    /// against too — one rule, one place. Filtering here as well would make an attribute both
    /// sortable and column-less, and would leave an app no way to give a renderer a sibling value
    /// short of giving it a column.
    /// </remarks>
    public bool IsVisible { get; init; } = true;

    public TranslatedString? Label { get; init; }
    /// <summary>Help text for the column header's [i] tooltip (#348); carried from the attribute.</summary>
    public TranslatedString? Description { get; init; }
    public string DataType { get; init; } = "string";
    public int Order { get; init; }
    public bool IsArray { get; init; }
    public bool IsSortable { get; init; }

    /// <summary>The query backing a Reference column, by name — the client's option source.</summary>
    public string? Query { get; init; }

    public string? ReferenceType { get; init; }
    public string? LookupReferenceType { get; init; }
    public string? AsDetailType { get; init; }

    /// <summary>A registered custom renderer's name, and whatever configuration it declared.</summary>
    public string? Renderer { get; init; }
    public Dictionary<string, object>? RendererOptions { get; init; }

    /// <summary>Presentation hints for every cell in this column. See <see cref="QueryResultItem"/>.</summary>
    public IReadOnlyDictionary<string, string>? TypeHints { get; init; }
}

/// <summary>
/// One row: an id, a display string, and a value per column.
/// </summary>
/// <remarks>
/// Deliberately too weak to act on. A row carries no attribute metadata, no <c>can</c> block and no
/// etag, because none of those can be trusted from a projection — a computed row has no document
/// behind it to re-judge. Anything that mutates re-loads by <see cref="Id"/> first.
/// <para>
/// <b>Type hints</b> are an open, string-keyed presentation side-channel, merged column → item →
/// value with later winning. There is no registry and no validation, which is the point: an
/// application adds its own keys without a framework change. Keys are lower-cased once, here, so a
/// client never has to try two spellings.
/// </para>
/// </remarks>
public sealed class QueryResultItem
{
    /// <summary>
    /// The row's identity, never null and unique within a result — enforced when the result is
    /// built, because a null or duplicate id silently collapses a grid rather than failing.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// What to show when the row is named rather than tabulated — a reference picker's selected
    /// value, the first column's link text. Resolved server-side so those surfaces need no second
    /// fetch.
    /// </summary>
    public string? Breadcrumb { get; init; }

    public required IReadOnlyList<QueryResultItemValue> Values { get; init; }

    /// <summary>Presentation hints for the whole row (conditional colouring, an extra class).</summary>
    public IReadOnlyDictionary<string, string>? TypeHints { get; init; }
}

/// <summary>One cell.</summary>
public sealed class QueryResultItemValue
{
    /// <summary>The <see cref="QueryColumn.Name"/> this value belongs to.</summary>
    public required string Key { get; init; }

    /// <summary>
    /// The value, typed as JSON rather than stringified.
    /// </summary>
    /// <remarks>
    /// A deliberate divergence from the framework this design follows, which sends the whole wire as
    /// strings and re-types client-side from the column. Spark already converts to JSON-typed values
    /// on the way out, so stringifying would <em>add</em> machinery, lose date and number fidelity,
    /// and churn every renderer for nothing.
    /// </remarks>
    public object? Value { get; init; }

    /// <summary>For a reference cell: the target document's id, so a link needs no second lookup.</summary>
    public string? ObjectId { get; init; }

    /// <summary>For a reference cell: the target's display string. For an array reference, see
    /// <see cref="Breadcrumbs"/>.</summary>
    public string? Breadcrumb { get; init; }

    /// <summary>Resolved display labels per referenced id, for a multi-reference cell.</summary>
    public Dictionary<string, string?>? Breadcrumbs { get; init; }

    public IReadOnlyDictionary<string, string>? TypeHints { get; init; }
}
