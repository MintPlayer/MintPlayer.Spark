using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using CodeCoverage.PackageVersioning;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Messaging.Abstractions;
using NSubstitute;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>
/// #153 M11 (D3 trigger B): a push to a versioned line dispatches <c>versioning.dispatch</c> only when the writer's plan
/// for the pushed commit releases something, and a push to any other branch never reaches the forge.
/// </summary>
public class DispatchRecipientTests : CoverageRavenTest
{
    private const string RepositoryId = "Repositories/153";
    private const string Dispatching = "versioning:\n  dispatch: publish.yml\n";
    private static readonly string Sha = new('a', 40);

    private readonly FakeForgeVersioning forge = new();
    private readonly IForgeMergeQueue git = Substitute.For<IForgeMergeQueue>();
    private readonly IForgeIntegration integration = Substitute.For<IForgeIntegration>();
    private readonly IGitWriterSidecar writer = Substitute.For<IGitWriterSidecar>();
    private readonly IMessageBus bus = Substitute.For<IMessageBus>();

    public DispatchRecipientTests()
    {
        git.Provider.Returns(EForgeProvider.GitHub);
        integration.Provider.Returns(EForgeProvider.GitHub);
        git.GetWriterAccessAsync(Arg.Any<Repository>(), null, false, Arg.Any<CancellationToken>())
            .Returns(new ForgePushAccess(new ForgeWriterAccess("https://example.invalid/acme/widgets.git", "https://example.invalid/acme/widgets.git", "x", "y"), null));
    }

    private void Config(string yml)
        => integration.GetFileContentAsync(Arg.Any<Repository>(), "main", CoverageYml.FileName, Arg.Any<CancellationToken>()).Returns(yml);

    private void Plan(params string[] releases)
        => writer.VersioningPlanAsync(Arg.Any<GitWriterVersioningPlanRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GitWriterVersioningPlanResult(GitWriterVersioningPlanResult.Planned, null, releases, [], [], null, 1));

    private static async Task SeedAsync(IDocumentStore store, VersioningDispatchState? state = null)
    {
        using var seed = store.OpenAsyncSession();
        await seed.StoreAsync(new Repository { FullName = "acme/widgets", Provider = EForgeProvider.GitHub, DefaultBranch = "main" }, RepositoryId);
        if (state is not null) await seed.StoreAsync(state, VersioningDispatchState.ForRepository(RepositoryId));
        await seed.SaveChangesAsync();
    }

    private async Task HandleAsync(IDocumentStore store, string branch)
    {
        using var session = store.OpenAsyncSession();
        await new VersioningDispatchRecipient(session, new SingleForgeResolver(integration), [forge], [git], writer, TimeProvider.System,
                NullLogger<VersioningDispatchRecipient>.Instance)
            .HandleAsync(new VersioningDispatchMessage { RepositoryId = RepositoryId, Branch = branch, Sha = Sha });
    }

    private async Task PushAsync(IDocumentStore store, string branch)
    {
        using var session = store.OpenAsyncSession();
        await new VersioningDispatchEventsRecipient(session, bus)
            .HandleAsync(new ForgeWebhookMessage<BranchCommitPushed>(EForgeProvider.GitHub, new BranchCommitPushed(RepositoryId, branch, Sha, "msg", DateTimeOffset.UnixEpoch)));
    }

    [Fact]
    public async Task A_merge_that_changes_no_package_dispatches_nothing()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        Config(Dispatching);
        Plan();

        await HandleAsync(store, "main");

        await writer.Received(1).VersioningPlanAsync(
            Arg.Is<GitWriterVersioningPlanRequest>(r => r.Commit == Sha && r.Branch == "main" && r.BranchMajor == null), Arg.Any<CancellationToken>());
        forge.Dispatches.Should().BeEmpty();
        await git.Received(1).RevokeWriterAccessAsync(Arg.Any<Repository>(), Arg.Any<ForgeWriterAccess>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_merge_that_changes_a_package_dispatches_the_workflow_on_its_branch()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        Config(Dispatching);
        Plan("Acme.Core");

        await HandleAsync(store, "release/8.x");

        await writer.Received(1).VersioningPlanAsync(Arg.Is<GitWriterVersioningPlanRequest>(r => r.BranchMajor == 8), Arg.Any<CancellationToken>());
        forge.Dispatches.Should().Equal(("publish.yml", "release/8.x"));
        using var session = store.OpenAsyncSession();
        var state = await session.LoadAsync<VersioningDispatchState>(VersioningDispatchState.ForRepository(RepositoryId));
        state!.Dispatch.Should().Be("publish.yml");
        state.ReleaseBranches.Should().Equal("release/*.x");
    }

    [Fact]
    public async Task Without_a_dispatch_setting_the_writer_is_not_asked_and_the_cache_is_dropped()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, new VersioningDispatchState { Dispatch = "publish.yml", ReleaseBranches = ["release/*.x"] });
        Config("versioning:\n");

        await HandleAsync(store, "main");

        await writer.DidNotReceiveWithAnyArgs().VersioningPlanAsync(default!, default);
        forge.Dispatches.Should().BeEmpty();
        using var session = store.OpenAsyncSession();
        (await session.LoadAsync<VersioningDispatchState>(VersioningDispatchState.ForRepository(RepositoryId))).Should().BeNull();
    }

    [Fact]
    public async Task Only_the_default_branch_and_cached_release_lines_are_forwarded()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);

        await PushAsync(store, "feature/x");
        await PushAsync(store, "release/8.x");
        await bus.DidNotReceiveWithAnyArgs().BroadcastOnceAsync<VersioningDispatchMessage>(default!, default!, default);

        await PushAsync(store, "main");
        await bus.Received(1).BroadcastOnceAsync(Arg.Is<VersioningDispatchMessage>(m => m.Branch == "main" && m.Sha == Sha),
            $"versioning-dispatch:{RepositoryId}:main:{Sha}", Arg.Any<CancellationToken>());

        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new VersioningDispatchState { Dispatch = "publish.yml", ReleaseBranches = ["release/*.x"] }, VersioningDispatchState.ForRepository(RepositoryId));
            await session.SaveChangesAsync();
        }
        await PushAsync(store, "release/8.x");
        await PushAsync(store, "feature/x");
        await bus.Received(1).BroadcastOnceAsync(Arg.Is<VersioningDispatchMessage>(m => m.Branch == "release/8.x"), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await bus.DidNotReceive().BroadcastOnceAsync(Arg.Is<VersioningDispatchMessage>(m => m.Branch == "feature/x"), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
