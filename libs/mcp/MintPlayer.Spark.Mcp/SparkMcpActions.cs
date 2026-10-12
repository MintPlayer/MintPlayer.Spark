using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Mcp;

/// <summary>
/// Runs a custom action from an application's own MCP tool through exactly the gates of
/// <c>POST /spark/actions/execute</c> (MCP PRD D9): the type right, the action catalogue, the object loaded through the
/// row-gated read, the row check for the action, disabled actions and the quota gates, then the action itself. The
/// agent layers apply inside <c>IPermissionService</c> as for every call; a tool that runs an action on a type agents
/// may only read declares it with <see cref="McpRunsActionAttribute"/>.
/// </summary>
/// <remarks>
/// Never a parallel path: the web's button and the agent's tool reach the same <c>ICustomActionExecution</c>, so a quota,
/// a cool-down or an organization role the action enforces holds for both.
/// </remarks>
public interface ISparkMcpActions
{
    /// <summary>
    /// Runs <paramref name="action"/> on the object <paramref name="id"/> of entity type <paramref name="type"/> (its name,
    /// alias or id), as the detail page's button does.
    /// </summary>
    Task<SparkMcpActionOutcome> RunAsync(string type, string action, string id, CancellationToken cancellationToken = default);
}

/// <summary>How a custom action run by <see cref="ISparkMcpActions"/> ended.</summary>
public enum SparkMcpActionStatus
{
    /// <summary>The action ran; <see cref="SparkMcpActionOutcome.Result"/> is what it handed to <c>SetResult</c>.</summary>
    Executed,

    /// <summary>
    /// Refused as a not-found is refused (MCP PRD D17): an unknown type or object, a right, a row rule. Answer it with
    /// <see cref="SparkMcpResult.Refuse"/>, so the caller cannot tell which.
    /// </summary>
    Refused,

    /// <summary>The action refused the input with a reason the user may read (<see cref="SparkMcpActionOutcome.Message"/>).</summary>
    Invalid,

    /// <summary>The action is disabled on this object now (<c>OnDisableActionsAsync</c>).</summary>
    Disabled,

    /// <summary>A quota or cool-down: <see cref="SparkMcpActionOutcome.Message"/> says why, <see cref="SparkMcpActionOutcome.RetryAfter"/> when.</summary>
    Throttled,

    /// <summary>The action failed unexpectedly; the details are in the server log only.</summary>
    Failed,
}

/// <summary>The outcome of <see cref="ISparkMcpActions.RunAsync"/>.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Result">What the action handed to <c>SetResult</c>, when it ran.</param>
/// <param name="Message">The user-facing reason of a refusal that is not a not-found, in the caller's language where the action translated it.</param>
/// <param name="RetryAfter">When a throttled action may be tried again, if the gate said.</param>
public sealed record SparkMcpActionOutcome(SparkMcpActionStatus Status, object? Result = null, string? Message = null, TimeSpan? RetryAfter = null);

/// <summary>The web's custom-action sequence (<c>ICustomActionExecution</c>), mapped as <c>ExecuteCustomAction</c> maps it to HTTP.</summary>
internal sealed partial class McpCustomActions(IModelLoader modelLoader, ICustomActionExecution execution, ILogger<McpCustomActions> logger) : ISparkMcpActions
{
    public async Task<SparkMcpActionOutcome> RunAsync(string type, string action, string id, CancellationToken cancellationToken = default)
    {
        var entityType = string.IsNullOrWhiteSpace(type) ? null : modelLoader.ResolveEntityType(type);
        if (entityType is null || string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(id))
            return new(SparkMcpActionStatus.Refused);

        try
        {
            // The detail page's invocation: the object as the parent, no selection. The executor reloads it through the
            // row-gated read under the action's own type (C3), so the submitted id is never taken as fact.
            var parent = new PersistentObject { Id = id, Name = entityType.Name, ObjectTypeId = entityType.Id };
            var outcome = await execution.ExecuteAsync(entityType, new CustomActionInvocation(action, parent, null, null, null, null), cancellationToken);
            return outcome.Kind switch
            {
                CustomActionOutcomeKind.Executed => new(SparkMcpActionStatus.Executed, outcome.Result),
                CustomActionOutcomeKind.Invalid => new(SparkMcpActionStatus.Invalid, Message: outcome.Error),
                CustomActionOutcomeKind.Disabled => new(SparkMcpActionStatus.Disabled, Message: $"'{action}' is not available on this object now."),
                // NotFound (no such action) answers like Refused on the web too, for a caller who may not know the catalogue.
                _ => new(SparkMcpActionStatus.Refused),
            };
        }
        catch (SparkAccessDeniedException)
        {
            return new(SparkMcpActionStatus.Refused);
        }
        catch (SparkActionDisabledException ex)
        {
            return new(SparkMcpActionStatus.Disabled, Message: $"'{ex.ActionName}' is not available on this object now.");
        }
        catch (SparkValidationException ex)
        {
            return new(SparkMcpActionStatus.Invalid, Message: ex.Message);
        }
        catch (SparkThrottledException ex)
        {
            return new(SparkMcpActionStatus.Throttled, Message: ex.Message, RetryAfter: ex.RetryAfter);
        }
        catch (SparkRetryActionException)
        {
            // A question for the user (Retry.Action): only the web app can show it.
            return new(SparkMcpActionStatus.Invalid, Message: $"'{action}' asks the user a question that only the web application can show. Run it there.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(logger, ex, action, entityType.Name);
            return new(SparkMcpActionStatus.Failed, Message: "The action failed.");
        }
    }

    [LoggerMessage(EventId = 2, EventName = "SparkMcpActionFailed", Level = LogLevel.Error,
        Message = "Custom action '{ActionName}' on '{EntityType}' failed in an MCP tool.")]
    private static partial void LogFailed(ILogger logger, Exception exception, string actionName, string entityType);
}
