using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using CodeCoverage.PackageVersioning;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>
/// #153 M10 (D10): who may ask for a backport, and that a line naming no release branch refuses the whole request
/// before the writer is reached.
/// </summary>
public class BackportRecipientTests : CoverageRavenTest
{
    private const string RepositoryId = "Repositories/153";
    private static readonly string Merge = new('c', 40);

    private readonly FakeForgeVersioning forge = new()
    {
        PullRequest = new(7, "fix: guard", null, "fix", new string('h', 40), "main", false, true, false, Merge, []),
    };
    private readonly IForgeMergeQueue git = Substitute.For<IForgeMergeQueue>();
    private readonly IForgeIntegration integration = Substitute.For<IForgeIntegration>();
    private readonly IGitWriterSidecar writer = Substitute.For<IGitWriterSidecar>();

    public BackportRecipientTests()
    {
        git.Provider.Returns(EForgeProvider.GitHub);
        integration.Provider.Returns(EForgeProvider.GitHub);
        integration.GetFileContentAsync(Arg.Any<Repository>(), "main", CoverageYml.FileName, Arg.Any<CancellationToken>()).Returns("versioning:\n");
        git.GetBranchHeadAsync(Arg.Any<Repository>(), "release/8.x", Arg.Any<CancellationToken>()).Returns(new string('8', 40));
        git.GetActorRoleAsync(Arg.Any<Repository>(), "maintainer", Arg.Any<CancellationToken>()).Returns(EForgeRepositoryRole.Write);
        git.GetActorRoleAsync(Arg.Any<Repository>(), "visitor", Arg.Any<CancellationToken>()).Returns(EForgeRepositoryRole.Triage);
        git.GetPullRequestCommitsAsync(Arg.Any<Repository>(), 7, Arg.Any<CancellationToken>()).Returns([]);
        git.GetWriterAccessAsync(Arg.Any<Repository>(), null, false, Arg.Any<CancellationToken>())
            .Returns(new ForgePushAccess(new ForgeWriterAccess("https://example.invalid/acme/widgets.git", "https://example.invalid/acme/widgets.git", "x", "y"), null));
    }

    private async Task HandleAsync(IDocumentStore store, string actor, EBackportSource source, params string[] lines)
    {
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Repository { FullName = "acme/widgets", Provider = EForgeProvider.GitHub, DefaultBranch = "main" }, RepositoryId);
            await seed.SaveChangesAsync();
        }
        using var session = store.OpenAsyncSession();
        await new BackportRecipient(session, new SingleForgeResolver(integration), [forge], [git], writer, NullLogger<BackportRecipient>.Instance)
            .HandleAsync(new BackportMessage { RepositoryId = RepositoryId, Number = 7, Lines = lines, ActorLogin = actor, Source = source });
    }

    [Fact]
    public async Task A_line_naming_no_release_branch_refuses_the_whole_request()
    {
        using var store = GetDocumentStore();

        await HandleAsync(store, "maintainer", EBackportSource.Comment, "8.x", "9.x");

        forge.Comments.Single().Body.Should().Contain("`9.x` names no release branch");
        await writer.DidNotReceiveWithAnyArgs().VersioningBackportAsync(default!, default);
    }

    [Fact]
    public async Task A_label_or_comment_needs_write_access_but_a_merge_does_not()
    {
        using var store = GetDocumentStore();
        writer.VersioningBackportAsync(Arg.Any<GitWriterBackportRequest>(), Arg.Any<CancellationToken>()).Returns(new GitWriterBackportResult(
            GitWriterBackportResult.Backported, null, "squash", null,
            [new("release/8.x", "backport/7-8.x", new string('d', 40), GitWriterBackportTargetResult.Created, [])], null, 1));

        await HandleAsync(store, "visitor", EBackportSource.Label, "8.x");
        forge.Comments.Single().Body.Should().Contain("needs write access");
        await writer.DidNotReceiveWithAnyArgs().VersioningBackportAsync(default!, default);

        await HandleAsync(store, "visitor", EBackportSource.Merge, "8.x");
        await writer.Received(1).VersioningBackportAsync(
            Arg.Is<GitWriterBackportRequest>(r => r.Commit == Merge && r.SourceBranch == "main" && r.Targets.Single() == new GitWriterBackportTarget("release/8.x", "backport/7-8.x")),
            Arg.Any<CancellationToken>());
        forge.OpenedPullRequests.Should().Equal(("backport/7-8.x", "release/8.x", "[8.x] fix: guard",
            $"Backport of #7 to `release/8.x`.\n\nCherry-picked from {Merge}. CI on this pull request is the check against the line's platform."));
    }
}
