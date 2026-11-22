using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.LookupReferences;
using CodeCoverage.PackageVersioning;
using CodeCoverage.Tests._Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Storage;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>
/// #693 M11 (D3, D16): the changelog copy of a package Release, written and read through <see cref="ISparkSession"/> only, so
/// every case runs on the in-memory store and on RavenDB.
/// </summary>
public class ChangelogRecipientTests : CoverageRavenTest
{
    private const long Repo = 7201;
    private const long Sibling = 72010;
    private static readonly string RepoId = Repository.DocumentId(EForgeProvider.GitHub, Repo);
    private static readonly string SiblingId = Repository.DocumentId(EForgeProvider.GitHub, Sibling);
    private static readonly DateTimeOffset Monday = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> Stores => ["memory", "raven"];

    private sealed class Builder(IServiceCollection services) : ISparkBuilder
    {
        public IServiceCollection Services => services;
        public IConfiguration? Configuration => null;
        public SparkModuleRegistry Registry { get; } = new();
    }

    /// <summary>One provider's services; every <see cref="ISparkSession"/> comes from a scope of its own.</summary>
    private ServiceProvider Provider(string store)
    {
        var services = new ServiceCollection().AddLogging();
        if (store == "memory") new Builder(services).UseInMemory();
        else services.AddSingleton<IDocumentStore>(GetDocumentStore()).AddRavenDbSessions();
        return services.BuildServiceProvider();
    }

    private static async Task InScopeAsync(ServiceProvider provider, Func<ISparkSession, Task> work)
    {
        using var scope = provider.CreateScope();
        await work(scope.ServiceProvider.GetRequiredService<ISparkSession>());
    }

    private static async Task SeedAsync(ServiceProvider provider, EChangelogPolicy policy = EChangelogPolicy.Enabled, bool accountChangelog = false)
        => await InScopeAsync(provider, async session =>
        {
            await session.StoreAsync(new Account { Changelog = accountChangelog }, "Accounts/github/1");
            foreach (var (id, gitHubId) in new[] { (RepoId, Repo), (SiblingId, Sibling) })
                await session.StoreAsync(new Repository
                {
                    GitHubId = gitHubId, Name = "lib", FullName = $"acme/lib{gitHubId}", OwnerLogin = "acme", Account = "Accounts/github/1", Changelog = policy,
                }, id);
            await session.SaveChangesAsync();
        });

    private static Task PublishAsync(ServiceProvider provider, string repositoryId, string tag, string body, DateTimeOffset at, bool prerelease = false)
        => InScopeAsync(provider, session => new ChangelogRecipient(session).HandleAsync(new ForgeWebhookMessage<ReleasePublished>(EForgeProvider.GitHub,
            new ReleasePublished(repositoryId, tag, at, new ForgeReleaseNotes(tag, body, prerelease, "master")))));

    private static async Task<List<ChangelogRelease>> ListAsync(ServiceProvider provider, string repositoryId)
    {
        List<ChangelogRelease> releases = [];
        await InScopeAsync(provider, async session =>
            releases = await ChangelogRecipient.ForRepositoryAsync(session, (await session.LoadAsync<Repository>(repositoryId))!));
        return releases;
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task A_package_release_is_copied_and_the_run_release_is_not(string store)
    {
        using var provider = Provider(store);
        await SeedAsync(provider);

        await PublishAsync(provider, RepoId, "Acme.Core/v11.2.0", "### Features\n- Faster (#12)", Monday);
        await PublishAsync(provider, RepoId, "Acme.Core/v11.3.0-preview.1", "### Fixes\n- Crash (#13)", Monday.AddDays(1), prerelease: true);
        await PublishAsync(provider, RepoId, "release/2026-11-23", "Every package", Monday.AddDays(1));
        await PublishAsync(provider, SiblingId, "Acme.Core/v1.0.0", "First release.", Monday.AddDays(2));

        var releases = await ListAsync(provider, RepoId);
        releases.Select(r => (r.Package, r.Version, r.Prerelease)).Should().Equal(
            ("Acme.Core", "11.3.0-preview.1", true), ("Acme.Core", "11.2.0", false));
        var first = releases[1];
        (first.Id, first.Repository, first.Tag, first.Target, first.Notes, first.ReleasedAtUtc).Should().Be(
            (ChangelogRelease.DocumentId(EForgeProvider.GitHub, Repo, "Acme.Core/v11.2.0"), RepoId, "Acme.Core/v11.2.0", (string?)"master",
                "### Features\n- Faster (#12)", Monday.UtcDateTime));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task An_edit_updates_the_notes_and_keeps_the_published_time(string store)
    {
        using var provider = Provider(store);
        await SeedAsync(provider);
        await PublishAsync(provider, RepoId, "Acme.Core/v11.2.0", "### Features\n- Fsater (#12)", Monday);

        await InScopeAsync(provider, session => new ChangelogRecipient(session).HandleAsync(new ForgeWebhookMessage<ReleaseEdited>(EForgeProvider.GitHub,
            new ReleaseEdited(RepoId, "Acme.Core/v11.2.0", new ForgeReleaseNotes("Acme.Core 11.2.0", "### Features\n- Faster (#12)", false, "master")))));

        var release = (await ListAsync(provider, RepoId)).Should().ContainSingle().Which;
        (release.Notes, release.ReleasedAtUtc).Should().Be(("### Features\n- Faster (#12)", Monday.UtcDateTime));
    }

    [Theory]
    [InlineData("memory", EChangelogPolicy.Inherit, false, false)]
    [InlineData("memory", EChangelogPolicy.Inherit, true, true)]
    [InlineData("memory", EChangelogPolicy.Disabled, true, false)]
    [InlineData("raven", EChangelogPolicy.Enabled, false, true)]
    public async Task Only_a_repository_with_the_changelog_on_is_copied(string store, EChangelogPolicy policy, bool accountChangelog, bool copied)
    {
        using var provider = Provider(store);
        await SeedAsync(provider, policy, accountChangelog);

        await PublishAsync(provider, RepoId, "Acme.Core/v11.2.0", "notes", Monday);

        (await ListAsync(provider, RepoId)).Should().HaveCount(copied ? 1 : 0);
    }

    [Fact]
    public void Title_correction_follows_the_changelog_unless_the_repository_decides()
    {
        var on = new Account { Changelog = true };
        Repository.ResolvePrTitleCorrection(new Repository(), on).Should().Be((true, "organization changelog"));
        Repository.ResolvePrTitleCorrection(new Repository { PrTitleCorrection = EPrTitlePolicy.Disabled }, on).Should().Be((false, "repository"));
        Repository.ResolvePrTitleCorrection(new Repository { Changelog = EChangelogPolicy.Enabled }, new Account()).Should().Be((true, "repository changelog"));
        Repository.ResolvePrTitleCorrection(new Repository(), new Account()).Should().Be((false, "organization"));
        Repository.ResolvePrTemplate(new Repository(), null).Should().Be((true, "default"));
        Repository.ResolvePrTemplate(new Repository(), new Account { PrTemplate = EPrTemplateDefault.Disabled }).Should().Be((false, "organization"));
        Repository.ResolvePrTemplate(new Repository { PrTemplate = EPrTemplatePolicy.Enabled }, new Account { PrTemplate = EPrTemplateDefault.Disabled })
            .Should().Be((true, "repository"));
    }
}
