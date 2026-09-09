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
/// <para>
/// <b>Bounded per message, and continued by re-queuing.</b> The sweep shares
/// <see cref="Feedback.CoverageQueues.Publishing"/> with the PR comments, because the licence caps
/// subscriptions and it cannot have a queue of its own. Queue-mates are processed in order, so a
/// repository with tens of thousands of documents would hold up every comment behind it. Each
/// message therefore deletes at most <see cref="MaxDeletesPerMessage"/> documents and, if more
/// remain, re-broadcasts itself and returns — the continuation goes to the back of the queue, so
/// feedback waiting behind it gets through between chunks.
/// </para>
/// <para>
/// No cursor is needed, and no <c>ICheckpointRecipient</c>: deleting is idempotent and a deleted
/// document is gone from the prefix stream, so the next chunk resumes simply by streaming the same
/// prefix again. For the same reason a retry after a mid-sweep failure is harmless. What the order
/// does matter for is the <see cref="Repository"/> document itself — it is the marker this handler
/// gates on, so it is deleted last, once nothing beneath it is left. Deleting it early would make
/// the continuation find it missing, stop, and strand every remaining document as an orphan.
/// </para>
/// </summary>
public partial class DeleteRepositoryDataRecipient : IRecipient<DeleteRepositoryDataMessage>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IMessageBus messageBus;
    [Inject] private readonly ILogger<DeleteRepositoryDataRecipient> logger;

    /// <summary>
    /// How many documents one message may delete before handing the queue back. Large enough that
    /// an ordinary repository finishes in a single message, small enough that a very large one
    /// cannot monopolise the shared publishing queue.
    /// </summary>
    private const int MaxDeletesPerMessage = 2_000;

    /// <summary>Documents per transaction, so one sweep is never a single enormous batch.</summary>
    private const int BatchSize = 512;

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

        var fullName = repository.FullName;
        var budget = MaxDeletesPerMessage;

        // Commits and everything whose id descends from one, then pull-request feedback.
        budget -= await SweepPrefixAsync($"Commits/{message.RepositoryGitHubId}/", budget, cancellationToken);
        if (budget > 0)
            budget -= await SweepPrefixAsync($"PullRequestFeedbacks/{message.RepositoryGitHubId}/", budget, cancellationToken);

        if (budget <= 0)
        {
            // More to do than this message is allowed to take. Hand the queue back; the
            // continuation re-streams the same prefixes, which now start where this one stopped.
            await messageBus.BroadcastAsync(message, cancellationToken);
            logger.LogInformation(
                "Deleted {Count} documents of {FullName}; re-queued to continue",
                MaxDeletesPerMessage, fullName);
            return;
        }

        // Repository-scoped tokens: no id relationship, so this one is a query.
        //
        // ⚠️ Shrink, do not delete. A token may be scoped to several repositories, so deleting one
        // repository must not revoke a credential that is still serving the others — that would
        // break CI on repositories nobody touched. Only a token left with nothing to upload for is
        // removed; an emptied list would otherwise silently WIDEN it to account scope, which is the
        // opposite of what deleting its last repository should mean.
        // Same id as above, reused rather than recomputed.
        var tokens = await session.Query<ApiToken>()
            .Where(t => t.GithubRepositories.Contains(repositoryId))
            .Take(1024)
            .ToListAsync(cancellationToken);
        foreach (var token in tokens)
        {
            token.GithubRepositories = [.. token.GithubRepositories.Where(id => id != repositoryId)];
            if (token.GithubRepositories.Count == 0)
                session.Delete(token);
        }

        // Last, and only now that nothing beneath it remains: this document is what the guards
        // above gate on, so while it exists the sweep can always be resumed.
        session.Delete(repository);
        await session.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Deleted {FullName}, requested by {UserId}", fullName, message.RequestedByUserId);
    }

    /// <summary>
    /// Deletes up to <paramref name="budget"/> documents under <paramref name="prefix"/>, in
    /// transactions of <see cref="BatchSize"/>, and returns how many it deleted.
    /// </summary>
    /// <remarks>
    /// Ids are collected from the stream first and deleted after it closes, rather than deleting
    /// as it is read: the stream is a live server-side operation, and saving underneath it mixes a
    /// read that is still in flight with the writes that invalidate it. The collection is bounded
    /// by the budget, so holding the ids costs nothing worth avoiding.
    /// </remarks>
    private async Task<int> SweepPrefixAsync(string prefix, int budget, CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        await using (var stream = await session.Advanced.StreamAsync<object>(startsWith: prefix, token: cancellationToken))
        {
            while (ids.Count < budget && await stream.MoveNextAsync())
                ids.Add(stream.Current.Id);
        }

        for (var i = 0; i < ids.Count; i += BatchSize)
        {
            foreach (var id in ids.Skip(i).Take(BatchSize))
                session.Delete(id);

            await session.SaveChangesAsync(cancellationToken);
        }

        return ids.Count;
    }
}
