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
    [Description("The attributes of one entity type this user can read: names, labels, descriptions, data types and references. Schema only, no data.")]
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
    [Description("One object as this user sees it: its readable attributes and values, and whether it may be edited or deleted.")]
    public async Task<CallToolResult> GetAsync(
        [Description("The object's type, as discover lists it.")] string type,
        [Description("The object's id, as query returns it.")] string id,
        CancellationToken cancellationToken = default)
    {
        var obj = await objectReadPath.LoadAsync(new PersistentObjectReferenceRequest { ObjectTypeId = type, Id = id }, cancellationToken);
        if (obj is null)
            return SparkMcpResult.Refuse();

        // Through Spark's HTTP options first, so the redaction net (D13a) sees the object exactly as a web
        // response would; then compacted to what an agent needs.
        var node = JsonSerializer.SerializeToNode(obj, jsonOptions.Value.SerializerOptions)?.AsObject()
            ?? throw new InvalidOperationException("A persistent object serialized to nothing.");

        var attributes = new JsonObject();
        if (node["attributes"] is JsonArray array)
        {
            foreach (var attribute in array.OfType<JsonObject>())
            {
                if (attribute["name"]?.GetValue<string>() is not { } name)
                    continue;
                var value = attribute["value"]?.DeepClone();
                attributes[name] = attribute["breadcrumb"] is JsonValue breadcrumb && value is JsonValue
                    ? new JsonObject { ["id"] = value, ["breadcrumb"] = breadcrumb.DeepClone() }
                    : value;
            }
        }

        SparkMcpResult.Track(1);
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

    private static CallToolResult? Paging(int skip, int take)
        => skip < 0 || take is < 1 or > MaxTake
            ? SparkMcpResult.Invalid($"skip must be 0 or more and take between 1 and {MaxTake}.")
            : null;
}
