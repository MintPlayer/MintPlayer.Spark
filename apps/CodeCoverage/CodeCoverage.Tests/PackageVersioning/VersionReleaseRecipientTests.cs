using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.LookupReferences;
using CodeCoverage.PackageVersioning;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Assertions;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>
/// The server host's Releases (docs/coverage_package_versioning_PRD.md D15): one per tag in place, behind the resolved
/// release-on-tag policy, each outcome saved so a parked retry resumes.
/// </summary>
public class VersionReleaseRecipientTests : CoverageRavenTest
{
    private const string RepositoryId = "Repositories/153";
    private const string AccountId = "Accounts/github/1";
    private const string RunId = "VersioningRuns/github/153/9.1";

    private static VersioningRunRelease Release(string tag, bool withNotes = true, bool prerelease = false) => new()
    {
        Id = tag.Split("/v")[0], Version = tag.Split("/v")[1], Tag = tag, TagOutcome = withNotes ? "created" : "conflict",
        Release = withNotes ? new VersioningRunReleaseNotes { Name = tag.Replace("/v", " "), Body = "- fix: x (#1)", Prerelease = prerelease } : null,
    };

    private static async Task SeedAsync(IDocumentStore store, bool? accountOn, EReleaseOnTagPolicy policy, params VersioningRunRelease[] releases)
    {
        using var session = store.OpenAsyncSession();
        if (accountOn is { } on) await session.StoreAsync(new Account { ReleaseOnTag = on }, AccountId);
        await session.StoreAsync(new Repository
        {
            Account = accountOn is null ? null : AccountId, FullName = "acme/widgets", Provider = EForgeProvider.GitHub, ReleaseOnTag = policy,
        }, RepositoryId);
        await session.StoreAsync(new VersioningRun { Repository = RepositoryId, Releases = [.. releases] }, RunId);
        await session.SaveChangesAsync();
    }

    private static async Task HandleAsync(IDocumentStore store, FakeForgeVersioning forge)
    {
        using var session = store.OpenAsyncSession();
        await new VersionReleaseRecipient(session, [forge], TimeProvider.System, NullLogger<VersionReleaseRecipient>.Instance)
            .HandleAsync(new CreateVersionReleasesMessage { RunId = RunId });
    }

    [Theory]
    [InlineData(null, EReleaseOnTagPolicy.Inherit, true)]
    [InlineData(true, EReleaseOnTagPolicy.Inherit, true)]
    [InlineData(false, EReleaseOnTagPolicy.Inherit, false)]
    [InlineData(false, EReleaseOnTagPolicy.Enabled, true)]
    [InlineData(true, EReleaseOnTagPolicy.Disabled, false)]
    public async Task Releases_are_created_only_when_the_resolved_policy_is_on(bool? accountOn, EReleaseOnTagPolicy policy, bool created)
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, accountOn, policy, Release("Acme.Core/v1.2.0"));
        var forge = new FakeForgeVersioning();

        await HandleAsync(store, forge);

        forge.Releases.Select(r => r.TagName).Should().Equal(created ? ["Acme.Core/v1.2.0"] : []);
    }

    [Fact]
    public async Task Each_tag_in_place_gets_one_Release_and_a_rerun_skips_the_ones_done()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, true, EReleaseOnTagPolicy.Inherit,
            Release("Acme.Core/v1.2.0"), Release("Acme.Broken/v1.0.0", withNotes: false), Release("@acme/util/v2.0.0-next.1", prerelease: true));
        var forge = new FakeForgeVersioning();
        forge.Releases.Add(new ForgeRelease("@acme/util/v2.0.0-next.1", "made by hand", "", true));

        await HandleAsync(store, forge);
        await HandleAsync(store, forge);

        forge.Releases.Select(r => (r.TagName, r.Name, r.Prerelease)).Should().Equal(
            ("@acme/util/v2.0.0-next.1", "made by hand", true), ("Acme.Core/v1.2.0", "Acme.Core 1.2.0", false));
        using var session = store.OpenAsyncSession();
        var run = await session.LoadAsync<VersioningRun>(RunId);
        run!.Releases.Select(r => r.ReleaseOutcome).Should().Equal("created", null, "exists");
    }
}
