using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeCoverage.Git.MergeQueue;

/// <summary>Why a pull request leaves the queue instead of being resolved. Each is a verdict on the content; operational
/// failures throw <see cref="GitCommandException"/> instead.</summary>
public enum EvictionReason
{
    /// <summary>A conflicted path matches no auto-resolve rule (O1: hand-authored files are never merged by the bot).</summary>
    ConflictOutsideAllowList,
    /// <summary>git reported a conflict type other than <c>CONFLICT (contents)</c>/<c>CONFLICT (add/add)</c>, or any other
    /// message than <c>Auto-merging</c> (modify/delete, rename, directory/file, binary, mode, submodule, …).</summary>
    UnsupportedConflictType,
    /// <summary>merge-tree exited 1 without a single conflicted entry (a directory-rename conflict, for example).</summary>
    ConflictWithoutStages,
    /// <summary>An allow-listed path's conflicted entries are not two or three regular, non-executable files.</summary>
    UnsupportedEntry,
    /// <summary>A blob of an allow-listed path is over <see cref="MergeQueueResolver.MaxBlobBytes"/>.</summary>
    TooLarge,
    /// <summary>A side of an allow-listed path is not a JSON object.</summary>
    NotJson,
    /// <summary>A conflicting hunk touches a line that is not an allow-listed key's (O2).</summary>
    HunkOutsideAllowedKeys,
}

/// <summary>The result of merging a pull request's head into its base. <see cref="BaseCommit"/> and
/// <see cref="HeadCommit"/> are the commits the refs pointed at when the merge ran.</summary>
public abstract record MergeOutcome(string BaseCommit, string HeadCommit)
{
    /// <summary>No conflict: <see cref="Tree"/> is merge-tree's own result.</summary>
    public sealed record Clean(string BaseCommit, string HeadCommit, string Tree) : MergeOutcome(BaseCommit, HeadCommit);

    /// <summary>Every conflict was on allow-listed lines; <see cref="Paths"/> were set to the base branch's version.</summary>
    public sealed record Resolved(string BaseCommit, string HeadCommit, string Tree, IReadOnlyList<string> Paths) : MergeOutcome(BaseCommit, HeadCommit);

    /// <summary>The pull request leaves the queue; <see cref="Paths"/> are the ones that caused it.</summary>
    public sealed record Evicted(string BaseCommit, string HeadCommit, EvictionReason Reason, IReadOnlyList<string> Paths, string Detail) : MergeOutcome(BaseCommit, HeadCommit);
}

/// <summary>
/// The merge queue's resolver (docs/coverage_merge_queue_PRD.md §5.3 as amended by O1/O2). One three-way
/// <c>git merge-tree --write-tree</c> squashes and rebases the pull request onto its base in a bare repository, with no
/// checkout, no hooks, no merge drivers and no attributes from the repository (<see cref="GitRunner"/>). Conflicts are
/// then either all resolvable, by taking the base branch's version of allow-listed files whose every conflicting hunk
/// sits on allow-listed JSON keys (<see cref="ConflictHunks"/> holds the argument), or the pull request is evicted.
/// The output never contains conflict markers.
/// </summary>
public sealed partial class MergeQueueResolver(GitRunner git)
{
    /// <summary>The cap on one blob of an allow-listed path; Spark's <c>modelHashes.json</c> is a few KB.</summary>
    public const long MaxBlobBytes = 1024 * 1024;

    /// <summary>The only message types a resolvable merge may carry (O2; §5.3 step 2).</summary>
    private static readonly HashSet<string> ResolvableTypes = new(StringComparer.Ordinal) { "CONFLICT (contents)", "CONFLICT (add/add)" };
    private const string AutoMerging = "Auto-merging";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Where the throwaway index of §5.3 step 5 is written; the temp directory when null. The git writer points
    /// it at the job's own directory, which is deleted with the job.</summary>
    public string? ScratchDirectory { get; init; }

    /// <summary>Extra per-command git config for every command the resolver runs (the writer's memory bounds); never a
    /// hardened key, which <see cref="GitRunner"/> refuses.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Config { get; init; } = [];

    [GeneratedRegex(@"\A[0-9a-f]{40}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ObjectId();

    /// <summary>Merges <paramref name="headRef"/> into <paramref name="baseRef"/> in the repository at
    /// <paramref name="gitDirectory"/>. Stage 2 is the base branch, stage 3 the pull request. In a partial clone
    /// (<c>--filter=blob:none</c>) merge-tree fetches the blobs it needs on demand; <paramref name="credential"/> is for those
    /// fetches (docs/coverage_merge_queue_PRD.md S8).</summary>
    public async Task<MergeOutcome> MergeAsync(string gitDirectory, string baseRef, string headRef, IReadOnlyList<AutoResolveRule> rules, CancellationToken cancellationToken, GitCredential? credential = null)
    {
        var format = (await RunAsync(gitDirectory, ["rev-parse", "--show-object-format"], cancellationToken)).StandardOutput.Trim();
        if (format != "sha1")
            throw new GitCommandException("rev-parse", $"object format '{format}' is not supported (the empty attribute tree is SHA-1).");
        var baseCommit = await ResolveCommitAsync(gitDirectory, baseRef, cancellationToken);
        var headCommit = await ResolveCommitAsync(gitDirectory, headRef, cancellationToken);

        var merge = await git.RunAsync(new GitInvocation
        {
            Arguments = ["merge-tree", "--write-tree", "-z", "--messages", baseCommit, headCommit],
            GitDirectory = gitDirectory,
            Credential = credential,
            Timeout = Timeout,
            Config = [.. Config, new("merge.conflictStyle", "zdiff3")],
        }, cancellationToken);
        if (merge.TimedOut || merge.ExitCode is not (0 or 1))
            throw new GitCommandException("merge-tree", merge);
        MergeTreeOutput output;
        try
        {
            output = MergeTreeOutput.Parse(merge.StandardOutput);
        }
        catch (FormatException e)
        {
            throw new GitCommandException("merge-tree", e.Message);
        }

        MergeOutcome Evict(EvictionReason reason, IEnumerable<string> paths, string detail)
            => new MergeOutcome.Evicted(baseCommit, headCommit, reason, paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(), detail);

        var conflicted = output.Stages.Select(s => s.Path).Distinct(StringComparer.Ordinal).ToList();
        var unruled = conflicted.Where(p => !rules.Any(r => r.Matches(p))).ToList();
        if (unruled.Count > 0)
            return Evict(EvictionReason.ConflictOutsideAllowList, unruled, "Only allow-listed generated files are resolved; these conflicts need a human.");

        var unsupported = output.Messages.Where(m => m.Type != AutoMerging && !ResolvableTypes.Contains(m.Type)).ToList();
        if (unsupported.Count > 0)
            return Evict(EvictionReason.UnsupportedConflictType, unsupported.SelectMany(m => m.Paths), string.Join("; ", unsupported.Select(m => m.Type).Distinct()));

        if (merge.ExitCode == 1 && output.Stages.Count == 0)
            return Evict(EvictionReason.ConflictWithoutStages, output.Messages.SelectMany(m => m.Paths), "merge-tree reported a conflict without conflicted entries.");
        if (merge.ExitCode == 0)
            return new MergeOutcome.Clean(baseCommit, headCommit, output.Tree);

        var resolutions = new List<MergeTreeStage>();
        foreach (var path in conflicted)
        {
            var rule = rules.First(r => r.Matches(path));
            var entries = output.Stages.Where(s => s.Path == path).ToDictionary(s => s.Stage);
            if (!entries.TryGetValue(2, out var ours) || !entries.ContainsKey(3) || entries.Values.Any(e => e.Mode != "100644"))
                return Evict(EvictionReason.UnsupportedEntry, [path], "Both sides must hold the file as a regular, non-executable file.");

            if (rule.AnyLine)
            {
                // Q7's whole-file entry: no hunk check, no JSON; the size cap still holds (a side is never read whole).
                foreach (var entry in entries.Values)
                {
                    if (await BlobSizeAsync(gitDirectory, entry.ObjectId, credential, cancellationToken) > MaxBlobBytes)
                        return Evict(EvictionReason.TooLarge, [path], $"A side is over {MaxBlobBytes} bytes.");
                }
                resolutions.Add(ours);
                continue;
            }

            var sides = new Dictionary<int, string>();
            foreach (var (stage, entry) in entries)
            {
                if (await ReadBlobAsync(gitDirectory, entry.ObjectId, credential, cancellationToken) is not { } text)
                    return Evict(EvictionReason.TooLarge, [path], $"A side is over {MaxBlobBytes} bytes.");
                sides[stage] = text;
            }
            if (await ReadBlobAsync(gitDirectory, $"{output.Tree}:{path}", credential, cancellationToken) is not { } merged)
                return Evict(EvictionReason.TooLarge, [path], $"The merged file is over {MaxBlobBytes} bytes.");

            var allowed = new Dictionary<int, IReadOnlySet<string>>();
            foreach (var (stage, text) in sides)
            {
                if (ConflictHunks.AllowedLineTexts(text, rule.Keys) is not { } set)
                    return Evict(EvictionReason.NotJson, [path], $"Stage {stage} is not a JSON object.");
                allowed[stage] = set;
            }

            var hunks = ConflictHunks.Parse(merged);
            if (hunks is null || hunks.Count == 0)
                return Evict(EvictionReason.HunkOutsideAllowedKeys, [path], "The conflict regions could not be read.");
            static bool Within(IReadOnlyList<string> lines, IReadOnlySet<string>? allowed) => lines.Count == 0 || allowed is not null && lines.All(allowed.Contains);
            if (hunks.Any(h => !Within(h.Ours, allowed[2]) || !Within(h.Theirs, allowed[3]) || !Within(h.Base, allowed.GetValueOrDefault(1))))
                return Evict(EvictionReason.HunkOutsideAllowedKeys, [path], $"A conflicting hunk touches a line outside {string.Join(", ", rule.Keys.Order(StringComparer.Ordinal))}.");

            resolutions.Add(ours);
        }

        var tree = await ReplaceAsync(gitDirectory, output.Tree, resolutions, cancellationToken);
        return new MergeOutcome.Resolved(baseCommit, headCommit, tree, resolutions.Select(r => r.Path).ToList());
    }

    /// <summary>The squashed commit: <paramref name="tree"/> on top of <paramref name="baseCommit"/>, authored by the pull
    /// request's author and committed by the bot (§5.1). Returns its OID.</summary>
    public async Task<string> CommitAsync(string gitDirectory, string tree, string baseCommit, GitIdentity author, GitIdentity committer, string message, CancellationToken cancellationToken)
    {
        if (!ObjectId().IsMatch(tree) || !ObjectId().IsMatch(baseCommit))
            throw new ArgumentException("The tree and the parent must be full object ids.");
        var result = await RunAsync(gitDirectory, ["commit-tree", tree, "-p", baseCommit, "-F", "-"], cancellationToken, input: message, author: author, committer: committer);
        return Oid(result, "commit-tree");
    }

    private async Task<string> ResolveCommitAsync(string gitDirectory, string reference, CancellationToken cancellationToken)
    {
        var result = await git.RunAsync(new GitInvocation
        {
            Arguments = ["rev-parse", "--verify", "--quiet", "--end-of-options", reference + "^{commit}"],
            Config = Config,
            GitDirectory = gitDirectory,
            Timeout = Timeout,
        }, cancellationToken);
        if (!result.Succeeded)
            throw new GitCommandException("rev-parse", $"'{reference}' is not a commit in the repository.");
        return Oid(result, "rev-parse");
    }

    /// <summary>A blob's text, or null when it is over the cap (the size is asked first, so a huge blob is never
    /// read).</summary>
    private async Task<string?> ReadBlobAsync(string gitDirectory, string objectName, GitCredential? credential, CancellationToken cancellationToken)
    {
        if (await BlobSizeAsync(gitDirectory, objectName, credential, cancellationToken) > MaxBlobBytes)
            return null;
        return (await RunAsync(gitDirectory, ["cat-file", "blob", objectName], cancellationToken, credential: credential)).StandardOutput;
    }

    private async Task<long> BlobSizeAsync(string gitDirectory, string objectName, GitCredential? credential, CancellationToken cancellationToken)
    {
        var size = await RunAsync(gitDirectory, ["cat-file", "-s", objectName], cancellationToken, credential: credential);
        if (!long.TryParse(size.StandardOutput.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var bytes))
            throw new GitCommandException("cat-file", "the size does not parse.");
        return bytes;
    }

    /// <summary>§5.3 step 5: the merged tree with the resolved paths replaced, built in a throwaway index so nothing
    /// else in the repository changes.</summary>
    private async Task<string> ReplaceAsync(string gitDirectory, string tree, IReadOnlyList<MergeTreeStage> entries, CancellationToken cancellationToken)
    {
        var index = Path.Combine(ScratchDirectory ?? Path.GetTempPath(), $"codecoverage-mq-{Guid.NewGuid():N}.index");
        try
        {
            await RunAsync(gitDirectory, ["read-tree", tree], cancellationToken, index: index);
            var info = new StringBuilder();
            foreach (var entry in entries)
                info.Append(entry.Mode).Append(' ').Append(entry.ObjectId).Append('\t').Append(entry.Path).Append('\0');
            await RunAsync(gitDirectory, ["update-index", "-z", "--index-info"], cancellationToken, input: info.ToString(), index: index);
            // --missing-ok: in a blob-less partial clone the tree names blobs that were never fetched (they exist on the
            // forge); without it write-tree would try to fetch every one of them (M3: "could not fetch … from promisor
            // remote"). The only blobs the resolver adds are the stage-2 ones it already read.
            return Oid(await RunAsync(gitDirectory, ["write-tree", "--missing-ok"], cancellationToken, index: index), "write-tree");
        }
        finally
        {
            File.Delete(index);
            File.Delete(index + ".lock");
        }
    }

    private async Task<GitResult> RunAsync(string gitDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken,
        string? input = null, string? index = null, GitIdentity? author = null, GitIdentity? committer = null, GitCredential? credential = null)
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
        if (!result.Succeeded)
            throw new GitCommandException(arguments[0], result);
        return result;
    }

    private static string Oid(GitResult result, string verb)
    {
        var oid = result.StandardOutput.Trim();
        return ObjectId().IsMatch(oid) ? oid : throw new GitCommandException(verb, "did not print an object id.");
    }
}
