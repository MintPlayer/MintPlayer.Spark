using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Storage.InMemory;

/// <summary>
/// The documents of one in-memory store: id → JSON, CLR type, collection and version. Reads copy the entry out
/// (strings are immutable), and a commit applies all its writes under one lock after checking every expected
/// version, so a unit of work is atomic and a concurrency failure changes nothing.
/// </summary>
internal sealed class InMemoryDatabase
{
    private readonly Dictionary<string, Entry> documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock gate = new();
    private long etag;

    internal sealed record Entry(string Id, string Json, Type ClrType, string Collection, string Version);

    internal abstract record Write(string Id, string? ExpectedVersion);

    internal sealed record Put(string Id, string? ExpectedVersion, string Json, Type ClrType, string Collection) : Write(Id, ExpectedVersion);

    internal sealed record Remove(string Id, string? ExpectedVersion) : Write(Id, ExpectedVersion);

    /// <summary>Raised after a commit, outside the lock, with what it stored and deleted.</summary>
    public event Action<IReadOnlyList<SparkStoreChange>>? Committed;

    public Entry? Get(string id)
    {
        lock (gate)
            return documents.GetValueOrDefault(id);
    }

    public IReadOnlyList<Entry> InCollection(string collection)
    {
        lock (gate)
            return [.. documents.Values.Where(e => string.Equals(e.Collection, collection, StringComparison.OrdinalIgnoreCase))];
    }

    public IReadOnlyList<Entry> StartingWith(string idPrefix)
    {
        lock (gate)
            return [.. documents.Values
                .Where(e => e.Id.StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Applies <paramref name="writes"/> as one unit and returns each written id's new version (null for a delete).
    /// An expected version of <c>""</c> means "must not exist" (RavenDB's empty change vector); any other non-null value
    /// must equal the stored version.
    /// </summary>
    /// <exception cref="SparkStoreConcurrencyException">An expected version did not match; nothing was written.</exception>
    public IReadOnlyDictionary<string, string?> Commit(IReadOnlyList<Write> writes)
    {
        if (writes.Count == 0)
            return new Dictionary<string, string?>();

        var versions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var changes = new List<SparkStoreChange>(writes.Count);
        lock (gate)
        {
            foreach (var write in writes)
            {
                if (write.ExpectedVersion is not { } expected)
                    continue;
                var current = documents.GetValueOrDefault(write.Id)?.Version;
                if (expected.Length == 0 ? current is not null : !string.Equals(current, expected, StringComparison.Ordinal))
                    throw new SparkStoreConcurrencyException(
                        $"Document '{write.Id}' is at version '{current ?? "(none)"}', the write expected '{(expected.Length == 0 ? "(none)" : expected)}'.");
            }

            foreach (var write in writes)
            {
                switch (write)
                {
                    case Put put:
                        var version = $"A:{++etag}";
                        documents[put.Id] = new Entry(put.Id, put.Json, put.ClrType, put.Collection, version);
                        versions[put.Id] = version;
                        changes.Add(new SparkStoreChange(put.Id, put.Collection, version));
                        break;
                    case Remove remove:
                        if (documents.Remove(remove.Id, out var removed))
                            changes.Add(new SparkStoreChange(remove.Id, removed.Collection, null));
                        versions[remove.Id] = null;
                        break;
                }
            }
        }

        Committed?.Invoke(changes);
        return versions;
    }
}
