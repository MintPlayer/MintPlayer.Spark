using CodeCoverage.Dependencies.Onboarding;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using Microsoft.Extensions.Options;
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
/// "Set up rulesets" (#554 f3): a query action on the repository lists (<c>selectionRule ">0"</c>) that sets up the merge
/// queue's two rulesets (merge-queue.md §3) on every selected repository, moving any classic branch protection into them.
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
/// The lease survives the prompts (<see cref="ISparkOneShotOAuthLease"/>). Everything before the apply only reads, so the
/// passes that carry the answers repeat it harmlessly.
/// </para>
/// <para>
/// <b>Partial results are reported, never hidden</b>, as in <see cref="EnableDependencyUpdatesAction"/>: one notification
/// per repository set up, one warning per repository that was not, and the same in the result for API callers.
/// </para>
/// </remarks>
public partial class SetUpRulesetsAction : SparkCustomAction
{
    [Inject] private readonly IDependencyOnboardingService permissions;
    [Inject] private readonly IEnumerable<IForgeMergeQueue> queues;
    [Inject] private readonly ISparkOneShotOAuth oauth;
    [Inject] private readonly IOptions<SparkOneShotOAuthOptions> oauthOptions;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IManager manager;
    [Inject] private readonly ILogger<SetUpRulesetsAction> logger;

    public override async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        var ids = args.SelectedItems.Select(i => i.Id).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
            return;

        var failed = new List<RulesetRepositoryResult>();
        var loaded = await session.LoadAsync<Repository>(ids, cancellationToken);
        failed.AddRange(ids.Where(id => loaded.GetValueOrDefault(id) is null).Select(id => Failure(id, id, Reason("notFound"))));
        var repositories = ids.Select(id => loaded.GetValueOrDefault(id)).OfType<Repository>().ToList();
        var managed = await permissions.CheckAsync(repositories, cancellationToken);

        var targets = new List<(Repository Repository, IForgeMergeQueue Queue, string Branch)>();
        foreach (var repository in repositories)
        {
            if (managed.GetValueOrDefault(repository.Id!) is null)
                failed.Add(Failure(repository, Reason("notManaged")));
            else if (repository.Provider != EForgeProvider.GitHub || queues.FirstOrDefault(q => q.Provider == repository.Provider) is not { } queue)
                failed.Add(Failure(repository, Reason("unsupported")));
            else if (repository.DefaultBranch is not { Length: > 0 } branch)
                failed.Add(Failure(repository, Reason("noDefaultBranch")));
            else
                targets.Add((repository, queue, branch));
        }

        if (targets.Count == 0)
        {
            Report(args, [], failed);
            return;
        }
        if (!GitHubRulesetsOAuth.IsConfigured(oauthOptions.Value))
        {
            manager.Client.Notify(manager.GetTranslatedMessage("rulesets.notConfigured"), NotificationKind.Error);
            return;
        }

        // Pass 1 (the popup). Every later pass borrows the same token.
        var authorized = await oauth.AuthorizeAsync(GitHubRulesetsOAuth.ClientName, [GitHubRulesetsOAuth.Scope], cancellationToken);
        if (!authorized.Succeeded)
        {
            if (authorized.Error != SparkOneShotOAuthErrors.Cancelled)
                manager.Client.Notify(manager.GetTranslatedMessage(ErrorKey(authorized.Error)), NotificationKind.Error);
            return;
        }
        await using var lease = authorized.Lease!;

        // Read every repository with the token (side-effect free, repeated on each pass).
        var read = new List<(Repository Repository, IForgeMergeQueue Queue, string Branch, ForgeRulesetState State, MergeQueueRulesetChoices Defaults)>();
        foreach (var (repository, queue, branch) in targets)
        {
            try
            {
                if (await queue.GetRulesetStateAsync(repository, branch, lease.AccessToken, cancellationToken) is not { } state)
                {
                    failed.Add(Failure(repository, Reason("unsupported")));
                    continue;
                }
                var head = await queue.GetBranchHeadAsync(repository, branch, cancellationToken);
                var headChecks = head is null ? [] : await queue.GetCheckNamesOnCommitAsync(repository, head, cancellationToken);
                read.Add((repository, queue, branch, state, MergeQueueRulesetPlanner.Defaults(state, headChecks)));
            }
            catch (Exception ex) when (ex is ForgeAccessDeniedException or ForgeRequestException or HttpRequestException)
            {
                failed.Add(Failure(repository, ex.Message));
            }
        }
        if (read.Count == 0)
        {
            Report(args, [], failed);
            return;
        }

        // Pass 2: the form, one for all; it starts from what the repositories agree on.
        var form = await manager.GetPersistentObjectAsync(Guid.Parse(PersistentObjectIds.Default.SetUpRulesets));
        var approvalsStart = read.Select(r => r.Defaults.RequiredApprovals).Distinct().Count() == 1 ? read[0].Defaults.RequiredApprovals : 1;
        var adminsStart = read.All(r => r.Defaults.AdminsMayMergeByPullRequest == true);
        form[nameof(MergeQueueRulesetChoices.RequiredApprovals)].Value = approvalsStart;
        form[nameof(MergeQueueRulesetChoices.AdminsMayMergeByPullRequest)].Value = adminsStart;
        var next = manager.GetTranslatedMessage("onboarding.plan");
        manager.Retry.Action(manager.GetTranslatedMessage("rulesets.chooseTitle"), [next], next, form,
            manager.GetTranslatedMessage("rulesets.chooseMessage"), cancellable: true);
        var chosen = manager.Retry.Result!;
        if (chosen.Option == RetryResult.CancelOption)
            return;
        var approvals = Math.Max(0, chosen.PersistentObject?[nameof(MergeQueueRulesetChoices.RequiredApprovals)].GetValue<int?>() ?? approvalsStart);
        var admins = chosen.PersistentObject?[nameof(MergeQueueRulesetChoices.AdminsMayMergeByPullRequest)].GetValue<bool?>() ?? adminsStart;

        // Pass 3: one confirmation with every repository's exact plan.
        var planned = read.Select(r => (r.Repository, r.Queue, r.Branch,
            Plan: MergeQueueRulesetPlanner.Plan(r.State, r.Defaults with { RequiredApprovals = approvals, AdminsMayMergeByPullRequest = admins }))).ToList();
        var summary = string.Join("\n\n", planned.Select(p =>
            p.Repository.FullName + "\n" + string.Join("\n", MergeQueueRulesetPlanner.Describe(p.Plan).Select(l => "- " + l))));
        if (!planned.Any(p => p.Plan.CanApply))
        {
            manager.Client.Notify(manager.GetTranslatedMessage("rulesets.nothingToApply") + "\n\n" + summary, NotificationKind.Info);
            failed.AddRange(planned.Where(p => p.Plan.Blockers.Count > 0).Select(p => Failure(p.Repository, string.Join(" ", p.Plan.Blockers))));
            Report(args, [], failed);
            return;
        }
        var apply = manager.GetTranslatedMessage("rulesets.apply");
        manager.Retry.Action(manager.GetTranslatedMessage("rulesets.confirmTitle"), [apply], apply, message: summary, cancellable: true);
        if (manager.Retry.Result!.Option == RetryResult.CancelOption)
            return;

        // The apply: one repository at a time, each reported on its own.
        var applied = new List<RulesetRepositoryResult>();
        foreach (var (repository, queue, branch, plan) in planned)
        {
            if (plan.Blockers.Count > 0)
            {
                failed.Add(Failure(repository, string.Join(" ", plan.Blockers)));
                continue;
            }
            if (!plan.CanApply)
                continue; // set up already
            try
            {
                var steps = await queue.ApplyRulesetPlanAsync(repository, branch, plan, lease.AccessToken, cancellationToken);
                applied.Add(new RulesetRepositoryResult(repository.Id!, repository.FullName, string.Join(" ", steps.Select(s => s.Description)), [.. steps]));
            }
            catch (Exception ex) when (ex is ForgeAccessDeniedException or ForgeRequestException or HttpRequestException)
            {
                logger.LogWarning(ex, "Set up rulesets: {Repository} was refused", repository.FullName);
                failed.Add(Failure(repository, ex.Message));
            }
        }
        Report(args, applied, failed);
    }

    private void Report(CustomActionArgs args, IReadOnlyList<RulesetRepositoryResult> applied, IReadOnlyList<RulesetRepositoryResult> failed)
    {
        args.SetResult(new SetUpRulesetsResult(applied, failed));
        foreach (var done in applied)
            manager.Client.Notify(manager.GetTranslatedMessage("rulesets.applied", done.FullName, done.Message), NotificationKind.Success);
        foreach (var refusal in failed)
            manager.Client.Notify(manager.GetTranslatedMessage("rulesets.failed", refusal.FullName, refusal.Message), NotificationKind.Warning);
    }

    private static string ErrorKey(string? error) => error switch
    {
        SparkOneShotOAuthErrors.PopupBlocked => "rulesets.error.popupBlocked",
        SparkOneShotOAuthErrors.Denied => "rulesets.error.denied",
        SparkOneShotOAuthErrors.Expired => "rulesets.error.expired",
        _ => "rulesets.error.failed",
    };

    private string Reason(string key) => manager.GetTranslatedMessage($"rulesets.reason.{key}");

    private static RulesetRepositoryResult Failure(Repository repository, string message) => Failure(repository.Id!, repository.FullName, message);

    private static RulesetRepositoryResult Failure(string id, string fullName, string message) => new(id, fullName, message, []);
}

/// <summary>What <see cref="SetUpRulesetsAction"/> returns (the response envelope's <c>result</c>).</summary>
/// <param name="Applied">The repositories whose rulesets were changed, with each step and its link.</param>
/// <param name="NotApplied">Every selected repository that was not changed for a reason, with why. One already set up is in neither.</param>
public sealed record SetUpRulesetsResult(IReadOnlyList<RulesetRepositoryResult> Applied, IReadOnlyList<RulesetRepositoryResult> NotApplied);

public sealed record RulesetRepositoryResult(string RepositoryId, string FullName, string Message, IReadOnlyList<ForgeRulesetApplied> Steps);
