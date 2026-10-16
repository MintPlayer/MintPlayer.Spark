using System.Text.RegularExpressions;
using CodeCoverage.Git.MergeQueue.Resolution;

namespace CodeCoverage.GitWriter.Jobs.Dependencies;

/// <summary>What the line rule accepted in one workflow file: the planned entries it applies and the uses:/with: value
/// pairs the YAML comparison must then find, and nothing else.</summary>
public sealed record WorkflowRefPlan(IReadOnlyList<DependencyPlanEntry> Entries, IReadOnlySet<(string Old, string New)> Uses,
    IReadOnlySet<(string Key, string Old, string New)> With);

/// <summary>
/// The R15 carve-out at the line level (docs/code-coverage/dependency-updates-PRD.md gap C), for
/// <c>.github/workflows/*.yml|yaml</c> and <c>action.yml|yaml</c> under the <c>dependencies</c> profile only:
/// <list type="bullet">
/// <item>every change block replaces k lines with k lines, paired 1:1 in order;</item>
/// <item>a <c>uses: owner/repo[/path]@ref</c> pair keeps everything byte-identical (indentation, quotes,
/// <c>owner/repo</c> in ASCII) except the ref and a trailing <c># vX</c> comment;</item>
/// <item>the new ref is the 40-hex SHA the BOT resolved for the planned tag (<see cref="DependencyPlanEntry.NewDigest"/>),
/// or, where the line pins a tag rather than a SHA, the planned tag itself;</item>
/// <item>a <c>key: value</c> pair may change only the value of a key in the closed setup-action list
/// (<see cref="GitHubWorkflowValidator.VersionKeys"/>), to a version-shaped value: no <c>${{</c>, no block scalar.</item>
/// </list>
/// Which step a key belongs to is decided by <see cref="GitHubWorkflowValidator.ValidateChange"/> on the parsed files.
/// </summary>
public static partial class WorkflowRefPatchRule
{
    [GeneratedRegex(@"\A(?<lead>[ ]*(?:-[ ]+)?uses:[ ]+)(?<q>[""']?)(?<action>[^@\s""'#]+)@(?<ref>[^\s""'#]+)\k<q>(?<tail>(?<cs>[ \t]+)#(?<cs2>[ \t]*)(?<comment>[^\r]*?))?(?<end>[ \t]*\r?)\z", RegexOptions.CultureInvariant)]
    private static partial Regex UsesLine();

    [GeneratedRegex(@"\A(?<lead>[ ]*(?:-[ ]+)?)(?<key>[A-Za-z0-9_-]+):(?<sp>[ ]+)(?<q>[""']?)(?<value>[^\s""'#]*)\k<q>(?<tail>[ \t]+#[^\r]*)?(?<end>[ \t]*\r?)\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyLine();

    /// <summary>An ASCII <c>owner/repo[/path]</c>; a homoglyph never matches.</summary>
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9._-]{1,100}(?:/[A-Za-z0-9._/-]{1,300})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex Action();

    [GeneratedRegex(@"\A[0-9a-f]{40}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha();

    /// <summary>The verdict on one workflow file's hunks against the entries planned for it.</summary>
    public static (WorkflowRefPlan? Plan, string? Reason, string? Detail) Check(PatchFile file, IReadOnlyList<DependencyPlanEntry> entries)
    {
        var used = new List<DependencyPlanEntry>();
        var uses = new HashSet<(string, string)>();
        var with = new HashSet<(string, string, string)>();
        foreach (var hunk in file.Hunks)
        {
            foreach (var block in hunk.Blocks())
            {
                if (block.Removed.Count != block.Added.Count)
                    return (null, DependencyPatchReasons.WorkflowEdit, $"Line {block.OldLine}: a workflow edit that adds or removes lines.");
                for (var k = 0; k < block.Removed.Count; k++)
                {
                    var line = block.OldLine + k;
                    var (old, @new) = (block.Removed[k], block.Added[k]);
                    if (UsesLine().Match(old) is { Success: true } o && UsesLine().Match(@new) is { Success: true } n)
                    {
                        var pair = UsesPair(o, n, entries, line);
                        if (pair.Reason is { } reason)
                            return (null, reason, pair.Detail);
                        used.Add(pair.Entry!);
                        uses.Add(($"{o.Groups["action"].Value}@{o.Groups["ref"].Value}", $"{n.Groups["action"].Value}@{n.Groups["ref"].Value}"));
                        continue;
                    }
                    if (KeyLine().Match(old) is { Success: true } ko && KeyLine().Match(@new) is { Success: true } kn)
                    {
                        var key = ko.Groups["key"].Value;
                        if (!GitHubWorkflowValidator.VersionKeys.Contains(key) || kn.Groups["key"].Value != key
                            || ko.Groups["lead"].Value != kn.Groups["lead"].Value || ko.Groups["sp"].Value != kn.Groups["sp"].Value
                            || ko.Groups["q"].Value != kn.Groups["q"].Value || ko.Groups["tail"].Value != kn.Groups["tail"].Value
                            || ko.Groups["end"].Value != kn.Groups["end"].Value)
                            return (null, DependencyPatchReasons.WorkflowEdit, $"Line {line}: a change to a key outside the closed setup-action version inputs.");
                        var (from, to) = (ko.Groups["value"].Value, kn.Groups["value"].Value);
                        if (!GitHubWorkflowValidator.VersionValue().IsMatch(from) || !GitHubWorkflowValidator.VersionValue().IsMatch(to))
                            return (null, DependencyPatchReasons.WorkflowEdit, $"Line {line}: a setup-action version value that is not one version token.");
                        if (entries.FirstOrDefault(e => e.CurrentValue == from && e.NewValue == to) is not { } entry)
                            return (null, DependencyPatchReasons.UnplannedChange, $"Line {line}: {key} changes to a value no planned entry for this file names.");
                        used.Add(entry);
                        with.Add((key, from, to));
                        continue;
                    }
                    return (null, DependencyPatchReasons.WorkflowEdit, $"Line {line}: a workflow edit other than a uses: ref or a setup-action version.");
                }
            }
        }
        return (new WorkflowRefPlan(used.Distinct().ToList(), uses, with), null, null);
    }

    private readonly record struct PairVerdict(DependencyPlanEntry? Entry, string? Reason, string? Detail);

    private static PairVerdict UsesPair(Match o, Match n, IReadOnlyList<DependencyPlanEntry> entries, int line)
    {
        var action = o.Groups["action"].Value;
        if (!Action().IsMatch(action) || n.Groups["action"].Value != action)
            return new(null, DependencyPatchReasons.WorkflowEdit, $"Line {line}: uses: must keep its ASCII owner/repo byte for byte (no ./local, docker:// or look-alike).");
        if (o.Groups["lead"].Value != n.Groups["lead"].Value || o.Groups["q"].Value != n.Groups["q"].Value || o.Groups["end"].Value != n.Groups["end"].Value)
            return new(null, DependencyPatchReasons.WorkflowEdit, $"Line {line}: uses: changed more than its ref and comment.");
        var (oldRef, newRef) = (o.Groups["ref"].Value, n.Groups["ref"].Value);
        var entry = entries.FirstOrDefault(e => Names(e, action) && (e.CurrentDigest is { } d && d == oldRef || e.CurrentValue == oldRef));
        if (entry is null)
            return new(null, DependencyPatchReasons.UnplannedChange, $"Line {line}: no planned entry for this file updates {action}@{oldRef}.");
        var pinnedSha = entry.CurrentDigest is { } digest && digest == oldRef;
        var resolved = entry.NewDigest is { } sha && Sha().IsMatch(sha) ? sha : null;
        var refAllowed = newRef == resolved || !pinnedSha && !Sha().IsMatch(oldRef) && newRef == entry.NewValue;
        if (!refAllowed)
            return new(null, DependencyPatchReasons.WorkflowRef, $"Line {line}: the new ref of {action} is not the SHA the bot resolved for {entry.NewValue}.");
        var oldTail = o.Groups["tail"];
        var newTail = n.Groups["tail"].Value;
        var tailAllowed = newTail == oldTail.Value
                          || newTail == $" # {entry.NewValue}"
                          || oldTail.Success && newTail == $"{o.Groups["cs"].Value}#{o.Groups["cs2"].Value}{entry.NewValue}";
        if (!tailAllowed)
            return new(null, DependencyPatchReasons.WorkflowEdit, $"Line {line}: the comment after uses: may only become # {entry.NewValue}.");
        return new(entry, null, null);
    }

    /// <summary>The entry is for <paramref name="action"/>: its depName or packageName, or the repository of a sub-path
    /// action (<c>github/codeql-action/init</c> is <c>github/codeql-action</c>).</summary>
    private static bool Names(DependencyPlanEntry entry, string action)
        => new[] { entry.DepName, entry.PackageName }.OfType<string>()
            .Any(name => action == name || action.StartsWith(name + "/", StringComparison.Ordinal));
}
