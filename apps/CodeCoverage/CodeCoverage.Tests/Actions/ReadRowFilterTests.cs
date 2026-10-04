using CodeCoverage.Tests._Infrastructure;
using CodeCoverage.Actions;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Queries;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Actions;

/// <summary>
/// The row rules that keep a private repository's commits, builds and boards off the generic
/// <c>/spark</c> surface.
/// </summary>
/// <remarks>
/// <c>security.json</c> grants <c>QueryRead/Commit</c> and <c>QueryRead/Build</c> to anonymous
/// visitors, so the type-level check admits everyone and these rules are the whole boundary. None
/// of them had a test: <see cref="WriteRowFilterTests"/> covers Repository and Account only.
/// <para>
/// The visibility inputs are the REAL <see cref="SparkVisibility"/>, driven by a scripted forge,
/// rather than a substituted <see cref="ISparkVisibility"/>. Its repository-id query is what every
/// one of these rules actually consumes, and a substitute would assert the rule against an answer
/// the production query might never give.
/// </para>
/// </remarks>
public class ReadRowFilterTests : CoverageRavenTest
{
    protected override bool DeployIndexes => true;

    private const long PublicRepo = 7101;
    private const long PrivateRepo = 7102;
    private const long OthersPrivateRepo = 7103;

    private static readonly string PublicCommit = Commit.DocumentId(EForgeProvider.GitHub, PublicRepo, "aaaa1111");
    private static readonly string PrivateCommit = Commit.DocumentId(EForgeProvider.GitHub, PrivateRepo, "bbbb2222");
    private static readonly string OthersCommit = Commit.DocumentId(EForgeProvider.GitHub, OthersPrivateRepo, "cccc3333");

    private static async Task SeedAsync(IDocumentStore store)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(new Repository
        {
            GitHubId = PublicRepo, Name = "open", FullName = "acme/open", OwnerLogin = "acme", IsPrivate = false,
        }, Repository.DocumentId(EForgeProvider.GitHub, PublicRepo));
        await session.StoreAsync(new Repository
        {
            GitHubId = PrivateRepo, Name = "secret", FullName = "acme/secret", OwnerLogin = "acme", IsPrivate = true,
        }, Repository.DocumentId(EForgeProvider.GitHub, PrivateRepo));
        await session.StoreAsync(new Repository
        {
            GitHubId = OthersPrivateRepo, Name = "hidden", FullName = "other/hidden", OwnerLogin = "other", IsPrivate = true,
        }, Repository.DocumentId(EForgeProvider.GitHub, OthersPrivateRepo));

        foreach (var (id, repo, sha) in new[]
                 {
                     (PublicCommit, PublicRepo, "aaaa1111"),
                     (PrivateCommit, PrivateRepo, "bbbb2222"),
                     (OthersCommit, OthersPrivateRepo, "cccc3333"),
                 })
        {
            await session.StoreAsync(new Commit
            {
                Repository = Repository.DocumentId(EForgeProvider.GitHub, repo),
                Sha = sha,
                AuthoredAt = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
            }, id);
            await session.StoreAsync(new Build { Commit = id, CiRunId = repo, CiRunAttempt = 1, Run = Build.ComposeRun(repo, 1) },
                $"{id}/builds/{repo}-1");
        }

        await session.SaveChangesAsync();
    }

    /// <summary>A real <see cref="SparkVisibility"/> over a forge that reports the given logins as the viewer's owners.</summary>
    private static SparkVisibility VisibilityFor(IAsyncDocumentSession session, params string[] logins)
    {
        var forge = Substitute.For<IForgeIntegration>();
        forge.Provider.Returns(EForgeProvider.GitHub);
        forge.GetAllowedOwnersAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(logins.Select(l => new ForgeOwner(EForgeProvider.GitHub, l)).ToArray()));

        var resolver = Substitute.For<IForgeIntegrationResolver>();
        resolver.GetLinkedProvidersAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EForgeProvider>>(logins.Length == 0 ? [] : [EForgeProvider.GitHub]));
        resolver.For(EForgeProvider.GitHub).Returns(forge);

        return new SparkVisibility(resolver, session);
    }

    /// <summary>Builds a real actions class, handing it the given visibility and session by parameter type.</summary>
    private static T CreateActions<T>(ISparkVisibility visibility, IAsyncDocumentSession session)
    {
        var ctor = typeof(T).GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var args = ctor.GetParameters()
            .Select(p =>
            {
                if (p.ParameterType == typeof(ISparkVisibility)) return visibility;
                if (p.ParameterType == typeof(IAsyncDocumentSession)) return session;
                try { return Substitute.For([p.ParameterType], null); }
                catch { return null; }
            })
            .ToArray();
        return (T)ctor.Invoke(args);
    }

    private static CustomQueryArgs ParentArgs(string parentType, string parentId) => new()
    {
        Query = new SparkQuery { Id = Guid.NewGuid(), Name = "Test", Source = "Custom.Test" },
        Parent = new PersistentObject { Id = parentId, Name = parentType, ObjectTypeId = Guid.NewGuid() },
        ParentType = parentType,
    };

    private async Task<string[]> VisibleCommitsAsync(IDocumentStore store, params string[] logins)
    {
        using var session = store.OpenAsyncSession();
        var actions = CreateActions<CommitActions>(VisibilityFor(session, logins), session);
        var filter = await actions.GetRowFilterAsync("Query");
        var rows = await session.Query<Commit>().Where(filter!).ToListAsync();
        return [.. rows.Select(c => c.Id!).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task An_anonymous_viewer_sees_only_commits_of_public_repositories()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        WaitForIndexing(store);

        (await VisibleCommitsAsync(store)).Should().Equal(PublicCommit);
    }

    [Fact]
    public async Task An_owner_also_sees_their_own_private_commits_but_nobody_elses()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        WaitForIndexing(store);

        (await VisibleCommitsAsync(store, "acme"))
            .Should().Equal(new[] { PublicCommit, PrivateCommit }.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Commit_includes_its_repository_by_default()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();

        CreateActions<CommitActions>(VisibilityFor(session), session).GetDefaultIncludes()
            .Should().Equal(nameof(Commit.Repository));
    }

    [Fact]
    public async Task Repository_Commits_is_scoped_to_its_parent_and_newest_first()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        using (var seed = store.OpenAsyncSession())
        {
            // An upload-only commit (no webhook timestamp) sorts by when it was first seen.
            await seed.StoreAsync(new Commit
            {
                Repository = Repository.DocumentId(EForgeProvider.GitHub, PublicRepo),
                Sha = "dddd4444",
                FirstSeenAtUtc = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero),
            }, Commit.DocumentId(EForgeProvider.GitHub, PublicRepo, "dddd4444"));
            await seed.SaveChangesAsync();
        }
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var actions = CreateActions<CommitActions>(VisibilityFor(session), session);
        var rows = (await actions.Repository_Commits(
            ParentArgs("Repository", Repository.DocumentId(EForgeProvider.GitHub, PublicRepo)))).ToList();

        rows.Select(c => c.Sha).Should().Equal("dddd4444", "aaaa1111");
    }

    [Fact]
    public async Task Repository_Commits_refuses_a_parent_of_the_wrong_type()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var actions = CreateActions<CommitActions>(VisibilityFor(session), session);

        await new Func<Task>(() => actions.Repository_Commits(ParentArgs("Account", "Accounts/github/1"))).Should().ThrowExactlyAsync<InvalidOperationException>();
    }

    private static Build BuildOf(string? commitId) => new() { Commit = commitId };

    [Theory]
    [InlineData(PublicRepo, new string[0], true)]
    [InlineData(PrivateRepo, new string[0], false)]
    [InlineData(PrivateRepo, new[] { "acme" }, true)]
    [InlineData(OthersPrivateRepo, new[] { "acme" }, false)]
    public async Task A_build_is_visible_exactly_when_its_repository_is(long repo, string[] logins, bool expected)
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();
        var actions = CreateActions<BuildActions>(VisibilityFor(session, logins), session);

        var commitId = Commit.DocumentId(EForgeProvider.GitHub, repo, "0123abcd");

        (await actions.IsAllowedAsync("Read", BuildOf(commitId))).Should().Be(expected);
    }

    /// <summary>
    /// The legacy <c>Commits/{repositoryId}/…</c> shape still resolves, and the fork-upload shape
    /// with its <c>pr/{n}</c> pair does too — the regression the remarks on BuildActions record was
    /// exactly a positional parse that denied every build.
    /// </summary>
    [Theory]
    [InlineData("Commits/7101/0123abcd", true)]
    [InlineData("Commits/github/7101/pr/5/0123abcd", true)]
    [InlineData("Commits/github/7102/pr/5/0123abcd", false)]
    [InlineData("Commits/github/not-a-number/0123abcd", false)]
    [InlineData("Builds/7101/0123abcd", false)]
    [InlineData(null, false)]
    public async Task A_build_whose_commit_id_does_not_name_a_visible_repository_is_denied(string? commitId, bool expected)
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();
        var actions = CreateActions<BuildActions>(VisibilityFor(session), session);

        (await actions.IsAllowedAsync("Read", BuildOf(commitId))).Should().Be(expected);
    }

    [Fact]
    public async Task Commit_Builds_lists_the_builds_of_its_parent_commit_only()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();
        var actions = CreateActions<BuildActions>(VisibilityFor(session), session);

        var builds = await actions.Commit_Builds(ParentArgs("Commit", PublicCommit)).ToListAsync();

        builds.Select(b => b.Commit).Should().Equal(PublicCommit);
        actions.GetDefaultIncludes().Should().Equal(nameof(Build.Commit));
    }

    // ------------------------------------------------------------------------------------------
    // GitHubProject
    // ------------------------------------------------------------------------------------------

    private static async Task SeedBoardsAsync(IDocumentStore store)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(new GitHubProject
        {
            Account = Account.DocumentId(EForgeProvider.GitHub, 1), OwnerLogin = "acme", NodeId = "PVT_acme", Number = 1, Name = "Roadmap",
        }, GitHubProject.DocumentId("PVT_acme"));
        await session.StoreAsync(new GitHubProject
        {
            Account = Account.DocumentId(EForgeProvider.GitHub, 2), OwnerLogin = "other", NodeId = "PVT_other", Number = 1, Name = "Backlog",
        }, GitHubProject.DocumentId("PVT_other"));
        await session.SaveChangesAsync();
    }

    [Theory]
    [InlineData("Query")]
    [InlineData("Edit")]
    public async Task A_board_is_visible_and_editable_only_by_a_viewer_who_manages_its_owner(string action)
    {
        using var store = GetDocumentStore();
        await SeedBoardsAsync(store);
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();
        var actions = CreateActions<GitHubProjectActions>(VisibilityFor(session, "acme"), session);

        var filter = await actions.GetRowFilterAsync(action);
        var boards = await session.Query<GitHubProject>().Where(filter!).ToListAsync();

        boards.Select(b => b.Name).Should().Equal("Roadmap");
    }

    [Fact]
    public async Task An_anonymous_viewer_sees_no_board_at_all()
    {
        using var store = GetDocumentStore();
        await SeedBoardsAsync(store);
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();
        var actions = CreateActions<GitHubProjectActions>(VisibilityFor(session), session);

        var filter = await actions.GetRowFilterAsync("Query");

        (await session.Query<GitHubProject>().Where(filter!).ToListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Account_Projects_lists_the_boards_of_its_parent_account()
    {
        using var store = GetDocumentStore();
        await SeedBoardsAsync(store);
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();
        var actions = CreateActions<GitHubProjectActions>(VisibilityFor(session), session);

        var boards = await actions.Account_Projects(ParentArgs("Account", Account.DocumentId(EForgeProvider.GitHub, 2))).ToListAsync();

        boards.Select(b => b.Name).Should().Equal("Backlog");
    }

    private static PersistentObject BoardObject() => new() { Name = "GitHubProject", ObjectTypeId = Guid.NewGuid() };

    [Fact]
    public async Task Saving_a_board_stamps_each_rule_id_from_its_event_type()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var actions = CreateActions<GitHubProjectActions>(VisibilityFor(session), session);
        var board = new GitHubProject
        {
            EventMappings =
            [
                new EventColumnMapping { EventType = "PullRequestOpened" },
                new EventColumnMapping { EventType = "" }, // half-filled row: left alone, not rejected
            ],
        };

        await actions.BeforeSaveAsync(BoardObject(), board);

        board.EventMappings.Select(m => m.Id).Should().Equal("PullRequestOpened", "");
    }

    [Fact]
    public async Task Saving_a_board_that_maps_one_event_twice_is_refused()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var actions = CreateActions<GitHubProjectActions>(VisibilityFor(session), session);
        var board = new GitHubProject
        {
            EventMappings =
            [
                new EventColumnMapping { EventType = "PullRequestMerged", TargetColumnOptionId = "a" },
                new EventColumnMapping { EventType = "pullrequestmerged", TargetColumnOptionId = "b" },
            ],
        };

        var ex = (await new Func<Task>(() => actions.BeforeSaveAsync(BoardObject(), board)).Should().ThrowExactlyAsync<InvalidOperationException>()).Which;

        ex.Message.Should().Contain("more than once");
    }

    // ------------------------------------------------------------------------------------------
    // SparkVisibility itself
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Owners come back as provider-qualified KEYS, deduplicated, and a linked provider with no
    /// registered integration is skipped rather than failing the request.
    /// </summary>
    [Fact]
    public async Task Allowed_owners_are_keys_from_every_linked_forge_deduplicated()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();

        var github = Substitute.For<IForgeIntegration>();
        github.GetAllowedOwnersAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new[]
        {
            new ForgeOwner(EForgeProvider.GitHub, "acme"),
            new ForgeOwner(EForgeProvider.GitHub, "ACME"),
        }));
        var resolver = Substitute.For<IForgeIntegrationResolver>();
        resolver.GetLinkedProvidersAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EForgeProvider>>([EForgeProvider.GitHub, EForgeProvider.GitLab]));
        resolver.For(EForgeProvider.GitHub).Returns(github);
        resolver.For(EForgeProvider.GitLab).Returns((IForgeIntegration?)null);

        var visibility = new SparkVisibility(resolver, session);

        (await visibility.GetAllowedOwnersAsync()).Should().Equal("github:acme");
        (await visibility.CanManageOwnerAsync("GITHUB:acme")).Should().BeTrue();
        (await visibility.CanManageOwnerAsync("gitlab:acme")).Should().BeFalse();

        // Memoized per request: a second ask does not go back to the forge.
        await visibility.GetAllowedOwnersAsync();
        await resolver.Received(1).GetLinkedProvidersAsync(Arg.Any<CancellationToken>());
    }
}
