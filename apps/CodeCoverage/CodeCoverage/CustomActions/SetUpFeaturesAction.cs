using CodeCoverage.Dependencies.Onboarding;
using CodeCoverage.Entities;
using CodeCoverage.MergeQueue;
using CodeCoverage.Onboarding;
using CodeCoverage.PackageVersioning;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Actions;
using Raven.Client.Documents.Session;

namespace CodeCoverage.CustomActions;

/// <summary>
/// "Set up features" (#571 M10c, coverage_backport_PRD D3), on a Repository's page: the onboarding chooser. A checkbox form
/// (the virtual <c>SetUpFeatures</c> type) asks which features, the plan is shown for a confirmation, and one pull request
/// on <c>mintplayer/onboarding</c> carries all of them (<see cref="IOnboardingService"/>). It replaces "Set up versioning".
/// For the people who may change the repository's dependency updates (a manager of the account, or an administrator of the
/// repository on the forge, checked now). Everything before the last prompt only reads, so the passes that carry the
/// answers repeat it harmlessly.
/// </summary>
public partial class SetUpFeaturesAction : SparkCustomAction
{
    [Inject] private readonly IDependencyOnboardingService permissions;
    [Inject] private readonly IOnboardingService onboarding;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IManager manager;

    public override async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        if (args.Parent?.Id is not { Length: > 0 } id || await session.LoadAsync<Repository>(id, cancellationToken) is not { } repository)
            return;
        if ((await permissions.CheckAsync([repository], cancellationToken)).GetValueOrDefault(repository.Id!) is null)
        {
            Report(new(VersioningOnboardingOutcome.Refused, manager.GetTranslatedMessage("onboarding.notManaged")), args);
            return;
        }

        // Step 0: which features. The form starts from what the open onboarding pull request carries.
        var state = await session.LoadAsync<VersioningOnboardingState>(VersioningOnboardingState.ForRepository(repository.Id!), cancellationToken);
        var form = await manager.GetPersistentObjectAsync(Guid.Parse(PersistentObjectIds.Default.SetUpFeatures));
        foreach (var feature in Enum.GetValues<EOnboardingFeature>())
            form[feature.ToString()].Value = state?.Features.Contains(feature) ?? false;
        var plan = manager.GetTranslatedMessage("onboarding.plan");
        manager.Retry.Action(manager.GetTranslatedMessage("onboarding.chooseTitle"), [plan], plan, form, manager.GetTranslatedMessage("onboarding.chooseMessage"), cancellable: true);
        var chosen = manager.Retry.Result!;
        if (chosen.Option == RetryResult.CancelOption) return;
        var features = Enum.GetValues<EOnboardingFeature>().Where(f => IsChecked(chosen.PersistentObject, f)).ToHashSet();
        if (features.Count == 0)
        {
            Report(new(VersioningOnboardingOutcome.Refused, manager.GetTranslatedMessage("onboarding.nothingPicked")), args);
            return;
        }

        // Step 1: the plan, for a confirmation.
        VersioningOnboardingOutcome outcome;
        try
        {
            var preview = await onboarding.PreviewAsync(repository, features, cancellationToken);
            if (preview.Plan is not { } planned)
            {
                Report(new(VersioningOnboardingOutcome.Refused, preview.Refusal!), args);
                return;
            }
            var open = manager.GetTranslatedMessage("onboarding.open");
            manager.Retry.Action(manager.GetTranslatedMessage("onboarding.confirmTitle"), [open], open, message: Summary(planned), cancellable: true);
            if (manager.Retry.Result!.Option == RetryResult.CancelOption) return;

            outcome = await onboarding.OnboardAsync(repository, features, cancellationToken);
        }
        catch (Exception ex) when (ex is GitWriterUnavailableException or HttpRequestException)
        {
            outcome = new(VersioningOnboardingOutcome.Refused, manager.GetTranslatedMessage("onboarding.unavailable"));
        }
        Report(outcome, args);
    }

    private static bool IsChecked(PersistentObject? form, EOnboardingFeature feature)
        => form?[feature.ToString()].GetValue<bool>() == true;

    private void Report(VersioningOnboardingOutcome outcome, CustomActionArgs args)
    {
        args.SetResult(outcome);
        manager.Client.Notify(outcome.Note, outcome.Outcome is VersioningOnboardingOutcome.Opened or VersioningOnboardingOutcome.Seeded or VersioningOnboardingOutcome.Nothing
            ? NotificationKind.Success : NotificationKind.Warning);
    }

    /// <summary>The plan in a few lines: the files, the renames, the settings switched on at the merge, and what is left by hand.</summary>
    internal string Summary(OnboardingPlan plan)
    {
        var lines = new List<string>
        {
            manager.GetTranslatedMessage("onboarding.summary.features", string.Join(", ", plan.Features)),
            plan.Files.Count == 0
                ? manager.GetTranslatedMessage("onboarding.summary.noFiles")
                : manager.GetTranslatedMessage("onboarding.summary.files", string.Join(", ", plan.Files.Select(f => f.Delete ? $"-{f.Path}" : f.Path))),
        };
        if (plan.Renames.Renames.Count > 0)
            lines.Add(manager.GetTranslatedMessage("onboarding.summary.renames", string.Join(", ", plan.Renames.Renames.Select(r => $"{r.From} → {r.To}"))));
        if (plan.OnMerge.Count > 0)
            lines.Add(manager.GetTranslatedMessage("onboarding.summary.onMerge", string.Join(", ", plan.OnMerge)));
        lines.AddRange(plan.Notes);
        return string.Join("\n", lines);
    }
}
