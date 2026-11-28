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
using Raven.Client.Documents.Session;
using System.Globalization;
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
    /// The form (approvals, admin bypass, checks, summary job), starting from what the repositories agree on with their open
    /// <see cref="RulesetNotice"/>s applied (#714 Dec6), and each repository's plan with the answer. <see langword="null"/>
    /// when the admin cancelled. Ruleset 1 also covers a repository's release lines
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
    public static RulesetRepositoryResult Failure(Repository repository, string message) => new(repository.Id.Value, repository.FullName, message, []);
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
    [Inject] private readonly IRulesetNotices notices;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly TimeProvider time;

    /// <summary>How long a required check may go unseen before the form marks it (#714 Dec10 B2).</summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromDays(14);

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
                read.Add(new RulesetRead(target, state, MergeQueueRulesetPlanner.Defaults(state), versioning?.EffectiveLines));
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
            // #714 Dec6: open notices fix the starting answers (dead names out, pins re-pinned), and the message says so.
            var prefill = Prefill(r.Defaults, repository.KnownChecks, await notices.ListOpenAsync(repository.Id.Value, cancellationToken));
            var defaults = prefill.Choices;
            var form = await manager.GetPersistentObjectAsync(Guid.Parse(PersistentObjectIds.Default.SetUpRulesets), cancellationToken: cancellationToken);
            // The parent makes the RequiredChecks picker query this repository's checks (CustomQueryArgs.Parent), and its
            // "Add new" confirm run against it.
            form.Parent = new PersistentObject { Id = repository.Id.Value, Name = nameof(Repository), ObjectTypeId = Guid.Empty };
            form[nameof(MergeQueueRulesetChoices.RequiredApprovals)].Value = defaults.RequiredApprovals;
            form[nameof(MergeQueueRulesetChoices.AdminsMayMergeByPullRequest)].Value = defaults.AdminsMayMergeByPullRequest ?? false;
            form[nameof(MergeQueueRulesetChoices.RequiredChecks)].Value = defaults.RequiredChecks.ToArray();
            if (form.TryGetAttribute(nameof(MergeQueueRulesetChoices.SummaryJob), out var summaryField))
            {
                summaryField.Value = defaults.SummaryJob;
                // Dec6: a summary job that never reported is left empty and asked for again.
                summaryField.IsRequired = prefill.LostSummaryJob is not null;
            }
            // Ruleset 3 (R2): the lines' checks, from ruleset 3, else the default branch's. Ignored without release lines.
            var lines = r.Lines ?? (releaseLinesComing ? ReleaseLinePattern.Default : null);
            form[nameof(MergeQueueRulesetChoices.LineRequiredChecks)].Value = (defaults.LineRequiredChecks ?? defaults.RequiredChecks).ToArray();
            form[nameof(MergeQueueRulesetChoices.LineRequiredChecks)].ShowedOn = lines is not null ? EShowedOn.PersistentObject : EShowedOn.None;
            manager.Retry.Action(manager.GetTranslatedMessage("rulesets.chooseTitle", repository.FullName), [next], next, form,
                ChooseMessage(prefill, repository.KnownChecks), cancellable: true);
            var chosen = manager.Retry.Result!;
            if (chosen.Option == RetryResult.CancelOption)
                return null;

            var answer = chosen.PersistentObject;
            var approvals = Math.Max(0, answer?[nameof(MergeQueueRulesetChoices.RequiredApprovals)].GetValue<int?>() ?? defaults.RequiredApprovals);
            var admins = answer?[nameof(MergeQueueRulesetChoices.AdminsMayMergeByPullRequest)].GetValue<bool?>() ?? defaults.AdminsMayMergeByPullRequest;
            var checks = answer is null ? defaults.RequiredChecks
                : Names(answer[nameof(MergeQueueRulesetChoices.RequiredChecks)].Value);
            // An answer without the field keeps ruleset 3's own checks (or the default branch's, when it does not exist).
            var lineChecks = answer?.TryGetAttribute(nameof(MergeQueueRulesetChoices.LineRequiredChecks), out var lineAnswer) == true
                ? Names(lineAnswer.Value) : defaults.LineRequiredChecks;
            // Likewise the summary job; an emptied field means none. The queue's own check is left to the plan's blocker (D62).
            var summaryJob = answer?.TryGetAttribute(nameof(MergeQueueRulesetChoices.SummaryJob), out var summaryAnswer) == true
                ? Names(summaryAnswer.Value).Select(ForgeCheck.GetId).FirstOrDefault(name => name.Length > 0)
                : defaults.SummaryJob;
            planned.Add(new RulesetPlanned(r.Target, MergeQueueRulesetPlanner.Plan(r.State, defaults with
            {
                RequiredApprovals = approvals,
                AdminsMayMergeByPullRequest = admins,
                RequiredChecks = CheckIds(checks),
                ReleaseLines = lines is not null ? [lines.RefGlob] : null,
                LineRequiredChecks = lineChecks is null ? null : CheckIds(lineChecks),
                SummaryJob = summaryJob,
            })));
        }
        return planned;
    }

    /// <summary>
    /// The form's starting answers once the repository's open notices are applied (#714 Dec6), and what the message says
    /// about them.
    /// </summary>
    /// <param name="Removed">Required names a stuck pull request never saw, taken out of the picks.</param>
    /// <param name="LostSummaryJob">The summary job, when it was one of them: the field is left empty and asked for again.</param>
    /// <param name="Repinned">The names a refused merge showed pinned to the wrong App, with the App that reports them now.</param>
    internal sealed record RulesetPrefill(
        MergeQueueRulesetChoices Choices,
        IReadOnlyList<string> Removed,
        string? LostSummaryJob,
        RulesetNotice? Missing,
        IReadOnlyDictionary<string, long> Repinned);

    /// <summary>
    /// <paramref name="defaults"/> with the App of every known check (Dec4), the open notices' fixes applied (Dec6): a pin
    /// mismatch is re-pinned to the App that reports the name now, and a check that never reported is taken out of both
    /// lists. Nothing is guessed: a lost summary job leaves the field empty.
    /// </summary>
    internal static RulesetPrefill Prefill(MergeQueueRulesetChoices defaults, IEnumerable<ForgeCheck> known, IReadOnlyList<RulesetNotice> open)
    {
        var checkApps = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var check in known)
            if (check.AppId is { } app && ForgeCheck.GetId(check.Id) is { Length: > 0 } name)
                checkApps.TryAdd(name, app);

        var repinned = new Dictionary<string, long>(StringComparer.Ordinal);
        if (open.FirstOrDefault(n => n.Kind == ERulesetNoticeKind.PinMismatch) is { } pins)
            foreach (var (name, app) in pins.ReportingApps)
                checkApps[name] = repinned[name] = app;

        var choices = defaults with { CheckApps = checkApps };
        if (open.FirstOrDefault(n => n.Kind == ERulesetNoticeKind.MissingCheck) is not { } missing)
            return new RulesetPrefill(choices, [], null, null, repinned);

        var dead = missing.Checks.ToHashSet(StringComparer.Ordinal);
        var removed = defaults.RequiredChecks.Concat(defaults.LineRequiredChecks ?? []).Append(defaults.SummaryJob ?? string.Empty)
            .Where(dead.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var lost = defaults.SummaryJob is { } job && dead.Contains(job) ? job : null;
        choices = choices with
        {
            RequiredChecks = [.. defaults.RequiredChecks.Where(name => !dead.Contains(name))],
            LineRequiredChecks = defaults.LineRequiredChecks is null ? null : [.. defaults.LineRequiredChecks.Where(name => !dead.Contains(name))],
            SummaryJob = lost is null ? defaults.SummaryJob : null,
        };
        return new RulesetPrefill(choices, removed, lost, missing, repinned);
    }

    /// <summary>
    /// The form's message as HTML: what the open notices changed (Dec6), the offer to switch to a summary job (Dec11), and
    /// the required names no pull request reported lately (Dec10 B2), which are marked and never removed.
    /// </summary>
    private string ChooseMessage(RulesetPrefill prefill, IReadOnlyList<ForgeCheck> known)
    {
        var choices = prefill.Choices;
        List<string> parts = [HtmlMessage.Encode(manager.GetTranslatedMessage("rulesets.chooseMessage"))];
        if (prefill.Missing is { } missing && prefill.Removed.Count > 0)
            parts.Add(HtmlMessage.Encode(manager.GetTranslatedMessage("rulesets.prefill.removed", string.Join(", ", prefill.Removed), missing.PullRequest ?? 0)));
        if (prefill.LostSummaryJob is { } lost)
            parts.Add(HtmlMessage.Encode(manager.GetTranslatedMessage("rulesets.prefill.summaryJobLost", lost, prefill.Missing?.PullRequest ?? 0,
                string.Join(", ", prefill.Missing?.ReportedOnHead ?? []))));
        if (prefill.Repinned.Count > 0)
            parts.Add(HtmlMessage.Encode(manager.GetTranslatedMessage("rulesets.prefill.repinned",
                string.Join(", ", prefill.Repinned.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key} (App {p.Value})")))));
        if (choices.SummaryJob is null && prefill.LostSummaryJob is null && MergeQueueRulesetPlanner.RequiresOneCheckPerJob(choices.RequiredChecks))
            parts.Add(HtmlMessage.Encode(manager.GetTranslatedMessage("rulesets.summaryJobOffer")));

        var since = time.GetUtcNow().UtcDateTime - StaleAfter;
        var stale = choices.RequiredChecks.Concat(choices.LineRequiredChecks ?? []).Append(choices.SummaryJob ?? string.Empty)
            .Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(name => (Name: name, Seen: known.FirstOrDefault(c => c.Id == name)))
            .Where(c => c.Seen is null || c.Seen.LastSeenUtc < since)
            .Select(c => c.Seen is null
                ? manager.GetTranslatedMessage("rulesets.stale.neverSeen", c.Name)
                : manager.GetTranslatedMessage("rulesets.stale.notSeenSince", c.Name, c.Seen.LastSeenUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
            .ToList();
        if (stale.Count > 0)
            parts.Add(HtmlMessage.Section(manager.GetTranslatedMessage("rulesets.stale.heading"), stale));
        return string.Join("<br><br>", parts);
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
                applied.Add(new RulesetRepositoryResult(repository.Id.Value, repository.FullName, string.Join(" ", steps.Select(s => s.Description)), [.. steps]));
                // #714: the managers confirmed the rulesets they want, so what the bell would tell them no longer applies.
                await notices.ResolveAllAsync(repository.Id.Value, cancellationToken);
            }
            catch (Exception ex) when (ex is ForgeAccessDeniedException or ForgeRequestException or HttpRequestException)
            {
                logger.LogWarning(ex, "Set up rulesets: {Repository} was refused", repository.FullName);
                failed.Add(RulesetRepositoryResult.Failure(repository, ex.Message));
            }
        }
        if (applied.Count > 0)
            await session.SaveChangesAsync(cancellationToken);
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
        // A single reference (the summary job) answers with the one id.
        JsonElement { ValueKind: JsonValueKind.String } single => [single.GetString()!],
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
