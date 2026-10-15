using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodeCoverage.Git.MergeQueue.Resolution;

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
    /// <summary>A blob of an allow-listed path is over its strategy's cap (<see cref="IConflictResolver.MaxSideBytes"/>: take-base
    /// 16 MiB, never parsed; keep-both, line-set and the validators 4 MiB, §10.6).</summary>
    TooLarge,
    /// <summary>A side of an allow-listed path is not a JSON object.</summary>
    NotJson,
    /// <summary>A conflicting hunk touches a line that is not an allow-listed key's (O2).</summary>
    HunkOutsideAllowedKeys,
    /// <summary>The rule's strategy cannot resolve this conflict's shape (keep-both: add/add, a changed base line, a
    /// marker-like line, a side that is not UTF-8; §10.4 step 2).</summary>
    ResolverRefused,
    /// <summary>A side already fails one of the rule's validators, so the result is not judged (inputs first, §10.4
    /// step 4).</summary>
    InvalidSide,
    /// <summary>Every side passes the rule's validators, the resolved content does not (or it holds a conflict
    /// marker).</summary>
    InvalidResult,
    /// <summary>git merged the file cleanly, both sides pass the rule's validators, and the merged file does not
    /// (§10.4, clean-merge validation).</summary>
    MergedContentInvalid,
}

/// <summary>The result of merging a pull request's head into its base. <see cref="BaseCommit"/> and
/// <see cref="HeadCommit"/> are the commits the refs pointed at when the merge ran.</summary>
public abstract record MergeOutcome(string BaseCommit, string HeadCommit)
{
    /// <summary>No conflict: <see cref="Tree"/> is merge-tree's own result.</summary>
    public sealed record Clean(string BaseCommit, string HeadCommit, string Tree) : MergeOutcome(BaseCommit, HeadCommit);

    /// <summary>Every conflict was resolved under its rule; <see cref="Resolutions"/> says how, path by path.</summary>
    public sealed record Resolved(string BaseCommit, string HeadCommit, string Tree, IReadOnlyList<string> Paths) : MergeOutcome(BaseCommit, HeadCommit)
    {
        /// <summary>One per resolved path, in <see cref="Paths"/>' order (§10.6).</summary>
        public IReadOnlyList<ResolvedPath> Resolutions { get; init; } = [];
    }

    /// <summary>The pull request leaves the queue; <see cref="Paths"/> are the ones that caused it.</summary>
    public sealed record Evicted(string BaseCommit, string HeadCommit, EvictionReason Reason, IReadOnlyList<string> Paths, string Detail) : MergeOutcome(BaseCommit, HeadCommit);
}

/// <summary>How one conflicted path was resolved (§10.6): the strategy, whether it needs the regenerate workflow, and the
/// index of the <c>autoResolve</c> rule that matched it (the app maps that rule to its workflow, O36).</summary>
public sealed record ResolvedPath(string Path, string Strategy, bool Regenerate, int Rule);

/// <summary>
/// The merge queue's resolver (docs/coverage_merge_queue_PRD.md §5.3 as amended by O1/O2, and §10). One three-way
/// <c>git merge-tree --write-tree</c> squashes and rebases the pull request onto its base in a bare repository, with no
/// checkout, no hooks, no merge drivers and no attributes from the repository (<see cref="GitRunner"/>). Each conflicted
/// path is then dispatched to the strategy of the first <c>autoResolve</c> rule that matches it, through
/// <see cref="ConflictResolverRegistry"/> (§10.3); one refusal evicts the pull request. The output never contains
/// conflict markers.
/// </summary>
public sealed partial class MergeQueueResolver(GitRunner git, ConflictResolverRegistry? registry = null)
{
    /// <summary>
    /// The resolver's own version (merge-queue PRD O35 #4). The writer answers it with <c>git --version</c> as the prepare's
    /// <c>resolverVersion</c>: a later job that rebuilds another commit under another version restarts the entry instead
    /// of evicting it. Bump it whenever a change here can change a prepared tree.
    /// </summary>
    public const string ResolverVersion = "resolution-2";

    /// <summary>The strategies and validators; the built-in set when none was given.</summary>
    public ConflictResolverRegistry Registry { get; } = registry ?? ConflictResolverRegistry.Default;

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

    /// <summary>
    /// Why a conflict outside the allow-list ends the run (O1), as the eviction message's second paragraph shows it. The
    /// saga's own sentence says what to do; before, this also said "these conflicts need a human", which repeated it
    /// (merge-queue PRD §0 D72).
    /// </summary>
    public const string OutsideAllowListDetail = "Only allow-listed generated files are resolved automatically.";

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

        AutoResolveRule? RuleFor(string path) => rules.FirstOrDefault(r => r.Matches(path));
        var conflicted = output.Stages.Select(s => s.Path).Distinct(StringComparer.Ordinal).ToList();
        // A path no rule matches, or whose first matching rule says `evict`, is never merged by the bot (O1, §10.3).
        var unruled = conflicted.Where(p => RuleFor(p) is not { } r || r.Strategy == MergeQueueWireRules.Evict).ToList();
        if (unruled.Count > 0)
            return Evict(EvictionReason.ConflictOutsideAllowList, unruled, OutsideAllowListDetail);

        var unsupported = output.Messages.Where(m => m.Type != AutoMerging && !ResolvableTypes.Contains(m.Type)).ToList();
        if (unsupported.Count > 0)
            return Evict(EvictionReason.UnsupportedConflictType, unsupported.SelectMany(m => m.Paths), string.Join("; ", unsupported.Select(m => m.Type).Distinct()));

        if (merge.ExitCode == 1 && output.Stages.Count == 0)
            return Evict(EvictionReason.ConflictWithoutStages, output.Messages.SelectMany(m => m.Paths), "merge-tree reported a conflict without conflicted entries.");
        var repository = new ConflictRepository(git, gitDirectory, credential, Timeout, Config, cancellationToken);

        // §10.4, clean-merge validation: a file git merged on its own is held to its rule's validators too, inputs first.
        foreach (var path in output.Messages.Where(m => m.Type == AutoMerging).SelectMany(m => m.Paths).Distinct(StringComparer.Ordinal).Except(conflicted, StringComparer.Ordinal))
        {
            if (RuleFor(path) is not { Validate.Count: > 0 } rule || rule.Strategy == MergeQueueWireRules.Evict)
                continue;
            if (await ValidateCleanMergeAsync(repository, rule, path, baseCommit, headCommit, output.Tree) is { } invalid)
                return Evict(invalid.Reason, [path], invalid.Detail);
        }

        if (merge.ExitCode == 0)
            return new MergeOutcome.Clean(baseCommit, headCommit, output.Tree);

        var replacements = new List<MergeTreeStage>();
        var resolved = new List<ResolvedPath>();
        foreach (var path in conflicted)
        {
            // §10.3: the first matching rule names the strategy; the registry runs it.
            var rule = RuleFor(path)!;
            var resolver = Registry.Resolver(rule.Strategy);
            var entries = output.Stages.Where(s => s.Path == path).ToDictionary(s => s.Stage);
            if (!resolver.CanResolve(path, entries, rule))
                return Evict(EvictionReason.UnsupportedEntry, [path], "Both sides must hold the file as a regular, non-executable file.");
            foreach (var entry in entries.Values)
            {
                if (await repository.SizeAsync(entry.ObjectId) > resolver.MaxSideBytes)
                    return Evict(EvictionReason.TooLarge, [path], $"A side is over {resolver.MaxSideBytes} bytes.");
            }

            var input = new ConflictInput(path, rule, entries, output.Tree, repository);
            switch (await resolver.ResolveAsync(input, cancellationToken))
            {
                case ConflictResolution.UseStage use when entries.TryGetValue(use.Stage, out var stage):
                    replacements.Add(stage with { Stage = 0 });
                    break;
                case ConflictResolution.Content content:
                    if (await ValidateContentAsync(input, content.Text, resolver.MaxSideBytes) is { } invalid)
                        return Evict(invalid.Reason, [path], invalid.Detail);
                    replacements.Add(new MergeTreeStage("100644", await repository.WriteBlobAsync(content.Text), 0, path));
                    break;
                case ConflictResolution.Refused refused:
                    return Evict(refused.Reason, [path], refused.Detail);
                default:
                    throw new InvalidOperationException($"The {rule.Strategy} resolver answered no usable resolution for a path.");
            }
            resolved.Add(new ResolvedPath(path, rule.Strategy, rule.Regenerate ?? resolver.RegeneratesByDefault, IndexOf(rules, rule)));
        }

        var tree = await ReplaceAsync(gitDirectory, output.Tree, replacements, cancellationToken);
        return new MergeOutcome.Resolved(baseCommit, headCommit, tree, resolved.Select(r => r.Path).ToList()) { Resolutions = resolved };
    }

    /// <summary>
    /// §10.4 step 4: content a resolver made, judged before it is written. The rule's validators run <b>inputs first</b>:
    /// each judges the result only if every side passes it, and a side that already fails refuses (<c>invalid-side</c>),
    /// since the strategy cannot be blamed for it. <c>no-conflict-markers</c> always judges the result. A failing result
    /// is <c>invalid-result</c>, naming the validator and the position, never the content.
    /// </summary>
    private async Task<ConflictResolution.Refused?> ValidateContentAsync(ConflictInput input, string result, long maxSideBytes)
    {
        var validators = input.Rule.Validate.Where(v => v != MergeQueueWireRules.NoConflictMarkers).Distinct(StringComparer.Ordinal).Select(Registry.Validator).ToList();
        if (validators.Count > 0)
        {
            var sides = await ResolutionLimits.ReadSidesAsync(input, maxSideBytes);
            if (sides.Problem is { } problem)
                return new ConflictResolution.Refused(EvictionReason.InvalidSide, problem);
            foreach (var validator in validators)
            {
                foreach (var (stage, text) in sides.Texts)
                {
                    var side = validator.Validate(text);
                    if (!side.Valid)
                        return new ConflictResolution.Refused(EvictionReason.InvalidSide,
                            $"{ResolutionLimits.StageName(stage)} already fails {validator.Name} at {side.Position}: {side.Problem}.");
                }
            }
        }
        foreach (var validator in validators.Prepend(Registry.Validator(MergeQueueWireRules.NoConflictMarkers)))
        {
            var verdict = validator.Validate(result);
            if (!verdict.Valid)
                return new ConflictResolution.Refused(EvictionReason.InvalidResult,
                    $"The {input.Rule.Strategy} result fails {validator.Name} at {verdict.Position}: {verdict.Problem}.");
        }
        return null;
    }

    /// <summary>
    /// Clean-merge validation (§10.4): the merged file of a path git merged without a conflict, judged by each of its
    /// rule's validators that both sides (the base branch's and the pull request's version) pass. A merged file over the
    /// validators' cap is <c>too-large</c>; a side that is too large or not UTF-8 is not judged (it fails every validator,
    /// so the inputs-first rule skips it).
    /// </summary>
    private async Task<ConflictResolution.Refused?> ValidateCleanMergeAsync(ConflictRepository repository, AutoResolveRule rule, string path,
        string baseCommit, string headCommit, string tree)
    {
        async Task<string?> Text(string name)
            => await repository.SizeAsync(name) > ResolutionLimits.MaxParsedBytes ? null
                : await repository.ReadBytesAsync(name, ResolutionLimits.MaxParsedBytes) is { } bytes ? ConflictRepository.DecodeStrict(bytes) : null;

        if (await repository.SizeAsync($"{tree}:{path}") > ResolutionLimits.MaxParsedBytes)
            return new ConflictResolution.Refused(EvictionReason.TooLarge, $"The merged file is over {ResolutionLimits.MaxParsedBytes} bytes, the cap of its validators.");
        var ours = await Text($"{baseCommit}:{path}");
        var theirs = await Text($"{headCommit}:{path}");
        if (ours is null || theirs is null)
            return null;
        var merged = await Text($"{tree}:{path}");
        foreach (var validator in rule.Validate.Distinct(StringComparer.Ordinal).Select(Registry.Validator))
        {
            if (!validator.Validate(ours).Valid || !validator.Validate(theirs).Valid)
                continue;
            var verdict = merged is null ? ContentVerdict.Invalid("not strict UTF-8 text") : validator.Validate(merged);
            if (!verdict.Valid)
                return new ConflictResolution.Refused(EvictionReason.MergedContentInvalid,
                    $"git merged the file without a conflict, but the result fails {validator.Name} at {verdict.Position}: {verdict.Problem}.");
        }
        return null;
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

    private static int IndexOf(IReadOnlyList<AutoResolveRule> rules, AutoResolveRule rule)
    {
        for (var i = 0; i < rules.Count; i++)
        {
            if (ReferenceEquals(rules[i], rule))
                return i;
        }
        return -1;
    }

    private static string Oid(GitResult result, string verb)
    {
        var oid = result.StandardOutput.Trim();
        return ObjectId().IsMatch(oid) ? oid : throw new GitCommandException(verb, "did not print an object id.");
    }
}
