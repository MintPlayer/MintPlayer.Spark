using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeCoverage.Forge;
using Repository = CodeCoverage.Entities.Repository;

namespace CodeCoverage.Services;

/// <summary>
/// #554: reading and writing the merge queue's rulesets and the classic protection they replace.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both halves run on the caller's token, never the App's</b> (#554 redesign): a one-shot OAuth token of the signed-in
/// admin, revoked by the caller when the action ends. The Apps hold no Administration, so no installation token can read
/// classic protection's details or change a ruleset; only <see cref="AppIdAsync"/> still speaks as the App (its JWT).
/// </para>
/// <para>
/// A refusal is a <see cref="ForgeAccessDeniedException"/> carrying GitHub's own message and no App permission to accept:
/// the person lacks admin on the repository, or the organization has not approved the OAuth app.
/// </para>
/// </remarks>
public partial class GitHubForgeMergeQueue
{
    public async Task<ForgeRulesetState?> GetRulesetStateAsync(Repository repository, string branch, string accessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        var branchPath = BranchPath(branch);
        var appId = await AppIdAsync(cancellationToken);
        var token = accessToken;

        var rulesets = new List<ForgeExistingRuleset>();
        var available = true;
        IReadOnlyList<JsonElement> listed;
        try
        {
            listed = await GetPagedAsync(repository, $"{RepoPath(repository)}/rulesets?includes_parents=false", null, cancellationToken, token);
        }
        catch (ForgeAccessDeniedException ex) when (IsPlanWithoutRulesets(ex.Message))
        {
            available = false;
            listed = [];
        }

        foreach (var id in listed.Select(r => Num(r, "id")).OfType<long>().Distinct().Order())
        {
            using var reply = await SendAsync(token, HttpMethod.Get,
                $"{RepoPath(repository)}/rulesets/{id.ToString(CultureInfo.InvariantCulture)}", null, DefaultApiVersion, cancellationToken);
            if (reply.Status == HttpStatusCode.NotFound) continue; // deleted between the listing and now
            EnsureSuccess(reply);
            rulesets.Add(new ForgeExistingRuleset(id, ReadRulesetDefinition(reply.Json!.RootElement)));
        }

        using var protection = await SendAsync(token, HttpMethod.Get, $"{RepoPath(repository)}/branches/{branchPath}/protection", null, DefaultApiVersion, cancellationToken);
        ForgeClassicProtection? classic = null;
        if (protection.Status != HttpStatusCode.NotFound)
        {
            EnsureSuccess(protection);
            classic = ReadClassicProtection(protection.Json!.RootElement);
        }

        return new ForgeRulesetState(appId, rulesets, classic, available);
    }

    public async Task<IReadOnlyList<ForgeRulesetApplied>> ApplyRulesetPlanAsync(Repository repository, string branch, MergeQueueRulesetPlan plan,
        string accessToken, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        if (!plan.CanApply)
            throw new InvalidOperationException(plan.Blockers.Count > 0
                ? "The plan has blockers; nothing was changed."
                : "The plan has nothing to change.");
        var branchPath = BranchPath(branch);
        var token = accessToken;
        var applied = new List<ForgeRulesetApplied>();

        foreach (var step in plan.Steps)
        {
            switch (step)
            {
                case CreateRulesetStep create:
                {
                    using var reply = await SendAsync(token, HttpMethod.Post, $"{RepoPath(repository)}/rulesets",
                        RulesetBody(create.Definition), DefaultApiVersion, cancellationToken);
                    EnsureSuccess(reply);
                    var id = Num(reply.Json!.RootElement, "id");
                    applied.Add(new($"Created ruleset \"{create.Definition.Name}\".", id is { } created ? RulesetUrl(repository, created) : null));
                    break;
                }
                case UpdateRulesetStep update:
                {
                    using var reply = await SendAsync(token, HttpMethod.Put,
                        $"{RepoPath(repository)}/rulesets/{update.Id.ToString(CultureInfo.InvariantCulture)}",
                        RulesetBody(update.Definition), DefaultApiVersion, cancellationToken);
                    EnsureSuccess(reply);
                    applied.Add(new($"Updated ruleset \"{update.Definition.Name}\": {string.Join("; ", update.Changes)}.", RulesetUrl(repository, update.Id)));
                    break;
                }
                case DeleteClassicProtectionStep:
                {
                    using var reply = await SendAsync(token, HttpMethod.Delete,
                        $"{RepoPath(repository)}/branches/{branchPath}/protection", null, DefaultApiVersion, cancellationToken);
                    if (reply.Status != HttpStatusCode.NotFound) // already gone is what the step wanted
                        EnsureSuccess(reply);
                    applied.Add(new($"Removed the classic branch protection on {branch}.",
                        $"https://github.com/{repository.FullName}/settings/branches"));
                    break;
                }
                default:
                    throw new NotSupportedException($"Unknown ruleset step {step.GetType().Name}.");
            }
        }
        return applied;
    }

    /// <summary>The App's own id (<c>GET /app</c>, as the App): ruleset 1's bypass actor.</summary>
    private async Task<long> AppIdAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var app = await installationService.CreateAppClientAsync();
        return (await app.GitHubApps.GetCurrent()).Id;
    }

    private static string RulesetUrl(Repository repository, long id)
        => $"https://github.com/{repository.FullName}/rules/{id.ToString(CultureInfo.InvariantCulture)}";

    internal static ForgeRulesetDefinition ReadRulesetDefinition(JsonElement root)
    {
        var include = new List<string>();
        var exclude = new List<string>();
        if (root.TryGetProperty("conditions", out var conditions) && conditions.ValueKind == JsonValueKind.Object
            && conditions.TryGetProperty("ref_name", out var refName) && refName.ValueKind == JsonValueKind.Object)
        {
            include.AddRange(Items(refName, "include").Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!));
            exclude.AddRange(Items(refName, "exclude").Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!));
        }

        var rules = Items(root, "rules")
            .Where(r => Str(r, "type") is { Length: > 0 })
            .Select(r => new ForgeRulesetRule(Str(r, "type")!,
                r.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Object
                    ? JsonNode.Parse(parameters.GetRawText()) as JsonObject
                    : null))
            .ToList();

        var actors = Items(root, "bypass_actors")
            .Select(a => new ForgeRulesetActor(Num(a, "actor_id"), Str(a, "actor_type") ?? string.Empty, Str(a, "bypass_mode") ?? string.Empty))
            .ToList();

        return new ForgeRulesetDefinition(Str(root, "name") ?? string.Empty, Str(root, "target") ?? "branch", Str(root, "enforcement") ?? string.Empty,
            include, exclude, rules, actors);
    }

    internal static ForgeClassicProtection ReadClassicProtection(JsonElement root)
    {
        var reviews = root.TryGetProperty("required_pull_request_reviews", out var r) && r.ValueKind == JsonValueKind.Object ? r : default;
        var checks = new SortedSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("required_status_checks", out var required) && required.ValueKind == JsonValueKind.Object)
        {
            foreach (var context in Items(required, "contexts"))
                if (context.ValueKind == JsonValueKind.String && context.GetString() is { Length: > 0 } name) checks.Add(name);
            foreach (var check in Items(required, "checks"))
                if (Str(check, "context") is { Length: > 0 } name) checks.Add(name);
        }

        return new ForgeClassicProtection(
            LockBranch: Enabled(root, "lock_branch"),
            RequiredApprovals: reviews.ValueKind == JsonValueKind.Object ? (int)(Num(reviews, "required_approving_review_count") ?? 0) : 0,
            DismissStaleReviews: reviews.ValueKind == JsonValueKind.Object && Flag(reviews, "dismiss_stale_reviews"),
            RequireLastPushApproval: reviews.ValueKind == JsonValueKind.Object && Flag(reviews, "require_last_push_approval"),
            RequireCodeOwnerReviews: reviews.ValueKind == JsonValueKind.Object && Flag(reviews, "require_code_owner_reviews"),
            RequireConversationResolution: Enabled(root, "required_conversation_resolution"),
            RequiredChecks: [.. checks],
            RequiredLinearHistory: Enabled(root, "required_linear_history"),
            RequiredSignatures: Enabled(root, "required_signatures"),
            HasPushRestrictions: root.TryGetProperty("restrictions", out var restrictions) && restrictions.ValueKind == JsonValueKind.Object);
    }

    /// <summary>Classic protection's <c>{ "setting": { "enabled": true } }</c> shape.</summary>
    private static bool Enabled(JsonElement root, string name)
        => root.TryGetProperty(name, out var setting) && setting.ValueKind == JsonValueKind.Object && Flag(setting, "enabled");

    internal static JsonObject RulesetBody(ForgeRulesetDefinition definition) => new()
    {
        ["name"] = definition.Name,
        ["target"] = definition.Target,
        ["enforcement"] = definition.Enforcement,
        ["conditions"] = new JsonObject
        {
            ["ref_name"] = new JsonObject
            {
                ["include"] = new JsonArray([.. definition.Include.Select(i => (JsonNode)JsonValue.Create(i))]),
                ["exclude"] = new JsonArray([.. definition.Exclude.Select(e => (JsonNode)JsonValue.Create(e))]),
            },
        },
        ["rules"] = new JsonArray([.. definition.Rules.Select(rule =>
        {
            var node = new JsonObject { ["type"] = rule.Type };
            if (rule.Parameters is { } parameters) node["parameters"] = parameters.DeepClone();
            return (JsonNode)node;
        })]),
        ["bypass_actors"] = new JsonArray([.. definition.BypassActors.Select(actor => (JsonNode)new JsonObject
        {
            ["actor_id"] = actor.ActorId,
            ["actor_type"] = actor.ActorType,
            ["bypass_mode"] = actor.BypassMode,
        })]),
    };
}
