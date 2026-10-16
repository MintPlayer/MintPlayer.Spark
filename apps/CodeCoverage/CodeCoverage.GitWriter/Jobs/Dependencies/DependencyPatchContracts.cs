namespace CodeCoverage.GitWriter.Jobs.Dependencies;

/// <summary>
/// One planned update, as the bot planned it from a lookup record (docs/code-coverage/dependency-updates-PRD.md §13.2,
/// R1, R17). A minimal stand-in for the plan entry of <c>apps/CodeCoverage/contracts/dependency-update/plan.schema.json</c>:
/// the validators read only these fields, so swapping in the contract's DTO is a mapping, not a redesign.
/// </summary>
/// <param name="DepName">Renovate's <c>depName</c> (<c>express</c>, <c>actions/checkout</c>, <c>com.google.guava:guava</c>).</param>
/// <param name="PackageFile">The repository-relative file the record came from.</param>
/// <param name="CurrentValue">The value as written today (<c>^4.18.2</c>, <c>v4.1.1</c>).</param>
/// <param name="NewValue">The value to write. Equal to <paramref name="CurrentValue"/> for a lockfile-only update.</param>
/// <param name="GroupName">The plan group; a record without one is its own group (D-C1).</param>
/// <param name="Datasource">Renovate's <c>datasource</c>; adapters and file patterns key on it, never on the manager (D-S12d).</param>
public sealed record DependencyPlanEntry(string DepName, string PackageFile, string CurrentValue, string NewValue, string GroupName, string Datasource)
{
    /// <summary>Renovate's <c>packageName</c> when it differs from <see cref="DepName"/>.</summary>
    public string? PackageName { get; init; }

    /// <summary>The pinned digest as written today (an Actions SHA, a Docker <c>sha256:</c>).</summary>
    public string? CurrentDigest { get; init; }

    /// <summary>The digest to write. For Actions it is the 40-hex SHA the BOT resolved for <see cref="NewValue"/>
    /// (§13.11.3 #10): a runner that wrote any other SHA is refused.</summary>
    public string? NewDigest { get; init; }

    /// <summary>Renovate's <c>sharedVariableName</c> (a Maven property, a Gradle catalog version), which names the line
    /// that holds the value.</summary>
    public string? SharedVariableName { get; init; }

    /// <summary>Renovate's manager, informational only.</summary>
    public string? Manager { get; init; }

    /// <summary>A lockfile-only update: the manifest keeps its value.</summary>
    public bool IsLockfileOnly => CurrentValue == NewValue && CurrentDigest == NewDigest;
}

/// <summary>Gap B's limits (§13.11.4): the registry hosts a lockfile may point at besides the ones its base already
/// names, and how many packages it may newly introduce before a human must look.</summary>
public sealed record LockfileScanOptions(IReadOnlyCollection<string> AllowedHosts, int MaxNewPackages)
{
    /// <summary>The public registries of the ecosystems the apply adapters cover (R12).</summary>
    public static IReadOnlyList<string> PublicRegistries { get; } =
    [
        "registry.npmjs.org", "registry.yarnpkg.com", "api.nuget.org", "pypi.org", "files.pythonhosted.org", "index.crates.io",
        "static.crates.io", "crates.io", "repo.packagist.org", "repo1.maven.org", "repo.maven.apache.org", "plugins.gradle.org",
        "proxy.golang.org", "sum.golang.org", "rubygems.org",
    ];

    public static LockfileScanOptions Default { get; } = new(PublicRegistries, 25);
}

/// <summary>What the <c>dependencies</c> profile is checked against.</summary>
/// <param name="Plan">The planned entries of this apply run.</param>
public sealed record DependencyPatchRequest(IReadOnlyList<DependencyPlanEntry> Plan)
{
    /// <summary>The groups <c>result.json</c> says it applied. When given, the groups derived from the patch must equal
    /// them (§13.11.3 #4).</summary>
    public IReadOnlyCollection<string>? ClaimedAppliedGroups { get; init; }

    public LockfileScanOptions Lockfiles { get; init; } = LockfileScanOptions.Default;

    /// <summary>The patch cap; the merge queue's artifact read is capped at 8 MB too.</summary>
    public long MaxBytes { get; init; } = 8L * 1024 * 1024;

    /// <summary>The target <c>action.yml</c> per new <c>uses:</c> value, when the caller fetched them (a later milestone).</summary>
    public IReadOnlyDictionary<string, string>? TargetActions { get; init; }
}

public enum DependencyPatchOutcome
{
    Accepted,
    Rejected,

    /// <summary>Everything else passed, but a lockfile brings in more new packages than
    /// <see cref="LockfileScanOptions.MaxNewPackages"/>: not refused, held for a human (gap B).</summary>
    NeedsHuman,
}

/// <summary>The verdict on an apply patch.</summary>
/// <param name="AppliedGroups">The plan groups the patch applies, derived from the patch itself.</param>
/// <param name="RequiresWorkflowsPermission">The patch changes <c>.github/workflows/</c>, so the writer's token needs
/// <c>workflows: write</c>; it is requested only then (R15).</param>
/// <param name="NewPackages">Package names the lockfiles add that their base did not hold.</param>
public sealed record DependencyPatchVerdict(DependencyPatchOutcome Outcome, string? Reason, string? Detail, IReadOnlyList<string> Paths,
    IReadOnlyList<string> AppliedGroups, bool RequiresWorkflowsPermission, IReadOnlyList<string> NewPackages)
{
    public bool Accepted => Outcome == DependencyPatchOutcome.Accepted;

    public static DependencyPatchVerdict Reject(string reason, string? detail, params string[] paths)
        => new(DependencyPatchOutcome.Rejected, reason, detail, paths, [], false, []);

    public static DependencyPatchVerdict From(PatchVerdict verdict) => Reject(verdict.Reason!, verdict.Detail, [.. verdict.Paths]);
}

/// <summary>Why a dependency or migrate patch is refused, beside <see cref="PatchReasons"/>.</summary>
public static class DependencyPatchReasons
{
    /// <summary>The bot's own plan file (<c>.mintplayer/dependency-plan.json</c>, R14), or anything under <c>.mintplayer/</c>.</summary>
    public const string PlanPath = "plan-path";
    /// <summary>A path the profile's deny-list names; the deny-list wins over every allow rule.</summary>
    public const string DeniedPath = "denied-path";
    /// <summary>An added, deleted or renamed file; the <c>dependencies</c> profile only edits existing files (gap A).</summary>
    public const string AddedOrDeleted = "added-or-deleted";
    /// <summary>A path that is not a planned package file, nor a lockfile of one, nor a workflow the plan updates.</summary>
    public const string OutsidePlannedFiles = "outside-planned-files";
    /// <summary>A change block in a manifest that is not line-for-line (an added or removed line).</summary>
    public const string NotASubstitution = "not-a-substitution";
    /// <summary>A changed manifest line that no planned <c>current → new</c> token swap, on a line naming the dependency,
    /// explains.</summary>
    public const string UnplannedChange = "unplanned-change";
    /// <summary>Only some entries of a plan group were applied (groups move together, D-C1).</summary>
    public const string PartialGroup = "partial-group";
    /// <summary>The groups derived from the patch differ from the ones <c>result.json</c> claims.</summary>
    public const string GroupMismatch = "group-mismatch";
    /// <summary>A lockfile line points at a host outside the allow-list, or uses a git/file/link/http source (gap B).</summary>
    public const string LockfileSource = "lockfile-source";
    /// <summary>A changed lockfile entry without its integrity value (gap B).</summary>
    public const string LockfileIntegrity = "lockfile-integrity";
    /// <summary>A lockfile change no applied entry owns.</summary>
    public const string UnownedLockfile = "unowned-lockfile";
    /// <summary>A workflow or <c>action.yml</c> edit outside the R15 carve-out (gap C).</summary>
    public const string WorkflowEdit = "workflow-edit";
    /// <summary>A <c>uses:</c> ref other than the one the bot resolved for the planned tag (§13.11.3 #10).</summary>
    public const string WorkflowRef = "workflow-ref";
}
