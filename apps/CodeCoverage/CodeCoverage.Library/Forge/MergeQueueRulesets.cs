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
/// The release lines' ref globs (<c>refs/heads/release/*-*.x</c>), which ruleset 1 targets beside the default branch.
/// Ruleset 2 stays on the default branch: its checks are the default head's (PRD M10e, §8.2 "the lines get the same
/// restriction", owner 2026-11-06: "The release branches should probably be protected too").
/// </param>
public sealed record MergeQueueRulesetChoices(
    int RequiredApprovals,
    IReadOnlyList<string> RequiredChecks,
    bool? AdminsMayMergeByPullRequest = null,
    IReadOnlyList<string>? ReleaseLines = null);

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
/// the merge on ng-bootstrap #451) and, when chosen, the Repository admin role for pull requests only. Anything else on it
/// is removed: an App exempt from a review or check rule would be a bypass (O22).
/// </para>
/// <para>
/// <b>Ruleset 2</b> ("merge-queue: reviews and checks") holds the <c>pull_request</c> and <c>required_status_checks</c>
/// rules. Its other rules and its people bypass actors are kept; only the App is removed from them (O22). Parameters the
/// plan does not manage (GitHub's <c>allowed_merge_methods</c>, a check's <c>integration_id</c>) are kept as they are.
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
        List<string> restricted = [DefaultBranch, .. (choices.ReleaseLines ?? []).Where(l => l != DefaultBranch).Distinct(StringComparer.Ordinal)];

        if (choices.RequiredApprovals is < 0 or > 10)
            blockers.Add($"The approval count must be between 0 and 10, not {choices.RequiredApprovals}.");
        var checks = choices.RequiredChecks.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.Ordinal).ToList();
        if (checks.Contains(CheckGate.QueueCheckName, StringComparer.Ordinal))
            blockers.Add($"\"{CheckGate.QueueCheckName}\" cannot be a required check: it is still running while the queue merges, so every merge would be refused (D62).");
        if (state.Classic?.HasPushRestrictions == true)
            blockers.Add("Classic protection limits pushes to named people, teams or Apps. Rulesets have no such setting, so remove the restriction by hand first.");

        var existing1 = Single(state, RestrictUpdatesName, blockers);
        var existing2 = Single(state, ReviewsAndChecksName, blockers);

        var desired1 = RestrictUpdates(state, choices, restricted, existing1);
        var desired2 = ReviewsAndChecks(state, choices.RequiredApprovals, checks, include, existing2);
        AddStep(steps, existing1, desired1);
        AddStep(steps, existing2, desired2);

        if (state.Classic is { } classic)
            steps.Add(new DeleteClassicProtectionStep(ClassicNotes(classic, choices.RequiredApprovals, checks)));

        return new MergeQueueRulesetPlan(steps, blockers);
    }

    /// <summary>
    /// The starting answers for one repository: ruleset 2's own settings when it exists, else classic protection's, else
    /// <paramref name="headChecks"/> (the checks on the default branch's head) and one approval. Never the queue's own
    /// check (D62). (From the abandoned e3.)
    /// </summary>
    public static MergeQueueRulesetChoices Defaults(ForgeRulesetState state, IReadOnlyList<string> headChecks)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(headChecks);
        var ruleset1 = state.Rulesets.FirstOrDefault(r => r.Definition.Name == RestrictUpdatesName)?.Definition;
        var ruleset2 = state.Rulesets.FirstOrDefault(r => r.Definition.Name == ReviewsAndChecksName)?.Definition;

        var approvals = ruleset2?.Rules.FirstOrDefault(r => r.Type == "pull_request")?.Parameters?["required_approving_review_count"] is JsonValue count
            && count.TryGetValue<int>(out var n) ? n : state.Classic?.RequiredApprovals ?? 1;
        var ruleset2Checks = (ruleset2?.Rules.FirstOrDefault(r => r.Type == "required_status_checks")?.Parameters?["required_status_checks"] as JsonArray ?? [])
            .OfType<JsonObject>().Select(c => c["context"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList();
        IEnumerable<string> checks = ruleset2Checks.Count > 0 ? ruleset2Checks
            : state.Classic is { RequiredChecks.Count: > 0 } classic ? classic.RequiredChecks
            : headChecks;
        return new MergeQueueRulesetChoices(approvals,
            [.. checks.Where(c => !string.IsNullOrWhiteSpace(c) && c != CheckGate.QueueCheckName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            ruleset1?.BypassActors.Any(IsAdminByPullRequest) ?? false);
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
                + string.Join(", ", create.Definition.Rules.Select(r => r.Type)) + ".",
            UpdateRulesetStep update => $"Update ruleset \"{update.Definition.Name}\": {string.Join("; ", update.Changes)}.",
            DeleteClassicProtectionStep delete => "Delete the classic branch protection: " + string.Join(" ", delete.Notes),
            _ => step.ToString()!,
        })];
    }

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

    private static ForgeRulesetDefinition ReviewsAndChecks(ForgeRulesetState state, int approvals, List<string> checks, List<string> include,
        ForgeExistingRuleset? existing)
    {
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
        {
            var statusChecks = Clone(kept.FirstOrDefault(r => r.Type == "required_status_checks")?.Parameters);
            var byContext = (statusChecks["required_status_checks"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Where(c => c["context"] is JsonValue)
                .GroupBy(c => c["context"]!.GetValue<string>(), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            statusChecks["strict_required_status_checks_policy"] = true;
            statusChecks["required_status_checks"] = new JsonArray([.. checks.Order(StringComparer.Ordinal)
                .Select(c => (JsonNode)(byContext.TryGetValue(c, out var known) ? known.DeepClone() : new JsonObject { ["context"] = c }))]);
            rules.Add(new("required_status_checks", statusChecks));
        }

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
            else if (!JsonNode.DeepEquals(current.Parameters ?? [], rule.Parameters ?? [])) changes.Add($"rule changed: {rule.Type}");
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
