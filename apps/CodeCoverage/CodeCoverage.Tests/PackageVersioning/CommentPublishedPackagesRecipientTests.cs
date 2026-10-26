using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.PackageVersioning;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Assertions;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>
/// The comment that tells a merged pull request what its publish run released: only after a green run, only the tags in
/// place, once per pull request however often the webhook is delivered.
/// </summary>
public class CommentPublishedPackagesRecipientTests : CoverageRavenTest
{
    private const string RepositoryId = "Repositories/153";
    private const string RunId = "VersioningRuns/github/153/9.1";
    private const string Commit = "4444444444444444444444444444444444444444";
    private const string Url = "https://github.com/acme/widgets/actions/runs/9";

    private static VersioningRunRelease Release(string id, string version, string? outcome = "created") => new()
    {
        Id = id, Version = version, Tag = $"{id}/v{version}", TagOutcome = outcome,
    };

    private static async Task SeedAsync(IDocumentStore store, params VersioningRunRelease[] releases)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(new Repository { FullName = "acme/widgets", Provider = EForgeProvider.GitHub, GitHubId = 153 }, RepositoryId);
        await session.StoreAsync(new VersioningRun { Repository = RepositoryId, Branch = "master", Commit = Commit, RunId = 9, RunAttempt = 1, Releases = [.. releases] }, RunId);
        await session.SaveChangesAsync();
    }

    private static async Task HandleAsync(IDocumentStore store, FakeForgeVersioning forge, string conclusion = "success")
    {
        using var session = store.OpenAsyncSession();
        await new CommentPublishedPackagesRecipient(session, [forge], NullLogger<CommentPublishedPackagesRecipient>.Instance)
            .HandleAsync(new CommentPublishedPackagesMessage { RunId = RunId, Conclusion = conclusion, HtmlUrl = Url });
    }

    private static async Task<VersioningRun> RunAsync(IDocumentStore store)
    {
        using var session = store.OpenAsyncSession();
        return (await session.LoadAsync<VersioningRun>(RunId))!;
    }

    [Fact]
    public async Task A_green_run_comments_its_tagged_packages_once_on_the_merged_pull_request()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, Release("MintPlayer.Tools", "11.0.0-rc.1"), Release("@acme/ui", "22.1.0", "already-present"),
            Release("Acme.Broken", "1.0.0", "conflict"));
        var forge = new FakeForgeVersioning();
        forge.MergedPullRequests[Commit] = [203];

        await HandleAsync(store, forge);
        await HandleAsync(store, forge);

        forge.Comments.Should().ContainSingle();
        forge.Comments[0].Number.Should().Be(203);
        forge.Comments[0].Body.Should().Be($"Published 2 packages in [the publish run]({Url}):\n\n| Package | Version |\n|---|---|\n"
            + "| `@acme/ui` | `22.1.0` |\n| `MintPlayer.Tools` | `11.0.0-rc.1` |\n");
        var run = await RunAsync(store);
        run.PublishedComment.Should().Be("commented");
        run.CommentedPullRequests.Should().Equal(203);
    }

    [Theory]
    [InlineData("failure", "failed-run")]
    [InlineData("cancelled", "failed-run")]
    public async Task A_run_that_did_not_go_green_comments_nothing(string conclusion, string outcome)
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, Release("MintPlayer.Tools", "11.0.0"));
        var forge = new FakeForgeVersioning();
        forge.MergedPullRequests[Commit] = [203];

        await HandleAsync(store, forge, conclusion);

        forge.Comments.Should().BeEmpty();
        (await RunAsync(store)).PublishedComment.Should().Be(outcome);
    }

    [Fact]
    public async Task Nothing_released_or_no_merged_pull_request_comments_nothing()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, Release("MintPlayer.Tools", "11.0.0"));
        var forge = new FakeForgeVersioning();

        await HandleAsync(store, forge);

        forge.Comments.Should().BeEmpty();
        (await RunAsync(store)).PublishedComment.Should().Be("none");
    }

    [Fact]
    public void One_package_reads_in_the_singular()
        => CommentPublishedPackagesRecipient.Body([Release("MintPlayer.Tools", "11.0.0")], null)
            .Should().StartWith("Published 1 package:\n\n");
}
