using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.PackageVersioning;
using CodeCoverage.Versioning.Releases;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Authorization.OneShotOAuth;
using System.Text.Json;

namespace CodeCoverage.MergeQueue;

/// <summary>
/// The steps that set up the merge queue's rulesets with the admin's one-shot token (#554 f3/f4), shared by
/// "Set up rulesets" (several repositories) and the onboarding chooser (one). Each step is one pass of the action's retry
/// conversation: <see cref="AuthorizeAsync"/> is the popup, <see cref="ChooseAsync"/> the form; everything before
/// <see cref="ApplyAsync"/> only reads, so the passes that carry the answers repeat it harmlessly.
/// </summary>
public interface IMergeQueueRulesetSetup
{
    /// <summary>Whether the rulesets OAuth app has a client id and secret (<see cref="GitHubRulesetsOAuth.IsConfigured"/>).</summary>
    bool IsConfigured { get; }

    /// <summary>The repository's target, or <see langword="null"/> with the translated reason it cannot have one.</summary>
    RulesetTarget? TargetFor(Repository repository, out string? reason);

    /// <summary>
    /// The popup. <see langword="null"/> when it did not give a token; the admin has been told why, except when they
    /// cancelled it. The caller disposes the lease, which revokes the token when the action ends.
    /// </summary>
    Task<ISparkOneShotOAuthLease?> AuthorizeAsync(CancellationToken cancellationToken);

    /// <summary>Reads each target with the token; a refused or unsupported read becomes a failure, never an exception.</summary>
    Task<(IReadOnlyList<RulesetRead> Read, IReadOnlyList<RulesetRepositoryResult> Failed)> ReadAsync(
        IReadOnlyList<RulesetTarget> targets, string accessToken, CancellationToken cancellationToken);

    /// <summary>
    /// The form (approvals, admin bypass), starting from what the repositories agree on, and each repository's plan with
    /// the answer. <see langword="null"/> when the admin cancelled. Ruleset 1 also covers a repository's release lines
    /// (<see cref="RulesetRead.Lines"/>); <paramref name="releaseLinesComing"/> (the chooser with Release lines picked)
    /// assumes the default pattern where coverage.yml has none yet, because the onboarding pull request adds it.
    /// </summary>
    Task<IReadOnlyList<RulesetPlanned>?> ChooseAsync(IReadOnlyList<RulesetRead> read, CancellationToken cancellationToken,
        bool releaseLinesComing = false);

    /// <summary>Each repository's name and its plan, one "- " line per step or blocker. Plain text, for a notification.</summary>
    string Summary(IReadOnlyList<RulesetPlanned> planned);

    /// <summary>
    /// The same as <see cref="Summary"/> as HTML for a confirmation (<see cref="IRetryAccessor.Action"/>'s message): a list of
    /// the repositories, each with a list of its steps, every value encoded.
    /// </summary>
    string SummaryHtml(IReadOnlyList<RulesetPlanned> planned);

    /// <summary>Applies every plan that can apply, one repository at a time; a blocked or refused one is a failure.</summary>
    Task<(IReadOnlyList<RulesetRepositoryResult> Applied, IReadOnlyList<RulesetRepositoryResult> Failed)> ApplyAsync(
        IReadOnlyList<RulesetPlanned> planned, string accessToken, CancellationToken cancellationToken);

    /// <summary>One success notification per repository set up, one warning per repository that was not.</summary>
    void Notify(IReadOnlyList<RulesetRepositoryResult> applied, IReadOnlyList<RulesetRepositoryResult> failed);
}

/// <summary>A repository the rulesets can be set up on: GitHub, with a merge queue and a default branch.</summary>
public sealed record RulesetTarget(Repository Repository, IForgeMergeQueue Queue, string Branch);

/// <summary>A target as the token read it, and the choices its state suggests.</summary>
/// <param name="Lines">The release lines of the default branch's coverage.yml (<c>versioning.lines</c>); null without them.</param>
public sealed record RulesetRead(RulesetTarget Target, ForgeRulesetState State, MergeQueueRulesetChoices Defaults, ReleaseLinePattern? Lines = null);

/// <summary>A target and its plan with the admin's choices.</summary>
public sealed record RulesetPlanned(RulesetTarget Target, MergeQueueRulesetPlan Plan);

/// <summary>One repository's outcome: what was done (<paramref name="Steps"/>), or why nothing was.</summary>
public sealed record RulesetRepositoryResult(string RepositoryId, string FullName, string Message, IReadOnlyList<ForgeRulesetApplied> Steps)
{
    public static RulesetRepositoryResult Failure(Repository repository, string message) => new(repository.Id!, repository.FullName, message, []);
}

[Register(typeof(IMergeQueueRulesetSetup), ServiceLifetime.Scoped)]
public partial class MergeQueueRulesetSetup : IMergeQueueRulesetSetup
{
    [Inject] private readonly IEnumerable<IForgeMergeQueue> queues;
    [Inject] private readonly ISparkOneShotOAuth oauth;
    [Inject] private readonly IOptions<SparkOneShotOAuthOptions> oauthOptions;
    [Inject] private readonly IManager manager;
    [Inject] private readonly ILogger<MergeQueueRulesetSetup> logger;
    [Inject] private readonly IForgeIntegrationResolver integrations;

    public bool IsConfigured => GitHubRulesetsOAuth.IsConfigured(oauthOptions.Value);

    public RulesetTarget? TargetFor(Repository repository, out string? reason)
    {
        reason = null;
        if (repository.Provider != EForgeProvider.GitHub || queues.FirstOrDefault(q => q.Provider == repository.Provider) is not { } queue)
            reason = Reason("unsupported");
        else if (repository.DefaultBranch is not { Length: > 0 } branch)
            reason = Reason("noDefaultBranch");
        else
            return new RulesetTarget(repository, queue, branch);
        return null;
    }

    public async Task<ISparkOneShotOAuthLease?> AuthorizeAsync(CancellationToken cancellationToken)
    {
        var authorized = await oauth.AuthorizeAsync(GitHubRulesetsOAuth.ClientName, [GitHubRulesetsOAuth.Scope], cancellationToken);
        if (authorized.Succeeded)
            return authorized.Lease;
        if (authorized.Error != SparkOneShotOAuthErrors.Cancelled)
            manager.Client.Notify(manager.GetTranslatedMessage(ErrorKey(authorized.Error)), NotificationKind.Error);
        return null;
    }

    public async Task<(IReadOnlyList<RulesetRead> Read, IReadOnlyList<RulesetRepositoryResult> Failed)> ReadAsync(
        IReadOnlyList<RulesetTarget> targets, string accessToken, CancellationToken cancellationToken)
    {
        var read = new List<RulesetRead>();
        var failed = new List<RulesetRepositoryResult>();
        foreach (var target in targets)
        {
            var (repository, queue, branch) = target;
            try
            {
                if (await queue.GetRulesetStateAsync(repository, branch, accessToken, cancellationToken) is not { } state)
                {
                    failed.Add(RulesetRepositoryResult.Failure(repository, Reason("unsupported")));
                    continue;
                }
                // The lines ruleset 1 covers (PRD §8.2), from the App's read of coverage.yml: a file that does not parse
                // gives no lines, as it does for the merge queue (MergeQueueYml).
                var yml = await integrations.For(repository).GetFileContentAsync(repository, branch, CoverageYml.FileName, cancellationToken);
                var versioning = yml is null ? null : VersioningYml.Parse(yml) is { Config: { } config, Errors.Count: 0 } ? config : null;
                // Segment P: a repository that promotes its lines lets the App push the promotion past ruleset 3.
                read.Add(new RulesetRead(target, state, MergeQueueRulesetPlanner.Defaults(state) with { PromotesLines = versioning?.Lines?.PromotesCurrent == true },
                    versioning?.EffectiveLines));
            }
            catch (Exception ex) when (ex is ForgeAccessDeniedException or ForgeRequestException or HttpRequestException)
            {
                failed.Add(RulesetRepositoryResult.Failure(repository, ex.Message));
            }
        }
        return (read, failed);
    }

    public async Task<IReadOnlyList<RulesetPlanned>?> ChooseAsync(IReadOnlyList<RulesetRead> read, CancellationToken cancellationToken,
        bool releaseLinesComing = false)
    {
        // One prompt per repository (owner 2026-11-07, #554): its required checks are its own, picked from the checks seen
        // on its pull requests, so a shared form could not ask them. The prompts are consecutive retry steps; each pass
        // of the conversation replays the answered ones in the same order.
        var planned = new List<RulesetPlanned>();
        var next = manager.GetTranslatedMessage("onboarding.plan");
        foreach (var r in read)
        {
            var repository = r.Target.Repository;
            var form = await manager.GetPersistentObjectAsync(Guid.Parse(PersistentObjectIds.Default.SetUpRulesets), cancellationToken: cancellationToken);
            // The parent makes the RequiredChecks picker query this repository's checks (CustomQueryArgs.Parent), and its
            // "Add new" confirm run against it.
            form.Parent = new PersistentObject { Id = repository.Id, Name = nameof(Repository), ObjectTypeId = Guid.Empty };
            form[nameof(MergeQueueRulesetChoices.RequiredApprovals)].Value = r.Defaults.RequiredApprovals;
            form[nameof(MergeQueueRulesetChoices.AdminsMayMergeByPullRequest)].Value = r.Defaults.AdminsMayMergeByPullRequest ?? false;
            form[nameof(MergeQueueRulesetChoices.RequiredChecks)].Value = r.Defaults.RequiredChecks.ToArray();
            // Ruleset 3 (R2): the lines' checks, from ruleset 3, else the default branch's. Ignored without release lines.
            var lines = r.Lines ?? (releaseLinesComing ? ReleaseLinePattern.Default : null);
            form[nameof(MergeQueueRulesetChoices.LineRequiredChecks)].Value = (r.Defaults.LineRequiredChecks ?? r.Defaults.RequiredChecks).ToArray();
            form[nameof(MergeQueueRulesetChoices.LineRequiredChecks)].ShowedOn = lines is not null ? EShowedOn.PersistentObject : EShowedOn.None;
            manager.Retry.Action(manager.GetTranslatedMessage("rulesets.chooseTitle", repository.FullName), [next], next, form,
                manager.GetTranslatedMessage("rulesets.chooseMessage"), cancellable: true);
            var chosen = manager.Retry.Result!;
            if (chosen.Option == RetryResult.CancelOption)
                return null;

            var answer = chosen.PersistentObject;
            var approvals = Math.Max(0, answer?[nameof(MergeQueueRulesetChoices.RequiredApprovals)].GetValue<int?>() ?? r.Defaults.RequiredApprovals);
            var admins = answer?[nameof(MergeQueueRulesetChoices.AdminsMayMergeByPullRequest)].GetValue<bool?>() ?? r.Defaults.AdminsMayMergeByPullRequest;
            var checks = answer is null ? r.Defaults.RequiredChecks
                : Names(answer[nameof(MergeQueueRulesetChoices.RequiredChecks)].Value);
            // An answer without the field keeps ruleset 3's own checks (or the default branch's, when it does not exist).
            var lineChecks = answer?.TryGetAttribute(nameof(MergeQueueRulesetChoices.LineRequiredChecks), out var lineAnswer) == true
                ? Names(lineAnswer.Value) : r.Defaults.LineRequiredChecks;
            planned.Add(new RulesetPlanned(r.Target, MergeQueueRulesetPlanner.Plan(r.State, r.Defaults with
            {
                RequiredApprovals = approvals,
                AdminsMayMergeByPullRequest = admins,
                RequiredChecks = CheckIds(checks),
                ReleaseLines = lines is not null ? [lines.RefGlob] : null,
                LineRequiredChecks = lineChecks is null ? null : CheckIds(lineChecks),
            })));
        }
        return planned;
    }

    /// <summary>The picked checks' names (a <see cref="ForgeCheck"/> id), never the queue's own check (D62).</summary>
    private static List<string> CheckIds(IEnumerable<string> picked)
        => [.. picked.Select(ForgeCheck.GetId).Where(c => c.Length > 0 && c != CheckGate.QueueCheckName).Distinct(StringComparer.Ordinal)];

    public string Summary(IReadOnlyList<RulesetPlanned> planned)
        => string.Join("\n\n", planned.Select(p =>
            p.Target.Repository.FullName + "\n" + string.Join("\n", Lines(p).Select(l => "- " + l))));

    public string SummaryHtml(IReadOnlyList<RulesetPlanned> planned)
        => HtmlMessage.List(planned.Select(p =>
            HtmlMessage.Encode(p.Target.Repository.FullName) + HtmlMessage.List(Lines(p).Select(HtmlMessage.Encode))));

    /// <summary>
    /// The plan's lines, and for a blocked plan where to fix it by hand: the repository's rules settings on GitHub (#554;
    /// every target is a GitHub repository, <see cref="TargetFor"/>).
    /// </summary>
    private IEnumerable<string> Lines(RulesetPlanned p)
    {
        foreach (var line in MergeQueueRulesetPlanner.Describe(p.Plan))
            yield return line;
        if (p.Plan.Blockers.Count > 0)
            yield return manager.GetTranslatedMessage("rulesets.settingsPage", RulesSettingsUrl(p.Target.Repository));
    }

    /// <summary>GitHub's page for a repository's rulesets.</summary>
    internal static string RulesSettingsUrl(Repository repository)
        => $"https://github.com/{Uri.EscapeDataString(repository.OwnerLogin)}/{Uri.EscapeDataString(repository.Name)}/settings/rules";

    public async Task<(IReadOnlyList<RulesetRepositoryResult> Applied, IReadOnlyList<RulesetRepositoryResult> Failed)> ApplyAsync(
        IReadOnlyList<RulesetPlanned> planned, string accessToken, CancellationToken cancellationToken)
    {
        var applied = new List<RulesetRepositoryResult>();
        var failed = new List<RulesetRepositoryResult>();
        foreach (var ((repository, queue, branch), plan) in planned)
        {
            if (plan.Blockers.Count > 0)
            {
                failed.Add(RulesetRepositoryResult.Failure(repository, string.Join(" ", plan.Blockers)));
                continue;
            }
            if (!plan.CanApply)
                continue; // set up already
            try
            {
                var steps = await queue.ApplyRulesetPlanAsync(repository, branch, plan, accessToken, cancellationToken);
                applied.Add(new RulesetRepositoryResult(repository.Id!, repository.FullName, string.Join(" ", steps.Select(s => s.Description)), [.. steps]));
            }
            catch (Exception ex) when (ex is ForgeAccessDeniedException or ForgeRequestException or HttpRequestException)
            {
                logger.LogWarning(ex, "Set up rulesets: {Repository} was refused", repository.FullName);
                failed.Add(RulesetRepositoryResult.Failure(repository, ex.Message));
            }
        }
        return (applied, failed);
    }

    public void Notify(IReadOnlyList<RulesetRepositoryResult> applied, IReadOnlyList<RulesetRepositoryResult> failed)
    {
        foreach (var done in applied)
            manager.Client.Notify(manager.GetTranslatedMessage("rulesets.applied", done.FullName, done.Message), NotificationKind.Success);
        foreach (var refusal in failed)
            manager.Client.Notify(manager.GetTranslatedMessage("rulesets.failed", refusal.FullName, refusal.Message), NotificationKind.Warning);
    }

    /// <summary>
    /// The ids an array reference answered with: a JSON array of strings from the wire, or the array the form was
    /// prefilled with. <see cref="PersistentObjectAttribute.GetValue{T}"/> reads scalars only.
    /// </summary>
    internal static IReadOnlyList<string> Names(object? value) => value switch
    {
        null => [],
        JsonElement { ValueKind: JsonValueKind.Array } array
            => [.. array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)],
        JsonElement => [],
        string single => [single],
        IEnumerable<object?> items => [.. items.OfType<string>()],
        System.Collections.IEnumerable items => [.. items.OfType<string>()],
        _ => [],
    };

    private static string ErrorKey(string? error) => error switch
    {
        SparkOneShotOAuthErrors.PopupBlocked => "rulesets.error.popupBlocked",
        SparkOneShotOAuthErrors.Denied => "rulesets.error.denied",
        SparkOneShotOAuthErrors.Expired => "rulesets.error.expired",
        _ => "rulesets.error.failed",
    };

    private string Reason(string key) => manager.GetTranslatedMessage($"rulesets.reason.{key}");
}
