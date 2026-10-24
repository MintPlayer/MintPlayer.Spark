using CodeCoverage.Dependencies.Onboarding;
using CodeCoverage.Entities;
using CodeCoverage.MergeQueue;
using CodeCoverage.PackageVersioning;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Actions;
using Raven.Client.Documents.Session;

namespace CodeCoverage.CustomActions;

/// <summary>
/// "Set up versioning" (#153 M13, PRD D17), on a Repository's page: opens (or refreshes) the onboarding pull request that
/// moves the repository onto server-side versioning. For the people who may change its dependency updates (a manager of
/// the account, or an administrator of the repository on the forge, checked now). It runs while the person waits, so
/// the notification says what happened: the pull request, the seeds pushed, nothing to do, or why not.
/// </summary>
public partial class SetUpVersioningAction : SparkCustomAction
{
    [Inject] private readonly IDependencyOnboardingService permissions;
    [Inject] private readonly IVersioningOnboardingService onboarding;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IManager manager;

    public override async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        if (args.Parent?.Id is not { Length: > 0 } id || await session.LoadAsync<Repository>(id, cancellationToken) is not { } repository)
            return;
        var outcome = await RunAsync(repository, cancellationToken);
        args.SetResult(outcome);
        manager.Client.Notify(outcome.Note, outcome.Outcome is VersioningOnboardingOutcome.Opened or VersioningOnboardingOutcome.Seeded or VersioningOnboardingOutcome.Nothing
            ? NotificationKind.Success : NotificationKind.Warning);
    }

    internal async Task<VersioningOnboardingOutcome> RunAsync(Repository repository, CancellationToken cancellationToken)
    {
        if ((await permissions.CheckAsync([repository], cancellationToken)).GetValueOrDefault(repository.Id!) is null)
            return new(VersioningOnboardingOutcome.Refused, manager.GetTranslatedMessage("versioningOnboarding.notManaged"));
        try
        {
            return await onboarding.OnboardAsync(repository, cancellationToken);
        }
        catch (Exception ex) when (ex is GitWriterUnavailableException or HttpRequestException)
        {
            return new(VersioningOnboardingOutcome.Refused, manager.GetTranslatedMessage("versioningOnboarding.unavailable"));
        }
    }
}
