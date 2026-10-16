using CodeCoverage.Entities;
using CodeCoverage.Git.Dependencies;

namespace CodeCoverage.Dependencies.Policy;

public enum EDependencyRunMode
{
    /// <summary>The scheduled slot: everything the policy admits.</summary>
    Scheduled,

    /// <summary>R10's out-of-slot run for a security fix: only the fix's groups and the groups that may auto-merge.</summary>
    Security,
}

/// <summary>A vulnerable dependency the advisory sources name (R10): its records skip schedule and cooldown.</summary>
public sealed record DependencySecurityFix(string Ecosystem, string DepName, string? Advisory = null);

/// <summary>Everything one evaluation reads. Pure data: the engine never touches the database or the clock.</summary>
public sealed record DependencyPolicyInput
{
    public required LookupDocument Lookup { get; init; }

    public required DependencyPolicyConfig Config { get; init; }

    /// <summary>The repository's stored state; never mutated (the outcome carries the next state).</summary>
    public DependencyUpdateState State { get; init; } = new();

    /// <summary>The account's fleet state: red versions (Q15) and confirmed own releases (§7.6, gap E).</summary>
    public DependencyFleetState Fleet { get; init; } = new();

    public required DateTimeOffset Now { get; init; }

    /// <summary>The blob id of the default branch's <c>coverage.yml</c>; approvals are bound to it (gap E).</summary>
    public string ConfigBlob { get; init; } = string.Empty;

    /// <summary>A scheduled slot advances the four-slot clock of the R2 holds; an out-of-slot run does not.</summary>
    public bool IsScheduledSlot { get; init; } = true;

    public EDependencyRunMode Mode { get; init; } = EDependencyRunMode.Scheduled;

    public IReadOnlyCollection<DependencySecurityFix> SecurityFixes { get; init; } = [];
}

/// <summary>Why an available update is not in the plan, or why a dependency is not considered at all.</summary>
public enum EDependencyReportReason
{
    /// <summary>Younger than the cooldown (Q16); <see cref="DependencyReportEntry.Until"/> says when it is old enough.</summary>
    Cooldown,

    /// <summary>Over the threshold, waiting for a writer's <c>approve &lt;group&gt;</c> (R6).</summary>
    AwaitingApproval,

    /// <summary>Above an enforced account rule's ceiling: no approval admits it (Q9).</summary>
    AboveAccountRule,

    /// <summary>R1: Renovate did not report a field the plan needs (updateType, version, value).</summary>
    MissingField,

    /// <summary>A label the bot never plans on its own (rollback, replacement), or a record it cannot plan safely
    /// (no current value, a path outside the repository).</summary>
    NeedsHuman,

    /// <summary>A hold: R2 blockers, a human's value (Q11), a comment command, or the file's <c>hold</c>.</summary>
    Held,

    /// <summary>Q15: the version went red in another repository of the fleet.</summary>
    FleetRed,

    /// <summary>A preview platform channel while the repository is on a stable one (R7).</summary>
    PreviewNotFollowed,

    /// <summary>Ignored by a rule.</summary>
    Ignored,

    /// <summary>The package file is under <c>dependencies.exclude</c>.</summary>
    Excluded,

    /// <summary>Renovate skipped the dependency (its <c>skipReason</c>).</summary>
    Skipped,

    /// <summary>R10 security run: this group is left for the next slot.</summary>
    NotInSecurityRun,
}

/// <summary>One line of the pull request body's available / held / awaiting-approval / ignored tables.</summary>
public sealed record DependencyReportEntry(
    string Group,
    string Ecosystem,
    string DepName,
    string PackageFile,
    string? Current,
    string? Target,
    EUpdateLevel? Level,
    EDependencyReportReason Reason,
    string Detail)
{
    /// <summary>When a cooldown ends.</summary>
    public DateTimeOffset? Until { get; init; }
}

/// <summary>One planned update, with the record and candidate it came from.</summary>
public sealed record DependencyPlannedItem(LookupRecord Record, LookupCandidate Candidate, EUpdateLevel Level)
{
    /// <summary>The contract item (plan.schema.json). Every token comes from the record: <c>newValue</c> is the candidate's
    /// value (the current one for a digest or lockfile-only update), and a digest is planned only where one is pinned today
    /// (npm's <c>newDigest</c> is an integrity hash, not a pin).</summary>
    public PlanItem ToPlanItem()
    {
        var label = UpdateTypeFloor.Labeled(Candidate.UpdateType);
        return new PlanItem
        {
            DepName = Record.DepName,
            PackageName = Record.PackageName,
            Ecosystem = Record.Ecosystem,
            Datasource = Record.Datasource,
            Manager = Record.Manager,
            DepType = Record.DepType,
            PackageFile = Record.PackageFile,
            LockFiles = [.. Record.LockFiles],
            CurrentValue = Record.CurrentValue!,
            NewValue = Candidate.Value ?? Record.CurrentValue!,
            NewVersion = Candidate.Version!,
            CurrentDigest = Record.CurrentDigest,
            NewDigest = Record.CurrentDigest is null ? null : Candidate.Digest,
            ReplaceString = Record.ReplaceString,
            SharedVariableName = Record.SharedVariableName,
            // The floor's level when it raised the label; Renovate's own label otherwise (digest, pin, lockfileUpdate).
            UpdateType = label == Level && Candidate.UpdateType is { } original
                ? System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(original.ToString())
                : Level.ToString().ToLowerInvariant(),
        };
    }
}

/// <summary>A plan group as the policy decided it (D-C1, D-C2).</summary>
public sealed record DependencyPlannedGroup
{
    public required string Group { get; init; }

    /// <summary>The largest update in the group, after the R17 floor.</summary>
    public required EUpdateLevel Level { get; init; }

    /// <summary>Within the threshold, so the merge queue may take it (R5, R6). Never for a platform major (R7), an approved
    /// group, a migration (R9) or a manager on R17's guard-2 list.</summary>
    public required bool AutoMerge { get; init; }

    /// <summary>Over the threshold and admitted by an approval (R6).</summary>
    public bool Approved { get; init; }

    /// <summary>Holds a security fix (R10).</summary>
    public bool Security { get; init; }

    /// <summary>R9: the <c>migrate</c> workflow dispatched after apply for this approved major.</summary>
    public string? MigrateWorkflow { get; init; }

    public IReadOnlyList<DependencyPlannedItem> Items { get; init; } = [];

    /// <summary>A platform group's instruction to the SDK adapter (R7, R8).</summary>
    public PlanPlatform? Platform { get; init; }

    /// <summary>The group's dependencies, ordinal-sorted (<c>platform:&lt;sdk&gt;</c> for a platform group): half of the
    /// approval binding (gap E).</summary>
    public required IReadOnlyList<string> DepNames { get; init; }

    /// <summary><c>depName@major</c> per dependency, ordinal-sorted: the other half of the approval binding.</summary>
    public required IReadOnlyList<string> TargetMajors { get; init; }

    public PlanGroup ToPlanGroup() => new()
    {
        Group = Group,
        Items = [.. Items.Select(i => i.ToPlanItem())],
        Platform = Platform,
    };
}

/// <summary>A platform location the policy reports but never plans (R8: computed values).</summary>
public sealed record DependencyPlatformReport(string Sdk, string File, string Value, string Reason);

/// <summary>What one evaluation decided.</summary>
public sealed record DependencyPolicyOutcome
{
    /// <summary>The plan groups, in apply order: security fixes, then by level (patch first, so the safest groups land
    /// before a riskier one can be reset), then by name; platform groups last, because they move the TFM under everything.</summary>
    public IReadOnlyList<DependencyPlannedGroup> Groups { get; init; } = [];

    /// <summary>Updates that exist but wait for the cooldown, or are left for the next slot.</summary>
    public IReadOnlyList<DependencyReportEntry> Available { get; init; } = [];

    public IReadOnlyList<DependencyReportEntry> Held { get; init; } = [];

    /// <summary>R6: over the threshold; each needs a writer's <c>approve &lt;group&gt;</c>.</summary>
    public IReadOnlyList<DependencyReportEntry> AwaitingApproval { get; init; } = [];

    public IReadOnlyList<DependencyReportEntry> Ignored { get; init; } = [];

    public IReadOnlyList<DependencyPlatformReport> PlatformReports { get; init; } = [];

    /// <summary>The state to store: first-seen times added, lifted holds removed, slot counters advanced, stale approvals
    /// dropped.</summary>
    public required DependencyUpdateState NextState { get; init; }

    /// <summary>Holds this evaluation lifted, for the pull request body.</summary>
    public IReadOnlyList<DependencyHold> LiftedHolds { get; init; } = [];

    /// <summary>Approvals that no longer match their group (a newer major, another membership, another <c>coverage.yml</c>).</summary>
    public IReadOnlyList<DependencyApproval> ExpiredApprovals { get; init; } = [];

    /// <summary>Every group may auto-merge (R5/R6); the coverage gate (R11) and the queue (R5) are checked by the caller.</summary>
    public bool AutoMergeable => Groups.Count > 0 && Groups.All(g => g.AutoMerge);

    /// <summary>The contract plan (plan.schema.json) for the apply dispatch.</summary>
    public DependencyPlan ToPlan(string planId, string expectedSha, PlanOptions? options = null) => new()
    {
        PlanId = planId,
        ExpectedSha = expectedSha,
        Options = options,
        Groups = [.. Groups.Select(g => g.ToPlanGroup())],
    };
}
