using System.Security.Cryptography;
using System.Text;
using CodeCoverage.Entities;
using CodeCoverage.Git.Dependencies;

namespace CodeCoverage.Dependencies.Policy;

/// <summary>
/// Holds and approvals on <see cref="DependencyUpdateState"/>: how they are taken and when they lift (PRD R2, R6, Q11,
/// §7.5, gap E). Pure functions over the state document; <see cref="DependencyStateStore"/> persists it.
/// </summary>
public static class DependencyHolds
{
    /// <summary>R2: a blocker hold always retries after this many scheduled slots.</summary>
    public const int RetrySlots = 4;

    /// <summary>
    /// R2: after an apply run, hold every group the run did not apply. An <c>unresolvable</c> group with attributed blockers
    /// is held until a blocker shows a version the lookup did not offer when the hold was taken; any other (a <c>failed</c>
    /// group, or no blocker named) until anything in the lookup changes. Both always retry after <see cref="RetrySlots"/>.
    /// </summary>
    /// <returns>The holds taken.</returns>
    public static IReadOnlyList<DependencyHold> RecordApplyResult(DependencyUpdateState state, DependencyPlan plan, ApplyResult result,
        LookupDocument lookup, DateTimeOffset now)
    {
        var taken = new List<DependencyHold>();
        var fingerprint = Fingerprint(lookup);
        foreach (var group in result.Groups.Where(g => g.Status != EApplyGroupStatus.Applied))
        {
            var planned = plan.Groups.FirstOrDefault(g => g.Group == group.Group);
            if (planned is null)
                continue; // result.json names a group the plan never had; the patch check refuses that (D-V5).

            var depNames = planned.Platform is { } platform
                ? [$"platform:{platform.Sdk}"]
                : planned.Items.Select(i => i.DepName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var attributed = group.Blockers.Where(b => !string.IsNullOrWhiteSpace(b.DepName)).Select(b => b.DepName!).Distinct(StringComparer.Ordinal).ToList();
            var blocker = group.Status == EApplyGroupStatus.Unresolvable && attributed.Count > 0;
            var hold = new DependencyHold
            {
                Id = $"{(blocker ? "blocker" : "apply")}:{group.Group}",
                Kind = blocker ? EDependencyHoldKind.Blocker : EDependencyHoldKind.Unattributed,
                Group = group.Group,
                DepNames = depNames,
                Reason = blocker
                    ? $"the resolver refused it: blocked by {string.Join(", ", attributed)} (R2)"
                    : $"the apply run {(group.Status == EApplyGroupStatus.Unresolvable ? "could not resolve" : "failed on")} it{(group.Message is { Length: > 0 } m ? $": {Shorten(m)}" : string.Empty)} (R2)",
                Blockers = blocker ? [.. attributed.Select(dep => new DependencyBlockerSeen { DepName = dep, SeenVersions = Seen(lookup, dep) })] : [],
                Fingerprint = blocker ? null : fingerprint,
                CreatedAtUtc = now.UtcDateTime,
                PlanId = plan.PlanId,
            };
            state.Holds.RemoveAll(h => h.Group == group.Group && h.Kind is EDependencyHoldKind.Blocker or EDependencyHoldKind.Unattributed);
            state.Holds.Add(hold);
            taken.Add(hold);
        }
        return taken;
    }

    /// <summary>
    /// Q11: a human set <paramref name="heldValue"/> for <paramref name="depName"/> on the rolling branch; the value wins and
    /// holds the dependency until the lookup offers a version token not seen now (one newer than the bot had proposed).
    /// </summary>
    public static DependencyHold AddHumanHold(DependencyUpdateState state, string depName, string heldValue, string? proposedVersion,
        string by, string? commit, LookupDocument lookup, DateTimeOffset now)
    {
        var seen = Seen(lookup, depName);
        if (proposedVersion is not null && !seen.Contains(proposedVersion))
            seen.Add(proposedVersion);
        var hold = new DependencyHold
        {
            Id = $"human:{depName}",
            Kind = EDependencyHoldKind.Human,
            DepNames = [depName],
            Reason = $"{depName} held at {heldValue} (set by @{by}{(commit is null ? string.Empty : $" in {commit[..Math.Min(commit.Length, 12)]}")})",
            SeenVersions = seen,
            HeldValue = heldValue,
            ProposedVersion = proposedVersion,
            By = by,
            Commit = commit,
            CreatedAtUtc = now.UtcDateTime,
        };
        state.Holds.RemoveAll(h => h.Id == hold.Id);
        state.Holds.Add(hold);
        return hold;
    }

    /// <summary>§7.5 <c>hold &lt;group|package&gt;</c>: a group name holds that group, anything else the dependency.
    /// It lifts only on <see cref="RemoveCommandHold"/>.</summary>
    public static DependencyHold AddCommandHold(DependencyUpdateState state, string target, bool isGroup, string by, DateTimeOffset now)
    {
        var hold = new DependencyHold
        {
            Id = $"command:{target}",
            Kind = EDependencyHoldKind.Command,
            Group = isGroup ? target : string.Empty,
            DepNames = isGroup ? [] : [target],
            Reason = $"held by @{by} (comment)",
            By = by,
            CreatedAtUtc = now.UtcDateTime,
        };
        state.Holds.RemoveAll(h => h.Id == hold.Id);
        state.Holds.Add(hold);
        return hold;
    }

    /// <summary>§7.5 <c>unhold</c>: removes a command hold and a human hold on the target; never a bot hold (R2's clock is
    /// the bot's, and an enforced rule is never a hold).</summary>
    public static bool RemoveCommandHold(DependencyUpdateState state, string target)
        => state.Holds.RemoveAll(h => h.Id == $"command:{target}" || h.Id == $"human:{target}") > 0;

    /// <summary>
    /// R6 <c>approve &lt;group&gt;</c>, bound per gap E to what the pull request body showed as awaiting approval: the group,
    /// its exact dependency set, each target major, and the base <c>coverage.yml</c> blob. Null when the group is not
    /// awaiting approval (nothing to approve, or above an enforced ceiling, which no comment lifts).
    /// </summary>
    /// <remarks>The caller checks the commenter's role (write or above, <c>created</c> comments only) before, and again at
    /// apply time (N6).</remarks>
    public static DependencyApproval? Approve(DependencyUpdateState state, DependencyPolicyOutcome outcome, string group, string configBlob,
        string by, long? commentId, DateTimeOffset now)
    {
        var entries = outcome.AwaitingApproval.Where(e => e.Group == group && e.Target is not null).ToList();
        if (entries.Count == 0)
            return null;
        var approval = new DependencyApproval
        {
            Group = group,
            DepNames = [.. entries.Select(e => e.DepName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            TargetMajors = [.. entries.Select(e => $"{e.DepName}@{UpdateTypeFloor.TargetMajor(e.Target!)}").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            ConfigBlob = configBlob,
            ApprovedBy = by,
            ApprovedAtUtc = now.UtcDateTime,
            CommentId = commentId,
        };
        state.Approvals.RemoveAll(a => a.Group == group);
        state.Approvals.Add(approval);
        return approval;
    }

    /// <summary>Whether <paramref name="hold"/> lifts against this lookup (R2, Q11).</summary>
    public static bool Lifts(DependencyHold hold, LookupDocument lookup, string fingerprint) => hold.Kind switch
    {
        EDependencyHoldKind.Blocker => hold.SlotsHeld >= RetrySlots
            || hold.Blockers.Any(b => Seen(lookup, b.DepName).Any(v => !b.SeenVersions.Contains(v, StringComparer.Ordinal))),
        EDependencyHoldKind.Unattributed => hold.SlotsHeld >= RetrySlots || hold.Fingerprint != fingerprint,
        EDependencyHoldKind.Human => hold.DepNames.SelectMany(d => Seen(lookup, d)).Any(v => !hold.SeenVersions.Contains(v, StringComparer.Ordinal)),
        _ => false,
    };

    /// <summary>Every version token the lookup shows for a dependency: its current value and version, and each candidate's.</summary>
    public static List<string> Seen(LookupDocument lookup, string depName)
        => [.. lookup.Records.Where(r => string.Equals(r.DepName, depName, StringComparison.Ordinal))
            .SelectMany(r => new[] { r.CurrentValue, r.CurrentVersion, r.LockedVersion }.Concat(r.Candidates.Select(c => (string?)DependencyPolicyEngine.Token(c))))
            .Concat(lookup.Platforms.Where(p => $"platform:{p.Sdk}" == depName).SelectMany(p => p.Current.Channels.Concat(p.Candidates.Select(c => c.Channel))).Select(c => (string?)c))
            .OfType<string>().Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>R2's "any change": a hash over every record's identity, current value and candidate tokens, and every
    /// platform's channels. Order-independent.</summary>
    public static string Fingerprint(LookupDocument lookup)
    {
        var lines = lookup.Records
            .Select(r => $"{r.Ecosystem}|{r.DepName}|{r.PackageFile}|{r.CurrentValue}|{string.Join(",", r.Candidates.Select(DependencyPolicyEngine.Token))}")
            .Concat(lookup.Platforms.Select(p => $"platform:{p.Sdk}|{string.Join(",", p.Current.Channels)}|{string.Join(",", p.Candidates.Select(c => c.Channel))}"))
            .Order(StringComparer.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }

    public static DependencyHold Copy(DependencyHold hold) => new()
    {
        Id = hold.Id,
        Kind = hold.Kind,
        Group = hold.Group,
        DepNames = [.. hold.DepNames],
        Reason = hold.Reason,
        Blockers = [.. hold.Blockers.Select(b => new DependencyBlockerSeen { DepName = b.DepName, SeenVersions = [.. b.SeenVersions] })],
        Fingerprint = hold.Fingerprint,
        SeenVersions = [.. hold.SeenVersions],
        HeldValue = hold.HeldValue,
        ProposedVersion = hold.ProposedVersion,
        SlotsHeld = hold.SlotsHeld,
        CreatedAtUtc = hold.CreatedAtUtc,
        By = hold.By,
        Commit = hold.Commit,
        PlanId = hold.PlanId,
    };

    private static string Shorten(string message) => message.Length <= 200 ? message : message[..200] + "…";
}

/// <summary>A blocking note on a migrated pull request (R19).</summary>
/// <param name="DepName">The extra bump's dependency.</param>
/// <param name="Version">The version the migration introduced.</param>
public sealed record DependencyMigrationNote(string DepName, string PackageFile, string? From, string? Version, string Reason);

/// <summary>
/// R19: <c>nx migrate --include=all</c> bumps dependencies beyond the group (S17: typescript 5.9.2 → 6.0.3). They ride
/// along, but one that hits a bot hold gets a blocking note "held version introduced by migration".
/// </summary>
public static class DependencyMigrationNotes
{
    public static IReadOnlyList<DependencyMigrationNote> Check(MigrateResult result, DependencyPolicyConfig config, DependencyUpdateState state,
        DependencyFleetState fleet, string ecosystem = "npm")
    {
        var notes = new List<DependencyMigrationNote>();
        foreach (var bump in result.ExtraBumps)
        {
            string? reason = null;
            var version = bump.To;
            if (version is not null && fleet.RedVersions.Any(r => r.Ecosystem == ecosystem && r.DepName == bump.DepName && r.Version == Bare(version)))
                reason = "red in another repository of the fleet (Q15)";
            else if (config.IgnoredBy(ecosystem, bump.DepName) is { } ignored)
                reason = ignored;
            else if (config.FileHold(ecosystem, bump.DepName) is { } fileHold)
                reason = fileHold;
            else if (state.Holds.FirstOrDefault(h => h.DepNames.Contains(bump.DepName, StringComparer.Ordinal)) is { } hold)
                reason = hold.Reason;
            else if (version is not null && bump.From is not null)
            {
                var level = UpdateTypeFloor.Apply(EUpdateLevel.Minor, bump.From, version);
                var ceiling = config.Ceiling(ecosystem, bump.DepName);
                if ((int)level > (int)ceiling)
                    reason = $"above the account rule's ceiling ({ceiling.ToString().ToLowerInvariant()})";
            }
            if (reason is not null)
                notes.Add(new DependencyMigrationNote(bump.DepName, bump.PackageFile, bump.From, bump.To, $"held version introduced by migration: {reason}"));
        }
        return notes;
    }

    /// <summary>The token without a range operator, for the equality check against fleet-red versions; not a parse.</summary>
    private static string Bare(string value) => value.TrimStart('^', '~', '=', 'v', ' ');
}
