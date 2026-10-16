using CodeCoverage.CustomActions;
using CodeCoverage.Dependencies.Onboarding;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Tests.Dependencies.Lifecycle;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Retry;
using NSubstitute;
using Xunit;
using static CodeCoverage.Tests.Dependencies.Lifecycle.DependencyHarness;

namespace CodeCoverage.Tests.CustomActions;

/// <summary>
/// "Enable dependency updates" (dependency-updates PRD R22, R25, R26): every selected row re-checked, the allowed ones saved
/// through Spark (so the switch's after-commit hook onboards them, R23), the others reported, never skipped silently.
/// </summary>
public class EnableDependencyUpdatesActionTests : CoverageRavenTest
{
    private static readonly string Foreign = Repository.DocumentId(EForgeProvider.GitHub, 920001);

    private sealed class ScriptedOnboarding : IDependencyOnboardingService
    {
        public Task<DependencyOnboardingResponse> ListAsync(IReadOnlyDictionary<string, string?> query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public List<string> Checked { get; } = [];

        public Task<IReadOnlyDictionary<string, string?>> CheckAsync(IReadOnlyList<Repository> repositories, CancellationToken cancellationToken = default)
        {
            Checked.AddRange(repositories.Select(r => r.Id!));
            return Task.FromResult<IReadOnlyDictionary<string, string?>>(repositories.ToDictionary(r => r.Id!, r => r.Id == RepositoryId ? DependencyOnboardingService.Manager : null));
        }
    }

    private static PersistentObject RepositoryObject(string id) => new()
    {
        Id = id, Name = "Repository", ObjectTypeId = EnableDependencyUpdatesAction.RepositoryType,
        Attributes =
        [
            new PersistentObjectAttribute { Name = nameof(Repository.DependencyUpdates), Value = "Inherit" },
            new PersistentObjectAttribute { Name = nameof(Repository.DependencyOnboardingMergeQueue), Value = "Inherit" },
        ],
    };

    [Theory]
    [InlineData("with", "Yes")]
    [InlineData("without", "No")]
    public async Task The_allowed_rows_are_saved_through_spark_and_the_others_reported(string answer, string mergeQueue)
    {
        var h = new DependencyHarness(GetDocumentStore());
        await h.SeedAsync(enabled: false);
        await h.StoreAsync(new Repository { GitHubId = 920001, Name = "tool", FullName = "other-org/tool", OwnerLogin = "other-org" }, Foreign);

        var onboarding = new ScriptedOnboarding();
        var database = Substitute.For<IDatabaseAccess>();
        var saved = new List<PersistentObject>();
        database.GetPersistentObjectsByIdAsync(EnableDependencyUpdatesAction.RepositoryType, Arg.Any<IReadOnlyList<string>>())
            .Returns(call => Task.FromResult<IReadOnlyList<PersistentObject>>([.. call.Arg<IReadOnlyList<string>>().Select(RepositoryObject)]));
        database.SavePersistentObjectAsync(Arg.Any<PersistentObject>()).Returns(call => { saved.Add(call.Arg<PersistentObject>()); return call.Arg<PersistentObject>(); });
        var manager = Substitute.For<IManager>();
        manager.GetTranslatedMessage("dependencyOnboarding.withMergeQueue", Arg.Any<object[]>()).Returns("with");
        manager.GetTranslatedMessage("dependencyOnboarding.withoutMergeQueue", Arg.Any<object[]>()).Returns("without");
        manager.Retry.Result.Returns(new RetryResult { Option = answer });

        using var session = h.Store.OpenAsyncSession();
        var action = new EnableDependencyUpdatesAction(onboarding, database, session, manager, NullLogger<EnableDependencyUpdatesAction>.Instance);
        var args = new CustomActionArgs
        {
            SelectedItems = [new QueryResultItem { Id = RepositoryId, Values = [] }, new QueryResultItem { Id = Foreign, Values = [] }],
        };

        await action.ExecuteAsync(args);

        onboarding.Checked.Should().Equal([RepositoryId, Foreign]);
        manager.Retry.Received(1).Action(Arg.Any<string>(), Arg.Is<string[]>(o => o.SequenceEqual(new[] { "with", "without" })), "with", null, null, true);
        saved.Should().ContainSingle();
        saved[0].Id.Should().Be(RepositoryId);
        saved[0][nameof(Repository.DependencyUpdates)].Value.Should().Be("Enabled");
        saved[0][nameof(Repository.DependencyOnboardingMergeQueue)].Value.Should().Be(mergeQueue);
        var result = (DependencyOnboardingResult)args.Result!;
        result.Enabled.Should().Equal(["acme/widget"]);
        result.NotEnabled.Should().ContainSingle().Which.Should().Be(new DependencyOnboardingRefusal(Foreign, "other-org/tool", "you don't manage this repository"));
    }

    [Fact]
    public async Task Nothing_is_saved_when_the_prompt_is_cancelled()
    {
        var h = new DependencyHarness(GetDocumentStore());
        await h.SeedAsync(enabled: false);
        var database = Substitute.For<IDatabaseAccess>();
        var manager = Substitute.For<IManager>();
        manager.Retry.Result.Returns(new RetryResult { Option = RetryResult.CancelOption });

        using var session = h.Store.OpenAsyncSession();
        await new EnableDependencyUpdatesAction(new ScriptedOnboarding(), database, session, manager, NullLogger<EnableDependencyUpdatesAction>.Instance)
            .ExecuteAsync(new CustomActionArgs { SelectedItems = [new QueryResultItem { Id = RepositoryId, Values = [] }] });

        await database.DidNotReceiveWithAnyArgs().SavePersistentObjectAsync(default!);
    }
}
