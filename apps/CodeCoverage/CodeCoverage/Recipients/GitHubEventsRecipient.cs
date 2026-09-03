using System.Text.Json;
using CodeCoverage.Entities;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Webhooks.GitHub.Messages;
using Octokit.Webhooks.Events;
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
            var loaded = await session.LoadAsync<Repository>(removedIds, ct);
            foreach (var existing in loaded.Values)
            {
                if (existing is not null)
                    session.Delete(existing);
            }
        }
    }

    private async Task OnRepository(RepositoryEvent evt, CancellationToken ct)
    {
        var ghRepo = evt.Repository;
        if (ghRepo is null) return;

        if (evt.Action == "deleted")
        {
            var existing = await session.LoadAsync<Repository>(Repository.DocumentId(ghRepo.Id), ct);
            if (existing is not null)
                session.Delete(existing);
            return;
        }

        var account = await GetOrCreateAccount(ghRepo.Owner.Id, ct);
        if (string.IsNullOrEmpty(account.Login))
        {
            account.Login = ghRepo.Owner.Login;
            account.AvatarUrl = ghRepo.Owner.AvatarUrl;
            account.Type = ghRepo.Owner.Type.StringValue == "Organization" ? "Organization" : "User";
        }

        var repository = await UpsertRepository(ghRepo.Id, ghRepo.Name, ghRepo.FullName, ghRepo.Private, account, ct);
        repository.DefaultBranch = ghRepo.DefaultBranch;
        repository.Archived = ghRepo.Archived;
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
