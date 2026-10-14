namespace CodeCoverage.Entities;

/// <summary>
/// One repository's merge queue: a serial lane per base branch (merge-queue PRD §5.5, D11).
/// </summary>
/// <remarks>
/// <para>
/// <b>One document per repository, with a lane per base</b>, rather than one per (repository, base). The webhook
/// normaliser asks "does this repository have anything queued?" on every check-run and status delivery, which are the
/// most frequent events the App receives; a single point load by a deterministic id answers it without a query, and
/// without an index (§0 D43).
/// </para>
/// <para>
/// A plain document, not a Spark persistent object: nothing in the model, no rights. The queue's view on the
/// repository page is M6's.
/// </para>
/// </remarks>
public class MergeQueue
{
    public string? Id { get; set; }

    /// <summary>The repository document id, <c>Repositories/{provider}/{id}</c>.</summary>
    public string Repository { get; set; } = string.Empty;

    /// <summary>One lane per base branch that has ever had an entry.</summary>
    public List<MergeQueueLane> Lanes { get; set; } = [];

    /// <summary>Whether any lane holds a running or waiting entry: the webhook filter.</summary>
    public bool IsBusy() => Lanes.Any(lane => lane.Current is not null || lane.Waiting.Count > 0);

    /// <summary>The lane of <paramref name="baseBranch"/>, created when absent.</summary>
    public MergeQueueLane LaneFor(string baseBranch)
    {
        var lane = Lanes.FirstOrDefault(l => string.Equals(l.BaseBranch, baseBranch, StringComparison.Ordinal));
        if (lane is null)
        {
            lane = new MergeQueueLane { BaseBranch = baseBranch };
            Lanes.Add(lane);
        }
        return lane;
    }

    /// <summary><c>MergeQueues/{provider}/{repositoryId}</c>, from the repository's own id.</summary>
    public static string DocumentId(string repositoryId) => $"MergeQueues/{RepositorySuffix(repositoryId)}";

    /// <summary>The part of a repository document id after <c>Repositories/</c>.</summary>
    internal static string RepositorySuffix(string repositoryId)
    {
        const string prefix = "Repositories/";
        ArgumentException.ThrowIfNullOrEmpty(repositoryId);
        if (!repositoryId.StartsWith(prefix, StringComparison.Ordinal) || repositoryId.Length == prefix.Length)
            throw new ArgumentException($"'{repositoryId}' is not a repository document id.", nameof(repositoryId));
        return repositoryId[prefix.Length..];
    }
}

/// <summary>One base branch's serial queue (D11): at most one entry runs, the others wait in order.</summary>
public class MergeQueueLane
{
    public string BaseBranch { get; set; } = string.Empty;

    /// <summary>The entry being prepared, regenerated, checked or merged; null when the lane is idle.</summary>
    public string? Current { get; set; }

    /// <summary>The entries waiting their turn, first in line first. Never contains <see cref="Current"/>.</summary>
    public List<string> Waiting { get; set; } = [];

    /// <summary>A paused lane finishes its current entry and starts no next one.</summary>
    public bool Paused { get; set; }

    /// <summary>1 for the running entry, 2.. for the waiting ones; null when the entry is not in this lane.</summary>
    public int? PositionOf(string entryId)
    {
        if (Current == entryId) return 1;
        var index = Waiting.IndexOf(entryId);
        return index < 0 ? null : index + (Current is null ? 1 : 2);
    }
}
