using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Queries;

/// <summary>
/// An immutable view of the query being executed, for <see cref="SparkQueryContext.Query"/>.
/// </summary>
/// <remarks>
/// <para>
/// This exists because handing out the <see cref="SparkQuery"/> itself is unsafe, and an earlier
/// revision of this context did exactly that with a comment claiming it was "read-only". It was
/// not: <c>init</c> freezes the reference, not the object, and every property on
/// <see cref="SparkQuery"/> has a public setter. <c>ModelLoader</c> is registered
/// <c>Singleton</c> and <c>GetQueries()</c> returns its cached instances <b>by reference</b>, so
/// <c>context.Query.Source = "Custom.Mine"</c> inside a hook would have rewritten the query for
/// every subsequent request, for every user, until the process restarted — and would have
/// presented as an intermittent caching bug rather than as the mutation it was.
/// </para>
/// <para>
/// Copying <see cref="SortColumns"/> is part of that, not incidental tidiness:
/// <c>SparkQuery.WithSortColumns</c> is <c>MemberwiseClone</c> plus a reassignment, so even a
/// per-request clone of the query would still share the original array, and
/// <c>context.Query.SortColumns[0] = …</c> would reach the singleton.
/// </para>
/// <para>
/// A comment asking a caller not to mutate something is the weakest safety there is. This type is
/// the version the compiler enforces.
/// </para>
/// </remarks>
public sealed record SparkQueryInfo
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string? Alias { get; init; }

    /// <summary><c>Database.PropertyName</c> or <c>Custom.MethodName</c>.</summary>
    public required string Source { get; init; }

    public string? EntityType { get; init; }
    public string? IndexName { get; init; }
    public SparkQueryRenderMode RenderMode { get; init; }
    public bool IsStreamingQuery { get; init; }

    /// <summary>The sort in effect: the caller's override when there was one, else the declared order.</summary>
    public required IReadOnlyList<SortColumn> SortColumns { get; init; }

    internal static SparkQueryInfo From(SparkQuery query) => new()
    {
        Id = query.Id,
        Name = query.Name,
        Alias = query.Alias,
        Source = query.Source,
        EntityType = query.EntityType,
        IndexName = query.IndexName,
        RenderMode = query.RenderMode,
        IsStreamingQuery = query.IsStreamingQuery,
        // Copied, not aliased — see the remarks.
        SortColumns = query.SortColumns.ToArray(),
    };
}
