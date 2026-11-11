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
using MintPlayer.Spark.Authorization.OneShotOAuth;
using Raven.Client.Documents.Session;

namespace CodeCoverage.CustomActions;

/// <summary>
/// "Set up features" (#571 M10c, coverage_backport_PRD D3), on a Repository's page: the onboarding chooser. A checkbox form
/// (the virtual <c>SetUpFeatures</c> type) asks which features, the plan is shown for a confirmation, and one pull request
/// on <c>mintplayer/onboarding</c> carries all of them (<see cref="IOnboardingService"/>). It replaces "Set up versioning".
/// For the people who may change the repository's dependency updates (a manager of the account, or an administrator of the
/// repository on the forge, checked now). Everything before the last prompt only reads, so the passes that carry the
/// answers repeat it harmlessly.
/// With the merge queue picked, the rulesets are a step of the chooser (#554 f4, owner 2026-11-06: "apply at
/// confirmation"): the GitHub popup and the rulesets form follow the checkbox form, the rulesets' plan joins the
/// confirmation, and they are applied right after the onboarding pull request opens, with the admin's one-shot token
/// (<see cref="IMergeQueueRulesetSetup"/>). When they cannot be (not GitHub, no OAuth app, a refused read), the
/// confirmation says they are left to set by hand and the pull request still opens.
/// </summary>
public partial class SetUpFeaturesAction : SparkCustomAction
{
    [Inject] private readonly IDependencyOnboardingService permissions;
    [Inject] private readonly IOnboardingService onboarding;
    [Inject] private readonly IMergeQueueRulesetSetup rulesets;
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

        // Step 0: which features. The form starts from what the repository has now, plus what an open onboarding pull
        // request carries (OnboardingFeatureDetection), not from what this chooser last asked for.
        var current = await onboarding.CurrentFeaturesAsync(repository, cancellationToken);
        var form = await manager.GetPersistentObjectAsync(Guid.Parse(PersistentObjectIds.Default.SetUpFeatures));
        foreach (var feature in Enum.GetValues<EOnboardingFeature>())
            form[feature.ToString()].Value = current.Contains(feature);
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

        // What refuses without reading the forge refuses here, before the GitHub popup and the rulesets form: the admin
        // must not authorize for a pull request that cannot open (#554, owner 2026-11-06).
        if (onboarding.RefusalBeforePlanning(repository) is { } refusal)
        {
            Report(new(VersioningOnboardingOutcome.Refused, refusal), args);
            return;
        }

        // The rulesets step (#554 f4): with the merge queue picked, the popup and the rulesets form come here, and the
        // rulesets are applied at the confirmation with the admin's one-shot token, not at the merge.
        string? byHand = null;
        IReadOnlyList<RulesetPlanned>? rulesetPlans = null;
        ISparkOneShotOAuthLease? lease = null;
        try
        {
            if (features.Contains(EOnboardingFeature.MergeQueue))
            {
                if (rulesets.TargetFor(repository, out var reason) is not { } target)
                    byHand = reason;
                else if (!rulesets.IsConfigured)
                    byHand = manager.GetTranslatedMessage("rulesets.notConfigured");
                else
                {
                    lease = await rulesets.AuthorizeAsync(cancellationToken);
                    if (lease is null)
                        return;
                    var (read, refused) = await rulesets.ReadAsync([target], lease.AccessToken, cancellationToken);
                    if (refused.Count > 0)
                        byHand = refused[0].Message;
                    else if ((rulesetPlans = await rulesets.ChooseAsync(read, cancellationToken,
                        releaseLinesComing: features.Contains(EOnboardingFeature.ReleaseLines))) is null)
                        return;
                }
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
                manager.Retry.Action(manager.GetTranslatedMessage("onboarding.confirmTitle"), [open], open,
                    message: Summary(planned, rulesetPlans, byHand), cancellable: true);
                if (manager.Retry.Result!.Option == RetryResult.CancelOption) return;

                outcome = await onboarding.OnboardAsync(repository, features, cancellationToken);
            }
            catch (Exception ex) when (ex is GitWriterUnavailableException or HttpRequestException)
            {
                outcome = new(VersioningOnboardingOutcome.Refused, manager.GetTranslatedMessage("onboarding.unavailable"));
            }
            Report(outcome, args);

            // The rulesets follow only an onboarding that went through: a refused one leaves no workflow to report the
            // checks ruleset 2 requires.
            if (rulesetPlans is not null && lease is not null && outcome.Outcome is not VersioningOnboardingOutcome.Refused)
            {
                var (applied, failed) = await rulesets.ApplyAsync(rulesetPlans, lease.AccessToken, cancellationToken);
                rulesets.Notify(applied, failed);
            }
        }
        finally
        {
            if (lease is not null)
                await lease.DisposeAsync();
        }
    }

    private static bool IsChecked(PersistentObject? form, EOnboardingFeature feature)
        => form?[feature.ToString()].GetValue<bool>() == true;

    private void Report(VersioningOnboardingOutcome outcome, CustomActionArgs args)
    {
        args.SetResult(outcome);
        manager.Client.Notify(outcome.Note, outcome.Outcome is VersioningOnboardingOutcome.Opened or VersioningOnboardingOutcome.Seeded or VersioningOnboardingOutcome.Nothing
            ? NotificationKind.Success : NotificationKind.Warning);
    }

    /// <summary>
    /// The plan as the confirmation's HTML (<see cref="HtmlMessage"/>): a list each for the features, the files, the renames
    /// and the settings switched on at the merge, the notes as lines, then the rulesets or why they are left by hand.
    /// </summary>
    internal string Summary(OnboardingPlan plan, IReadOnlyList<RulesetPlanned>? rulesetPlans = null, string? rulesetsByHand = null)
    {
        var html = new List<string>
        {
            HtmlMessage.Section(manager.GetTranslatedMessage("onboarding.summary.features"), plan.Features.Select(f => f.ToString())),
            plan.Files.Count == 0
                ? HtmlMessage.Line(manager.GetTranslatedMessage("onboarding.summary.noFiles"))
                : HtmlMessage.Section(manager.GetTranslatedMessage("onboarding.summary.files"), plan.Files.Select(f => f.Delete ? $"-{f.Path}" : f.Path)),
        };
        if (plan.Renames.Renames.Count > 0)
            html.Add(HtmlMessage.Section(manager.GetTranslatedMessage("onboarding.summary.renames"), plan.Renames.Renames.Select(r => $"{r.From} → {r.To}")));
        if (plan.OnMerge.Count > 0)
            html.Add(HtmlMessage.Section(manager.GetTranslatedMessage("onboarding.summary.onMerge"), plan.OnMerge.Select(o => o.ToString())));
        html.AddRange(plan.Notes.Select(HtmlMessage.Line));
        if (rulesetPlans is not null)
            html.Add(HtmlMessage.Encode(manager.GetTranslatedMessage("onboarding.summary.rulesets")) + rulesets.SummaryHtml(rulesetPlans));
        if (rulesetsByHand is not null)
            html.Add(HtmlMessage.Line(manager.GetTranslatedMessage("onboarding.summary.rulesetsByHand", rulesetsByHand)));
        return string.Concat(html);
    }
}
