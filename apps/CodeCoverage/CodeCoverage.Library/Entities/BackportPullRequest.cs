namespace CodeCoverage.Entities;

/// <summary>
/// A backport pull request the App opened itself, kept until it merges or closes (#568, backport approval floor). It is
/// the proof that the App wrote the pull request: an author login or a <c>backport/</c> branch name alone could come from
/// any App or person.
/// </summary>
/// <remarks>
/// <para>
/// Loaded by id only (<see cref="DocumentId"/>), so the merge queue's approval floor and the review webhook each make one
/// point load.
/// </para>
/// <para>
/// Two things read it:
/// <list type="bullet">
/// <item>The approval floor. Under <c>ExternalAuthors</c>, a recorded backport counts as a writer's pull request. The
/// cherry-picked change already passed the floor on its own pull request. Only a writer, or an App with write access,
/// can push to a branch in the base repository.</item>
/// <item>A review. When a writer approves a recorded backport that is not in the queue, the App queues it. This is what
/// carries a backport through the <c>Always</c> floor without a person queueing it.</item>
/// </list>
/// </para>
/// </remarks>
public class BackportPullRequest
{
    public string? Id { get; set; }

    public string Repository { get; set; } = string.Empty;

    /// <summary>The backport pull request.</summary>
    public int Number { get; set; }

    /// <summary>The backported pull request.</summary>
    public int Source { get; set; }

    /// <summary>The release line it targets.</summary>
    public string Branch { get; set; } = string.Empty;

    // The backported pull request's merge commit on the default branch (release lines recovery plan, segment B), so the
    // queue can order a line's backports by master order. Null on a record written before segment B.
    public string? SourceSha { get; set; }

    public DateTime OpenedUtc { get; set; }

    /// <summary><c>BackportPullRequests/{provider}/{repositoryId}/{number}</c>.</summary>
    public static string DocumentId(string repositoryId, int number)
        => $"BackportPullRequests/{MergeQueue.RepositorySuffix(repositoryId)}/{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}
