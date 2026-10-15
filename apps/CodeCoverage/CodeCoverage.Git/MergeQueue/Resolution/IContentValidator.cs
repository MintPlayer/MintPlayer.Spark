namespace CodeCoverage.Git.MergeQueue.Resolution;

/// <summary>
/// A check that a file's content is valid for its type (docs/coverage_merge_queue_PRD.md §10.3, §10.4 step 4, O33): pure,
/// deterministic, and never quoting the content. <c>coverage.yml</c>'s <c>autoResolve[].validate</c> names validators by
/// <see cref="Name"/>; <c>no-conflict-markers</c> runs on every resolved content whatever the rule says.
/// Registered with <c>[Register(typeof(IContentValidator), ServiceLifetime.Singleton)]</c> and found by name through
/// <see cref="ConflictResolverRegistry"/>.
/// </summary>
public interface IContentValidator
{
    /// <summary>One of <see cref="MergeQueueWireRules.Validators"/>.</summary>
    string Name { get; }

    /// <summary>The verdict on <paramref name="text"/>, decoded from strict UTF-8 by the caller.</summary>
    ContentVerdict Validate(string text);
}

/// <summary>A validator's verdict. <see cref="Problem"/> and the position say what is wrong and where; they never quote
/// the content, because the eviction message is posted on the pull request.</summary>
/// <param name="Line">1-based line of the problem, when known.</param>
/// <param name="Column">1-based column (in UTF-8 bytes for JSON, characters otherwise), when known.</param>
public sealed record ContentVerdict(bool Valid, string? Problem = null, long? Line = null, long? Column = null)
{
    public static ContentVerdict Ok { get; } = new(true);

    public static ContentVerdict Invalid(string problem, long? line = null, long? column = null) => new(false, problem, line, column);

    /// <summary>"line 3, column 7" (or less when unknown), for an eviction message.</summary>
    public string Position => (Line, Column) switch
    {
        ({ } l, { } c) => $"line {l}, column {c}",
        ({ } l, null) => $"line {l}",
        _ => "an unknown position",
    };
}
