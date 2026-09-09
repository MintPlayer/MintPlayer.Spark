using System.Text.Json;
using CodeCoverage.Entities;
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
    [Inject] private readonly IGitHubInstallationService installationService;

    /// <summary>Bound on a per-account repository sweep; the session's request budget is 30.</summary>
    private const int MaxRepositoriesPerAccount = 1024;

    /// <summary>How many former names one repository remembers, oldest dropped first.</summary>
    private const int MaxPreviousFullNames = 16;

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
            RememberFullName(previous, ghRepo.FullName);

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

        var account = await GetOrCreateAccount(accountId, ct);
        var previousLogin = account.Login;
        account.Login = login;
        if (ghAccount.TryGetProperty("avatar_url", out var avatarElement))
            account.AvatarUrl = avatarElement.GetString();

        if (string.IsNullOrEmpty(previousLogin) || previousLogin == account.Login)
            return;

        foreach (var repository in await LoadRepositoriesOfAsync(account, ct))
        {
            RememberFullName(repository, $"{account.Login}/{repository.Name}");
            repository.OwnerLogin = account.Login;
            repository.FullName = $"{account.Login}/{repository.Name}";
        }

        logger.LogInformation("Account {Previous} renamed to {Current}", previousLogin, account.Login);
    }

    private async Task OnPush(PushEvent evt, CancellationToken ct)
    {
        if (evt.Repository is null || evt.Deleted || evt.HeadCommit is null) return;
        if (!evt.Ref.StartsWith("refs/heads/")) return;

        var branch = evt.Ref["refs/heads/".Length..];
        var account = await GetOrCreateAccount(evt.Repository.Owner.Id, ct);
        var repository = await UpsertRepository(evt.Repository.Id, evt.Repository.Name, evt.Repository.FullName, evt.Repository.Private, account, ct);
        repository.DefaultBranch = evt.Repository.DefaultBranch;

        var commit = await GetOrCreateCommit(evt.Repository.Id, evt.After, ct);
        commit.Branch = branch;
        // Deliberately does NOT write ParentSha. `evt.Before` is the previous
        // ref tip, which is not this commit's parent in three of the six push
        // shapes: a push of five commits creates one document (the head), so
        // Before is the tip five commits back; a branch creation gives the
        // all-zero sha; a force-push gives an abandoned tip that need not be an
        // ancestor at all. It also raced the PR-event writer below for the same
        // field, so whichever arrived last decided what the value meant. One
        // writer, one meaning — see docs/upload-result-contract.md §5.
        commit.Message = evt.HeadCommit.Message;
        if (DateTimeOffset.TryParse(evt.HeadCommit.Timestamp, out var timestamp))
            commit.AuthoredAt = timestamp;
    }

    private async Task OnPullRequest(PullRequestEvent evt, CancellationToken ct)
    {
        if (evt.Repository is null) return;

        // Retention D4: merged PRs surrender their build data. Closed-unmerged
        // PRs keep theirs — they may reopen, and nothing about them is final.
        if (evt.Action == "closed" && evt.PullRequest.Merged == true)
        {
            await messageBus.BroadcastAsync(new Ingestion.DeletePullRequestBuildsMessage
            {
                RepositoryGitHubId = evt.Repository.Id,
                PullRequestNumber = (int)evt.Number,
            }, ct);

            // After the broadcast, not before: the retention message is in-process and cheap, and
            // it should not wait behind a GitHub round-trip that may take seconds or fail.
            await DeleteHeadBranchIfEnabled(evt, ct);
            return;
        }

        if (evt.Action is not ("opened" or "synchronize" or "reopened")) return;

        var pr = evt.PullRequest;
        var commit = await GetOrCreateCommit(evt.Repository.Id, pr.Head.Sha, ct);
        commit.Branch = pr.Head.Ref;
        commit.PullRequestNumber = (int)evt.Number;
        commit.Message ??= pr.Title;
        // The authoritative writer of the PR's target, for the same reason as
        // ParentSha below: `synchronize` re-sends a moved base, and a retarget
        // changes the ref outright, so a frozen first-seen value goes stale.
        commit.PullRequestBaseRef = pr.Base.Ref;
        commit.PullRequestBaseSha = pr.Base.Sha;
        // The sole writer of ParentSha, and the only one that ever meant
        // anything: the PR's base tip. Plain `=` rather than `??=` because
        // GitHub re-sends `synchronize` with an updated base when the base
        // branch advances, and freezing the first-seen base would quietly go
        // stale; a moved head is a new document, so this never corrupts an
        // earlier commit. Still only a hint for finding the PR — patch coverage
        // resolves its own merge base at compute time.
        commit.ParentSha = pr.Base.Sha;

        // Only on open/reopen: `synchronize` is already served by the finalize
        // path, which edits the same comment with the real numbers. Broadcast
        // rather than post from here, so an outage on GitHub's side cannot fail
        // the webhook delivery and cost us the event.
        if (evt.Action is "opened" or "reopened")
        {
            await messageBus.BroadcastAsync(new Feedback.OpenPullRequestCommentMessage
            {
                RepositoryGitHubId = evt.Repository.Id,
                PullRequestNumber = (int)evt.Number,
                HeadSha = pr.Head.Sha,
                AuthorIsBot = pr.User?.Type is not null && pr.User.Type == Octokit.Webhooks.Models.UserType.Bot,
            }, ct);
        }
    }

    /// <summary>
    /// Deletes a merged pull request's head branch, when the repository opted in.
    /// </summary>
    /// <remarks>
    /// Ported from the WebhooksDemo recipient this replaced, with the two changes that made the
    /// setting safe on a multi-tenant server: it is gated on a per-repository opt-in instead of
    /// applying bot-wide, and it fires only for merged pull requests instead of every close.
    /// <para>
    /// Deliberately not on the project-automation path. A board and a repository are siblings, so
    /// gating this on a board would have made it unreachable for an owner with no board, inert for
    /// a board carrying no pull-request rule, and duplicated for an owner with two. See C16.
    /// </para>
    /// <para>
    /// Never throws. Deleting the branch is a courtesy after the merge has already landed; failing
    /// the webhook delivery over it would cost us the event and change nothing about the merge.
    /// </para>
    /// </remarks>
    private async Task DeleteHeadBranchIfEnabled(PullRequestEvent evt, CancellationToken ct)
    {
        var repository = await session.LoadAsync<Repository>(Repository.DocumentId(evt.Repository!.Id), ct);
        if (repository is null || !repository.DeleteBranchOnPrClose)
            return;

        var pr = evt.PullRequest;
        var headRepo = pr.Head.Repo;
        var baseRepo = pr.Base.Repo;

        // A fork's head branch lives in a repository we were never given write access to, and that
        // its own owner still wants. The opt-in is on the base repository and cannot speak for it.
        if (headRepo is null || baseRepo is null || headRepo.Id != baseRepo.Id)
            return;

        var installationId = evt.Installation?.Id;
        if (installationId is null)
        {
            logger.LogWarning("No installation on pull_request for {FullName}; cannot delete branch {Ref}.",
                baseRepo.FullName, pr.Head.Ref);
            return;
        }

        var owner = baseRepo.Owner.Login;
        var name = baseRepo.Name;
        try
        {
            var client = await installationService.CreateInstallationClientAsync(installationId.Value);
            await client.Git.Reference.Delete(owner, name, $"heads/{pr.Head.Ref}");
            logger.LogInformation("Deleted branch {Owner}/{Repo}:{Ref} after PR #{Number} merged.",
                owner, name, pr.Head.Ref, pr.Number);
        }
        catch (Octokit.NotFoundException)
        {
            // Lost a race with GitHub's own delete_branch_on_merge, or somebody deleted it by hand.
            // The intended state is reached either way, so this is information, not a failure.
            logger.LogInformation("Branch {Owner}/{Repo}:{Ref} was already gone for PR #{Number}.",
                owner, name, pr.Head.Ref, pr.Number);
        }
        catch (Octokit.ApiValidationException ex)
        {
            // 422 is normally a protected branch. Retrying would fail identically every time.
            logger.LogWarning(ex, "Refused to delete branch {Owner}/{Repo}:{Ref} for PR #{Number} (likely protected).",
                owner, name, pr.Head.Ref, pr.Number);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete branch {Owner}/{Repo}:{Ref} for PR #{Number}.",
                owner, name, pr.Head.Ref, pr.Number);
        }
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
    private static void RememberFullName(Repository? repository, string newFullName)
    {
        if (repository is null) return;

        var previous = repository.FullName;
        if (string.IsNullOrEmpty(previous) || previous == newFullName) return;
        if (repository.PreviousFullNames.Contains(previous, StringComparer.OrdinalIgnoreCase)) return;

        repository.PreviousFullNames.Add(previous);
        if (repository.PreviousFullNames.Count > MaxPreviousFullNames)
            repository.PreviousFullNames.RemoveAt(0);
    }

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
