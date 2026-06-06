namespace MintPlayer.Spark.Client;

/// <summary>
/// Shape of <c>GET /spark/permissions/{entityTypeId}</c>: four booleans covering each of the
/// mutating/reading actions. The endpoint always returns all four, even for anonymous callers —
/// absence of a claim is modelled as <c>false</c>, not a 403. This lets the Angular SPA render
/// "view only" UI for partially-granted entity types without a round-trip per action.
/// </summary>
public sealed class SparkPermissions
{
    public bool CanRead { get; init; }
    public bool CanCreate { get; init; }
    public bool CanEdit { get; init; }
    public bool CanDelete { get; init; }
}

/// <summary>
/// Shape of <c>GET /spark/aliases</c>: two dictionaries mapping each entity-type and query's
/// id (as string) to its user-facing alias. The caller only sees entries they have
/// <c>Query</c> rights on — the server filters.
/// </summary>
public sealed class SparkAliases
{
    public Dictionary<string, string> EntityTypes { get; init; } = new();
    public Dictionary<string, string> Queries { get; init; } = new();
}
