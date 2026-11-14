using System.Net;
using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.LookupReferences;
using CodeCoverage.MergeQueue;
using CodeCoverage.PackageVersioning;
using CodeCoverage.Tests.MergeQueue;
using CodeCoverage.Versioning.Versions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>
/// Segment P end to end on the server side: what a <c>/release</c> asks the writer for and records (P3, P5), and what a
/// person's version tag leaves behind (Q6b = C). The writer is a fake; its own behaviour is in GitWriter's PromoteTests.
/// </summary>
public class ReleaseFlowTests : CoverageRavenTest
{
    private const string RepositoryId = "Repositories/626";
    private const string Promoted = "1111000000000000000000000000000000000000";
    private const string Tagged = "7777000000000000000000000000000000000000";
    private static readonly string Green = new('c', 40);

    private const string PromoteYml = "versioning:\n  lines:\n    current: promote\n    trigger: manual\n    workflow: release.yml\n";

    private readonly FakeForgeVersioning forge = new();
    private readonly IForgeMergeQueue git = Substitute.For<IForgeMergeQueue>();
    private readonly IForgeIntegration integration = Substitute.For<IForgeIntegration>();
    private readonly IGitWriterSidecar writer = Substitute.For<IGitWriterSidecar>();
    private readonly IHttpClientFactory http = Substitute.For<IHttpClientFactory>();
    private readonly List<string> asked = [];
    private Func<HttpRequestMessage, HttpResponseMessage> registry = _ => new(HttpStatusCode.NotFound);
    private GitWriterPromoteRequest? promoteRequest;

    public ReleaseFlowTests()
    {
        git.Provider.Returns(EForgeProvider.GitHub);
        integration.Provider.Returns(EForgeProvider.GitHub);
        Yml(PromoteYml);
        git.GetActorRoleAsync(Arg.Any<Repository>(), "maintainer", Arg.Any<CancellationToken>()).Returns(EForgeRepositoryRole.Maintain);
        git.GetActorRoleAsync(Arg.Any<Repository>(), "writer", Arg.Any<CancellationToken>()).Returns(EForgeRepositoryRole.Write);
        git.ListBranchesAsync(Arg.Any<Repository>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(["release/dotnet-11.x", "release/angular-22.x"]);
        git.ListWorkflowRunsAsync(Arg.Any<Repository>(), "release.yml", "main", Arg.Any<CancellationToken>())
            .Returns([new ForgeWorkflowRun(9, "completed", "success", Green, "main", "https://example.invalid/runs/9")]);
        git.GetWriterAccessAsync(Arg.Any<Repository>(), null, true, Arg.Any<CancellationToken>()) // tags and line moves carry workflow history
            .Returns(new ForgePushAccess(new ForgeWriterAccess("https://example.invalid/acme/widgets.git", "https://example.invalid/acme/widgets.git", "x", "y"), null));
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new Registry(this)));
        writer.VersioningPromoteAsync(Arg.Do<GitWriterPromoteRequest>(r => promoteRequest = r), Arg.Any<CancellationToken>())
            .Returns(new GitWriterPromoteResult(GitWriterPromoteResult.Promoted, null,
            [
                new("release/dotnet-11.x", GitWriterPromoteLineResult.Promoted, Promoted, [], [], Tags: ["Acme.Core/v11.4.0"]),
                new("release/angular-22.x", GitWriterPromoteLineResult.Unchanged, new string('2', 40), [], []),
            ], null, 10, [new("Acme.Stale", "11.0.0", "Acme.Stale/v11.0.0 is not above the package's tag Acme.Stale/v11.1.0.")]));
    }

    private void Yml(string yml)
        => integration.GetFileContentAsync(Arg.Any<Repository>(), "main", CoverageYml.FileName, Arg.Any<CancellationToken>()).Returns(yml);

    private sealed class Registry(ReleaseFlowTests test) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            test.asked.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(test.registry(request));
        }
    }

    private static async Task SeedAsync(IDocumentStore store, params ReleaseVersion[] versions)
    {
        using var seed = store.OpenAsyncSession();
        await seed.StoreAsync(new Repository { FullName = "acme/widgets", Provider = EForgeProvider.GitHub, DefaultBranch = "main", GitHubId = 626 }, RepositoryId);
        foreach (var version in versions) await seed.StoreAsync(version, version.Id);
        await seed.SaveChangesAsync();
    }

    private static ReleaseVersion Pending(string tag)
    {
        VersionTag.TryParse(tag, out var parsed).Should().BeTrue();
        return new ReleaseVersion
        {
            Id = ReleaseVersion.DocumentId(RepositoryId, tag), Repository = RepositoryId, PackageId = parsed.PackageId, Version = parsed.Version.ToString(),
            Tag = tag, Actor = "octo", Source = ReleaseVersion.FromTag, RecordedUtc = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
        };
    }

    private async Task ReleaseAsync(IDocumentStore store, string actor, params ReleaseVersionRequest[] versions)
    {
        using var session = store.OpenAsyncSession();
        await new ReleaseRecipient(session, new SingleForgeResolver(integration), [forge], [git], writer, http, TimeProvider.System, Tests.Commands.CommandTestKit.Lifecycle(store, new Tests.Commands.FakeCommentReactions()), NullLogger<ReleaseRecipient>.Instance)
            .HandleAsync(new ReleaseMessage { RepositoryId = RepositoryId, Number = 5, Request = new ReleaseCommentRequest([], false, null, versions), ActorLogin = actor });
    }

    [Fact]
    public async Task A_release_sends_the_asked_and_the_recorded_versions_records_the_placed_tag_and_forgets_a_stale_record()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, Pending("Acme.Stale/v11.0.0"));

        await ReleaseAsync(store, "maintainer", new ReleaseVersionRequest("Acme.Core", "11.4.0"));

        promoteRequest!.Commit.Should().Be(Green);
        promoteRequest.Lines.Select(l => (l.Branch, l.Major)).Should().BeEquivalentTo([("release/angular-22.x", 22), ("release/dotnet-11.x", 11)]);
        promoteRequest.Versions.Should().BeEquivalentTo([new GitWriterPromoteVersion("Acme.Core", "11.4.0", false), new GitWriterPromoteVersion("Acme.Stale", "11.0.0", true)]);
        asked.Should().Contain("https://api.nuget.org/v3-flatcontainer/acme.core/index.json");

        using var check = store.OpenAsyncSession();
        var placed = await check.LoadAsync<ReleaseVersion>(ReleaseVersion.DocumentId(RepositoryId, "Acme.Core/v11.4.0"));
        placed!.PlacedCommit.Should().Be(Promoted);
        placed.Actor.Should().Be("maintainer");
        placed.Source.Should().Be(ReleaseVersion.FromComment);
        (await check.LoadAsync<ReleaseVersion>(ReleaseVersion.DocumentId(RepositoryId, "Acme.Stale/v11.0.0"))).Should().BeNull("a record that no longer fits is reported once and forgotten");
        forge.Comments.Single().Body.Should().Contain("with `Acme.Core/v11.4.0`").And.Contain("`Acme.Stale@11.0.0` was not placed");
        await git.Received(1).RevokeWriterAccessAsync(Arg.Any<Repository>(), Arg.Any<ForgeWriterAccess>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_commenter_below_the_release_role_is_told_so_and_nothing_runs()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);

        await ReleaseAsync(store, "writer");

        forge.Comments.Single().Body.Should().Contain("needs the maintain role").And.Contain("@writer");
        await writer.DidNotReceiveWithAnyArgs().VersioningPromoteAsync(default!, default);
    }

    [Fact]
    public async Task A_lowered_release_role_on_the_account_lets_a_writer_release()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Account { ReleaseRole = EReleaseRole.Write }, "Accounts/626");
            (await seed.LoadAsync<Repository>(RepositoryId))!.Account = "Accounts/626";
            await seed.SaveChangesAsync();
        }

        await ReleaseAsync(store, "writer");

        await writer.ReceivedWithAnyArgs(1).VersioningPromoteAsync(default!, default);
    }

    [Theory]
    [InlineData("""{ "versions": ["11.4.0"] }""", HttpStatusCode.OK, "is published already")]
    [InlineData("", HttpStatusCode.BadGateway, "could not be asked")]
    public async Task An_asked_version_a_registry_has_or_cannot_answer_for_refuses_the_release(string body, HttpStatusCode status, string reason)
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        registry = _ => new(status) { Content = new StringContent(body) };

        await ReleaseAsync(store, "maintainer", new ReleaseVersionRequest("Acme.Core", "11.4.0"));

        forge.Comments.Single().Body.Should().Contain(reason);
        await writer.DidNotReceiveWithAnyArgs().VersioningPromoteAsync(default!, default);
    }

    [Theory]
    [InlineData("versioning:\n  lines: {}\n", "this repository's lines take backports")]
    [InlineData("versioning:\n", "has no `versioning.lines` section")]
    public async Task A_repository_that_does_not_promote_is_told_why(string yml, string reason)
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        Yml(yml);

        await ReleaseAsync(store, "maintainer");

        forge.Comments.Single().Body.Should().Contain(reason);
        await writer.DidNotReceiveWithAnyArgs().VersioningPromoteAsync(default!, default);
    }

    [Fact]
    public async Task Without_a_green_run_nothing_is_released()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        git.ListWorkflowRunsAsync(Arg.Any<Repository>(), "release.yml", "main", Arg.Any<CancellationToken>())
            .Returns([new ForgeWorkflowRun(9, "completed", "failure", Green, "main", "https://example.invalid/runs/9")]);

        await ReleaseAsync(store, "maintainer");

        forge.Comments.Single().Body.Should().Contain("none of the latest `release.yml` runs on main succeeded");
        await writer.DidNotReceiveWithAnyArgs().VersioningPromoteAsync(default!, default);
    }

    [Fact]
    public async Task A_writer_that_is_down_says_so_and_ends_the_credential()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        writer.VersioningPromoteAsync(Arg.Any<GitWriterPromoteRequest>(), Arg.Any<CancellationToken>())
            .Returns<GitWriterPromoteResult>(_ => throw new HttpRequestException("Connection refused"));

        await ReleaseAsync(store, "maintainer");

        forge.Comments.Single().Body.Should().Contain("the git writer was unavailable");
        await git.Received(1).RevokeWriterAccessAsync(Arg.Any<Repository>(), Arg.Any<ForgeWriterAccess>(), Arg.Any<CancellationToken>());
    }

    // ---- A person's version tag (HandTagRecipient)

    private void WriterJudges(string tagOutcome, string? detail = null)
        => writer.VersioningHandTagAsync(Arg.Any<GitWriterHandTagRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GitWriterHandTagResult(GitWriterHandTagResult.Planned, null, tagOutcome, "release/dotnet-11.x", Tagged, detail, 5));

    private async Task TagAsync(IDocumentStore store, string tag = "Acme.Core/v11.5.0")
    {
        using var session = store.OpenAsyncSession();
        await new HandTagRecipient(session, new SingleForgeResolver(integration), [forge], [git], writer, http, TimeProvider.System, NullLogger<HandTagRecipient>.Instance)
            .HandleAsync(new HandTagMessage { RepositoryId = RepositoryId, Tag = tag, TagObject = Tagged, PusherLogin = "octo" });
    }

    [Fact]
    public async Task A_valid_hand_tag_is_recorded_for_the_next_promotion_and_the_pusher_is_told()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        WriterJudges(GitWriterHandTagResult.Recorded);

        await TagAsync(store);

        using var check = store.OpenAsyncSession();
        var record = await check.LoadAsync<ReleaseVersion>(ReleaseVersion.DocumentId(RepositoryId, "Acme.Core/v11.5.0"));
        record!.Source.Should().Be(ReleaseVersion.FromTag);
        record.Actor.Should().Be("octo");
        record.PlacedCommit.Should().BeNull();
        await writer.Received(1).VersioningHandTagAsync(Arg.Is<GitWriterHandTagRequest>(r => r.Promotes), Arg.Any<CancellationToken>());
        forge.CommitComments.Single().Should().Be((Tagged, HandTagRecipient.Comment("octo", Parse("Acme.Core/v11.5.0"), "release/dotnet-11.x", null)));
        await git.Received(1).RevokeWriterAccessAsync(Arg.Any<Repository>(), Arg.Any<ForgeWriterAccess>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_hand_tag_of_a_published_version_is_removed_and_not_recorded()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        WriterJudges(GitWriterHandTagResult.Recorded);
        registry = _ => new(HttpStatusCode.OK) { Content = new StringContent("""{ "versions": ["11.5.0"] }""") };

        await TagAsync(store);

        using var check = store.OpenAsyncSession();
        (await check.LoadAsync<ReleaseVersion>(ReleaseVersion.DocumentId(RepositoryId, "Acme.Core/v11.5.0"))).Should().BeNull();
        forge.CommitComments.Single().Body.Should().Contain("was removed: Acme.Core/v11.5.0 is published already");
    }

    [Fact]
    public async Task A_refused_hand_tag_gets_the_writers_reason_and_a_foreign_tag_gets_nothing()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        WriterJudges(GitWriterHandTagResult.Refused, "Acme.Core/v12.0.0 does not keep the major of Acme.Core/v11.4.0, the tag it follows");

        await TagAsync(store, "Acme.Core/v12.0.0");
        WriterJudges(GitWriterHandTagResult.NotAPackage);
        await TagAsync(store, "schemas/v3.0.0");

        forge.CommitComments.Should().ContainSingle().Which.Body.Should().Contain("was removed: Acme.Core/v12.0.0 does not keep the major")
            .And.Contain("must be above the tag it follows");
        using var check = store.OpenAsyncSession();
        (await check.Advanced.LoadStartingWithAsync<ReleaseVersion>(ReleaseVersion.Prefix(RepositoryId))).Should().BeEmpty();
    }

    [Fact]
    public async Task A_backport_repositorys_valid_tag_stays_and_is_recorded_where_it_is()
    {
        // Segment T3: every repository with release lines has its tags checked, not only a promoting one.
        using var store = GetDocumentStore();
        await SeedAsync(store);
        Yml("versioning:\n  lines: {}\n");
        GitWriterHandTagRequest? request = null;
        writer.VersioningHandTagAsync(Arg.Do<GitWriterHandTagRequest>(r => request = r), Arg.Any<CancellationToken>())
            .Returns(new GitWriterHandTagResult(GitWriterHandTagResult.Planned, null, GitWriterHandTagResult.Accepted, "release/dotnet-11.x", Tagged, null, 5));

        await TagAsync(store);

        request!.Promotes.Should().BeFalse();
        request.Lines.Select(l => (l.Branch, l.Major)).Should().Equal(("release/angular-22.x", 22), ("release/dotnet-11.x", 11));
        using var check = store.OpenAsyncSession();
        var record = await check.LoadAsync<ReleaseVersion>(ReleaseVersion.DocumentId(RepositoryId, "Acme.Core/v11.5.0"));
        record!.PlacedCommit.Should().Be(Tagged);
        record.Actor.Should().Be("octo");
        forge.CommitComments.Should().BeEmpty("a tag that stays needs no word");
        asked.Should().BeEmpty("only a tag recorded for a promotion asks the registries");
    }

    [Fact]
    public async Task A_repository_without_release_lines_never_asks_the_writer_about_its_tags()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        Yml("versioning:\n");

        await TagAsync(store);

        await writer.DidNotReceiveWithAnyArgs().VersioningHandTagAsync(default!, default);
        forge.CommitComments.Should().BeEmpty();
    }

    private static VersionTag Parse(string tag)
    {
        VersionTag.TryParse(tag, out var parsed).Should().BeTrue();
        return parsed;
    }
}
