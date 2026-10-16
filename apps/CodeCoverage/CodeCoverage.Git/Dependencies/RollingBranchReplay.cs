using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodeCoverage.Git.MergeQueue;

namespace CodeCoverage.Git.Dependencies;

/// <summary>A planned manifest edit, as the replay needs it: which file, which dependency, the value before and the bot's.</summary>
public sealed record ReplayManifestEntry(string DepName, string? PackageName, string PackageFile, string CurrentValue, string NewValue);

/// <summary>
/// Q11/R4: a human set <paramref name="HeldValue"/> for <paramref name="DepName"/> on the rolling branch in
/// <paramref name="Commit"/>. The value wins and becomes a hold (the bot had proposed <paramref name="ProposedValue"/>).
/// </summary>
public sealed record HumanManifestValue(string DepName, string PackageFile, string HeldValue, string? ProposedValue, string Commit, string? Author);

/// <summary>What replaying the human commits of the old rolling branch onto the rebuilt bot commit made.</summary>
public abstract record ReplayOutcome(IReadOnlyList<HumanManifestValue> HumanValues)
{
    /// <summary>Every human commit applied; <paramref name="Tip"/> is the new rolling head (the bot commit when there was none).</summary>
    public sealed record Replayed(string Tip, IReadOnlyList<string> Commits, IReadOnlyList<HumanManifestValue> HumanValues) : ReplayOutcome(HumanValues);

    /// <summary>
    /// R4/§7.3 "anything else": a human commit conflicts outside a planned manifest (a lockfile included: lockfiles are never
    /// merged textually), so nothing is pushed and the branch is kept for a person. The manifest values found so far still
    /// become holds, so the next run does not plan what the human changed.
    /// </summary>
    public sealed record Conflict(string Commit, IReadOnlyList<string> Paths, string Detail, IReadOnlyList<HumanManifestValue> HumanValues) : ReplayOutcome(HumanValues);
}

/// <summary>
/// The human half of R4's rebuild (docs/code-coverage/dependency-updates-PRD.md §7.3, P6): the rolling branch is rebuilt from
/// the default branch each slot, and the commits people pushed on top of the bot's last commit are replayed onto the new one
/// with <c>git merge-tree --merge-base</c>, a cherry-pick without a worktree. Plumbing only: nothing of the repository runs,
/// no hook, no filter, no merge driver (the writer's hardened <see cref="GitRunner"/>).
/// </summary>
/// <remarks>
/// <para><b>Resolution table</b> (§7.3, as resolver rules):</para>
/// <list type="bullet">
/// <item>a conflict in a <b>planned manifest</b>: every conflict region takes the human's lines ("the human's value wins");
/// the bot's other edits in that file stay;</item>
/// <item>any other conflict, a <b>lockfile</b> included (never merged textually), a delete, a mode change or a binary:
/// <see cref="ReplayOutcome.Conflict"/>;</item>
/// <item>a merge commit of the human's is skipped: the rebuilt branch already holds the default branch it merged.</item>
/// </list>
/// <para>Each replayed commit keeps the human's author, committer, dates and message, so a replay of the same input builds
/// the same commits.</para>
/// </remarks>
public sealed partial class RollingBranchReplay(GitRunner git)
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Where the throwaway index files go; the system temp directory when null.</summary>
    public string? ScratchDirectory { get; init; }

    public IReadOnlyList<KeyValuePair<string, string>> Config { get; init; } = [];

    /// <summary>The most human commits replayed; a longer history is a conflict for a person.</summary>
    public const int MaxCommits = 50;

    private const int MarkerSize = 7;

    /// <summary>
    /// The human commits on <paramref name="oldTip"/> above <paramref name="botCommit"/>, oldest first, merges left out. Null
    /// when <paramref name="botCommit"/> is not an ancestor of <paramref name="oldTip"/> (someone rewrote the branch).
    /// </summary>
    public async Task<IReadOnlyList<string>?> HumanCommitsAsync(string gitDirectory, string botCommit, string oldTip, CancellationToken cancellationToken, GitCredential? credential = null)
    {
        if (botCommit == oldTip)
            return [];
        var ancestor = await RunAsync(gitDirectory, ["merge-base", "--is-ancestor", botCommit, oldTip], cancellationToken, credential, allowFailure: true);
        if (!ancestor.Succeeded)
            return null;
        var list = await RunAsync(gitDirectory, ["rev-list", "--reverse", "--topo-order", "--no-merges", $"{botCommit}..{oldTip}"], cancellationToken, credential);
        return [.. list.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>Replays <paramref name="humanCommits"/> (oldest first) onto <paramref name="newBotCommit"/>.</summary>
    public async Task<ReplayOutcome> ReplayAsync(string gitDirectory, string newBotCommit, IReadOnlyList<string> humanCommits,
        IReadOnlyList<ReplayManifestEntry> entries, CancellationToken cancellationToken, GitCredential? credential = null)
    {
        var values = new List<HumanManifestValue>();
        if (humanCommits.Count > MaxCommits)
            return new ReplayOutcome.Conflict(humanCommits[0], [], $"{humanCommits.Count} commits were pushed on the rolling branch; at most {MaxCommits} are replayed.", values);

        var manifests = entries.Select(e => e.PackageFile).ToHashSet(StringComparer.Ordinal);
        var tip = newBotCommit;
        var made = new List<string>();
        foreach (var commit in humanCommits)
        {
            var info = await CommitInfoAsync(gitDirectory, commit, cancellationToken, credential);
            if (info.Parent is not { } parent)
                return new ReplayOutcome.Conflict(commit, [], "A root commit on the rolling branch cannot be replayed.", values);

            values.AddRange(await HumanValuesAsync(gitDirectory, parent, commit, info.AuthorName, entries, manifests, cancellationToken, credential));

            var merge = await git.RunAsync(new GitInvocation
            {
                Arguments = ["merge-tree", "--write-tree", "-z", "--messages", $"--merge-base={parent}", tip, commit],
                GitDirectory = gitDirectory,
                Credential = credential,
                Timeout = Timeout,
                Config = [.. Config, new("merge.conflictStyle", "zdiff3")],
            }, cancellationToken);
            if (merge.TimedOut || merge.ExitCode is not (0 or 1))
                throw new GitCommandException("merge-tree", merge);
            var output = MergeTreeOutput.Parse(merge.StandardOutput);

            var tree = output.Tree;
            if (merge.ExitCode == 1)
            {
                var conflicted = output.Stages.Select(s => s.Path).Distinct(StringComparer.Ordinal).ToList();
                var foreign = conflicted.Where(p => !manifests.Contains(p)).ToList();
                if (foreign.Count > 0 || output.Stages.Count == 0)
                    return new ReplayOutcome.Conflict(commit, foreign.Count > 0 ? foreign : conflicted,
                        $"{Short(commit)} conflicts with the rebuilt dependency commit outside a planned manifest; the rolling branch was kept as it is.", values);
                var replacements = new List<MergeTreeStage>();
                foreach (var path in conflicted)
                {
                    var stages = output.Stages.Where(s => s.Path == path).ToList();
                    if (!stages.Any(s => s.Stage == 2) || !stages.Any(s => s.Stage == 3) || stages.Any(s => s.Mode != "100644"))
                        return new ReplayOutcome.Conflict(commit, [path], $"{Short(commit)} deletes or retypes {path}, which the dependency commit edits.", values);
                    var merged = (await RunAsync(gitDirectory, ["cat-file", "blob", $"{output.Tree}:{path}"], cancellationToken, credential)).StandardOutput;
                    if (TakeTheirs(merged) is not { } resolved)
                        return new ReplayOutcome.Conflict(commit, [path], $"The conflict regions of {path} could not be read.", values);
                    var blob = Oid(await RunAsync(gitDirectory, ["hash-object", "-w", "--stdin"], cancellationToken, credential, input: resolved), "hash-object");
                    replacements.Add(new MergeTreeStage("100644", blob, 0, path));
                }
                tree = await ReplaceAsync(gitDirectory, output.Tree, replacements, cancellationToken);
            }

            GitIdentity author, committer;
            try
            {
                author = new GitIdentity(info.AuthorName, info.AuthorEmail, info.AuthorDate);
                committer = new GitIdentity(info.CommitterName, info.CommitterEmail, info.CommitterDate);
            }
            catch (ArgumentException)
            {
                return new ReplayOutcome.Conflict(commit, [], $"{Short(commit)}'s author or committer cannot be written into a new commit.", values);
            }
            tip = Oid(await RunAsync(gitDirectory, ["commit-tree", tree, "-p", tip, "-F", "-"], cancellationToken, credential, input: info.Message,
                author: author, committer: committer), "commit-tree");
            made.Add(tip);
        }
        return new ReplayOutcome.Replayed(tip, made, values);
    }

    /// <summary>
    /// The human's manifest values in one commit: each changed line of a planned manifest that names a planned dependency (on
    /// the line, or up to three lines above it, as D-V2). The value is the token where the two lines differ, after their
    /// common prefix and suffix: compared, never parsed (R1).
    /// </summary>
    internal async Task<IReadOnlyList<HumanManifestValue>> HumanValuesAsync(string gitDirectory, string parent, string commit, string? author,
        IReadOnlyList<ReplayManifestEntry> entries, IReadOnlySet<string> manifests, CancellationToken cancellationToken, GitCredential? credential)
    {
        if (manifests.Count == 0)
            return [];
        var diff = await RunAsync(gitDirectory, ["diff-tree", "-p", "-U3", "--no-renames", "--no-color", parent, commit, "--", .. manifests.Order(StringComparer.Ordinal)],
            cancellationToken, credential);
        return ReadHumanValues(diff.StandardOutput, commit, author, entries);
    }

    /// <summary>The parse half of <see cref="HumanValuesAsync"/>, over a unified diff.</summary>
    public static IReadOnlyList<HumanManifestValue> ReadHumanValues(string unifiedDiff, string commit, string? author, IReadOnlyList<ReplayManifestEntry> entries)
    {
        var found = new List<HumanManifestValue>();
        string? file = null;
        var context = new List<string>();
        var removed = new List<string>();
        var added = new List<string>();

        void Flush()
        {
            if (file is not null)
            {
                for (var k = 0; k < Math.Min(removed.Count, added.Count); k++)
                {
                    var above = context.Concat(removed.Take(k)).TakeLast(3).ToList();
                    foreach (var entry in entries.Where(e => e.PackageFile == file))
                    {
                        // The line names the dependency; its old side held the previous build's value, which need not be this
                        // plan's (a newer version may have come out since), so it is not compared.
                        if (!Names(entry, removed[k], above) && !Names(entry, added[k], above))
                            continue;
                        var value = Difference(removed[k], added[k]);
                        if (value.Length == 0 || value == entry.NewValue || found.Any(f => f.DepName == entry.DepName && f.PackageFile == file && f.Commit == commit))
                            continue;
                        found.Add(new HumanManifestValue(entry.DepName, file, value, entry.NewValue, commit, author));
                    }
                }
            }
            context.AddRange(removed);
            removed.Clear();
            added.Clear();
        }

        foreach (var raw in unifiedDiff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Flush();
                file = null;
                context.Clear();
                continue;
            }
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                file = line.StartsWith("+++ b/", StringComparison.Ordinal) ? line["+++ b/".Length..] : null;
                continue;
            }
            if (line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("index ", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                Flush();
                context.Clear();
                continue;
            }
            if (line.StartsWith('-'))
            {
                if (added.Count > 0)
                    Flush();
                removed.Add(line[1..]);
            }
            else if (line.StartsWith('+'))
                added.Add(line[1..]);
            else
            {
                Flush();
                context.Add(line.Length > 0 ? line[1..] : line);
            }
        }
        Flush();
        return found;
    }

    private static bool Names(ReplayManifestEntry entry, string line, IReadOnlyList<string> above)
    {
        var names = new[] { entry.DepName, entry.PackageName, LastSegment(entry.DepName), entry.PackageName is { } p ? LastSegment(p) : null }
            .OfType<string>().Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        return names.Any(n => line.Contains(n, StringComparison.Ordinal) || above.Any(a => a.Contains(n, StringComparison.Ordinal)));
    }

    private static string LastSegment(string name)
    {
        var cut = name.LastIndexOfAny(['/', ':']);
        return cut >= 0 && cut < name.Length - 1 ? name[(cut + 1)..] : name;
    }

    /// <summary>The part of <paramref name="after"/> that differs from <paramref name="before"/>, between their common prefix
    /// and suffix, widened to whole version-token characters.</summary>
    public static string Difference(string before, string after)
    {
        var prefix = 0;
        while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix])
            prefix++;
        var suffix = 0;
        while (suffix < before.Length - prefix && suffix < after.Length - prefix && before[^(suffix + 1)] == after[^(suffix + 1)])
            suffix++;
        var start = prefix;
        while (start > 0 && IsTokenChar(after[start - 1]))
            start--;
        var end = after.Length - suffix;
        while (end < after.Length && IsTokenChar(after[end]))
            end++;
        return start >= end ? string.Empty : after[start..end];
    }

    /// <summary>What a version token is made of when the difference is widened to whole tokens; a range operator that changed
    /// is inside the difference already, and quotes, tags and <c>=</c> end a token.</summary>
    private static bool IsTokenChar(char c) => char.IsLetterOrDigit(c) || c is '.' or '-' or '+' or '_';

    /// <summary>
    /// Every zdiff3 conflict region of <paramref name="merged"/> resolved for the human: where the region's three sides have
    /// as many lines, line by line, the human's line where the human changed it and the bot's otherwise (a bot edit on the
    /// next line survives a human edit on this one); else the human's lines whole. Null when a region does not parse. Line
    /// endings are kept as the file has them.
    /// </summary>
    public static string? TakeTheirs(string merged)
    {
        var output = new StringBuilder(merged.Length);
        var lines = SplitKeepingEnds(merged);
        var i = 0;
        while (i < lines.Count)
        {
            if (!IsMarker(lines[i], '<'))
            {
                output.Append(lines[i++]);
                continue;
            }
            i++;
            var ours = new List<string>();
            List<string>? @base = null;
            var theirs = new List<string>();
            while (i < lines.Count && !IsMarker(lines[i], '|') && !IsMarker(lines[i], '='))
                ours.Add(lines[i++]);
            if (i < lines.Count && IsMarker(lines[i], '|'))
            {
                i++;
                @base = [];
                while (i < lines.Count && !IsMarker(lines[i], '='))
                    @base.Add(lines[i++]);
            }
            if (i >= lines.Count)
                return null;
            i++; // =======
            while (i < lines.Count && !IsMarker(lines[i], '>'))
                theirs.Add(lines[i++]);
            if (i >= lines.Count)
                return null;
            i++; // >>>>>>>

            if (@base is not null && @base.Count == ours.Count && ours.Count == theirs.Count)
            {
                for (var k = 0; k < theirs.Count; k++)
                    output.Append(theirs[k] != @base[k] ? theirs[k] : ours[k]);
            }
            else
            {
                foreach (var line in theirs)
                    output.Append(line);
            }
        }
        return output.ToString();
    }

    private static bool IsMarker(string line, char c)
    {
        if (line.Length < MarkerSize)
            return false;
        for (var k = 0; k < MarkerSize; k++)
        {
            if (line[k] != c)
                return false;
        }
        var rest = line[MarkerSize..];
        return c == '=' ? rest.TrimEnd('\r', '\n').Length == 0 : rest.Length == 0 || rest[0] is ' ' or '\r' or '\n';
    }

    private static List<string> SplitKeepingEnds(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
                continue;
            lines.Add(text[start..(i + 1)]);
            start = i + 1;
        }
        if (start < text.Length)
            lines.Add(text[start..]);
        return lines;
    }

    private sealed record CommitInfo(string? Parent, string AuthorName, string AuthorEmail, DateTimeOffset AuthorDate,
        string CommitterName, string CommitterEmail, DateTimeOffset CommitterDate, string Message);

    private async Task<CommitInfo> CommitInfoAsync(string gitDirectory, string commit, CancellationToken cancellationToken, GitCredential? credential)
    {
        var raw = (await RunAsync(gitDirectory, ["log", "-1", "--format=%P%x00%an%x00%ae%x00%aI%x00%cn%x00%ce%x00%cI%x00%B", commit], cancellationToken, credential)).StandardOutput;
        var fields = raw.Split('\0', 8);
        if (fields.Length < 8)
            throw new GitCommandException("log", "did not print the commit's fields.");
        var parents = fields[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var message = fields[7].TrimEnd('\n') + "\n";
        return new CommitInfo(parents.FirstOrDefault(), fields[1], fields[2], DateTimeOffset.Parse(fields[3], CultureInfo.InvariantCulture),
            fields[4], fields[5], DateTimeOffset.Parse(fields[6], CultureInfo.InvariantCulture), message);
    }

    private async Task<string> ReplaceAsync(string gitDirectory, string tree, IReadOnlyList<MergeTreeStage> entries, CancellationToken cancellationToken)
    {
        var index = Path.Combine(ScratchDirectory ?? Path.GetTempPath(), $"codecoverage-replay-{Guid.NewGuid():N}.index");
        try
        {
            await RunAsync(gitDirectory, ["read-tree", tree], cancellationToken, null, index: index);
            var info = new StringBuilder();
            foreach (var entry in entries)
                info.Append(entry.Mode).Append(' ').Append(entry.ObjectId).Append('\t').Append(entry.Path).Append('\0');
            await RunAsync(gitDirectory, ["update-index", "-z", "--index-info"], cancellationToken, null, input: info.ToString(), index: index);
            return Oid(await RunAsync(gitDirectory, ["write-tree", "--missing-ok"], cancellationToken, null, index: index), "write-tree");
        }
        finally
        {
            File.Delete(index);
            File.Delete(index + ".lock");
        }
    }

    private async Task<GitResult> RunAsync(string gitDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken, GitCredential? credential,
        string? input = null, string? index = null, GitIdentity? author = null, GitIdentity? committer = null, bool allowFailure = false)
    {
        var result = await git.RunAsync(new GitInvocation
        {
            Arguments = arguments,
            GitDirectory = gitDirectory,
            Credential = credential,
            Timeout = Timeout,
            StandardInput = input,
            IndexFile = index,
            Author = author,
            Committer = committer,
            Config = Config,
        }, cancellationToken);
        if (!result.Succeeded && !allowFailure)
            throw new GitCommandException(arguments[0], result);
        return result;
    }

    [GeneratedRegex(@"\A[0-9a-f]{40}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ObjectId();

    private static string Oid(GitResult result, string verb)
    {
        var oid = result.StandardOutput.Trim();
        return ObjectId().IsMatch(oid) ? oid : throw new GitCommandException(verb, "did not print an object id.");
    }

    private static string Short(string sha) => sha.Length > 12 ? sha[..12] : sha;
}
