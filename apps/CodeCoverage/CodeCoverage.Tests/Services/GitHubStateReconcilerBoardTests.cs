using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;
using Raven.Client.Documents;
using Xunit;
using Account = CodeCoverage.Entities.Account;
using ProjectColumn = CodeCoverage.Entities.ProjectColumn;
using Repository = CodeCoverage.Entities.Repository;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// The halves of the nightly reconcile that <see cref="GitHubStateReconcilerTests"/> leaves out:
/// repositories that arrive (new, or transferred in from another account), renames nobody told us
/// about, and the whole Projects-V2 board path.
/// </summary>
/// <remarks>
/// The board source is scripted rather than empty, so discovery, the idempotent upsert, the
/// disconnect of a board that went missing, and the column refresh all actually run. The same
/// fail-closed rule applies to boards as to repositories and is pinned the same way: not knowing
/// what an installation holds must change nothing.
/// </remarks>
public class GitHubStateReconcilerBoardTests : CoverageRavenTest
{
    protected override bool DeployIndexes => true;

    private const long AccountGitHubId = 4200;
    private const long InstallationId = 77;

    private static string AccountId => Account.DocumentId(EForgeProvider.GitHub, AccountGitHubId);

    private sealed class ScriptedRepositories(params InstallationRepository[] live) : IInstallationRepositories
    {
        public Exception? Throws { get; init; }

        public Task<IReadOnlyList<InstallationRepository>> ListAsync(long installationId, int max, CancellationToken cancellationToken = default)
            => Throws is not null
                ? Task.FromException<IReadOnlyList<InstallationRepository>>(Throws)
                : Task.FromResult<IReadOnlyList<InstallationRepository>>(live);
    }

    private sealed class ScriptedProjects : IInstallationProjects
    {
        public List<InstallationProject> Boards { get; init; } = [];
        public Exception? ListThrows { get; init; }
        public Dictionary<string, ProjectStatusField> StatusFields { get; init; } = [];
        public Exception? StatusThrows { get; init; }
        public List<(string Owner, bool IsOrganization)> ListCalls { get; } = [];
        public List<string> StatusCalls { get; } = [];

        public Task<IReadOnlyList<InstallationProject>> ListAsync(
            long installationId, string ownerLogin, bool ownerIsOrganization, int max, CancellationToken cancellationToken = default)
        {
            ListCalls.Add((ownerLogin, ownerIsOrganization));
            return ListThrows is not null
                ? Task.FromException<IReadOnlyList<InstallationProject>>(ListThrows)
                : Task.FromResult<IReadOnlyList<InstallationProject>>(Boards);
        }

        public Task<ProjectStatusField> GetStatusFieldAsync(long installationId, string projectNodeId, CancellationToken cancellationToken = default)
        {
            StatusCalls.Add(projectNodeId);
            return StatusThrows is not null
                ? Task.FromException<ProjectStatusField>(StatusThrows)
                : Task.FromResult(StatusFields.GetValueOrDefault(projectNodeId, ProjectStatusField.None));
        }
    }

    private static InstallationRepository Live(long id, string fullName, bool isPrivate = false)
        => new(id, fullName.Split('/')[1], fullName, fullName.Split('/')[0], isPrivate, "main", Archived: false);

    private static async Task SeedAccountAsync(IDocumentStore store, string type = "Organization")
    {
        using var seed = store.OpenAsyncSession();
        await seed.StoreAsync(new Account
        {
            GitHubId = AccountGitHubId, Login = "acme", Type = type, InstallationId = InstallationId,
        }, AccountId);
        await seed.SaveChangesAsync();
    }

    private static async Task SeedAsync(IDocumentStore store, params object[] documents)
    {
        using var seed = store.OpenAsyncSession();
        foreach (var document in documents)
        {
            var id = document switch
            {
                Repository r => Repository.DocumentId(EForgeProvider.GitHub, r.GitHubId),
                GitHubProject p => GitHubProject.DocumentId(p.NodeId),
                _ => throw new ArgumentException("unsupported seed"),
            };
            await seed.StoreAsync(document, id);
        }
        await seed.SaveChangesAsync();
    }

    private async Task ReconcileAsync(IDocumentStore store, IInstallationRepositories repositories, IInstallationProjects projects)
    {
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();
        var account = await session.LoadAsync<Account>(AccountId);
        await new GitHubStateReconciler(session, repositories, projects, NullLogger<GitHubStateReconciler>.Instance)
            .ReconcileAsync(account);
        await session.SaveChangesAsync();
    }

    private static async Task<T> LoadAsync<T>(IDocumentStore store, string id)
    {
        using var session = store.OpenAsyncSession();
        return (await session.LoadAsync<T>(id))!;
    }

    [Fact]
    public async Task A_repository_the_app_has_never_seen_is_created_for_the_account()
    {
        using var store = GetDocumentStore();
        await SeedAccountAsync(store);

        await ReconcileAsync(store, new ScriptedRepositories(Live(501, "acme/brand-new", isPrivate: true)), new ScriptedProjects());

        var created = await LoadAsync<Repository>(store, Repository.DocumentId(EForgeProvider.GitHub, 501));
        created.Account.Should().Be(AccountId);
        created.FullName.Should().Be("acme/brand-new");
        created.IsPrivate.Should().BeTrue();
        created.DefaultBranch.Should().Be("main");
        created.Connection.Should().Be(RepositoryConnection.Connected);
    }

    /// <summary>
    /// A transfer INTO the installation: the repository exists, but under another account. It is
    /// adopted rather than duplicated, reconnected, and its old name remembered so published badges
    /// keep resolving.
    /// </summary>
    [Fact]
    public async Task A_repository_transferred_in_is_adopted_reconnected_and_remembers_its_old_name()
    {
        using var store = GetDocumentStore();
        await SeedAccountAsync(store);
        var transferred = new Repository
        {
            GitHubId = 502, Account = Account.DocumentId(EForgeProvider.GitHub, 9999), Name = "tool",
            FullName = "previous-owner/tool", OwnerLogin = "previous-owner",
        };
        transferred.MarkDisconnected(DisconnectedReasons.RemovedFromInstallation);
        await SeedAsync(store, transferred);

        await ReconcileAsync(store, new ScriptedRepositories(Live(502, "acme/tool")), new ScriptedProjects());

        var adopted = await LoadAsync<Repository>(store, Repository.DocumentId(EForgeProvider.GitHub, 502));
        adopted.Account.Should().Be(AccountId);
        adopted.FullName.Should().Be("acme/tool");
        adopted.OwnerLogin.Should().Be("acme");
        adopted.PreviousFullNames.Should().Equal("previous-owner/tool");
        adopted.Connection.Should().Be(RepositoryConnection.Connected);
    }

    /// <summary>A name already remembered is not remembered twice.</summary>
    [Fact]
    public async Task A_rename_back_to_a_remembered_name_does_not_duplicate_the_memo()
    {
        using var store = GetDocumentStore();
        await SeedAccountAsync(store);
        await SeedAsync(store, new Repository
        {
            GitHubId = 503, Account = AccountId, Name = "old", FullName = "acme/old", OwnerLogin = "acme",
            PreviousFullNames = ["acme/old"],
        });

        await ReconcileAsync(store, new ScriptedRepositories(Live(503, "acme/new")), new ScriptedProjects());

        (await LoadAsync<Repository>(store, Repository.DocumentId(EForgeProvider.GitHub, 503))).PreviousFullNames
            .Should().Equal("acme/old");
    }

    /// <summary>Uninstalled: every repository AND every board of the account is disconnected, and the installation forgotten.</summary>
    [Fact]
    public async Task An_installation_that_is_gone_disconnects_the_accounts_boards_too()
    {
        using var store = GetDocumentStore();
        await SeedAccountAsync(store);
        await SeedAsync(store, new GitHubProject { NodeId = "PVT_gone", Account = AccountId, OwnerLogin = "acme", Number = 1, Name = "Roadmap" });

        await ReconcileAsync(store,
            new ScriptedRepositories { Throws = new NotFoundException("gone", System.Net.HttpStatusCode.NotFound) },
            new ScriptedProjects());

        (await LoadAsync<GitHubProject>(store, GitHubProject.DocumentId("PVT_gone"))).Connection
            .Should().Be(RepositoryConnection.Disconnected);
        (await LoadAsync<Account>(store, AccountId)).InstallationId.HasValue.Should().BeFalse();
    }

    [Theory]
    [InlineData("Organization", true)]
    [InlineData("User", false)]
    public async Task Boards_are_listed_through_the_owner_type_the_account_has(string type, bool expectOrganization)
    {
        using var store = GetDocumentStore();
        await SeedAccountAsync(store, type);
        var projects = new ScriptedProjects();

        await ReconcileAsync(store, new ScriptedRepositories(), projects);

        projects.ListCalls.Should().Equal(("acme", expectOrganization));
    }

    /// <summary>
    /// Discovery upserts by node id: a new board is created, a known one updated in place — its
    /// user-owned configuration untouched — and a board no longer listed is disconnected.
    /// </summary>
    [Fact]
    public async Task Board_discovery_upserts_by_node_id_and_disconnects_what_went_missing()
    {
        using var store = GetDocumentStore();
        await SeedAccountAsync(store);
        await SeedAsync(store,
            new GitHubProject
            {
                NodeId = "PVT_known", Account = AccountId, OwnerLogin = "acme", Number = 1, Name = "Old title",
                EventMappings = [new EventColumnMapping { Id = "PullRequestOpened", EventType = "PullRequestOpened", TargetColumnOptionId = "opt" }],
            },
            new GitHubProject { NodeId = "PVT_removed", Account = AccountId, OwnerLogin = "acme", Number = 2, Name = "Removed" });
        var projects = new ScriptedProjects
        {
            Boards = [new InstallationProject("PVT_known", 1, "New title", false), new InstallationProject("PVT_new", 3, "Fresh", false)],
        };

        await ReconcileAsync(store, new ScriptedRepositories(), projects);

        var known = await LoadAsync<GitHubProject>(store, GitHubProject.DocumentId("PVT_known"));
        known.Name.Should().Be("New title");
        known.InstallationId.Should().Be(InstallationId);
        known.EventMappings.Should().ContainSingle("discovery never touches the user's rules");
        var fresh = await LoadAsync<GitHubProject>(store, GitHubProject.DocumentId("PVT_new"));
        fresh.Account.Should().Be(AccountId);
        fresh.Number.Should().Be(3);
        fresh.AutomationEnabled.Should().BeFalse();
        (await LoadAsync<GitHubProject>(store, GitHubProject.DocumentId("PVT_removed"))).Connection
            .Should().Be(RepositoryConnection.Disconnected);
        projects.StatusCalls.Should().BeEmpty("columns are refreshed only for boards with automation switched on");
    }

    [Fact]
    public async Task A_board_the_installation_cannot_see_is_disconnected()
    {
        using var store = GetDocumentStore();
        await SeedAccountAsync(store);
        await SeedAsync(store, new GitHubProject { NodeId = "PVT_x", Account = AccountId, OwnerLogin = "acme", Number = 1, Name = "X" });

        await ReconcileAsync(store, new ScriptedRepositories(),
            new ScriptedProjects { ListThrows = new NotFoundException("no boards", System.Net.HttpStatusCode.NotFound) });

        (await LoadAsync<GitHubProject>(store, GitHubProject.DocumentId("PVT_x"))).Connection
            .Should().Be(RepositoryConnection.Disconnected);
    }

    /// <summary>A transient board failure changes nothing and does not undo the repository reconcile.</summary>
    [Fact]
    public async Task A_transient_board_failure_changes_nothing_and_keeps_the_repository_reconcile()
    {
        using var store = GetDocumentStore();
        await SeedAccountAsync(store);
        await SeedAsync(store, new GitHubProject { NodeId = "PVT_y", Account = AccountId, OwnerLogin = "acme", Number = 1, Name = "Y" });

        await ReconcileAsync(store, new ScriptedRepositories(Live(504, "acme/lib")),
            new ScriptedProjects { ListThrows = new ApiException("bad gateway", System.Net.HttpStatusCode.BadGateway) });

        (await LoadAsync<GitHubProject>(store, GitHubProject.DocumentId("PVT_y"))).Connection
            .Should().Be(RepositoryConnection.Connected);
        (await LoadAsync<Repository>(store, Repository.DocumentId(EForgeProvider.GitHub, 504))).FullName
            .Should().Be("acme/lib");
    }

    private static GitHubProject AutomatedBoard(string nodeId, params EventColumnMapping[] rules) => new()
    {
        NodeId = nodeId, Account = AccountId, OwnerLogin = "acme", Number = 1, Name = "Automated",
        AutomationEnabled = true, StatusFieldId = "old-field",
        Columns = [new ProjectColumn { Id = "stale", Name = "Stale" }],
        EventMappings = [.. rules],
    };

    /// <summary>An automated board's columns are refreshed nightly; a rule pointing at a column that is gone stays, and is logged.</summary>
    [Fact]
    public async Task An_automated_boards_columns_are_refreshed()
    {
        using var store = GetDocumentStore();
        await SeedAccountAsync(store);
        await SeedAsync(store, AutomatedBoard("PVT_auto",
            new EventColumnMapping { Id = "PullRequestOpened", EventType = "PullRequestOpened", TargetColumnOptionId = "stale" }));
        var projects = new ScriptedProjects
        {
            Boards = [new InstallationProject("PVT_auto", 1, "Automated", false)],
            StatusFields = { ["PVT_auto"] = new ProjectStatusField("field-1", [new("todo", "Todo"), new("done", "Done")]) },
        };

        await ReconcileAsync(store, new ScriptedRepositories(), projects);

        var board = await LoadAsync<GitHubProject>(store, GitHubProject.DocumentId("PVT_auto"));
        board.StatusFieldId.Should().Be("field-1");
        board.Columns.Select(c => c.Name).Should().Equal("Todo", "Done");
        board.ColumnsSyncedAtUtc.HasValue.Should().BeTrue();
        board.EventMappings.Should().ContainSingle("an orphaned rule is reported, not deleted");
    }

    [Fact]
    public async Task An_automated_board_without_a_Status_field_has_its_columns_cleared()
    {
        using var store = GetDocumentStore();
        await SeedAccountAsync(store);
        await SeedAsync(store, AutomatedBoard("PVT_nostatus"));
        var projects = new ScriptedProjects { Boards = [new InstallationProject("PVT_nostatus", 1, "Automated", false)] };

        await ReconcileAsync(store, new ScriptedRepositories(), projects);

        var board = await LoadAsync<GitHubProject>(store, GitHubProject.DocumentId("PVT_nostatus"));
        board.StatusFieldId.Should().BeNull();
        board.Columns.Should().BeEmpty();
        board.ColumnsSyncedAtUtc.HasValue.Should().BeTrue();
    }

    [Fact]
    public async Task A_column_refresh_that_fails_keeps_the_cached_columns()
    {
        using var store = GetDocumentStore();
        await SeedAccountAsync(store);
        await SeedAsync(store, AutomatedBoard("PVT_flaky"));
        var projects = new ScriptedProjects
        {
            Boards = [new InstallationProject("PVT_flaky", 1, "Automated", false)],
            StatusThrows = new HttpRequestException("timeout"),
        };

        await ReconcileAsync(store, new ScriptedRepositories(), projects);

        var board = await LoadAsync<GitHubProject>(store, GitHubProject.DocumentId("PVT_flaky"));
        board.StatusFieldId.Should().Be("old-field");
        board.Columns.Select(c => c.Id).Should().Equal("stale");
    }
}
