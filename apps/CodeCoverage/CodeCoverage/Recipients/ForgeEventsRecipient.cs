using CodeCoverage.Entities;
using CodeCoverage.Forge;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Recipients;

/// <summary>
/// What the app does when a forge reports something — written once, for every forge (D21).
/// </summary>
/// <remarks>
/// <para>
/// <b>One class, several <c>IRecipient</c> interfaces.</b> The GitHub recipient this takes work from
/// warns against "splitting one cohesive handler into five classes", and that caution still holds —
/// these handlers share the commit-upsert helper and are read together. Implementing the interface
/// once per event keeps them in one place, which the generator supports: it emits one
/// <c>AddScoped</c> per closed <c>IRecipient&lt;T&gt;</c> rather than one per class.
/// </para>
/// <para>
/// ⚠️ Declare every interface on a single <c>partial</c> part. The generator fires per class
/// declaration with a base list, so interfaces split across two parts would register twice and run
/// each handler twice per message.
/// </para>
/// <para>
/// <b>Nothing here names a forge.</b> That is the property the neutral envelope exists for: adding
/// GitLab means writing a normaliser that publishes these same events, not editing this file.
/// </para>
/// </remarks>
public partial class ForgeEventsRecipient :
    IRecipient<ForgeWebhookMessage<BranchCommitPushed>>,
    IRecipient<ForgeWebhookMessage<PullRequestUpdated>>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IMessageBus messageBus;
    [Inject] private readonly ILogger<ForgeEventsRecipient> logger;

    public async Task HandleAsync(ForgeWebhookMessage<BranchCommitPushed> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        var (commit, _) = await GetOrCreateCommitAsync(evt.RepositoryId, evt.Sha, cancellationToken);
        if (commit is null) return;

        commit.Branch = evt.Branch;
        commit.Message = evt.Message;
        if (evt.AuthoredAt is { } authoredAt) commit.AuthoredAt = authoredAt;

        // Deliberately does NOT write ParentSha — the event does not carry one, and the reason is
        // recorded on BranchCommitPushed: a push reports the previous ref tip, which is not this
        // commit's parent in three of the six push shapes. The pull-request handler below is the
        // one writer of that field, and means something precise by it.
    }

    public async Task HandleAsync(ForgeWebhookMessage<PullRequestUpdated> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        var (commit, repository) = await GetOrCreateCommitAsync(evt.RepositoryId, evt.HeadSha, cancellationToken);
        if (commit is null || repository is null) return;

        commit.Branch = evt.HeadRef;
        commit.PullRequestNumber = evt.Number;
        commit.Message ??= evt.Title;

        // Plain assignment rather than ??=: a forge re-reports an updated base when the base branch
        // advances or the request is retargeted, so a frozen first-seen value goes quietly stale. A
        // moved head is a new document, so this never corrupts an earlier commit.
        commit.PullRequestBaseRef = evt.BaseRef;
        commit.PullRequestBaseSha = evt.BaseSha;

        // The sole writer of ParentSha, and the only one that ever meant anything: the request's
        // base tip. Still only a hint for finding the pull request — patch coverage resolves its own
        // merge base at compute time.
        commit.ParentSha = evt.BaseSha;

        // Only on a first open. A later push is already served by the finalize path, which edits the
        // same comment with real numbers, and a pull request no human opened gets no "waiting for
        // coverage" comment because nothing will ever arrive to replace it.
        if (evt.IsFirstOpen && !evt.AuthorIsBot)
        {
            await messageBus.BroadcastAsync(new Feedback.OpenPullRequestCommentMessage
            {
                RepositoryGitHubId = repository.GitHubId,
                PullRequestNumber = evt.Number,
                HeadSha = evt.HeadSha,
                AuthorIsBot = evt.AuthorIsBot,
            }, cancellationToken);
        }
    }

    /// <summary>
    /// Loads or creates the commit document for a repository and sha.
    /// </summary>
    /// <returns>Null when the repository is unknown, which is not an error — a forge can report an
    /// event for something we have never been connected to, and inventing a repository from a
    /// webhook is how a server ends up tracking things nobody asked it to.</returns>
    private async Task<(Commit? Commit, Repository? Repository)> GetOrCreateCommitAsync(
        string repositoryId, string sha, CancellationToken cancellationToken)
    {
        var repository = await session.LoadAsync<Repository>(repositoryId, cancellationToken);
        if (repository is null)
        {
            logger.LogDebug("Forge event for unknown repository {RepositoryId}; ignored", repositoryId);
            return (null, null);
        }

        // ⚠️ Via the loaded document rather than by parsing the id. Deriving the numeric id from the
        // string would be the id-parsing pattern that M6 is about to invalidate — a forge segment
        // shifts every positional index, and a parser that guesses wrong here would attach commits
        // to the wrong repository. One load is cheap and stays correct across the re-key.
        var id = Commit.DocumentId(repository.GitHubId, sha);
        var commit = await session.LoadAsync<Commit>(id, cancellationToken);
        if (commit is null)
        {
            commit = new Commit
            {
                Sha = sha,
                Repository = repository.Id,
                FirstSeenAtUtc = DateTimeOffset.UtcNow,
            };
            await session.StoreAsync(commit, id, cancellationToken);
        }

        return (commit, repository);
    }
}
