using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeCoverage.Forge;

/// <summary>One bypass actor of a ruleset, in the forge's words (GitHub: <c>Integration</c>/<c>RepositoryRole</c>, <c>exempt</c>/<c>always</c>/<c>pull_request</c>).</summary>
public sealed record ForgeRulesetActor(long? ActorId, string ActorType, string BypassMode);

/// <summary>One rule of a ruleset: its type and its parameters as the forge holds them (null when it takes none).</summary>
public sealed record ForgeRulesetRule(string Type, JsonObject? Parameters = null);

/// <summary>What a ruleset says, without its id: what the planner compares and what an apply sends.</summary>
/// <param name="Include">The branch patterns it targets (<c>~DEFAULT_BRANCH</c>, <c>refs/heads/release/*</c>).</param>
public sealed record ForgeRulesetDefinition(
    string Name,
    string Target,
    string Enforcement,
    IReadOnlyList<string> Include,
    IReadOnlyList<string> Exclude,
    IReadOnlyList<ForgeRulesetRule> Rules,
    IReadOnlyList<ForgeRulesetActor> BypassActors);

/// <summary>A repository ruleset that exists.</summary>
public sealed record ForgeExistingRuleset(long Id, ForgeRulesetDefinition Definition);

/// <summary>
/// Classic branch protection on the branch, as far as #554's plan needs it. Read with the Administration permission, which
/// only the ruleset step's own token holds (#554 e0).
/// </summary>
/// <param name="HasPushRestrictions">Pushes are limited to named people, teams or Apps: no ruleset equivalent, so the plan stops.</param>
public sealed record ForgeClassicProtection(
    bool LockBranch,
    int RequiredApprovals,
    bool DismissStaleReviews,
    bool RequireLastPushApproval,
    bool RequireCodeOwnerReviews,
    bool RequireConversationResolution,
    IReadOnlyList<string> RequiredChecks,
    bool RequiredLinearHistory,
    bool RequiredSignatures,
    bool HasPushRestrictions);

/// <summary>What the forge holds for the merge queue's branch now (#554).</summary>
/// <param name="AppId">The App's own id: the bypass actor of ruleset 1.</param>
/// <param name="Rulesets">The repository's own rulesets (not an organization's), every one, whatever it targets.</param>
/// <param name="Classic">Classic protection on the default branch; null when there is none.</param>
/// <param name="RulesetsAvailable">False when the repository's plan has no rulesets (a private repository on GitHub Free, D15).</param>
public sealed record ForgeRulesetState(long AppId, IReadOnlyList<ForgeExistingRuleset> Rulesets, ForgeClassicProtection? Classic,
    bool RulesetsAvailable = true);

/// <summary>One step an apply carried out (#554): what it did, and where to see the result on the forge.</summary>
public sealed record ForgeRulesetApplied(string Description, string? Url);

/// <summary>What the person chose in the plan's prompt (#554 e3).</summary>
/// <param name="RequiredApprovals">Ruleset 2's approval count: 0 for a single maintainer (merge-queue.md §3).</param>
/// <param name="RequiredChecks">Ruleset 2's required checks, as confirmed. Never the queue's own check.</param>
/// <param name="AdminsMayMergeByPullRequest">The Repository admin role as ruleset 1's <c>pull_request</c> bypass (O21 B); null keeps what ruleset 1 has, off when it does not exist.</param>
/// <param name="ReleaseLines">
/// The release lines' ref globs (<c>refs/heads/release/*-*.x</c>), which ruleset 1 targets beside the default branch
/// (PRD M10e, §8.2 "the lines get the same restriction", owner 2026-11-06: "The release branches should probably be
/// protected too"), and the lines' own checks ruleset targets. Ruleset 2 stays on the default branch.
/// </param>
/// <param name="LineRequiredChecks">
/// The release lines' required checks (ruleset 3), chosen apart from the default branch's: a line runs the workflows of
/// its own tree, so an older line may never report a check master added later (docs/release_lines_recovery_plan.md R2,
/// owner 2026-11-11). Null keeps ruleset 3's own checks, or takes <paramref name="RequiredChecks"/> when it does not
/// exist; empty creates no ruleset 3 and leaves an existing one as it is.
/// </param>
/// <param name="SummaryJob">
/// The one CI job that waits for every other job (#714 Dec3; Spark's <c>pull-request</c>). With it, rulesets 2 and 3
/// require the App's own checks among the picks (<see cref="MergeQueueRulesetPlanner.AppCheckNames"/>) and this job,
/// nothing else, so renaming, joining or splitting another job never touches them. Null keeps the picks as they are.
/// </param>
/// <param name="CheckApps">
/// The App each check name is reported by (#714 Dec4: <see cref="Entities.ForgeCheck.AppId"/>, or the App a refused merge
/// named): a required check is pinned to it (<c>integration_id</c>), so only that App's run satisfies it. The App's own
/// checks are always pinned to the App; a name without an entry keeps the pin the forge holds.
/// </param>
public sealed record MergeQueueRulesetChoices(
    int RequiredApprovals,
    IReadOnlyList<string> RequiredChecks,
    bool? AdminsMayMergeByPullRequest = null,
    IReadOnlyList<string>? ReleaseLines = null,
    IReadOnlyList<string>? LineRequiredChecks = null,
    string? SummaryJob = null,
    IReadOnlyDictionary<string, long>? CheckApps = null);

/// <summary>One change the plan makes, in the order it must be made.</summary>
public abstract record MergeQueueRulesetStep;

public sealed record CreateRulesetStep(ForgeRulesetDefinition Definition) : MergeQueueRulesetStep;

/// <param name="Changes">What differs, one line each, for the plan shown before anything changes.</param>
public sealed record UpdateRulesetStep(long Id, ForgeRulesetDefinition Definition, IReadOnlyList<string> Changes) : MergeQueueRulesetStep;

/// <param name="Notes">Where each classic setting went, or that it was dropped, one line each.</param>
public sealed record DeleteClassicProtectionStep(IReadOnlyList<string> Notes) : MergeQueueRulesetStep;

/// <summary>The plan: steps in order, or blockers that stop it. Neither means "already configured".</summary>
public sealed record MergeQueueRulesetPlan(IReadOnlyList<MergeQueueRulesetStep> Steps, IReadOnlyList<string> Blockers)
{
    public bool IsConfigured => Steps.Count == 0 && Blockers.Count == 0;
    public bool CanApply => Steps.Count > 0 && Blockers.Count == 0;
}

/// <summary>
/// #554: the two rulesets of merge-queue.md §3 and the classic protection they replace, as a pure plan (no forge call).
/// </summary>
/// <remarks>
/// <para>
/// <b>Ruleset 1</b> ("merge-queue: restrict updates") holds exactly <c>update</c> (no fetch-and-merge), <c>deletion</c> and
/// <c>non_fast_forward</c>, with the App <c>exempt</c> (D16: the UI's "Always allow" stores <c>always</c>, which refused
/// the merge on ng-bootstrap #451) and, when chosen, the Repository admin role for pull requests only. Other Apps already
/// <c>exempt</c> on it are kept (another environment's CodeCoverage App). Anything else on it is removed: an App exempt
/// from a review or check rule would be a bypass (O22).
/// </para>
/// <para>
/// <b>Ruleset 2</b> ("merge-queue: reviews and checks") holds the <c>pull_request</c> and <c>required_status_checks</c>
/// rules. Its other rules and its people bypass actors are kept; only the App is removed from them (O22). Parameters the
/// plan does not manage (GitHub's <c>allowed_merge_methods</c>, a check's <c>integration_id</c>) are kept as they are.
/// </para>
/// <para>
/// <b>Ruleset 3</b> ("merge-queue: release-line checks") holds only <c>required_status_checks</c>, on the release lines,
/// with checks chosen apart from ruleset 2's (R2, owner 2026-11-11). No <c>pull_request</c> rule: only the App updates a
/// line (ruleset 1), through the queue, whose approval floor applies. It exists only when the repository has release
/// lines; its other rules and people bypass actors are kept, as on ruleset 2.
/// </para>
/// <para>
/// <b>Ruleset 4</b> ("merge-queue: backport branches", also only with release lines) restricts <c>creation</c>,
/// <c>update</c> and <c>deletion</c> of <c>refs/heads/backport/*</c>, with the App <c>exempt</c> and the Repository admin
/// role <c>always</c> (segment D, Q13). The queue reads it back through the branch's rules before it lets the App's
/// backport merge without an approval (<c>MergeQueueEngine.BackportBranchProtectedAsync</c>).
/// </para>
/// <para>
/// <b>Classic protection is moved, then deleted</b> (owner, 2026-11-06): the steps run ruleset 1, ruleset 2, then the
/// deletion, so the branch is never open in between.
/// </para>
/// </remarks>
public static class MergeQueueRulesetPlanner
{
    public const string RestrictUpdatesName = "merge-queue: restrict updates";
    public const string ReviewsAndChecksName = "merge-queue: reviews and checks";
    public const string LineChecksName = "merge-queue: release-line checks";
    public const string BackportBranchesName = "merge-queue: backport branches";

    /// <summary>
    /// The App's own check on release lines (segment E, Q1b): a current line takes only the App's backports. Ruleset 3
    /// always requires it, bound to the App (<c>integration_id</c>), so nobody else can report it green.
    /// </summary>
    public const string LinePolicyCheckName = "Line policy";

    /// <summary>
    /// The checks the App reports itself that a ruleset may require (#714 Dec3): the coverage checks and the line policy. A
    /// job rename never touches them. The queue's own check is not one (D62), and "PR size" and "Backport targets" only inform.
    /// </summary>
    public static readonly IReadOnlyList<string> AppCheckNames = ["coverage/patch", "coverage/project", LinePolicyCheckName];

    /// <summary>
    /// Rules that require more than one check the App does not report itself: one per CI job, which a job rename leaves
    /// waiting for a name that never reports. Offered the switch to one summary job (#714 Dec11).
    /// </summary>
    public static bool RequiresOneCheckPerJob(IEnumerable<string> requiredChecks)
        => requiredChecks.Distinct(StringComparer.Ordinal).Count(name => !AppCheckNames.Contains(name, StringComparer.Ordinal)) > 1;

    /// <summary>The branches the App's backports are pushed to (<c>backport/&lt;pr&gt;-&lt;line&gt;</c>).</summary>
    public const string BackportBranchesPattern = "refs/heads/backport/*";

    /// <summary>What ruleset 4 restricts: a backport branch's content is the App's pick or an admin's resolution (Q13).</summary>
    public static readonly IReadOnlyList<string> BackportBranchRules = ["creation", "update", "deletion"];

    /// <summary>GitHub's id of the Repository admin role as a bypass actor (merge-queue.md §3).</summary>
    public const long RepositoryAdminRoleId = 5;

    private const string DefaultBranch = "~DEFAULT_BRANCH";

    public static MergeQueueRulesetPlan Plan(ForgeRulesetState state, MergeQueueRulesetChoices choices)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(choices);

        var blockers = new List<string>();
        var steps = new List<MergeQueueRulesetStep>();
        if (!state.RulesetsAvailable)
            return new MergeQueueRulesetPlan(steps,
                ["This repository's plan has no rulesets (a private repository on GitHub Free). Make it public or upgrade the plan first."]);
        List<string> include = [DefaultBranch];
        List<string> lines = [.. (choices.ReleaseLines ?? []).Where(l => l != DefaultBranch).Distinct(StringComparer.Ordinal)];
        List<string> restricted = [DefaultBranch, .. lines];

        if (choices.RequiredApprovals is < 0 or > 10)
            blockers.Add($"The approval count must be between 0 and 10, not {choices.RequiredApprovals}.");
        // #714, S2 on the sandbox (2026-11-28): the picker offers the App's own checks too. One of them as the summary job
        // would leave no CI job required, and the next setup would see no summary job at all (SummaryJobOf).
        if (choices.SummaryJob?.Trim() is { Length: > 0 } job
            && (AppCheckNames.Contains(job, StringComparer.Ordinal)
                || (choices.CheckApps?.TryGetValue(job, out var jobApp) == true && jobApp == state.AppId)))
            blockers.Add($"\"{job}\" is reported by the App itself, not by a CI job, so it cannot be the summary job. Pick the workflow job that waits for the others.");
        var checks = Checks(SummaryJobShape(choices.RequiredChecks, choices.SummaryJob), blockers);
        var existing3 = Single(state, LineChecksName, blockers);
        var lineChecks = lines.Count == 0 ? []
            : Checks(SummaryJobShape(choices.LineRequiredChecks ?? (existing3 is null ? choices.RequiredChecks : RequiredCheckNames(existing3.Definition)),
                choices.SummaryJob), blockers);
        // Segment E: the line policy is always required on the lines, whatever else the admin picks.
        lineChecks.RemoveAll(c => c == LinePolicyCheckName);
        if (state.Classic?.HasPushRestrictions == true)
            blockers.Add("Classic protection limits pushes to named people, teams or Apps. Rulesets have no such setting, so remove the restriction by hand first.");

        var existing1 = Single(state, RestrictUpdatesName, blockers);
        var existing2 = Single(state, ReviewsAndChecksName, blockers);

        var desired1 = RestrictUpdates(state, choices, restricted, existing1);
        var desired2 = ReviewsAndChecks(state, choices, checks, include, existing2);
        AddStep(steps, existing1, desired1);
        AddStep(steps, existing2, desired2);
        if (lines.Count > 0)
        {
            AddStep(steps, existing3, LineChecks(state, choices.CheckApps, lineChecks, lines, existing3));
            var existing4 = Single(state, BackportBranchesName, blockers);
            AddStep(steps, existing4, BackportBranches(state, existing4));
        }

        if (state.Classic is { } classic)
            steps.Add(new DeleteClassicProtectionStep(ClassicNotes(classic, choices.RequiredApprovals, checks)));

        return new MergeQueueRulesetPlan(steps, blockers);
    }

    /// <summary>
    /// The starting answers for one repository: ruleset 2's own settings when it exists, else classic protection's, else
    /// no required checks and no approval. Never the queue's own check (D62). (From the abandoned e3.)
    /// </summary>
    /// <remarks>
    /// No approval when nothing says otherwise (the Tools onboarding, 2026-11-12): with 1, a solo maintainer can never
    /// merge, since GitHub never counts the author's own approval (merge-queue.md §3), and Tools' ruleset 27099667 was
    /// created that way. The queue's approval floor still asks an approval of authors without write. A team that wants
    /// GitHub to require reviews raises it in the form.
    /// </remarks>
    /// <remarks>
    /// No guess from the default branch's head (#554, 2026-11-07): it carried the checks of jobs that run on a push
    /// alone (a deploy), which never report on a pull request, so ruleset 26719109 required checks no pull request could
    /// pass. The admin now picks them per repository from the checks seen on its pull requests.
    /// </remarks>
    public static MergeQueueRulesetChoices Defaults(ForgeRulesetState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var ruleset1 = state.Rulesets.FirstOrDefault(r => r.Definition.Name == RestrictUpdatesName)?.Definition;
        var ruleset2 = state.Rulesets.FirstOrDefault(r => r.Definition.Name == ReviewsAndChecksName)?.Definition;

        var approvals = ruleset2?.Rules.FirstOrDefault(r => r.Type == "pull_request")?.Parameters?["required_approving_review_count"] is JsonValue count
            && count.TryGetValue<int>(out var n) ? n : state.Classic?.RequiredApprovals ?? 0;
        var ruleset2Checks = (ruleset2?.Rules.FirstOrDefault(r => r.Type == "required_status_checks")?.Parameters?["required_status_checks"] as JsonArray ?? [])
            .OfType<JsonObject>().Select(c => c["context"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList();
        IEnumerable<string> checks = ruleset2Checks.Count > 0 ? ruleset2Checks
            : state.Classic?.RequiredChecks ?? [];
        // Ruleset 3's own checks; without it, null, so the form starts from ruleset 2's (a current line runs master's tree).
        var ruleset3 = state.Rulesets.FirstOrDefault(r => r.Definition.Name == LineChecksName)?.Definition;
        var required = Clean(checks);
        var lineRequired = ruleset3 is null ? null : Clean(RequiredCheckNames(ruleset3));
        // The summary-job shape only when both rulesets are in it already: with a job set, ruleset 3 keeps nothing else, so
        // a line that requires more would lose those checks to a guess.
        var summaryJob = SummaryJobOf(required) is { } job
            && (lineRequired ?? []).All(name => name == job || AppCheckNames.Contains(name, StringComparer.Ordinal)) ? job : null;
        return new MergeQueueRulesetChoices(approvals, required,
            ruleset1?.BypassActors.Any(IsAdminByPullRequest) ?? false,
            LineRequiredChecks: lineRequired,
            SummaryJob: summaryJob);
    }

    /// <summary>
    /// The summary job a required list already has (#714 Dec3): its one name the App does not report itself. Null for a list
    /// with none, or with one per CI job (<see cref="RequiresOneCheckPerJob"/>), which the setup offers to switch.
    /// </summary>
    public static string? SummaryJobOf(IEnumerable<string> requiredChecks)
    {
        var jobs = requiredChecks.Where(name => !string.IsNullOrWhiteSpace(name) && !AppCheckNames.Contains(name, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal).Take(2).ToList();
        return jobs.Count == 1 ? jobs[0] : null;
    }

    /// <summary>
    /// What a ruleset requires with a summary job (#714 Dec3): the App's own checks among <paramref name="picked"/>, and the
    /// job. Without one, the picks as they are.
    /// </summary>
    internal static IEnumerable<string> SummaryJobShape(IEnumerable<string> picked, string? summaryJob)
        => string.IsNullOrWhiteSpace(summaryJob) ? picked
            : [.. picked.Where(name => AppCheckNames.Contains(name, StringComparer.Ordinal)), summaryJob.Trim()];

    /// <summary>The checks the form offers: never the queue's own (D62), nor the line policy, which the plan always adds.</summary>
    private static List<string> Clean(IEnumerable<string> checks)
        => [.. checks.Where(c => !string.IsNullOrWhiteSpace(c) && c is not (CheckGate.QueueCheckName or LinePolicyCheckName))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>The non-blank, distinct checks; the queue's own check is a blocker (D62), named once however many lists hold it.</summary>
    private static List<string> Checks(IEnumerable<string> chosen, List<string> blockers)
    {
        var checks = chosen.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.Ordinal).ToList();
        var blocker = $"\"{CheckGate.QueueCheckName}\" cannot be a required check: it is still running while the queue merges, so every merge would be refused (D62).";
        if (checks.Contains(CheckGate.QueueCheckName, StringComparer.Ordinal) && !blockers.Contains(blocker))
            blockers.Add(blocker);
        return checks;
    }

    /// <summary>The plan as the confirmation shows it, one line per step or blocker. (From the abandoned e3.)</summary>
    public static IReadOnlyList<string> Describe(MergeQueueRulesetPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Blockers.Count > 0)
            return [.. plan.Blockers.Select(b => $"Blocked: {b}")];
        if (plan.IsConfigured)
            return ["The rulesets are set up already."];
        return [.. plan.Steps.Select(step => step switch
        {
            CreateRulesetStep create => $"Create ruleset \"{create.Definition.Name}\" on {Patterns(create.Definition)}: "
                + string.Join(", ", create.Definition.Rules.Select(r => r.Type)) + "."
                + (RequiredCheckNames(create.Definition) is { Count: > 0 } checks ? $" Required checks: {string.Join(", ", checks)}." : ""),
            UpdateRulesetStep update => $"Update ruleset \"{update.Definition.Name}\": {string.Join("; ", update.Changes)}.",
            DeleteClassicProtectionStep delete => "Delete the classic branch protection: " + string.Join(" ", delete.Notes),
            _ => step.ToString()!,
        })];
    }

    /// <summary>The contexts of a definition's <c>required_status_checks</c> rule, in its order (#554: the summary names them).</summary>
    private static List<string> RequiredCheckNames(ForgeRulesetDefinition definition)
        => [.. (definition.Rules.FirstOrDefault(r => r.Type == "required_status_checks")?.Parameters?["required_status_checks"] as JsonArray ?? [])
            .OfType<JsonObject>().Select(c => c["context"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>()];

    private static ForgeExistingRuleset? Single(ForgeRulesetState state, string name, List<string> blockers)
    {
        var named = state.Rulesets.Where(r => r.Definition.Name == name).ToList();
        if (named.Count > 1)
            blockers.Add($"{named.Count} rulesets are named \"{name}\". Delete all but one, then run this again.");
        return named.Count == 1 ? named[0] : null;
    }

    private static ForgeRulesetDefinition RestrictUpdates(ForgeRulesetState state, MergeQueueRulesetChoices choices, List<string> include,
        ForgeExistingRuleset? existing)
    {
        var admins = choices.AdminsMayMergeByPullRequest
            ?? existing?.Definition.BypassActors.Any(IsAdminByPullRequest) ?? false;
        List<ForgeRulesetActor> actors = [new(state.AppId, "Integration", "exempt")];
        // Another App exempt here is kept: a repository can have more than one CodeCoverage App installed (development and
        // production), and each environment's setup removed the other's, so the other App's pushes were refused (production
        // planned "bypass removed: App 4567511 (exempt)" on PieterjanDeClippel/SparkTest, 2026-11-06). An App that is
        // uninstalled stays exempt until it is removed on GitHub by hand (owner, 2026-11-06: option 2).
        actors.AddRange((existing?.Definition.BypassActors ?? [])
            .Where(a => a is { ActorType: "Integration", BypassMode: "exempt" } && a.ActorId != state.AppId));
        if (admins) actors.Add(new(RepositoryAdminRoleId, "RepositoryRole", "pull_request"));

        return new ForgeRulesetDefinition(RestrictUpdatesName, "branch", "active", include, [],
        [
            // No parameters: update_allows_fetch_and_merge=false is GitHub's default, and GitHub stores and returns the rule
            // without it ({"type":"update"}, read live from PieterjanDeClippel/SparkTest on 2026-11-06). Sending it made
            // every later run "update" a ruleset that was already right. A ruleset that allows it (true) still differs.
            new("update"),
            new("deletion"),
            new("non_fast_forward"),
        ], actors);
    }

    private static bool IsAdminByPullRequest(ForgeRulesetActor actor)
        => actor is { ActorType: "RepositoryRole", ActorId: RepositoryAdminRoleId, BypassMode: "pull_request" };

    private static ForgeRulesetDefinition ReviewsAndChecks(ForgeRulesetState state, MergeQueueRulesetChoices choices, List<string> checks,
        List<string> include, ForgeExistingRuleset? existing)
    {
        var approvals = choices.RequiredApprovals;
        var classic = state.Classic;
        var kept = existing?.Definition.Rules ?? [];
        var rules = new List<ForgeRulesetRule>();

        var pullRequest = Clone(kept.FirstOrDefault(r => r.Type == "pull_request")?.Parameters);
        pullRequest["required_approving_review_count"] = approvals;
        pullRequest["dismiss_stale_reviews_on_push"] = false;
        pullRequest["require_last_push_approval"] = false;
        pullRequest["require_code_owner_review"] = Flag(pullRequest, "require_code_owner_review") || classic?.RequireCodeOwnerReviews == true;
        pullRequest["required_review_thread_resolution"] = Flag(pullRequest, "required_review_thread_resolution") || classic?.RequireConversationResolution == true;
        rules.Add(new("pull_request", pullRequest));

        if (checks.Count > 0)
            rules.Add(StatusChecks(kept, checks, state.AppId, choices.CheckApps));

        if (classic?.RequiredLinearHistory == true && kept.All(r => r.Type != "required_linear_history"))
            rules.Add(new("required_linear_history"));
        if (classic?.RequiredSignatures == true && kept.All(r => r.Type != "required_signatures"))
            rules.Add(new("required_signatures"));
        rules.AddRange(kept.Where(r => r.Type is not ("pull_request" or "required_status_checks")));

        var actors = (existing?.Definition.BypassActors ?? [])
            .Where(a => !(a.ActorType == "Integration" && a.ActorId == state.AppId))
            .ToList();
        return new ForgeRulesetDefinition(ReviewsAndChecksName, "branch", "active", include, [], rules, actors);
    }

    /// <summary>
    /// Ruleset 3: the lines' required checks and nothing the plan does not manage removed, with the App <c>exempt</c>
    /// (lockfile-notice-and-line-cut PRD D2, owner 2026-11-12). The App creates the lines (a creation must pass the
    /// required checks, which a commit never carries for a branch that does not exist yet: SpaServices' cut answered 422)
    /// and pushes promotions; its other line changes go through the queue, which waits for these checks itself.
    /// </summary>
    private static ForgeRulesetDefinition LineChecks(ForgeRulesetState state, IReadOnlyDictionary<string, long>? checkApps, List<string> checks,
        List<string> lines, ForgeExistingRuleset? existing)
    {
        var kept = existing?.Definition.Rules ?? [];
        // The line policy is one of the App's own checks, so it is bound to this App: another App's green must not pass it.
        var statusChecks = StatusChecks(kept, [.. checks, LinePolicyCheckName], state.AppId, checkApps);
        List<ForgeRulesetRule> rules = [statusChecks, .. kept.Where(r => r.Type != "required_status_checks")];
        var actors = (existing?.Definition.BypassActors ?? [])
            .Where(a => !(a.ActorType == "Integration" && a.ActorId == state.AppId))
            .ToList();
        actors.Insert(0, new(state.AppId, "Integration", "exempt"));
        return new ForgeRulesetDefinition(LineChecksName, "branch", "active", lines, [], rules, actors);
    }

    /// <summary>
    /// Ruleset 4 (segment D, Q13): only the App and repository admins create, update or delete a <c>backport/*</c> branch,
    /// so the App's backports need no approval in the queue. Other Apps already exempt are kept, as on ruleset 1; its other
    /// rules are kept, its other people bypass actors are not (they could push what then merges unreviewed).
    /// </summary>
    private static ForgeRulesetDefinition BackportBranches(ForgeRulesetState state, ForgeExistingRuleset? existing)
    {
        var kept = existing?.Definition.Rules ?? [];
        List<ForgeRulesetRule> rules = [.. BackportBranchRules.Select(type => new ForgeRulesetRule(type)), .. kept.Where(r => !BackportBranchRules.Contains(r.Type))];
        List<ForgeRulesetActor> actors = [new(state.AppId, "Integration", "exempt")];
        actors.AddRange((existing?.Definition.BypassActors ?? [])
            .Where(a => a is { ActorType: "Integration", BypassMode: "exempt" } && a.ActorId != state.AppId));
        actors.Add(new(RepositoryAdminRoleId, "RepositoryRole", "always"));
        return new ForgeRulesetDefinition(BackportBranchesName, "branch", "active", [BackportBranchesPattern], [], rules, actors);
    }

    /// <summary>
    /// A <c>required_status_checks</c> rule with these checks, strict, keeping the rule's other parameters and what the forge
    /// holds for a check already required. Each check is pinned (#714 Dec4): the App's own checks to the App, a name in
    /// <paramref name="checkApps"/> to the App that reports it; any other keeps the pin it has, or none.
    /// </summary>
    private static ForgeRulesetRule StatusChecks(IReadOnlyList<ForgeRulesetRule> kept, List<string> checks, long appId,
        IReadOnlyDictionary<string, long>? checkApps)
    {
        var statusChecks = Clone(kept.FirstOrDefault(r => r.Type == "required_status_checks")?.Parameters);
        var byContext = (statusChecks["required_status_checks"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(c => c["context"] is JsonValue)
            .GroupBy(c => c["context"]!.GetValue<string>(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        statusChecks["strict_required_status_checks_policy"] = true;
        statusChecks["required_status_checks"] = new JsonArray([.. checks.Order(StringComparer.Ordinal).Select(c =>
        {
            var check = byContext.TryGetValue(c, out var known) ? (JsonObject)known.DeepClone() : new JsonObject { ["context"] = c };
            long? pin = AppCheckNames.Contains(c, StringComparer.Ordinal) ? appId
                : checkApps?.TryGetValue(c, out var reporter) == true ? reporter
                : null;
            // Written only when it differs, so the pin GitHub returns (a number read from its JSON) stays as it was.
            if (pin is { } app && PinOf(check) != app)
                check["integration_id"] = app;
            return (JsonNode)check;
        })]);
        return new("required_status_checks", statusChecks);
    }

    /// <summary>The App a required check is pinned to (<c>integration_id</c>); null for any source.</summary>
    private static long? PinOf(JsonObject check)
        => check["integration_id"] is JsonValue id && id.GetValueKind() == JsonValueKind.Number
            && long.TryParse(id.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var app) ? app : null;

    private static void AddStep(List<MergeQueueRulesetStep> steps, ForgeExistingRuleset? existing, ForgeRulesetDefinition desired)
    {
        if (existing is null)
        {
            steps.Add(new CreateRulesetStep(desired));
            return;
        }
        var changes = Differences(existing.Definition, desired);
        if (changes.Count > 0)
            steps.Add(new UpdateRulesetStep(existing.Id, desired, changes));
    }

    /// <summary>What an update changes, one line each; empty when the ruleset already says what it should.</summary>
    internal static IReadOnlyList<string> Differences(ForgeRulesetDefinition actual, ForgeRulesetDefinition desired)
    {
        var changes = new List<string>();
        if (actual.Target != desired.Target)
            changes.Add($"target: {actual.Target} → {desired.Target}");
        if (actual.Enforcement != desired.Enforcement)
            changes.Add($"enforcement: {actual.Enforcement} → {desired.Enforcement}");
        if (!SameSet(actual.Include, desired.Include) || !SameSet(actual.Exclude, desired.Exclude))
            changes.Add($"branches: {Patterns(actual)} → {Patterns(desired)}");

        foreach (var rule in desired.Rules)
        {
            var current = actual.Rules.FirstOrDefault(r => r.Type == rule.Type);
            if (current is null) changes.Add($"rule added: {rule.Type}");
            else if (!JsonNode.DeepEquals(current.Parameters ?? [], rule.Parameters ?? []))
            {
                // #714 Dec10 B1: the checks a ruleset adds, drops and re-pins, which is what makes a fix safe to confirm.
                var checkChanges = rule.Type == "required_status_checks" ? CheckChanges(current, rule) : [];
                changes.AddRange(checkChanges.Count > 0 ? checkChanges : [$"rule changed: {rule.Type}"]);
            }
        }
        foreach (var rule in actual.Rules.Where(r => desired.Rules.All(d => d.Type != r.Type)))
            changes.Add($"rule removed: {rule.Type}");

        foreach (var actor in desired.BypassActors.Where(a => !actual.BypassActors.Contains(a)))
        {
            var before = actual.BypassActors.FirstOrDefault(a => a.ActorType == actor.ActorType && a.ActorId == actor.ActorId);
            changes.Add(before is null
                ? $"bypass added: {Describe(actor)}"
                : $"bypass changed: {Describe(before)} → {actor.BypassMode}");
        }
        foreach (var actor in actual.BypassActors.Where(a => desired.BypassActors.All(d => d.ActorType != a.ActorType || d.ActorId != a.ActorId)))
            changes.Add($"bypass removed: {Describe(actor)}");
        return changes;
    }

    /// <summary>
    /// The required checks a <c>required_status_checks</c> rule adds, drops and re-pins, one line each; empty when only its
    /// other parameters differ.
    /// </summary>
    private static List<string> CheckChanges(ForgeRulesetRule actual, ForgeRulesetRule desired)
    {
        var before = Pins(actual);
        var after = Pins(desired);
        var changes = new List<string>();
        var added = after.Keys.Where(name => !before.ContainsKey(name)).Order(StringComparer.Ordinal).ToList();
        var dropped = before.Keys.Where(name => !after.ContainsKey(name)).Order(StringComparer.Ordinal).ToList();
        if (added.Count > 0)
            changes.Add($"required checks added: {string.Join(", ", added)}");
        if (dropped.Count > 0)
            changes.Add($"required checks dropped: {string.Join(", ", dropped)}");
        foreach (var name in after.Keys.Where(before.ContainsKey).Order(StringComparer.Ordinal))
            if (before[name] != after[name])
                changes.Add($"\"{name}\" pinned: {Source(before[name])} → {Source(after[name])}");
        return changes;

        static string Source(long? app) => app is { } id ? $"App {id}" : "any source";
    }

    /// <summary>A <c>required_status_checks</c> rule's contexts and the App each is pinned to (null: any source).</summary>
    private static Dictionary<string, long?> Pins(ForgeRulesetRule rule)
    {
        var pins = new Dictionary<string, long?>(StringComparer.Ordinal);
        foreach (var check in (rule.Parameters?["required_status_checks"] as JsonArray ?? []).OfType<JsonObject>())
            if (check["context"] is JsonValue context && context.TryGetValue<string>(out var name))
                pins.TryAdd(name, PinOf(check));
        return pins;
    }

    private static List<string> ClassicNotes(ForgeClassicProtection classic, int approvals, List<string> checks)
    {
        var notes = new List<string>();
        if (classic.LockBranch)
            notes.Add($"Lock branch: replaced by \"{RestrictUpdatesName}\", which exempts the App (a lock exempts nobody).");
        notes.Add($"Required approvals: {classic.RequiredApprovals} → {approvals}, in \"{ReviewsAndChecksName}\".");
        var moved = classic.RequiredChecks.Where(c => checks.Contains(c, StringComparer.Ordinal)).ToList();
        var dropped = classic.RequiredChecks.Where(c => !checks.Contains(c, StringComparer.Ordinal)).ToList();
        if (moved.Count > 0) notes.Add($"Required checks moved to \"{ReviewsAndChecksName}\": {string.Join(", ", moved)}.");
        if (dropped.Count > 0) notes.Add($"Required checks dropped: {string.Join(", ", dropped)}.");
        if (classic.DismissStaleReviews) notes.Add("Dismiss stale approvals: dropped, the queue's rebase would dismiss every approval (merge-queue.md §3.1).");
        if (classic.RequireLastPushApproval) notes.Add("Approval of the most recent push: dropped, the queue's push would need another approval (merge-queue.md §3.1).");
        if (classic.RequireCodeOwnerReviews) notes.Add($"Code owner review: moved to \"{ReviewsAndChecksName}\".");
        if (classic.RequireConversationResolution) notes.Add($"Conversation resolution: moved to \"{ReviewsAndChecksName}\".");
        if (classic.RequiredLinearHistory) notes.Add($"Linear history: moved to \"{ReviewsAndChecksName}\".");
        if (classic.RequiredSignatures) notes.Add($"Signed commits: moved to \"{ReviewsAndChecksName}\".");
        notes.Add($"Force pushes and deletion: blocked by \"{RestrictUpdatesName}\".");
        return notes;
    }

    private static JsonObject Clone(JsonObject? parameters) => parameters?.DeepClone() as JsonObject ?? [];

    private static bool Flag(JsonObject parameters, string name)
        => parameters[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static bool SameSet(IReadOnlyList<string> a, IReadOnlyList<string> b)
        => a.ToHashSet(StringComparer.Ordinal).SetEquals(b);

    private static string Patterns(ForgeRulesetDefinition definition)
        => string.Join(", ", definition.Include) + (definition.Exclude.Count > 0 ? $" (except {string.Join(", ", definition.Exclude)})" : "");

    private static string Describe(ForgeRulesetActor actor) => actor switch
    {
        { ActorType: "Integration" } => $"App {actor.ActorId} ({actor.BypassMode})",
        { ActorType: "RepositoryRole", ActorId: RepositoryAdminRoleId } => $"Repository admin role ({actor.BypassMode})",
        _ => $"{actor.ActorType} {actor.ActorId} ({actor.BypassMode})",
    };
}
