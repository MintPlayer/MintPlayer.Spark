using CodeCoverage.Entities;
using CodeCoverage.Indexes;
using MintPlayer.Spark;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Ingestion;

/// <summary>
/// Deletes a merged PR's build data: every document under
/// <c>{commitId}/builds</c> — Builds (their report attachments die with them),
/// FileCoverage, per-flag documents, tree summaries. The Commit itself
/// survives with its display summary; <see cref="Commit.LatestBuildId"/> is
/// cleared so nothing dangles, and the base resolver's tree-liveness check
/// makes any lingering reference degrade to a walk, not a wrong answer.
/// Default-branch commits are never touched — a PR's merge commit is the
/// repository's history, not the PR's scratch space.
/// </summary>
public partial class DeletePullRequestBuildsRecipient : IRecipient<DeletePullRequestBuildsMessage>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ILogger<DeletePullRequestBuildsRecipient> logger;

    public async Task HandleAsync(DeletePullRequestBuildsMessage message, CancellationToken cancellationToken = default)
    {
        using var requestScope = session.IgnoreMaxRequests(logger: logger);

        var repository = await session.LoadAsync<Repository>(Repository.DocumentId(message.RepositoryGitHubId), cancellationToken);
        var repositoryId = repository?.Id ?? Repository.DocumentId(message.RepositoryGitHubId);

        var commits = await session.Query<Commits_ByRepository.Result, Commits_ByRepository>()
            .Where(r => r.Repository == repositoryId && r.PullRequestNumber == message.PullRequestNumber)
            .OfType<Commit>()
            .Take(256)
            .ToListAsync(cancellationToken);

        var deleted = 0;
        foreach (var commit in commits)
        {
            if (commit.Id is null)
                continue;
            if (repository?.DefaultBranch is not null
                && string.Equals(commit.Branch, repository.DefaultBranch, StringComparison.Ordinal))
                continue;

            await using (var stream = await session.Advanced.StreamAsync<object>(
                startsWith: $"{commit.Id}/builds", token: cancellationToken))
            {
                while (await stream.MoveNextAsync())
                {
                    session.Delete(stream.Current.Id);
                    deleted++;
                }
            }
            commit.LatestBuildId = null;
        }

        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation("PR #{Number} on {Repo} merged: deleted {Deleted} build document(s) across {Commits} commit(s)",
            message.PullRequestNumber, repositoryId, deleted, commits.Count);
    }
}
