using System.Collections;
using System.Collections.Concurrent;
using MintPlayer.Spark.Logging.Models;

namespace MintPlayer.Spark.Logging.Services;

/// <summary>
/// The state a log scope pushes through <c>ILogger.BeginScope</c>. The document provider files each entry under the
/// innermost one open in the async flow. As a key/value list it also tags every other sink's entries with <c>LogId</c>.
/// </summary>
internal sealed class LogScopeState(string id, string? relatedPersistentObjectId, DateTime? expiresAtUtc)
    : IReadOnlyList<KeyValuePair<string, object?>>
{
    private readonly ConcurrentQueue<LogEntry> entries = new();
    private volatile bool closed;

    public string Id { get; } = id;
    public string? RelatedPersistentObjectId { get; } = relatedPersistentObjectId;
    public DateTime? ExpiresAtUtc { get; } = expiresAtUtc;

    /// <summary>
    /// Adds an entry unless the scope is closed. A task started inside the scope and still logging after its
    /// <c>Dispose</c> sees the scope in its captured flow; its late entries are dropped, never filed under an outer log
    /// (#660 S2).
    /// </summary>
    public bool TryAdd(LogEntry entry)
    {
        if (closed)
            return false;
        entries.Enqueue(entry);
        return true;
    }

    /// <summary>Closes the scope and hands over what it gathered.</summary>
    public IReadOnlyList<LogEntry> Close()
    {
        closed = true;
        var drained = new List<LogEntry>(entries.Count);
        while (entries.TryDequeue(out var entry))
            drained.Add(entry);
        return drained;
    }

    public int Count => 1;
    public KeyValuePair<string, object?> this[int index]
        => index == 0 ? new("LogId", Id) : throw new ArgumentOutOfRangeException(nameof(index));
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() { yield return this[0]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public override string ToString() => $"LogId:{Id}";
}
