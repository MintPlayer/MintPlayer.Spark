using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions.Csv;

/// <summary>
/// CSV import and export of persistent objects (warehouse WX11): any query exported to a file, and any file imported
/// into persistent objects through the same save path as <c>/spark/po/create</c> and <c>/spark/po/update</c> — the
/// caller's rights, the row gates, the attribute shield, validation, interceptors and approvals all apply per row.
/// </summary>
/// <remarks>
/// Served at <c>POST /spark/queries/export-csv</c> and <c>POST /spark/po/import-csv</c>; a package or host calls it
/// directly from a custom action (a warehouse "import locations" action, say) to add its own checks around it.
/// </remarks>
public interface ISparkCsvTransfer
{
    /// <summary>
    /// The query's rows as CSV, one column per query column (by attribute name, so the file imports back), with the
    /// row id in a leading <c>@id</c> column when <see cref="SparkCsvExportRequest.IncludeId"/>. <see langword="null"/>
    /// when the query does not exist or the caller may not read it (one answer).
    /// </summary>
    Task<SparkCsvFile?> ExportAsync(SparkCsvExportRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports the file's rows into <see cref="SparkCsvImportRequest.Type"/>, each row on its own (a refused row does
    /// not stop the others), and says per row what happened. A file the importer cannot use at all (unknown type,
    /// unknown columns, too many rows) is refused as a whole: <see cref="SparkCsvImportResult.Error"/>, no rows.
    /// </summary>
    /// <exception cref="Authorization.SparkAccessDeniedException">The caller may neither create nor edit the type.</exception>
    Task<SparkCsvImportResult> ImportAsync(SparkCsvImportRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The reserved column names of a Spark CSV file. Attribute names never start with <c>@</c>.</summary>
public static class SparkCsvColumns
{
    /// <summary>The document id: an update of that row. Empty, or absent, for a new row (or a natural-id upsert).</summary>
    public const string Id = "@id";

    /// <summary>The version the row was exported at: when given, an update of a row changed since is refused (409).</summary>
    public const string Etag = "@etag";
}

/// <summary>A generated file.</summary>
public sealed record SparkCsvFile(string FileName, string ContentType, byte[] Content, int Rows);

/// <summary>What to export: a query, filtered and sorted as the grid shows it, and how to write it.</summary>
public sealed class SparkCsvExportRequest
{
    /// <summary>The query's id or alias.</summary>
    public string? QueryId { get; set; }
    public string? Search { get; set; }
    public SortColumn[]? SortColumns { get; set; }
    public QueryColumnFilter[]? Columns { get; set; }
    public string? ParentId { get; set; }
    public string? ParentType { get; set; }
    public SparkDeletedFilter? Deleted { get; set; }
    public SparkDeletedFilter? ParentDeleted { get; set; }

    /// <summary>The field separator: <c>,</c> (default), <c>;</c> (Excel in comma-decimal locales such as Belgium) or tab.</summary>
    public char Delimiter { get; set; } = ',';

    /// <summary>Whether the file starts with the UTF-8 BOM (default: yes, so Excel reads accents right).</summary>
    public bool IncludeBom { get; set; } = true;

    /// <summary>Whether the file has leading <c>@id</c> and <c>@etag</c> columns, so editing it and importing it back updates the rows (default: yes).</summary>
    public bool IncludeId { get; set; } = true;
}

/// <summary>Whether an import may create rows, update rows, or both.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SparkCsvImportMode>))]
public enum SparkCsvImportMode
{
    /// <summary>A row naming an id (or whose natural id exists) updates it; any other row is created.</summary>
    Upsert,

    /// <summary>Every row is created; a row naming an id is refused.</summary>
    CreateOnly,

    /// <summary>Every row updates an existing row; a row that names none is refused.</summary>
    UpdateOnly,
}

/// <summary>What an empty cell means.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SparkCsvEmptyCells>))]
public enum SparkCsvEmptyCells
{
    /// <summary>An empty cell leaves the attribute as it is (a new row: its default). The safe default: a column cut short in a spreadsheet never wipes data.</summary>
    Ignore,

    /// <summary>An empty cell clears the attribute (sets it to null).</summary>
    Clear,
}

/// <summary>A CSV file to import into one entity type.</summary>
public sealed class SparkCsvImportRequest
{
    /// <summary>The entity type's id, name or alias.</summary>
    public string? Type { get; set; }

    /// <summary>The file's text (a leading BOM is skipped).</summary>
    public string? Csv { get; set; }

    /// <summary>The field separator; <see langword="null"/> detects <c>,</c>, <c>;</c> or tab from the header.</summary>
    public char? Delimiter { get; set; }

    /// <summary>
    /// Check every row exactly as the import would — rights, row gates, validation, interceptors' refusals — and write
    /// nothing. The result says what would happen.
    /// </summary>
    public bool DryRun { get; set; }

    public SparkCsvImportMode Mode { get; set; } = SparkCsvImportMode.Upsert;

    public SparkCsvEmptyCells EmptyCells { get; set; } = SparkCsvEmptyCells.Ignore;

    /// <summary>
    /// Header → attribute name, for a file whose headers are not the attribute names. A header mapped to
    /// <see langword="null"/> or an empty name is ignored. Unmapped headers must be attribute names (case-insensitive)
    /// or a reserved column (<see cref="SparkCsvColumns"/>). An unknown <c>@…</c> header refuses the file; any other
    /// unknown header refuses each row that has a value in it, with the answer an attribute the caller may not set gets
    /// (so the file learns nothing about hidden attributes), and a typo never silently drops a value.
    /// </summary>
    public Dictionary<string, string?>? Mapping { get; set; }
}

/// <summary>What happened to one row.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SparkCsvRowStatus>))]
public enum SparkCsvRowStatus
{
    /// <summary>Created (in a dry run: would be created).</summary>
    Created,

    /// <summary>Updated (in a dry run: would be updated).</summary>
    Updated,

    /// <summary>An <c>ISaveReplacement</c> diverted the save for approval: nothing is written yet.</summary>
    Submitted,

    /// <summary>An interceptor cancelled the save, or the row is empty: nothing written, nothing wrong.</summary>
    Skipped,

    /// <summary>Refused: see the errors. Nothing was written for this row.</summary>
    Refused,
}

/// <summary>One error of a refused row.</summary>
/// <param name="Attribute">The attribute (or reserved column) it concerns; <see langword="null"/> for the row as a whole.</param>
[SparkContract]
public sealed record SparkCsvRowError(string? Attribute, string Message);

/// <summary>The outcome of one data row.</summary>
/// <param name="Line">The 1-based record number (the header is record 1): the spreadsheet's row number, and the line number unless a quoted cell spans lines.</param>
/// <param name="Id">The row's id: the created or updated document, when known.</param>
[SparkContract]
public sealed record SparkCsvImportRow(int Line, SparkCsvRowStatus Status, string? Id, IReadOnlyList<SparkCsvRowError> Errors);

/// <summary>The outcome of an import.</summary>
/// <param name="Error">Why the whole file was refused (no row was looked at); <see langword="null"/> otherwise.</param>
[SparkContract]
public sealed record SparkCsvImportResult(
    bool DryRun,
    int Created,
    int Updated,
    int Submitted,
    int Skipped,
    int Refused,
    IReadOnlyList<SparkCsvImportRow> Rows,
    string? Error = null)
{
    public static SparkCsvImportResult FileRefused(bool dryRun, string error) => new(dryRun, 0, 0, 0, 0, 0, [], error);
}
