using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Boards.Kinds;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Boards.Services;

/// <summary>One entry of the "Add tile" picker: a target the caller may pin, with the kinds that can show it.</summary>
public sealed class SparkBoardCatalogEntry
{
    public required IReadOnlyList<string> Kinds { get; init; }
    public required IReadOnlyDictionary<string, string> Target { get; init; }
    public TranslatedString? Title { get; init; }
    public string? Icon { get; init; }

    /// <summary>The picker's section: <c>programUnits</c> or <c>queries</c>.</summary>
    public required string Group { get; init; }
}

public sealed class SparkBoardCatalog
{
    public required IReadOnlyList<SparkBoardCatalogEntry> Entries { get; init; }

    /// <summary>Every registered kind, so the client can tell a missing renderer from a missing kind.</summary>
    public required IReadOnlyList<string> Kinds { get; init; }
}

/// <summary>What the caller may add (§4.4): the menu entries and the queries they can open. Nothing they cannot.</summary>
internal sealed partial class SparkBoardCatalogBuilder
{
    [Inject] private readonly IProgramUnitsLoader programUnitsLoader;
    [Inject] private readonly IQueryLoader queryLoader;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly SparkTileKindRegistry kinds;

    public async Task<SparkBoardCatalog> BuildAsync(CancellationToken cancellationToken)
    {
        var entries = new List<SparkBoardCatalogEntry>();
        if (kinds.Find(SparkTileKinds.ProgramUnit) is ProgramUnitTileKind programUnits)
        {
            foreach (var group in programUnitsLoader.GetProgramUnits().ProgramUnitGroups.OrderBy(g => g.Order))
            foreach (var unit in group.ProgramUnits)
            {
                if (!await programUnits.IsVisibleAsync(unit, cancellationToken))
                    continue;
                entries.Add(new SparkBoardCatalogEntry
                {
                    Kinds = [SparkTileKinds.ProgramUnit],
                    Target = new Dictionary<string, string> { ["programUnit"] = unit.Id.ToString() },
                    Title = unit.Name,
                    Icon = unit.Icon,
                    Group = "programUnits",
                });
            }
        }

        string[] queryKinds = [.. new[] { SparkTileKinds.Query, SparkTileKinds.Count }.Where(k => kinds.Find(k) is not null)];
        if (queryKinds.Length > 0)
        {
            foreach (var query in queryLoader.GetQueries().OrderBy(q => q.Name, StringComparer.Ordinal))
            {
                if (!await QueryTileKind.QueryAllowedAsync(permissionService, query, cancellationToken))
                    continue;
                entries.Add(new SparkBoardCatalogEntry
                {
                    Kinds = queryKinds,
                    Target = new Dictionary<string, string> { ["query"] = query.Alias is { Length: > 0 } alias ? alias : query.Id.ToString() },
                    Title = query.Label ?? TranslatedString.Create(query.Name),
                    Group = "queries",
                });
            }
        }

        return new SparkBoardCatalog { Entries = entries, Kinds = kinds.Kinds.Keys.Order(StringComparer.Ordinal).ToList() };
    }
}
