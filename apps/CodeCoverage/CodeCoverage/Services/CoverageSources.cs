using CodeCoverage.Entities;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Services;

/// <summary>
/// Where a commit's per-file coverage is read from, for every reader: the browse API and the MCP tools (plan M6).
/// One definition, so the website and an agent never disagree about which numbers a commit has.
/// </summary>
/// <remarks>
/// Static over the caller's session rather than a service: both readers already hold the request's session, and the
/// controller tests that build <c>BrowseController</c> by hand need nothing new registered.
/// </remarks>
public static class CoverageSources
{
    /// <summary>
    /// The coverage source of <paramref name="commit"/>: its assembly when one exists (its ids follow the same
    /// <c>{source}/tree</c> and <c>{source}/files/{hash}</c> scheme as a build's), else the latest finalized build for
    /// commits that predate assemblies.
    /// </summary>
    public static async Task<string> ForCommitAsync(IAsyncDocumentSession session, Commit commit, CancellationToken cancellationToken)
    {
        var assemblyId = CommitAssembly.DocumentId(commit.Id!);
        return await session.Advanced.ExistsAsync(assemblyId, cancellationToken) ? assemblyId : commit.LatestBuildId!;
    }

    /// <summary>
    /// The per-file totals of a coverage source: one point-load of the summary materialized at finalize. Builds
    /// finalized before the summary existed fall back to streaming their <see cref="FileCoverage"/> documents and
    /// deriving it on the fly.
    /// </summary>
    public static async Task<List<TreeFileSummary>> LoadTreeAsync(IAsyncDocumentSession session, string sourceId, CancellationToken cancellationToken)
    {
        var summary = await session.LoadAsync<BuildTreeSummary>(BuildTreeSummary.DocumentId(sourceId), cancellationToken);
        if (summary is not null)
            return summary.Files;

        var files = new List<TreeFileSummary>();
        await using var stream = await session.Advanced.StreamAsync<FileCoverage>(
            startsWith: $"{sourceId}/files/", token: cancellationToken);
        while (await stream.MoveNextAsync())
        {
            var file = stream.Current.Document;
            files.Add(new TreeFileSummary
            {
                Path = file.Path,
                Matched = file.Matched,
                LinesCovered = file.Lines.Count(l => l.Status != LineStatus.NotCovered),
                LinesCoverable = file.Lines.Count,
            });
        }
        return files;
    }
}
