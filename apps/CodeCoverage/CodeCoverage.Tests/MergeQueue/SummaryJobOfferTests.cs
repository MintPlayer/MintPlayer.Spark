using CodeCoverage.Dependencies.Lifecycle;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using CodeCoverage.Migrations;
using CodeCoverage.Tests.CommitGraph;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Messaging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.MergeQueue;

/// <summary>
/// #714 Dec11: the one-off pass after the deploy. The migration queues one offer per repository whose merge queue is on; the
/// recipient reads the default branch's rules and offers the summary job when they require one check per job.
/// </summary>
public class SummaryJobOfferTests : CoverageRavenTest
{
    private static readonly string On = Repository.DocumentId(EForgeProvider.GitHub, 5001);
    private static readonly string Off = Repository.DocumentId(EForgeProvider.GitHub, 5002);
    private static readonly string Archived = Repository.DocumentId(EForgeProvider.GitHub, 5003);
    private static readonly string NeverRead = Repository.DocumentId(EForgeProvider.GitHub, 5004);
    private readonly MutableTime time = new(new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.Zero));
    private readonly IForgeMergeQueue queue = Substitute.For<IForgeMergeQueue>();

    public SummaryJobOfferTests()
    {
        queue.Provider.Returns(EForgeProvider.GitHub);
    }

    private static ForgeBranchRules RequiringNames(params string[] names)
        => new(true, names, 0, false, false, false, false,
            [new ForgeRuleset(2, "Reviews and checks", ["required_status_checks"], EForgeRulesetBypass.Never, [.. names.Select(name => new ForgeRequiredCheck(name))])],
            EForgeClassicProtection.None);

    private static Repository Repo(string name, bool archived = false) => new()
    {
        Provider = EForgeProvider.GitHub,
        OwnerLogin = "acme",
        Name = name,
        FullName = $"acme/{name}",
        DefaultBranch = "master",
        Archived = archived,
    };

    private async Task<IDocumentStore> StoreAsync()
    {
        var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(Repo("on"), On);
        await session.StoreAsync(Repo("off"), Off);
        await session.StoreAsync(Repo("archived", archived: true), Archived);
        await session.StoreAsync(Repo("never-read"), NeverRead);
        await session.StoreAsync(new DependencyUpdateRun { FileMergeQueue = DependencyFileMergeQueue.Enabled }, DependencyUpdateRun.ForRepository(On));
        await session.StoreAsync(new DependencyUpdateRun { FileMergeQueue = DependencyFileMergeQueue.Disabled }, DependencyUpdateRun.ForRepository(Off));
        await session.StoreAsync(new DependencyUpdateRun { FileMergeQueue = DependencyFileMergeQueue.Enabled }, DependencyUpdateRun.ForRepository(Archived));
        await session.SaveChangesAsync();
        return store;
    }

    private async Task HandleAsync(IDocumentStore store, string repositoryId)
    {
        using var session = store.OpenAsyncSession();
        await new OfferSummaryJobRecipient(session, [queue], new RulesetNotices(session, time), NullLogger<OfferSummaryJobRecipient>.Instance)
            .HandleAsync(new OfferSummaryJobMessage { RepositoryId = repositoryId });
    }

    private static async Task<RulesetNotice?> OfferAsync(IDocumentStore store, string repositoryId)
    {
        using var session = store.OpenAsyncSession();
        return await session.LoadAsync<RulesetNotice>(RulesetNotice.DocumentId(repositoryId, ERulesetNoticeKind.SummaryJob));
    }

    [Fact]
    public async Task The_migration_queues_one_offer_per_repository_whose_merge_queue_is_on()
    {
        using var store = await StoreAsync();
        var bus = Substitute.For<IMessageBus>();

        await new M_202611271200_OfferSummaryJob(store, bus, NullLogger<M_202611271200_OfferSummaryJob>.Instance).UpAsync(default);

        await bus.Received(1).BroadcastOnceAsync(Arg.Any<OfferSummaryJobMessage>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await bus.Received(1).BroadcastOnceAsync(Arg.Is<OfferSummaryJobMessage>(m => m.RepositoryId == On), OfferSummaryJobMessage.OnceKey(On),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rules_with_one_check_per_job_are_offered_the_summary_job_on_the_default_branch()
    {
        using var store = await StoreAsync();
        queue.GetBranchRulesAsync(Arg.Any<Repository>(), "master", Arg.Any<CancellationToken>()).Returns(RequiringNames("build", "test", "coverage/patch"));

        await HandleAsync(store, On);

        var offer = (await OfferAsync(store, On))!;
        offer.IsOpen().Should().BeTrue();
        offer.BaseBranch.Should().Be("master");
        offer.Checks.Should().Equal(["build", "test"]);
    }

    [Fact]
    public async Task Rules_that_require_one_summary_job_are_not_offered_anything()
    {
        using var store = await StoreAsync();
        queue.GetBranchRulesAsync(Arg.Any<Repository>(), "master", Arg.Any<CancellationToken>()).Returns(RequiringNames("pull-request", "coverage/patch"));

        await HandleAsync(store, On);

        (await OfferAsync(store, On)).Should().BeNull();
    }

    [Fact]
    public async Task A_forge_that_refuses_or_an_archived_repository_is_skipped()
    {
        using var store = await StoreAsync();
        queue.GetBranchRulesAsync(Arg.Any<Repository>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ForgeRequestException("rate limited", 403));

        await HandleAsync(store, On);
        await HandleAsync(store, Archived);

        (await OfferAsync(store, On)).Should().BeNull();
        await queue.Received(1).GetBranchRulesAsync(Arg.Any<Repository>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
