using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using CodeCoverage.Onboarding;
using CodeCoverage.PackageVersioning;
using CodeCoverage.Tests.Dependencies.Lifecycle;
using CodeCoverage.Tests.MergeQueue;
using CodeCoverage.Tests.PackageVersioning;
using CodeCoverage.Tests.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.Onboarding;

/// <summary>
/// #571 M10c (coverage_backport_PRD D3): the chooser's one pull request on <c>mintplayer/onboarding</c>, opened on the default
/// branch's head and rebuilt on every new head while it is open.
/// </summary>
public class OnboardingServiceTests : CoverageRavenTest
{
    private const long RepoId = 5710;
    private const string Head = "5710571057105710571057105710571057105710";
    private const string NextHead = "5711571157115711571157115711571157115711";

    private const string Csproj = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net11.0</TargetFramework>\n"
        + "    <PackageId>Acme.Tools</PackageId>\n    <Version>11.2.0</Version>\n  </PropertyGroup>\n</Project>\n";

    private sealed record Harness(IOnboardingService Service, FakeGitWriter Writer, FakeForgeDependencyUpdates Pulls, ScriptedDiffService Forge, FakeForgeMergeQueue Queue);

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("offline");
    }

    private static Harness Create(IDocumentStore store, ScriptedDiffService? forge = null, FakeForgeDependencyUpdates? pulls = null)
    {
        forge ??= Repository(Head);
        pulls ??= new FakeForgeDependencyUpdates();
        var queue = new FakeForgeMergeQueue();
        queue.BranchHeads["main"] = forge.Trees["main"].CommitSha;
        queue.BranchHeads["release/6.0"] = "6060606060606060606060606060606060606060";
        var writer = new FakeGitWriter();

        var services = new ServiceCollection();
        services.AddLogging(l => l.SetMinimumLevel(LogLevel.None));
        services.AddSingleton(store.OpenAsyncSession());
        services.AddSingleton<IForgeIntegration>(forge);
        services.AddSingleton<IForgeIntegrationResolver>(forge);
        services.AddSingleton<IForgeMergeQueue>(queue);
        services.AddSingleton<IForgeDependencyUpdates>(pulls);
        services.AddSingleton<IForgeVersioning>(new FakeForgeVersioning());
        services.AddSingleton<IGitWriterSidecar>(writer);
        services.AddSingleton(TimeProvider.System);
        services.AddHttpClient(Microsoft.Extensions.Options.Options.DefaultName).ConfigurePrimaryHttpMessageHandler(() => new Offline());
        services.AddScoped<IVersioningOnboardingService, VersioningOnboardingService>();
        services.AddScoped<IOnboardingService, OnboardingService>();
        return new Harness(services.BuildServiceProvider().GetRequiredService<IOnboardingService>(), writer, pulls, forge, queue);
    }

    /// <summary>A .NET repository with coverage.yml at <paramref name="head"/>.</summary>
    private static ScriptedDiffService Repository(string head, ScriptedDiffService? forge = null)
    {
        forge ??= new ScriptedDiffService();
        forge.Trees["main"] = new ForgeTree(head, "tree-" + head[..4], [new ForgeTreeEntry("src/Tools/Tools.csproj", Csproj.Length), new ForgeTreeEntry("coverage.yml", 30)], false);
        forge.Files[$"{head}/src/Tools/Tools.csproj"] = Csproj;
        forge.Files[$"{head}/{CoverageYml.FileName}"] = "gate:\n  patchTarget: 80\n";
        return forge;
    }

    private static async Task<Repository> SeedRepositoryAsync(IDocumentStore store)
    {
        var repository = new Repository { GitHubId = RepoId, Name = "tools", FullName = "acme/tools", OwnerLogin = "acme", DefaultBranch = "main" };
        using var seed = store.OpenAsyncSession();
        await seed.StoreAsync(repository, CodeCoverage.Entities.Repository.DocumentId(EForgeProvider.GitHub, RepoId));
        await seed.SaveChangesAsync();
        return repository;
    }

    private static async Task<VersioningOnboardingState> StateAsync(IDocumentStore store, Repository repository)
    {
        using var read = store.OpenAsyncSession();
        return (await read.LoadAsync<VersioningOnboardingState>(VersioningOnboardingState.ForRepository(repository.Id!)))!;
    }

    [Fact]
    public async Task D3_one_pull_request_on_the_onboarding_branch_carries_every_picked_feature()
    {
        using var store = GetDocumentStore();
        var repository = await SeedRepositoryAsync(store);
        var h = Create(store);

        var outcome = await h.Service.OnboardAsync(repository, new HashSet<EOnboardingFeature> { EOnboardingFeature.ReleaseLines, EOnboardingFeature.DependencyUpdates });

        outcome.Outcome.Should().Be(VersioningOnboardingOutcome.Opened, outcome.Note);
        var push = h.Writer.Requests("onboarding").Single();
        push.ExpectedSha.Should().Be(Head);
        push.Message.Should().Be(OnboardingPlanner.Title);
        push.Files!.Select(f => f.Path).Should().Contain("coverage.yml").And.Contain(".github/workflows/dependency-lookup.yml");
        var created = h.Pulls.Created.Single();
        (created.Head, created.Base, created.Title).Should().Be((OnboardingPlanner.Branch, "main", OnboardingPlanner.Title));
        created.Body.Should().Contain("release/dotnet-6.x");

        var state = await StateAsync(store, repository);
        state.PullRequestNumber.Should().Be(outcome.PullRequest);
        state.BaseSha.Should().Be(Head);
        state.Features.Should().BeEquivalentTo([EOnboardingFeature.ReleaseLines, EOnboardingFeature.Versioning, EOnboardingFeature.DependencyUpdates]);
        state.OnMerge.Should().Equal([EOnboardingFeature.DependencyUpdates]);
        state.Renames.Select(r => $"{r.From} -> {r.To}").Should().Equal(["release/6.0 -> release/dotnet-6.x"]);
        state.Seeds.Select(s => $"{s.PackageId}@{s.Version}").Should().Equal(["Acme.Tools@11.2.0"]);
        h.Queue.RevokedTokens.Should().HaveCount(1, "the push credential is ended after the push");
    }

    [Fact]
    public async Task D3_a_new_default_branch_head_rebuilds_the_open_pull_request_and_the_same_head_does_nothing()
    {
        using var store = GetDocumentStore();
        var repository = await SeedRepositoryAsync(store);
        var pulls = new FakeForgeDependencyUpdates();
        var forge = Repository(Head);
        var opened = await Create(store, forge, pulls).Service.OnboardAsync(repository, new HashSet<EOnboardingFeature> { EOnboardingFeature.MergeQueue });

        var same = Create(store, forge, pulls);
        (await same.Service.RebuildAsync(repository)).Outcome.Should().Be(VersioningOnboardingOutcome.Nothing);
        same.Writer.Requests("onboarding").Should().BeEmpty();

        Repository(NextHead, forge);
        var moved = Create(store, forge, pulls);
        (await moved.Service.RebuildAsync(repository)).Outcome.Should().Be(VersioningOnboardingOutcome.Opened);
        moved.Writer.Requests("onboarding").Single().ExpectedSha.Should().Be(NextHead);
        pulls.Created.Should().HaveCount(1, "the open pull request is updated, not opened again");
        pulls.Updated.Single().Number.Should().Be(opened.PullRequest!.Value);
        (await StateAsync(store, repository)).BaseSha.Should().Be(NextHead);
    }

    [Fact]
    public async Task D3_a_closed_pull_request_is_not_rebuilt()
    {
        using var store = GetDocumentStore();
        var repository = await SeedRepositoryAsync(store);
        var pulls = new FakeForgeDependencyUpdates();
        var forge = Repository(Head);
        var opened = await Create(store, forge, pulls).Service.OnboardAsync(repository, new HashSet<EOnboardingFeature> { EOnboardingFeature.MergeQueue });
        await pulls.ClosePullRequestAsync(repository, opened.PullRequest!.Value);

        Repository(NextHead, forge);
        var moved = Create(store, forge, pulls);
        (await moved.Service.RebuildAsync(repository)).Outcome.Should().Be(VersioningOnboardingOutcome.Nothing);
        moved.Writer.Requests("onboarding").Should().BeEmpty();
    }

    [Fact]
    public async Task D3_the_preview_plans_without_writing_anything()
    {
        using var store = GetDocumentStore();
        var repository = await SeedRepositoryAsync(store);
        var h = Create(store);

        var preview = await h.Service.PreviewAsync(repository, new HashSet<EOnboardingFeature> { EOnboardingFeature.SizeLabels });

        preview.Plan!.OnMerge.Should().Equal([EOnboardingFeature.SizeLabels]);
        h.Writer.DependencyJobs.Should().BeEmpty();
        h.Pulls.Created.Should().BeEmpty();
        using var read = store.OpenAsyncSession();
        (await read.LoadAsync<VersioningOnboardingState>(VersioningOnboardingState.ForRepository(repository.Id!))).Should().BeNull();
    }
}
