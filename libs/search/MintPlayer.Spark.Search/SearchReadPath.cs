using System.Globalization;
using System.Text.Json;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Search;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Services.Breadcrumb;

namespace MintPlayer.Spark.Search;

/// <summary>A candidate the caller may see: its type and the row the type's list query returned for it.</summary>
internal sealed record VisibleHit(SearchableType Type, QueryResultItem Row, string BaseLabel);

/// <summary>A searchable type the caller may query, with the list query its hits are re-read through.</summary>
internal sealed record SearchableType(EntityTypeDefinition Definition, SparkQuery ListQuery, bool Fuzzy)
{
    public string Name => Definition.Name;

    public string Label => Definition.Label?.GetValue(CultureInfo.CurrentUICulture.Name) is { Length: > 0 } label
        ? label
        : Definition.Name;
}

/// <summary>
/// The security half of search (<c>docs/spark_search_PRD.md</c> D2). A provider's candidates are re-read through each
/// type's list query, restricted to their ids, as the current caller: the type's <c>Query</c> right, the row filter and
/// <c>RowSecurityGate</c> (filter, breadcrumb, redaction) all apply as they do to the list page itself. A candidate the
/// caller may not read is dropped without a trace, and the label is rendered from the redacted row, never from an index.
/// </summary>
internal sealed partial class SearchReadPath
{
    [Inject] private readonly IModelLoader models;
    [Inject] private readonly IQueryLoader queries;
    [Inject] private readonly IPermissionService permissions;
    [Inject] private readonly IQueryExecutor queryExecutor;
    [Inject] private readonly SparkSearchOptions options;

    private IReadOnlyList<SearchableType>? allowed;

    /// <summary>The searchable types the caller may query, once per request.</summary>
    public async Task<IReadOnlyList<SearchableType>> AllowedTypesAsync(CancellationToken cancellationToken)
    {
        if (allowed is not null)
            return allowed;

        var list = new List<SearchableType>();
        var allQueries = queries.GetQueries().ToList();
        foreach (var type in models.GetEntityTypes().Where(t => t.Search?.Enabled == true))
        {
            if (SparkEntityLinks.ListQuery(type, allQueries) is not { } listQuery)
                continue;
            if (!await permissions.IsAllowedAsync("Query", type.Name, cancellationToken))
                continue;
            list.Add(new SearchableType(type, listQuery, type.Search!.Fuzzy ?? options.Fuzzy));
        }
        return allowed = list;
    }

    /// <summary>The candidates the caller may see, in the provider's order, without duplicates.</summary>
    public async Task<IReadOnlyList<VisibleHit>> ReadAsync(IEnumerable<SparkSearchCandidate> candidates, CancellationToken cancellationToken)
    {
        var types = (await AllowedTypesAsync(cancellationToken)).ToDictionary(t => t.Name, StringComparer.Ordinal);
        var ordered = candidates
            .Where(c => types.ContainsKey(c.EntityType))
            .DistinctBy(c => (c.EntityType, c.Id))
            .ToList();

        var rows = new Dictionary<(string Type, string Id), QueryResultItem>();
        foreach (var group in ordered.GroupBy(c => c.EntityType))
        {
            var type = types[group.Key];
            var ids = group.Select(c => c.Id).ToList();
            QueryResult result;
            try
            {
                result = await queryExecutor.ExecuteQueryAsync(type.ListQuery, take: ids.Count, restrictToIds: ids, cancellationToken: cancellationToken);
            }
            catch (SparkAccessDeniedException)
            {
                // A row policy refused the whole list: nothing of this type is shown, as on the list page.
                continue;
            }
            foreach (var row in result.Items)
                rows[(group.Key, row.Id)] = row;
        }

        return ordered
            .Where(c => rows.ContainsKey((c.EntityType, c.Id)))
            .Select(c =>
            {
                var type = types[c.EntityType];
                var row = rows[(c.EntityType, c.Id)];
                return new VisibleHit(type, row, RenderLabel(type.Definition, row));
            })
            .ToList();
    }

    /// <summary>
    /// The hit's label (D3): <c>search.label</c>, else the breadcrumb template, filled from the row's values. A
    /// placeholder the row has no value for (a column the list query doesn't return, or a redacted one) falls back to
    /// the row's breadcrumb, which the read path already resolved as the caller may see it.
    /// </summary>
    internal static string RenderLabel(EntityTypeDefinition type, QueryResultItem row)
    {
        var fallback = row.Breadcrumb ?? row.Id;
        var template = type.Search?.Label ?? type.Breadcrumb;
        if (string.IsNullOrWhiteSpace(template))
            return fallback;

        IReadOnlyList<BreadcrumbToken> tokens;
        try
        {
            tokens = BreadcrumbTemplate.Parse(template);
        }
        catch (FormatException)
        {
            return fallback;
        }

        var text = new System.Text.StringBuilder();
        foreach (var token in tokens)
        {
            switch (token)
            {
                case LiteralToken literal:
                    text.Append(literal.Text);
                    break;
                case FieldToken field:
                    var cell = row.Values.FirstOrDefault(v => string.Equals(v.Key, field.AttributeName, StringComparison.OrdinalIgnoreCase));
                    var value = cell is null ? null : CellText(cell);
                    if (value is null)
                        return fallback;
                    text.Append(value);
                    break;
            }
        }

        var label = text.ToString().Trim();
        return label.Length > 0 ? label : fallback;
    }

    private static string? CellText(QueryResultItemValue cell)
    {
        if (cell.Breadcrumb is { } breadcrumb)
            return breadcrumb;
        if (cell.Breadcrumbs is { Count: > 0 } many)
            return string.Join(", ", many.Values.Where(v => !string.IsNullOrEmpty(v)));
        return cell.Value switch
        {
            null => null,
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
            JsonElement e => e.ToString(),
            IFormattable f => f.ToString(null, CultureInfo.CurrentCulture),
            var v => v.ToString(),
        };
    }
}
