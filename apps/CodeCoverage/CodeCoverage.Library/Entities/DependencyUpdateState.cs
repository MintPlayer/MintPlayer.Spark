using CodeCoverage.LookupReferences;

namespace CodeCoverage.Entities;

/// <summary>The effective dependency-update schedule of one repository (PRD §4.1, Q8); see <see cref="Repository.ResolveDependencySchedule"/>.</summary>
/// <param name="TimeZone">An IANA id; the caller reads an unknown one as UTC.</param>
public sealed record DependencySchedule(EDependencyScheduleFrequency Frequency, EDependencyWeekday Weekday, int Hour, string TimeZone);

/// <summary>
/// What the dependency policy engine remembers about one repository between slots (docs/code-coverage/dependency-updates-PRD.md
/// §13.10 R2, R6, Q11, D-S12a): first-seen times, holds and approvals.
/// </summary>
/// <remarks>
/// <para>
/// One document per repository, <c>DependencyUpdateStates/{provider}/{id}</c> beside <c>Repositories/{provider}/{id}</c>,
/// always loaded by id: the engine never queries for state, so there is no RQL to splice anything into.
/// </para>
/// <para>
/// Not a Spark entity: nothing here is shown or edited through the generic UI. The PR body renders it (N5), and the
/// comment commands (N6) write holds and approvals through <c>DependencyStateStore</c>.
/// </para>
/// </remarks>
public class DependencyUpdateState
{
    public const string Prefix = "DependencyUpdateStates/";

    /// <summary>A repository's history is bounded: the oldest first-seen entries go first.</summary>
    public const int MaxFirstSeen = 5000;

    public string? Id { get; set; }

    public string Repository { get; set; } = string.Empty;

    /// <summary>When the bot first saw each candidate version (D-S12a): the cooldown clock of a record without a
    /// <c>releaseTimestamp</c>. Never earlier than the real release, so it is conservative.</summary>
    public List<DependencySeenVersion> FirstSeen { get; set; } = [];

    public List<DependencyHold> Holds { get; set; } = [];

    public List<DependencyApproval> Approvals { get; set; } = [];

    /// <summary>The number of scheduled slots the engine has evaluated; a blocker hold always retries after four (R2).</summary>
    public int Slots { get; set; }

    public DateTime? LastEvaluatedAtUtc { get; set; }

    public static string ForRepository(string repositoryId)
    {
        const string prefix = "Repositories/";
        if (!repositoryId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{repositoryId}' is not a repository document id.", nameof(repositoryId));
        return Prefix + repositoryId[prefix.Length..];
    }
}

/// <summary>A candidate version and when the bot first saw it.</summary>
public class DependencySeenVersion
{
    public string Ecosystem { get; set; } = string.Empty;
    public string DepName { get; set; } = string.Empty;

    /// <summary>The candidate's version token, or its value when Renovate gave no version. A token, never parsed.</summary>
    public string Version { get; set; } = string.Empty;

    public DateTime FirstSeenAtUtc { get; set; }
}

public enum EDependencyHoldKind
{
    /// <summary>R2: the resolver refused the group and named blockers; lifts when a blocker shows a version not seen when
    /// the hold was taken, and always after four slots.</summary>
    Blocker,

    /// <summary>R2's fallback: the group failed or no blocker was parsed; lifts when anything in the lookup changes, and
    /// always after four slots.</summary>
    Unattributed,

    /// <summary>Q11: a human set a manifest value on the rolling branch; lifts when a version newer than the one the bot had
    /// proposed appears (any version token not seen when the hold was taken).</summary>
    Human,

    /// <summary>A <c>hold</c> comment command (§7.5); lifts only on <c>unhold</c>.</summary>
    Command,
}

/// <summary>A hold on a plan group, or on single dependencies.</summary>
public class DependencyHold
{
    public string Id { get; set; } = string.Empty;

    public EDependencyHoldKind Kind { get; set; }

    /// <summary>The plan group it holds; empty for a hold on <see cref="DepNames"/> alone.</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>The dependencies it holds (the group's members when it was taken).</summary>
    public List<string> DepNames { get; set; } = [];

    /// <summary>Shown in the pull request body.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary><see cref="EDependencyHoldKind.Blocker"/>: each blocker and the version tokens the lookup offered for it
    /// when the hold was taken.</summary>
    public List<DependencyBlockerSeen> Blockers { get; set; } = [];

    /// <summary><see cref="EDependencyHoldKind.Unattributed"/>: the lookup fingerprint when the hold was taken.</summary>
    public string? Fingerprint { get; set; }

    /// <summary><see cref="EDependencyHoldKind.Human"/>: every version token the lookup offered for the held dependencies
    /// when the hold was taken, the bot's own proposal included.</summary>
    public List<string> SeenVersions { get; set; } = [];

    /// <summary><see cref="EDependencyHoldKind.Human"/>: the manifest value the human set.</summary>
    public string? HeldValue { get; set; }

    /// <summary><see cref="EDependencyHoldKind.Human"/>: the version the bot had proposed.</summary>
    public string? ProposedVersion { get; set; }

    /// <summary>Scheduled slots evaluated while the hold stood.</summary>
    public int SlotsHeld { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Who set it (a login), or null for the bot's own holds.</summary>
    public string? By { get; set; }

    /// <summary>The commit that set a human hold.</summary>
    public string? Commit { get; set; }

    /// <summary>The plan whose apply produced a blocker hold.</summary>
    public string? PlanId { get; set; }
}

public class DependencyBlockerSeen
{
    /// <summary>The blocking package, as the resolver named it.</summary>
    public string DepName { get; set; } = string.Empty;

    /// <summary>The version tokens the lookup offered for it (and its current value) when the hold was taken.</summary>
    public List<string> SeenVersions { get; set; } = [];
}

/// <summary>
/// A writer's <c>approve &lt;group&gt;</c> (R6), bound per gap E to the group, its exact dependency set, each dependency's
/// target major (R17 leading segment) and the base <c>coverage.yml</c> blob. A newer major, a changed membership or a
/// changed file make it stale; the approver's role is checked again at apply time (N6).
/// </summary>
public class DependencyApproval
{
    public string Group { get; set; } = string.Empty;

    /// <summary>Ordinal-sorted.</summary>
    public List<string> DepNames { get; set; } = [];

    /// <summary><c>depName@major</c> per dependency, ordinal-sorted.</summary>
    public List<string> TargetMajors { get; set; } = [];

    /// <summary>The blob id of the default branch's <c>coverage.yml</c> the approval was given under; empty when absent.</summary>
    public string ConfigBlob { get; set; } = string.Empty;

    public string ApprovedBy { get; set; } = string.Empty;

    public DateTime ApprovedAtUtc { get; set; }

    public long? CommentId { get; set; }
}

/// <summary>
/// The fleet half of the dependency policy (Q15, §7.6), per account: versions that went red somewhere, and releases the
/// bot confirmed as the account's own.
/// </summary>
/// <remarks><c>DependencyFleetStates/{provider}/{id}</c> beside <c>Accounts/{provider}/{id}</c>, loaded by id.</remarks>
public class DependencyFleetState
{
    public const string Prefix = "DependencyFleetStates/";
    public const int MaxEntries = 2000;

    public string? Id { get; set; }

    public string Account { get; set; } = string.Empty;

    /// <summary>Q15: a version that went red at stage 4 in any repository is held for every repository that has not merged it.</summary>
    public List<DependencyFleetRed> RedVersions { get; set; } = [];

    /// <summary>§7.6, gap E: releases confirmed as own (producer on this account declares the package, and the publish
    /// coincided with a release or push event from it). Exempt from the cooldown.</summary>
    public List<DependencyOwnRelease> OwnReleases { get; set; } = [];

    public static string ForAccount(string accountId)
    {
        const string prefix = "Accounts/";
        if (!accountId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{accountId}' is not an account document id.", nameof(accountId));
        return Prefix + accountId[prefix.Length..];
    }
}

public class DependencyFleetRed
{
    public string Ecosystem { get; set; } = string.Empty;
    public string DepName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;

    /// <summary>The repository it went red in.</summary>
    public string Repository { get; set; } = string.Empty;

    public string? RunUrl { get; set; }

    public DateTime AtUtc { get; set; }
}

public class DependencyOwnRelease
{
    public string Ecosystem { get; set; } = string.Empty;
    public string DepName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;

    /// <summary>The producer repository on this account.</summary>
    public string ProducerRepository { get; set; } = string.Empty;

    public DateTime AtUtc { get; set; }
}
