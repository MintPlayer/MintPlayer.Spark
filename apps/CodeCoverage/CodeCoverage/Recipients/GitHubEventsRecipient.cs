using CodeCoverage.Forge;
using System.Text.Json;
using CodeCoverage.Entities;
using CodeCoverage.LookupReferences;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Webhooks.GitHub.Messages;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit.Webhooks.Events;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Recipients;

/// <summary>
/// Keeps Accounts/Repositories/Commits in sync with GitHub via App webhooks.
///
/// Subscribes to the catch-all <see cref="GitHubWebhookMessage"/> (queue
/// "spark-github-all") and dispatches on EventType, deserializing EventJson
/// per event — deliberately NOT the typed GitHubWebhookMessage&lt;TEvent&gt;
/// envelopes: the webhook processor broadcasts BOTH the catch-all and the
/// typed envelope for every event regardless of subscribers, so switching
/// would only swap which family of unconsumed messages accumulates while
/// splitting one cohesive handler into five classes (docs/PLAN.md M8 2.3).
/// </summary>
public partial class GitHubEventsRecipient : IRecipient<GitHubWebhookMessage>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IMessageBus messageBus;
    [Inject] private readonly ILogger<GitHubEventsRecipient> logger;

    /// <summary>Bound on a per-account repository sweep; the session's request budget is 30.</summary>
    private const int MaxRepositoriesPerAccount = 1024;

    public async Task HandleAsync(GitHubWebhookMessage message, CancellationToken cancellationToken = default)
    {
        switch (message.EventType)
        {
            case "installation":
                await OnInstallation(Deserialize<InstallationEvent>(message), cancellationToken);
                break;
            case "installation_repositories":
                await OnInstallationRepositories(Deserialize<InstallationRepositoriesEvent>(message), cancellationToken);
                break;
            case "repository":
                await OnRepository(Deserialize<RepositoryEvent>(message), cancellationToken);
                break;
            // Two events report the same fact, and which one arrives depends on the App's event
            // subscriptions. `installation_target` is the documented one, but it is NOT among the
            // events either Coverage app subscribes to (measured 2026-09-05: member, membership,
            // organization, pull_request, push, repository, team, team_add) — and an App only
            // receives what it subscribes to, so relying on it alone would have been a handler that
            // never ran. `organization` IS subscribed and carries action `renamed`.
            case "installation_target":
            case "organization":
                await OnAccountRenamed(message, cancellationToken);
                break;
            case "push":
                await OnPush(Deserialize<PushEvent>(message), cancellationToken);
                break;
            case "pull_request":
                await OnPullRequest(Deserialize<PullRequestEvent>(message), cancellationToken);
                break;
            default:
                return;
        }

        await session.SaveChangesAsync(cancellationToken);
    }

    private static TEvent Deserialize<TEvent>(GitHubWebhookMessage message)
        => JsonSerializer.Deserialize<TEvent>(message.EventJson)!;

    private async Task OnInstallation(InstallationEvent evt, CancellationToken ct)
    {
        var ghAccount = evt.Installation.Account;
        var account = await GetOrCreateAccount(ghAccount.Id, ct);
        account.Login = ghAccount.Login;
        account.Type = evt.Installation.TargetType.StringValue == "Organization" ? "Organization" : "User";
        account.AvatarUrl = ghAccount.AvatarUrl;

        switch (evt.Action)
        {
            case "created":
            case "unsuspend":
            case "new_permissions_accepted":
                account.InstallationId = evt.Installation.Id;

                // `repositories` is only populated on `created` (and `deleted`). An `unsuspend`
                // carries no list, so this upsert reconnects nothing — and the repositories that
                // `suspend` disconnected would stay hidden until the nightly sweep, which is a day
                // of an account's repositories silently missing after the App is re-enabled.
                // The reconcile below is what actually restores them.
                await UpsertRepositories(
                    (evt.Repositories ?? []).Select(r => (r.Id, r.Name, r.FullName, r.Private)), account, ct);
                await messageBus.BroadcastAsync(new Ingestion.ReconcileAccountMessage
                {
                    AccountGitHubId = ghAccount.Id,
                }, ct);
                break;
            case "deleted":
            case "suspend":
                account.InstallationId = null;
                // The App can no longer see anything this account owns, so nothing it owns should
                // still be advertised. The documents stay; only the advertising stops.
                await DisconnectRepositoriesOfAsync(
                    account,
                    evt.Action == "suspend" ? DisconnectedReasons.AppSuspended : DisconnectedReasons.AppUninstalled,
                    ct);
                break;
        }

        logger.LogInformation("Installation {Action} for {Login} ({InstallationId})",
            evt.Action, ghAccount.Login, evt.Installation.Id);
    }

    private async Task OnInstallationRepositories(InstallationRepositoriesEvent evt, CancellationToken ct)
    {
        var ghAccount = evt.Installation.Account;
        var account = await GetOrCreateAccount(ghAccount.Id, ct);
        account.Login = ghAccount.Login;
        account.AvatarUrl = ghAccount.AvatarUrl;
        account.InstallationId = evt.Installation.Id;

        await UpsertRepositories(
            (evt.RepositoriesAdded ?? []).Select(r => (r.Id, r.Name, r.FullName, r.Private)), account, ct);

        var removedIds = (evt.RepositoriesRemoved ?? [])
            .Select(r => Repository.DocumentId(r.Id))
            .ToArray();
        if (removedIds.Length > 0)
        {
            // Removed from the installation's selection, not removed from existence: this fires
            // when someone deselects a repository, and it is also how a transfer out of the
            // organization reaches us. Deleting here — which is what this did before, in code that
            // had never once run — would destroy every commit, build and report for a repository
            // whose owner may re-add it a minute later.
            var loaded = await session.LoadAsync<Repository>(removedIds, ct);
            foreach (var existing in loaded.Values)
            {
                if (existing is null) continue;

                // Only the account that still owns the repository may disconnect it.
                //
                // When the App is installed on BOTH the source and the destination of a transfer,
                // three events describe one move: `removed` from the old installation, and
                // `transferred` + `added` from the new one. They are sent at the same instant and
                // arrive in no guaranteed order, so an unguarded `removed` that lands after the
                // others would disconnect a repository the App can plainly still see, and leave it
                // that way until the nightly reconciler.
                //
                // Ownership settles that without needing an order: if the repository has already
                // been re-parented, this removal is the old owner reporting a repository that is no
                // longer theirs, and it is stale. If it has not, the removal is current and the
                // repository really has left. Correct whichever way round the two arrive.
                if (existing.Account is not null && existing.Account != account.Id)
                {
                    logger.LogInformation(
                        "Ignoring a stale removal of {FullName} from {Login}: it now belongs to {Owner}",
                        existing.FullName, account.Login, existing.Account);
                    continue;
                }

                Disconnect(existing, DisconnectedReasons.RemovedFromInstallation);
            }
        }

        // The payload announces that the set changed; it cannot be trusted to say how. Narrowing an
        // installation from "all repositories" to a selected few arrives as action `added` with an
        // EMPTY repositories_removed — every repository that silently left is reported nowhere.
        // So apply the payload for the timely case, and ask GitHub for the truth.
        await messageBus.BroadcastAsync(new Ingestion.ReconcileAccountMessage
        {
            AccountGitHubId = ghAccount.Id,
        }, ct);
    }

    private async Task OnRepository(RepositoryEvent evt, CancellationToken ct)
    {
        var ghRepo = evt.Repository;
        if (ghRepo is null) return;

        if (evt.Action == "deleted")
        {
            // Deleted on GitHub, so the numeric id can never come back — but the coverage history
            // is still ours and someone may still be reading a report through a link. It stops
            // being advertised; the owner decides whether the data goes, through the explicit
            // delete action.
            var existing = await session.LoadAsync<Repository>(Repository.DocumentId(ghRepo.Id), ct);
            if (existing is not null)
                Disconnect(existing, DisconnectedReasons.DeletedOnGitHub);
            return;
        }

        // Unconditional, unlike before: an account we already knew never learned that its login
        // had changed, so an organization rename left a stale login on the account and on every
        // repository under it until something happened to recreate the document.
        var account = await GetOrCreateAccount(ghRepo.Owner.Id, ct);
        account.Login = ghRepo.Owner.Login;
        account.AvatarUrl = ghRepo.Owner.AvatarUrl;
        account.Type = ghRepo.Owner.Type.StringValue == "Organization" ? "Organization" : "User";

        // Before the upsert overwrites it: the name we knew this repository by is the one that is
        // baked into published badge URLs, and a rename or transfer is the moment to remember it.
        var previous = await session.LoadAsync<Repository>(Repository.DocumentId(ghRepo.Id), ct);
        if (evt.Action is "renamed" or "transferred")
            previous?.RememberPreviousFullName(ghRepo.FullName);

        var repository = await UpsertRepository(ghRepo.Id, ghRepo.Name, ghRepo.FullName, ghRepo.Private, account, ct);
        repository.DefaultBranch = ghRepo.DefaultBranch;
        repository.Archived = ghRepo.Archived;

        // Deliberately does NOT disconnect on `transferred`, which is the opposite of what the
        // event's name suggests. Measured against the real API on 2026-09-05 by transferring
        // MintPlayer/CodeCoverage out and back:
        //
        //   into an org where the App is installed   → repository.transferred
        //                                            + installation_repositories.added
        //   out of that org                          → installation_repositories.removed ONLY
        //
        // GitHub tells the installation that GAINS a repository; the one losing it hears only
        // that its repository set shrank. So `transferred` arriving means we just acquired this
        // repository, and disconnecting here would mark a repository we can see as unreachable,
        // then rely on the `added` that follows to undo it — a correctness bug resting on the
        // delivery order of two independently queued messages. Losing a repository is
        // OnInstallationRepositories' job, and it is the only path that can observe it.
    }

    /// <summary>
    /// An account renamed itself. It keeps its numeric id, so the document is the same one — but its
    /// login, and the owner half of every full name beneath it, are now wrong.
    /// <para>
    /// Handles both <c>installation_target</c> and <c>organization</c>, because which one an App
    /// receives depends on its event subscriptions and the Coverage apps subscribe only to the
    /// latter.
    /// </para>
    /// <para>
    /// Read out of the raw payload rather than through a typed event: Octokit.Webhooks models
    /// <c>InstallationTargetEvent</c> with only the fields common to every webhook, and the ones
    /// this event exists to carry — the account and <c>changes.login.from</c> — are not among them.
    /// </para>
    /// </summary>
    private async Task OnAccountRenamed(GitHubWebhookMessage message, CancellationToken ct)
    {
        using var payload = JsonDocument.Parse(message.EventJson);
        var root = payload.RootElement;

        // Only the rename matters. `organization` also fires for member_added, member_removed and
        // friends, none of which change an account's identity.
        if (root.TryGetProperty("action", out var action)
            && action.ValueKind == JsonValueKind.String
            && action.GetString() != "renamed")
        {
            return;
        }

        // `installation_target` puts the account under "account"; `organization` puts it under
        // "organization". Same shape, different key.
        if (!root.TryGetProperty("account", out var ghAccount) || ghAccount.ValueKind != JsonValueKind.Object)
        {
            if (!root.TryGetProperty("organization", out ghAccount) || ghAccount.ValueKind != JsonValueKind.Object)
                return;
        }

        if (!ghAccount.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var accountId))
            return;

        var login = ghAccount.TryGetProperty("login", out var loginElement) ? loginElement.GetString() : null;
        if (string.IsNullOrEmpty(login)) return;

        // Created if absent, because a rename for an owner we have never seen still establishes
        // who they are — unlike a merged pull request, where minting a document would invent
        // tracking nobody asked for.
        var account = await GetOrCreateAccount(accountId, ct);

        await messageBus.BroadcastAsync(new ForgeWebhookMessage<OwnerRenamed>(
            EForgeProvider.GitHub,
            new OwnerRenamed(
                AccountId: account.Id ?? Account.DocumentId(accountId),
                NewLogin: login,
                NewAvatarUrl: ghAccount.TryGetProperty("avatar_url", out var avatarElement)
                    ? avatarElement.GetString()
                    : null)), ct);
    }

    private async Task OnPush(PushEvent evt, CancellationToken ct)
    {
        if (evt.Repository is null || evt.Deleted || evt.HeadCommit is null) return;
        if (!evt.Ref.StartsWith("refs/heads/")) return;

        var branch = evt.Ref["refs/heads/".Length..];
        var account = await GetOrCreateAccount(evt.Repository.Owner.Id, ct);
        var repository = await UpsertRepository(evt.Repository.Id, evt.Repository.Name, evt.Repository.FullName, evt.Repository.Private, account, ct);
        repository.DefaultBranch = evt.Repository.DefaultBranch;

        // The commit work itself is neutral and lives in ForgeEventsRecipient. What stays here is
        // the part only GitHub can do: reading its payload and resolving the account and repository
        // it names. Note the neutral event carries no parent — see BranchCommitPushed for why a
        // push cannot honestly supply one.
        DateTimeOffset? authoredAt = DateTimeOffset.TryParse(evt.HeadCommit.Timestamp, out var timestamp)
            ? timestamp
            : null;

        await messageBus.BroadcastAsync(new ForgeWebhookMessage<BranchCommitPushed>(
            EForgeProvider.GitHub,
            new BranchCommitPushed(
                RepositoryId: repository.Id!,
                Branch: branch,
                Sha: evt.After,
                Message: evt.HeadCommit.Message,
                AuthoredAt: authoredAt)), ct);
    }

    private async Task OnPullRequest(PullRequestEvent evt, CancellationToken ct)
    {
        if (evt.Repository is null) return;

        // Retention D4: merged PRs surrender their build data. Closed-unmerged
        // PRs keep theirs — they may reopen, and nothing about them is final.
        if (evt.Action == "closed" && evt.PullRequest.Merged == true)
        {
            // ⚠️ Loaded, never upserted. A close is not a reason to mint documents, and upserting
            // here would rewrite Repository.Account from the payload — which silently re-points a
            // repository at a different account and loses the delete-branch policy it was
            // inheriting. An unknown repository has nothing to retain and nothing to delete.
            var mergedRepository = await session.LoadAsync<Repository>(Repository.DocumentId(evt.Repository.Id), ct);
            if (mergedRepository is null) return;

            var head = evt.PullRequest.Head.Repo;
            var @base = evt.PullRequest.Base.Repo;

            await messageBus.BroadcastAsync(new ForgeWebhookMessage<PullRequestMerged>(
                EForgeProvider.GitHub,
                new PullRequestMerged(
                    RepositoryId: mergedRepository.Id!,
                    Number: (int)evt.Number,
                    HeadRef: evt.PullRequest.Head.Ref,
                    // Unknown counts as "from a fork". Declining to delete is recoverable;
                    // deleting someone else's branch is not.
                    HeadIsFromSameRepository: head is not null && @base is not null && head.Id == @base.Id)), ct);
            return;
        }

        if (evt.Action is not ("opened" or "synchronize" or "reopened")) return;

        var pr = evt.PullRequest;
        var account = await GetOrCreateAccount(evt.Repository.Owner.Id, ct);
        var repository = await UpsertRepository(evt.Repository.Id, evt.Repository.Name, evt.Repository.FullName, evt.Repository.Private, account, ct);

        // The three actions collapse to one neutral event because the app does the same thing for
        // all three; only "was this the first open?" survives the collapse, because exactly one
        // consumer — the pending coverage comment — needs it.
        await messageBus.BroadcastAsync(new ForgeWebhookMessage<PullRequestUpdated>(
            EForgeProvider.GitHub,
            new PullRequestUpdated(
                RepositoryId: repository.Id!,
                Number: (int)evt.Number,
                HeadSha: pr.Head.Sha,
                HeadRef: pr.Head.Ref,
                BaseRef: pr.Base.Ref,
                BaseSha: pr.Base.Sha,
                Title: pr.Title,
                IsFirstOpen: evt.Action is "opened" or "reopened",
                AuthorIsBot: pr.User?.Type is not null && pr.User.Type == Octokit.Webhooks.Models.UserType.Bot)), ct);
    }

    private async Task<Account> GetOrCreateAccount(long gitHubId, CancellationToken ct)
    {
        var id = Account.DocumentId(gitHubId);
        var account = await session.LoadAsync<Account>(id, ct);
        if (account is null)
        {
            account = new Account { GitHubId = gitHubId };
            await session.StoreAsync(account, id, ct);
        }
        return account;
    }

    private async Task<Repository> UpsertRepository(long gitHubId, string name, string fullName, bool isPrivate, Account account, CancellationToken ct)
    {
        var id = Repository.DocumentId(gitHubId);
        var repository = await session.LoadAsync<Repository>(id, ct);
        if (repository is null)
        {
            repository = new Repository { GitHubId = gitHubId };
            await session.StoreAsync(repository, id, ct);
        }
        ApplyRepositoryFields(repository, name, fullName, isPrivate, account);
        return repository;
    }

    /// <summary>
    /// One round-trip for the whole batch: an installation event carries every
    /// repository the App was granted, and a per-repo LoadAsync loop blows
    /// RavenDB's 30-requests-per-session cap on any real account.
    /// </summary>
    private async Task UpsertRepositories(
        IEnumerable<(long GitHubId, string Name, string FullName, bool IsPrivate)> repos, Account account, CancellationToken ct)
    {
        var items = repos.ToList();
        if (items.Count == 0) return;

        var loaded = await session.LoadAsync<Repository>(
            items.Select(r => Repository.DocumentId(r.GitHubId)), ct);

        foreach (var item in items)
        {
            var id = Repository.DocumentId(item.GitHubId);
            var repository = loaded.GetValueOrDefault(id);
            if (repository is null)
            {
                repository = new Repository { GitHubId = item.GitHubId };
                await session.StoreAsync(repository, id, ct);
            }
            ApplyRepositoryFields(repository, item.Name, item.FullName, item.IsPrivate, account);
        }
    }

    private static void ApplyRepositoryFields(Repository repository, string name, string fullName, bool isPrivate, Account account)
    {
        repository.Account = account.Id;
        repository.Name = name;
        repository.FullName = fullName;
        repository.OwnerLogin = fullName.Split('/')[0];
        repository.IsPrivate = isPrivate;

        // Every caller of this reached us through an installation the App still holds, which is
        // itself the proof that the repository is reachable. The one exception — a transfer, where
        // the payload proves the opposite — disconnects again after upserting, deliberately.
        Connect(repository);
    }

    /// <summary>Marks a repository reachable again, clearing any record of why it was not.</summary>
    private static void Connect(Repository repository)
    {
        repository.Connection = RepositoryConnection.Connected;
        repository.DisconnectedReason = null;
        repository.DisconnectedAtUtc = null;
    }

    /// <summary>
    /// Marks a repository unreachable. Never deletes: the coverage history stays, the badge keeps
    /// serving its last known value, and the report URLs keep resolving — the repository simply
    /// stops being advertised to anyone but its owner.
    /// </summary>
    private static void Disconnect(Repository repository, string reason)
    {
        repository.Connection = RepositoryConnection.Disconnected;
        repository.DisconnectedReason = reason;
        repository.DisconnectedAtUtc = DateTime.UtcNow;
    }

    private async Task DisconnectRepositoriesOfAsync(Account account, string reason, CancellationToken ct)
    {
        foreach (var repository in await LoadRepositoriesOfAsync(account, ct))
            Disconnect(repository, reason);
    }

    /// <summary>
    /// The repositories owned by an account, by the <see cref="Repository.Account"/> reference
    /// rather than by <c>OwnerLogin</c>: a rename changes the login, and the callers here are
    /// precisely the ones that run while it is changing.
    /// </summary>
    private async Task<IReadOnlyList<Repository>> LoadRepositoriesOfAsync(Account account, CancellationToken ct)
    {
        if (account.Id is null) return [];

        // One query, not a load per repository — this runs inside a session whose request budget
        // is 30, and a real organization has more repositories than that.
        return await session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(r => r.Account == account.Id)
            .Take(MaxRepositoriesPerAccount)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Records the name a repository used to be known by, if it is really changing and we have not
    /// already recorded it. Capped, because a repository renamed often would otherwise grow an
    /// unbounded array inside an index.
    /// </summary>

    private async Task<Commit> GetOrCreateCommit(long repoGitHubId, string sha, CancellationToken ct)
    {
        var id = Commit.DocumentId(repoGitHubId, sha);
        var commit = await session.LoadAsync<Commit>(id, ct);
        if (commit is null)
        {
            commit = new Commit { Sha = sha, Repository = Repository.DocumentId(repoGitHubId), FirstSeenAtUtc = DateTimeOffset.UtcNow };
            await session.StoreAsync(commit, id, ct);
        }
        return commit;
    }
}
