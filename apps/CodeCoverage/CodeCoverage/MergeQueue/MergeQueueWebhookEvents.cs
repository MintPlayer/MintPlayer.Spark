using System.ComponentModel;
using CodeCoverage.Entities;
using MintPlayer.Spark.Apps;

namespace CodeCoverage.MergeQueue;

/// <summary>#691 D41: the pull request a merge-queue event is about.</summary>
public sealed record MergeQueuePullRequest(
    [property: Description("owner/name on the forge.")] string Repository,
    [property: Description("github, gitlab or bitbucket.")] string Forge,
    int Number,
    string? Title,
    [property: Description("The branch it merges into.")] string BaseBranch,
    [property: Description("The queue run: each time the pull request is queued again, the run goes up.")] int Run,
    [property: Description("Who queued it.")] string EnqueuedBy)
{
    internal static MergeQueuePullRequest Of(MergeQueueEntry entry, Repository repository) => new(
        repository.FullName, repository.Provider.ToString().ToLowerInvariant(), entry.Number, entry.Title, entry.BaseBranch, entry.Run, entry.EnqueuedBy);
}

/// <summary>#691 D41: a pull request entered the merge queue.</summary>
[WebhookEvent("merge_queue.entry.queued", Permission = "merge_queue:read", Description = "A pull request entered the merge queue.")]
public sealed record MergeQueueEntryQueued(MergeQueuePullRequest PullRequest, DateTimeOffset QueuedAt);

/// <summary>#691 D41: the merge queue merged a pull request.</summary>
[WebhookEvent("merge_queue.entry.merged", Permission = "merge_queue:read", Description = "The merge queue merged a pull request.")]
public sealed record MergeQueueEntryMerged(MergeQueuePullRequest PullRequest, string? MergeCommitSha, DateTimeOffset MergedAt);

/// <summary>#691 D41: a pull request left the merge queue without being merged.</summary>
[WebhookEvent("merge_queue.entry.removed", Permission = "merge_queue:read", Description = "A pull request left the merge queue unmerged: evicted by a failure, or dequeued.")]
public sealed record MergeQueueEntryRemoved(
    MergeQueuePullRequest PullRequest,
    [property: Description("evicted (a check, conflict or step failed) or dequeued (removed by a person, closed, or merged outside the queue).")] string State,
    [property: Description("A short code for the eviction, e.g. checks-failed; null when dequeued.")] string? Reason,
    string? Message,
    DateTimeOffset RemovedAt);
