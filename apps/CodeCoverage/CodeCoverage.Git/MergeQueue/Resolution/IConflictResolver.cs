namespace CodeCoverage.Git.MergeQueue.Resolution;

/// <summary>
/// One conflict-resolution strategy of the merge queue (docs/coverage_merge_queue_PRD.md §10.3, O34): a pure function of
/// the rule, the path and the conflicted path's stage blobs, which answers a stage to take, new bytes, or a refusal.
/// <c>coverage.yml</c>'s <c>autoResolve</c> binds paths to strategies by <see cref="Strategy"/>; only the git writer runs
/// them. Implementations are registered with <c>[Register(typeof(IConflictResolver), ServiceLifetime.Singleton)]</c> and
/// found by name through <see cref="ConflictResolverRegistry"/>.
/// </summary>
/// <remarks>
/// A resolver never runs repository code and never reads anything but the blobs of its own path: the output must be the
/// same for the prepare, the queue-branch push and the publish, which rebuild the same commit (§0 D65).
/// </remarks>
public interface IConflictResolver
{
    /// <summary>The name <c>coverage.yml</c> and the wire use (<c>take-base</c>, <c>keep-both</c>, …); one of
    /// <see cref="MergeQueueWireRules.Strategies"/>.</summary>
    string Strategy { get; }

    /// <summary>Whether a rule that does not say <c>regenerate</c> needs the regenerate workflow after this strategy
    /// (take-base: always; keep-both: no).</summary>
    bool RegeneratesByDefault { get; }

    /// <summary>The largest stage blob this strategy accepts; a bigger one evicts <see cref="EvictionReason.TooLarge"/>
    /// before <see cref="ResolveAsync"/> runs.</summary>
    long MaxSideBytes { get; }

    /// <summary>Whether the conflicted entries have a shape this strategy handles at all (which stages exist, their
    /// modes). False evicts <see cref="EvictionReason.UnsupportedEntry"/>.</summary>
    bool CanResolve(string path, IReadOnlyDictionary<int, MergeTreeStage> stages, AutoResolveRule rule);

    Task<ConflictResolution> ResolveAsync(ConflictInput input, CancellationToken cancellationToken);
}

/// <summary>What a resolver made of one conflicted path.</summary>
public abstract record ConflictResolution
{
    /// <summary>Take this stage's blob as it is (take-base: stage 2, the base branch).</summary>
    public sealed record UseStage(int Stage) : ConflictResolution;

    /// <summary>New content, as text decoded from strict UTF-8. The merge queue validates it (<c>no-conflict-markers</c>
    /// and the rule's validators) before it writes it with <c>hash-object</c>.</summary>
    public sealed record Content(string Text) : ConflictResolution;

    /// <summary>The pull request leaves the queue. <see cref="Detail"/> reaches the eviction message: it names the
    /// problem and never quotes the file's content.</summary>
    public sealed record Refused(EvictionReason Reason, string Detail) : ConflictResolution;
}

/// <summary>The input of <see cref="IConflictResolver.ResolveAsync"/>.</summary>
/// <param name="Path">The conflicted path.</param>
/// <param name="Rule">The first <c>autoResolve</c> rule that matches it.</param>
/// <param name="Stages">Its conflicted index entries by stage: 1 the merge base (absent for add/add), 2 the base branch,
/// 3 the pull request.</param>
/// <param name="MergedTree">merge-tree's result tree, which holds the path with zdiff3 conflict markers.</param>
/// <param name="Repository">Read access to the blobs, and the few git commands a strategy may run.</param>
public sealed record ConflictInput(string Path, AutoResolveRule Rule, IReadOnlyDictionary<int, MergeTreeStage> Stages, string MergedTree,
    ConflictRepository Repository);
