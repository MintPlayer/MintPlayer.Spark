using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Endpoints.PersistentObject;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Mcp.Plans;

/// <summary>One change an agent asks for in <c>propose_changes</c>.</summary>
public sealed class McpChange
{
    [Description("create, update, delete, or action (a custom action).")]
    public string Kind { get; set; } = "";

    [Description("The entity type's name, as discover lists it.")]
    public string Type { get; set; } = "";

    [Description("The object's id (update, delete; for action, the object it runs on). Omit for create.")]
    public string? Id { get; set; }

    [Description("Optional: the etag get returned. If the object changed since, the proposal fails rather than overwrite what you have not seen.")]
    public string? Etag { get; set; }

    [Description("create and update: the attributes to set, by name, with their new values (JSON). Attributes you leave out keep their value.")]
    public Dictionary<string, JsonElement>? Values { get; set; }

    [Description("action: the custom action's name.")]
    public string? Action { get; set; }

    [Description("action on a selection: the query the rows come from.")]
    public string? Query { get; set; }

    [Description("action on a selection: the selected rows' ids.")]
    public string[]? Ids { get; set; }
}

/// <summary>A plan the caller cannot have, or whose operation is refused; the message is the tool's answer.</summary>
internal sealed class McpPlanRefusal(string message) : Exception(message);

/// <summary>
/// <c>propose_changes</c> and <c>apply_changes</c> (MCP PRD D8, §4.4): every operation goes through the web's own
/// write path (<see cref="IPersistentObjectSavePath"/>, <see cref="ICustomActionExecution"/>), checked without
/// writing at propose time and run for real at apply time, after every check again.
/// </summary>
/// <remarks>
/// <para>
/// <b>What runs is what was shown.</b> An operation is prepared the same way at both times — the object loaded as
/// the caller sees it, the values set as the web client would post them, the readable diff rendered from that — so
/// apply re-renders the diff and refuses a plan whose rendering changed (other rights, other data) as well as one
/// whose echoed <c>changes</c> differ from the stored rendering.
/// </para>
/// <para>
/// <b>All or nothing.</b> The saves and deletes of a plan commit in one RavenDB request
/// (<see cref="ISparkWriteUnit.RunInOneCommitAsync"/>), each carrying the version captured at propose time, together
/// with the plan's own "applied" record. A conflict anywhere fails the request and nothing is written. A custom action
/// runs code the framework cannot hold back, so a custom action is always a plan of its own.
/// </para>
/// </remarks>
internal sealed class McpPlanner(
    IModelLoader modelLoader,
    IPersistentObjectReadPath objectReadPath,
    IPersistentObjectSavePath savePath,
    ICustomActionExecution customActions,
    ISparkWriteUnit writeUnit,
    IPermissionService permissionService,
    IEntityMapper entityMapper,
    INewInvoker newInvoker,
    ISparkTypeResolver typeResolver,
    IDocumentStore store,
    IAsyncDocumentSession session,
    ISparkCurrentUser currentUser,
    IHttpContextAccessor httpContextAccessor,
    SparkMcpOptions options,
    IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions,
    ILogger<McpPlanner> logger)
{
    /// <summary>The most operations one plan may hold (MCP PRD §4.4).</summary>
    public const int MaxOperations = 50;

    /// <summary>How long a proposed plan can be applied (MCP PRD D8).</summary>
    public static readonly TimeSpan ProposalLifetime = TimeSpan.FromMinutes(10);

    /// <summary>The most characters of diff one plan may show: the answer has to fit an agent's tool-output budget.</summary>
    private const int MaxChangesCharacters = 30_000;

    private const string Collection = "McpPlans/";
    private static readonly Regex PlanIdPattern = new("^McpPlans/[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    /// <summary>A validated plan, as <c>propose_changes</c> answers it.</summary>
    public sealed record Proposal(string PlanId, DateTime ExpiresAt, IReadOnlyList<string> Changes);

    /// <summary>A committed plan, as <c>apply_changes</c> answers it.</summary>
    public sealed record Application(string PlanId, IReadOnlyList<string> Changes, IReadOnlyList<string?> ResultIds, object? ActionResult)
    {
        /// <summary>
        /// The operations the application diverted for approval (an <c>ISaveReplacement</c>, commerce X7): committed as
        /// proposals with the plan, not saved. Their <see cref="ResultIds"/> entry is null.
        /// </summary>
        public IReadOnlyList<Submission> Submitted { get; init; } = [];
    }

    /// <summary>An operation the application diverted for approval: its 1-based number and what it became.</summary>
    public sealed record Submission(int Operation, string Kind, string ReferenceId);

    private sealed record Prepared(McpPlanOperation Operation, EntityTypeDefinition EntityType, PersistentObject? Object, string Text);

    public async Task<Proposal> ProposeAsync(string? summary, IReadOnlyList<McpChange>? changes, CancellationToken cancellationToken)
    {
        var owner = Owner();
        if (changes is null || changes.Count == 0)
            throw new McpPlanRefusal("A plan needs at least one operation.");
        if (changes.Count > MaxOperations)
            throw new McpPlanRefusal($"A plan holds at most {MaxOperations} operations; this one has {changes.Count}. Split the work into several plans.");

        var operations = changes.Select(ToOperation).ToList();
        EnsureShape(operations);

        using var _ = session.IgnoreMaxRequests(RequestBudget(operations.Count), logger, $"MCP propose_changes ({operations.Count} operations)");

        var rendered = new List<string>(operations.Count);
        for (var i = 0; i < operations.Count; i++)
        {
            var prepared = await PrepareAsync(operations[i], i, proposing: true, cancellationToken);
            await Guarded(i, () => CheckAsync(prepared, i, cancellationToken));
            rendered.Add(prepared.Text);
        }

        if (rendered.Sum(r => r.Length) > MaxChangesCharacters)
            throw new McpPlanRefusal($"The plan's changes are too long to show ({rendered.Sum(r => r.Length)} characters, at most {MaxChangesCharacters}). Propose fewer changes per plan.");

        var now = DateTime.UtcNow;
        var plan = new McpPlan
        {
            Id = Collection + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            UserId = owner.UserId,
            ClientId = owner.ClientId,
            AgentKind = owner.Kind,
            GrantId = owner.GrantId,
            Summary = string.IsNullOrWhiteSpace(summary) ? null : summary.Trim(),
            Operations = operations,
            Changes = rendered,
            CreatedAt = now,
            ExpiresAt = now + ProposalLifetime,
        };

        // Its own session: the request session holds what the checks loaded, and nothing of it may be written.
        using (var planSession = store.OpenAsyncSession())
        {
            await planSession.StoreAsync(plan, plan.Id, cancellationToken);
            planSession.Advanced.GetMetadataFor(plan)[Constants.Documents.Metadata.Expires] = plan.ExpiresAt;
            await planSession.SaveChangesAsync(cancellationToken);
        }

        return new Proposal(plan.Id!, plan.ExpiresAt, rendered);
    }

    public async Task<Application> ApplyAsync(string? planId, IReadOnlyList<string>? changes, CancellationToken cancellationToken)
    {
        var owner = Owner();

        // Bound to the user, the agent and the grant that proposed it (D8): anyone else's plan, and a malformed or
        // unknown id, answer alike.
        if (string.IsNullOrEmpty(planId) || !PlanIdPattern.IsMatch(planId))
            throw new McpPlanRefusal(SparkMcpResult.NotFoundMessage);

        // Claimed first, in a session of its own with optimistic concurrency: two applies of one plan race on this
        // write, and only one gets past it. The plan is never appliable again, whatever happens next.
        McpPlan plan;
        using (var claim = store.OpenAsyncSession())
        {
            claim.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
            plan = await claim.LoadAsync<McpPlan>(planId, cancellationToken)
                ?? throw new McpPlanRefusal(SparkMcpResult.NotFoundMessage);

            if (!string.Equals(plan.UserId, owner.UserId, StringComparison.Ordinal)
                || !string.Equals(plan.ClientId, owner.ClientId, StringComparison.Ordinal)
                || !string.Equals(plan.GrantId, owner.GrantId, StringComparison.Ordinal))
                throw new McpPlanRefusal(SparkMcpResult.NotFoundMessage);

            if (plan.Status != McpPlanStatus.Proposed)
                throw new McpPlanRefusal("This plan was already used; a plan runs once. Propose the changes again.");

            if (plan.ExpiresAt <= DateTime.UtcNow)
                throw new McpPlanRefusal($"This plan expired ({ProposalLifetime.TotalMinutes:0} minutes after it was proposed). Propose the changes again.");

            // The echo (D8): the arguments the user approved must be the plan, exactly. Not a claim of the plan: a
            // mistyped echo can be corrected.
            if (!SameChanges(plan.Changes, changes))
                throw new McpPlanRefusal("changes does not match the plan. Pass the changes exactly as propose_changes returned them, or propose again.");

            plan.Status = McpPlanStatus.Applying;
            try
            {
                await claim.SaveChangesAsync(cancellationToken);
            }
            catch (Raven.Client.Exceptions.ConcurrencyException)
            {
                throw new McpPlanRefusal("This plan was already used; a plan runs once. Propose the changes again.");
            }
        }

        try
        {
            return await RunAsync(plan, cancellationToken);
        }
        catch (SparkConcurrencyException)
        {
            // The batch's commit found a document changed after its checks (RavenDB's own change-vector check).
            const string conflict = "Conflict (409): an object of the plan changed since the plan was proposed. Nothing was saved. Read it again and propose new changes.";
            await RecordFailureAsync(plan.Id!, conflict);
            throw new McpPlanRefusal(conflict);
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(plan.Id!, ex is McpPlanRefusal ? ex.Message : "The apply failed: " + ex.GetType().Name);
            throw;
        }
    }

    private async Task<Application> RunAsync(McpPlan plan, CancellationToken cancellationToken)
    {
        var operations = plan.Operations;
        using var _ = session.IgnoreMaxRequests(RequestBudget(operations.Count), logger, $"MCP apply_changes ({operations.Count} operations)");

        // Everything prepared again, as the caller sees it now (rights may have changed), before anything runs.
        var prepared = new List<Prepared>(operations.Count);
        for (var i = 0; i < operations.Count; i++)
        {
            var again = await PrepareAsync(operations[i], i, proposing: false, cancellationToken);
            if (!string.Equals(Normalize(again.Text), Normalize(plan.Changes[i]), StringComparison.Ordinal))
                throw new McpPlanRefusal($"Operation {i + 1} no longer matches the plan: the object or your rights changed since it was proposed. Nothing was saved. Propose again.");
            // Every check of propose again, before anything is written: a refusal now costs no rollback, and a value
            // the write shield would drop silently is refused here, as at propose time.
            var index = i;
            await Guarded(index, () => CheckAsync(again, index, cancellationToken));
            prepared.Add(again);
        }

        var resultIds = new List<string?>(operations.Count);
        var submitted = new List<Submission>();
        object? actionResult = null;

        if (prepared is [{ Operation.Kind: Kinds.Action } action])
        {
            // A custom action runs code that may commit by itself, so it is never batched (EnsureShape): it runs
            // alone, through the same gate sequence as the button, and the plan's record follows it.
            actionResult = await Guarded(0, () => ExecuteActionAsync(action, cancellationToken));
            resultIds.Add(null);
            try
            {
                await RecordSuccessAsync(plan.Id!, resultIds, inRequestSession: false, cancellationToken);
            }
            catch (Exception ex)
            {
                // The action ran; a missing record must not turn that into a failure the agent would retry. The plan
                // stays "Applying", which is just as final: it can never run again.
                logger.LogWarning(ex, "MCP plan {PlanId} ran its custom action but its record could not be written.", plan.Id);
            }
        }
        else
        {
            await writeUnit.RunInOneCommitAsync(async () =>
            {
                for (var i = 0; i < prepared.Count; i++)
                {
                    var index = i;
                    resultIds.Add(await Guarded(index, () => WriteAsync(prepared[index], index, submitted, cancellationToken)));
                }

                // The plan's "applied" record commits with the data, in the same transaction.
                await RecordSuccessAsync(plan.Id!, resultIds, inRequestSession: true, cancellationToken);
            }, RequestBudget(operations.Count), $"MCP apply_changes ({operations.Count} operations)");
        }

        return new Application(plan.Id!, plan.Changes, resultIds, actionResult) { Submitted = submitted };
    }

    // ---- preparing an operation ------------------------------------------------------------------------------------

    private static class Kinds
    {
        public const string Create = "create";
        public const string Update = "update";
        public const string Delete = "delete";
        public const string Action = "action";
    }

    private static McpPlanOperation ToOperation(McpChange change, int index)
    {
        var kind = (change.Kind ?? "").Trim().ToLowerInvariant();
        if (kind is not (Kinds.Create or Kinds.Update or Kinds.Delete or Kinds.Action))
            throw new McpPlanRefusal($"Operation {index + 1}: kind must be create, update, delete or action.");
        if (string.IsNullOrWhiteSpace(change.Type))
            throw new McpPlanRefusal($"Operation {index + 1}: type is required.");
        if (kind is Kinds.Update or Kinds.Delete && string.IsNullOrWhiteSpace(change.Id))
            throw new McpPlanRefusal($"Operation {index + 1}: {kind} needs the object's id.");
        if (kind == Kinds.Create && !string.IsNullOrEmpty(change.Id))
            throw new McpPlanRefusal($"Operation {index + 1}: a create takes no id; the application assigns it.");
        if (kind is Kinds.Create or Kinds.Update && change.Values is not { Count: > 0 })
            throw new McpPlanRefusal($"Operation {index + 1}: {kind} needs values to set.");
        if (kind is Kinds.Delete or Kinds.Action && change.Values is { Count: > 0 })
            throw new McpPlanRefusal($"Operation {index + 1}: {kind} takes no values.");
        if (kind == Kinds.Action && string.IsNullOrWhiteSpace(change.Action))
            throw new McpPlanRefusal($"Operation {index + 1}: action needs the custom action's name.");

        return new McpPlanOperation
        {
            Kind = kind,
            Type = change.Type.Trim(),
            Id = string.IsNullOrWhiteSpace(change.Id) ? null : change.Id,
            Etag = string.IsNullOrWhiteSpace(change.Etag) ? null : change.Etag,
            Values = change.Values?.ToDictionary(v => v.Key, v => v.Value.GetRawText(), StringComparer.Ordinal),
            Action = kind == Kinds.Action ? change.Action!.Trim() : null,
            Query = kind == Kinds.Action && !string.IsNullOrWhiteSpace(change.Query) ? change.Query : null,
            Ids = kind == Kinds.Action ? change.Ids?.ToList() : null,
        };
    }

    /// <summary>One operation per object, and a custom action only on its own.</summary>
    private static void EnsureShape(IReadOnlyList<McpPlanOperation> operations)
    {
        if (operations.Count > 1 && operations.Any(o => o.Kind == Kinds.Action))
            throw new McpPlanRefusal("A custom action is a plan of its own: it runs code that cannot be part of another change's all-or-nothing commit. Propose it separately.");

        var duplicate = operations.Where(o => o.Id is not null)
            .GroupBy(o => (Type: o.Type.ToLowerInvariant(), Id: o.Id!.ToLowerInvariant()))
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new McpPlanRefusal($"Two operations change the same object ({duplicate.Key.Id}); combine them into one.");
    }

    /// <summary>
    /// Builds the operation as the web client would post it, from what the caller may see now, and renders its line
    /// of the diff. At propose time the version is captured; at apply time it must still be the captured one.
    /// </summary>
    private async Task<Prepared> PrepareAsync(McpPlanOperation operation, int index, bool proposing, CancellationToken cancellationToken)
    {
        var number = index + 1;
        var entityType = modelLoader.ResolveEntityType(operation.Type)
            ?? throw new McpPlanRefusal($"Operation {number}: {SparkMcpResult.NotFoundMessage}");
        // D28: the stored plan names the type by its id from here on, so applying it resolves the very type that was
        // proposed. The name an agent passed is resolved alias first, and could name another type at apply time.
        if (proposing)
            operation.Type = entityType.Id.ToString("D");

        // The type-level right first, as the web endpoints ask it before anything else: a caller who may not create,
        // edit or delete this type learns nothing about its attributes, and the application's OnNewAsync hook never
        // runs for them. A custom action's own sequence asks its right first too (CustomActionExecution).
        var verb = operation.Kind switch { Kinds.Create => "New", Kinds.Update => "Edit", Kinds.Delete => "Delete", _ => null };
        if (verb is not null)
            await Guarded(index, () => permissionService.EnsureAuthorizedAsync(verb, entityType.Name));

        PersistentObject? obj = null;
        PersistentObject? loaded = null;
        if (operation.Id is not null)
        {
            loaded = await Guarded(index, () => objectReadPath.LoadAsync(
                new PersistentObjectReferenceRequest { ObjectTypeId = entityType.Id.ToString("D"), Id = operation.Id }, cancellationToken))
                ?? throw new McpPlanRefusal($"Operation {number}: {SparkMcpResult.NotFoundMessage}");

            if (proposing)
            {
                // The agent may say which version it read; a newer one is not overwritten unseen.
                if (operation.Etag is not null && !string.Equals(operation.Etag, loaded.Etag, StringComparison.Ordinal))
                    throw new McpPlanRefusal(Conflict(number, "changed since you read it (the etag differs)"));
                operation.Etag = loaded.Etag;
            }
            else if (!string.Equals(operation.Etag, loaded.Etag, StringComparison.Ordinal))
            {
                throw new McpPlanRefusal(Conflict(number, "changed since the plan was proposed"));
            }
        }

        var header = operation.Kind switch
        {
            Kinds.Create => $"{number}. create {entityType.Name}",
            Kinds.Action => $"{number}. action {operation.Action} on {entityType.Name}" + (loaded is null ? "" : $" {Describe(loaded)}")
                + (operation.Ids is { Count: > 0 } ids ? $" selection [{string.Join(", ", ids)}] of query {operation.Query}" : ""),
            _ => $"{number}. {operation.Kind} {entityType.Name} {Describe(loaded!)}",
        };

        var text = new StringBuilder(header);
        if (operation.Kind is Kinds.Create or Kinds.Update)
        {
            // The object as the web client posts it: the presented object, through Spark's own JSON, back.
            var source = operation.Kind == Kinds.Create
                ? await ScaffoldAsync(entityType, cancellationToken)
                : loaded!;
            obj = RoundTrip(source);
            obj.Etag = operation.Etag;

            var changed = false;
            foreach (var (name, json) in operation.Values!.OrderBy(v => v.Key, StringComparer.Ordinal))
            {
                // An attribute the caller cannot see (a hidden reference included, D19) or may not set is not there for
                // them: one answer for both, which names nothing the caller could not already see.
                if (!obj.TryGetAttribute(name, out var attribute) || attribute.IsReadOnly)
                    throw new McpPlanRefusal($"Operation {number}: '{name}' is not an attribute you can set on {entityType.Name}.");

                using var document = JsonDocument.Parse(json);
                var value = document.RootElement.Clone();
                var before = ValueText(attribute.Value);
                var after = ValueText(value);
                attribute.Value = value;
                attribute.IsValueChanged = true;

                if (operation.Kind == Kinds.Create)
                {
                    text.Append($"\n   {name} = {after}");
                    changed = true;
                }
                else if (!string.Equals(before, after, StringComparison.Ordinal))
                {
                    text.Append($"\n   {name}: {before} -> {after}");
                    changed = true;
                }
            }

            if (!changed)
                throw new McpPlanRefusal($"Operation {number}: the values are the current ones; there is nothing to change.");
        }

        return new Prepared(operation, entityType, obj ?? loaded, text.ToString());
    }

    private async Task CheckAsync(Prepared prepared, int index, CancellationToken cancellationToken)
    {
        var number = index + 1;
        var operation = prepared.Operation;
        switch (operation.Kind)
        {
            case Kinds.Create:
                await savePath.CheckCreateAsync(prepared.EntityType, prepared.Object!, cancellationToken);
                break;
            case Kinds.Update:
                await savePath.CheckUpdateAsync(prepared.EntityType, operation.Id!, prepared.Object!, cancellationToken);
                break;
        }

        // The save drops, without a word, a value the caller may not write (the write shield: a refusal would name the
        // attribute). On the web that is the right answer; in a plan it would make the shown diff a lie, so the
        // operation is refused instead, with the answer an unknown attribute gets.
        if (operation.Kind is Kinds.Create or Kinds.Update)
        {
            foreach (var name in operation.Values!.Keys)
            {
                if (!prepared.Object!.TryGetAttribute(name, out _))
                    throw new McpPlanRefusal($"Operation {number}: '{name}' is not an attribute you can set on {prepared.EntityType.Name}.");
            }
            return;
        }

        switch (operation.Kind)
        {
            case Kinds.Delete:
                if (!await savePath.CheckDeleteAsync(prepared.EntityType, operation.Id!, operation.Etag!, cancellationToken))
                    throw new McpPlanRefusal($"Operation {number}: {SparkMcpResult.NotFoundMessage}");
                break;
            case Kinds.Action:
                Outcome(number, await customActions.CheckAsync(prepared.EntityType, Invocation(prepared), cancellationToken));
                break;
        }
    }

    private async Task<string?> WriteAsync(Prepared prepared, int index, List<Submission> submitted, CancellationToken cancellationToken)
    {
        switch (prepared.Operation.Kind)
        {
            case Kinds.Create:
                return Written(await savePath.CreateAsync(prepared.EntityType, prepared.Object!, parent: null, cancellationToken), index, submitted)?.Id;
            case Kinds.Update:
                Written(await savePath.UpdateAsync(prepared.EntityType, prepared.Operation.Id!, prepared.Object!, retry: null, cancellationToken), index, submitted);
                return null;
            case Kinds.Delete:
                if (!await savePath.DeleteAsync(prepared.EntityType, prepared.Operation.Id!, prepared.Operation.Etag!, cancellationToken))
                    throw SparkConcurrencyException.DeletedSinceLoaded(prepared.Operation.Etag!);
                return null;
            default:
                throw new InvalidOperationException($"Operation kind '{prepared.Operation.Kind}' does not write through a batch.");
        }
    }

    /// <summary>
    /// The saved row of a create or an update, or null when the application diverted it for approval (recorded in
    /// <paramref name="submitted"/>, commerce X7).
    /// </summary>
    /// <exception cref="SparkCancelException">
    /// An interceptor cancelled it (#482). Alone on the web that is "nothing happened"; in a plan it would be a
    /// silently smaller change than the one approved, so the whole plan fails — for an update too, which the save path
    /// answers with the stored row (commerce F4: that used to be reported as applied).
    /// </exception>
    private static PersistentObject? Written(SaveOutcome outcome, int index, List<Submission> submitted)
    {
        switch (outcome.Kind)
        {
            case SaveOutcomeKind.Cancelled:
                throw new SparkCancelException();
            case SaveOutcomeKind.Submitted:
                submitted.Add(new Submission(index + 1, outcome.Receipt!.Kind, outcome.Receipt.ReferenceId));
                return null;
            default:
                return outcome.Object;
        }
    }

    private async Task<object?> ExecuteActionAsync(Prepared prepared, CancellationToken cancellationToken)
        => Outcome(1, await customActions.ExecuteAsync(prepared.EntityType, Invocation(prepared), cancellationToken)).Result;

    private static CustomActionInvocation Invocation(Prepared prepared)
        => new(prepared.Operation.Action!, prepared.Object, prepared.Operation.Ids, ParentId: null, ParentType: null, QueryId: prepared.Operation.Query);

    private static CustomActionOutcome Outcome(int number, CustomActionOutcome outcome) => outcome.Kind switch
    {
        CustomActionOutcomeKind.Executed or CustomActionOutcomeKind.Checked => outcome,
        CustomActionOutcomeKind.Invalid => throw new McpPlanRefusal($"Operation {number}: {outcome.Error}"),
        CustomActionOutcomeKind.Disabled => throw new McpPlanRefusal($"Operation {number}: the action is not available on this object now."),
        _ => throw new McpPlanRefusal($"Operation {number}: {SparkMcpResult.NotFoundMessage}"),
    };

    /// <summary>
    /// Runs <paramref name="work"/> for operation <paramref name="index"/> and turns the write path's refusals into the
    /// tool's answers, mapped as the web endpoints map them (a denial is the uniform not-found).
    /// </summary>
    private static async Task<T> Guarded<T>(int index, Func<Task<T>> work)
    {
        var number = index + 1;
        try
        {
            return await work();
        }
        catch (SparkAccessDeniedException)
        {
            throw new McpPlanRefusal($"Operation {number}: {SparkMcpResult.NotFoundMessage}");
        }
        catch (SparkConcurrencyException ex)
        {
            throw new McpPlanRefusal(Conflict(number, ex.Reason == SparkConcurrencyException.Deleted ? "was deleted" : "changed since the plan was proposed"));
        }
        catch (SparkSaveValidationException ex)
        {
            var errors = ex.Result.Errors.Select(e => $"{e.AttributeName}: {SparkMcpResult.Label(e.ErrorMessage)}");
            throw new McpPlanRefusal($"Operation {number} is not valid: {string.Join("; ", errors)}");
        }
        catch (SparkValidationException ex)
        {
            throw new McpPlanRefusal($"Operation {number} is not valid: {ex.Message}");
        }
        catch (SparkActionDisabledException)
        {
            throw new McpPlanRefusal($"Operation {number}: the action is not available on this object now.");
        }
        catch (SparkThrottledException)
        {
            throw new McpPlanRefusal($"Operation {number}: too many requests; try again later.");
        }
        catch (SparkRetryActionException)
        {
            throw new McpPlanRefusal($"Operation {number}: the application asks a question only its web pages can answer; do this one in the browser.");
        }
        catch (SparkCancelException)
        {
            throw new McpPlanRefusal($"Operation {number}: the application cancelled it. Nothing was saved.");
        }
    }

    private static Task Guarded(int index, Func<Task> work)
        => Guarded<object?>(index, async () => { await work(); return null; });

    private static string Conflict(int number, string what)
        => $"Conflict (409): the object of operation {number} {what}. Nothing was saved. Read it again and propose new changes.";

    // ---- the plan's record -----------------------------------------------------------------------------------------

    private async Task RecordSuccessAsync(string planId, List<string?> resultIds, bool inRequestSession, CancellationToken cancellationToken)
    {
        if (inRequestSession)
        {
            var plan = await session.LoadAsync<McpPlan>(planId, cancellationToken);
            MarkApplied(plan, resultIds);
            session.Advanced.GetMetadataFor(plan)[Constants.Documents.Metadata.Expires] = plan.ExpiresAt;
            return;
        }

        using var record = store.OpenAsyncSession();
        var stored = await record.LoadAsync<McpPlan>(planId, cancellationToken);
        MarkApplied(stored, resultIds);
        record.Advanced.GetMetadataFor(stored)[Constants.Documents.Metadata.Expires] = stored.ExpiresAt;
        await record.SaveChangesAsync(cancellationToken);
    }

    private void MarkApplied(McpPlan plan, List<string?> resultIds)
    {
        var now = DateTime.UtcNow;
        plan.Status = McpPlanStatus.Applied;
        plan.AppliedAt = now;
        plan.ResultIds = resultIds;
        plan.ExpiresAt = now + options.PlanRetention;
    }

    /// <summary>A failed apply is kept as long as an applied one: both are the record of what the agent attempted.</summary>
    private async Task RecordFailureAsync(string planId, string error)
    {
        try
        {
            using var record = store.OpenAsyncSession();
            var plan = await record.LoadAsync<McpPlan>(planId);
            if (plan is null || plan.Status != McpPlanStatus.Applying)
                return;
            plan.Status = McpPlanStatus.Failed;
            plan.Error = error;
            plan.ExpiresAt = DateTime.UtcNow + options.PlanRetention;
            record.Advanced.GetMetadataFor(plan)[Constants.Documents.Metadata.Expires] = plan.ExpiresAt;
            await record.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // The plan stays "Applying", which is just as final: it can never run again.
            logger.LogWarning(ex, "Could not record the failure of MCP plan {PlanId}.", planId);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private sealed record PlanOwner(string UserId, string? ClientId, string Kind, string? GrantId);

    /// <summary>The signed-in caller the plan is bound to. Anonymous callers never propose (D8, D17).</summary>
    private PlanOwner Owner()
    {
        var state = McpRequestState.Of(httpContextAccessor.HttpContext);
        if (state is null || state.IsAnonymous || string.IsNullOrEmpty(currentUser.Id))
            throw new McpAnonymousPlanException();
        return new PlanOwner(currentUser.Id, state.Agent.ClientId, state.Agent.Kind.ToString(), state.Agent.GrantId ?? state.Grant?.Id);
    }

    private async Task<PersistentObject> ScaffoldAsync(EntityTypeDefinition entityType, CancellationToken cancellationToken)
    {
        // As /spark/po/new builds it (New.cs ScaffoldAsync, then OnNewAsync): the caller's shape, the CLR defaults,
        // the application's construction hook.
        var po = await entityMapper.GetPersistentObjectAsync(entityType.Id, SparkCoreActions.New, cancellationToken);
        if (typeResolver.Resolve(entityType.ClrType) is not { } clrType)
            return po;

        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(clrType);
        }
        catch (Exception ex) when (ex is MissingMethodException or MemberAccessException)
        {
        }

        if (instance is not null)
            entityMapper.PopulateAttributeValues(po, instance);
        await newInvoker.InvokeAsync(clrType, po, parent: null, asDetailParent: null, asDetailAttribute: null, parameters: null, cancellationToken);
        return po;
    }

    private PersistentObject RoundTrip(PersistentObject source)
    {
        var json = JsonSerializer.Serialize(source, jsonOptions.Value.SerializerOptions);
        return JsonSerializer.Deserialize<PersistentObject>(json, jsonOptions.Value.SerializerOptions)
            ?? throw new InvalidOperationException("A persistent object did not survive its own JSON.");
    }

    /// <summary>Compact, readable JSON (no escaping of non-ASCII), the same whatever whitespace the agent sent.</summary>
    private static readonly JsonSerializerOptions DiffJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private string ValueText(object? value)
    {
        var element = value switch
        {
            null => default,
            JsonElement e => e,
            _ => JsonSerializer.SerializeToElement(value, jsonOptions.Value.SerializerOptions),
        };
        return element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "null" : JsonSerializer.Serialize(element, DiffJson);
    }

    private static string Describe(PersistentObject obj)
        => string.IsNullOrEmpty(obj.Breadcrumb) || obj.Breadcrumb == obj.Id ? $"({obj.Id})" : $"'{obj.Breadcrumb}' ({obj.Id})";

    /// <summary>Line endings and trailing spaces are the transport's, not the plan's.</summary>
    private static string Normalize(string text)
        => string.Join('\n', text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.TrimEnd()));

    private static bool SameChanges(IReadOnlyList<string> stored, IReadOnlyList<string>? echoed)
        => echoed is not null && echoed.Count == stored.Count
           && stored.Zip(echoed).All(p => string.Equals(Normalize(p.First), Normalize(p.Second ?? ""), StringComparison.Ordinal));

    /// <summary>Requests one plan may make on the request session: the reads and writes of every operation.</summary>
    private static int RequestBudget(int operations) => 30 + operations * 10;
}

/// <summary>An anonymous caller asked to propose or apply: answered with the uniform refusal (D17).</summary>
internal sealed class McpAnonymousPlanException() : Exception("Anonymous callers cannot change data.");
