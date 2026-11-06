using CodeCoverage.Dependencies.Onboarding;
using CodeCoverage.Entities;
using CodeCoverage.MergeQueue;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Actions;
using Raven.Client.Documents.Session;

namespace CodeCoverage.CustomActions;

/// <summary>
/// "Set up rulesets" (#554 f3): a query action on the repository lists (<c>selectionRule ">0"</c>) that sets up the merge
/// queue's two rulesets (merge-queue.md §3) on every selected repository, moving any classic branch protection into them.
/// The onboarding chooser does the same for one repository (<see cref="SetUpFeaturesAction"/>, f4); both go through
/// <see cref="IMergeQueueRulesetSetup"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The admin's own token, never the App's.</b> GitHub has no App permission narrower than Administration for rulesets,
/// and the Apps no longer hold it. The action authorizes the dedicated OAuth app (<see cref="GitHubRulesetsOAuth"/>) in a
/// popup, and the token's lease revokes it when the action ends, or after five minutes.
/// </para>
/// <para>
/// <b>Popup first, then preview</b> (owner, 2026-11-06). Only the token can read the full state: classic protection needs
/// Administration, and a ruleset's bypass actors are shown only to someone with write access to it. So the passes are:
/// <list type="number">
/// <item>the popup;</item>
/// <item>the form (approvals, admin bypass), starting from what the repositories have;</item>
/// <item>one confirmation listing each repository's exact plan or blockers;</item>
/// <item>the apply, one repository at a time.</item>
/// </list>
/// The lease survives the prompts (<see cref="MintPlayer.Spark.Authorization.OneShotOAuth.ISparkOneShotOAuthLease"/>).
/// </para>
/// <para>
/// <b>Partial results are reported, never hidden</b>, as in <see cref="EnableDependencyUpdatesAction"/>: one notification
/// per repository set up, one warning per repository that was not, and the same in the result for API callers.
/// </para>
/// </remarks>
public partial class SetUpRulesetsAction : SparkCustomAction
{
    [Inject] private readonly IDependencyOnboardingService permissions;
    [Inject] private readonly IMergeQueueRulesetSetup rulesets;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IManager manager;

    public override async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        var ids = args.SelectedItems.Select(i => i.Id).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
            return;

        var failed = new List<RulesetRepositoryResult>();
        var loaded = await session.LoadAsync<Repository>(ids, cancellationToken);
        failed.AddRange(ids.Where(id => loaded.GetValueOrDefault(id) is null)
            .Select(id => new RulesetRepositoryResult(id, id, manager.GetTranslatedMessage("rulesets.reason.notFound"), [])));
        var repositories = ids.Select(id => loaded.GetValueOrDefault(id)).OfType<Repository>().ToList();
        var managed = await permissions.CheckAsync(repositories, cancellationToken);

        var targets = new List<RulesetTarget>();
        foreach (var repository in repositories)
        {
            if (managed.GetValueOrDefault(repository.Id!) is null)
                failed.Add(RulesetRepositoryResult.Failure(repository, manager.GetTranslatedMessage("rulesets.reason.notManaged")));
            else if (rulesets.TargetFor(repository, out var reason) is { } target)
                targets.Add(target);
            else
                failed.Add(RulesetRepositoryResult.Failure(repository, reason!));
        }

        if (targets.Count == 0)
        {
            Report(args, [], failed);
            return;
        }
        if (!rulesets.IsConfigured)
        {
            manager.Client.Notify(manager.GetTranslatedMessage("rulesets.notConfigured"), NotificationKind.Error);
            return;
        }

        // Pass 1 (the popup). Every later pass borrows the same token.
        await using var lease = await rulesets.AuthorizeAsync(cancellationToken);
        if (lease is null)
            return;

        var (read, refused) = await rulesets.ReadAsync(targets, lease.AccessToken, cancellationToken);
        failed.AddRange(refused);
        if (read.Count == 0)
        {
            Report(args, [], failed);
            return;
        }

        // Pass 2: the form, one for all.
        if (await rulesets.ChooseAsync(read, cancellationToken) is not { } planned)
            return;

        // Pass 3: one confirmation with every repository's exact plan.
        var summary = rulesets.Summary(planned);
        if (!planned.Any(p => p.Plan.CanApply))
        {
            manager.Client.Notify(manager.GetTranslatedMessage("rulesets.nothingToApply") + "\n\n" + summary, NotificationKind.Info);
            failed.AddRange(planned.Where(p => p.Plan.Blockers.Count > 0)
                .Select(p => RulesetRepositoryResult.Failure(p.Target.Repository, string.Join(" ", p.Plan.Blockers))));
            Report(args, [], failed);
            return;
        }
        var apply = manager.GetTranslatedMessage("rulesets.apply");
        manager.Retry.Action(manager.GetTranslatedMessage("rulesets.confirmTitle"), [apply], apply, message: rulesets.SummaryHtml(planned), cancellable: true);
        if (manager.Retry.Result!.Option == RetryResult.CancelOption)
            return;

        var (applied, notApplied) = await rulesets.ApplyAsync(planned, lease.AccessToken, cancellationToken);
        failed.AddRange(notApplied);
        Report(args, applied, failed);
    }

    private void Report(CustomActionArgs args, IReadOnlyList<RulesetRepositoryResult> applied, IReadOnlyList<RulesetRepositoryResult> failed)
    {
        args.SetResult(new SetUpRulesetsResult(applied, failed));
        rulesets.Notify(applied, failed);
    }
}

/// <summary>What <see cref="SetUpRulesetsAction"/> returns (the response envelope's <c>result</c>).</summary>
/// <param name="Applied">The repositories whose rulesets were changed, with each step and its link.</param>
/// <param name="NotApplied">Every selected repository that was not changed for a reason, with why. One already set up is in neither.</param>
public sealed record SetUpRulesetsResult(IReadOnlyList<RulesetRepositoryResult> Applied, IReadOnlyList<RulesetRepositoryResult> NotApplied);
