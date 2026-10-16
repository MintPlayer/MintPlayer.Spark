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
/// PRD R20, R21, D-O13): it only narrows the signed-in viewer's OWN list, an installation of someone else answers exactly
/// like one that does not exist, a malformed value is an unknown installation, and the enable action re-checks every row.
/// </summary>
/// <remarks>
/// Through the real endpoints (<c>GET /api/dependency-updates/onboarding</c>, <c>POST /spark/actions/execute</c>) of the real
/// composition root, with <see cref="OwnerPrivacyHostFixture"/>: user A (<see cref="OwnerPrivacyHostFixture.OwnerUserId"/>)
/// owns acme and reaches its installation 5555; user B (<see cref="OwnerPrivacyHostFixture.OtherOwnerUserId"/>) owns globex
/// and reaches its installation 6666, which exists in the database with two private repositories. The forge's listing is
/// scripted the way GitHub answers the viewer's own token.
/// </remarks>
[Collection(CoverageWebHostCollection.Name)]
public class DependencyOnboardingTamperEndToEndTests(OwnerPrivacyHostFixture host) : IClassFixture<OwnerPrivacyHostFixture>
{
    private const string Onboarding = "/api/dependency-updates/onboarding";
    private const string RepositoryTypeId = "22880468-80f5-4fd2-9472-4f87842ce4ff";
    private const string OnboardingQueryId = "9d2f6b41-7c3e-4a85-b0d1-5e8a2c4f6b93";
    private const string UnknownInstallation = "7070707";
    private const string A = OwnerPrivacyHostFixture.OwnerUserId;

    private static string Narrow(string installation) => $"{Onboarding}?installation_id={installation}";

    private static string[] FullNames(string body)
    {
        using var json = JsonDocument.Parse(body);
        return [.. json.RootElement.GetProperty("repositories").EnumerateArray().Select(r => r.GetProperty("fullName").GetString()!)];
    }

    private static void AssertNoTraceOfB(string body)
    {
        body.Should().NotContain("globex");
        body.Should().NotContain("b-secret");
        body.Should().NotContain("b-tool");
        body.Should().NotContain(OwnerPrivacyHostFixture.OtherInstallation);
    }

    [Fact]
    public async Task The_installation_parameter_narrows_the_viewers_own_list()
    {
        var (status, body) = await host.GetAsync(Narrow(OwnerPrivacyHostFixture.AcmeInstallation), A);

        status.Should().Be(HttpStatusCode.OK);
        FullNames(body).Should().BeEquivalentTo(["acme/open", "acme/secret", "acme/admin-only"]);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("narrowed").GetBoolean().Should().BeTrue();
    }

    // (1) + (2)
    [Fact]
    public async Task Another_users_installation_lists_nothing_and_answers_byte_for_byte_like_one_that_does_not_exist()
    {
        var (foreignStatus, foreign) = await host.GetAsync(Narrow(OwnerPrivacyHostFixture.OtherInstallation), A);
        var (unknownStatus, unknown) = await host.GetAsync(Narrow(UnknownInstallation), A);

        foreignStatus.Should().Be(HttpStatusCode.OK);
        FullNames(foreign).Should().BeEmpty("installation 6666 is B's: A reaches nothing of it");
        unknownStatus.Should().Be(foreignStatus);
        foreign.Should().Be(unknown, "no existence oracle: B's installation and one that does not exist are the same answer");
        using var json = JsonDocument.Parse(foreign);
        json.RootElement.GetProperty("narrowed").GetBoolean().Should().BeTrue("narrowed is what the URL asked, the same for both");
        json.RootElement.GetProperty("login").GetString().Should().Be("privacy-owner");
        AssertNoTraceOfB(foreign);
    }

    // (3)
    [Theory]
    [InlineData("installation_id=not-a-number")]
    [InlineData("installation_id=99999999999999999999999")]
    [InlineData("installation_id=-6666")]
    [InlineData("installation_id=0")]
    [InlineData("installation_id=")]
    [InlineData("installation_id=6666%3B%20drop")]
    [InlineData("installation_id=5555&installation_id=6666")]
    [InlineData("installation_id=6666&installation_id=5555")]
    [InlineData("installation_id=7070707&setup_action=install&extra=1&installation_id[]=6666")]
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
        FullNames(reached).Should().BeEmpty("R39: reaching acme's installation is not managing any of its repositories");
        reached.Should().Be(foreign).And.Be(unknown);
        reached.Should().NotContain("acme/");
    }

    // (5)
    [Fact]
    public async Task Enabling_Bs_repositories_as_A_enables_nothing_answers_like_missing_ones_and_leaves_Bs_settings_alone()
    {
        var (status, body) = await EnableAsync(host.OtherRepositoryIds, A);
        var (missingStatus, missing) = await EnableAsync([Repository.DocumentId(EForgeProvider.GitHub, 4799), Repository.DocumentId(EForgeProvider.GitHub, 4798)], A);

        status.Should().NotBe(HttpStatusCode.OK, $"A may not even see B's private repositories ({body})");
        ((int)status).Should().BeLessThan(500);
        missingStatus.Should().Be(status);
        body.Should().Be(missing, "B's repositories and ones that do not exist are refused alike");
        AssertNoTraceOfB(body);
        await AssertBUnchangedAsync();
    }

    [Fact]
    public async Task Enabling_a_repository_the_member_can_see_but_does_not_manage_enables_nothing()
    {
        var (status, body) = await EnableAsync([host.PrivateRepositoryId], OwnerPrivacyHostFixture.MemberUserId);

        ((int)status).Should().BeLessThan(500, body);
        if (status == HttpStatusCode.OK)
        {
            using var json = JsonDocument.Parse(body);
            var result = json.RootElement.GetProperty("result");
            result.GetProperty("enabled").GetArrayLength().Should().Be(0);
            result.GetProperty("notEnabled").EnumerateArray().Select(r => r.GetProperty("repositoryId").GetString()).Should().Equal([host.PrivateRepositoryId]);
        }
        using var session = host.Store.OpenAsyncSession();
        (await session.LoadAsync<Repository>(host.PrivateRepositoryId)).DependencyUpdates.Should().Be(EDependencyUpdatesPolicy.Enabled, "the seeded value, unchanged");
        (await session.LoadAsync<Repository>(host.PrivateRepositoryId)).DependencyOnboardingMergeQueue.Should().Be(default(EDependencyOnboardingMergeQueuePolicy));
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

    private Task<(HttpStatusCode Status, string Body)> EnableAsync(string[] ids, string user)
        => host.PostAsync("/spark/actions/execute", new
        {
            objectTypeId = RepositoryTypeId, actionName = "EnableDependencyUpdates", selectedItemIds = ids, queryId = OnboardingQueryId,
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
