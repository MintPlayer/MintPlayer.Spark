using CodeCoverage.Tests._Infrastructure;
using CodeCoverage.Actions;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using System.Security.Claims;
using Xunit;
using ProjectColumn = CodeCoverage.Entities.ProjectColumn;

namespace CodeCoverage.Tests.Actions;

/// <summary>
/// The actions classes nothing else reaches: the two virtual pages (Home, ForgeAccounts), the
/// generated account rows, the protected-attribute rules, and the parent-scoped custom queries.
/// </summary>
/// <remarks>
/// Each class is built through its generated constructor with the dependencies a case cares about
/// and substitutes for the rest, the same way <see cref="WriteRowFilterTests"/> does.
/// </remarks>
public class RemainingActionsTests : CoverageRavenTest
{
    protected override bool DeployIndexes => true;

    /// <summary>Builds <typeparamref name="T"/>, taking each constructor argument from <paramref name="deps"/> when one fits.</summary>
    private static T Create<T>(params object[] deps)
    {
        var ctor = typeof(T).GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var args = ctor.GetParameters()
            .Select(p =>
            {
                var supplied = deps.FirstOrDefault(p.ParameterType.IsInstanceOfType);
                if (supplied is not null) return supplied;
                // An unauthenticated context unless a case supplies one: an auto-substituted
                // HttpContext would send ApiTokenActions' create path into UserManager.
                if (p.ParameterType == typeof(IHttpContextAccessor))
                {
                    var accessor = Substitute.For<IHttpContextAccessor>();
                    accessor.HttpContext.Returns((HttpContext?)null);
                    return accessor;
                }
                try { return Substitute.For([p.ParameterType], null); }
                catch { return null; }
            })
            .ToArray();
        return (T)ctor.Invoke(args);
    }

    private static PersistentObject Page(string name, params string[] attributes) => new()
    {
        Name = name,
        ObjectTypeId = Guid.NewGuid(),
        Attributes = [.. attributes.Select(a => new PersistentObjectAttribute { Name = a })],
    };

    private static IMyAccountsService MyAccounts(params int[] repoCounts)
    {
        var service = Substitute.For<IMyAccountsService>();
        service.GetAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<EForgeProvider?>())
            .Returns(Task.FromResult(new MyAccountsResult("https://example.invalid/connect",
                [.. repoCounts.Select((count, i) => new MyAccountRow($"Accounts/github/{i}", $"a{i}", "github", "User", null, count, null, true))],
                false)));
        return service;
    }

    private static ITranslationsLoader Translations(params (string Key, string English)[] entries)
    {
        var loader = Substitute.For<ITranslationsLoader>();
        loader.Resolve(Arg.Any<string>()).Returns((TranslatedString?)null);
        foreach (var (key, english) in entries)
            loader.Resolve(key).Returns(TranslatedString.Create(english));
        return loader;
    }

    private static IRequestCultureResolver English()
    {
        var culture = Substitute.For<IRequestCultureResolver>();
        culture.GetCurrentCulture().Returns("en");
        return culture;
    }

    private static IManager ManagerServing(PersistentObject page, IClientAccessor? client = null)
    {
        var manager = Substitute.For<IManager>();
        manager.GetPersistentObjectAsync(page.Name, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(page);
        manager.Client.Returns(client ?? Substitute.For<IClientAccessor>());
        return manager;
    }

    private static IHttpContextAccessor Signed(bool authenticated)
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(new DefaultHttpContext
        {
            User = new ClaimsPrincipal(authenticated ? new ClaimsIdentity([new Claim(ClaimTypes.Name, "u")], "test") : new ClaimsIdentity()),
        });
        return accessor;
    }

    // ------------------------------------------------------------------------------------------
    // HomeActions
    // ------------------------------------------------------------------------------------------

    /// <remarks>
    /// The counts are not hidden here any more: <c>security.json</c> denies them to the anonymous
    /// role, and <c>IManager</c> builds the page without them for that caller (#264, D13a; proven over
    /// HTTP by <c>AttributeDenialEndToEndTests</c>). The hook only skips computing them.
    /// </remarks>
    [Fact]
    public async Task An_anonymous_home_page_is_titled_prompts_to_sign_in_and_counts_nothing()
    {
        var page = Page("Home", "Subtitle", "AccountCount", "RepoCount");
        var myAccounts = MyAccounts(4);
        var actions = Create<HomeActions>(ManagerServing(page), myAccounts, English(), Signed(false),
            Translations(("app.welcomeTitle", "Coverage"), ("app.welcomeSubtitle", "Line coverage."), ("app.signInPrompt", "Sign in.")));

        var home = (await actions.OnLoadAsync("home", null))!;

        home.Breadcrumb.Should().Be("Coverage");
        home["Subtitle"].Value.Should().Be("Line coverage. Sign in.");
        home["AccountCount"].Value.Should().BeNull();
        home["RepoCount"].Value.Should().BeNull();
        await myAccounts.DidNotReceiveWithAnyArgs().GetAsync(default);
    }

    [Fact]
    public async Task A_signed_in_home_page_counts_the_callers_accounts_and_repositories()
    {
        var page = Page("Home", "Subtitle", "AccountCount", "RepoCount");
        var actions = Create<HomeActions>(ManagerServing(page), MyAccounts(4, 6), English(), Signed(true),
            Translations(("app.welcomeSubtitle", "Line coverage.")));

        var home = (await actions.OnLoadAsync("home", null))!;

        home.Breadcrumb.Should().Be("", "a missing translation renders empty rather than throwing");
        home["Subtitle"].Value.Should().Be("Line coverage.");
        home["AccountCount"].Value.Should().Be(2);
        home["RepoCount"].Value.Should().Be(10);
    }

    // ------------------------------------------------------------------------------------------
    // ForgeAccountsActions
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_forge_accounts_page_for_an_unknown_forge_does_not_exist()
    {
        var actions = Create<ForgeAccountsActions>(ManagerServing(Page("ForgeAccounts")), MyAccounts(), English(), Translations());

        (await actions.OnLoadAsync("nosuchforge", null)).Should().BeNull();
    }

    [Theory]
    [InlineData("Accounts on {0}", "Accounts on GitLab")]
    [InlineData(null, "GitLab")]
    public async Task A_forge_accounts_page_is_titled_from_the_template_or_the_forge_name(string? template, string expected)
    {
        var page = Page("ForgeAccounts", "Provider", "AccountCount", "RepoCount");
        var myAccounts = MyAccounts(3);
        var translations = template is null ? Translations() : Translations(("app.forgeAccountsTitle", template));
        var actions = Create<ForgeAccountsActions>(ManagerServing(page), myAccounts, English(), translations);

        var result = (await actions.OnLoadAsync("gitlab", null))!;

        result.Id.Should().Be("gitlab", "the grid below is scoped by the page's id");
        result["Provider"].Value.Should().Be("gitlab");
        result.Breadcrumb.Should().Be(expected);
        result["AccountCount"].Value.Should().Be(1);
        result["RepoCount"].Value.Should().Be(3);
        await myAccounts.Received(1).GetAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), EForgeProvider.GitLab);
    }

    // ------------------------------------------------------------------------------------------
    // MyAccountRowActions
    // ------------------------------------------------------------------------------------------

    private static CustomQueryArgs QueryArgs(PersistentObject? parent, string? parentType = null) => new()
    {
        Query = new SparkQuery { Id = Guid.NewGuid(), Name = "MyAccounts", Source = "Custom.MyAccounts" },
        Parent = parent,
        ParentType = parentType ?? parent?.Name,
    };

    /// <summary>
    /// Narrowed to one forge only on that forge's page, and only when the page's id names a forge it
    /// knows; everywhere else the merged list. By the id and not the Provider attribute: the parent
    /// is rebuilt for the caller, who is Read-denied Provider, so the attribute is absent (D13a).
    /// </summary>
    [Theory]
    [InlineData(null, null, null)]
    [InlineData("Home", "gitlab", null)]
    [InlineData("ForgeAccounts", "gitlab", EForgeProvider.GitLab)]
    [InlineData("ForgeAccounts", "nosuchforge", null)]
    [InlineData("ForgeAccounts", null, null)]
    public async Task My_accounts_narrow_to_a_forge_only_on_its_own_page(string? parentName, string? provider, EForgeProvider? expected)
    {
        var myAccounts = MyAccounts(1, 2);
        var actions = Create<MyAccountRowActions>(myAccounts);
        PersistentObject? parent = null;
        if (parentName is not null)
        {
            parent = Page(parentName);
            parent.Id = provider;
        }

        var rows = (await actions.MyAccounts(QueryArgs(parent))).ToList();

        rows.Should().HaveCount(2);
        await myAccounts.Received(1).GetAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), expected);
    }

    /// <summary>
    /// The account type no longer ships as a column nobody draws (#264, G-Q4): an organisation
    /// without an avatar gets the group marker in its avatar cell, and every other row is untouched.
    /// </summary>
    [Theory]
    [InlineData("Organization", null, MyAccountRowActions.GroupAvatar)]
    [InlineData("group", "", MyAccountRowActions.GroupAvatar)]
    [InlineData("WORKSPACE", null, MyAccountRowActions.GroupAvatar)]
    [InlineData("Organization", "https://avatars.example.invalid/1", "https://avatars.example.invalid/1")]
    [InlineData("User", null, null)]
    [InlineData("Bot", null, null)]
    public void An_organisation_without_an_avatar_gets_the_group_marker(string type, string? avatarUrl, string? expected)
    {
        var row = new MyAccountRow("github:acme", "acme", "github", type, avatarUrl, 0, null, true);

        MyAccountRowActions.WithAvatarFallback(row).AvatarUrl.Should().Be(expected);
    }

    /// <summary>Every class that opts out of framework row security must say why, in words a reviewer can check.</summary>
    [Fact]
    public void Every_self_securing_actions_class_states_its_rationale()
    {
        ISparkOwnsRowSecurity[] owners =
        [
            Create<MyAccountRowActions>(),
            Create<AccountActions>(),
            Create<ApiTokenActions>(),
            Create<ProjectColumnActions>(),
        ];

        foreach (var owner in owners)
            owner.RowSecurityRationale.Length.Should().BeGreaterThan(100, owner.GetType().Name);
    }

    // ------------------------------------------------------------------------------------------
    // Protected attributes
    // ------------------------------------------------------------------------------------------

    private static ISparkVisibility Managing(params string[] ownerKeys)
    {
        var visibility = Substitute.For<ISparkVisibility>();
        visibility.GetAllowedOwnersAsync().Returns(Task.FromResult(ownerKeys));
        visibility.CanManageOwnerAsync(Arg.Any<string>())
            .Returns(call => Task.FromResult(ownerKeys.Contains(call.Arg<string>(), StringComparer.OrdinalIgnoreCase)));
        return visibility;
    }

    /// <summary>
    /// The installation id is no longer withheld per row: it is <c>[IgnoreProperty]</c>, so it is in
    /// no model and on no wire for anyone (#264), and the hook has nothing left to protect.
    /// </summary>
    [Fact]
    public async Task An_account_protects_no_attribute_per_row()
    {
        var account = new Account { Login = "acme", InstallationId = 5 };

        (await Create<AccountActions>(Managing("github:other")).GetProtectedAttributesAsync("Read", account)).Should().BeNull();
    }

    [Fact]
    public async Task A_repositorys_badge_token_is_withheld_from_anyone_who_does_not_manage_it()
    {
        var repository = new Repository { OwnerLogin = "acme", BadgeToken = "t" };

        (await Create<RepositoryActions>(Managing("github:acme")).GetProtectedAttributesAsync("Read", repository)).Should().BeNull();
        (await Create<RepositoryActions>(Managing()).GetProtectedAttributesAsync("Read", repository))
            .Should().Equal(nameof(Repository.BadgeToken));
        Create<RepositoryActions>(Managing()).GetDefaultIncludes().Should().Equal(nameof(Repository.Account));
    }

    // ------------------------------------------------------------------------------------------
    // Parent-scoped custom queries
    // ------------------------------------------------------------------------------------------

    private static async Task SeedRepositoriesAsync(IDocumentStore store)
    {
        using var seed = store.OpenAsyncSession();
        await seed.StoreAsync(new Repository
        {
            GitHubId = 1, Name = "mine", FullName = "acme/mine", OwnerLogin = "acme", Account = Account.DocumentId(EForgeProvider.GitHub, 1),
        }, Repository.DocumentId(EForgeProvider.GitHub, 1));
        await seed.StoreAsync(new Repository
        {
            GitHubId = 2, Name = "theirs", FullName = "other/theirs", OwnerLogin = "other", Account = Account.DocumentId(EForgeProvider.GitHub, 2),
        }, Repository.DocumentId(EForgeProvider.GitHub, 2));
        await seed.StoreAsync(new Account { GitHubId = 1, Login = "acme" }, Account.DocumentId(EForgeProvider.GitHub, 1));
        await seed.StoreAsync(new Account { GitHubId = 2, Login = "other" }, Account.DocumentId(EForgeProvider.GitHub, 2));
        await seed.SaveChangesAsync();
    }

    /// <summary>The token picker's options for <paramref name="parent"/>, as the caller managing only <c>github:acme</c>.</summary>
    private async Task<List<string>> SelectableRepositoriesAsync(PersistentObject? parent)
    {
        using var store = GetDocumentStore();
        await SeedRepositoriesAsync(store);
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();

        var offered = await Create<RepositoryActions>(Managing("github:acme"), session)
            .ApiToken_SelectableRepositories(QueryArgs(parent));
        // An unresolvable parent answers with an in-memory empty sequence, which RavenDB's
        // ToListAsync cannot materialize.
        var rows = offered is IRavenQueryable<Repository> raven ? await raven.ToListAsync() : offered.ToList();
        return [.. rows.Select(r => r.FullName)];
    }

    // The New form forwards the Upload tokens card's parent: an Account.
    [Fact]
    public async Task Token_picker_under_an_account_offers_that_accounts_repositories()
        => (await SelectableRepositoriesAsync(new PersistentObject
        {
            Id = Account.DocumentId(EForgeProvider.GitHub, 1), Name = "Account", ObjectTypeId = Guid.NewGuid(),
        })).Should().Equal("acme/mine");

    // The edit form's parent is the token itself — the case that used to 500 through EnsureParent.
    [Fact]
    public async Task Token_picker_on_a_token_offers_its_accounts_repositories()
    {
        var token = Page("ApiToken", nameof(ApiToken.Account));
        token.Id = "ApiTokens/1";
        token[nameof(ApiToken.Account)].Value = Account.DocumentId(EForgeProvider.GitHub, 1);

        (await SelectableRepositoriesAsync(token)).Should().Equal("acme/mine");
    }

    [Fact]
    public async Task Token_picker_offers_nothing_for_an_account_the_caller_does_not_manage()
        => (await SelectableRepositoriesAsync(new PersistentObject
        {
            Id = Account.DocumentId(EForgeProvider.GitHub, 2), Name = "Account", ObjectTypeId = Guid.NewGuid(),
        })).Should().BeEmpty();

    [Fact]
    public async Task Token_picker_offers_nothing_without_a_parent()
        => (await SelectableRepositoriesAsync(null)).Should().BeEmpty();

    [Fact]
    public async Task Account_Repositories_lists_the_repositories_of_its_parent_account()
    {
        using var store = GetDocumentStore();
        await SeedRepositoriesAsync(store);
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();
        var parent = new PersistentObject { Id = Account.DocumentId(EForgeProvider.GitHub, 2), Name = "Account", ObjectTypeId = Guid.NewGuid() };

        var listed = await Create<RepositoryActions>(Managing(), session).Account_Repositories(QueryArgs(parent)).ToListAsync();

        listed.Select(r => r.FullName).Should().Equal("other/theirs");
    }

    [Fact]
    public async Task Project_Columns_offers_the_parent_boards_columns_and_nothing_elsewhere()
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new GitHubProject
            {
                NodeId = "PVT_cols", Columns = [new ProjectColumn { Id = "a", Name = "Todo" }, new ProjectColumn { Id = "b", Name = "Done" }],
            }, GitHubProject.DocumentId("PVT_cols"));
            await seed.SaveChangesAsync();
        }
        using var session = store.OpenAsyncSession();
        var actions = Create<ProjectColumnActions>(session);
        var board = new PersistentObject { Id = GitHubProject.DocumentId("PVT_cols"), Name = "GitHubProject", ObjectTypeId = Guid.NewGuid() };
        var missing = new PersistentObject { Id = GitHubProject.DocumentId("PVT_none"), Name = "GitHubProject", ObjectTypeId = Guid.NewGuid() };

        (await actions.Project_Columns(QueryArgs(board))).Select(c => c.Name).Should().Equal("Todo", "Done");
        (await actions.Project_Columns(QueryArgs(missing))).Should().BeEmpty();
        (await actions.Project_Columns(QueryArgs(null))).Should().BeEmpty();
        (await actions.Project_Columns(QueryArgs(board, parentType: "Account"))).Should().BeEmpty();
    }

    // ------------------------------------------------------------------------------------------
    // ApiTokenActions
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// The plaintext goes to the caller exactly once, after the save, as a notification — it is
    /// deliberately never a field. A second save (an edit) shows nothing.
    /// </summary>
    [Fact]
    public async Task A_new_tokens_plaintext_is_shown_once_after_the_save()
    {
        using var store = GetDocumentStore();
        var accountId = Account.DocumentId(EForgeProvider.GitHub, 1);
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Account { GitHubId = 1, Login = "acme" }, accountId);
            await seed.SaveChangesAsync();
        }
        using var session = store.OpenAsyncSession();
        var client = Substitute.For<IClientAccessor>();
        var actions = Create<ApiTokenActions>(Managing("github:acme"), session, ManagerServing(Page("ApiToken"), client));
        var token = new ApiToken { Description = "ci" };
        var po = Page("ApiToken");
        po.Parent = new PersistentObject { Id = accountId, Name = nameof(Account), ObjectTypeId = Guid.NewGuid() };

        await actions.BeforeSaveAsync(po, token);
        await actions.AfterSaveAsync(po, token);
        await actions.AfterSaveAsync(po, token);

        token.Hash.Should().NotBeNullOrEmpty();
        token.Account.Should().Be(accountId);
        client.Received(1).Notify(Arg.Is<string>(m => m.StartsWith("Copy this now, it is shown once: covt_")),
            NotificationKind.Success, TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task Account_UploadTokens_lists_the_tokens_of_its_parent_account()
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Account { GitHubId = 1, Login = "acme" }, Account.DocumentId(EForgeProvider.GitHub, 1));
            await seed.StoreAsync(new Account { GitHubId = 2, Login = "other" }, Account.DocumentId(EForgeProvider.GitHub, 2));
            await seed.StoreAsync(new ApiToken { Account = Account.DocumentId(EForgeProvider.GitHub, 1), Description = "mine" }, ApiToken.NewDocumentId());
            await seed.StoreAsync(new ApiToken { Account = Account.DocumentId(EForgeProvider.GitHub, 2), Description = "theirs" }, ApiToken.NewDocumentId());
            // Same numeric id on another forge: matched by document id, so never listed here.
            await seed.StoreAsync(new ApiToken { Account = Account.DocumentId(EForgeProvider.GitLab, 1), Description = "gitlab twin" }, ApiToken.NewDocumentId());
            await seed.SaveChangesAsync();
        }
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();
        var parent = Page("Account", nameof(Account.Login));
        parent.Id = Account.DocumentId(EForgeProvider.GitHub, 1);
        parent[nameof(Account.Login)].Value = "acme";

        var tokens = await Create<ApiTokenActions>(Managing(), session).Account_UploadTokens(QueryArgs(parent)).ToListAsync();

        tokens.Select(t => t.Description).Should().Equal("mine");
    }
}
