using CodeCoverage.CustomActions;
using CodeCoverage.Dependencies.Onboarding;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Onboarding;
using CodeCoverage.Tests.Dependencies.Lifecycle;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;
using NSubstitute;
using Xunit;
using static CodeCoverage.Tests.Dependencies.Lifecycle.DependencyHarness;

namespace CodeCoverage.Tests.CustomActions;

/// <summary>
/// multi-target-dependency-split-PRD P7 (M7): "Convert central package management" on several repositories asks one direction,
/// then converts each repository the caller manages and reports every one that was not.
/// </summary>
public class ConvertCentralPackagesActionTests : CoverageRavenTest
{
    private static readonly string Foreign = Repository.DocumentId(EForgeProvider.GitHub, 920001);

    private sealed class ScriptedPermissions : IDependencyOnboardingService
    {
        public Task<DependencyOnboardingResponse> ListAsync(IReadOnlyDictionary<string, string?> query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, string?>> CheckAsync(IReadOnlyList<Repository> repositories, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, string?>>(
                repositories.ToDictionary(r => r.Id.Value, r => string.Equals(r.Id.Value, RepositoryId, StringComparison.OrdinalIgnoreCase) ? DependencyOnboardingService.Owner : null));
    }

    private readonly IManager manager = Substitute.For<IManager>();
    private readonly IOnboardingService onboarding = Substitute.For<IOnboardingService>();

    public ConvertCentralPackagesActionTests()
    {
        manager.GetTranslatedMessage(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call => call.ArgAt<string>(0));
        onboarding.ConvertCentralPackagesAsync(Arg.Any<Repository>(), Arg.Any<ECentralPackageDirection>(), Arg.Any<CancellationToken>())
            .Returns(new CentralPackageResult(true, "opened #7 into master", "https://github.com/acme/widget/pull/7"));
    }

    private async Task<ConvertCentralPackagesAction> ActionAsync()
    {
        var h = new DependencyHarness(GetDocumentStore());
        await h.SeedAsync(enabled: false);
        await h.StoreAsync(new Repository { GitHubId = 920001, Name = "tool", FullName = "other-org/tool", OwnerLogin = "other-org", DefaultBranch = "main" }, Foreign);
        return new ConvertCentralPackagesAction(new ScriptedPermissions(), onboarding, h.Store.OpenAsyncSession(), manager);
    }

    private static CustomActionArgs Selected(params string[] ids)
        => new() { SelectedItems = [.. ids.Select(id => new QueryResultItem { Id = id, Values = [] })] };

    [Theory]
    [InlineData("centralPackages.to", ECentralPackageDirection.ToCentral)]
    [InlineData("centralPackages.from", ECentralPackageDirection.FromCentral)]
    public async Task Two_repositories_one_direction_the_managed_one_converts(string option, ECentralPackageDirection direction)
    {
        var action = await ActionAsync();
        manager.Retry.Result.Returns(new RetryResult { Option = option });
        var args = Selected(RepositoryId, Foreign);

        await action.ExecuteAsync(args);

        manager.Retry.Received(1).Action("centralPackages.chooseTitle", Arg.Is<string[]>(o => o.SequenceEqual(new[] { "centralPackages.to", "centralPackages.from" })),
            "centralPackages.to", null, "centralPackages.chooseMessage", true);
        await onboarding.Received(1).ConvertCentralPackagesAsync(Arg.Is<Repository>(r => string.Equals(r.Id.Value, RepositoryId, StringComparison.OrdinalIgnoreCase)), direction, Arg.Any<CancellationToken>());
        await onboarding.DidNotReceive().ConvertCentralPackagesAsync(Arg.Is<Repository>(r => string.Equals(r.Id.Value, Foreign, StringComparison.OrdinalIgnoreCase)), Arg.Any<ECentralPackageDirection>(), Arg.Any<CancellationToken>());

        var result = (ConvertCentralPackagesResult)args.Result!;
        result.Direction.Should().Be(direction);
        result.Repositories.Single(r => string.Equals(r.Id, RepositoryId, StringComparison.OrdinalIgnoreCase)).PullRequestUrl.Should().Be("https://github.com/acme/widget/pull/7");
        result.Repositories.Single(r => string.Equals(r.Id, Foreign, StringComparison.OrdinalIgnoreCase)).Note.Should().Be("centralPackages.reason.notManaged");
        manager.Client.Received(1).Notify("centralPackages.opened", NotificationKind.Success);
        manager.Client.Received(1).Notify("centralPackages.notOpened", NotificationKind.Warning);
    }

    [Fact]
    public async Task Cancel_converts_nothing()
    {
        var action = await ActionAsync();
        manager.Retry.Result.Returns(new RetryResult { Option = RetryResult.CancelOption });

        await action.ExecuteAsync(Selected(RepositoryId));

        await onboarding.DidNotReceive().ConvertCentralPackagesAsync(Arg.Any<Repository>(), Arg.Any<ECentralPackageDirection>(), Arg.Any<CancellationToken>());
    }
}
