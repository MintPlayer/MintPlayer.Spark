using CodeCoverage.Actions;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.LookupReferences;
using CodeCoverage.Services;
using CodeCoverage.Tests._Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Queries;
using NSubstitute;
using Xunit;

namespace CodeCoverage.Tests.Actions;

/// <summary>
/// #693 M11, Q12: the repository page's changelog queries. A row is readable iff its repository is visible; the lists are
/// the parent repository's own, and the upcoming one is empty while the changelog is off. Run on both stores (D16).
/// </summary>
public class ChangelogQueryActionsTests : CoverageRavenTest
{
    private const long Repo = 6931;
    private const long OtherRepo = 69310; // shares the digits' prefix: the trailing slash keeps its rows out
    private static readonly string RepositoryId = Repository.DocumentId(EForgeProvider.GitHub, Repo);
    private static readonly string OtherId = Repository.DocumentId(EForgeProvider.GitHub, OtherRepo);
    private const string AccountId = "Accounts/github/6931";

    public static TheoryData<string> Stores => SparkStores.Both;

    private ServiceProvider Provider(string store) => SparkStores.Provider(store, () => GetDocumentStore());

    private static ISparkVisibility Visible(params string[] repositoryIds)
    {
        var visibility = Substitute.For<ISparkVisibility>();
        visibility.GetVisibleRepositoryIdsAsync().Returns(repositoryIds);
        return visibility;
    }

    private static T Create<T>(ISparkVisibility visibility, ISparkSession session)
    {
        var ctor = typeof(T).GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        return (T)ctor.Invoke([.. ctor.GetParameters().Select(p =>
            p.ParameterType == typeof(ISparkVisibility) ? visibility
            : p.ParameterType == typeof(ISparkSession) ? session
            : Substitute.For([p.ParameterType], null))]);
    }

    private static CustomQueryArgs RepositoryArgs(string id) => new()
    {
        Query = new SparkQuery { Id = Guid.NewGuid(), Name = "Test", Source = "Custom.Test" },
        Parent = new PersistentObject { Id = id, Name = "Repository", ObjectTypeId = Guid.NewGuid() },
        ParentType = "Repository",
    };

    private static Task SeedAsync(ServiceProvider provider, EChangelogPolicy repository, bool? account = null)
        => provider.InScopeAsync(async session =>
        {
            await session.StoreAsync(new Repository
            {
                GitHubId = Repo, FullName = "acme/widgets", Provider = EForgeProvider.GitHub, DefaultBranch = "main", Changelog = repository,
                Account = account is null ? null : AccountId,
            }, RepositoryId);
            await session.StoreAsync(new Repository { GitHubId = OtherRepo, FullName = "acme/other", Provider = EForgeProvider.GitHub, DefaultBranch = "main" }, OtherId);
            if (account is { } policy) await session.StoreAsync(new Account { Changelog = policy }, AccountId);

            foreach (var (repo, id, tag, day) in new[] { (Repo, RepositoryId, "Acme.Core/v1.0.0", 1), (Repo, RepositoryId, "Acme.Core/v1.1.0", 3), (OtherRepo, OtherId, "Acme.Other/v1.0.0", 2) })
                await session.StoreAsync(new ChangelogRelease
                {
                    Repository = id, Package = tag.Split('/')[0], Version = tag.Split("/v")[1], Tag = tag,
                    ReleasedAtUtc = new DateTime(2026, 11, day, 12, 0, 0, DateTimeKind.Utc), Notes = "- notes",
                }, ChangelogRelease.DocumentId(EForgeProvider.GitHub, repo, tag));

            foreach (var (repo, id, branch) in new[] { (Repo, RepositoryId, "release/dotnet-11.x"), (Repo, RepositoryId, "main"), (OtherRepo, OtherId, "main") })
                await session.StoreAsync(new UpcomingChangelog
                {
                    Repository = id, Branch = branch, Commit = new string('a', 40), UpdatedAtUtc = DateTime.UnixEpoch,
                    Releases = [new UpcomingChangelogRelease { Package = "Acme.Core", Notes = "- next" }],
                }, UpcomingChangelog.DocumentId(EForgeProvider.GitHub, repo, branch));
            await session.SaveChangesAsync();
        });

    [Fact]
    public async Task A_release_or_a_line_is_readable_exactly_when_its_repository_is_visible()
    {
        var session = Substitute.For<ISparkSession>();
        var release = (await Create<ChangelogReleaseActions>(Visible(RepositoryId), session).GetRowFilterAsync("Read"))!.Compile();
        release(new ChangelogRelease { Repository = RepositoryId }).Should().BeTrue();
        release(new ChangelogRelease { Repository = OtherId }).Should().BeFalse();

        var line = (await Create<UpcomingChangelogActions>(Visible(OtherId), session).GetRowFilterAsync("Query"))!.Compile();
        line(new UpcomingChangelog { Repository = OtherId }).Should().BeTrue();
        line(new UpcomingChangelog { Repository = RepositoryId }).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Repository_Changelog_lists_the_parent_s_releases_only(string store)
    {
        using var provider = Provider(store);
        await SeedAsync(provider, EChangelogPolicy.Enabled);

        await provider.InScopeAsync(async session =>
        {
            var actions = Create<ChangelogReleaseActions>(Visible(RepositoryId), session);
            (await actions.Repository_Changelog(RepositoryArgs(RepositoryId))).Select(r => r.Tag).Order(StringComparer.Ordinal)
                .Should().Equal("Acme.Core/v1.0.0", "Acme.Core/v1.1.0");
            (await actions.Repository_Changelog(RepositoryArgs(Repository.DocumentId(EForgeProvider.GitHub, 404)))).Should().BeEmpty();
        });
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Repository_UpcomingChangelog_lists_the_parent_s_lines_default_branch_first(string store)
    {
        using var provider = Provider(store);
        await SeedAsync(provider, EChangelogPolicy.Enabled);

        await provider.InScopeAsync(async session =>
        {
            var actions = Create<UpcomingChangelogActions>(Visible(RepositoryId), session);
            (await actions.Repository_UpcomingChangelog(RepositoryArgs(RepositoryId))).Select(l => l.Branch)
                .Should().Equal("main", "release/dotnet-11.x");
            (await actions.Repository_UpcomingChangelog(RepositoryArgs(Repository.DocumentId(EForgeProvider.GitHub, 404)))).Should().BeEmpty();
        });
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Repository_UpcomingChangelog_is_empty_once_the_changelog_is_off(string store)
    {
        using var provider = Provider(store);
        await SeedAsync(provider, EChangelogPolicy.Disabled);

        await provider.InScopeAsync(async session =>
            (await Create<UpcomingChangelogActions>(Visible(RepositoryId), session).Repository_UpcomingChangelog(RepositoryArgs(RepositoryId)))
                .Should().BeEmpty());
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Repository_UpcomingChangelog_follows_the_account_when_the_repository_inherits(string store)
    {
        using var provider = Provider(store);
        await SeedAsync(provider, EChangelogPolicy.Inherit, account: true);

        await provider.InScopeAsync(async session =>
            (await Create<UpcomingChangelogActions>(Visible(RepositoryId), session).Repository_UpcomingChangelog(RepositoryArgs(RepositoryId)))
                .Should().HaveCount(2));
    }
}
