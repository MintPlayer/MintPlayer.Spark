using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Streaming;

/// <summary>
/// Per-connection stateful diff calculator for streaming queries.
/// Tracks previous PersistentObject state and computes patch messages
/// containing only changed attribute values.
/// </summary>
internal sealed class StreamingDiffEngine
{
    private Dictionary<string, PersistentObject>? _previousState;

    /// <summary>
    /// Computes a streaming message by comparing current items against previous state.
    /// First call returns a SnapshotMessage. Subsequent calls return PatchMessage with only changes,
    /// or null if nothing changed.
    /// </summary>
    public StreamingMessage? ComputeMessage(PersistentObject[] currentItems)
    {
        if (_previousState is null)
        {
            // First call: send full snapshot
            _previousState = currentItems
                .Where(po => po.Id is not null)
                .ToDictionary(po => po.Id!);
            return new SnapshotMessage { Data = currentItems };
        }

        // Subsequent calls: compute diff
        var patches = new List<PatchItem>();

        foreach (var current in currentItems)
        {
            if (current.Id is null) continue;

            if (_previousState.TryGetValue(current.Id, out var previous))
            {
                var changedAttributes = ComputeAttributeDiff(previous, current);
                if (changedAttributes.Count > 0)
                {
                    patches.Add(new PatchItem
                    {
                        Id = current.Id,
                        Attributes = changedAttributes
                    });
                }
            }
            else
            {
                // New item — send all attribute values as a patch (not full metadata)
                patches.Add(new PatchItem
                {
                    Id = current.Id,
                    Attributes = current.Attributes
                        .ToDictionary(a => a.Name, a => a.Value)
                });
            }
        }

        // Update stored state
        _previousState = currentItems
            .Where(po => po.Id is not null)
            .ToDictionary(po => po.Id!);

        if (patches.Count == 0)
            return null;

        return new PatchMessage { Updated = patches.ToArray() };
    }

    private static Dictionary<string, object?> ComputeAttributeDiff(
        PersistentObject previous, PersistentObject current)
    {
        var changed = new Dictionary<string, object?>();

        foreach (var currentAttr in current.Attributes)
        {
            var previousAttr = previous.Attributes.FirstOrDefault(a => a.Name == currentAttr.Name);
            if (previousAttr is null)
            {
                // New attribute
                changed[currentAttr.Name] = currentAttr.Value;
            }
            else if (!ValuesEqual(previousAttr.Value, currentAttr.Value))
            {
                changed[currentAttr.Name] = currentAttr.Value;
            }
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
