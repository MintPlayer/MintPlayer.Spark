namespace CodeCoverage.Entities;

/// <summary>
/// The merge queue's latest check run on one pull request, whatever put it there: the "Not queued" offer, a run of the
/// queue, or the check a run ended with (merge-queue PRD §0 O32, D89). The coverage comment links it, so a reader lands on
/// the check that shows the queue's state and carries its button.
/// </summary>
/// <remarks>
/// A run's own check id stays on its <see cref="MergeQueueEntry"/>, through which the saga updates that check. This one is
/// only ever a pointer for people, written by the saga each time it starts a new check run.
/// </remarks>
public class MergeQueueLatestCheck
{
    public string? Id { get; set; }

    /// <summary>The repository document.</summary>
    public string Repository { get; set; } = string.Empty;

    public int Number { get; set; }

    /// <summary>The commit the check run is on.</summary>
    public string Sha { get; set; } = string.Empty;

    /// <summary>The forge's id for the check run (GitHub's <c>check_run_id</c>).</summary>
    public long CheckRunId { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    /// <summary><c>MergeQueueLatestChecks/{provider}/{repositoryId}/{number}</c>.</summary>
    public static string DocumentId(string repositoryId, int number)
        => $"MergeQueueLatestChecks/{MergeQueue.RepositorySuffix(repositoryId)}/{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}
