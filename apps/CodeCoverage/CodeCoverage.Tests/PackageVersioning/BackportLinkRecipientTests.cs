using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.PackageVersioning;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>#570 M8c, PRD §5.5 and D7: a merged backport labels its source; a hand-pushed backport branch gets its pull request.</summary>
public class BackportLinkRecipientTests : CoverageRavenTest
{
    private const string RepositoryId = "Repositories/5708";
    private const string Line = "release/dotnet-10.x";
    private static readonly string SourceMerge = new('5', 40);

    private readonly FakeForgeVersioning forge = new();
    private readonly IForgeMergeQueue git = Substitute.For<IForgeMergeQueue>();
    private readonly IForgeLabels labels = Substitute.For<IForgeLabels>();
    private readonly IForgeIntegration integration = Substitute.For<IForgeIntegration>();

    public BackportLinkRecipientTests()
    {
        git.Provider.Returns(EForgeProvider.GitHub);
        labels.Provider.Returns(EForgeProvider.GitHub);
        integration.Provider.Returns(EForgeProvider.GitHub);
        integration.GetFileContentAsync(Arg.Any<Repository>(), "main", CoverageYml.FileName, Arg.Any<CancellationToken>()).Returns("versioning:\n  lines: {}\n");
        git.GetPullRequestCommitsAsync(Arg.Any<Repository>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        git.GetActorRoleAsync(Arg.Any<Repository>(), "maintainer", Arg.Any<CancellationToken>()).Returns(EForgeRepositoryRole.Write);
        forge.OtherPullRequests[7] = Pull(7, "fix: guard", null, "fix", "main", SourceMerge);
    }

    private static ForgePullRequestText Pull(int number, string title, string? body, string head, string @base, string? merge = null, bool merged = true)
        => new(number, title, body, head, new string('h', 40), @base, !merged, merged, false, merge ?? new string('m', 40), []);

    private async Task<BackportLinkRecipient> RecipientAsync(IDocumentStore store)
    {
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Repository { FullName = "acme/widgets", Provider = EForgeProvider.GitHub, DefaultBranch = "main" }, RepositoryId);
            await seed.SaveChangesAsync();
        }
        return new BackportLinkRecipient(store.OpenAsyncSession(), new SingleForgeResolver(integration), [forge], [git], [labels], NullLogger<BackportLinkRecipient>.Instance);
    }

    private async Task MergedAsync(IDocumentStore store, ForgePullRequestText backport)
    {
        forge.OtherPullRequests[backport.Number] = backport;
        await (await RecipientAsync(store)).HandleAsync(new BackportMergedMessage { RepositoryId = RepositoryId, Number = backport.Number });
    }

    [Fact]
    public async Task The_apps_backport_labels_its_source_once_merged_without_a_comment()
    {
        using var store = GetDocumentStore();

        await MergedAsync(store, Pull(20, "[dotnet-10.x] fix: guard", "Backport of #7.\n\n" + BackportProvenance.Marker("acme/widgets", 7), "backport/7-dotnet-10.x", Line));

        await labels.Received(1).AddLabelAsync(Arg.Any<Repository>(), 7, "backported: dotnet-10.x", Arg.Any<CancellationToken>());
        forge.Comments.Should().BeEmpty("the app's own report already linked it");
    }

    [Theory]
    [InlineData("Backport of #7", "mine")]
    [InlineData(null, "backport/7-dotnet-10.x")]
    public async Task A_hand_made_backport_is_recognised_labelled_and_linked(string? body, string head)
    {
        using var store = GetDocumentStore();

        await MergedAsync(store, Pull(21, "guard on 10", body, head, Line));

        await labels.Received(1).AddLabelAsync(Arg.Any<Repository>(), 7, "backported: dotnet-10.x", Arg.Any<CancellationToken>());
        forge.Comments.Should().ContainSingle().Which.Should().Be((7, "`release/dotnet-10.x` has this change: #21, backported by hand."));
    }

    [Fact]
    public async Task A_cherry_pick_trailer_finds_the_source_through_its_merge_commit()
    {
        using var store = GetDocumentStore();
        forge.MergedPullRequests[SourceMerge] = [7];
        git.GetPullRequestCommitsAsync(Arg.Any<Repository>(), 22, Arg.Any<CancellationToken>())
            .Returns([new ForgePullRequestCommit(new string('c', 40), null, null, null, false, $"fix: guard\n\n(cherry picked from commit {SourceMerge})")]);

        await MergedAsync(store, Pull(22, "guard", "no words", "mine", Line));

        await labels.Received(1).AddLabelAsync(Arg.Any<Repository>(), 7, "backported: dotnet-10.x", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("main")]                   // into the default branch: not a backport
    [InlineData("feature/x")]              // into a branch that is no line
    public async Task A_merge_outside_the_lines_labels_nothing(string @base)
    {
        using var store = GetDocumentStore();

        await MergedAsync(store, Pull(23, "x", "Backport of #7", "mine", @base));

        await labels.DidNotReceiveWithAnyArgs().AddLabelAsync(default!, default, default!, default);
    }

    [Fact]
    public async Task A_source_that_is_no_merged_pull_request_into_the_default_branch_is_not_labelled()
    {
        using var store = GetDocumentStore();
        forge.OtherPullRequests[8] = Pull(8, "open", null, "x", "main", merged: false);

        await MergedAsync(store, Pull(24, "x", "Backport of #8", "mine", Line));
        await MergedAsync(store, Pull(25, "x", "Backport of #99", "mine", Line));

        await labels.DidNotReceiveWithAnyArgs().AddLabelAsync(default!, default, default!, default);
        forge.Comments.Should().BeEmpty();
    }

    [Fact]
    public async Task A_hand_pushed_backport_branch_gets_its_pull_request_into_the_line()
    {
        using var store = GetDocumentStore();

        await (await RecipientAsync(store)).HandleAsync(Pushed("backport/7-dotnet-10.x"));

        forge.OpenedPullRequests.Should().HaveCount(1);
        var opened = forge.OpenedPullRequests[0];
        opened.Head.Should().Be("backport/7-dotnet-10.x");
        opened.Base.Should().Be(Line);
        opened.Title.Should().Be("[dotnet-10.x] fix: guard");
        opened.Body.Should().Contain(BackportProvenance.Marker("acme/widgets", 7));
    }

    [Theory]
    [InlineData("backport/8-dotnet-10.x")]    // the source is not merged
    [InlineData("backport/99-dotnet-10.x")]   // no such pull request
    [InlineData("backport/7-10.x")]           // a bare major names no line when lines are on
    public async Task A_branch_naming_no_line_or_no_merged_source_opens_nothing(string branch)
    {
        using var store = GetDocumentStore();
        forge.OtherPullRequests[8] = Pull(8, "open", null, "x", "main", merged: false);

        await (await RecipientAsync(store)).HandleAsync(Pushed(branch));

        forge.OpenedPullRequests.Should().BeEmpty();
    }

    [Theory]   // T1: the pusher's role is read per request; the forge's own branch rule is not relied on alone
    [InlineData(EForgeRepositoryRole.None)]
    [InlineData(EForgeRepositoryRole.Read)]
    [InlineData(EForgeRepositoryRole.Triage)]
    public async Task A_backport_branch_pushed_without_write_access_opens_nothing(EForgeRepositoryRole role)
    {
        using var store = GetDocumentStore();
        git.GetActorRoleAsync(Arg.Any<Repository>(), "outsider", Arg.Any<CancellationToken>()).Returns(role);

        await (await RecipientAsync(store)).HandleAsync(Pushed("backport/7-dotnet-10.x", "outsider"));

        forge.OpenedPullRequests.Should().BeEmpty();
        forge.Comments.Should().BeEmpty();
    }

    private static BackportBranchPushedMessage Pushed(string branch, string pusher = "maintainer")
        => new() { RepositoryId = RepositoryId, Branch = branch, PusherLogin = pusher };

    [Fact]
    public async Task The_forwarder_skips_bots_later_pushes_and_other_branches()
    {
        var bus = Substitute.For<MintPlayer.Spark.Messaging.Abstractions.IMessageBus>();
        var forwarder = new BackportLinkEventsRecipient(bus);
        BranchCommitPushed Push(string branch, bool created, bool bot) => new(RepositoryId, branch, new string('a', 40), null, null, created, bot, "maintainer");

        await forwarder.HandleAsync(new ForgeWebhookMessage<BranchCommitPushed>(EForgeProvider.GitHub, Push("backport/7-dotnet-10.x", true, false)));
        await forwarder.HandleAsync(new ForgeWebhookMessage<BranchCommitPushed>(EForgeProvider.GitHub, Push("backport/7-dotnet-10.x", true, true)));
        await forwarder.HandleAsync(new ForgeWebhookMessage<BranchCommitPushed>(EForgeProvider.GitHub, Push("backport/7-dotnet-10.x", false, false)));
        await forwarder.HandleAsync(new ForgeWebhookMessage<BranchCommitPushed>(EForgeProvider.GitHub, Push("feature/x", true, false)));

        bus.ReceivedCalls().Should().HaveCount(1);
    }
}
