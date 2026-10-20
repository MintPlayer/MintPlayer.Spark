using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Moderation.Documents;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace MintPlayer.Spark.Moderation.Migrations;

/// <summary>
/// Rewrites the stored <c>TargetType</c> of every moderation document from the type's <b>name</b> to its <b>id</b>
/// (D28, commerce X9): a name is what an application's <c>[assembly: PersistentObjectName]</c> changes, so a stored
/// name points at nothing after a rename, or at another type that took the name.
/// </summary>
/// <remarks>
/// <para>
/// Six collections hold it: votes, reputation events, flags, review cases, locks and the audit log. The audit log
/// also holds CLR simple names, written by the old satellite audit sink; those are mapped through
/// <paramref name="clrSimpleNameToId"/> where a simple name is unambiguous.
/// </para>
/// <para>
/// Idempotent: the map holds names only, and a value already an id (a GUID) or absent is never in it, so a second
/// run changes nothing; RavenDB writes no document the script left unchanged. A value the model no longer knows is
/// left as it is and returned, for the caller to log.
/// </para>
/// <para>
/// The script is a constant and the map a query parameter: no value is spliced into RQL. The collection name is,
/// because RQL takes no parameter for it, and is validated by <see cref="RqlIdentifier.Collection"/> (repo rule).
/// </para>
/// </remarks>
internal static class ModerationTargetTypeUpgrade
{
    private const string Script = """
        update {
            var t = this.TargetType;
            if (typeof t === 'string' && Object.prototype.hasOwnProperty.call($map, t))
                this.TargetType = $map[t];
        }
        """;

    /// <summary>The document classes whose <c>TargetType</c> holds a type.</summary>
    internal static readonly Type[] Documents =
    [
        typeof(ModerationVote), typeof(ReputationEvent), typeof(ModerationFlag),
        typeof(ReviewCase), typeof(ModerationLock), typeof(ModerationAuditEntry),
    ];

    /// <summary>Type name → id (<c>"D"</c>) of every model type.</summary>
    public static Dictionary<string, string> NameMap(IEnumerable<EntityTypeDefinition> types)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in types)
            map.TryAdd(type.Name, type.Id.ToString("D"));
        return map;
    }

    /// <summary>CLR simple name → id, for a simple name exactly one model type has and no type is named.</summary>
    public static Dictionary<string, string> ClrSimpleNameMap(IEnumerable<EntityTypeDefinition> types)
    {
        var list = types.ToList();
        var names = new HashSet<string>(list.Select(t => t.Name), StringComparer.Ordinal);
        return list
            .Where(t => t.ClrType is { Length: > 0 })
            .GroupBy(t => SimpleName(t.ClrType!), StringComparer.Ordinal)
            .Where(g => g.Count() == 1 && !names.Contains(g.Key))
            .ToDictionary(g => g.Key, g => g.Single().Id.ToString("D"), StringComparer.Ordinal);
    }

    /// <returns>Every stored value that is neither an id nor a name the model knows, per collection.</returns>
    public static async Task<IReadOnlyList<(string Collection, string Value)>> RunAsync(
        IDocumentStore store,
        IReadOnlyDictionary<string, string> nameToId,
        IReadOnlyDictionary<string, string> clrSimpleNameToId,
        CancellationToken cancellationToken = default)
    {
        var unknown = new List<(string, string)>();
        foreach (var document in Documents)
        {
            var collection = RqlIdentifier.Collection(store.Conventions.FindCollectionName(document));
            var map = new Dictionary<string, string>(nameToId, StringComparer.Ordinal);
            if (document == typeof(ModerationAuditEntry))
                foreach (var pair in clrSimpleNameToId)
                    map.TryAdd(pair.Key, pair.Value);

            foreach (var value in await StoredValuesAsync(store, collection, cancellationToken))
            {
                if (Guid.TryParse(value, out _) || map.ContainsKey(value)) continue;
                unknown.Add((collection, value));
            }

            // Nothing to map: no patch, so a database without moderation data is never touched.
            if (map.Count == 0) continue;

            var operation = await store.Operations.SendAsync(
                new PatchByQueryOperation(
                    new IndexQuery
                    {
                        Query = $"from '{collection}'\n{Script}",
                        QueryParameters = new Parameters { ["map"] = map },
                    },
                    new QueryOperationOptions { StaleTimeout = TimeSpan.FromMinutes(5) }),
                token: cancellationToken);

            // Waited, so a throw aborts startup and the migration is retried rather than marked done half-applied.
            await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(5));
        }

        return unknown;
    }

    /// <summary>The distinct non-empty <c>TargetType</c> values of one collection: a scan, no index.</summary>
    private static async Task<IReadOnlyCollection<string>> StoredValuesAsync(IDocumentStore store, string collection, CancellationToken cancellationToken)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        using var session = store.OpenAsyncSession();
        var query = session.Advanced.AsyncRawQuery<TargetTypeRow>($"from '{collection}' select TargetType");
        await using var stream = await session.Advanced.StreamAsync(query, cancellationToken);
        while (await stream.MoveNextAsync())
        {
            if (stream.Current.Document.TargetType is { Length: > 0 } value)
                values.Add(value);
        }
        return values;
    }

    private static string SimpleName(string clrType)
    {
        var name = clrType;
        var dot = Math.Max(name.LastIndexOf('.'), name.LastIndexOf('+'));
        return dot < 0 ? name : name[(dot + 1)..];
    }

    private sealed class TargetTypeRow
    {
        public string? TargetType { get; set; }
    }
}
