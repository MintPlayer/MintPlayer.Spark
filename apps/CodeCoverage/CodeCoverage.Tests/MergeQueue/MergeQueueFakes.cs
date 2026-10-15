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

    /// <summary>When set and it answers, its access is handed out instead of a minted one (e.g. a refusal).</summary>
    public Func<ForgePullRequest?, ForgePushAccess?>? WriterAccess { get; set; }

    /// <summary>Every writer credential minted (T9): the pull request it pushes (null: the queue branch only), whether it
    /// carries the workflow permission, and its token, in order.</summary>
    public List<(int? Number, bool Workflows, string Token)> WriterAccesses { get; } = [];

    /// <summary>Every writer credential ended, by token, in order.</summary>
    public List<string> RevokedTokens { get; } = [];

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

    /// <summary>The workflow files at each ref (§0 D65); a ref not listed has none.</summary>
    public Dictionary<string, List<ForgeWorkflowFile>> WorkflowFiles { get; } = new(StringComparer.Ordinal);

    /// <summary>Every ref whose workflow files were read, in order.</summary>
    public List<string> WorkflowFileReads { get; } = [];

    public Task<IReadOnlyList<ForgeWorkflowFile>> GetWorkflowFilesAsync(Repository repository, string gitRef, CancellationToken cancellationToken = default)
    {
        WorkflowFileReads.Add(gitRef);
        return Task.FromResult<IReadOnlyList<ForgeWorkflowFile>>(WorkflowFiles.TryGetValue(gitRef, out var files) ? [.. files] : []);
    }

    public Task<ForgeMergeRequest> RequestMergeAsync(Repository repository, int number, string sha, EForgeMergeMethod method, string? commitTitle, string? commitMessage, CancellationToken cancellationToken = default)
    {
        if (MergeThrows is { } thrown)
        {
            MergeThrows = null;
            throw thrown;
        }
        MergeRequests.Add((number, sha));
        MergeMessages.Add((commitTitle, commitMessage));
        return Task.FromResult(MergeReplies.Count > 0 ? MergeReplies.Dequeue() : new ForgeMergeRequest(EForgeMergeState.Merged, null, "feedfacefeedfacefeedfacefeedfacefeedface", null));
    }

    /// <summary>The squash commit's title and message each merge request asked for, in order.</summary>
    public List<(string? Title, string? Message)> MergeMessages { get; } = [];

    public Task<ForgeMergeRequest> GetMergeRequestStatusAsync(Repository repository, int number, string requestId, CancellationToken cancellationToken = default)
        => Task.FromResult(MergeStatuses.Count > 0 ? MergeStatuses.Dequeue() : new ForgeMergeRequest(EForgeMergeState.Pending, requestId, null, null));

    public Task<IReadOnlyList<ForgeReview>> GetReviewsAsync(Repository repository, int number, CancellationToken cancellationToken = default)
        => Task.FromResult((IReadOnlyList<ForgeReview>)Reviews);

    /// <summary>The pull request's commits, oldest first (§0 D60).</summary>
    public List<ForgePullRequestCommit> Commits { get; } = [];

    public Task<IReadOnlyList<ForgePullRequestCommit>> GetPullRequestCommitsAsync(Repository repository, int number, CancellationToken cancellationToken = default)
        => Task.FromResult((IReadOnlyList<ForgePullRequestCommit>)Commits);

    /// <summary>Every open pull request, on any base; <see cref="ListOpenPullRequestsAsync"/> answers those on the asked one.</summary>
    public List<ForgeOpenPullRequest> OpenPullRequests { get; } = [];

    /// <summary>Every base branch whose open pull requests were listed, in order.</summary>
    public List<string> OpenPullRequestQueries { get; } = [];

    /// <summary>When set, the next listing of open pull requests throws it (a missing permission, a 5xx).</summary>
    public Exception? ListOpenPullRequestsThrows { get; set; }

    public Task<IReadOnlyList<ForgeOpenPullRequest>> ListOpenPullRequestsAsync(Repository repository, string baseBranch, CancellationToken cancellationToken = default)
    {
        OpenPullRequestQueries.Add(baseBranch);
        if (ListOpenPullRequestsThrows is { } thrown)
        {
            ListOpenPullRequestsThrows = null;
            throw thrown;
        }
        return Task.FromResult((IReadOnlyList<ForgeOpenPullRequest>)[.. OpenPullRequests.Where(p => p.BaseRef == baseBranch)]);
    }

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

    public Task<ForgePushAccess> GetWriterAccessAsync(Repository repository, ForgePullRequest? pullRequest, bool pushesWorkflowChanges, CancellationToken cancellationToken = default)
    {
        if (WriterAccess?.Invoke(pullRequest) is { } scripted)
            return Task.FromResult(scripted);
        var token = $"job-token-{WriterAccesses.Count + 1}";
        WriterAccesses.Add((pullRequest?.Number, pushesWorkflowChanges, token));
        var push = pullRequest is { IsFromFork: true, HeadRepositoryFullName: { } fork } ? fork : repository.FullName;
        return Task.FromResult(new ForgePushAccess(
            new ForgeWriterAccess($"https://github.com/{repository.FullName}.git", $"https://github.com/{push}.git", "x-access-token", token), null));
    }

    public Task<bool> RevokeWriterAccessAsync(Repository repository, ForgeWriterAccess access, CancellationToken cancellationToken = default)
    {
        RevokedTokens.Add(access.Password);
        return Task.FromResult(true);
    }

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

    /// <summary>Scripted answers to the queue-branch push (§0 D65); when none is left it answers <c>pushed</c>.</summary>
    public Queue<GitWriterQueueBranchResult> QueueBranchResults { get; } = new();
    public List<GitWriterQueueBranchRequest> QueueBranchPushes { get; } = [];

    public Task<GitWriterPrepareResult> PrepareAsync(GitWriterPrepareRequest request, CancellationToken cancellationToken = default)
    {
        Prepares.Add(request);
        return Task.FromResult(PrepareResults.Dequeue());
    }

    public Task<GitWriterQueueBranchResult> PushQueueBranchAsync(GitWriterQueueBranchRequest request, CancellationToken cancellationToken = default)
    {
        QueueBranchPushes.Add(request);
        return Task.FromResult(QueueBranchResults.Count > 0 ? QueueBranchResults.Dequeue() : Pushed(request.PreparedSha));
    }

    public static GitWriterQueueBranchResult Pushed(string sha, string? reason = null)
        => new(GitWriterOutcomes.Pushed, reason, sha, [], null, 10);

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
