using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.LookupReferences;
using CodeCoverage.MergeQueue;
using CodeCoverage.PackageVersioning;
using CodeCoverage.Tests._Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Messaging.Abstractions;
using NSubstitute;
using Xunit;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>
/// #693 Q12: a push to a versioned line of a repository with the changelog on composes what the line's next release would
/// list, through the writer's plan job with notes, and keeps it per line through <c>ISparkSession</c>.
/// </summary>
public class UpcomingChangelogRecipientTests : CoverageRavenTest
{
    private const long Repo = 693;
    private static readonly string RepositoryId = Repository.DocumentId(EForgeProvider.GitHub, Repo);
    private static readonly string Sha = new('a', 40);
    private const string Lines = "versioning:\n  lines: {}\n";

    private readonly IForgeMergeQueue git = Substitute.For<IForgeMergeQueue>();
    private readonly IForgeIntegration integration = Substitute.For<IForgeIntegration>();
    private readonly IGitWriterSidecar writer = Substitute.For<IGitWriterSidecar>();
    private readonly IMessageBus bus = Substitute.For<IMessageBus>();

    public static TheoryData<string> Stores => SparkStores.Both;

    public UpcomingChangelogRecipientTests()
    {
        git.Provider.Returns(EForgeProvider.GitHub);
        integration.Provider.Returns(EForgeProvider.GitHub);
        writer.IsConfigured.Returns(true);
        git.GetWriterAccessAsync(Arg.Any<Repository>(), null, false, Arg.Any<CancellationToken>())
            .Returns(new ForgePushAccess(new ForgeWriterAccess("https://example.invalid/acme/widgets.git", "https://example.invalid/acme/widgets.git", "x", "y"), null));
        integration.GetFileContentAsync(Arg.Any<Repository>(), "main", CoverageYml.FileName, Arg.Any<CancellationToken>()).Returns(Lines);
    }

    private ServiceProvider Provider(string store) => SparkStores.Provider(store, () => GetDocumentStore());

    private static Task SeedAsync(ServiceProvider provider, EChangelogPolicy policy = EChangelogPolicy.Enabled)
        => provider.InScopeAsync(async session =>
        {
            await session.StoreAsync(new Repository
            {
                GitHubId = Repo, FullName = "acme/widgets", Provider = EForgeProvider.GitHub, DefaultBranch = "main", Changelog = policy,
            }, RepositoryId);
            await session.SaveChangesAsync();
        });

    private void Plan(params GitWriterUpcomingNotes[] notes)
        => writer.VersioningPlanAsync(Arg.Any<GitWriterVersioningPlanRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GitWriterVersioningPlanResult(GitWriterVersioningPlanResult.Planned, null, [.. notes.Select(n => n.Id)], [], [], null, 1, notes));

    private Task HandleAsync(ServiceProvider provider, string branch) => provider.InScopeAsync(session =>
        new UpcomingChangelogRecipient(session, new SingleForgeResolver(integration), [git], writer, TimeProvider.System, NullLogger<UpcomingChangelogRecipient>.Instance)
            .HandleAsync(new UpcomingChangelogMessage { RepositoryId = RepositoryId, Branch = branch, Sha = Sha }));

    private Task PushAsync(ServiceProvider provider, string branch) => provider.InScopeAsync(session =>
        new UpcomingChangelogEventsRecipient(session, bus).HandleAsync(
            new ForgeWebhookMessage<BranchCommitPushed>(EForgeProvider.GitHub, new BranchCommitPushed(RepositoryId, branch, Sha, "msg", DateTimeOffset.UnixEpoch))));

    private static async Task<List<UpcomingChangelog>> ListAsync(ServiceProvider provider)
    {
        List<UpcomingChangelog> lines = [];
        await provider.InScopeAsync(async session =>
            lines = await UpcomingChangelogRecipient.ForRepositoryAsync(session, (await session.LoadAsync<Repository>(RepositoryId))!));
        return lines;
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task A_push_to_the_default_branch_and_to_a_line_keeps_each_line_s_upcoming_notes(string store)
    {
        using var provider = Provider(store);
        await SeedAsync(provider);

        Plan(new GitWriterUpcomingNotes("Acme.Ui", "### Fixes\n- b (#2)"), new GitWriterUpcomingNotes("Acme.Core", "### Features\n- a (#1)"));
        await HandleAsync(provider, "main");
        Plan(new GitWriterUpcomingNotes("Acme.Core", "### Fixes\n- c (#3)"));
        await HandleAsync(provider, "release/dotnet-11.x");
        Plan();
        await HandleAsync(provider, "release/dotnet-11.x");
        await HandleAsync(provider, "feature/x");

        await writer.Received(3).VersioningPlanAsync(Arg.Is<GitWriterVersioningPlanRequest>(r => r.Notes), Arg.Any<CancellationToken>());
        var lines = await ListAsync(provider);
        lines.Select(l => (l.Branch, l.Packages)).Should().Equal(("main", "Acme.Core, Acme.Ui"), ("release/dotnet-11.x", ""));
        lines[0].Notes.Should().Be("## Acme.Core\n\n### Features\n- a (#1)\n\n## Acme.Ui\n\n### Fixes\n- b (#2)");
        (lines[0].Commit, lines[0].LinePattern).Should().Be((Sha, (string?)"release/{ecosystem}-{major}.x"));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task A_push_is_forwarded_only_for_the_default_branch_or_a_cached_line_with_the_changelog_on(string store)
    {
        using var provider = Provider(store);
        await SeedAsync(provider);

        await PushAsync(provider, "release/dotnet-11.x");   // no default-branch composition yet, so no cached pattern
        await PushAsync(provider, "main");
        Plan();
        await HandleAsync(provider, "main");
        await PushAsync(provider, "release/dotnet-11.x");
        await PushAsync(provider, "feature/x");

        await bus.Received(2).BroadcastOnceAsync(Arg.Any<UpcomingChangelogMessage>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await bus.Received(1).BroadcastOnceAsync(Arg.Is<UpcomingChangelogMessage>(m => m.Branch == "release/dotnet-11.x"), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task With_the_changelog_off_nothing_is_forwarded_or_composed()
    {
        using var provider = Provider(SparkStores.Memory);
        await SeedAsync(provider, EChangelogPolicy.Disabled);

        await PushAsync(provider, "main");
        await HandleAsync(provider, "main");

        await bus.DidNotReceiveWithAnyArgs().BroadcastOnceAsync(default(UpcomingChangelogMessage)!, default!, default);
        await writer.DidNotReceiveWithAnyArgs().VersioningPlanAsync(default!, default);
    }

    [Fact]
    public void A_branch_is_escaped_so_a_line_never_shares_another_s_prefix()
        => UpcomingChangelog.DocumentId(EForgeProvider.GitHub, Repo, "release/dotnet-11.x").Should().Be("UpcomingChangelogs/github/693/release%2Fdotnet-11.x");
}
