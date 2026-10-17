using System.Net;
using System.Text.Json;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.LookupReferences;
using CodeCoverage.Tests._Infrastructure;
using Xunit;

namespace CodeCoverage.Tests.Controllers;

/// <summary>
/// The onboarding page's <c>installation_id</c> cannot be tampered into another user's installation (dependency-updates
/// PRD R20, R21, D-O13, D-O14): it only narrows the signed-in viewer's OWN list, an installation of someone else answers
/// exactly like one that does not exist, a malformed value is an unknown installation, and the enable action re-checks
/// every row. Both paths that read it are covered: the header endpoint and the Spark query the grid runs, whose virtual
/// parent <c>DependencyOnboardingScope</c> carries the same query string as its id.
/// </summary>
/// <remarks>
/// Through the real endpoints (<c>GET /api/dependency-updates/onboarding</c>, <c>POST /spark/queries/execute</c>,
/// <c>POST /spark/actions/execute</c>) of the real composition root, with <see cref="OwnerPrivacyHostFixture"/>: user A
/// (<see cref="OwnerPrivacyHostFixture.OwnerUserId"/>) owns acme and reaches its installation 5555; user B
/// (<see cref="OwnerPrivacyHostFixture.OtherOwnerUserId"/>) owns globex and reaches its installation 6666, which exists in the
/// database with two private repositories. The forge's listing is scripted the way GitHub answers the viewer's own token.
/// </remarks>
[Collection(CoverageWebHostCollection.Name)]
public class DependencyOnboardingTamperEndToEndTests(OwnerPrivacyHostFixture host) : IClassFixture<OwnerPrivacyHostFixture>
{
    private const string Onboarding = "/api/dependency-updates/onboarding";
    private const string RepositoryTypeId = "22880468-80f5-4fd2-9472-4f87842ce4ff";
    private const string OnboardingRowTypeId = "4832e68c-b6c1-464e-af34-9d8ceb013c34";
    private const string OnboardingQueryId = "9d2f6b41-7c3e-4a85-b0d1-5e8a2c4f6b93";
    private const string ScopeType = "DependencyOnboardingScope";
    private const string UnknownInstallation = "7070707";
    private const string A = OwnerPrivacyHostFixture.OwnerUserId;

    private static string Narrow(string installation) => $"{Onboarding}?installation_id={installation}";

    private static string[] AcmeRepositories => ["acme/admin-only", "acme/open", "acme/secret"];

    /// <summary>The 9 malformed or duplicated forms (3), as the raw query string the page forwards.</summary>
    public static TheoryData<string> MalformedQueries() =>
    [
        "installation_id=not-a-number",
        "installation_id=99999999999999999999999",
        "installation_id=-6666",
        "installation_id=0",
        "installation_id=",
        "installation_id=6666%3B%20drop",
        "installation_id=5555&installation_id=6666",
        "installation_id=6666&installation_id=5555",
        "installation_id=7070707&setup_action=install&extra=1&installation_id[]=6666",
    ];

    private static void AssertNoTraceOfB(string body)
    {
        body.Should().NotContain("globex");
        body.Should().NotContain("b-secret");
        body.Should().NotContain("b-tool");
        body.Should().NotContain(OwnerPrivacyHostFixture.OtherInstallation);
    }

    // ── the header endpoint ────────────────────────────────────────────────────────────────────

    private static (int Repositories, bool Narrowed) Header(string body)
    {
        using var json = JsonDocument.Parse(body);
        return (json.RootElement.GetProperty("repositories").GetInt32(), json.RootElement.GetProperty("narrowed").GetBoolean());
    }

    [Fact]
    public async Task The_installation_parameter_narrows_the_viewers_own_list()
    {
        var (status, body) = await host.GetAsync(Narrow(OwnerPrivacyHostFixture.AcmeInstallation), A);

        status.Should().Be(HttpStatusCode.OK);
        Header(body).Should().Be((3, true));
        (await RowsAsync($"installation_id={OwnerPrivacyHostFixture.AcmeInstallation}", A)).Names.Should().Equal(AcmeRepositories);
    }

    // (1) + (2)
    [Fact]
    public async Task Another_users_installation_lists_nothing_and_answers_byte_for_byte_like_one_that_does_not_exist()
    {
        var (foreignStatus, foreign) = await host.GetAsync(Narrow(OwnerPrivacyHostFixture.OtherInstallation), A);
        var (unknownStatus, unknown) = await host.GetAsync(Narrow(UnknownInstallation), A);

        foreignStatus.Should().Be(HttpStatusCode.OK);
        Header(foreign).Repositories.Should().Be(0, "installation 6666 is B's: A reaches nothing of it");
        unknownStatus.Should().Be(foreignStatus);
        foreign.Should().Be(unknown, "no existence oracle: B's installation and one that does not exist are the same answer");
        using var json = JsonDocument.Parse(foreign);
        json.RootElement.GetProperty("narrowed").GetBoolean().Should().BeTrue("narrowed is what the URL asked, the same for both");
        json.RootElement.GetProperty("login").GetString().Should().Be("privacy-owner");
        AssertNoTraceOfB(foreign);
    }

    // (3)
    [Theory]
    [MemberData(nameof(MalformedQueries))]
    public async Task A_malformed_or_duplicated_installation_is_an_unknown_one_without_an_error(string query)
    {
        var (unknownStatus, unknown) = await host.GetAsync(Narrow(UnknownInstallation), A);

        var (status, body) = await host.GetAsync($"{Onboarding}?{query}", A);

        status.Should().Be(HttpStatusCode.OK, "a bad parameter is no server error");
        body.Should().NotContain("Exception").And.NotContain("   at ");
        AssertNoTraceOfB(body);
        if (query.Contains("setup_action=install", StringComparison.Ordinal))
            body.Should().Be(unknown.Replace("\"setupAction\":null", "\"setupAction\":\"install\"", StringComparison.Ordinal));
        else
            body.Should().Be(unknown, "treated as an installation that does not exist");
        unknownStatus.Should().Be(status);
    }

    // (4)
    [Fact]
    public async Task A_read_only_member_who_reaches_the_installation_is_offered_nothing_and_learns_no_more_than_from_a_foreign_one()
    {
        const string member = OwnerPrivacyHostFixture.MemberUserId;
        var (status, reached) = await host.GetAsync(Narrow(OwnerPrivacyHostFixture.AcmeInstallation), member);
        var (_, foreign) = await host.GetAsync(Narrow(OwnerPrivacyHostFixture.OtherInstallation), member);
        var (_, unknown) = await host.GetAsync(Narrow(UnknownInstallation), member);

        status.Should().Be(HttpStatusCode.OK);
        Header(reached).Repositories.Should().Be(0, "R39: reaching acme's installation is not managing any of its repositories");
        reached.Should().Be(foreign).And.Be(unknown);
        reached.Should().NotContain("acme/");
    }

    // (6)
    [Theory]
    [InlineData(OwnerPrivacyHostFixture.OtherInstallation)]
    [InlineData(OwnerPrivacyHostFixture.AcmeInstallation)]
    [InlineData(UnknownInstallation)]
    public async Task An_anonymous_caller_gets_the_uniform_401_whatever_the_installation(string installation)
    {
        var (status, body) = await host.GetAsync(Narrow(installation), user: null);
        var (plainStatus, plain) = await host.GetAsync(Onboarding, user: null);

        status.Should().Be(HttpStatusCode.Unauthorized);
        plainStatus.Should().Be(status);
        body.Should().Be(plain);
        body.Should().NotContain("acme/");
        AssertNoTraceOfB(body);
    }

    // ── the grid's query (D-O14): the same rule through /spark/queries/execute ─────────────────

    private Task<(HttpStatusCode Status, string Body)> QueryAsync(string? scope, string? user)
        => host.PostAsync("/spark/queries/execute", scope is null
            ? new { queryId = OnboardingQueryId }
            : (object)new { queryId = OnboardingQueryId, parentId = scope, parentType = ScopeType }, user);

    /// <summary>The rows' full names (sorted by the query), and the part of the answer that depends on the rows alone.</summary>
    private async Task<(HttpStatusCode Status, string[] Names, string Rows, string Body)> RowsAsync(string? scope, string? user)
    {
        var (status, body) = await QueryAsync(scope, user);
        if (status != HttpStatusCode.OK)
            return (status, [], "", body);
        using var json = JsonDocument.Parse(body);
        var items = json.RootElement.GetProperty("items");
        var names = items.EnumerateArray()
            .Select(i => i.GetProperty("values").EnumerateArray().First(v => v.GetProperty("key").GetString() == "FullName").GetProperty("value").GetString()!)
            .ToArray();
        return (status, names, $"{items.GetRawText()}|{json.RootElement.GetProperty("totalItems").GetRawText()}", body);
    }

    [Fact]
    public async Task The_query_lists_the_viewers_own_repositories_and_narrows_to_the_named_installation()
    {
        var all = await RowsAsync(null, A);
        var narrowed = await RowsAsync($"installation_id={OwnerPrivacyHostFixture.AcmeInstallation}&setup_action=install", A);

        all.Status.Should().Be(HttpStatusCode.OK, all.Body);
        all.Names.Should().Equal(AcmeRepositories);
        narrowed.Names.Should().Equal(AcmeRepositories);
        AssertNoTraceOfB(all.Body);
    }

    [Fact]
    public async Task The_query_answers_another_users_installation_byte_for_byte_like_one_that_does_not_exist()
    {
        var foreign = await RowsAsync($"installation_id={OwnerPrivacyHostFixture.OtherInstallation}", A);
        var unknown = await RowsAsync($"installation_id={UnknownInstallation}", A);

        foreign.Status.Should().Be(HttpStatusCode.OK, foreign.Body);
        foreign.Names.Should().BeEmpty("installation 6666 is B's: A reaches nothing of it");
        unknown.Status.Should().Be(foreign.Status);
        foreign.Rows.Should().Be(unknown.Rows, "no existence oracle");
        AssertNoTraceOfB(foreign.Body);
    }

    [Theory]
    [MemberData(nameof(MalformedQueries))]
    public async Task The_query_treats_a_malformed_or_duplicated_installation_as_an_unknown_one(string query)
    {
        var unknown = await RowsAsync($"installation_id={UnknownInstallation}", A);

        var tampered = await RowsAsync(query, A);

        tampered.Status.Should().Be(HttpStatusCode.OK, tampered.Body);
        tampered.Body.Should().NotContain("Exception").And.NotContain("   at ");
        tampered.Names.Should().BeEmpty("a malformed scope narrows to nothing, never widens to all of the viewer's installations");
        tampered.Rows.Should().Be(unknown.Rows);
        AssertNoTraceOfB(tampered.Body);
    }

    [Fact]
    public async Task A_read_only_member_gets_no_rows_from_the_query_whatever_the_scope()
    {
        const string member = OwnerPrivacyHostFixture.MemberUserId;
        foreach (var scope in new[] { null, $"installation_id={OwnerPrivacyHostFixture.AcmeInstallation}", $"installation_id={OwnerPrivacyHostFixture.OtherInstallation}" })
        {
            var rows = await RowsAsync(scope, member);
            rows.Status.Should().Be(HttpStatusCode.OK, rows.Body);
            rows.Names.Should().BeEmpty($"R39: a read-only member manages nothing ({scope ?? "no scope"})");
        }
    }

    [Fact]
    public async Task Another_parent_type_does_not_narrow_or_widen_beyond_the_viewers_own_list()
    {
        var (status, body) = await host.PostAsync("/spark/queries/execute",
            new { queryId = OnboardingQueryId, parentId = host.OtherRepositoryIds[0], parentType = "Repository" }, A);

        ((int)status).Should().BeLessThan(500, body);
        AssertNoTraceOfB(body);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("installation_id=" + OwnerPrivacyHostFixture.OtherInstallation)]
    [InlineData("installation_id=" + OwnerPrivacyHostFixture.AcmeInstallation)]
    public async Task An_anonymous_caller_gets_no_rows_from_the_query(string? scope)
    {
        var (status, body) = await QueryAsync(scope, user: null);

        status.Should().NotBe(HttpStatusCode.OK);
        body.Should().NotContain("acme/");
        AssertNoTraceOfB(body);
    }

    // ── the query action ────────────────────────────────────────────────────────────────────────

    // (5)
    [Fact]
    public async Task Enabling_Bs_repositories_as_A_enables_nothing_answers_like_missing_ones_and_leaves_Bs_settings_alone()
    {
        var (status, body) = await EnableAsync(host.OtherRepositoryIds, A);
        var (missingStatus, missing) = await EnableAsync([Repository.DocumentId(EForgeProvider.GitHub, 4799), Repository.DocumentId(EForgeProvider.GitHub, 4798)], A);

        status.Should().NotBe(HttpStatusCode.OK, $"B's repositories are not rows of A's query ({body})");
        ((int)status).Should().BeLessThan(500);
        missingStatus.Should().Be(status);
        body.Should().Be(missing, "B's repositories and ones that do not exist are refused alike");
        AssertNoTraceOfB(body);
        await AssertBUnchangedAsync();
    }

    [Fact]
    public async Task Enabling_Bs_repositories_under_Bs_installation_scope_enables_nothing_either()
    {
        var (status, body) = await EnableAsync(host.OtherRepositoryIds, A, $"installation_id={OwnerPrivacyHostFixture.OtherInstallation}");
        var (missingStatus, missing) = await EnableAsync(host.OtherRepositoryIds, A, $"installation_id={UnknownInstallation}");

        status.Should().NotBe(HttpStatusCode.OK, body);
        ((int)status).Should().BeLessThan(500);
        missingStatus.Should().Be(status);
        body.Should().Be(missing);
        AssertNoTraceOfB(body);
        await AssertBUnchangedAsync();
    }

    [Fact]
    public async Task A_scope_narrowed_to_another_installation_cannot_carry_a_selection_from_outside_it()
    {
        // acme/secret is A's own, but not a row of installation 7070707: the action re-runs the query under the same scope.
        var (status, body) = await EnableAsync([host.PrivateRepositoryId], A, $"installation_id={UnknownInstallation}");

        status.Should().NotBe(HttpStatusCode.OK, body);
        ((int)status).Should().BeLessThan(500);
        using var session = host.Store.OpenAsyncSession();
        (await session.LoadAsync<Repository>(host.PrivateRepositoryId)).DependencyOnboardingMergeQueue.Should().Be(default(EDependencyOnboardingMergeQueuePolicy));
    }

    [Fact]
    public async Task Enabling_a_repository_the_member_can_see_but_does_not_manage_enables_nothing()
    {
        var (status, body) = await EnableAsync([host.PrivateRepositoryId], OwnerPrivacyHostFixture.MemberUserId);

        status.Should().NotBe(HttpStatusCode.OK, $"the member manages nothing, so it is no row of their query ({body})");
        ((int)status).Should().BeLessThan(500, body);
        using var session = host.Store.OpenAsyncSession();
        (await session.LoadAsync<Repository>(host.PrivateRepositoryId)).DependencyUpdates.Should().Be(EDependencyUpdatesPolicy.Enabled, "the seeded value, unchanged");
        (await session.LoadAsync<Repository>(host.PrivateRepositoryId)).DependencyOnboardingMergeQueue.Should().Be(default(EDependencyOnboardingMergeQueuePolicy));
    }

    [Fact]
    public async Task The_action_is_no_longer_reachable_on_the_Repository_type()
    {
        var (status, body) = await host.PostAsync("/spark/actions/execute", new
        {
            objectTypeId = RepositoryTypeId, actionName = "EnableDependencyUpdates", selectedItemIds = host.OtherRepositoryIds, queryId = OnboardingQueryId,
        }, A);

        status.Should().NotBe(HttpStatusCode.OK, body);
        ((int)status).Should().BeLessThan(500);
        AssertNoTraceOfB(body);
        await AssertBUnchangedAsync();
    }

    private Task<(HttpStatusCode Status, string Body)> EnableAsync(string[] ids, string user, string? scope = null)
        => host.PostAsync("/spark/actions/execute", scope is null
            ? new { objectTypeId = OnboardingRowTypeId, actionName = "EnableDependencyUpdates", selectedItemIds = ids, queryId = OnboardingQueryId }
            : (object)new
            {
                objectTypeId = OnboardingRowTypeId, actionName = "EnableDependencyUpdates", selectedItemIds = ids, queryId = OnboardingQueryId,
                parentId = scope, parentType = ScopeType,
            }, user);

    private async Task AssertBUnchangedAsync()
    {
        using var session = host.Store.OpenAsyncSession();
        foreach (var id in host.OtherRepositoryIds)
        {
            var repository = await session.LoadAsync<Repository>(id);
            repository.DependencyUpdates.Should().Be(EDependencyUpdatesPolicy.Disabled, $"{id} is B's and was seeded Disabled");
            repository.DependencyOnboardingMergeQueue.Should().Be(default(EDependencyOnboardingMergeQueuePolicy));
        }
    }
}
