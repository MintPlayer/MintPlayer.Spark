using CodeCoverage.Entities;
using CodeCoverage.Services;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Ingestion;

/// <summary>
/// Computes <see cref="PatchCoverage"/> for a finalizing build: GitHub's
/// three-dot diff names the added lines, the build's own FileCoverage answers
/// whether they ran. Everything degrades to null rather than failing a
/// finalize — no diff base, no API access, no measurable overlap all mean
/// "no patch verdict", never an error.
/// </summary>
public static class PatchCoverageCalculator
{
    public static async Task<PatchCoverage?> ComputeAsync(
        IAsyncDocumentSession session, IGitHubDiffService diffService, Build build, Commit commit, CancellationToken cancellationToken)
    {
        if (build.Id is null || commit.Repository is null)
            return null;

        // Declared base first (what the uploader's affected-computation used);
        // the PR base tip webhook hint second. Neither present — typically a
        // default-branch push — means there is nothing to diff against.
        var baseRef = build.DeclaredBaseSha ?? commit.ParentSha;
        if (baseRef is null || string.Equals(baseRef, commit.Sha, StringComparison.OrdinalIgnoreCase))
            return null;

        var repository = await session.LoadAsync<Repository>(commit.Repository, cancellationToken);
        if (repository is null)
            return null;

        long? installationId = null;
        if (repository.Account is not null)
            installationId = (await session.LoadAsync<Account>(repository.Account, cancellationToken))?.InstallationId;

        var comparison = await diffService.CompareAsync(repository, installationId, baseRef, commit.Sha, cancellationToken);
        if (comparison is null)
            return null;

        var candidates = comparison.Files
            .Where(f => f.Status != "removed" && f.AddedLines.Length > 0)
            .ToDictionary(f => FileCoverage.DocumentId(build.Id, f.Path), f => f);

        var measured = await session.LoadAsync<FileCoverage>(candidates.Keys, cancellationToken);

        int covered = 0, coverable = 0, matched = 0;
        foreach (var (id, diffFile) in candidates)
        {
            if (!measured.TryGetValue(id, out var file) || file is null)
                continue; // in the diff, not in the report — skipped, not zeroed

            matched++;
            var byNumber = file.Lines
                .GroupBy(l => l.Number)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var lineNumber in diffFile.AddedLines)
            {
                // Lines the report never mentions are non-executable (blank,
                // comment, type-only) and belong in neither count.
                if (!byNumber.TryGetValue(lineNumber, out var line))
                    continue;

                coverable++;
                if (line.Status != LineStatus.NotCovered)
                    covered++; // partials count as hits — the Codecov formula
            }
        }

        return new PatchCoverage
        {
            DiffBaseRef = baseRef,
            MergeBaseSha = comparison.MergeBaseSha,
            LinesCovered = covered,
            LinesCoverable = coverable,
            FilesInDiff = comparison.Files.Count,
            FilesMatched = matched,
            DiffTruncated = comparison.Truncated,
        };
    }
}
