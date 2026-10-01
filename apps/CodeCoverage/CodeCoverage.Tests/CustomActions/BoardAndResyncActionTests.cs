using CodeCoverage.CustomActions;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;
using ProjectColumn = CodeCoverage.Entities.ProjectColumn;

namespace CodeCoverage.Tests.CustomActions;

/// <summary>
/// The two buttons that ask GitHub for the current truth: <c>SyncColumns</c> on a board and
/// <c>Resync</c> on the home page.
/// </summary>
/// <remarks>
/// Both share one promise, and it is what is pinned here: a GitHub failure never destroys cached
/// state and never turns the click into an error page. The user is told what happened instead.
/// </remarks>
public class BoardAndResyncActionTests : CoverageRavenTest
{
    protected override bool DeployIndexes => true;

    private const string BoardId = "GitHubProjects/PVT_sync";

    private sealed record Harness(SyncColumnsAction Action, IClientAccessor Client, IInstallationProjects Projects, IDatabaseAccess Database);

    private static Harness CreateSync(IAsyncDocumentSession session)
    {
        var client = Substitute.For<IClientAccessor>();
        var manager = Substitute.For<IManager>();
        manager.Client.Returns(client);
        var projects = Substitute.For<IInstallationProjects>();
        var database = Substitute.For<IDatabaseAccess>();
        database.GetPersistentObjectAsync(Arg.Any<Guid>(), Arg.Any<string>())
            .Returns(Task.FromResult<PersistentObject?>(new PersistentObject { Id = BoardId, Name = "GitHubProject", ObjectTypeId = Guid.Empty }));

        var action = new SyncColumnsAction(projects, session, NullLogger<SyncColumnsAction>.Instance, manager, database);
        return new Harness(action, client, projects, database);
    }

    private static CustomActionArgs BoardArgs(string? id = BoardId) => new()
    {
        Parent = new PersistentObject { Id = id, Name = "GitHubProject", ObjectTypeId = Guid.Empty, Attributes = [] },
    };

    private static async Task SeedBoardAsync(IDocumentStore store, RepositoryConnection connection = RepositoryConnection.Connected)
    {
        using var seed = store.OpenAsyncSession();
        await seed.StoreAsync(new GitHubProject
        {
            NodeId = "PVT_sync", Number = 9, Name = "Roadmap", InstallationId = 5, OwnerLogin = "acme",
            Connection = connection,
            DisconnectedReason = connection == RepositoryConnection.Disconnected ? "IntegrationRemoved" : null,
            StatusFieldId = "cached-field",
            Columns = [new ProjectColumn { Id = "cached", Name = "Cached" }],
            EventMappings =
            [
                new EventColumnMapping { Id = "PullRequestOpened", EventType = "PullRequestOpened", TargetColumnOptionId = "todo" },
                new EventColumnMapping { Id = "PullRequestMerged", EventType = "PullRequestMerged", TargetColumnOptionId = "gone" },
                new EventColumnMapping { Id = "IssueClosed", EventType = "IssueClosed", TargetColumnOptionId = "gone", Enabled = false },
            ],
        }, BoardId);
        await seed.SaveChangesAsync();
    }

    private static async Task<GitHubProject> LoadBoardAsync(IDocumentStore store)
    {
        using var session = store.OpenAsyncSession();
        return (await session.LoadAsync<GitHubProject>(BoardId))!;
    }

    [Fact]
    public async Task Without_a_board_in_context_there_is_nothing_to_sync()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var h = CreateSync(session);

        await h.Action.ExecuteAsync(new CustomActionArgs());
        await h.Action.ExecuteAsync(BoardArgs(id: null));

        h.Client.Received(2).Notify(Arg.Is<string>(m => m.Contains("No board in context")), NotificationKind.Warning);
    }

    [Fact]
    public async Task A_board_that_no_longer_exists_is_reported()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var h = CreateSync(session);

        await h.Action.ExecuteAsync(BoardArgs());

        h.Client.Received(1).Notify("That board no longer exists.", NotificationKind.Error);
    }

    [Fact]
    public async Task A_disconnected_board_is_not_sent_to_GitHub()
    {
        using var store = GetDocumentStore();
        await SeedBoardAsync(store, RepositoryConnection.Disconnected);
        using var session = store.OpenAsyncSession();
        var h = CreateSync(session);

        await h.Action.ExecuteAsync(BoardArgs());

        await h.Projects.DidNotReceiveWithAnyArgs().GetStatusFieldAsync(default, default!, default);
        h.Client.Received(1).Notify(Arg.Is<string>(m => m.Contains("is disconnected (IntegrationRemoved)")), NotificationKind.Warning);
    }

    /// <summary>Stale columns are usable; empty ones would make every rule report a missing target.</summary>
    [Fact]
    public async Task A_GitHub_failure_leaves_the_cached_columns_alone()
    {
        using var store = GetDocumentStore();
        await SeedBoardAsync(store);
        using var session = store.OpenAsyncSession();
        var h = CreateSync(session);
        h.Projects.GetStatusFieldAsync(5, "PVT_sync", Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("timeout"));

        await h.Action.ExecuteAsync(BoardArgs());

        var board = await LoadBoardAsync(store);
        board.StatusFieldId.Should().Be("cached-field");
        board.Columns.Select(c => c.Id).Should().Equal("cached");
        h.Client.Received(1).Notify(Arg.Is<string>(m => m.Contains("could not be reached")), NotificationKind.Error);
    }

    [Fact]
    public async Task A_board_without_a_Status_field_has_its_columns_cleared_and_says_so()
    {
        using var store = GetDocumentStore();
        await SeedBoardAsync(store);
        using var session = store.OpenAsyncSession();
        var h = CreateSync(session);
        h.Projects.GetStatusFieldAsync(5, "PVT_sync", Arg.Any<CancellationToken>()).Returns(Task.FromResult(ProjectStatusField.None));

        await h.Action.ExecuteAsync(BoardArgs());

        var board = await LoadBoardAsync(store);
        board.StatusFieldId.Should().BeNull();
        board.Columns.Should().BeEmpty();
        h.Client.Received(1).Notify(Arg.Is<string>(m => m.Contains("has no Status field")), NotificationKind.Warning);
        h.Client.Received().RefreshAttribute(Arg.Any<PersistentObject>(), nameof(GitHubProject.Columns));
    }

    /// <summary>A rule whose target column is gone is reported by event name, and kept — the rules are the user's.</summary>
    [Fact]
    public async Task Rules_targeting_a_column_that_is_gone_are_reported_not_deleted()
    {
        using var store = GetDocumentStore();
        await SeedBoardAsync(store);
        using var session = store.OpenAsyncSession();
        var h = CreateSync(session);
        h.Projects.GetStatusFieldAsync(5, "PVT_sync", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProjectStatusField("field-2", [new("todo", "Todo"), new("done", "Done")])));

        await h.Action.ExecuteAsync(BoardArgs());

        var board = await LoadBoardAsync(store);
        board.StatusFieldId.Should().Be("field-2");
        board.Columns.Select(c => c.Name).Should().Equal("Todo", "Done");
        board.EventMappings.Should().HaveCount(3);
        // The disabled rule also points at "gone", but a disabled rule cannot misfire.
        h.Client.Received(1).Notify(
            Arg.Is<string>(m => m.Contains("1 enabled rule(s)") && m.Contains("PullRequestMerged") && !m.Contains("IssueClosed")),
            NotificationKind.Warning);
    }

    [Fact]
    public async Task A_clean_sync_reports_success_and_refreshes_the_board()
    {
        using var store = GetDocumentStore();
        await SeedBoardAsync(store);
        using (var edit = store.OpenAsyncSession())
        {
            var seeded = (await edit.LoadAsync<GitHubProject>(BoardId))!;
            seeded.EventMappings.RemoveAll(m => m.TargetColumnOptionId == "gone");
            await edit.SaveChangesAsync();
        }
        using var session = store.OpenAsyncSession();
        var h = CreateSync(session);
        h.Projects.GetStatusFieldAsync(5, "PVT_sync", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProjectStatusField("field-3", [new("todo", "Todo")])));

        await h.Action.ExecuteAsync(BoardArgs());

        h.Client.Received(1).Notify("Synchronized 1 column(s) for board #9.", NotificationKind.Success);
        h.Client.Received(1).RefreshAttribute(Arg.Any<PersistentObject>(), nameof(GitHubProject.StatusFieldId));
    }

    /// <summary>If the refreshed object cannot be read back, the save still stands; only the refresh is skipped.</summary>
    [Fact]
    public async Task A_board_that_cannot_be_read_back_is_saved_but_not_refreshed()
    {
        using var store = GetDocumentStore();
        await SeedBoardAsync(store);
        using var session = store.OpenAsyncSession();
        var h = CreateSync(session);
        h.Database.GetPersistentObjectAsync(Arg.Any<Guid>(), Arg.Any<string>()).Returns(Task.FromResult<PersistentObject?>(null));
        h.Projects.GetStatusFieldAsync(5, "PVT_sync", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProjectStatusField("field-4", [new("todo", "Todo")])));

        await h.Action.ExecuteAsync(BoardArgs());

        (await LoadBoardAsync(store)).StatusFieldId.Should().Be("field-4");
        h.Client.DidNotReceiveWithAnyArgs().RefreshAttribute(default!, default!);
    }

    // ------------------------------------------------------------------------------------------
    // Resync
    // ------------------------------------------------------------------------------------------

    private static IForgeIntegration ResyncForge(params string[] owners)
    {
        var forge = Substitute.For<IForgeIntegration>();
        forge.Provider.Returns(EForgeProvider.GitHub);
        forge.GetAllowedOwnersAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(owners.Select(o => new ForgeOwner(EForgeProvider.GitHub, o)).ToArray()));
        return forge;
    }

    private static IMyAccountsService MyAccounts(params int[] repoCounts)
    {
        var service = Substitute.For<IMyAccountsService>();
        service.GetAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<EForgeProvider?>())
            .Returns(Task.FromResult(new MyAccountsResult("https://example.invalid/connect",
                [.. repoCounts.Select((count, i) => new MyAccountRow($"Accounts/github/{i}", $"a{i}", "github", "User", null, count, null, true))],
                false)));
        return service;
    }

    private static PersistentObject Home() => new()
    {
        Name = "Home", ObjectTypeId = Guid.Empty,
        Attributes = [new PersistentObjectAttribute { Name = "AccountCount" }, new PersistentObjectAttribute { Name = "RepoCount" }],
    };

    private static async Task SeedAccountsAsync(IDocumentStore store)
    {
        using var seed = store.OpenAsyncSession();
        await seed.StoreAsync(new Account { GitHubId = 1, Login = "acme" }, Account.DocumentId(EForgeProvider.GitHub, 1));
        await seed.StoreAsync(new Account { GitHubId = 2, Login = "flaky" }, Account.DocumentId(EForgeProvider.GitHub, 2));
        await seed.StoreAsync(new Account { GitHubId = 3, Login = "stranger" }, Account.DocumentId(EForgeProvider.GitHub, 3));
        await seed.StoreAsync(new Account { GitHubId = 4, Login = "gone", Connection = RepositoryConnection.Disconnected },
            Account.DocumentId(EForgeProvider.GitHub, 4));
        await seed.SaveChangesAsync();
    }

    /// <summary>
    /// The button reconciles exactly the connected accounts the caller manages, survives one of
    /// them failing, and corrects the counts on the page it was pressed on.
    /// </summary>
    [Fact]
    public async Task Resync_reconciles_the_callers_accounts_and_refreshes_the_home_counts()
    {
        using var store = GetDocumentStore();
        await SeedAccountsAsync(store);
        WaitForIndexing(store);
        var forge = ResyncForge("acme", "flaky", "gone");
        forge.ReconcileAsync(Arg.Is<Account>(a => a.Login == "flaky"), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("GitHub hiccup"));
        var client = Substitute.For<IClientAccessor>();
        var manager = Substitute.For<IManager>();
        manager.Client.Returns(client);
        var home = Home();

        using (var session = store.OpenAsyncSession())
            await new ResyncAction(new SingleForgeResolver(forge), MyAccounts(3, 4), session, NullLogger<ResyncAction>.Instance, manager)
                .ExecuteAsync(new CustomActionArgs { Parent = home });

        await forge.Received(1).InvalidateAsync(Arg.Any<CancellationToken>());
        await forge.Received(2).ReconcileAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>());
        await forge.DidNotReceive().ReconcileAsync(Arg.Is<Account>(a => a.Login == "stranger" || a.Login == "gone"), Arg.Any<CancellationToken>());
        home["AccountCount"].Value.Should().Be(2);
        home["RepoCount"].Value.Should().Be(7);
        client.Received(1).RefreshQuery("my-accounts");
        client.DidNotReceiveWithAnyArgs().Notify(default(string)!, default);
    }

    /// <summary>A caller who manages nothing reconciles nothing, and a missing home page is simply not refreshed.</summary>
    [Fact]
    public async Task Resync_with_no_owners_only_refreshes_the_grid()
    {
        using var store = GetDocumentStore();
        await SeedAccountsAsync(store);
        var forge = ResyncForge();
        var client = Substitute.For<IClientAccessor>();
        var manager = Substitute.For<IManager>();
        manager.Client.Returns(client);

        using (var session = store.OpenAsyncSession())
            await new ResyncAction(new SingleForgeResolver(forge), MyAccounts(), session, NullLogger<ResyncAction>.Instance, manager)
                .ExecuteAsync(new CustomActionArgs());

        await forge.DidNotReceiveWithAnyArgs().ReconcileAsync(default!, default);
        client.DidNotReceiveWithAnyArgs().RefreshAttribute(default!, default!);
        client.Received(1).RefreshQuery("my-accounts");
    }

    /// <summary>
    /// A write conflict on save is caught and reported, not a 500. Provoked for real: the reconcile
    /// touches the account while another writer changes the same document, under optimistic
    /// concurrency.
    /// </summary>
    [Fact]
    public async Task Resync_reports_a_save_failure_instead_of_failing_the_click()
    {
        using var store = GetDocumentStore();
        await SeedAccountsAsync(store);
        WaitForIndexing(store);
        var forge = ResyncForge("acme");
        forge.ReconcileAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            call.Arg<Account>().AvatarUrl = "https://example.invalid/mine.png";
            using var other = store.OpenAsyncSession();
            (await other.LoadAsync<Account>(Account.DocumentId(EForgeProvider.GitHub, 1)))!.AvatarUrl = "https://example.invalid/theirs.png";
            await other.SaveChangesAsync();
        });
        var client = Substitute.For<IClientAccessor>();
        var manager = Substitute.For<IManager>();
        manager.Client.Returns(client);

        using (var session = store.OpenAsyncSession())
        {
            session.Advanced.UseOptimisticConcurrency = true;
            await new ResyncAction(new SingleForgeResolver(forge), MyAccounts(1), session, NullLogger<ResyncAction>.Instance, manager)
                .ExecuteAsync(new CustomActionArgs { Parent = Home() });
        }

        client.Received(1).Notify("Some accounts could not be updated. Try again in a moment.", NotificationKind.Warning);
        client.Received(1).RefreshQuery("my-accounts");
    }
}
