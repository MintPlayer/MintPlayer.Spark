using System.Text.Json;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Csv;
using MintPlayer.Spark.Endpoints.PersistentObject;
using MintPlayer.Spark.Endpoints.Queries;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;
using Raven.Client.Documents.Session;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace MintPlayer.Spark.Csv;

/// <summary>
/// The CSV import and export of persistent objects (warehouse WX11). Export pages through the query read path, so the
/// file holds exactly the rows and columns the grid shows the caller. Import builds each row as the web client posts it
/// (the scaffold of <c>/spark/po/new</c>, or the row as <c>/spark/po/load</c> presents it) and saves it through
/// <see cref="IPersistentObjectSavePath"/>, the sequence <c>/spark/po/create</c>, <c>/spark/po/update</c> and the MCP
/// tools share — never a raw session write.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which row a record writes</b>: a non-empty <c>@id</c> updates that row; otherwise, for an <c>IHasNaturalId</c>
/// type, the id the row's values derive updates the row holding it when there is one (upsert); otherwise the record
/// creates a row. Two records of one file that write the same row refuse the second, so the result never depends on
/// the order of two edits.
/// </para>
/// <para>
/// <b>Rows are independent</b>: each commits on its own (with the index wait every HTTP save has), so a refused row
/// leaves the others written. A dry run answers row by row what the import would do, through the same checks
/// (<see cref="IPersistentObjectSavePath.CheckCreateAsync"/>, <see cref="IPersistentObjectSavePath.CheckUpdateAsync"/>).
/// </para>
/// <para>
/// <b>What a refusal says</b>: a row that does not exist and a row the caller may not see get one answer, as do an
/// unknown column and an attribute the caller may not set (#453, MCP D19) — the file learns nothing the grid would not
/// show. A column the caller may not set is ignored where the cell is empty or holds the current value, so a file
/// exported with read-only columns imports back.
/// </para>
/// </remarks>
[Register(typeof(ISparkCsvTransfer), ServiceLifetime.Scoped)]
internal sealed class SparkCsvTransfer : ISparkCsvTransfer
{
    /// <summary>The most data rows one import takes: every row is a save with its index wait.</summary>
    internal const int MaxImportRows = 5000;

    /// <summary>The most rows one export writes; a larger query is refused, never cut short silently.</summary>
    internal const int MaxExportRows = 100_000;

    private const string NotSettable = "'{0}' is not an attribute you can set on {1}.";
    private const string NoSuchRow = "No such row, or you may not see it.";

    private readonly IModelLoader modelLoader;
    private readonly IQueryReadPath queryReadPath;
    private readonly IPersistentObjectReadPath objectReadPath;
    private readonly IPersistentObjectSavePath savePath;
    private readonly IPermissionService permissionService;
    private readonly IEntityMapper entityMapper;
    private readonly INewInvoker newInvoker;
    private readonly ISparkTypeResolver typeResolver;
    private readonly IAsyncDocumentSession session;
    private readonly IOptions<HttpJsonOptions> jsonOptions;

    public SparkCsvTransfer(
        IModelLoader modelLoader,
        IQueryReadPath queryReadPath,
        IPersistentObjectReadPath objectReadPath,
        IPersistentObjectSavePath savePath,
        IPermissionService permissionService,
        IEntityMapper entityMapper,
        INewInvoker newInvoker,
        ISparkTypeResolver typeResolver,
        IAsyncDocumentSession session,
        IOptions<HttpJsonOptions> jsonOptions)
    {
        this.modelLoader = modelLoader;
        this.queryReadPath = queryReadPath;
        this.objectReadPath = objectReadPath;
        this.savePath = savePath;
        this.permissionService = permissionService;
        this.entityMapper = entityMapper;
        this.newInvoker = newInvoker;
        this.typeResolver = typeResolver;
        this.session = session;
        this.jsonOptions = jsonOptions;
    }

    // ---------------------------------------------------------------- export

    public async Task<SparkCsvFile?> ExportAsync(SparkCsvExportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.QueryId))
            return null;

        var writer = new SparkCsvWriter(request.Delimiter, request.IncludeBom);
        List<QueryColumn>? columns = null;
        var rows = 0;
        var skip = 0;
        while (true)
        {
            var outcome = await queryReadPath.ExecuteAsync(new ExecuteQueryRequest
            {
                QueryId = request.QueryId,
                Search = request.Search,
                SortColumns = request.SortColumns,
                Columns = request.Columns,
                ParentId = request.ParentId,
                ParentType = request.ParentType,
                Deleted = request.Deleted,
                ParentDeleted = request.ParentDeleted,
                Skip = skip,
                Take = QueryReadPath.MaxTake,
            }, cancellationToken);

            if (outcome.Result is not { } result)
            {
                // The first page answers whether the caller may read the query at all (one answer for "no such
                // query"); a later page failing would mean a cut-short file, which is never returned.
                if (columns is null && outcome.StatusCode == StatusCodes.Status404NotFound)
                    return null;
                throw new SparkValidationException(outcome.Error ?? "The query could not be exported.");
            }

            if (columns is null)
            {
                if (result.TotalItems > MaxExportRows)
                    throw new SparkValidationException($"The query has {result.TotalItems} rows; an export holds at most {MaxExportRows}. Filter it first.");
                RaiseRequestBudget(result.TotalItems / QueryReadPath.MaxTake + 1, perUnit: 8);

                columns = [.. result.Columns.OrderBy(c => c.Order)];
                IEnumerable<string> header = columns.Select(c => c.Name);
                if (request.IncludeId)
                    header = new[] { SparkCsvColumns.Id, SparkCsvColumns.Etag }.Concat(header);
                writer.Header(header);
            }

            foreach (var item in result.Items)
            {
                var cells = new List<object?>(columns.Count + 2);
                if (request.IncludeId)
                {
                    cells.Add(item.Id);
                    cells.Add(item.Etag);
                }
                foreach (var column in columns)
                    cells.Add(ExportValue(column, item.Values.FirstOrDefault(v => v.Key == column.Name)));
                writer.Row(cells);
                rows++;
            }

            skip += result.Items.Count;
            if (result.Items.Count == 0 || skip >= result.TotalItems)
                break;
            if (rows >= MaxExportRows)
                throw new SparkValidationException($"The query has more than {MaxExportRows} rows; filter it first.");
        }

        return new SparkCsvFile(FileName(request.QueryId), "text/csv; charset=utf-8", writer.ToBytes(), rows);
    }

    /// <summary>A reference cell exports the target's id (what an import reads back), never its display text.</summary>
    private static object? ExportValue(QueryColumn column, QueryResultItemValue? value)
    {
        if (value is null)
            return null;
        if (string.Equals(column.DataType, "Reference", StringComparison.Ordinal) && !column.IsArray && value.ObjectId is { Length: > 0 } id)
            return id;
        return value.Value;
    }

    internal static string FileName(string queryId)
    {
        var safe = string.Concat(queryId.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')).Trim('-');
        return (safe.Length == 0 ? "export" : safe.Length > 80 ? safe[..80] : safe) + ".csv";
    }

    // ---------------------------------------------------------------- import

    public async Task<SparkCsvImportResult> ImportAsync(SparkCsvImportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dryRun = request.DryRun;

        // An unknown type and a type the caller may neither create nor edit are one answer: the denial.
        var entityType = string.IsNullOrWhiteSpace(request.Type)
            ? null
            : modelLoader.ResolveEntityType(request.Type) ?? modelLoader.GetEntityTypeByName(request.Type);
        if (entityType is null)
            throw new SparkAccessDeniedException("csv-import");

        var mayCreate = request.Mode != SparkCsvImportMode.UpdateOnly
            && await permissionService.IsAllowedAsync(SparkCoreActions.New, entityType.Name, cancellationToken);
        var mayEdit = request.Mode != SparkCsvImportMode.CreateOnly
            && await permissionService.IsAllowedAsync(SparkCoreActions.Edit, entityType.Name, cancellationToken);
        if (!mayCreate && !mayEdit)
            throw new SparkAccessDeniedException(entityType.Name);

        if (string.IsNullOrEmpty(request.Csv))
            return SparkCsvImportResult.FileRefused(dryRun, "The file is empty.");

        List<List<string>> records;
        try
        {
            records = SparkCsv.Parse(request.Csv, request.Delimiter);
        }
        catch (ArgumentException ex)
        {
            return SparkCsvImportResult.FileRefused(dryRun, ex.Message);
        }
        if (records.Count == 0 || SparkCsv.IsBlank(records[0]))
            return SparkCsvImportResult.FileRefused(dryRun, "The file has no header.");

        var dataRows = records.Skip(1).Count(r => !SparkCsv.IsBlank(r));
        if (dataRows > MaxImportRows)
            return SparkCsvImportResult.FileRefused(dryRun, $"The file has {dataRows} rows; an import takes at most {MaxImportRows}. Split it.");

        var (plan, planError) = CsvImportPlan.Build(records[0], request.Mapping);
        if (plan is null)
            return SparkCsvImportResult.FileRefused(dryRun, planError!);

        var clrType = typeResolver.Resolve(entityType.ClrType);
        var hasNaturalId = clrType is not null && typeof(IHasNaturalId).IsAssignableFrom(clrType);
        RaiseRequestBudget(dataRows, perUnit: 12);

        var context = new ImportContext(entityType, clrType, hasNaturalId, mayCreate, mayEdit, request, plan);
        var results = new List<SparkCsvImportRow>(dataRows);
        for (var index = 1; index < records.Count; index++)
        {
            var record = records[index];
            if (SparkCsv.IsBlank(record))
                continue;
            var line = index + 1;
            SparkCsvImportRow row;
            try
            {
                row = await ImportRowAsync(context, record, line, cancellationToken);
            }
            catch (SparkAccessDeniedException)
            {
                // A type-level or row-level denial, at any step: the same answer as a row that is not there.
                row = Refused(line, null, NoSuchRow);
            }
            catch (SparkConcurrencyException ex)
            {
                row = Refused(line, null, ex.Reason == SparkConcurrencyException.Deleted
                    ? "The row was deleted since it was exported."
                    : ex.UserMessage ?? "The row changed since it was exported, or a row with this key already exists.");
            }
            catch (SparkSaveValidationException ex)
            {
                row = new SparkCsvImportRow(line, SparkCsvRowStatus.Refused, null,
                    [.. ex.Result.Errors.Select(e => new SparkCsvRowError(e.AttributeName, e.ErrorMessage.ToString()))]);
            }
            catch (SparkValidationException ex)
            {
                row = Refused(line, ex.AttributeName, ex.Message);
            }
            catch (SparkActionDisabledException ex)
            {
                row = Refused(line, null, ex.Message);
            }
            catch (SparkThrottledException ex)
            {
                row = Refused(line, null, ex.Message);
            }
            catch (SparkRetryActionException)
            {
                // A save hook that asks the user a question cannot be answered in a bulk import.
                row = Refused(line, null, "Saving this row asks a question; save it on its own.");
            }
            results.Add(row);
        }

        return new SparkCsvImportResult(
            dryRun,
            Created: results.Count(r => r.Status == SparkCsvRowStatus.Created),
            Updated: results.Count(r => r.Status == SparkCsvRowStatus.Updated),
            Submitted: results.Count(r => r.Status == SparkCsvRowStatus.Submitted),
            Skipped: results.Count(r => r.Status == SparkCsvRowStatus.Skipped),
            Refused: results.Count(r => r.Status == SparkCsvRowStatus.Refused),
            Rows: results);
    }

    private sealed record ImportContext(
        EntityTypeDefinition EntityType,
        Type? ClrType,
        bool HasNaturalId,
        bool MayCreate,
        bool MayEdit,
        SparkCsvImportRequest Request,
        CsvImportPlan Plan)
    {
        /// <summary>The rows this file already wrote (or, in a dry run, would write): id → record number.</summary>
        public Dictionary<string, int> Targets { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<SparkCsvImportRow> ImportRowAsync(ImportContext context, List<string> record, int line, CancellationToken cancellationToken)
    {
        var plan = context.Plan;
        var entityType = context.EntityType;
        if (record.Count > plan.Width && record.Skip(plan.Width).Any(c => c.Length > 0))
            return Refused(line, null, "The row has more cells than the header.");

        string Cell(int? column) => column is { } i && i < record.Count ? record[i] : "";
        var id = SparkCsv.Unguard(Cell(plan.IdColumn).Trim());
        var etag = Cell(plan.EtagColumn).Trim();

        PersistentObject obj;
        string? targetId = null;
        if (id.Length > 0)
        {
            if (!context.MayEdit)
                return Refused(line, SparkCsvColumns.Id, context.Request.Mode == SparkCsvImportMode.CreateOnly
                    ? "This import only creates rows, and the row names an id."
                    : NoSuchRow);
            if (await LoadAsync(entityType, id, cancellationToken) is not { } loaded)
                return Refused(line, SparkCsvColumns.Id, NoSuchRow);
            obj = RoundTrip(loaded);
            obj.Etag = etag.Length > 0 ? etag : loaded.Etag;
            targetId = id;
        }
        else
        {
            obj = await ScaffoldAsync(context, cancellationToken);
            if (context.HasNaturalId)
            {
                // The id the row's values derive (the probe the create itself makes, DatabaseAccess H2): a row
                // already holding it is updated rather than refused as "already exists".
                var probeErrors = Apply(context, obj, record, isNew: true, out _);
                if (probeErrors.Count > 0)
                    return new SparkCsvImportRow(line, SparkCsvRowStatus.Refused, null, probeErrors);

                var derived = (entityMapper.ToEntity(obj) as IHasNaturalId)?.GetId();
                if (!string.IsNullOrEmpty(derived) && context.MayEdit && await LoadAsync(entityType, derived, cancellationToken) is { } held)
                {
                    obj = RoundTrip(held);
                    obj.Etag = etag.Length > 0 ? etag : held.Etag;
                    targetId = derived;
                }
                else if (!string.IsNullOrEmpty(derived))
                {
                    // A create; its key still counts against a second record of the same key.
                    if (context.Targets.TryGetValue(derived, out var earlierCreate))
                        return Refused(line, null, $"Row {earlierCreate} of this file already writes this row.");
                    context.Targets[derived] = line;
                }
            }

            if (targetId is null && !context.MayCreate)
                return Refused(line, null, context.Request.Mode == SparkCsvImportMode.UpdateOnly
                    ? "This import only updates rows, and the row names none that exists."
                    : NoSuchRow);
        }

        if (targetId is not null)
        {
            if (context.Targets.TryGetValue(targetId, out var earlier))
                return Refused(line, null, $"Row {earlier} of this file already writes this row.");
            context.Targets[targetId] = line;
        }

        var isNew = targetId is null;
        var errors = isNew && context.HasNaturalId
            ? []                                   // applied above, for the probe
            : Apply(context, obj, record, isNew, out _);
        if (errors.Count > 0)
            return new SparkCsvImportRow(line, SparkCsvRowStatus.Refused, targetId, errors);

        if (!isNew && !obj.Attributes.Any(a => a.IsValueChanged))
            return new SparkCsvImportRow(line, SparkCsvRowStatus.Skipped, targetId, []);

        if (context.Request.DryRun)
        {
            if (isNew)
                await savePath.CheckCreateAsync(entityType, obj, cancellationToken);
            else
                await savePath.CheckUpdateAsync(entityType, targetId!, obj, cancellationToken);
            return new SparkCsvImportRow(line, isNew ? SparkCsvRowStatus.Created : SparkCsvRowStatus.Updated, targetId, []);
        }

        var outcome = isNew
            ? await savePath.CreateAsync(entityType, obj, parent: null, cancellationToken)
            : await savePath.UpdateAsync(entityType, targetId!, obj, retry: null, cancellationToken);
        return outcome.Kind switch
        {
            SaveOutcomeKind.Saved => new SparkCsvImportRow(line, isNew ? SparkCsvRowStatus.Created : SparkCsvRowStatus.Updated, outcome.Object?.Id ?? targetId, []),
            SaveOutcomeKind.Submitted => new SparkCsvImportRow(line, SparkCsvRowStatus.Submitted, targetId, []),
            _ => new SparkCsvImportRow(line, SparkCsvRowStatus.Skipped, targetId, []),
        };
    }

    /// <summary>
    /// Sets the record's cells on <paramref name="obj"/> as the web client would (a JSON value, marked changed). An
    /// update sets only what differs. Returns the cells that cannot be set.
    /// </summary>
    private static List<SparkCsvRowError> Apply(ImportContext context, PersistentObject obj, List<string> record, bool isNew, out bool changed)
    {
        changed = false;
        var errors = new List<SparkCsvRowError>();
        foreach (var (column, name) in context.Plan.Attributes)
        {
            var cell = column < record.Count ? record[column] : "";
            var empty = cell.Trim().Length == 0;
            var settable = obj.TryGetAttribute(name, out var attribute) && !attribute.IsReadOnly;
            if (!settable)
            {
                // Not there for this caller, or read-only: harmless when the file only repeats what is there
                // (an exported read-only column), refused otherwise — one message for both.
                var current = attribute is null ? "" : SparkCsv.FormatValue(attribute.Value);
                if (!empty && !string.Equals(SparkCsv.Unguard(cell), current, StringComparison.Ordinal))
                    errors.Add(new SparkCsvRowError(name, string.Format(NotSettable, name, context.EntityType.Name)));
                continue;
            }

            JsonElement? value;
            if (empty)
            {
                if (context.Request.EmptyCells == SparkCsvEmptyCells.Ignore)
                    continue;
                value = null;
            }
            else if (SparkCsvCells.TryConvert(cell, attribute!.DataType, attribute.IsArray, out var converted, out var error))
            {
                value = converted;
            }
            else
            {
                errors.Add(new SparkCsvRowError(name, error!));
                continue;
            }

            if (!isNew && SameValue(attribute!.Value, value))
                continue;
            attribute!.Value = value;
            attribute.IsValueChanged = true;
            changed = true;
        }
        return errors;
    }

    private static bool SameValue(object? current, JsonElement? next)
    {
        var before = SparkCsv.FormatValue(current);
        var after = next is { } element ? SparkCsv.FormatValue(element) : "";
        return string.Equals(before, after, StringComparison.Ordinal);
    }

    private Task<PersistentObject?> LoadAsync(EntityTypeDefinition entityType, string id, CancellationToken cancellationToken)
        => objectReadPath.LoadAsync(new PersistentObjectReferenceRequest { ObjectTypeId = entityType.Id.ToString("D"), Id = id }, cancellationToken);

    /// <summary>
    /// A new row as <c>/spark/po/new</c> builds it: the caller's shape, the CLR defaults, the construction hook. A caller
    /// who may only edit (a natural-id update) gets the Edit shape without the hook: nothing is created from it.
    /// </summary>
    private async Task<PersistentObject> ScaffoldAsync(ImportContext context, CancellationToken cancellationToken)
    {
        var verb = context.MayCreate ? SparkCoreActions.New : SparkCoreActions.Edit;
        var po = await entityMapper.GetPersistentObjectAsync(context.EntityType.Id, verb, cancellationToken);
        if (context.ClrType is not { } clrType)
            return RoundTrip(po);

        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(clrType);
        }
        catch (Exception ex) when (ex is MissingMethodException or MemberAccessException)
        {
        }
        if (instance is not null)
            entityMapper.PopulateAttributeValues(po, instance);
        if (context.MayCreate)
            await newInvoker.InvokeAsync(clrType, po, parent: null, asDetailParent: null, asDetailAttribute: null, parameters: null, cancellationToken);
        return RoundTrip(po);
    }

    /// <summary>The object as the web client posts it: through Spark's own JSON and back (MCP planner, same reason).</summary>
    private PersistentObject RoundTrip(PersistentObject source)
    {
        var json = JsonSerializer.Serialize(source, jsonOptions.Value.SerializerOptions);
        return JsonSerializer.Deserialize<PersistentObject>(json, jsonOptions.Value.SerializerOptions)
            ?? throw new InvalidOperationException("A persistent object did not survive its own JSON.");
    }

    /// <summary>Every row loads, saves and waits on the request session; its default budget of 30 requests would stop a file after a few rows.</summary>
    private void RaiseRequestBudget(int units, int perUnit)
    {
        var needed = 30 + (long)units * perUnit;
        var advanced = session.Advanced;
        if (advanced.MaxNumberOfRequestsPerSession < needed)
            advanced.MaxNumberOfRequestsPerSession = (int)Math.Min(int.MaxValue, needed);
    }

    private static SparkCsvImportRow Refused(int line, string? attribute, string message)
        => new(line, SparkCsvRowStatus.Refused, null, [new SparkCsvRowError(attribute, message)]);
}

/// <summary>Which column of the file sets what: the reserved columns and the attribute columns, by position.</summary>
internal sealed class CsvImportPlan
{
    public int Width { get; private init; }
    public int? IdColumn { get; private init; }
    public int? EtagColumn { get; private init; }

    /// <summary>Column index → attribute name as the file names it (matched against the object case-insensitively).</summary>
    public IReadOnlyList<(int Column, string Name)> Attributes { get; private init; } = [];

    /// <summary>
    /// The plan of <paramref name="header"/>, or why the file cannot be read: an empty header cell, two columns setting one
    /// attribute, or a reserved name (<c>@…</c>) Spark does not know. Unknown attribute names are not refused here but
    /// per row, with the same answer as an attribute the caller may not set.
    /// </summary>
    public static (CsvImportPlan? Plan, string? Error) Build(IReadOnlyList<string> header, IReadOnlyDictionary<string, string?>? mapping)
    {
        var map = mapping is null ? null : new Dictionary<string, string?>(mapping, StringComparer.OrdinalIgnoreCase);
        int? id = null, etag = null;
        var attributes = new List<(int, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Count; i++)
        {
            var name = SparkCsv.Unguard(header[i].Trim());
            if (map is not null && map.TryGetValue(name, out var target))
            {
                if (string.IsNullOrWhiteSpace(target))
                    continue;
                name = target.Trim();
            }
            if (name.Length == 0)
                return (null, $"Column {i + 1} has no name. Name it, or map it to nothing to ignore it.");
            if (!seen.Add(name))
                return (null, $"Two columns set '{name}'.");

            if (string.Equals(name, SparkCsvColumns.Id, StringComparison.OrdinalIgnoreCase))
                id = i;
            else if (string.Equals(name, SparkCsvColumns.Etag, StringComparison.OrdinalIgnoreCase))
                etag = i;
            else if (name.StartsWith('@'))
                return (null, $"'{name}' is not a column Spark knows.");
            else
                attributes.Add((i, name));
        }
        return (new CsvImportPlan { Width = header.Count, IdColumn = id, EtagColumn = etag, Attributes = attributes }, null);
    }
}
