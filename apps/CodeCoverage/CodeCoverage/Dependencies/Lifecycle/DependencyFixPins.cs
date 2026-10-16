using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Entities;
using CodeCoverage.Git.Dependencies;

namespace CodeCoverage.Dependencies.Lifecycle;

/// <summary>One dependency kept at the version a human fix on the rolling branch was made against (D-N1).</summary>
/// <param name="Version">The version the previous build planned and the fix was written for.</param>
/// <param name="Wanted">What the policy would plan now instead; null when the group is not planned at all this time.</param>
public sealed record DependencyPin(string Group, string DepName, string PackageFile, string Version, string? Wanted);

/// <summary>
/// D-N1, R4's "packages a human fix depends on stay pinned". A person who pushes a code fix onto the rolling branch (a call
/// site adapted to a new API, a test updated for a new behaviour) wrote it against the versions that build planned. When the
/// next slot rebuilds the branch from the default branch and a group's version moved on, replaying that fix onto the newer
/// version can break it in ways no merge conflict shows. So:
/// <list type="number">
/// <item><b>Detection, by the fix's base.</b> The human commits are everything above the bot's own commit
/// (<see cref="DependencyUpdateRun.RollingBotSha"/>). If they change any path that is not a package file or lockfile of the plan
/// that commit was built from (<see cref="DependencyUpdateRunData.RollingPlanJson"/>), they are a fix that depends on that
/// plan. An unreadable or truncated compare counts as a fix (conservative). Commits that touch only planned manifests are the
/// Q11 case, which the human holds already cover.</item>
/// <item><b>The pin.</b> Every group of that previous plan whose planned versions differ from what the policy plans now (or
/// that the policy does not plan this time) is planned again exactly as before: the same items, the same tokens, built from
/// the previous plan entries over the current lookup records. Nothing is parsed (R1): the tokens are the previous plan's.</item>
/// <item><b>What wins over a pin.</b> A person's own hold on the group (a <c>hold</c> comment, a human value): the person said
/// so. A record whose current value moved on the default branch (someone merged that dependency, or another version, there):
/// the base no longer has what the pin would edit. A platform group (it edits by channel, not by token).</item>
/// <item><b>Never auto-merged.</b> A pinned group is not auto-merged: a person already changed code on this pull request,
/// so a person merges it. The pin lasts as long as the fix stays on the branch; merging or closing the pull request ends it,
/// and so does a rebuild after the person removed the commits.</item>
/// </list>
/// </summary>
public static class DependencyFixPins
{
    /// <summary>Whether <paramref name="changedPaths"/> (the human commits' change against the bot's commit) holds a fix that
    /// depends on <paramref name="previous"/>: anything beyond its package files and lockfiles. Null (unknown) is a fix.</summary>
    public static bool HumanFixPresent(DependencyPlan previous, IReadOnlyCollection<string>? changedPaths)
    {
        if (changedPaths is null)
            return true;
        var planned = previous.Groups.SelectMany(g => g.Items).SelectMany(i => i.LockFiles.Prepend(i.PackageFile)).ToHashSet(StringComparer.Ordinal);
        return changedPaths.Any(p => !planned.Contains(p) && !p.StartsWith(".mintplayer/", StringComparison.Ordinal));
    }

    /// <summary>The outcome with every group of <paramref name="previous"/> pinned where the policy moved it, and the pins.</summary>
    public static (DependencyPolicyOutcome Outcome, IReadOnlyList<DependencyPin> Pins) Apply(DependencyPolicyOutcome outcome, DependencyPlan previous,
        LookupDocument lookup, DependencyUpdateState state)
    {
        var groups = outcome.Groups.ToList();
        var pins = new List<DependencyPin>();
        foreach (var before in previous.Groups)
        {
            if (before.Platform is not null || before.Items.Count == 0)
                continue;
            var names = before.Items.Select(i => i.DepName).ToHashSet(StringComparer.Ordinal);
            if (state.Holds.Any(h => h.Kind is EDependencyHoldKind.Command or EDependencyHoldKind.Human
                    && (h.Group == before.Group || h.DepNames.Any(names.Contains))))
                continue;

            var now = groups.FirstOrDefault(g => g.Group == before.Group);
            if (now is not null && SameTargets(now, before))
                continue;

            var items = new List<DependencyPlannedItem>();
            foreach (var item in before.Items)
            {
                var record = lookup.Records.FirstOrDefault(r => r.DepName == item.DepName && r.PackageFile == item.PackageFile && r.CurrentValue == item.CurrentValue);
                if (record is null)
                {
                    items.Clear();
                    break;
                }
                items.Add(new DependencyPlannedItem(record, new LookupCandidate
                {
                    Version = item.NewVersion,
                    Value = item.NewValue,
                    Digest = item.NewDigest,
                    UpdateType = Enum.TryParse<EUpdateType>(item.UpdateType, ignoreCase: true, out var type) ? type : null,
                }, Level(item.UpdateType)));
            }
            if (items.Count == 0)
                continue;

            var pinned = new DependencyPlannedGroup
            {
                Group = before.Group,
                Level = items.Max(i => i.Level),
                AutoMerge = false,
                Approved = now?.Approved ?? false,
                Security = now?.Security ?? false,
                Items = items,
                DepNames = [.. items.Select(i => i.Record.DepName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                TargetMajors = [.. items.Select(i => $"{i.Record.DepName}@{UpdateTypeFloor.TargetMajor(DependencyPolicyEngine.Token(i.Candidate))}")
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            };
            if (now is null)
                groups.Add(pinned);
            else
                groups[groups.IndexOf(now)] = pinned;
            foreach (var item in before.Items)
            {
                var wanted = now?.Items.FirstOrDefault(i => i.Record.DepName == item.DepName && i.Record.PackageFile == item.PackageFile)?.Candidate.Version;
                pins.Add(new DependencyPin(before.Group, item.DepName, item.PackageFile, item.NewVersion, wanted));
            }
        }
        if (pins.Count == 0)
            return (outcome, pins);

        // The policy's apply order (D-P10), kept for the pinned groups too.
        var ordered = groups
            .OrderBy(g => g.Platform is null ? 0 : 1)
            .ThenBy(g => g.Security ? 0 : 1)
            .ThenBy(g => g.Level)
            .ThenBy(g => g.Group, StringComparer.Ordinal)
            .ToList();
        return (outcome with { Groups = ordered }, pins);
    }

    private static bool SameTargets(DependencyPlannedGroup now, PlanGroup before)
    {
        if (now.Items.Count != before.Items.Count)
            return false;
        return before.Items.All(b => now.Items.Any(n => n.Record.DepName == b.DepName && n.Record.PackageFile == b.PackageFile
            && (n.Candidate.Value ?? n.Record.CurrentValue) == b.NewValue && n.Candidate.Version == b.NewVersion));
    }

    private static EUpdateLevel Level(string? updateType) => updateType?.ToLowerInvariant() switch
    {
        "major" => EUpdateLevel.Major,
        "minor" => EUpdateLevel.Minor,
        _ => EUpdateLevel.Patch,
    };
}
