namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// The rules a <b>composed query</b> must satisfy, and the announcement each one makes at startup.
/// </summary>
/// <remarks>
/// A composed query is one whose entity type declares no <c>clrType</c>: there is no entity class,
/// no collection and no document behind a row. The rows come from <c>{Name}Actions</c> and are
/// computed, which is what makes such a query useful — and what takes row-level security out of the
/// picture entirely (there is no stored document to re-judge).
/// <para>
/// Shared by the runtime loader and the build-time <c>--spark-verify-model</c> gate, for the same
/// reason <see cref="SparkQueryAliases"/> is: the set of models CI accepts must be provably the set
/// the application runs. The two readings differ (files on disk vs. the loaded model); the rules do
/// not.
/// </para>
/// </remarks>
public static class SparkComposedQueries
{
    /// <summary>
    /// Whether this type is composed — served by an actions class rather than by a collection.
    /// </summary>
    public static bool IsComposed(EntityTypeDefinition type) => string.IsNullOrEmpty(type.ClrType);

    /// <summary>
    /// Every reason the given model cannot be served, one message per problem, each naming the fix.
    /// Empty means the composed queries in this model are usable.
    /// </summary>
    /// <remarks>
    /// Returns messages rather than throwing so a single pass reports every problem: an author who
    /// has to fix one, re-run, and discover the next pays a round trip per mistake.
    /// </remarks>
    public static IReadOnlyList<string> Validate(
        IReadOnlyCollection<EntityTypeDefinition> types, IReadOnlyCollection<SparkQuery> queries)
    {
        var problems = new List<string>();
        var byName = new Dictionary<string, EntityTypeDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in types)
            byName[type.Name] = type;

        foreach (var query in queries)
        {
            if (string.IsNullOrEmpty(query.EntityType)) continue;
            if (!byName.TryGetValue(query.EntityType, out var type)) continue;
            if (!IsComposed(type)) continue;

            // Refused here rather than at the first MoveNext inside an open websocket, where it
            // surfaced as `CLR type '' not found` wrapped in `{"message":"Stream failed"}` — a
            // message that names neither the query nor the reason, arrives only once someone opens
            // the page, and cannot be seen in CI at all.
            if (query.IsStreamingQuery)
            {
                problems.Add(
                    $"Query '{query.Name}' streams over '{type.Name}', which declares no clrType. Streaming " +
                    $"reads a RavenDB change stream for a collection, and a composed type has no collection " +
                    $"to watch. Either give the query a non-streaming source, or point it at a type backed by " +
                    $"an entity class.");
            }

            // A composed type's attributes are hand-authored, and the only two in the wild are
            // PersistentObject-only — which is what an author copies when they add a query to one.
            // The result is a grid with rows and no columns: the rows arrive, the client has
            // nothing to render them into, and nothing anywhere says why.
            if (!type.Attributes.Any(a => a.ShowedOn.HasFlag(EShowedOn.Query)))
            {
                problems.Add(
                    $"Query '{query.Name}' returns rows of '{type.Name}', but none of that type's " +
                    $"{type.Attributes.Length} attribute(s) is shown on a query — every one is " +
                    $"\"showedOn\": \"PersistentObject\". Columns come from the attributes marked " +
                    $"\"Query\", so this query would render rows into a grid with no columns. Mark the " +
                    $"attributes the grid should show as \"showedOn\": \"Query, PersistentObject\" (or " +
                    $"\"Query\") in the type's model file.");
            }
        }

        return problems;
    }

    /// <summary>
    /// The line a composed query announces itself with at startup, or <see langword="null"/> when
    /// the query is not composed.
    /// </summary>
    /// <remarks>
    /// ⚠️ This exists because a composed grid is <b>indistinguishable from every other Spark grid</b>
    /// once rendered, while being the only kind the framework does not row-filter. The risk is not
    /// the deliberate landing page someone wrote on purpose; it is the next developer who reaches
    /// for a composed query because writing one is easier than writing a row rule, over data that
    /// does have owners — and gets a grid that looks exactly right. An announcement per query, at
    /// every startup, is the cheapest thing that puts the fact in front of someone.
    /// </remarks>
    public static string? Announce(SparkQuery query, EntityTypeDefinition type)
    {
        if (!IsComposed(type)) return null;

        return $"Spark: query '{query.Name}' ({query.Source}) is COMPOSED — its rows come from " +
               $"{type.Name}Actions, not from a collection. Row filtering, value redaction and per-row " +
               $"permissions do not apply and cannot: there is no document behind a row. " +
               $"{type.Name}Actions is responsible for returning only rows this caller may see, and only " +
               $"values this caller may read. The type-level 'Query' right is still enforced.";
    }
}
