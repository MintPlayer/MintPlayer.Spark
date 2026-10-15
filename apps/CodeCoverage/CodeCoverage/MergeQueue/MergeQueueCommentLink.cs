using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using Raven.Client.Documents.Session;

namespace CodeCoverage.MergeQueue;

/// <summary>
/// Where the coverage comment points for the merge queue (merge-queue PRD §0 O32, D89): the pull request's queue check on
/// GitHub, whose page shows the queue's state and carries its button.
/// </summary>
/// <remarks>
/// <para>
/// <b>A link, not a state.</b> The comment is written when a pull request opens and when its coverage arrives. The queue
/// moves several times in between (offered, queued, rebasing, checking, merging, evicted), and saying where it stands
/// would mean re-running the comment's publish, check runs included, on each move. So the comment names no state; the
/// check it links shows the live one.
/// </para>
/// <para>
/// <b>The latest check run, recorded by the saga</b> (<see cref="MergeQueueLatestCheck"/>). An older run still leads to a
/// working button, because a click names the pull request, not the run. Without one, the pull request's checks tab.
/// </para>
/// </remarks>
public static class MergeQueueCommentLink
{
    /// <summary>
    /// The link, or null when the default branch's <c>coverage.yml</c> does not turn a working queue on (O16), when
    /// <paramref name="baseRef"/> (when known) is not one of its branches, or off GitHub, whose URL shape this is.
    /// </summary>
    public static async Task<string?> ResolveAsync(
        IAsyncDocumentSession session, IForgeIntegration forge, Repository repository, int number, string? baseRef, CancellationToken cancellationToken)
    {
        if (repository.Provider != EForgeProvider.GitHub || repository.Id is null || repository.DefaultBranch is not { } defaultBranch)
            return null;

        string? yml;
        try
        {
            yml = await forge.GetFileContentAsync(repository, defaultBranch, CoverageYml.FileName, cancellationToken);
        }
        catch (Exception ex) when (ex is ForgeAccessDeniedException or ForgeRequestException or HttpRequestException)
        {
            // The comment goes out without the line rather than not at all.
            return null;
        }
        var parsed = MergeQueueYml.Parse(yml, defaultBranch);
        if (!parsed.IsUsable || parsed.Section is not { } section) return null;
        if (baseRef is not null && !section.Branches.Contains(baseRef, StringComparer.Ordinal)) return null;

        var checks = $"https://github.com/{repository.OwnerLogin}/{repository.Name}/pull/{number}/checks";
        var latest = await session.LoadAsync<MergeQueueLatestCheck>(MergeQueueLatestCheck.DocumentId(repository.Id, number), cancellationToken);
        return latest is null ? checks : $"{checks}?check_run_id={latest.CheckRunId}";
    }
}
