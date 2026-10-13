using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Endpoints.PersistentObject;
using MintPlayer.Spark.Endpoints.Queries;
using MintPlayer.Spark.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MintPlayer.Spark.Mcp.Tools;

/// <summary>
/// The generic read tools every Spark MCP server has (MCP PRD §4.4): <c>discover</c>, <c>describe</c>,
/// <c>query</c>, <c>get</c>. Each goes through the same shared service its web endpoint calls (D9) — the
/// catalogue, the query read path, the object read path — so rights, row security, attribute redaction and
/// the agent layers (applied inside <c>IPermissionService</c>) are the web's own. No raw session here.
/// </summary>
/// <remarks>
/// Every refusal is <see cref="SparkMcpResult.Refuse"/>: an unknown type, a type the caller may not use and a
/// missing row answer alike (D17).
/// </remarks>
[McpServerToolType]
internal sealed class SparkGenericTools(
    ISparkCatalogue catalogue,
    IQueryReadPath queryReadPath,
    IPersistentObjectReadPath objectReadPath,
    IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions)
{
    private const int DefaultTake = 25;
    private const int MaxTake = 100;

    [McpServerTool(Name = "discover", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the entity types this user can read through this server, each with its queries. Start here, then use describe and query.")]
    public async Task<CallToolResult> DiscoverAsync(
        [Description("Only types whose name or label contains this text.")] string? search = null,
        [Description("Types to skip, for paging.")] int skip = 0,
        [Description("Types to return, 1 to 100.")] int take = 50,
        CancellationToken cancellationToken = default)
    {
        if (Paging(skip, take) is { } invalid)
            return invalid;

        var types = await catalogue.ListEntityTypesAsync(cancellationToken);
        var queries = await catalogue.ListQueriesAsync(cancellationToken);

        var matching = types
            .Where(t => string.IsNullOrWhiteSpace(search)
                || t.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (SparkMcpResult.Label(t.Label)?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        var page = matching.Skip(skip).Take(take).Select(t => new
        {
            name = t.Name,
            label = SparkMcpResult.Label(t.Label),
            queries = queries
                // A sub-query lists rows under one parent object; it cannot run on its own here.
                .Where(q => q.ParentReference is null && string.Equals(q.EntityType, t.Name, StringComparison.Ordinal))
                .Select(q => new { name = q.Alias ?? q.Name, label = SparkMcpResult.Label(q.Label) })
                .ToList(),
        }).ToList();

        SparkMcpResult.Track(page.Count);
        var next = skip + page.Count;
        return SparkMcpResult.Json(new
        {
            totalTypes = matching.Count,
            nextSkip = next < matching.Count ? next : (int?)null,
            types = page,
        });
    }

    [McpServerTool(Name = "describe", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The attributes of one entity type this user can read: names, labels, descriptions, data types and references, each reference with the query its values come from. Schema only, no data.")]
    public async Task<CallToolResult> DescribeAsync(
        [Description("The type's name, as discover lists it.")] string type,
        CancellationToken cancellationToken = default)
    {
        var definition = await catalogue.GetEntityTypeAsync(type, SparkCoreActions.Read, cancellationToken);
        if (definition is null)
            return SparkMcpResult.Refuse();

        var queries = await catalogue.ListQueriesAsync(cancellationToken);
        return SparkMcpResult.Json(new
        {
            name = definition.Name,
            label = SparkMcpResult.Label(definition.Label),
            attributes = definition.Attributes.OrderBy(a => a.Order).Select(a => new
            {
                name = a.Name,
                label = SparkMcpResult.Label(a.Label),
                description = SparkMcpResult.Label(a.Description),
                dataType = a.DataType,
                isArray = a.IsArray,
                isRequired = a.IsRequired,
                isReadOnly = a.IsReadOnly,
                referenceType = a.ReferenceType,
                // The query a reference's values are picked from (docs/default_index_PRD.md, D6), by the name discover
                // lists; null when it names none, or one this user may not list.
                query = a.DataType == "Reference" && a.Query is { } referenceQuery
                    ? queries.FirstOrDefault(q => q.ParentReference is null && q.Name == referenceQuery) is { } listed
                        ? listed.Alias ?? listed.Name
                        : null
                    : null,
                asDetailType = a.AsDetailType,
            }),
            queries = queries
                .Where(q => q.ParentReference is null && string.Equals(q.EntityType, definition.Name, StringComparison.Ordinal))
                .Select(q => new { name = q.Alias ?? q.Name, label = SparkMcpResult.Label(q.Label) }),
        });
    }

    [McpServerTool(Name = "query", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Runs a query (by the name discover lists) and returns one page of rows: object ids, breadcrumbs and the columns' values. Only rows this user may see.")]
    public async Task<CallToolResult> QueryAsync(
        [Description("The query's name, as discover lists it.")] string query,
        [Description("Free-text search over the query's searchable columns.")] string? search = null,
        [Description("Column filters: per column, values to include and/or exclude. Columns combine with AND, values within a column with OR.")] QueryColumnFilter[]? filters = null,
        [Description("Sort order: columns with direction 'asc' or 'desc'.")] SortColumn[]? sort = null,
        [Description("Rows to skip, for paging (use nextSkip from the previous page).")] int skip = 0,
        [Description("Rows to return, 1 to 100.")] int take = DefaultTake,
        CancellationToken cancellationToken = default)
    {
        if (Paging(skip, take) is { } invalid)
            return invalid;

        var outcome = await queryReadPath.ExecuteAsync(new ExecuteQueryRequest
        {
            QueryId = query,
            Search = search,
            Columns = filters,
            SortColumns = sort,
            Skip = skip,
            Take = take,
        }, cancellationToken);

        if (outcome.Result is { } result)
            return SparkMcpResult.Rows(result);

        // A 400 is decided after authorization (an unknown sort column, a bad direction), so its text tells
        // nothing to a caller who may not use the query; every not-found is the uniform refusal.
        return outcome.StatusCode == StatusCodes.Status400BadRequest
            ? SparkMcpResult.Invalid(outcome.Error ?? "Invalid request.")
            : SparkMcpResult.Refuse();
    }

    [McpServerTool(Name = "get", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("One object as this user sees it: its readable attributes and values, and, where the application decides it per row, whether it may be edited or deleted. An attribute holding one detail object (asDetailType in describe) shows it inline; an attribute holding detail rows (a person's jobs, a car's services) shows how many there are: name it in include to have the rows, as many as fit (at most 100; omitted says how many more).")]
    public async Task<CallToolResult> GetAsync(
        [Description("The object's type, as discover lists it.")] string type,
        [Description("The object's id, as query returns it.")] string id,
        [Description("Detail attributes whose rows to return, by name.")] string[]? include = null,
        CancellationToken cancellationToken = default)
    {
        var obj = await objectReadPath.LoadAsync(new PersistentObjectReferenceRequest { ObjectTypeId = type, Id = id }, cancellationToken);
        if (obj is null)
            return SparkMcpResult.Refuse();

        // Through Spark's HTTP options first, so the redaction net (D13a) sees the object exactly as a web
        // response would; then compacted to what an agent needs.
        var node = JsonSerializer.SerializeToNode(obj, jsonOptions.Value.SerializerOptions)?.AsObject()
            ?? throw new InvalidOperationException("A persistent object serialized to nothing.");

        var included = new HashSet<string>(include ?? [], StringComparer.OrdinalIgnoreCase);
        var rows = 1;
        var budget = MaxDetailCharacters;
        var attributes = Attributes(node, included, ref rows, ref budget);

        SparkMcpResult.Track(rows);
        return SparkMcpResult.Json(new JsonObject
        {
            ["id"] = node["id"]?.DeepClone(),
            ["type"] = node["name"]?.DeepClone(),
            ["breadcrumb"] = node["breadcrumb"]?.DeepClone(),
            ["etag"] = node["etag"]?.DeepClone(),
            ["can"] = node["can"]?.DeepClone(),
            ["attributes"] = attributes,
        });
    }

    /// <summary>The most detail rows <c>get</c> returns per attribute.</summary>
    internal const int MaxDetailRows = 100;

    /// <summary>
    /// The characters of detail rows one <c>get</c> returns, across its included attributes: the object's own attributes
    /// fit beside them within <see cref="SparkMcpResult.MaxCharacters"/>, so a long history ends at a row, never mid-text
    /// (spike S9: a car's 100 service rows of 200 characters already came to 36,833 of the 40,000).
    /// </summary>
    internal const int MaxDetailCharacters = 30_000;

    /// <summary>
    /// An object's attributes by name, compacted: a reference as its id and breadcrumb; a detail object (AsDetail,
    /// single) inline, compacted the same way; detail rows (AsDetail, array) as their count, or, when
    /// <paramref name="included"/> names the attribute, as the rows themselves, at most <see cref="MaxDetailRows"/> and
    /// <see cref="MaxDetailCharacters"/> (<c>omitted</c> says how many more there are). The web carries detail rows in
    /// <c>object</c>/<c>objects</c>, not <c>value</c>: an agent saw <c>null</c> for them until the S9 run (plan M7).
    /// </summary>
    internal static JsonObject Attributes(JsonObject node, HashSet<string> included, ref int rows, ref int budget, JsonObject? into = null)
    {
        var attributes = into ?? new JsonObject();
        if (node["attributes"] is not JsonArray array)
            return attributes;

        foreach (var attribute in array.OfType<JsonObject>())
        {
            if (attribute["name"]?.GetValue<string>() is not { } name)
                continue;

            if (attribute["objects"] is JsonArray details)
            {
                if (!included.Contains(name))
                {
                    attributes[name] = new JsonObject { ["count"] = details.Count };
                    continue;
                }
                var shown = new JsonArray();
                foreach (var detail in details.OfType<JsonObject>().Take(MaxDetailRows))
                {
                    // The row's key first (a value object's id), then its attributes.
                    var row = detail["id"] is JsonValue rowId ? new JsonObject { ["id"] = rowId.DeepClone() } : new JsonObject();
                    var unbounded = int.MaxValue;
                    Attributes(detail, [], ref rows, ref unbounded, row);
                    var size = row.ToJsonString().Length + 1;
                    if (size > budget)
                        break;
                    budget -= size;
                    shown.Add(row);
                    rows++;
                }
                attributes[name] = new JsonObject
                {
                    ["count"] = details.Count,
                    ["rows"] = shown,
                    ["omitted"] = details.Count - shown.Count,
                };
                continue;
            }

            if (attribute["object"] is JsonObject detailObject)
            {
                attributes[name] = Attributes(detailObject, [], ref rows, ref budget);
                continue;
            }

            var value = attribute["value"]?.DeepClone();
            attributes[name] = attribute["breadcrumb"] is JsonValue breadcrumb && value is JsonValue
                ? new JsonObject { ["id"] = value, ["breadcrumb"] = breadcrumb.DeepClone() }
                : value;
        }
        return attributes;
    }

    private static CallToolResult? Paging(int skip, int take)
        => skip < 0 || take is < 1 or > MaxTake
            ? SparkMcpResult.Invalid($"skip must be 0 or more and take between 1 and {MaxTake}.")
            : null;
}
