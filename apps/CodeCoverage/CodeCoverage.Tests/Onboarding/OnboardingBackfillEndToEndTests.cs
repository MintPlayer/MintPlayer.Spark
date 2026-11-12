#if DEBUG
using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.GitWriter;
using CodeCoverage.GitWriter.Tests._Infrastructure;
using CodeCoverage.MergeQueue;
using CodeCoverage.Onboarding;
using CodeCoverage.PackageVersioning;
using CodeCoverage.Tests.Dependencies.Lifecycle;
using CodeCoverage.Tests.MergeQueue;
using CodeCoverage.Tests.PackageVersioning;
using CodeCoverage.Tests.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CodeCoverage.Tests.Onboarding;

/// <summary>
/// docs/onboarding_backfill_partial_push_PRD.md M2: the onboarding merge end to end. The app's real merge handler and
/// versioning onboarding service, on the Spark test driver's database, talk HTTP to the real git writer (its
/// <see cref="GitWriterHost"/> on a TestServer, as <see cref="DevelopmentGitWriter"/> runs it), which clones from and pushes
/// to a local smart-HTTP git server. Only the forge's REST side (pull request, file reads, tokens) is faked.
/// </summary>
/// <remarks>
/// The repository has the shape of MintPlayer.AspNetCore.Tools when #56 merged (2026-11-12): two packages declaring their
/// versions by hand on the default branch, a line per major, a version the App had tagged at the wrong commit (the owner
/// chose to move it), and a remote that refuses one of the tags, as GitHub refused 45 of Tools' 87.
/// </remarks>
public sealed class OnboardingBackfillEndToEndTests : CoverageRavenTest, IAsyncLifetime
{
    private const long RepoId = 6380;
    private const string AppTagger = "CodeCoverage versioning";
    private const string AppEmail = "versioning@noreply.coverage.mintplayer.com";
    private static readonly string Token = "e2e-token-" + Guid.NewGuid().ToString("N");

    private readonly TempDir origins = new("e2e-origins");
    private readonly TempDir jobs = new("e2e-jobs");
    private GitHttpServer server = null!;
    private WebApplication writer = null!;

    public async Task InitializeAsync()
    {
        server = await GitHttpServer.StartAsync(origins.Path);
        server.RequireCredential = ("x-access-token", Token);
        var builder = GitWriterHost.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(GitWriterHost).Assembly.GetName().Name,
            EnvironmentName = "Production",
            ContentRootPath = jobs.Path,
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GitWriter:Root"] = jobs.Path,
            ["GitWriter:MinFreeBytes"] = "0",
            // The local server has no TLS; production accepts https only.
            ["GitWriter:AllowInsecureHttp"] = "true",
        });
        writer = GitWriterHost.Build(builder);
        await writer.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await writer.DisposeAsync();
        await server.DisposeAsync();
        jobs.Dispose();
        origins.Dispose();
    }

    /// <summary>No other HTTP leaves the test: the forge's REST side is faked.</summary>
    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("offline");
    }

    private static string Csproj(string id, string version)
        => $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <PackageId>{id}</PackageId>\n"
            + $"    <Version>{version}</Version>\n  </PropertyGroup>\n</Project>\n";

    [Fact]
    public async Task A_merge_tags_the_history_on_each_line_moves_the_chosen_tag_and_names_the_tag_the_remote_refused()
    {
        // The history: both packages at 10.0.0, then 10.0.1, then 11.0.0; the onboarding commit hands versions to the tags.
        using var origin = new Origin(server, "tools");
        var v10 = origin.Write("src/A/A.csproj", Csproj("Acme.A", "10.0.0")).Write("src/B/B.csproj", Csproj("Acme.B", "10.0.0")).Commit("10.0.0");
        var v1001 = origin.Write("src/A/A.csproj", Csproj("Acme.A", "10.0.1")).Write("src/B/B.csproj", Csproj("Acme.B", "10.0.1")).Commit("10.0.1");
        origin.Push("main");
        origin.Checkout("release/dotnet-10.x", "main").Push("release/dotnet-10.x");
        var v11 = origin.Checkout("main").Write("src/A/A.csproj", Csproj("Acme.A", "11.0.0")).Write("src/B/B.csproj", Csproj("Acme.B", "11.0.0")).Commit("11.0.0");
        var merge = origin.Write("src/A/A.csproj", Csproj("Acme.A", "0.0.0-PLACEHOLDER")).Write("src/B/B.csproj", Csproj("Acme.B", "0.0.0-PLACEHOLDER"))
            .Write(CoverageYml.FileName, "versioning:\n  lines: {}\n").Commit("Set up MintPlayer CodeCoverage (#56)");
        origin.Push("main");
        origin.Checkout("release/dotnet-11.x", "main").Push("release/dotnet-11.x");
        // The App's first publish on the 10.x line tagged Acme.A 10.0.0 at the line's head (Tools: v10.0.0 at 8b1472c).
        origin.InBare("-c", $"user.name={AppTagger}", "-c", $"user.email={AppEmail}", "tag", "-a", "-m", "released", "Acme.A/v10.0.0", v1001);
        // GitHub refused part of the push (Tools #56); its reason is the remote's to give.
        origin.Refuse("refs/tags/Acme.B/v11.0.0");

        using var store = GetDocumentStore();
        var repository = new Repository { GitHubId = RepoId, Name = "tools", FullName = "acme/tools", OwnerLogin = "acme", DefaultBranch = "main" };
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(repository, Repository.DocumentId(EForgeProvider.GitHub, RepoId));
            // What Set up features stored when it opened #56: versioning only, nothing to seed, the owner's "Move them".
            await seed.StoreAsync(new VersioningOnboardingState
            {
                PullRequestNumber = 56, BaseSha = v11, Features = [EOnboardingFeature.Versioning], MoveTags = ["Acme.A/v10.0.0"], Backfill = true,
            }, VersioningOnboardingState.ForRepository(repository.Id!));
            await seed.SaveChangesAsync();
        }

        var forge = new ScriptedDiffService();
        forge.Files[$"main/{CoverageYml.FileName}"] = "versioning:\n  lines: {}\n";
        var versioning = new FakeForgeVersioning
        {
            PullRequest = new ForgePullRequestText(56, OnboardingPlanner.Title, null, OnboardingPlanner.Branch, v11, "main", false, true, true, merge, []),
        };
        var queue = new FakeForgeMergeQueue
        {
            // GitHub refuses an App's tag on a commit whose workflow files differ, unless the token holds `workflows: write`
            // (Tools, 2026-11-12: "refusing to allow a GitHub App to create or update workflow … without `workflows`
            // permission"). The local server cannot tell tokens apart, so only a workflows token gets its credential.
            WriterAccess = (_, workflows) => workflows
                ? new ForgePushAccess(new ForgeWriterAccess(origin.Url.AbsoluteUri, origin.Url.AbsoluteUri, "x-access-token", Token), null)
                : ForgePushAccess.Refused("a contents-only token cannot push tags onto history that changes workflow files"),
        };
        using var session = store.OpenAsyncSession();
        var services = new ServiceCollection();
        services.AddLogging(l => l.SetMinimumLevel(LogLevel.None));
        services.AddSingleton(session);
        services.AddSingleton<MintPlayer.Spark.Messaging.Abstractions.IMessageBus>(new RecordingMessageBus());
        services.AddSingleton<IForgeIntegration>(forge);
        services.AddSingleton<IForgeIntegrationResolver>(forge);
        services.AddSingleton<IForgeMergeQueue>(queue);
        services.AddSingleton<IForgeDependencyUpdates>(new FakeForgeDependencyUpdates());
        services.AddSingleton<IForgeVersioning>(versioning);
        // The real writer, over HTTP.
        services.AddSingleton<IGitWriterSidecar>(new GitWriterSidecarClient(
            new HttpClient(writer.GetTestServer().CreateHandler()) { BaseAddress = new Uri("http://git-writer/"), Timeout = TimeSpan.FromMinutes(3) },
            Microsoft.Extensions.Options.Options.Create(new MergeQueueOptions())));
        services.AddSingleton(TimeProvider.System);
        services.AddHttpClient(Microsoft.Extensions.Options.Options.DefaultName).ConfigurePrimaryHttpMessageHandler(() => new Offline());
        services.AddScoped<IVersioningOnboardingService, VersioningOnboardingService>();
        services.AddScoped<IOnboardingService, OnboardingService>();
        using var provider = services.BuildServiceProvider();
        var loaded = (await session.LoadAsync<Repository>(Repository.DocumentId(EForgeProvider.GitHub, RepoId)))!;

        var merged = await provider.GetRequiredService<IOnboardingService>().MergedAsync(loaded);

        merged.Outcome.Should().Be(VersioningOnboardingOutcome.Merged, merged.Note);
        // Each version at its earliest occurrence, on the line of its major (#638 D7).
        string Tagged(string tag) => origin.InBare("rev-parse", tag + "^{commit}").Trim();
        Tagged("Acme.B/v10.0.0").Should().Be(v10);
        Tagged("Acme.A/v10.0.1").Should().Be(v1001);
        Tagged("Acme.B/v10.0.1").Should().Be(v1001);
        Tagged("Acme.A/v11.0.0").Should().Be(v11);
        // D2: the owner's move ran although the remote refused another tag of the same backfill.
        Tagged("Acme.A/v10.0.0").Should().Be(v10);
        // D1, D4: the refused tag is not there, and the note says which one and why, in the remote's words.
        origin.Ref("refs/tags/Acme.B/v11.0.0").Should().BeNull();
        merged.Note.Should().Contain("moved 1 tag(s) onto their earliest occurrence")
            .And.Contain("not tagged: Acme.B/v11.0.0 (rejected").And.Contain(Origin.RefusedMessage);

        using var read = store.OpenAsyncSession();
        var state = (await read.LoadAsync<VersioningOnboardingState>(VersioningOnboardingState.ForRepository(repository.Id!)))!;
        (state.MergeHandledAt, state.BackfilledAt).Should().Be((merge, merge), "the merge is handled once; a refusal is reported, not retried forever");
    }
}
#endif
