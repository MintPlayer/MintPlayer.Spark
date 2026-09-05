using System.Text.Json;
using CodeCoverage.Entities;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Webhooks.GitHub.Messages;
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
            case "installation_target":
                await OnInstallationTarget(message, cancellationToken);
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
                await UpsertRepositories(
                    (evt.Repositories ?? []).Select(r => (r.Id, r.Name, r.FullName, r.Private)), account, ct);
                break;
            case "deleted":
            case "suspend":
                account.InstallationId = null;
                // The App can no longer see anything this account owns, so nothing it owns should
                // still be advertised. The documents stay; only the advertising stops.
                await DisconnectRepositoriesOfAsync(account, DisconnectedReasons.AppUninstalled, ct);
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
                if (existing is not null)
                    Disconnect(existing, DisconnectedReasons.RemovedFromInstallation);
            }
        }
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

        if (evt.Action == "transferred")
        {
            // The upsert above has already re-parented the repository onto its new owner, which is
            // right — but a transfer is only observable to an installation that is losing access,
            // so being told about it means it left. If the App is installed on the new owner too,
            // the installation_repositories `added` that follows reconnects it; that ordering is
            // why this disconnects after the upsert rather than skipping it.
            Disconnect(repository, DisconnectedReasons.TransferredAway);
        }
    }

    /// <summary>
    /// An organization renamed itself. The account keeps its numeric id, so the document is the
    /// same one — but its login, and the owner half of every full name beneath it, are now wrong.
    /// <para>
    /// Read out of the raw payload rather than through a typed event: Octokit.Webhooks models
    /// <c>InstallationTargetEvent</c> with only the fields common to every webhook, and the two
    /// this event exists to carry — <c>account</c> and <c>changes.login.from</c> — are not among
    /// them.
    /// </para>
    /// </summary>
    private async Task OnInstallationTarget(GitHubWebhookMessage message, CancellationToken ct)
    {
        using var payload = JsonDocument.Parse(message.EventJson);
        if (!payload.RootElement.TryGetProperty("account", out var ghAccount)
            || ghAccount.ValueKind != JsonValueKind.Object
            || !ghAccount.TryGetProperty("id", out var idElement)
            || !idElement.TryGetInt64(out var accountId))
        {
            return;
        }

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
