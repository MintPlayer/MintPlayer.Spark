using CodeCoverage.Git.Dependencies;

namespace CodeCoverage.Dependencies.Lifecycle;

/// <summary>
/// D-N2: the plan that regenerates the lockfiles a replay could not merge. The merge queue's answer to a lockfile conflict is
/// "take one side, then regenerate" (O35/O37); here the bot's side is taken by the writer, and this plan asks the repository's
/// own apply workflow to regenerate from the manifests as they stand on the replayed head.
/// </summary>
/// <remarks>
/// <para><b>One group per lockfile</b> (<c>relock:&lt;path&gt;</c>), holding the planned items that own it as
/// <b>lockfile-only</b> updates (current value = new value = what the bot wrote), so nothing in a manifest changes and the
/// writer's <c>dependencies</c> profile accepts only lockfile changes, which gap B's scan then judges (D-V5: a lockfile-only
/// entry counts as applied when its lockfile changed). The adapters' pin and verify steps hold each owner at its planned
/// version, and the whole-tree consistency check (D-C3) proves the result installs with the humans' manifest edits.</para>
/// <para><b>Owners a person changed by hand are left out</b>: their version is the person's, not the plan's, and pinning
/// the plan's would undo it. A lockfile whose every owner a person changed cannot be regenerated this way; the replay then
/// stops, and the Q11 holds the replay recorded make the next rebuild leave those dependencies alone.</para>
/// <para>No version is parsed or invented (R1): every token comes from the plan the bot already committed.</para>
/// </remarks>
public static class DependencyRelockPlan
{
    public const string GroupPrefix = "relock:";

    /// <summary>The relock plan on <paramref name="baseSha"/>, or null when some lockfile has no owner left to pin.</summary>
    public static DependencyPlan? Build(DependencyPlan plan, IReadOnlyList<string> lockfiles, IReadOnlySet<(string DepName, string PackageFile)> changedByHand,
        string planId, string baseSha)
    {
        if (lockfiles.Count == 0)
            return null;
        var groups = new List<PlanGroup>();
        foreach (var path in lockfiles.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var owners = plan.Groups.SelectMany(g => g.Items)
                .Where(i => i.LockFiles.Contains(path, StringComparer.Ordinal) && !changedByHand.Contains((i.DepName, i.PackageFile)))
                .DistinctBy(i => (i.DepName, i.PackageFile))
                .ToList();
            if (owners.Count == 0)
                return null;
            groups.Add(new PlanGroup
            {
                Group = GroupPrefix + path,
                Items = [.. owners.Select(i => i with { CurrentValue = i.NewValue, CurrentDigest = i.NewDigest, UpdateType = "lockfileUpdate" })],
            });
        }
        return new DependencyPlan { PlanId = planId, ExpectedSha = baseSha, Options = plan.Options, Groups = groups };
    }
}
