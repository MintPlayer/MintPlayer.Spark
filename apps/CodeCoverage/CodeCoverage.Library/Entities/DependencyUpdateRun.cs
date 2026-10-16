namespace CodeCoverage.Entities;

/// <summary>
/// The dependency updater's saga state for one repository (docs/code-coverage/dependency-updates-PRD.md §13.8 N5–N7, R3, R4,
/// R14, R16): which run is going, which step it is at, the workflow runs it waits for, the rolling branch and its pull
/// request. The merge queue's <see cref="MergeQueueEntry"/> is the model: a <see cref="Run"/> per trigger, a
/// <see cref="Generation"/> per state change, so a step for a state the run has left is dropped.
/// </summary>
/// <remarks>
/// <para>
/// One document per repository, <c>DependencyUpdateRuns/{provider}/{id}</c>, always loaded by id (no query, so no RQL to
/// splice into). The bulky inputs of a run (the lookup report, the plan, the patches) live in
/// <see cref="DependencyUpdateRunData"/>, loaded only by the steps that read them.
/// </para>
/// <para>Not a Spark entity: nothing here is edited through the generic UI.</para>
/// </remarks>
public class DependencyUpdateRun
{
    public const string Prefix = "DependencyUpdateRuns/";
    public const int MaxHistory = 60;

    /// <summary>The bot-owned rolling branch (§7.3, R4).</summary>
    public const string RollingBranch = "mintplayer/dependencies";

    /// <summary>The bot-owned dispatch ref (R14): <c>expected_sha</c> + the plan commit, or + the applied group for a migrate run.</summary>
    public const string WorkBranch = "mintplayer/dependencies-work";

    /// <summary>The onboarding pull request's branch (R16).</summary>
    public const string OnboardingBranch = "mintplayer/dependencies-onboarding";

    public string? Id { get; set; }

    public string Repository { get; set; } = string.Empty;

    public int Run { get; set; }

    public int Generation { get; set; }

    public EDependencyRunState State { get; set; }

    public EDependencyRunTrigger Trigger { get; set; }

    /// <summary>A trigger that arrived while a run was going; it starts as soon as the run ends (coalesced, strongest kept).</summary>
    public EDependencyRunTrigger? PendingTrigger { get; set; }

    /// <summary>R10: the vulnerable dependencies of a security trigger (pending or running).</summary>
    public List<DependencySecurityFixRecord> SecurityFixes { get; set; } = [];

    public DateTime? StartedAtUtc { get; set; }

    public DateTime? StateChangedAtUtc { get; set; }

    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>Why the last run ended (<c>published</c>, <c>unchanged</c>, <c>nothing-planned</c>, <c>lookup-failed</c>, …).</summary>
    public string? Outcome { get; set; }

    public string? OutcomeMessage { get; set; }

    // ── The run ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The default branch when the run started, and its commit: the lookup's and the plan's <c>expected_sha</c>.</summary>
    public string? DefaultBranch { get; set; }

    public string? ExpectedSha { get; set; }

    /// <summary>The blob id of the default branch's <c>coverage.yml</c> (gap E: approvals are bound to it).</summary>
    public string ConfigBlob { get; set; } = string.Empty;

    /// <summary>D-P13: the default branch's <c>dependencies.schedule</c> parts as last read, so the scheduler honours them
    /// without reading every repository's file on every tick.</summary>
    public LookupReferences.EDependencyScheduleFrequency? FileFrequency { get; set; }

    public LookupReferences.EDependencyWeekday? FileWeekday { get; set; }

    public int? FileHour { get; set; }

    /// <summary>The file opted out (<c>dependencies.enabled: false</c>) when last read.</summary>
    public bool FileDisabled { get; set; }

    public string LookupAction { get; set; } = string.Empty;

    public string ApplyAction { get; set; } = string.Empty;

    /// <summary>The lookup, apply and migrate runs of this run, in dispatch order.</summary>
    public List<DependencyWorkflowRun> Runs { get; set; } = [];

    public string? PlanId { get; set; }

    /// <summary>R14: the plan commit on <see cref="WorkBranch"/>; the apply run must build exactly it.</summary>
    public string? PlanCommitSha { get; set; }

    /// <summary>Where <see cref="WorkBranch"/> was last pushed to, the lease of its next push and of its deletion.</summary>
    public string? WorkRefSha { get; set; }

    /// <summary>The commit the migrate runs build: <c>expected_sha</c> + the validated apply patch, on <see cref="WorkBranch"/>.</summary>
    public string? StagedSha { get; set; }

    /// <summary>The patch verdict: <c>accepted</c> or <c>needs-human</c> (gap B's new-package cap).</summary>
    public string? PatchVerdict { get; set; }

    /// <summary>The groups the validated patch applies (derived from the patch, D-V5).</summary>
    public List<string> AppliedGroups { get; set; } = [];

    /// <summary>The validated patch changes <c>.github/workflows</c>: the writer's token asks for <c>workflows: write</c> (R15).</summary>
    public bool RequiresWorkflowsPermission { get; set; }

    /// <summary>Approved groups whose migrate workflow still has to run (R9), in plan order.</summary>
    public List<string> MigrationsPending { get; set; } = [];

    /// <summary>Groups whose migration completed (R9 marker + conclusion), in order.</summary>
    public List<string> MigratedGroups { get; set; } = [];

    /// <summary>Groups dropped from this run's plan (a partial migration, R9): the run re-applies without them.</summary>
    public List<string> DroppedGroups { get; set; } = [];

    /// <summary>How often the run started over because the base moved under it (R3) or a run built another commit.</summary>
    public int Restarts { get; set; }

    /// <summary>Policy evaluations in this run: only the first of a scheduled run advances the slot clock (R2), whatever
    /// re-plans follow (a dropped migration, a bisect step).</summary>
    public int Evaluations { get; set; }

    public int TransientFailures { get; set; }

    public string? LastError { get; set; }

    public int Polls { get; set; }

    // ── The rolling branch and its pull request (R4, R16) ─────────────────────────────────────────

    /// <summary>The rolling branch's head as the bot last pushed or found it.</summary>
    public string? RollingSha { get; set; }

    /// <summary>The bot's own commit under any human commits on the rolling branch: what the replay starts from (R4).</summary>
    public string? RollingBotSha { get; set; }

    public int? PullRequestNumber { get; set; }

    /// <summary>R16: the first rolling pull request after onboarding is opened as a draft and never auto-merges.</summary>
    public bool FirstPullRequestOpened { get; set; }

    public bool PullRequestIsDraft { get; set; }

    /// <summary>The bot's own check run (<c>Dependency updates</c>) on <see cref="CheckSha"/>.</summary>
    public long? CheckRunId { get; set; }

    public string? CheckSha { get; set; }

    // ── Red CI (old §7.4, N6) ─────────────────────────────────────────────────────────────────────

    public DependencyBisect? Bisect { get; set; }

    /// <summary>The coverage gate's answer on the last green head (R11), for the body.</summary>
    public string? CoverageNote { get; set; }

    // ── Onboarding (R16) ──────────────────────────────────────────────────────────────────────────

    public EDependencyOnboarding Onboarding { get; set; }

    public int? OnboardingPullRequestNumber { get; set; }

    public string? OnboardingNote { get; set; }

    public List<DependencyRunEvent> History { get; set; } = [];

    public void Note(DateTime atUtc, string text)
    {
        History.Add(new DependencyRunEvent { AtUtc = atUtc, State = State, Run = Run, Text = text });
        if (History.Count > MaxHistory) History.RemoveAt(0);
    }

    /// <summary>The run waiting for a workflow run now (the last one dispatched and not finished), or null.</summary>
    public DependencyWorkflowRun? Pending() => Runs.LastOrDefault(r => !r.Finished);

    public static string ForRepository(string repositoryId)
    {
        const string prefix = "Repositories/";
        if (!repositoryId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{repositoryId}' is not a repository document id.", nameof(repositoryId));
        return Prefix + repositoryId[prefix.Length..];
    }
}

/// <summary>The bulky half of a run, loaded only where needed (<c>DependencyUpdateRunData/{provider}/{id}</c>).</summary>
public class DependencyUpdateRunData
{
    public const string Prefix = "DependencyUpdateRunData/";

    public string? Id { get; set; }

    public int Run { get; set; }

    /// <summary>lookup.json as the runner wrote it (schema-validated before it was stored).</summary>
    public string? LookupJson { get; set; }

    /// <summary>The plan the bot committed (plan.schema.json), as written.</summary>
    public string? PlanJson { get; set; }

    /// <summary>The policy's report for the pull request body: planned groups, held, awaiting approval, available, ignored.</summary>
    public string? ReportJson { get; set; }

    public string? ApplyResultJson { get; set; }

    /// <summary>The apply run's <c>dependency.patch</c>, base64.</summary>
    public string? Patch { get; set; }

    /// <summary>Each completed migration's patch (base64) and its R19 notes, in order.</summary>
    public List<DependencyMigrationRecord> Migrations { get; set; } = [];

    /// <summary>The 40-hex commit the bot resolved per planned Actions tag (gap C), <c>owner/repo@tag</c> → sha.</summary>
    public Dictionary<string, string> ResolvedActions { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The target <c>action.yml</c> per new <c>uses:</c> value (R15's interface check), read statically.</summary>
    public Dictionary<string, string> TargetActions { get; set; } = new(StringComparer.Ordinal);

    public static string ForRepository(string repositoryId)
        => Prefix + DependencyUpdateRun.ForRepository(repositoryId)[DependencyUpdateRun.Prefix.Length..];
}

public class DependencyMigrationRecord
{
    public string Group { get; set; } = string.Empty;
    public string Workflow { get; set; } = string.Empty;
    public string Patch { get; set; } = string.Empty;
    public List<string> Notes { get; set; } = [];
    public List<string> ExtraBumps { get; set; } = [];
}

public class DependencySecurityFixRecord
{
    public string Ecosystem { get; set; } = string.Empty;
    public string DepName { get; set; } = string.Empty;
    public string? Advisory { get; set; }
}

/// <summary>One dispatched workflow run of a dependency run.</summary>
public class DependencyWorkflowRun : WorkflowHookRun
{
    /// <summary><c>lookup</c>, <c>apply</c>, <c>migrate</c>.</summary>
    public string Purpose { get; set; } = string.Empty;

    /// <summary>The commit the run must build (D14): the default branch's for the lookup, the plan or staged commit otherwise.</summary>
    public string ExpectedSha { get; set; } = string.Empty;

    /// <summary>The run's artifact name (<c>dependency-lookup-&lt;run_id&gt;</c>, …).</summary>
    public string Artifact { get; set; } = string.Empty;

    /// <summary>For a migrate run: the group it migrates.</summary>
    public string? Group { get; set; }
}

/// <summary>Red-CI bisection state (old §7.4): which groups are out, how much budget is used, what was learnt.</summary>
public class DependencyBisect
{
    /// <summary>The groups in suspicion order (§7.4: majors, then versions red elsewhere, then the rest).</summary>
    public List<string> Suspects { get; set; } = [];

    /// <summary>The groups left out of the commit under test now.</summary>
    public List<string> Excluded { get; set; } = [];

    /// <summary>CI runs spent (the one re-run included).</summary>
    public int Spent { get; set; }

    public int Budget { get; set; }

    public bool Rerun { get; set; }

    /// <summary>The head under test.</summary>
    public string? Sha { get; set; }

    public string? FailingRunUrl { get; set; }

    public List<string> FailingChecks { get; set; } = [];

    public string? Culprit { get; set; }
}

public class DependencyRunEvent
{
    public DateTime AtUtc { get; set; }
    public EDependencyRunState State { get; set; }
    public int Run { get; set; }
    public string Text { get; set; } = string.Empty;
}

public enum EDependencyRunState
{
    /// <summary>No run is going.</summary>
    Idle,

    /// <summary>Reading <c>coverage.yml</c> and the default branch, then dispatching the lookup.</summary>
    Starting,

    /// <summary>Waiting for the lookup run.</summary>
    Looking,

    /// <summary>Policy, plan commit, apply dispatch.</summary>
    Planning,

    /// <summary>Waiting for the apply run.</summary>
    Applying,

    /// <summary>Waiting for a migrate run (R9).</summary>
    Migrating,

    /// <summary>Rebuilding the rolling branch and upserting the pull request.</summary>
    Publishing,

    /// <summary>Waiting for the rolling head's CI (N6, N7).</summary>
    AwaitingChecks,

    /// <summary>Red CI: testing a commit without the suspected groups (old §7.4).</summary>
    Bisecting,

    /// <summary>Opening the onboarding pull request (R16).</summary>
    Onboarding,
}

/// <summary>What started a run. Ordered by strength: a pending trigger is replaced only by a stronger one.</summary>
public enum EDependencyRunTrigger
{
    /// <summary>The scheduled slot (Q8).</summary>
    Scheduled,

    /// <summary>A writer's <c>refresh</c> comment, or a command that changes the plan (approve, hold, unhold).</summary>
    Refresh,

    /// <summary>A producer on the account released (§7.6), debounced 30 minutes.</summary>
    OwnRelease,

    /// <summary>R10: a vulnerable dependency; skips schedule and cooldown.</summary>
    Security,
}

public enum EDependencyOnboarding
{
    /// <summary>Not looked at yet.</summary>
    None,

    /// <summary>The onboarding pull request is open.</summary>
    PullRequestOpened,

    /// <summary>The templates are on the default branch (merged onboarding, or there before).</summary>
    Done,

    /// <summary>The onboarding could not be opened (a permission not accepted, a writer refusal); see the note.</summary>
    Blocked,
}

public static class DependencyRunStateExtensions
{
    /// <summary>A run is going: a trigger now waits for it.</summary>
    public static bool IsBusy(this EDependencyRunState state)
        => state is not (EDependencyRunState.Idle or EDependencyRunState.AwaitingChecks);
}
