using System.Collections.Concurrent;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Tests.MergeQueue;

/// <summary>A scripted <see cref="IForgeMergeQueue"/>: facts in dictionaries, every write recorded.</summary>
internal sealed class FakeForgeMergeQueue : IForgeMergeQueue
{
    public EForgeProvider Provider => EForgeProvider.GitHub;

    public Dictionary<string, EForgeRepositoryRole> Roles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ForgeBranchRules Rules { get; set; } = new(true, [], 0, false, false, false, false, [], EForgeClassicProtection.None);
    public Dictionary<string, string> BranchHeads { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ForgeCheckState> CheckStates { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, IReadOnlyList<string>> CheckNames { get; } = new(StringComparer.Ordinal);
    public Dictionary<long, ForgeWorkflowRun> Runs { get; } = [];
    public Dictionary<long, ForgeArtifactDownload> Artifacts { get; } = [];
    public Dictionary<string, string> Blobs { get; } = new(StringComparer.Ordinal);
    public List<ForgeReview> Reviews { get; } = [];
    public Queue<ForgeMergeRequest> MergeReplies { get; } = new();
    public Queue<ForgeMergeRequest> MergeStatuses { get; } = new();
    public ForgeWorkflowDispatch NextDispatch { get; set; } = new(EForgeDispatchOutcome.Dispatched, 9001, "https://github.com/acme/widget/actions/runs/9001", null);
    public bool RerunResult { get; set; } = true;
    public Func<ForgePullRequest, ForgePushAccess>? PushAccess { get; set; }

    public List<(string Workflow, string Branch, IReadOnlyDictionary<string, string> Inputs)> Dispatches { get; } = [];
    public List<(int Number, string Sha)> MergeRequests { get; } = [];
    public List<(string Sha, ForgeQueueCheck Check, long? ExistingId)> QueueChecks { get; } = [];
    public List<(int Number, string Label)> RemovedLabels { get; } = [];
    public List<(int Number, string Body)> Comments { get; } = [];
    public List<long> Reruns { get; } = [];
    public List<string> RoleQueries { get; } = [];

    /// <summary>When set, the next merge request throws it (a missing permission, a 5xx).</summary>
    public Exception? MergeThrows { get; set; }

    private long nextCheckId = 100;

    public Task<EForgeRepositoryRole> GetActorRoleAsync(Repository repository, string login, CancellationToken cancellationToken = default)
    {
        RoleQueries.Add(login);
        return Task.FromResult(Roles.TryGetValue(login, out var role) ? role : EForgeRepositoryRole.None);
    }

    public Task<ForgeBranchRules> GetBranchRulesAsync(Repository repository, string branch, CancellationToken cancellationToken = default)
        => Task.FromResult(Rules);

    public Task<string?> GetBranchHeadAsync(Repository repository, string branch, CancellationToken cancellationToken = default)
        => Task.FromResult(BranchHeads.TryGetValue(branch, out var sha) ? sha : null);

    public Task<ForgeCheckState> GetCheckStateAsync(Repository repository, string sha, CancellationToken cancellationToken = default)
        => Task.FromResult(CheckStates.TryGetValue(sha, out var state) ? state : new ForgeCheckState(sha, [], [], []));

    public Task<bool> RerunCheckAsync(Repository repository, ForgeCheckRun checkRun, CancellationToken cancellationToken = default)
    {
        Reruns.Add(checkRun.Id);
        return Task.FromResult(RerunResult);
    }

    public Task<IReadOnlyList<string>> GetCheckNamesOnCommitAsync(Repository repository, string sha, CancellationToken cancellationToken = default)
        => Task.FromResult(CheckNames.TryGetValue(sha, out var names) ? names : (IReadOnlyList<string>)[]);

    /// <summary>The runs a dispatch started, so far: only these exist for <see cref="ListWorkflowRunsAsync"/>.</summary>
    private readonly HashSet<long> started = [];

    /// <summary>
    /// When set, the next dispatch starts its run and then throws it: the forge created the run, but the answer that names
    /// it never reached the saga.
    /// </summary>
    public Exception? DispatchThrows { get; set; }

    public Task<ForgeWorkflowDispatch> DispatchWorkflowAsync(Repository repository, string workflowFile, string branch, IReadOnlyDictionary<string, string> inputs, CancellationToken cancellationToken = default)
    {
        Dispatches.Add((workflowFile, branch, inputs));
        if (NextDispatch.RunId is { } runId) started.Add(runId);
        if (DispatchThrows is { } thrown)
        {
            DispatchThrows = null;
            throw thrown;
        }
        return Task.FromResult(NextDispatch);
    }

    public Task<IReadOnlyList<ForgeWorkflowRun>> ListWorkflowRunsAsync(Repository repository, string workflowFile, string branch, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ForgeWorkflowRun>>([.. Runs.Values.Where(r => started.Contains(r.Id) && r.HeadBranch == branch).OrderByDescending(r => r.Id)]);

    public Task<ForgeWorkflowRun?> GetWorkflowRunAsync(Repository repository, long runId, CancellationToken cancellationToken = default)
        => Task.FromResult(Runs.TryGetValue(runId, out var run) ? run : null);

    /// <summary>Every run the saga asked to cancel, in order.</summary>
    public List<long> CancelledRuns { get; } = [];

    /// <summary>When set, the next cancellation throws it (a missing permission, a 5xx).</summary>
    public Exception? CancelThrows { get; set; }

    public Task<bool> CancelWorkflowRunAsync(Repository repository, long runId, CancellationToken cancellationToken = default)
    {
        if (CancelThrows is { } thrown)
        {
            CancelThrows = null;
            throw thrown;
        }
        CancelledRuns.Add(runId);
        if (!Runs.TryGetValue(runId, out var run) || run.IsCompleted) return Task.FromResult(false);
        Runs[runId] = run with { Status = "completed", Conclusion = "cancelled" };
        return Task.FromResult(true);
    }

    public Task<ForgeArtifactDownload> DownloadArtifactAsync(Repository repository, long runId, string artifactName, long maxBytes, CancellationToken cancellationToken = default)
        => Task.FromResult(Artifacts.TryGetValue(runId, out var artifact) ? artifact : new ForgeArtifactDownload(EForgeArtifactOutcome.NotFound, null, null, "no artifact"));

    public Task<string?> GetFileBlobShaAsync(Repository repository, string gitRef, string path, CancellationToken cancellationToken = default)
        => Task.FromResult(Blobs.TryGetValue($"{gitRef}:{path}", out var blob) ? blob : null);

    public Task<ForgeMergeRequest> RequestMergeAsync(Repository repository, int number, string sha, EForgeMergeMethod method, string? commitTitle, string? commitMessage, CancellationToken cancellationToken = default)
    {
        if (MergeThrows is { } thrown)
        {
            MergeThrows = null;
            throw thrown;
        }
        MergeRequests.Add((number, sha));
        return Task.FromResult(MergeReplies.Count > 0 ? MergeReplies.Dequeue() : new ForgeMergeRequest(EForgeMergeState.Merged, null, "feedfacefeedfacefeedfacefeedfacefeedface", null));
    }

    public Task<ForgeMergeRequest> GetMergeRequestStatusAsync(Repository repository, int number, string requestId, CancellationToken cancellationToken = default)
        => Task.FromResult(MergeStatuses.Count > 0 ? MergeStatuses.Dequeue() : new ForgeMergeRequest(EForgeMergeState.Pending, requestId, null, null));

    public Task<IReadOnlyList<ForgeReview>> GetReviewsAsync(Repository repository, int number, CancellationToken cancellationToken = default)
        => Task.FromResult((IReadOnlyList<ForgeReview>)Reviews);

    public Task<long> PublishQueueCheckAsync(Repository repository, string sha, ForgeQueueCheck check, long? existingId, CancellationToken cancellationToken = default)
    {
        QueueChecks.Add((sha, check, existingId));
        return Task.FromResult(existingId ?? nextCheckId++);
    }

    public Task AddLabelAsync(Repository repository, int number, string label, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<bool> RemoveLabelAsync(Repository repository, int number, string label, CancellationToken cancellationToken = default)
    {
        RemovedLabels.Add((number, label));
        return Task.FromResult(true);
    }

    public Task<long> PostCommentAsync(Repository repository, int number, string body, CancellationToken cancellationToken = default)
    {
        Comments.Add((number, body));
        return Task.FromResult((long)Comments.Count);
    }

    public Task<ForgePushAccess> GetPushAccessAsync(Repository repository, ForgePullRequest pullRequest, CancellationToken cancellationToken = default)
        => Task.FromResult(PushAccess?.Invoke(pullRequest)
            ?? new ForgePushAccess(new ForgeGitAccess($"https://github.com/{pullRequest.HeadRepositoryFullName ?? repository.FullName}.git", "x-access-token", "test-token", "github:1"), null));

    public ForgeQueueCheck LastCheck => QueueChecks[^1].Check;
}

/// <summary>A scripted git writer: answers in order, records every request.</summary>
internal sealed class FakeGitWriter : IGitWriterSidecar
{
    public bool IsConfigured { get; set; } = true;

    public Queue<GitWriterPrepareResult> PrepareResults { get; } = new();
    public Queue<GitWriterPublishResult> PublishResults { get; } = new();
    public List<GitWriterPrepareRequest> Prepares { get; } = [];
    public List<GitWriterPublishRequest> Publishes { get; } = [];
    public List<GitWriterDeleteQueueBranchRequest> Deletes { get; } = [];

    public Task<GitWriterPrepareResult> PrepareAsync(GitWriterPrepareRequest request, CancellationToken cancellationToken = default)
    {
        Prepares.Add(request);
        return Task.FromResult(PrepareResults.Dequeue());
    }

    /// <summary>
    /// When set, the next publish is recorded and then throws it: the writer pushed, but its answer never reached the saga
    /// (a lost response; to the saga it is the same as the app dying before it saved).
    /// </summary>
    public Exception? PublishThrows { get; set; }

    public Task<GitWriterPublishResult> PublishAsync(GitWriterPublishRequest request, CancellationToken cancellationToken = default)
    {
        Publishes.Add(request);
        if (PublishThrows is { } thrown)
        {
            PublishThrows = null;
            throw thrown;
        }
        return Task.FromResult(PublishResults.Dequeue());
    }

    public Task<GitWriterDeleteResult> DeleteQueueBranchAsync(GitWriterDeleteQueueBranchRequest request, CancellationToken cancellationToken = default)
    {
        Deletes.Add(request);
        return Task.FromResult(new GitWriterDeleteResult(GitWriterOutcomes.Deleted, null, null, 1));
    }

    public static GitWriterPrepareResult Prepared(string baseSha, string head, string prepared, params string[] resolved)
        => new(GitWriterOutcomes.Prepared, null, baseSha, head, prepared, resolved, [], null, 10);

    public static GitWriterPublishResult Published(string final, params string[] paths)
        => new(GitWriterOutcomes.Published, null, final, paths, null, 10);
}

/// <summary>The outbox, recording: a test delivers what the saga queued, in order.</summary>
internal sealed class FakeOutbox : IMessageOutbox
{
    public ConcurrentQueue<(object Message, BroadcastOptions? Options)> Messages { get; } = new();

    public Task EnqueueAsync<TMessage>(IAsyncDocumentSession session, TMessage message, BroadcastOptions? options = null, CancellationToken cancellationToken = default)
    {
        Messages.Enqueue((message!, options));
        return Task.CompletedTask;
    }
}
