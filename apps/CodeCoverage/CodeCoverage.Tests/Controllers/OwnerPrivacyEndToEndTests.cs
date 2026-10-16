using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.LookupReferences;
using CodeCoverage.Services;
using CodeCoverage.Tests._Infrastructure;
using CodeCoverage.Tests.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.Controllers;

/// <summary>
/// Nobody reads another owner's settings (dependency-updates PRD §13.12, D-A1–D-A6): an account's and a repository's
/// owner-only configuration reaches only the account's managers and the Administrators group, on every read path that can
/// return it — the detail load, the queries (rows, distinct values), the sub-query on the account page, and the
/// <c>/api/browse</c> endpoints, the dependency-updates ones included.
/// </summary>
/// <remarks>
/// <para>
/// Through the real composition root (<see cref="CoverageWebAppFactory"/>): the app's own <c>security.json</c>, model and
/// actions classes, so what is asserted is the layering as deployed — the grant in <c>security.json</c>, then the per-row
/// narrowing in <c>AccountActions</c> / <c>RepositoryActions</c>. Only the forge is scripted (the owner set a caller would get
/// from GitHub), and the caller is signed in by a header, as <c>HostMetricsDashboardTests</c> does. The Administrators group
/// is the real <c>CoverageAdministrators</c> provider, matched by the forge id on a seeded user.
/// </para>
/// <para>
/// <b>Generic on purpose.</b> Every attribute of <c>Account</c> and <c>Repository</c> in the model must be on this file's
/// public or private list (<see cref="Every_model_attribute_is_classified_as_public_or_private"/>), so a new setting cannot
/// arrive without someone deciding who may read it; and every attribute not on the public list is asserted empty for an
/// outsider, whatever it is called.
/// </para>
/// </remarks>
[Collection(CoverageWebHostCollection.Name)]
public class OwnerPrivacyEndToEndTests(OwnerPrivacyHostFixture host) : IClassFixture<OwnerPrivacyHostFixture>
{
    private const string AccountTypeId = "a6c9a501-43a6-44f2-acf1-bafcfb2698cb";
    private const string RepositoryTypeId = "22880468-80f5-4fd2-9472-4f87842ce4ff";
    private const string GetAccountsQueryId = "c74dd235-11a2-49ef-bb8f-f5ba78eb72c3";
    private const string GetRepositoriesQueryId = "551b2212-8f16-4129-aea6-1dfe313d8edc";
    private const string AccountRepositoriesQueryId = "7a4c9b30-5d2e-4f81-9c67-3e8b1a6d0f21";

    /// <summary>What a visitor may read of an account: the forge's own public profile, and whether we are connected to it.</summary>
    public static readonly string[] PublicAccountAttributes =
        ["Login", "Type", "AvatarUrl", "Provider", "Connection", "DisconnectedReason", "DisconnectedAtUtc"];

    /// <summary>Owner-only: settings, and the server-side key.</summary>
    public static readonly string[] PrivateAccountAttributes =
    [
        "OwnerKey", "DeleteBranchOnPrClose", "MergeQueueApprovalFloor", "MergeQueueCheckExclusions", "MergeQueueBypassRole",
        "DependencyUpdates", "DependencyScheduleFrequency", "DependencyScheduleWeekday", "DependencyScheduleHour",
        "DependencyTimeZone", "DependencyCooldown", "DependencyBisectBudget", "DependencyRules", "DependencyOnboardingMergeQueue",
    ];

    /// <summary>What a visitor of a repository they may see may read: its identity, coverage and connection.</summary>
    public static readonly string[] PublicRepositoryAttributes =
    [
        "Account", "Archived", "Connection", "DefaultBranch", "DisconnectedAtUtc", "DisconnectedReason", "FullName", "IsPrivate",
        "LatestCoverage", "LatestCoverageAtUtc", "LatestCoverageSha", "Name", "OwnerLogin", "Provider",
    ];

    /// <summary>Owner-only: the badge credential, the gate, the fork budget, every setting, and the server-side keys.</summary>
    public static readonly string[] PrivateRepositoryAttributes =
    [
        "BadgeToken", "Gate", "ForkUploads", "OwnerKey", "PreviousFullNames", "DeleteBranchOnPrClose",
        "MergeQueueApprovalFloor", "MergeQueueCheckExclusionsPolicy", "MergeQueueCheckExclusions", "MergeQueueBypassRole",
        "DependencyUpdates", "DependencySchedulePolicy", "DependencyScheduleFrequency", "DependencyScheduleWeekday",
        "DependencyScheduleHour", "DependencyCooldown", "DependencyBisectBudget", "DependencyOnboardingMergeQueue", "MergeQueueInCoverageYml",
    ];

    /// <summary>Callers who manage nothing of acme: a visitor and a signed-in stranger.</summary>
    public static TheoryData<string?> Outsiders => new() { null, OwnerPrivacyHostFixture.StrangerUserId };

    /// <summary>Callers who may read acme's settings: a manager of the account, and an administrator who manages nothing.</summary>
    public static TheoryData<string> Insiders => new() { OwnerPrivacyHostFixture.OwnerUserId, OwnerPrivacyHostFixture.AdminUserId };

    /// <summary>Everyone but an administrator.</summary>
    public static TheoryData<string?> NonAdministrators => new() { null, OwnerPrivacyHostFixture.StrangerUserId, OwnerPrivacyHostFixture.OwnerUserId };

    [Theory]
    [InlineData("Account")]
    [InlineData("Repository")]
    public void Every_model_attribute_is_classified_as_public_or_private(string type)
    {
        var (publicNames, privateNames) = type == "Account"
            ? (PublicAccountAttributes, PrivateAccountAttributes)
            : (PublicRepositoryAttributes, PrivateRepositoryAttributes);
        using var scope = host.Factory.Services.CreateScope();
        var definition = scope.ServiceProvider.GetRequiredService<IModelLoader>().GetEntityTypeByName(type)!;

        var unclassified = definition.Attributes.Select(a => a.Name).Except(publicNames).Except(privateNames).ToList();

        unclassified.Should().BeEmpty(
            $"a new {type} attribute must be put on this test's public or private list, so somebody decides who may read it");
        publicNames.Intersect(privateNames).Should().BeEmpty();
    }

    // ── The detail load ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Outsiders))]
    public async Task An_outsider_loading_the_account_page_sees_only_its_public_attributes(string? user)
    {
        var (status, body) = await host.PostAsync("/spark/po/load", new { objectTypeId = AccountTypeId, id = host.AccountId }, user);

        status.Should().Be(HttpStatusCode.OK, "the account page is public: it is what /{provider}/a/{login} opens");
        using var json = JsonDocument.Parse(body);
        var filled = FilledAttributes(json.RootElement);
        filled.Except(PublicAccountAttributes).Should().BeEmpty("an outsider may read only the account's public attributes");
        filled.Should().Contain("Login");
        AssertNoMarker(body);
    }

    [Theory]
    [MemberData(nameof(Insiders))]
    public async Task A_manager_and_an_administrator_loading_the_account_page_see_its_settings(string user)
    {
        var (status, body) = await host.PostAsync("/spark/po/load", new { objectTypeId = AccountTypeId, id = host.AccountId }, user);

        status.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(body);
        var filled = FilledAttributes(json.RootElement);
        foreach (var name in new[] { "DependencyTimeZone", "DependencyRules", "MergeQueueCheckExclusions", "DeleteBranchOnPrClose", "DependencyUpdates" })
            filled.Should().Contain(name);
        body.Should().Contain(OwnerPrivacyHostFixture.TimeZoneMarker);
        body.Should().Contain(OwnerPrivacyHostFixture.RulePackageMarker);
    }

    [Theory]
    [MemberData(nameof(Outsiders))]
    public async Task An_outsider_loading_a_public_repository_sees_only_its_public_attributes(string? user)
    {
        var (status, body) = await host.PostAsync("/spark/po/load", new { objectTypeId = RepositoryTypeId, id = host.PublicRepositoryId }, user);

        status.Should().Be(HttpStatusCode.OK, "a public repository's page is public");
        using var json = JsonDocument.Parse(body);
        FilledAttributes(json.RootElement).Except(PublicRepositoryAttributes).Should().BeEmpty(
            "an outsider may read only the repository's public attributes");
        AssertNoMarker(body);
    }

    [Theory]
    [MemberData(nameof(Insiders))]
    public async Task A_manager_and_an_administrator_loading_a_public_repository_see_its_settings(string user)
    {
        var (status, body) = await host.PostAsync("/spark/po/load", new { objectTypeId = RepositoryTypeId, id = host.PublicRepositoryId }, user);

        status.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(body);
        var filled = FilledAttributes(json.RootElement);
        foreach (var name in new[] { "DependencyUpdates", "DeleteBranchOnPrClose", "MergeQueueCheckExclusions" })
            filled.Should().Contain(name);
        body.Should().Contain(OwnerPrivacyHostFixture.RepositoryCheckMarker);
        if (user == OwnerPrivacyHostFixture.AdminUserId)
            body.Should().NotContain(OwnerPrivacyHostFixture.BadgeTokenMarker, "the badge token is a credential: an administrator reads settings, not secrets");
        else
            body.Should().Contain(OwnerPrivacyHostFixture.BadgeTokenMarker);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(OwnerPrivacyHostFixture.StrangerUserId)]
    [InlineData(OwnerPrivacyHostFixture.AdminUserId)]
    public async Task Edit_is_withheld_on_an_account_the_caller_does_not_manage(string? user)
    {
        var (_, body) = await host.PostAsync("/spark/po/load", new { objectTypeId = AccountTypeId, id = host.AccountId }, user);
        var (_, ownerBody) = await host.PostAsync("/spark/po/load", new { objectTypeId = AccountTypeId, id = host.AccountId }, OwnerPrivacyHostFixture.OwnerUserId);

        using var json = JsonDocument.Parse(body);
        using var ownerJson = JsonDocument.Parse(ownerBody);
        json.RootElement.GetProperty("disabledActions").EnumerateArray().Select(e => e.GetString()).Should().Contain("Edit");
        var ownerWithholdsEdit = ownerJson.RootElement.TryGetProperty("disabledActions", out var ownerDisabled)
            && ownerDisabled.ValueKind == JsonValueKind.Array
            && ownerDisabled.EnumerateArray().Any(e => e.GetString() == "Edit");
        ownerWithholdsEdit.Should().BeFalse("the account's manager may edit it");
    }

    // ── The queries ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(NonAdministrators))]
    public async Task Only_an_administrator_may_run_the_account_query_or_list_its_values(string? user)
    {
        var accounts = await host.PostAsync("/spark/queries/execute", new { queryId = GetAccountsQueryId }, user);
        var sorted = await host.PostAsync("/spark/queries/execute",
            new { queryId = GetAccountsQueryId, sortColumns = new[] { new { property = "DeleteBranchOnPrClose", direction = "desc" } } }, user);
        var filtered = await host.PostAsync("/spark/queries/execute",
            new { queryId = GetAccountsQueryId, columns = new[] { new { name = "DeleteBranchOnPrClose", includes = new object[] { true } } } }, user);
        var distinct = await host.PostAsync("/spark/queries/distinct-values", new { queryId = GetAccountsQueryId, column = "DeleteBranchOnPrClose" }, user);

        // No page lists accounts: the account page is a Read, its reference columns are breadcrumbs (Read), and nothing
        // offers an account picker. So the listing — and with it every sort and filter oracle on the account grid — is the
        // Administrators' alone (D-A2). security.json still grants QueryRead/Account (Read implies Query, #324);
        // AccountActions withholds Query per request.
        foreach (var (status, body) in new[] { accounts, sorted, filtered, distinct })
        {
            status.Should().NotBe(HttpStatusCode.OK, "AccountActions withholds the account grid from non-administrators");
            body.Should().NotContain("acme");
        }
    }

    [Fact]
    public async Task An_administrator_lists_the_accounts()
    {
        var (status, body) = await host.PostAsync("/spark/queries/execute", new { queryId = GetAccountsQueryId }, OwnerPrivacyHostFixture.AdminUserId);

        status.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("acme");
    }

    [Theory]
    [MemberData(nameof(Outsiders))]
    public async Task The_repository_lists_an_outsider_can_run_carry_only_public_attributes(string? user)
    {
        var all = await host.PostAsync("/spark/queries/execute", new { queryId = GetRepositoriesQueryId }, user);
        var ofAccount = await host.PostAsync("/spark/queries/execute",
            new { queryId = AccountRepositoriesQueryId, parentId = host.AccountId, parentType = "Account" }, user);

        foreach (var (status, body) in new[] { all, ofAccount })
        {
            status.Should().Be(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(body);
            var rows = json.RootElement.GetProperty("items").EnumerateArray().ToList();
            rows.Should().ContainSingle("the private repository is not listed to an outsider");
            FilledAttributes(rows[0]).Except(PublicRepositoryAttributes).Should().BeEmpty();
            AssertNoMarker(body);
        }
    }

    // ── /api/browse ─────────────────────────────────────────────────────────────────────────────

    public static TheoryData<string?, string> OutsiderBrowsePaths()
    {
        string[] paths =
        [
            "/api/browse/accounts/github/acme",
            "/api/browse/accounts/github/acme/repos",
            "/api/browse/accounts/github/acme/dependency-graph",
            "/api/browse/accounts/github/acme/dependency-updates",
            "/api/browse/accounts/github/acme/sparklines",
            "/api/browse/repos/github/acme/open",
            "/api/browse/repos/github/acme/open/dependency-updates",
        ];
        var data = new TheoryData<string?, string>();
        foreach (var user in new[] { null, OwnerPrivacyHostFixture.StrangerUserId })
            foreach (var path in paths)
                data.Add(user, path);
        return data;
    }

    [Theory]
    [MemberData(nameof(OutsiderBrowsePaths))]
    public async Task No_browse_endpoint_hands_an_outsider_the_owners_settings(string? user, string path)
    {
        var (status, body) = await host.GetAsync(path, user);

        status.Should().Be(HttpStatusCode.OK);
        // The fleet view's per-repository schedule line is the dependency-updates panel of a repository the caller can see
        // (D-N5), so it is not asserted here; the account's rules, check exclusions and badge token never travel.
        AssertNoMarker(body, includeTimeZone: false);
        body.Should().NotContain(OwnerPrivacyHostFixture.BadgeTokenMarker);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static void AssertNoMarker(string body, bool includeTimeZone = true)
    {
        body.Should().NotContain(OwnerPrivacyHostFixture.RulePackageMarker);
        body.Should().NotContain(OwnerPrivacyHostFixture.AccountCheckMarker);
        body.Should().NotContain(OwnerPrivacyHostFixture.RepositoryCheckMarker);
        body.Should().NotContain(OwnerPrivacyHostFixture.BadgeTokenMarker);
        if (includeTimeZone)
            body.Should().NotContain(OwnerPrivacyHostFixture.TimeZoneMarker);
    }

    /// <summary>The names of the attributes that carry anything at all: a value (false and 0 included), a breadcrumb or embedded rows.</summary>
    /// <remarks>A detail object carries <c>attributes</c> (by <c>name</c>); a query row carries <c>values</c> (by <c>key</c>).</remarks>
    private static List<string> FilledAttributes(JsonElement persistentObjectOrRow)
    {
        var (list, nameProperty) = persistentObjectOrRow.TryGetProperty("attributes", out var attributes)
            ? (attributes, "name")
            : (persistentObjectOrRow.GetProperty("values"), "key");
        return [.. list.EnumerateArray()
            .Where(a => Filled(a, "value") || Filled(a, "objectId") || Filled(a, "breadcrumb") || Filled(a, "breadcrumbs") || Filled(a, "object") || Filled(a, "objects"))
            .Select(a => a.GetProperty(nameProperty).GetString()!)];
    }

    private static bool Filled(JsonElement attribute, string property)
        => attribute.TryGetProperty(property, out var value) && value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.String => value.GetString()!.Length > 0,
            JsonValueKind.Array => value.GetArrayLength() > 0,
            JsonValueKind.Object => value.EnumerateObject().Any(),
            _ => true,
        };
}

/// <summary>
/// The application with a scripted owner set per caller, signed in by header, and one administrator by forge id: acme
/// (an organization with every owner-only setting filled with a findable value), its public repository <c>open</c> and its
/// private repository <c>secret</c>.
/// </summary>
public sealed class OwnerPrivacyHostFixture : CoverageRavenTest, IAsyncLifetime
{
    public const string OwnerUserId = "SparkUsers/privacy-owner";
    public const string StrangerUserId = "SparkUsers/privacy-stranger";
    public const string AdminUserId = "SparkUsers/privacy-admin";
    private const string AdminGitHubId = "770077";
    private const string UserHeader = "X-Test-User";

    public const string TimeZoneMarker = "Pacific/Chatham";
    public const string RulePackageMarker = "privacy-marker-package";
    public const string AccountCheckMarker = "privacy-marker-account-check";
    public const string RepositoryCheckMarker = "privacy-marker-repository-check";
    public const string BadgeTokenMarker = "privacy-marker-badge-token";

    public IDocumentStore Store { get; private set; } = null!;
    public CoverageWebAppFactory Factory { get; private set; } = null!;

    public string AccountId { get; } = Account.DocumentId(EForgeProvider.GitHub, 5);
    public string PublicRepositoryId { get; } = Repository.DocumentId(EForgeProvider.GitHub, 4601);
    public string PrivateRepositoryId { get; } = Repository.DocumentId(EForgeProvider.GitHub, 4602);

    public async Task InitializeAsync()
    {
        Store = GetDocumentStore();
        Factory = new CoverageWebAppFactory(Store, services =>
        {
            services.AddSingleton<IStartupFilter, TestPrincipalStartupFilter>();
            services.RemoveAll<IForgeIntegrationResolver>();
            services.AddScoped<IForgeIntegrationResolver, PerCallerForges>();
        }, new Dictionary<string, string?>
        {
            [$"{CoverageAdministrators.Section}:0"] = $"github:{AdminGitHubId}",
        });
        _ = Factory.CreateClient();

        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(User("privacy-owner", "880001"), OwnerUserId);
            await session.StoreAsync(User("privacy-stranger", "880002"), StrangerUserId);
            await session.StoreAsync(User("privacy-admin", AdminGitHubId), AdminUserId);

            await session.StoreAsync(new Account
            {
                GitHubId = 5,
                Provider = EForgeProvider.GitHub,
                Login = "acme",
                Type = "Organization",
                AvatarUrl = "https://avatars.example/acme.png",
                DeleteBranchOnPrClose = true,
                MergeQueueApprovalFloor = (EMergeQueueApprovalFloor)1,
                MergeQueueCheckExclusions = [AccountCheckMarker],
                MergeQueueBypassRole = (EMergeQueueBypassRole)1,
                DependencyUpdates = true,
                DependencyScheduleFrequency = EDependencyScheduleFrequency.Daily,
                DependencyScheduleWeekday = EDependencyWeekday.Thursday,
                DependencyScheduleHour = 17,
                DependencyTimeZone = TimeZoneMarker,
                DependencyCooldown = EDependencyCooldown.SevenDays,
                DependencyBisectBudget = EDependencyBisectBudget.Four,
                DependencyRules = [new DependencyRule { Id = "privacy-rule", Match = RulePackageMarker, MaxUpdate = EDependencyThreshold.Patch }],
            }, AccountId);

            await session.StoreAsync(Repo(4601, "open", isPrivate: false), PublicRepositoryId);
            await session.StoreAsync(Repo(4602, "secret", isPrivate: true), PrivateRepositoryId);
            await session.SaveChangesAsync();
        }
        WaitForIndexing(Store);
    }

    private Repository Repo(long id, string name, bool isPrivate) => new()
    {
        GitHubId = id,
        Provider = EForgeProvider.GitHub,
        Account = AccountId,
        Name = name,
        FullName = $"acme/{name}",
        OwnerLogin = "acme",
        IsPrivate = isPrivate,
        DefaultBranch = "main",
        BadgeToken = BadgeTokenMarker,
        DeleteBranchOnPrClose = EDeleteBranchPolicy.Enabled,
        MergeQueueCheckExclusionsPolicy = (EMergeQueueCheckExclusionsPolicy)1,
        MergeQueueCheckExclusions = [RepositoryCheckMarker],
        DependencyUpdates = EDependencyUpdatesPolicy.Enabled,
        DependencySchedulePolicy = EDependencySchedulePolicy.Override,
        DependencyScheduleHour = 3,
        LatestCoverage = new CoverageSummary { LinesCovered = 9, LinesCoverable = 10 },
    };

    private static SparkUser User(string name, string gitHubId) => new()
    {
        UserName = name,
        NormalizedUserName = name.ToUpperInvariant(),
        Logins = [new SparkUserLogin { LoginProvider = "GitHub", ProviderKey = gitHubId, ProviderDisplayName = "GitHub" }],
    };

    public Task<(HttpStatusCode Status, string Body)> PostAsync(string path, object body, string? user)
        => SendAsync(new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        }, user);

    public Task<(HttpStatusCode Status, string Body)> GetAsync(string path, string? user)
        => SendAsync(new HttpRequestMessage(HttpMethod.Get, path), user);

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpRequestMessage request, string? user)
    {
        using var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using (request)
        {
            if (user is not null)
                request.Headers.Add(UserHeader, user);
            using var response = await client.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }
    }

    public new async Task DisposeAsync()
    {
        try { await Factory.DisposeAsync(); }
        catch (Exception ex) { Console.WriteLine($"[OwnerPrivacyHostFixture] host disposal faulted: {ex}"); }

        try { Store?.Dispose(); }
        catch (Exception ex) { Console.WriteLine($"[OwnerPrivacyHostFixture] store disposal faulted: {ex}"); }

        try { base.Dispose(); }
        catch (Exception ex) { Console.WriteLine($"[OwnerPrivacyHostFixture] RavenDB test-driver disposal faulted: {ex}"); }
    }

    /// <summary>Signs the request in as the user id in <see cref="UserHeader"/>; no group claims.</summary>
    private sealed class TestPrincipalStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(UserHeader, out var user) && user.ToString() is { Length: > 0 } id)
                {
                    context.User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, id), new Claim(ClaimTypes.Name, id)], "OwnerPrivacyTest"));
                }
                await nextMiddleware();
            });
            next(app);
        };
    }

    /// <summary>
    /// The forge as each caller would see it: the owner manages github:acme, everybody else (the administrator included)
    /// manages nothing, and a visitor has no forge linked at all.
    /// </summary>
    private sealed class PerCallerForges : IForgeIntegrationResolver
    {
        private readonly ScriptedDiffService forge = new();
        private readonly bool signedIn;

        public PerCallerForges(IHttpContextAccessor accessor)
        {
            var user = accessor.HttpContext?.User;
            signedIn = user?.Identity?.IsAuthenticated == true;
            if (user?.FindFirstValue(ClaimTypes.NameIdentifier) == OwnerUserId)
                forge.Owners.Add(new ForgeOwner(EForgeProvider.GitHub, "acme"));
        }

        public IForgeIntegration? For(EForgeProvider provider) => forge.For(provider);

        public IForgeIntegration For(Repository repository) => forge;

        public IReadOnlyList<IForgeIntegration> All => [forge];

        public Task<IReadOnlyList<EForgeProvider>> GetLinkedProvidersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<EForgeProvider>>(signedIn ? [EForgeProvider.GitHub] : []);
    }
}
