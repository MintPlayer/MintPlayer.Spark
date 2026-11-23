using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Feedback;
using CodeCoverage.LookupReferences;
using CodeCoverage.MergeQueue;
using CodeCoverage.PackageVersioning;
using CodeCoverage.Tests._Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Assertions;
using NSubstitute;
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
    private const string Commit = "cccccccccccccccccccccccccccccccccccccccc";
    private const string RunTag = "release/dotnet-11.x/2026-11-22";

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
            Account = accountOn is null ? null : AccountId, FullName = "acme/widgets", Provider = EForgeProvider.GitHub, ReleaseOnTag = policy, DefaultBranch = "master",
        }, RepositoryId);
        await session.StoreAsync(new VersioningRun
        {
            Repository = RepositoryId, Branch = "release/dotnet-11.x", Commit = Commit, StartedAtUtc = new DateTime(2026, 11, 22, 9, 0, 0, DateTimeKind.Utc), Releases = [.. releases],
        }, RunId);
        await session.SaveChangesAsync();
    }

    private const string LinesYml = "versioning:\n  lines:\n    current: promote\n    trigger: manual\n    workflow: release.yml\n";

    private readonly IForgeIntegration integration = Substitute.For<IForgeIntegration>();
    private readonly IForgeMergeQueue git = Substitute.For<IForgeMergeQueue>();

    public VersionReleaseRecipientTests()
    {
        integration.Provider.Returns(EForgeProvider.GitHub);
        git.Provider.Returns(EForgeProvider.GitHub);
        Lines(LinesYml, "release/dotnet-11.x", "release/angular-22.x");
    }

    private void Lines(string yml, params string[] branches)
    {
        integration.GetFileContentAsync(Arg.Any<Repository>(), "master", CoverageYml.FileName, Arg.Any<CancellationToken>()).Returns(yml);
        git.ListBranchesAsync(Arg.Any<Repository>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(branches);
    }

    private async Task HandleAsync(IDocumentStore store, FakeForgeVersioning forge)
    {
        using var session = store.OpenAsyncSession();
        await new VersionReleaseRecipient(session, [forge], new SingleForgeResolver(integration), [git], TimeProvider.System, NullLogger<VersionReleaseRecipient>.Instance)
            .HandleAsync(new CreateVersionReleasesMessage { RunId = RunId });
    }

    [Theory]
    [InlineData(LinesYml, new[] { "release/dotnet-11.x", "release/angular-22.x" }, true)]                         // the current line of its ecosystem
    [InlineData(LinesYml, new[] { "release/dotnet-11.x", "release/dotnet-12.x", "release/angular-22.x" }, false)] // an older line: a backport release
    [InlineData("versioning:\n", new[] { "release/dotnet-12.x" }, true)]                                          // lines off: no line at all
    public async Task Only_a_current_line_s_run_Release_becomes_latest(string yml, string[] branches, bool latest)
    {
        // Changelog × release lines R2: a backport release on an older line must not take "Latest" from the current line.
        Lines(yml, branches);
        using var store = GetDocumentStore();
        await SeedAsync(store, true, EReleaseOnTagPolicy.Inherit, Release("Acme.Core/v11.0.3"));
        var forge = new FakeForgeVersioning();

        await HandleAsync(store, forge);

        var run = forge.Releases.Last();
        (run.TagName, run.Latest).Should().Be((RunTag, latest));
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

        forge.Releases.Select(r => r.TagName).Should().Equal(created ? ["Acme.Core/v1.2.0", RunTag] : []);
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
            ("@acme/util/v2.0.0-next.1", "made by hand", true), ("Acme.Core/v1.2.0", "Acme.Core 1.2.0", false), (RunTag, "dotnet-11.x 2026-11-22", false));
        using var session = store.OpenAsyncSession();
        var run = await session.LoadAsync<VersioningRun>(RunId);
        run!.Releases.Select(r => r.ReleaseOutcome).Should().Equal("created", null, "exists");
        run.RunReleaseTag.Should().Be(RunTag);
    }

    [Fact]
    public async Task A_dependency_only_package_gets_no_Release_and_the_run_Release_is_latest_and_lists_it()
    {
        // #693 D7: the run's own Release is the overview; a taken date tag (another run that day) moves to the next .n.
        using var store = GetDocumentStore();
        var dependent = Release("Acme.Util/v2.0.1");
        dependent.Release!.DependencyOnly = true;
        await SeedAsync(store, true, EReleaseOnTagPolicy.Inherit, Release("Acme.Core/v1.2.0"), dependent);
        var forge = new FakeForgeVersioning();
        forge.Releases.Add(new ForgeRelease(RunTag, "earlier run", "", false));

        await HandleAsync(store, forge);

        var run = forge.Releases.Last();
        (run.TagName, run.Latest, run.TargetCommit).Should().Be((RunTag + ".2", true, (string?)Commit));
        run.Body.Should().Be("### Packages\n- [Acme.Core 1.2.0](https://github.com/acme/widgets/releases/tag/Acme.Core/v1.2.0)\n\n### Dependency updates only\n- Acme.Util 2.0.1");
        forge.Releases.Select(r => r.TagName).Should().NotContain("Acme.Util/v2.0.1");
    }
}
