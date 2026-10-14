namespace CodeCoverage.GitWriter.Jobs;

// The writer's wire contract (camelCase JSON). Every request names the repository it works on and carries a short-lived,
// single-repository forge token minted for that one job (T9); nothing survives the job. Every answer is 200 with an
// outcome; a request that does not validate is a 400 with a RefusedResponse naming the field and its rule (§0 D57).

/// <summary>A forge credential, used for this job only and never stored.</summary>
public sealed class CredentialWire
{
    public string? Username { get; set; }
    public string? Password { get; set; }

    public override string ToString() => $"CredentialWire {{ Username = {Username} }}";
}

/// <summary>A commit identity. <see cref="Date"/> null lets git use the current time.</summary>
public sealed class IdentityWire
{
    public string? Name { get; set; }
    public string? Email { get; set; }
    public DateTimeOffset? Date { get; set; }
}

/// <summary>One auto-resolve entry (O1, O2): a path glob and the top-level JSON members whose lines may conflict, or
/// <see cref="AnyLine"/> for a file whose every line may (open question Q7).</summary>
public sealed class AutoResolveWire
{
    public string? Path { get; set; }
    public List<string>? Keys { get; set; }
    public bool AnyLine { get; set; }
}

/// <summary><c>POST /jobs/prepare</c> (§5.1 step 1).</summary>
public sealed class PrepareRequestWire
{
    public string? Provider { get; set; }
    public string? RepoId { get; set; }
    /// <summary>The base repository's https clone URL; the pull request's head is fetched from it too
    /// (<see cref="HeadRef"/>, e.g. <c>refs/pull/12/head</c>, which also exists for a fork's pull request).</summary>
    public string? CloneUrl { get; set; }
    public CredentialWire? Credential { get; set; }
    public string? BaseBranch { get; set; }
    /// <summary>When set, the base branch must still be at this commit, else <c>base-moved</c>.</summary>
    public string? BaseSha { get; set; }
    public string? HeadRef { get; set; }
    /// <summary>The head SHA pinned at enqueue (T3); a head at any other commit is <c>head-mismatch</c>.</summary>
    public string? HeadSha { get; set; }
    /// <summary><c>mq/&lt;base&gt;/pr-&lt;n&gt;</c> (D5).</summary>
    public string? QueueBranch { get; set; }
    /// <summary>The lease on the queue branch: the commit it must still be at, or null for "must not exist".</summary>
    public string? ExpectedQueueSha { get; set; }
    public List<AutoResolveWire>? AutoResolve { get; set; }
    public IdentityWire? Author { get; set; }
    public IdentityWire? Committer { get; set; }
    public string? Message { get; set; }

    /// <summary>
    /// Whether the pull request's net change may touch <c>.github/**</c> (§0 D63). False (the default, and a fork's
    /// pull request): such a change is evicted <c>touches-workflows</c> before anything is pushed, because the push to the
    /// queue branch alone would start the base repository's <c>push</c> workflows from the pull request's own workflow
    /// files. True: it is prepared and reported (<see cref="PrepareResponse.TouchesWorkflows"/>), and the saga decides.
    /// </summary>
    public bool AllowWorkflowChanges { get; set; }
}

/// <summary><c>POST /jobs/publish</c> (§5.1 step 3).</summary>
public sealed class PublishRequestWire
{
    public string? Provider { get; set; }
    public string? RepoId { get; set; }
    public string? CloneUrl { get; set; }
    /// <summary>Where the pull request's branch lives, when that is not <see cref="CloneUrl"/> (a fork, O3).</summary>
    public string? PushUrl { get; set; }
    public CredentialWire? Credential { get; set; }
    public string? QueueBranch { get; set; }
    /// <summary>The prepared commit; the queue branch must still be at it.</summary>
    public string? PreparedSha { get; set; }
    public string? HeadRef { get; set; }
    public string? PrBranch { get; set; }
    /// <summary>The lease on the pull request's branch: the head pinned at enqueue.</summary>
    public string? ExpectedPrHeadSha { get; set; }
    /// <summary>The regenerate artifact's <c>git diff</c>, base64; null or empty publishes the prepared tree as is.</summary>
    public string? Patch { get; set; }
    /// <summary>The globs the patch may touch (<c>coverage.yml</c> <c>regeneratedPaths</c>, read from the default branch,
    /// O16). May be empty when there is no regenerate step (§0 D57); a patch against an empty list is refused (T4).</summary>
    public List<string>? RegeneratedPaths { get; set; }
    public IdentityWire? Committer { get; set; }
}

/// <summary><c>DELETE /jobs/queue-branch</c>: removes <c>mq/…</c> when the entry ends (D5).</summary>
public sealed class DeleteQueueBranchWire
{
    public string? Provider { get; set; }
    public string? RepoId { get; set; }
    public string? CloneUrl { get; set; }
    public CredentialWire? Credential { get; set; }
    public string? QueueBranch { get; set; }
    /// <summary>When set, the branch is deleted only while it is at this commit (a lease).</summary>
    public string? ExpectedSha { get; set; }
}

/// <param name="Paths">Why, for <c>evicted</c>. For <c>prepared</c>: the <c>.github/**</c> paths of the net change when
/// <see cref="TouchesWorkflows"/>.</param>
public sealed record PrepareResponse(
    string Outcome, string? Reason, string? BaseSha, string? HeadSha, string? PreparedSha,
    IReadOnlyList<string> ResolvedPaths, IReadOnlyList<string> Paths, string? Detail, long DurationMs)
{
    /// <summary>The pull request's net change touches <c>.github/**</c> (D12): the saga decides what that stops (§0 D63).</summary>
    public bool TouchesWorkflows { get; init; }

    /// <summary>
    /// The base's <c>.github/workflows</c> differs from the pinned head's (§0 D59). A fork that holds only the head lacks
    /// the base's workflow files, so publishing to it needs the forge's workflow permission.
    /// </summary>
    public bool BaseChangedWorkflows { get; init; }
}

public sealed record PublishResponse(string Outcome, string? Reason, string? FinalSha, IReadOnlyList<string> Paths, string? Detail, long DurationMs);

public sealed record DeleteQueueBranchResponse(string Outcome, string? Reason, string? Detail, long DurationMs);

/// <summary>The body of a 400 (§0 D57): the first field that broke its rule, and the rule. Never the value.</summary>
public sealed record RefusedResponse(string Outcome, string Field, string Detail);

/// <summary>The <c>outcome</c> values.</summary>
public static class JobOutcomes
{
    /// <summary>Prepare: the squashed commit is on the queue branch.</summary>
    public const string Prepared = "prepared";
    /// <summary>Prepare: the content may not be merged by the bot; <c>reason</c> and <c>paths</c> say why.</summary>
    public const string Evicted = "evicted";
    /// <summary>Prepare: the pull request's head is not the pinned SHA (T3).</summary>
    public const string HeadMismatch = "head-mismatch";
    /// <summary>Prepare: the base branch is no longer at <c>baseSha</c>.</summary>
    public const string BaseMoved = "base-moved";
    /// <summary>The queue branch is not where the lease expected it (or, at publish, not at <c>preparedSha</c>).</summary>
    public const string QueueBranchMoved = "queue-branch-moved";
    /// <summary>Publish: the regenerate patch failed validation (T4); <c>reason</c> names the rule.</summary>
    public const string PatchRejected = "patch-rejected";
    /// <summary>Publish: the pull request's branch is not at <c>expectedPrHeadSha</c> (the author pushed), seen before the
    /// push or as the lease's rejection (§5.1 "branch moved during queue run").</summary>
    public const string BranchMoved = "branch-moved";
    /// <summary>Publish: the branch now holds the final commit.</summary>
    public const string Published = "published";
    /// <summary>The forge refused the push for another reason (a rule, a permission); <c>detail</c> has its message.</summary>
    public const string PushRejected = "push-rejected";
    /// <summary>Delete: the branch is gone (or was never there).</summary>
    public const string Deleted = "deleted";
    /// <summary>Every slot was taken for <c>SlotWaitSeconds</c>; retry later.</summary>
    public const string Busy = "busy";
    /// <summary>An operational failure; <c>reason</c> is one of <see cref="JobReasons"/>.</summary>
    public const string Failed = "failed";
    /// <summary>The 400's outcome: the request broke a rule, named in <see cref="RefusedResponse"/>. Sending it again
    /// changes nothing, so the saga never retries it (§0 D57).</summary>
    public const string Refused = "refused";
}

/// <summary>The <c>reason</c> of a failed job.</summary>
public static class JobReasons
{
    public const string Timeout = "timeout";
    public const string TooLarge = "too-large";
    public const string DiskFloor = "disk-floor";
    public const string Auth = "auth";
    public const string NotFound = "not-found";
    public const string Error = "error";
    /// <summary>D12, §0 D63: the pull request's net change touches <c>.github/**</c> and the request did not allow it
    /// (<see cref="PrepareRequestWire.AllowWorkflowChanges"/>); nothing was pushed.</summary>
    public const string TouchesWorkflows = "touches-workflows";
    /// <summary>
    /// Not a failure: <c>prepared</c> or <c>published</c> with the commit an earlier attempt of the same job already pushed,
    /// whose answer never reached the saga (merge-queue PRD D51 (1), D52). The branch was left as it was.
    /// </summary>
    public const string AlreadyPushed = "already-pushed";
}
