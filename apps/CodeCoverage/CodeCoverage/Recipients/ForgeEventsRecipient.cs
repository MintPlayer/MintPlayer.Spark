using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.LookupReferences;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
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
    IRecipient<ForgeWebhookMessage<PullRequestUpdated>>,
    IRecipient<ForgeWebhookMessage<PullRequestMerged>>,
    IRecipient<ForgeWebhookMessage<OwnerRenamed>>,
    IRecipient<ForgeWebhookMessage<RepositoryRenamed>>,
    IRecipient<ForgeWebhookMessage<RepositoryConnectionChanged>>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IMessageBus messageBus;
    [Inject] private readonly ILogger<ForgeEventsRecipient> logger;
    [Inject] private readonly IForgeIntegrationResolver forges;

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

    /// <remarks>
    /// Two effects, in a deliberate order. Retention runs first because it is in-process and cheap,
    /// and must not wait behind a forge round-trip that may take seconds or fail. The branch delete
    /// is a courtesy afterwards.
    /// </remarks>
    public async Task HandleAsync(ForgeWebhookMessage<PullRequestMerged> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        var repository = await session.LoadAsync<Repository>(evt.RepositoryId, cancellationToken);
        if (repository is null) return;

        // Retention: a merged pull request surrenders its build data. A closed-unmerged one does
        // not, which is why the event is PullRequestMerged and not PullRequestClosed.
        await messageBus.BroadcastAsync(new Ingestion.DeletePullRequestBuildsMessage
        {
            RepositoryGitHubId = repository.GitHubId,
            PullRequestNumber = evt.Number,
        }, cancellationToken);

        await DeleteHeadBranchIfEnabledAsync(repository, evt, cancellationToken);
    }

    /// <summary>
    /// Deletes a merged pull request's head branch, when the repository opted in.
    /// </summary>
    /// <remarks>
    /// <b>Policy lives here; the ref delete lives in the forge.</b> Resolving the opt-in and
    /// refusing to touch a fork's branch are decisions the app makes identically for every forge —
    /// only the delete itself is forge work, and it went behind
    /// <see cref="IForgeIntegration.DeleteBranchAsync"/>.
    /// <para>
    /// Deliberately not on the project-automation path. A board and a repository are siblings, so
    /// gating this on a board would make it unreachable for an owner with no board, inert for a
    /// board carrying no pull-request rule, and duplicated for an owner with two.
    /// </para>
    /// </remarks>
    private async Task DeleteHeadBranchIfEnabledAsync(Repository repository, PullRequestMerged evt, CancellationToken cancellationToken)
    {
        if (evt.HeadRef is not { Length: > 0 } headRef) return;

        // ⚠️ A fork's head branch lives in a repository we were never given write access to, and
        // that its own owner still wants. The opt-in is on the base repository and cannot speak
        // for it.
        if (!evt.HeadIsFromSameRepository) return;

        // Loaded only when the repository defers to it — a point load on a known id, never a query,
        // and skipped entirely when the repository has decided for itself. Deliberately not a
        // get-or-create: a read path must not mint documents.
        var account = repository.DeleteBranchOnPrClose is EDeleteBranchPolicy.Inherit && repository.Account is { } accountId
            ? await session.LoadAsync<Account>(accountId, cancellationToken)
            : null;

        if (!Repository.ResolveDeleteBranchOnPrClose(repository, account)) return;

        await forges.For(repository).DeleteBranchAsync(repository, headRef, cancellationToken);
    }

    /// <summary>
    /// An owner renamed itself, so every full name beneath it is now wrong.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Not a one-document change.</b> The account keeps its id, but the owner half of every
    /// repository's full name changes with it — and those old names are baked into published badge
    /// URLs, so each one is remembered before it is overwritten.
    /// <para>
    /// The sweep is bounded. An owner with more repositories than the cap leaves the remainder
    /// stale rather than exhausting the session's request budget mid-rename; the nightly reconciler
    /// is what corrects them. A partial rename is recoverable, a failed handler is not.
    /// </para>
    /// </remarks>
    public async Task HandleAsync(ForgeWebhookMessage<OwnerRenamed> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        var account = await session.LoadAsync<Account>(evt.AccountId, cancellationToken);
        if (account is null)
        {
            logger.LogDebug("Rename for unknown account {AccountId}; ignored", evt.AccountId);
            return;
        }

        var previousLogin = account.Login;
        account.Login = evt.NewLogin;
        if (evt.NewAvatarUrl is not null) account.AvatarUrl = evt.NewAvatarUrl;

        // Nothing beneath it moves unless the login actually changed. A forge may report a rename
        // that only touched the avatar, and rewriting every repository for that would be a large
        // write for no change.
        if (string.IsNullOrEmpty(previousLogin) || previousLogin == account.Login) return;

        foreach (var repository in await LoadRepositoriesOfAsync(account, cancellationToken))
        {
            var newFullName = $"{account.Login}/{repository.Name}";
            repository.RememberPreviousFullName(newFullName);
            repository.OwnerLogin = account.Login;
            repository.FullName = newFullName;
        }

        logger.LogInformation("Account {Previous} renamed to {Current}", previousLogin, account.Login);
    }

    /// <summary>
    /// A repository changed its name, or moved to a different owner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stored document keeps its id — a forge's numeric id survives a rename, which is exactly
    /// why ids are keyed on it rather than on the name. What changes is every string a human reads,
    /// and the old ones have to be remembered: badge URLs live in READMEs and report links live in
    /// pull-request comments posted years ago, and a rename must not break them.
    /// </para>
    /// <para>
    /// ⚠️ The previous name comes from the <b>event</b>, not from the document. A forge library that
    /// upserts repository metadata from the same payload has already overwritten the stored one, so
    /// reading it here would append the name the repository already has — an alias that aliases
    /// nothing. Writing the new values again is harmless: they are the same values.
    /// </para>
    /// </remarks>
    public async Task HandleAsync(ForgeWebhookMessage<RepositoryRenamed> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        var repository = await session.LoadAsync<Repository>(evt.RepositoryId, cancellationToken);
        if (repository is null)
        {
            logger.LogDebug("Rename for unknown repository {RepositoryId}; ignored", evt.RepositoryId);
            return;
        }

        // RecordPreviousFullName, not RememberPreviousFullName: the latter infers the old name from
        // the stored FullName, and by the time this runs a forge library may have upserted the new
        // one from the same payload. We do not have to infer — the event carries it.
        if (evt.PreviousFullName != evt.NewFullName)
            repository.RecordPreviousFullName(evt.PreviousFullName);

        repository.Name = evt.NewName;
        repository.FullName = evt.NewFullName;
        repository.OwnerLogin = evt.NewOwnerLogin;

        logger.LogInformation("Repository {Previous} is now {Current}",
            evt.PreviousFullName ?? "(unknown)", evt.NewFullName);
    }

    /// <summary>
    /// We gained or lost the ability to act on a repository.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Never deletes.</b> The coverage history stays, the badge keeps serving its last known
    /// value and the report URLs keep resolving — the repository simply stops being advertised to
    /// anyone but its owner. A forge revoking access is not the owner asking us to forget them, and
    /// conflating the two would destroy data on a permission change.
    /// </para>
    /// <para>
    /// ⚠️ <b>A lost-access event is checked against ownership before it is believed.</b> When a
    /// repository moves between two owners we both see, the loss and the gain are sent at the same
    /// instant and arrive in no guaranteed order. If the repository has already been re-parented,
    /// this loss is the old owner reporting something that is no longer theirs — stale, and acting
    /// on it would disconnect a repository we can plainly still see. Ownership settles that without
    /// needing an order, which is the only way to settle it.
    /// </para>
    /// </remarks>
    public async Task HandleAsync(ForgeWebhookMessage<RepositoryConnectionChanged> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        var repository = await session.LoadAsync<Repository>(evt.RepositoryId, cancellationToken);
        if (repository is null)
        {
            logger.LogDebug("Connection change for unknown repository {RepositoryId}; ignored", evt.RepositoryId);
            return;
        }

        if (evt.Connected)
        {
            repository.MarkConnected();
            logger.LogInformation("{FullName} is reachable again", repository.FullName);
            return;
        }

        if (evt.ReportedByAccountId is { Length: > 0 } reporter
            && repository.Account is not null
            && repository.Account != reporter)
        {
            logger.LogInformation(
                "Ignoring a stale loss of {FullName} reported by {Reporter}: it now belongs to {Owner}",
                repository.FullName, reporter, repository.Account);
            return;
        }

        repository.MarkDisconnected(evt.Reason ?? DisconnectedReasons.RemovedFromInstallation);
        logger.LogInformation("{FullName} is no longer reachable ({Reason})", repository.FullName, evt.Reason);
    }

    /// <summary>Bound on a per-account repository sweep; the session's request budget is 30.</summary>
    private const int MaxRepositoriesPerAccount = 1024;

    private async Task<IReadOnlyList<Repository>> LoadRepositoriesOfAsync(Account account, CancellationToken cancellationToken)
    {
        if (account.Id is null) return [];

        return await session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(r => r.Account == account.Id)
            .Take(MaxRepositoriesPerAccount)
            .ToListAsync(cancellationToken);
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
        var id = Commit.DocumentId(EForgeProvider.GitHub, repository.GitHubId, sha);
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
