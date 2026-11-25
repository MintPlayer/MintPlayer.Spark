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

    /// <param name="ExpiresAt">The store's expiry (<c>ISparkSession.SetExpiry</c>); nothing sweeps it in memory.</param>
    internal sealed record Entry(string Id, string Json, Type ClrType, string Collection, string Version, DateTimeOffset? ExpiresAt = null);

    internal abstract record Write(string Id, string? ExpectedVersion);

    internal sealed record Put(string Id, string? ExpectedVersion, string Json, Type ClrType, string Collection, DateTimeOffset? ExpiresAt = null) : Write(Id, ExpectedVersion);

    internal sealed record Remove(string Id, string? ExpectedVersion) : Write(Id, ExpectedVersion);

    /// <summary>A change of the stored document's JSON, applied under the lock (<c>PatchAppend</c>); a missing document is skipped.</summary>
    internal sealed record Patch(string Id, Func<Entry, string> Apply) : Write(Id, null);

    /// <summary>A unique key claimed (<paramref name="Claim"/>) or freed for the document <paramref name="Owner"/>.</summary>
    internal sealed record Reservation(string Key, string Owner, bool Claim);

    private readonly Dictionary<string, string> reservations = new(StringComparer.OrdinalIgnoreCase);

    public string? ReservationOwner(string key)
    {
        lock (gate)
            return reservations.GetValueOrDefault(key);
    }

    /// <summary>The last version number a commit gave out: every version up to it has committed (#536 D39's horizon).</summary>
    public long LastEtag
    {
        get
        {
            lock (gate)
                return etag;
        }
    }

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
    /// <exception cref="SparkReservationTakenException">A claimed key is held by another existing document; nothing was written.</exception>
    public IReadOnlyDictionary<string, string?> Commit(IReadOnlyList<Write> writes, IReadOnlyList<Reservation>? reservationChanges = null)
    {
        reservationChanges ??= [];
        if (writes.Count == 0 && reservationChanges.Count == 0)
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

            // A patch that cannot apply (a null list) must fail before anything is written.
            foreach (var patch in writes.OfType<Patch>())
                if (documents.GetValueOrDefault(patch.Id) is { } target)
                    patch.Apply(target);

            // A key's holder counts while its document exists once this commit is applied; one that is gone is taken over.
            foreach (var claim in reservationChanges.Where(r => r.Claim))
                if (reservations.GetValueOrDefault(claim.Key) is { } holder
                    && !string.Equals(holder, claim.Owner, StringComparison.OrdinalIgnoreCase)
                    && ExistsAfter(holder, writes))
                    throw new SparkReservationTakenException(claim.Key, holder);

            foreach (var change in reservationChanges)
            {
                if (change.Claim)
                    reservations[change.Key] = change.Owner;
                else if (reservations.GetValueOrDefault(change.Key) is { } holder && string.Equals(holder, change.Owner, StringComparison.OrdinalIgnoreCase))
                    reservations.Remove(change.Key);
            }

            foreach (var write in writes)
            {
                switch (write)
                {
                    case Put put:
                        var version = $"A:{++etag}";
                        documents[put.Id] = new Entry(put.Id, put.Json, put.ClrType, put.Collection, version, put.ExpiresAt);
                        versions[put.Id] = version;
                        changes.Add(new SparkStoreChange(put.Id, put.Collection, version));
                        break;
                    case Remove remove:
                        if (documents.Remove(remove.Id, out var removed))
                            changes.Add(new SparkStoreChange(remove.Id, removed.Collection, null));
                        versions[remove.Id] = null;
                        break;
                    case Patch patch when documents.GetValueOrDefault(patch.Id) is { } current:
                        var patched = $"A:{++etag}";
                        documents[patch.Id] = current with { Json = patch.Apply(current), Version = patched };
                        changes.Add(new SparkStoreChange(patch.Id, current.Collection, patched));
                        break;
                }
            }
        }

        Committed?.Invoke(changes);
        return versions;
    }

    /// <summary>
    /// A set-based change of <paramref name="collection"/> under the lock: <paramref name="rewrite"/> gets each row and
    /// returns its new JSON, <see cref="Deleted"/>, or <see langword="null"/> to leave it. Returns how many rows changed.
    /// </summary>
    public long Rewrite(string collection, Func<Entry, string?> rewrite)
    {
        var changes = new List<SparkStoreChange>();
        lock (gate)
        {
            // Every row is decided before any is written, so a rewrite that throws changes nothing.
            var decided = documents.Values
                .Where(e => string.Equals(e.Collection, collection, StringComparison.OrdinalIgnoreCase))
                .Select(e => (Entry: e, Json: rewrite(e)))
                .Where(d => d.Json is not null)
                .ToList();

            foreach (var (entry, json) in decided)
            {
                if (ReferenceEquals(json, Deleted))
                {
                    documents.Remove(entry.Id);
                    changes.Add(new SparkStoreChange(entry.Id, entry.Collection, null));
                    continue;
                }
                var version = $"A:{++etag}";
                documents[entry.Id] = entry with { Json = json!, Version = version };
                changes.Add(new SparkStoreChange(entry.Id, entry.Collection, version));
            }
        }

        if (changes.Count > 0)
            Committed?.Invoke(changes);
        return changes.Count;
    }

    /// <summary>What a <see cref="Rewrite"/> function returns to delete the row.</summary>
    public static readonly string Deleted = new(['\0']);

    private bool ExistsAfter(string id, IReadOnlyList<Write> writes)
    {
        var last = writes.LastOrDefault(w => w is not Patch && string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase));
        return last is null ? documents.ContainsKey(id) : last is Put;
    }
}
