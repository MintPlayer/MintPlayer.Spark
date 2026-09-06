using CodeCoverage.Entities;
using MintPlayer.Spark;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Ingestion;

/// <summary>
/// Erases everything belonging to one repository: the Repository document, every Commit and — by
/// id prefix — every Build, FileCoverage, per-flag document, tree summary and commit assembly
/// beneath those commits, plus its pull-request feedback and any token scoped to it.
/// <para>
/// This is the only path in the application that deletes coverage data. Losing access to a
/// repository never does: a transfer, an uninstall or a deletion on GitHub disconnects it, keeping
/// its history and its URLs alive, precisely so that this decision stays with the person who owns
/// the data rather than being a side effect of an event from GitHub.
/// </para>
/// <para>
/// The prefix sweep leans on the id scheme rather than on queries: every document under a
/// repository derives its id from <c>Commits/{repoGitHubId}/{sha}</c>, so one streamed prefix
/// reaches all of them without needing an index per type, and cannot miss a type someone adds
/// later.
/// </para>
/// </summary>
public partial class DeleteRepositoryDataRecipient : IRecipient<DeleteRepositoryDataMessage>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ILogger<DeleteRepositoryDataRecipient> logger;

    public async Task HandleAsync(DeleteRepositoryDataMessage message, CancellationToken cancellationToken = default)
    {
        using var requestScope = session.IgnoreMaxRequests(logger: logger);

        var repositoryId = Repository.DocumentId(message.RepositoryGitHubId);
        var repository = await session.LoadAsync<Repository>(repositoryId, cancellationToken);

        // Re-checked here and not only at the button: the message may have been queued before the
        // repository reconnected, and deleting a live repository's history would be unrecoverable.
        if (repository is null)
        {
            logger.LogInformation("Repository {Id} is already gone; nothing to delete", repositoryId);
            return;
        }

        if (repository.Connection != RepositoryConnection.Disconnected)
        {
            logger.LogWarning(
                "Refusing to delete {FullName}: it reconnected after the delete was requested",
                repository.FullName);
            return;
        }

        var deleted = 0;

        // Commits and everything whose id descends from one.
        await using (var commits = await session.Advanced.StreamAsync<object>(
            startsWith: $"Commits/{message.RepositoryGitHubId}/", token: cancellationToken))
        {
            while (await commits.MoveNextAsync())
            {
                session.Delete(commits.Current.Id);
                deleted++;
            }
        }

        await using (var feedback = await session.Advanced.StreamAsync<object>(
            startsWith: $"PullRequestFeedbacks/{message.RepositoryGitHubId}/", token: cancellationToken))
        {
            while (await feedback.MoveNextAsync())
            {
                session.Delete(feedback.Current.Id);
                deleted++;
            }
        }

        // Repository-scoped tokens: no id relationship, so this one is a query.
        var tokens = await session.Query<ApiToken>()
            .Where(t => t.RepositoryGitHubId == message.RepositoryGitHubId)
            .Take(1024)
            .ToListAsync(cancellationToken);
        foreach (var token in tokens)
        {
            session.Delete(token);
            deleted++;
        }

        var fullName = repository.FullName;
        session.Delete(repository);
        deleted++;

        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Deleted {FullName} and {Count} documents, requested by {UserId}",
            fullName, deleted, message.RequestedByUserId);
    }
}
