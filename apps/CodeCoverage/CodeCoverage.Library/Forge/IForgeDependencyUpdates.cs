using CodeCoverage.Entities;

namespace CodeCoverage.Forge;

/// <summary>
/// What the dependency updater needs from a forge beyond <see cref="IForgeMergeQueue"/> (dependency-updates PRD §13.8 N5–N7,
/// R15, R16): its own pull request, comment reactions, its own check run, a multi-file artifact, and two static reads of
/// another repository (an Action's tag and its <c>action.yml</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>A narrow interface of its own</b>, like <see cref="IForgeMergeQueue"/> beside <see cref="IForgeIntegration"/>: the
/// updater is one feature, and its members would widen the queue's seam for a caller that never uses them. The dispatch,
/// the run, the single-file artifact, the role, the comment and the writer's token stay on <see cref="IForgeMergeQueue"/>,
/// which the workflow hook speaks (N1).
/// </para>
/// <para>
/// <b>Errors</b> as in <see cref="IForgeMergeQueue"/>: a refusal for lack of permission throws
/// <see cref="ForgeAccessDeniedException"/>, anything else unexpected <see cref="ForgeRequestException"/>; an absence is
/// the documented empty value.
/// </para>
/// </remarks>
public interface IForgeDependencyUpdates
{
    EForgeProvider Provider { get; }

    /// <summary>
    /// The named files of the run's artifact <paramref name="artifactName"/> (the apply run's <c>result.json</c> and
    /// <c>dependency.patch</c>). Every file in the archive must be one of <paramref name="fileNames"/>; the archive and
    /// each file are held to <paramref name="maxBytes"/>. A missing name is simply absent from the answer.
    /// </summary>
    Task<ForgeArtifactFiles> DownloadArtifactFilesAsync(Repository repository, long runId, string artifactName, IReadOnlyCollection<string> fileNames,
        long maxBytes, CancellationToken cancellationToken = default);

    /// <summary>The open pull request from <paramref name="headBranch"/> (a branch of the repository itself) into
    /// <paramref name="baseBranch"/>, or null.</summary>
    Task<ForgeBotPullRequest?> FindPullRequestAsync(Repository repository, string headBranch, string baseBranch, CancellationToken cancellationToken = default);

    /// <summary>The pull request, open or not, or null when it does not exist.</summary>
    Task<ForgeBotPullRequest?> GetPullRequestAsync(Repository repository, int number, CancellationToken cancellationToken = default);

    /// <summary>Opens a pull request from a branch of the repository itself; <paramref name="draft"/> for R16's first one.</summary>
    Task<ForgeBotPullRequest> CreatePullRequestAsync(Repository repository, string headBranch, string baseBranch, string title, string body, bool draft,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces the pull request's title and body.</summary>
    Task UpdatePullRequestAsync(Repository repository, int number, string title, string body, CancellationToken cancellationToken = default);

    /// <summary>Puts a reaction on a pull request comment (👍 a command taken, 👎 one refused, §7.5).</summary>
    Task AddCommentReactionAsync(Repository repository, long commentId, EForgeReaction reaction, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates the updater's own check run (<see cref="DependencyCheckName"/>) on <paramref name="sha"/>; it stays
    /// in progress while a bisection runs (old §7.4). Returns its id for the next update.
    /// </summary>
    Task<long> PublishCheckAsync(Repository repository, string sha, ForgeQueueCheck check, long? existingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gap C: the commit tag <paramref name="tag"/> of the public repository <paramref name="ownerRepo"/> points at, an
    /// annotated tag dereferenced to its commit; null when there is no such tag (or a branch of that name only). Read with
    /// the installation's token as a static API call: nothing is cloned or run.
    /// </summary>
    Task<string?> ResolveTagCommitAsync(Repository repository, string ownerRepo, string tag, CancellationToken cancellationToken = default);

    /// <summary>
    /// R15: a text file of another repository at a ref (the target <c>action.yml</c> of an Actions bump, read statically),
    /// or null when absent, not a file, over 1 MiB or not UTF-8.
    /// </summary>
    Task<string?> GetRepositoryFileAsync(Repository repository, string ownerRepo, string gitRef, string path, CancellationToken cancellationToken = default);

    /// <summary>The name of the updater's own check run.</summary>
    public const string DependencyCheckName = "Dependency updates";
}

/// <summary>An artifact's named files, or why there are none.</summary>
public sealed record ForgeArtifactFiles(EForgeArtifactOutcome Outcome, IReadOnlyDictionary<string, byte[]> Files, string? Detail)
{
    public static ForgeArtifactFiles Failed(EForgeArtifactOutcome outcome, string detail) => new(outcome, new Dictionary<string, byte[]>(), detail);
}

/// <summary>A pull request as the updater reads it: enough to upsert its own and to tell whether it may be enqueued.</summary>
public sealed record ForgeBotPullRequest(int Number, string HeadRef, string HeadSha, string BaseRef, bool IsOpen, bool IsMerged, bool IsDraft, string? HtmlUrl);

public enum EForgeReaction
{
    ThumbsUp,
    ThumbsDown,
    Eyes,
}
