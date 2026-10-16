namespace CodeCoverage.GitWriter.Jobs.Dependencies;

// The dependency updater's writer jobs (docs/code-coverage/dependency-updates-PRD.md R4, R14, R16, P6). Same contract as the
// merge queue's: camelCase JSON, one single-repository token per job (T9), a 400 naming the first refused field (§0 D57), and
// every answer a 200 with an outcome.

/// <summary>The fields every dependency job carries: where the repository is, the job's credential, and the commit the run
/// planned against (<c>expected_sha</c>) on the default branch.</summary>
public class DependencyJobWire
{
    public string? Provider { get; set; }
    public string? RepoId { get; set; }
    public string? CloneUrl { get; set; }
    public CredentialWire? Credential { get; set; }

    /// <summary>The default branch: cloned blob-less, single-branch.</summary>
    public string? BaseBranch { get; set; }

    /// <summary>The commit the lookup and the plan were made against.</summary>
    public string? ExpectedSha { get; set; }
}

/// <summary>The fields of a job that builds a commit: dated identities (§0 D65) and a message.</summary>
public class DependencyCommitJobWire : DependencyJobWire
{
    public IdentityWire? Author { get; set; }
    public IdentityWire? Committer { get; set; }
    public string? Message { get; set; }
}

/// <summary>The fields of a job that judges the apply patch: the plan, the patch, what <c>result.json</c> claims.</summary>
public class DependencyPatchJobWire : DependencyCommitJobWire
{
    /// <summary>The plan the bot committed (plan.schema.json), as JSON text.</summary>
    public string? Plan { get; set; }

    /// <summary>The apply run's <c>dependency.patch</c>, base64; empty for a run that applied nothing.</summary>
    public string? Patch { get; set; }

    /// <summary>The groups <c>result.json</c> says it applied (D-V5: they must equal the patch's).</summary>
    public List<string>? ClaimedGroups { get; set; }

    /// <summary>R15: the target <c>action.yml</c> per new <c>uses:</c> value, as the bot read it.</summary>
    public Dictionary<string, string>? TargetActions { get; set; }

    /// <summary>Gap B: the registry hosts a lockfile may name besides the public ones (the account's allow-list).</summary>
    public List<string>? AllowedHosts { get; set; }
}

/// <summary>
/// <c>POST /jobs/dependencies/work-ref</c> (R14): <c>mintplayer/dependencies-work</c> = <c>expected_sha</c> + ONE commit.
/// With <see cref="PlanJson"/>, the commit adds the plan file and nothing else (<see cref="PlanCommitRule"/>); with a
/// <see cref="DependencyPatchJobWire.Patch"/>, it is the validated apply patch, the commit a migrate run builds (R9).
/// </summary>
public sealed class DependencyWorkRefWire : DependencyPatchJobWire
{
    public string? PlanJson { get; set; }

    /// <summary>The lease: the commit the work ref must still be at; null replaces whatever is there (a bot-owned ref a
    /// crashed run may have left).</summary>
    public string? ExpectedWorkSha { get; set; }
}

/// <summary><c>POST /jobs/dependencies/validate</c>: the <c>dependencies</c> profile on the apply patch, nothing pushed.</summary>
public sealed class DependencyValidateWire : DependencyPatchJobWire;

/// <summary>One migration's patch (R9), applied after the apply patch in the order given.</summary>
public sealed class DependencyMigratePatchWire
{
    public string? Group { get; set; }
    public string? Patch { get; set; }
}

/// <summary>
/// <c>POST /jobs/dependencies/rolling</c> (R4, P6): validates the apply patch and every migration patch, builds the bot commit
/// on <c>expected_sha</c>, replays the human commits of the old rolling branch onto it, and force-pushes
/// <c>mintplayer/dependencies</c> under a lease, unless the tree equals the branch's (no push, no CI).
/// </summary>
public sealed class DependencyRollingWire : DependencyPatchJobWire
{
    public List<DependencyMigratePatchWire>? Migrations { get; set; }

    /// <summary>The rolling branch's head as the bot read it right before; null: there is none.</summary>
    public string? ExpectedRollingSha { get; set; }

    /// <summary>The bot's own commit of the previous build; the commits above it on the branch are the humans'.</summary>
    public string? LastBotSha { get; set; }
}

/// <summary>One file of the onboarding commit: its text, or <see cref="Delete"/>.</summary>
public sealed class DependencyFileWire
{
    public string? Path { get; set; }
    public string? Content { get; set; }
    public bool Delete { get; set; }
}

/// <summary>
/// <c>POST /jobs/dependencies/files</c> (R16): one commit on <c>expected_sha</c> writing a closed set of paths (the workflow
/// templates, <c>coverage.yml</c>, <c>.github/dependabot.yml</c>), pushed to <c>mintplayer/dependencies-onboarding</c>.
/// </summary>
public sealed class DependencyFilesWire : DependencyCommitJobWire
{
    public List<DependencyFileWire>? Files { get; set; }
}

/// <summary><c>DELETE /jobs/dependencies/work-ref</c>: removes the work ref after a run (R14), only while it is at
/// <see cref="DependencyJobWire.ExpectedSha"/> (the lease; null deletes it wherever it is). No clone.</summary>
public sealed class DependencyDeleteRefWire : DependencyJobWire;

/// <summary>A job's answer.</summary>
/// <param name="Sha">The commit the job pushed or found (<c>pushed</c>, <c>unchanged</c>).</param>
/// <param name="BotSha">Rolling: the bot's own commit under the replayed ones.</param>
public sealed record DependencyJobResponse(string Outcome, string? Reason, string? Sha, string? BotSha, IReadOnlyList<string> Paths, string? Detail, long DurationMs)
{
    /// <summary>Validate: <c>accepted</c>, <c>rejected</c> or <c>needs-human</c>.</summary>
    public string? Verdict { get; init; }

    public IReadOnlyList<string> AppliedGroups { get; init; } = [];

    public bool RequiresWorkflowsPermission { get; init; }

    public IReadOnlyList<string> NewPackages { get; init; } = [];

    /// <summary>Rolling: the human manifest values found while replaying (Q11 holds).</summary>
    public IReadOnlyList<HumanValueWire> HumanValues { get; init; } = [];

    /// <summary>Rolling: how many human commits were replayed.</summary>
    public int Replayed { get; init; }
}

public sealed record HumanValueWire(string DepName, string PackageFile, string HeldValue, string? ProposedValue, string Commit, string? Author);

/// <summary>The dependency jobs' own outcomes, beside <see cref="JobOutcomes"/>.</summary>
public static class DependencyJobOutcomes
{
    /// <summary>Validate: the verdict is in <see cref="DependencyJobResponse.Verdict"/>.</summary>
    public const string Validated = "validated";

    /// <summary>Rolling: the rebuilt tree equals the branch's; nothing was pushed (R4).</summary>
    public const string Unchanged = "unchanged";

    /// <summary>Rolling: a human commit conflicts outside a planned manifest; nothing was pushed (§7.3).</summary>
    public const string ReplayConflict = "replay-conflict";

    /// <summary>Rolling: the bot's last commit is no longer under the branch's head; nothing was pushed.</summary>
    public const string RollingRewritten = "rolling-rewritten";

    /// <summary>Work ref: the plan commit broke <see cref="PlanCommitRule"/> (a bug in the request; nothing was pushed).</summary>
    public const string PlanRejected = "plan-rejected";

    /// <summary>The branch is not where the lease expected it.</summary>
    public const string BranchMoved = "branch-moved";

    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
    public const string NeedsHuman = "needs-human";
}
