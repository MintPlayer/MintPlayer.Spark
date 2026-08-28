using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Streaming;

/// <summary>
/// Per-connection stateful diff calculator for streaming queries: a snapshot first, then only what
/// changed.
/// </summary>
/// <remarks>
/// Keyed on <see cref="QueryResultItem.Id"/>, which is now guaranteed non-null and unique because
/// the projection refuses anything else. It used to key on a nullable id and skip rows that had
/// none — so a row type without a readable id streamed a snapshot and then never updated, silently.
/// </remarks>
internal sealed class StreamingDiffEngine
{
    private Dictionary<string, QueryResultItem>? _previousState;

    /// <summary>
    /// Computes the message for a batch: a <see cref="SnapshotMessage"/> on the first call (columns
    /// included, once), a <see cref="PatchMessage"/> carrying only changed values afterwards, or
    /// null when nothing changed.
    /// </summary>
    public StreamingMessage? ComputeMessage(StreamingQueryBatch batch)
    {
        var currentItems = batch.Items;

        if (_previousState is null)
        {
            _previousState = currentItems.ToDictionary(item => item.Id, StringComparer.Ordinal);
            return new SnapshotMessage { Columns = batch.Columns, Data = currentItems };
        }

        var patches = new List<PatchItem>();

        foreach (var current in currentItems)
        {
            if (_previousState.TryGetValue(current.Id, out var previous))
            {
                var changed = ComputeValueDiff(previous, current);
                if (changed.Count > 0)
                    patches.Add(new PatchItem { Id = current.Id, Values = changed });
            }
            else
            {
                // A row the client has not seen: send every value, but still no column metadata —
                // the shape was fixed by the snapshot.
                patches.Add(new PatchItem
                {
                    Id = current.Id,
                    Values = current.Values.ToDictionary(v => v.Key, v => v.Value),
                });
            }
        }

        _previousState = currentItems.ToDictionary(item => item.Id, StringComparer.Ordinal);

        return patches.Count == 0 ? null : new PatchMessage { Updated = [.. patches] };
    }

    private static Dictionary<string, object?> ComputeValueDiff(QueryResultItem previous, QueryResultItem current)
    {
        var changed = new Dictionary<string, object?>();

        foreach (var value in current.Values)
        {
            var before = previous.Values.FirstOrDefault(v => v.Key == value.Key);
            if (before is null || !ValuesEqual(before.Value, value.Value))
                changed[value.Key] = value.Value;
        }

        return changed;
    }

    private static bool ValuesEqual(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return a.Equals(b);
    }
}
