using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.LookupReferences;
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

    private sealed record Harness(IOnboardingService Service, FakeGitWriter Writer, FakeForgeDependencyUpdates Pulls, ScriptedDiffService Forge, FakeForgeMergeQueue Queue,
        FakeForgeVersioning Versioning, RecordingMessageBus Bus, Raven.Client.Documents.Session.IAsyncDocumentSession Session);

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
        var versioning = new FakeForgeVersioning();
        var bus = new RecordingMessageBus();
        var session = store.OpenAsyncSession();

        var services = new ServiceCollection();
        services.AddLogging(l => l.SetMinimumLevel(LogLevel.None));
        services.AddSingleton(session);
        services.AddSingleton<MintPlayer.Spark.Messaging.Abstractions.IMessageBus>(bus);
        services.AddSingleton<IForgeIntegration>(forge);
        services.AddSingleton<IForgeIntegrationResolver>(forge);
        services.AddSingleton<IForgeMergeQueue>(queue);
        services.AddSingleton<IForgeDependencyUpdates>(pulls);
        services.AddSingleton<IForgeVersioning>(versioning);
        services.AddSingleton<IGitWriterSidecar>(writer);
        services.AddSingleton(TimeProvider.System);
        services.AddHttpClient(Microsoft.Extensions.Options.Options.DefaultName).ConfigurePrimaryHttpMessageHandler(() => new Offline());
        services.AddScoped<IVersioningOnboardingService, VersioningOnboardingService>();
        services.AddScoped<IOnboardingService, OnboardingService>();
        return new Harness(services.BuildServiceProvider().GetRequiredService<IOnboardingService>(), writer, pulls, forge, queue, versioning, bus, session);
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

        var outcome = await h.Service.OnboardAsync(repository, new HashSet<EOnboardingFeature> { EOnboardingFeature.ReleaseLines, EOnboardingFeature.DependencyUpdates }, []);

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
        state.Cuts.Should().Equal(["release/dotnet-11.x"], "the current line has no branch (docs/onboarding_line_cuts_PRD.md D1)");
        h.Versioning.Branches.Should().BeEmpty("lines are created at the merge, from the commit that carries their triggers (D3)");
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
        var opened = await Create(store, forge, pulls).Service.OnboardAsync(repository, new HashSet<EOnboardingFeature> { EOnboardingFeature.MergeQueue }, []);

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
    public async Task Item615_the_form_starts_from_the_repository_plus_an_open_pull_requests_features_and_drops_a_closed_ones()
    {
        using var store = GetDocumentStore();
        var repository = await SeedRepositoryAsync(store);
        var pulls = new FakeForgeDependencyUpdates();
        var forge = Repository(Head);
        forge.Files[$"{Head}/{CoverageYml.FileName}"] = "versioning: {}\n";
        var h = Create(store, forge, pulls);
        // Only coverage.yml has anything.
        (await h.Service.CurrentFeaturesAsync(repository)).Should().BeEquivalentTo([EOnboardingFeature.Versioning]);

        // The open pull request's choice counts.
        var opened = await h.Service.OnboardAsync(repository, new HashSet<EOnboardingFeature> { EOnboardingFeature.MergeQueue }, []);
        (await Create(store, forge, pulls).Service.CurrentFeaturesAsync(repository))
            .Should().BeEquivalentTo([EOnboardingFeature.Versioning, EOnboardingFeature.MergeQueue]);

        // A closed pull request applied nothing.
        await pulls.ClosePullRequestAsync(repository, opened.PullRequest!.Value);
        (await Create(store, forge, pulls).Service.CurrentFeaturesAsync(repository))
            .Should().BeEquivalentTo([EOnboardingFeature.Versioning]);
    }

    [Fact]
    public async Task D3_a_closed_pull_request_is_not_rebuilt()
    {
        using var store = GetDocumentStore();
        var repository = await SeedRepositoryAsync(store);
        var pulls = new FakeForgeDependencyUpdates();
        var forge = Repository(Head);
        var opened = await Create(store, forge, pulls).Service.OnboardAsync(repository, new HashSet<EOnboardingFeature> { EOnboardingFeature.MergeQueue }, []);
        await pulls.ClosePullRequestAsync(repository, opened.PullRequest!.Value);

        Repository(NextHead, forge);
        var moved = Create(store, forge, pulls);
        (await moved.Service.RebuildAsync(repository)).Outcome.Should().Be(VersioningOnboardingOutcome.Nothing);
        moved.Writer.Requests("onboarding").Should().BeEmpty();
    }

    [Fact]
    public async Task M10d_the_merge_switches_settings_on_renames_the_lines_and_adds_the_regenerate_workflow_to_each_line()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store);
        var h = Create(store);
        var repository = (await h.Session.LoadAsync<Repository>(CodeCoverage.Entities.Repository.DocumentId(EForgeProvider.GitHub, RepoId)))!;
        var opened = await h.Service.OnboardAsync(repository, new HashSet<EOnboardingFeature>
            { EOnboardingFeature.ReleaseLines, EOnboardingFeature.MergeQueue, EOnboardingFeature.SizeLabels, EOnboardingFeature.DependencyUpdates }, []);
        opened.Outcome.Should().Be(VersioningOnboardingOutcome.Opened, opened.Note);

        // The merge: what the pull request wrote is on main now.
        h.Forge.Files[$"main/{CoverageYml.FileName}"] = "versioning:\n  lines: {}\nmergeQueue:\n  enabled: true\n  postRebaseActionName: regenerate.yml\n";
        h.Forge.Files["main/.github/workflows/regenerate.yml"] = "name: regenerate\n";
        h.Versioning.PullRequest = new ForgePullRequestText(opened.PullRequest!.Value, OnboardingPlanner.Title, null, OnboardingPlanner.Branch, Head, "main",
            false, true, true, "abcdabcdabcdabcdabcdabcdabcdabcdabcdabcd", []);

        var merged = await h.Service.MergedAsync(repository);

        merged.Outcome.Should().Be(VersioningOnboardingOutcome.Merged, merged.Note);
        h.Queue.Renames.Should().Equal([("release/6.0", "release/dotnet-6.x")]);
        // docs/onboarding_line_cuts_PRD.md D1, D3: the current line is created from the merge commit.
        h.Versioning.Branches.Should().Equal([("release/dotnet-11.x", "abcdabcdabcdabcdabcdabcdabcdabcdabcdabcd")]);
        merged.Note.Should().Contain("created release/dotnet-11.x");
        var line = h.Writer.Requests("onboarding").Last();
        (line.Line, line.BaseBranch, line.ExpectedSha).Should().Be(("dotnet-6.x", "release/dotnet-6.x", "6060606060606060606060606060606060606060"));
        line.Files!.Select(f => (f.Path, f.Content)).Should().Equal([(".github/workflows/regenerate.yml", "name: regenerate\n")]);
        var pull = h.Pulls.Created.Last();
        (pull.Head, pull.Base, pull.Title).Should().Be((OnboardingPlanner.LineBranch("dotnet-6.x"), "release/dotnet-6.x", OnboardingPlanner.LineTitle));

        using (var read = store.OpenAsyncSession())
        {
            var saved = (await read.LoadAsync<Repository>(repository.Id))!;
            (saved.DependencyUpdates, saved.PrSizeLabels).Should().Be((EDependencyUpdatesPolicy.Enabled, EPrSizeLabelPolicy.Enabled));
        }
        h.Bus.Of<CodeCoverage.Dependencies.Lifecycle.DependencyUpdateSignalMessage>().Should().ContainSingle();
        (await StateAsync(store, repository)).MergeHandledAt.Should().NotBeNull();

        (await h.Service.MergedAsync(repository)).Outcome.Should().Be(VersioningOnboardingOutcome.Nothing, "a merge is handled once");
        h.Queue.Renames.Should().HaveCount(1);
    }

    [Fact]
    public async Task Collisions_D9_the_preview_lists_them_and_the_merge_moves_the_ones_the_owner_chose()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store);
        var h = Create(store);
        var repository = (await h.Session.LoadAsync<Repository>(CodeCoverage.Entities.Repository.DocumentId(EForgeProvider.GitHub, RepoId)))!;
        h.Forge.Files[$"main/{CoverageYml.FileName}"] = "versioning: {}\n";
        h.Writer.Backfill = request => Task.FromResult(new GitWriterBackfillResult(
            request.PlanOnly ? GitWriterBackfillResult.Planned : GitWriterBackfillResult.Tagged, null, [], ["main"], [], null, 1)
        {
            Collisions = [new GitWriterBackfillCollision("Acme.Tools/v11.1.0", "8b1472c000000000000000000000000000000000", "abc1230000000000000000000000000000000000", "main",
                request.PlanOnly ? "planned" : request.Move?.Contains("Acme.Tools/v11.1.0") == true ? GitWriterBackfillCollision.Moved : "left", null)],
        });
        var features = new HashSet<EOnboardingFeature> { EOnboardingFeature.ReleaseLines };

        var preview = await h.Service.PreviewAsync(repository, features);

        preview.Plan!.TagCollisions.Select(c => $"{c.Name}@{c.Outcome}").Should().Equal("Acme.Tools/v11.1.0@planned");
        h.Writer.BackfillRequests.Should().ContainSingle(r => r.PlanOnly, "the preview writes nothing");

        var opened = await h.Service.OnboardAsync(repository, features, [], default, ["Acme.Tools/v11.1.0"]);
        (await StateAsync(store, repository)).MoveTags.Should().Equal(["Acme.Tools/v11.1.0"]);
        h.Versioning.PullRequest = new ForgePullRequestText(opened.PullRequest!.Value, OnboardingPlanner.Title, null, OnboardingPlanner.Branch, Head, "main",
            false, true, true, "abcdabcdabcdabcdabcdabcdabcdabcdabcdabcd", []);

        var merged = await h.Service.MergedAsync(repository);

        h.Writer.BackfillRequests.Last().Move.Should().Equal(["Acme.Tools/v11.1.0"]);
        h.Writer.BackfillRequests.Last().PlanOnly.Should().BeFalse();
        merged.Note.Should().Contain("moved 1 tag(s) onto their earliest occurrence");
    }

    [Fact]
    public async Task Backfill_D7_the_merge_tags_the_history_after_the_line_cuts_and_seeds_only_what_it_did_not_tag()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store);
        var h = Create(store);
        var repository = (await h.Session.LoadAsync<Repository>(CodeCoverage.Entities.Repository.DocumentId(EForgeProvider.GitHub, RepoId)))!;
        var opened = await h.Service.OnboardAsync(repository, new HashSet<EOnboardingFeature> { EOnboardingFeature.ReleaseLines }, []);
        opened.Outcome.Should().Be(VersioningOnboardingOutcome.Opened, opened.Note);
        h.Forge.Files[$"main/{CoverageYml.FileName}"] = "versioning:\n  lines: {}\n";
        const string merge = "abcdabcdabcdabcdabcdabcdabcdabcdabcdabcd";
        h.Versioning.PullRequest = new ForgePullRequestText(opened.PullRequest!.Value, OnboardingPlanner.Title, null, OnboardingPlanner.Branch, Head, "main",
            false, true, true, merge, []);
        var linesAtBackfill = -1;
        h.Writer.Backfill = request =>
        {
            linesAtBackfill = h.Versioning.Branches.Count;
            return Task.FromResult(new GitWriterBackfillResult(GitWriterBackfillResult.Tagged, null, [
                new GitWriterBackfillTag("Acme.Tools/v11.1.0", "1111111111111111111111111111111111111111", "release/dotnet-11.x", GitWriterTagResult.Created, null),
                new GitWriterBackfillTag("Acme.Tools/v11.2.0", "2222222222222222222222222222222222222222", "release/dotnet-11.x", GitWriterTagResult.Created, null),
            ], ["main", "release/dotnet-11.x"], [], null, 1));
        };

        var merged = await h.Service.MergedAsync(repository);

        merged.Outcome.Should().Be(VersioningOnboardingOutcome.Merged, merged.Note);
        h.Writer.BackfillRequests.Should().ContainSingle();
        var backfill = h.Writer.BackfillRequests.Single();
        (backfill.DefaultBranch, backfill.PlanOnly, backfill.Settings.Lines is not null).Should().Be(("main", false, true));
        linesAtBackfill.Should().Be(1, "D7: settings, renames, cuts, then the backfill, so a version goes on the line of its major");
        h.Writer.TagRequests.Should().BeEmpty("the backfill tagged the seed's version at its earliest occurrence already");
        (await StateAsync(store, repository)).SeedCommit.Should().Be(merge, "the seeds are done");
        merged.Note.Should().Contain("tagged 2 version(s) from the history");
    }

    [Fact]
    public async Task M10d_a_settings_only_choice_applies_at_once_without_a_pull_request()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store);
        var h = Create(store);
        var repository = (await h.Session.LoadAsync<Repository>(CodeCoverage.Entities.Repository.DocumentId(EForgeProvider.GitHub, RepoId)))!;

        var outcome = await h.Service.OnboardAsync(repository, new HashSet<EOnboardingFeature> { EOnboardingFeature.SizeLabels }, []);

        outcome.Outcome.Should().Be(VersioningOnboardingOutcome.Merged, outcome.Note);
        h.Pulls.Created.Should().BeEmpty();
        using var read = store.OpenAsyncSession();
        (await read.LoadAsync<Repository>(repository.Id))!.PrSizeLabels.Should().Be(EPrSizeLabelPolicy.Enabled);
    }

    private const string MultiTarget = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFrameworks>net10.0;net11.0</TargetFrameworks>\n"
        + "    <PackageId>Acme.Tools</PackageId>\n    <Version>11.2.0</Version>\n  </PropertyGroup>\n</Project>\n";

    /// <summary>The onboarding pull request merged: its files are on <c>main</c> at <paramref name="head"/>.</summary>
    private static void MergeInto(ScriptedDiffService forge, string head, IReadOnlyList<GitWriterFile> files)
    {
        var old = forge.Trees["main"];
        var contents = old.Entries.ToDictionary(e => e.Path, e => forge.Files[$"{old.CommitSha}/{e.Path}"], StringComparer.Ordinal);
        foreach (var file in files)
            if (file.Delete) contents.Remove(file.Path);
            else contents[file.Path] = file.Content!;
        forge.Trees["main"] = new ForgeTree(head, "tree-" + head[..4], [.. contents.Select(c => new ForgeTreeEntry(c.Key, c.Value.Length))], false);
        foreach (var (path, content) in contents)
            forge.Files[$"{head}/{path}"] = content;
    }

    [Fact]
    public async Task LineCuts_D2_D3_an_onboarded_repository_gets_its_lines_at_once_and_the_chosen_older_line_too()
    {
        using var store = GetDocumentStore();
        var repository = await SeedRepositoryAsync(store);
        var forge = Repository(Head);
        forge.Files[$"{Head}/src/Tools/Tools.csproj"] = MultiTarget;
        var features = new HashSet<EOnboardingFeature> { EOnboardingFeature.ReleaseLines };

        // The Tools case (2026-11-12): the onboarding merged before the bot created lines.
        var first = Create(store, forge);
        var preview = await first.Service.PreviewAsync(repository, features);
        preview.Plan!.Cuts.Select(c => c.Branch).Should().Equal(["release/dotnet-11.x"]);
        preview.Plan.OfferedLines.Select(c => (c.Branch, c.TargetFramework)).Should().Equal([("release/dotnet-10.x", "net10.0")]);
        var opened = await first.Service.OnboardAsync(repository, features, []);
        opened.Outcome.Should().Be(VersioningOnboardingOutcome.Opened);
        MergeInto(forge, NextHead, first.Writer.Requests("onboarding").Single().Files!);
        forge.Files[$"main/{CoverageYml.FileName}"] = forge.Files[$"{NextHead}/{CoverageYml.FileName}"];

        // Set up features again: nothing to change, so the lines are created now, from the head the plan read.
        var again = Create(store, forge);
        again.Versioning.PullRequest = new ForgePullRequestText(opened.PullRequest!.Value, OnboardingPlanner.Title, null, OnboardingPlanner.Branch, Head, "main",
            false, true, true, NextHead, []);
        var linesAtBackfill = new List<int>();
        again.Writer.Backfill = request =>
        {
            linesAtBackfill.Add(again.Versioning.Branches.Count);
            return Task.FromResult(new GitWriterBackfillResult(GitWriterBackfillResult.Tagged, null, [], ["main"], [], null, 1));
        };
        var outcome = await again.Service.OnboardAsync(repository, features, ["release/dotnet-10.x"]);

        outcome.Outcome.Should().Be(VersioningOnboardingOutcome.Merged, outcome.Note);
        again.Writer.Requests("onboarding").Should().BeEmpty();
        again.Versioning.Branches.Should().Equal([("release/dotnet-11.x", NextHead), ("release/dotnet-10.x", NextHead)]);
        // #638 D8: re-running Set up features backfills the history, after the cuts.
        linesAtBackfill.Should().NotBeEmpty().And.EndWith(2);
        outcome.Note.Should().Contain("created release/dotnet-11.x").And.Contain("created release/dotnet-10.x");
    }

    [Fact]
    public async Task Narrowing_D15_an_older_line_cut_from_a_multi_target_commit_gets_a_pull_request_that_narrows_it()
    {
        // #638 D15: Tools #55, done by the bot: release/dotnet-10.x cut where the project targets net10.0;net11.0.
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store);
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new PlatformRelease { Platform = "dotnet", Channel = "10.0", Major = 10, LatestSdk = "10.0.203", LatestVersion = "10.0.7" },
                PlatformRelease.IdFor("dotnet", "10.0"));
            await seed.SaveChangesAsync();
        }
        var forge = Repository(Head);
        forge.Files[$"{Head}/src/Tools/Tools.csproj"] = MultiTarget;
        var h = Create(store, forge);
        var repository = (await h.Session.LoadAsync<Repository>(CodeCoverage.Entities.Repository.DocumentId(EForgeProvider.GitHub, RepoId)))!;
        var opened = await h.Service.OnboardAsync(repository, new HashSet<EOnboardingFeature> { EOnboardingFeature.ReleaseLines }, ["release/dotnet-10.x"]);
        h.Versioning.PullRequest = new ForgePullRequestText(opened.PullRequest!.Value, OnboardingPlanner.Title, null, OnboardingPlanner.Branch, Head, "main",
            false, true, true, NextHead, []);
        // Both lines as the cut left them, at the merge commit.
        const string Source = "namespace Acme.Tools;\n#if NET11_0_OR_GREATER\npublic static class Net11 { }\n#endif\n";
        foreach (var line in new[] { "release/dotnet-11.x", "release/dotnet-10.x" })
            forge.Trees[line] = new ForgeTree(NextHead, "tree-5711", [new ForgeTreeEntry("src/Tools/Tools.csproj", MultiTarget.Length), new ForgeTreeEntry("src/Tools/Net11.cs", Source.Length)], false);
        forge.Files[$"{NextHead}/src/Tools/Tools.csproj"] = MultiTarget;
        forge.Files[$"{NextHead}/src/Tools/Net11.cs"] = Source;

        var merged = await h.Service.MergedAsync(repository);

        var narrowing = h.Writer.Requests("onboarding").Where(r => r.Narrow).Should().ContainSingle().Which;
        (narrowing.Line, narrowing.BaseBranch, narrowing.ExpectedSha, narrowing.Message).Should().Be(("dotnet-10.x", "release/dotnet-10.x", NextHead, OnboardingPlanner.NarrowTitle));
        narrowing.Files!.Select(f => (f.Path, f.Content)).Should().Equal([("src/Tools/Tools.csproj", MultiTarget.Replace("net10.0;net11.0", "net10.0"))]);
        var pull = h.Pulls.Created.Where(p => p.Base == "release/dotnet-10.x").Should().ContainSingle().Which;
        (pull.Head, pull.Title).Should().Be(("mintplayer/narrow-dotnet-10.x", OnboardingPlanner.NarrowTitle));
        pull.Body.Should().Contain("- `src/Tools/Tools.csproj`: TargetFrameworks `net10.0;net11.0` → `net10.0`")
            .And.Contain("- `src/Tools/Net11.cs:2`: `#if NET11_0_OR_GREATER`");
        h.Pulls.Created.Should().NotContain(p => p.Base == "release/dotnet-11.x", "the current line targets nothing above its major");
        var number = h.Pulls.PullRequests.Values.Single(p => p.HeadRef == "mintplayer/narrow-dotnet-10.x").Number;
        merged.Note.Should().Contain($"opened #{number} into release/dotnet-10.x to narrow it to .NET 10");
    }

    [Fact]
    public async Task LineCuts_D4_a_refused_line_is_noted_and_one_that_exists_counts_as_done()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store);
        var forge = Repository(Head);
        forge.Files[$"{Head}/src/Tools/Tools.csproj"] = MultiTarget;
        var h = Create(store, forge);
        var repository = (await h.Session.LoadAsync<Repository>(CodeCoverage.Entities.Repository.DocumentId(EForgeProvider.GitHub, RepoId)))!;
        var opened = await h.Service.OnboardAsync(repository, new HashSet<EOnboardingFeature> { EOnboardingFeature.ReleaseLines }, ["release/dotnet-10.x"]);
        (await StateAsync(store, repository)).Cuts.Should().Equal(["release/dotnet-11.x", "release/dotnet-10.x"]);

        h.Versioning.PullRequest = new ForgePullRequestText(opened.PullRequest!.Value, OnboardingPlanner.Title, null, OnboardingPlanner.Branch, Head, "main",
            false, true, true, "abcdabcdabcdabcdabcdabcdabcdabcdabcdabcd", []);
        // The first versioning run cut 11.x first (S3); a ruleset refuses 10.x.
        h.Versioning.Branches.Add(("release/dotnet-11.x", NextHead));
        h.Versioning.BranchRefusals["release/dotnet-10.x"] = "POST repos/acme/tools/git/refs answered 422: Reference update failed";

        var merged = await h.Service.MergedAsync(repository);

        merged.Outcome.Should().Be(VersioningOnboardingOutcome.Merged, merged.Note);
        merged.Note.Should().Contain("release/dotnet-11.x exists already")
            .And.Contain("release/dotnet-10.x was not created: POST repos/acme/tools/git/refs answered 422: Reference update failed");
        (await StateAsync(store, repository)).MergeHandledAt.Should().NotBeNull("a refusal is noted, not retried");
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
