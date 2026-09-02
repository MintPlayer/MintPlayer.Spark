using CodeCoverage.Entities;
using CodeCoverage.Indexes;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Services;

[Register(typeof(IBaseResolver), ServiceLifetime.Scoped)]
public partial class BaseResolver : IBaseResolver
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IGitHubDiffService diffService;

    // Generous enough to step over a run of cancelled/uncovered default-branch
    // commits, small enough that a repo with no usable base at all answers fast.
    private const int WalkLimit = 50;

    public async Task<ResolvedBase> ResolveAsync(Repository repository, Commit head, string? declaredBaseSha, CancellationToken cancellationToken)
    {
        var requested = string.IsNullOrWhiteSpace(declaredBaseSha) ? null : declaredBaseSha;

        if (requested is not null && !string.Equals(requested, head.Sha, StringComparison.OrdinalIgnoreCase))
        {
            var declared = await session.LoadAsync<Commit>(Entities.Commit.DocumentId(repository.GitHubId, requested), cancellationToken);
            if (await UsableBuildIdAsync(declared, cancellationToken) is { } declaredBuildId)
                return new ResolvedBase(requested, declared!.Sha, ResolvedBase.Exact, declaredBuildId, declared.Coverage, declared.Branch);
        }

        // The PR merge-base with the default branch, from GitHub's compare API.
        // Null whenever no API path exists (private repo without the App) or
        // the call fails — the walk below is the answer to both.
        if (repository.DefaultBranch is not null)
        {
            long? installationId = null;
            if (repository.Account is not null)
                installationId = (await session.LoadAsync<Account>(repository.Account, cancellationToken))?.InstallationId;

            var comparison = await diffService.CompareAsync(repository, installationId, repository.DefaultBranch, head.Sha, cancellationToken);
            var mergeBaseSha = comparison?.MergeBaseSha;
            if (mergeBaseSha is not null
                && !string.Equals(mergeBaseSha, head.Sha, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(mergeBaseSha, requested, StringComparison.OrdinalIgnoreCase))
            {
                var mergeBase = await session.LoadAsync<Commit>(Entities.Commit.DocumentId(repository.GitHubId, mergeBaseSha), cancellationToken);
                if (await UsableBuildIdAsync(mergeBase, cancellationToken) is { } mergeBuildId)
                    return new ResolvedBase(requested, mergeBase!.Sha, ResolvedBase.MergeBase, mergeBuildId, mergeBase.Coverage, mergeBase.Branch);
            }
        }

        // Same branch fallback as ResolveBaseline: OIDC-provisioned repos never
        // learn their default branch, so the head's own branch beats nothing.
        var branch = repository.DefaultBranch ?? head.Branch;
        var query = session.Query<Commits_ByRepository.Result, Commits_ByRepository>()
            .Where(r => r.Repository == repository.Id && r.HasCoverage);
        if (branch is not null)
            query = query.Where(r => r.Branch == branch);

        var candidates = await query
            .OrderByDescending(r => r.AuthoredAt)
            .OfType<Commit>()
            .Take(WalkLimit)
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            if (string.Equals(candidate.Sha, head.Sha, StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(candidate.Sha, requested, StringComparison.OrdinalIgnoreCase))
                continue; // already probed above, and it wasn't usable

            if (await UsableBuildIdAsync(candidate, cancellationToken) is { } buildId)
                return new ResolvedBase(requested, candidate.Sha, ResolvedBase.Walked, buildId, candidate.Coverage, candidate.Branch);
        }

        return new ResolvedBase(requested, null, ResolvedBase.None, null, null);
    }

    /// <summary>
    /// The build id whose tree summary is actually on disk, or null. Existence
    /// is checked, not inferred from <c>Coverage != null</c> — merged PRs get
    /// their build data deleted while the commit keeps its summary for display.
    /// </summary>
    private async Task<string?> UsableBuildIdAsync(Commit? commit, CancellationToken cancellationToken)
    {
        if (commit?.Coverage is null || commit.LatestBuildId is null || commit.Id is null)
            return null;

        // An assembled commit is usable by definition (the assembly is the
        // preferred base); a bare finalized build remains acceptable for commits
        // that predate assemblies.
        if (await session.Advanced.ExistsAsync(CommitAssembly.DocumentId(commit.Id), cancellationToken))
            return commit.LatestBuildId;

        return await session.Advanced.ExistsAsync(BuildTreeSummary.DocumentId(commit.LatestBuildId), cancellationToken)
            ? commit.LatestBuildId
            : null;
    }
}
