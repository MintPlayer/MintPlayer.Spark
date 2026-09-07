using CodeCoverage.Entities;
using CodeCoverage.Recipients;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Webhooks.GitHub.Messages;
using Octokit.Webhooks;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Recipients;

/// <summary>
/// The repository lifecycle: what happens to our documents when a repository is renamed,
/// transferred, archived, deleted on GitHub, or removed from an installation.
/// <para>
/// None of this had a test. The handler had exactly one branch — <c>deleted</c>, which hard-deleted
/// the Repository document and orphaned every Commit and Build beneath it — while
/// <c>transferred</c>, <c>renamed</c> and the rest fell through a single upsert that could not
/// express "this left us". The <c>installation_repositories</c> branch had never executed at all,
/// because the webhooks library dropped that event before any app could see it, so its
/// <c>session.Delete</c> was live, unreviewed and about to run for the first time.
/// </para>
/// <para>
/// Events are fed as raw JSON, which is the real seam: the recipient deserializes
/// <c>EventJson</c> itself, so this exercises Octokit's converters exactly as production does.
/// </para>
/// </summary>
public class GitHubRepositoryLifecycleTests : CoverageRavenTest
{
    private const long RepoId = 777;
    private const long OldOwnerId = 11;
    private const long NewOwnerId = 22;

    private sealed class RecordingMessageBus : IMessageBus
    {
        public List<object> Messages { get; } = [];
        public Task BroadcastAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message!);
            return Task.CompletedTask;
        }
        public Task BroadcastOnceAsync<TMessage>(TMessage message, string deduplicationKey, CancellationToken cancellationToken = default)
            => BroadcastAsync(message, cancellationToken);
        public Task DelayBroadcastAsync<TMessage>(TMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
            => BroadcastAsync(message, cancellationToken);
    }

    private static GitHubEventsRecipient CreateRecipient(IAsyncDocumentSession session, RecordingMessageBus? bus = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddSingleton(session);
        services.AddSingleton<IMessageBus>(bus ?? new RecordingMessageBus());
        services.AddScoped<GitHubEventsRecipient>();
        return services.BuildServiceProvider().GetRequiredService<GitHubEventsRecipient>();
    }

    private static string InstallationJson(string action, string repositories = "") => $$"""
        {
          "action": "{{action}}",
          "installation": {
            "id": 1, "node_id": "I_1", "app_id": 5, "app_slug": "coverage", "target_id": {{OldOwnerId}},
            "target_type": "Organization", "account": {{OwnerJson(OldOwnerId, "acme")}},
            "repository_selection": "all",
            "access_tokens_url": "https://api.github.com/app/installations/1/access_tokens",
            "repositories_url": "https://api.github.com/installation/repositories",
            "html_url": "https://github.com/settings/installations/1",
            "events": [], "created_at": "2026-01-01T00:00:00Z", "updated_at": "2026-01-01T00:00:00Z",
            "permissions": {}, "single_file_name": null, "suspended_by": null, "suspended_at": null
          },
          {{(repositories == "" ? "" : $"\"repositories\": [{repositories}],")}}
          "sender": {{OwnerJson(OldOwnerId, "acme")}}
        }
        """;

    private static GitHubWebhookMessage Message(string eventType, string json) => new()
    {
        Headers = new WebhookHeaders(),
        InstallationId = 1,
        RepositoryFullName = "acme/widgets",
        EventType = eventType,
        EventJson = json,
    };

    private static string OwnerJson(long id, string login) => $$"""
        {
          "login": "{{login}}", "id": {{id}}, "node_id": "U_{{id}}", "type": "Organization",
          "avatar_url": "https://avatars.example/u/{{id}}",
          "gravatar_id": "", "url": "https://api.github.com/users/{{login}}",
          "html_url": "https://github.com/{{login}}",
          "followers_url": "https://api.github.com/users/{{login}}/followers",
          "following_url": "https://api.github.com/users/{{login}}/following{/other_user}",
          "gists_url": "https://api.github.com/users/{{login}}/gists{/gist_id}",
          "starred_url": "https://api.github.com/users/{{login}}/starred{/owner}{/repo}",
          "subscriptions_url": "https://api.github.com/users/{{login}}/subscriptions",
          "organizations_url": "https://api.github.com/users/{{login}}/orgs",
          "repos_url": "https://api.github.com/users/{{login}}/repos",
          "events_url": "https://api.github.com/users/{{login}}/events{/privacy}",
          "received_events_url": "https://api.github.com/users/{{login}}/received_events",
          "site_admin": false
        }
        """;

    private static string RepositoryJson(long ownerId, string owner, string name, bool archived = false) => $$"""
        {
          "id": {{RepoId}}, "node_id": "R_1", "name": "{{name}}", "full_name": "{{owner}}/{{name}}",
          "private": false, "owner": {{OwnerJson(ownerId, owner)}},
          "html_url": "https://github.com/{{owner}}/{{name}}",
          "description": null, "fork": false,
          "url": "https://api.github.com/repos/{{owner}}/{{name}}",
          "created_at": "2026-01-01T00:00:00Z",
          "updated_at": "2026-01-01T00:00:00Z",
          "pushed_at": "2026-01-01T00:00:00Z",
          "git_url": "git://github.com/{{owner}}/{{name}}.git",
          "ssh_url": "git@github.com:{{owner}}/{{name}}.git",
          "clone_url": "https://github.com/{{owner}}/{{name}}.git",
          "svn_url": "https://github.com/{{owner}}/{{name}}",
          "homepage": null, "size": 1, "stargazers_count": 0, "watchers_count": 0,
          "language": null, "has_issues": true, "has_projects": true, "has_downloads": true,
          "has_wiki": true, "has_pages": false, "forks_count": 0, "mirror_url": null,
          "archived": {{(archived ? "true" : "false")}}, "disabled": false, "open_issues_count": 0, "license": null,
          "allow_forking": true, "is_template": false, "topics": [], "visibility": "public",
          "forks": 0, "open_issues": 0, "watchers": 0, "default_branch": "master"
        }
        """;

    /// <summary>
    /// Octokit deserializes a repository event into an action-specific subclass, and
    /// <c>renamed</c>, <c>transferred</c> and <c>edited</c> each declare <c>changes</c> required —
    /// so a fixture without it throws rather than producing a half-populated event. GitHub always
    /// sends it for those actions; these mirror the real shapes.
    /// </summary>
    private static string ChangesFor(string action, string previousName) => action switch
    {
        "renamed" => $$"""
            "changes": { "repository": { "name": { "from": "{{previousName}}" } } },
            """,
        "transferred" => $$"""
            "changes": { "owner": { "from": { "organization": {{OwnerJson(OldOwnerId, "acme")}} } } },
            """,
        "edited" => """
            "changes": { "default_branch": { "from": "main" } },
            """,
        _ => string.Empty,
    };

    private static string RepositoryEventJson(string action, long ownerId, string owner, string name,
        bool archived = false, string previousName = "widgets") => $$"""
        {
          "action": "{{action}}",
          {{ChangesFor(action, previousName)}}
          "repository": {{RepositoryJson(ownerId, owner, name, archived)}},
          "sender": {{OwnerJson(ownerId, owner)}}
        }
        """;

    private static string InstallationRepositoriesJson(string action, string added, string removed) => $$"""
        {
          "action": "{{action}}",
          "installation": {
            "id": 1, "node_id": "I_1", "app_id": 5, "app_slug": "coverage", "target_id": {{OldOwnerId}},
            "target_type": "Organization", "account": {{OwnerJson(OldOwnerId, "acme")}},
            "repository_selection": "selected",
            "access_tokens_url": "https://api.github.com/app/installations/1/access_tokens",
            "repositories_url": "https://api.github.com/installation/repositories",
            "html_url": "https://github.com/settings/installations/1",
            "events": [], "created_at": "2026-01-01T00:00:00Z", "updated_at": "2026-01-01T00:00:00Z",
            "permissions": {}, "single_file_name": null, "suspended_by": null, "suspended_at": null
          },
          "repository_selection": "selected",
          "repositories_added": [{{added}}],
          "repositories_removed": [{{removed}}],
          "requester": null,
          "sender": {{OwnerJson(OldOwnerId, "acme")}}
        }
        """;

    private static string LiteRepositoryJson(string owner, string name) => $$"""
        { "id": {{RepoId}}, "node_id": "R_1", "name": "{{name}}", "full_name": "{{owner}}/{{name}}", "private": false }
        """;

    /// <summary>Seeds a connected repository plus a commit under it, so orphaning is observable.</summary>
    private static async Task SeedAsync(IAsyncDocumentSession session, string owner = "acme", string name = "widgets")
    {
        var account = new Account { GitHubId = OldOwnerId, Login = owner, Type = "Organization" };
        await session.StoreAsync(account, Account.DocumentId(OldOwnerId));

        await session.StoreAsync(new Repository
        {
            GitHubId = RepoId,
            Account = Account.DocumentId(OldOwnerId),
            Name = name,
            FullName = $"{owner}/{name}",
            OwnerLogin = owner,
        }, Repository.DocumentId(RepoId));

        await session.StoreAsync(new Commit
        {
            Sha = "abc",
            Repository = Repository.DocumentId(RepoId),
            FirstSeenAtUtc = DateTimeOffset.UtcNow,
        }, Commit.DocumentId(RepoId, "abc"));

        await session.SaveChangesAsync();
    }

    [Fact]
    public async Task A_repository_deleted_on_GitHub_is_disconnected_and_its_history_survives()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedAsync(session);

        await CreateRecipient(session).HandleAsync(
            Message("repository", RepositoryEventJson("deleted", OldOwnerId, "acme", "widgets")));

        var repository = await session.LoadAsync<Repository>(Repository.DocumentId(RepoId));
        Assert.NotNull(repository);
        Assert.Equal(RepositoryConnection.Disconnected, repository.Connection);
        Assert.Equal(DisconnectedReasons.DeletedOnGitHub, repository.DisconnectedReason);

        // The point of not deleting: the commit under it is still reachable, so a report link
        // someone shared still resolves.
        Assert.NotNull(await session.LoadAsync<Commit>(Commit.DocumentId(RepoId, "abc")));
    }

    /// <summary>
    /// `repository.transferred` means we <em>gained</em> a repository, not that we lost one.
    /// <para>
    /// Measured against the real API on 2026-09-05 by transferring MintPlayer/CodeCoverage out of
    /// the organization and back: the installation that gains the repository receives
    /// `repository.transferred` followed by `installation_repositories.added`, while the one losing
    /// it receives `installation_repositories.removed` and <b>no repository event at all</b>.
    /// </para>
    /// <para>
    /// So this must re-parent and stay connected. Disconnecting here — which is what the event's
    /// name invites — would mark a repository we can plainly see as unreachable and then depend on
    /// the `added` that follows to undo it, resting correctness on the delivery order of two
    /// independently queued messages.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_transfer_reparents_the_repository_and_leaves_it_connected()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedAsync(session);

        await CreateRecipient(session).HandleAsync(
            Message("repository", RepositoryEventJson("transferred", NewOwnerId, "acme-archive", "widgets")));

        var repository = await session.LoadAsync<Repository>(Repository.DocumentId(RepoId));
        Assert.NotNull(repository);
        Assert.Equal("acme-archive/widgets", repository.FullName);
        Assert.Equal("acme-archive", repository.OwnerLogin);
        Assert.Equal(Account.DocumentId(NewOwnerId), repository.Account);
        Assert.Contains("acme/widgets", repository.PreviousFullNames);
        Assert.Equal(RepositoryConnection.Connected, repository.Connection);
        Assert.Null(repository.DisconnectedReason);
    }

    /// <summary>
    /// The other half of the same measurement, and the exact shape of the production bug: a repo
    /// transferred out of the organization is reported only as a shrinking repository set. This is
    /// the delivery the webhooks library used to discard, which is why MintPlayer/CodeCoverage went
    /// on being advertised for days after it left.
    /// </summary>
    [Fact]
    public async Task Losing_a_repository_is_reported_only_as_a_removal_and_that_is_what_disconnects()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedAsync(session);

        await CreateRecipient(session).HandleAsync(Message(
            "installation_repositories",
            InstallationRepositoriesJson("removed", added: "", removed: LiteRepositoryJson("acme", "widgets"))));

        var repository = await session.LoadAsync<Repository>(Repository.DocumentId(RepoId));
        Assert.NotNull(repository);
        Assert.Equal(RepositoryConnection.Disconnected, repository.Connection);
        Assert.Equal(DisconnectedReasons.RemovedFromInstallation, repository.DisconnectedReason);
    }

    /// <summary>
    /// The real gaining sequence, in the order GitHub delivered it (10:25:20 then 10:25:21), and
    /// again in the reverse order — because two messages on two queues have no guaranteed
    /// processing order, and the end state must not depend on it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gaining_a_repository_ends_connected_whichever_order_the_two_events_are_processed(bool reversed)
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedAsync(session);

        var recipient = CreateRecipient(session);
        var transferred = Message("repository", RepositoryEventJson("transferred", NewOwnerId, "acme-archive", "widgets"));
        var added = Message("installation_repositories",
            InstallationRepositoriesJson("added", added: LiteRepositoryJson("acme-archive", "widgets"), removed: ""));

        foreach (var message in reversed ? new[] { added, transferred } : new[] { transferred, added })
            await recipient.HandleAsync(message);

        var repository = await session.LoadAsync<Repository>(Repository.DocumentId(RepoId));
        Assert.NotNull(repository);
        Assert.Equal(RepositoryConnection.Connected, repository.Connection);
    }

    [Fact]
    public async Task A_rename_records_the_old_name_and_stays_connected()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedAsync(session);

        await CreateRecipient(session).HandleAsync(
            Message("repository", RepositoryEventJson("renamed", OldOwnerId, "acme", "gadgets")));

        var repository = await session.LoadAsync<Repository>(Repository.DocumentId(RepoId));
        Assert.NotNull(repository);
        Assert.Equal("acme/gadgets", repository.FullName);
        Assert.Contains("acme/widgets", repository.PreviousFullNames);
        Assert.Equal(RepositoryConnection.Connected, repository.Connection);
    }

    [Fact]
    public async Task Archiving_does_not_disconnect_because_an_archived_repository_is_still_ours()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedAsync(session);

        await CreateRecipient(session).HandleAsync(
            Message("repository", RepositoryEventJson("archived", OldOwnerId, "acme", "widgets", archived: true)));

        var repository = await session.LoadAsync<Repository>(Repository.DocumentId(RepoId));
        Assert.NotNull(repository);
        Assert.True(repository.Archived);
        Assert.Equal(RepositoryConnection.Connected, repository.Connection);
    }

    [Fact]
    public async Task A_repository_removed_from_an_installation_is_disconnected_not_deleted()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedAsync(session);

        await CreateRecipient(session).HandleAsync(Message(
            "installation_repositories",
            InstallationRepositoriesJson("removed", added: "", removed: LiteRepositoryJson("acme", "widgets"))));

        var repository = await session.LoadAsync<Repository>(Repository.DocumentId(RepoId));
        Assert.NotNull(repository);
        Assert.Equal(RepositoryConnection.Disconnected, repository.Connection);
        Assert.Equal(DisconnectedReasons.RemovedFromInstallation, repository.DisconnectedReason);
        Assert.NotNull(await session.LoadAsync<Commit>(Commit.DocumentId(RepoId, "abc")));
    }

    [Fact]
    public async Task Adding_a_repository_back_to_an_installation_reconnects_it()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedAsync(session);

        var recipient = CreateRecipient(session);
        await recipient.HandleAsync(Message(
            "installation_repositories",
            InstallationRepositoriesJson("removed", added: "", removed: LiteRepositoryJson("acme", "widgets"))));
        await recipient.HandleAsync(Message(
            "installation_repositories",
            InstallationRepositoriesJson("added", added: LiteRepositoryJson("acme", "widgets"), removed: "")));

        var repository = await session.LoadAsync<Repository>(Repository.DocumentId(RepoId));
        Assert.NotNull(repository);
        Assert.Equal(RepositoryConnection.Connected, repository.Connection);
        Assert.Null(repository.DisconnectedReason);
        Assert.Null(repository.DisconnectedAtUtc);
    }

    /// <summary>
    /// When the App is installed on both the source and the destination of a transfer, one move
    /// produces three events from two installations. Measured 2026-09-05 by transferring a probe
    /// repository from the MintPlayer org to a personal account, both installed org-wide:
    ///
    /// <code>
    /// 10:58:49  installation_repositories.removed   installation 153617061 (MintPlayer)
    /// 10:58:51  repository.transferred              installation 153539439 (personal)
    /// 10:58:51  installation_repositories.added     installation 153539439 (personal)
    /// </code>
    ///
    /// Two seconds apart, on two queues, with no ordering guarantee. If the removal is applied
    /// after the others it would disconnect a repository the App can plainly still see — so
    /// ownership, not arrival order, decides: a removal from an account that no longer owns the
    /// repository is stale.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_removal_from_the_previous_owner_cannot_disconnect_a_repository_that_moved_to_us(bool removalLast)
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedAsync(session);

        var recipient = CreateRecipient(session);
        var removedByOldOwner = Message("installation_repositories",
            InstallationRepositoriesJson("removed", added: "", removed: LiteRepositoryJson("acme", "widgets")));
        var gained = Message("repository", RepositoryEventJson("transferred", NewOwnerId, "acme-archive", "widgets"));

        foreach (var message in removalLast ? new[] { gained, removedByOldOwner } : new[] { removedByOldOwner, gained })
            await recipient.HandleAsync(message);

        var repository = await session.LoadAsync<Repository>(Repository.DocumentId(RepoId));
        Assert.Equal(Account.DocumentId(NewOwnerId), repository!.Account);
        Assert.Equal(RepositoryConnection.Connected, repository.Connection);
    }

    /// <summary>
    /// The guard must not swallow the case it was not written for: when nobody else has claimed the
    /// repository, a removal from its current owner is exactly the transfer-away signal, and the
    /// only one we get.
    /// </summary>
    [Fact]
    public async Task A_removal_from_the_current_owner_still_disconnects()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedAsync(session);

        await CreateRecipient(session).HandleAsync(Message("installation_repositories",
            InstallationRepositoriesJson("removed", added: "", removed: LiteRepositoryJson("acme", "widgets"))));

        var repository = await session.LoadAsync<Repository>(Repository.DocumentId(RepoId));
        Assert.Equal(RepositoryConnection.Disconnected, repository!.Connection);
        Assert.Equal(DisconnectedReasons.RemovedFromInstallation, repository.DisconnectedReason);
    }

    /// <summary>
    /// A repository the App has just been granted. It arrives with no document of its own, so this
    /// also covers the create half of every upsert path.
    /// </summary>
    [Fact]
    public async Task A_repository_created_on_GitHub_is_stored_and_connected()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();

        await CreateRecipient(session).HandleAsync(
            Message("repository", RepositoryEventJson("created", OldOwnerId, "acme", "widgets")));

        var repository = await session.LoadAsync<Repository>(Repository.DocumentId(RepoId));
        Assert.NotNull(repository);
        Assert.Equal("acme/widgets", repository.FullName);
        Assert.Equal("master", repository.DefaultBranch);
        Assert.Equal(RepositoryConnection.Connected, repository.Connection);
        Assert.Equal("acme", (await session.LoadAsync<Account>(Account.DocumentId(OldOwnerId)))!.Login);
    }

    /// <summary>
    /// Visibility keys on <c>IsPrivate</c>, so a repository going private has to move it or a
    /// previously-public repository's coverage stays world-readable.
    /// </summary>
    [Theory]
    [InlineData("privatized", true)]
    [InlineData("publicized", false)]
    public async Task Changing_visibility_on_GitHub_moves_IsPrivate(string action, bool expectedPrivate)
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedAsync(session);

        var json = RepositoryEventJson(action, OldOwnerId, "acme", "widgets")
            .Replace("\"private\": false", $"\"private\": {(expectedPrivate ? "true" : "false")}");
        await CreateRecipient(session).HandleAsync(Message("repository", json));

        var repository = await session.LoadAsync<Repository>(Repository.DocumentId(RepoId));
        Assert.Equal(expectedPrivate, repository!.IsPrivate);
        Assert.Equal(RepositoryConnection.Connected, repository.Connection);
    }

    [Fact]
    public async Task Unarchiving_clears_the_archived_flag()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedAsync(session);

        var recipient = CreateRecipient(session);
        await recipient.HandleAsync(Message("repository",
            RepositoryEventJson("archived", OldOwnerId, "acme", "widgets", archived: true)));
        await recipient.HandleAsync(Message("repository",
            RepositoryEventJson("unarchived", OldOwnerId, "acme", "widgets", archived: false)));

        var repository = await session.LoadAsync<Repository>(Repository.DocumentId(RepoId));
        Assert.False(repository!.Archived);
        Assert.Equal(RepositoryConnection.Connected, repository.Connection);
    }

    [Fact]
    public async Task Installing_the_App_records_the_installation_and_its_repositories()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();

        await CreateRecipient(session).HandleAsync(Message("installation",
            InstallationJson("created", repositories: LiteRepositoryJson("acme", "widgets"))));

        var account = await session.LoadAsync<Account>(Account.DocumentId(OldOwnerId));
        Assert.Equal(1, account!.InstallationId);
        Assert.Equal("Organization", account.Type);

        var repository = await session.LoadAsync<Repository>(Repository.DocumentId(RepoId));
        Assert.NotNull(repository);
        Assert.Equal(RepositoryConnection.Connected, repository.Connection);
    }

    [Fact]
    public async Task Uninstalling_the_App_disconnects_the_accounts_repositories_permanently()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedAsync(session);
        WaitForIndexing(store);

        await CreateRecipient(session).HandleAsync(Message("installation", InstallationJson("deleted")));

        var repository = await session.LoadAsync<Repository>(Repository.DocumentId(RepoId));
        Assert.Equal(RepositoryConnection.Disconnected, repository!.Connection);
        Assert.Equal(DisconnectedReasons.AppUninstalled, repository.DisconnectedReason);
        Assert.NotNull(await session.LoadAsync<Commit>(Commit.DocumentId(RepoId, "abc")));
    }

    /// <summary>
    /// D11: the payload announces that the set changed, so the authoritative answer is asked for.
    /// Without this the narrowing case — `added` with an empty `repositories_removed` — silently
    /// leaves every other repository of the account advertised.
    /// </summary>
    [Theory]
    [InlineData("added")]
    [InlineData("removed")]
    public async Task Any_installation_repositories_event_asks_for_a_reconcile(string action)
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedAsync(session);

        var bus = new RecordingMessageBus();
        var lite = LiteRepositoryJson("acme", "widgets");
        await CreateRecipient(session, bus).HandleAsync(Message("installation_repositories",
            InstallationRepositoriesJson(action,
                added: action == "added" ? lite : "",
                removed: action == "removed" ? lite : "")));

        Assert.Contains(
            bus.Messages.OfType<CodeCoverage.Ingestion.ReconcileAccountMessage>(),
            m => m.AccountGitHubId == OldOwnerId);
    }

    [Fact]
    public async Task Suspending_the_App_disconnects_the_accounts_repositories_as_temporary()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedAsync(session);
        WaitForIndexing(store);

        await CreateRecipient(session).HandleAsync(Message("installation", InstallationJson("suspend")));

        var repository = await session.LoadAsync<Repository>(Repository.DocumentId(RepoId));
        Assert.Equal(RepositoryConnection.Disconnected, repository!.Connection);
        Assert.Equal(DisconnectedReasons.AppSuspended, repository.DisconnectedReason);
        Assert.Null((await session.LoadAsync<Account>(Account.DocumentId(OldOwnerId)))!.InstallationId);
    }

    /// <summary>
    /// The gap this test exists for: GitHub populates the installation event's <c>repositories</c>
    /// array on <c>created</c> and <c>deleted</c>, but not on <c>unsuspend</c>. So the upsert that
    /// looks like it reconnects everything reconnects nothing, and an account's repositories would
    /// stay hidden for up to a day after the App is re-enabled. The reconcile broadcast is what
    /// actually restores them.
    /// </summary>
    [Fact]
    public async Task Unsuspending_asks_for_a_reconcile_because_the_payload_lists_no_repositories()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedAsync(session);
        WaitForIndexing(store);

        var bus = new RecordingMessageBus();
        var recipient = CreateRecipient(session, bus);
        await recipient.HandleAsync(Message("installation", InstallationJson("suspend")));
        await recipient.HandleAsync(Message("installation", InstallationJson("unsuspend")));

        var account = await session.LoadAsync<Account>(Account.DocumentId(OldOwnerId));
        Assert.NotNull(account!.InstallationId);

        var reconcile = bus.Messages.OfType<CodeCoverage.Ingestion.ReconcileAccountMessage>().ToList();
        Assert.Contains(reconcile, m => m.AccountGitHubId == OldOwnerId);
    }

    /// <summary>
    /// Org renames arrive as `organization.renamed`, not `installation_target`. Measured
    /// 2026-09-05: neither Coverage app subscribes to `installation_target`, and an App receives
    /// only what it subscribes to — so a handler listening for it alone would never have run.
    /// </summary>
    [Fact]
    public async Task An_organization_rename_rewrites_the_account_and_every_full_name_under_it()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedAsync(session);
        WaitForIndexing(store);

        var json = $$"""
            {
              "action": "renamed",
              "organization": {{OwnerJson(OldOwnerId, "acme-renamed")}},
              "changes": { "login": { "from": "acme" } },
              "sender": {{OwnerJson(OldOwnerId, "acme-renamed")}}
            }
            """;
        await CreateRecipient(session).HandleAsync(Message("organization", json));

        var account = await session.LoadAsync<Account>(Account.DocumentId(OldOwnerId));
        Assert.Equal("acme-renamed", account!.Login);

        var repository = await session.LoadAsync<Repository>(Repository.DocumentId(RepoId));
        Assert.Equal("acme-renamed/widgets", repository!.FullName);
        Assert.Equal("acme-renamed", repository.OwnerLogin);
        Assert.Contains("acme/widgets", repository.PreviousFullNames);
    }

    [Fact]
    public async Task An_organization_event_that_is_not_a_rename_changes_nothing()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedAsync(session);

        var json = $$"""
            {
              "action": "member_added",
              "organization": {{OwnerJson(OldOwnerId, "acme")}},
              "sender": {{OwnerJson(OldOwnerId, "acme")}}
            }
            """;
        await CreateRecipient(session).HandleAsync(Message("organization", json));

        var repository = await session.LoadAsync<Repository>(Repository.DocumentId(RepoId));
        Assert.Equal("acme/widgets", repository!.FullName);
        Assert.Empty(repository.PreviousFullNames);
    }

    [Fact]
    public async Task An_account_we_already_knew_learns_that_its_login_changed()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedAsync(session);

        // The guard this replaces refreshed the login only when it was empty, so a rename of an
        // account we already knew was ignored — and which event arrived next decided whether we
        // ever found out.
        await CreateRecipient(session).HandleAsync(
            Message("repository", RepositoryEventJson("edited", OldOwnerId, "acme-renamed", "widgets")));

        var account = await session.LoadAsync<Account>(Account.DocumentId(OldOwnerId));
        Assert.NotNull(account);
        Assert.Equal("acme-renamed", account.Login);
    }
}
