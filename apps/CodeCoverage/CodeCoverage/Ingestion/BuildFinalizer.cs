using CodeCoverage.Entities;
using CodeCoverage.Services;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Ingestion;

/// <summary>
/// Closing a Build promotes its merged coverage onto the Commit (which lists,
/// badges and deltas read) and materializes the per-file tree summary the
/// browse endpoints serve. Caller owns SaveChanges.
/// </summary>
public static class BuildFinalizer
{
    public static async Task Finalize(IAsyncDocumentSession session, IGitHubDiffService diffService, Build build, string reason, CancellationToken cancellationToken)
    {
        if (build.Status == "Finalized")
            return;

        build.Status = "Finalized";
        build.FinalizedAtUtc = DateTime.UtcNow;
        build.FinalizeReason = reason;

        if (build.Id is not null)
        {
            await MaterializeTreeSummary(session, build.Id, cancellationToken);
            build.FlagCoverage = await MaterializeFlagSummaries(session, build.Id, cancellationToken);
        }

        if (build.Commit is not null)
        {
            var commit = await session.LoadAsync<Commit>(build.Commit, cancellationToken);
            if (commit is not null)
            {
                // Null whenever there is no diff base or no API path — a patch
                // verdict is earned, never guessed, and never blocks a finalize.
                build.Patch = await PatchCoverageCalculator.ComputeAsync(session, diffService, build, commit, cancellationToken);

                commit.Coverage = build.Coverage;
                commit.LatestBuildId = build.Id;

                if (commit.Repository is not null)
                {
                    var repository = await session.LoadAsync<Repository>(commit.Repository, cancellationToken);
                    // Repo-level coverage tracks the default branch; a repo that
                    // never had data accepts any branch rather than showing nothing.
                    //
                    // Never a partial build (issue #11 D4): its total is a
                    // subset's, and OIDC-provisioned repos have no DefaultBranch,
                    // so without the Partial guard a PR-branch nx-affected run
                    // would overwrite the headline the badge serves.
                    if (repository is not null
                        && !build.Partial
                        && (repository.LatestCoverage is null
                            || repository.DefaultBranch is null
                            || string.Equals(commit.Branch, repository.DefaultBranch, StringComparison.Ordinal)))
                    {
                        repository.LatestCoverage = build.Coverage;
                        repository.LatestCoverageSha = commit.Sha;
                        repository.LatestCoverageAtUtc = DateTime.UtcNow;
                    }
                }
            }
        }
    }

    private static async Task MaterializeTreeSummary(IAsyncDocumentSession session, string buildId, CancellationToken cancellationToken)
    {
        var summary = new BuildTreeSummary { BuildId = buildId };
        await using (var stream = await session.Advanced.StreamAsync<FileCoverage>(
            startsWith: $"{buildId}/files/", token: cancellationToken))
        {
            while (await stream.MoveNextAsync())
            {
                var file = stream.Current.Document;
                summary.Files.Add(new TreeFileSummary
                {
                    Path = file.Path,
                    Matched = file.Matched,
                    LinesCovered = file.Lines.Count(l => l.Status != LineStatus.NotCovered),
                    LinesCoverable = file.Lines.Count,
                });
            }
        }

        await session.StoreAsync(summary, BuildTreeSummary.DocumentId(buildId), cancellationToken);
    }

    /// <summary>
    /// Streams every per-flag file document ({buildId}/flags/{flag}/files/…)
    /// into per-flag tree summaries and totals. One stream covers all flags —
    /// the flag is read back out of the id. Ids without "/files/" under the
    /// prefix are the per-flag trees a previous finalize wrote; skipped, then
    /// overwritten. Null (not empty) when no session carried a flag, so old
    /// unflagged builds and flagless repos look the same.
    /// </summary>
    private static async Task<Dictionary<string, CoverageSummary>?> MaterializeFlagSummaries(
        IAsyncDocumentSession session, string buildId, CancellationToken cancellationToken)
    {
        var prefix = $"{buildId}/flags/";
        var trees = new Dictionary<string, BuildTreeSummary>(StringComparer.Ordinal);

        await using (var stream = await session.Advanced.StreamAsync<FileCoverage>(
            startsWith: prefix, token: cancellationToken))
        {
            while (await stream.MoveNextAsync())
            {
                if (stream.Current.Id is not { } id)
                    continue;
                var rest = id.AsSpan(prefix.Length);
                var slash = rest.IndexOf('/');
                if (slash < 0 || !rest[slash..].StartsWith("/files/"))
                    continue;
                var flag = rest[..slash].ToString();

                if (!trees.TryGetValue(flag, out var tree))
                    trees[flag] = tree = new BuildTreeSummary { BuildId = buildId };

                var file = stream.Current.Document;
                tree.Files.Add(new TreeFileSummary
                {
                    Path = file.Path,
                    Matched = file.Matched,
                    LinesCovered = file.Lines.Count(l => l.Status != LineStatus.NotCovered),
                    LinesCoverable = file.Lines.Count,
                });
            }
        }

        if (trees.Count == 0)
            return null;

        var totals = new Dictionary<string, CoverageSummary>(StringComparer.Ordinal);
        foreach (var (flag, tree) in trees)
        {
            await session.StoreAsync(tree, BuildTreeSummary.FlagDocumentId(buildId, flag), cancellationToken);
            totals[flag] = new CoverageSummary
            {
                LinesCovered = tree.Files.Sum(f => f.LinesCovered),
                LinesCoverable = tree.Files.Sum(f => f.LinesCoverable),
                FilesCount = tree.Files.Count,
            };
        }
        return totals;
    }
}
