using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Queries;

namespace MintPlayer.Spark.Actions;

/// <summary>When the framework is asking <c>OnDisableActionsAsync</c>.</summary>
public enum DisableActionsPhase
{
    /// <summary>A page is loading: the answer is returned to the client as <c>DisabledActions</c>.</summary>
    Load,

    /// <summary>
    /// An action is being submitted (<see cref="DisableActionsContext.ActionName"/>). If the hook
    /// disables it on any evaluated target, the request is refused with <c>403</c>.
    /// </summary>
    Submit,
}

/// <summary>What an <see cref="IDisablable"/> handed to <c>OnDisableActionsAsync</c> stands for.</summary>
public enum DisableActionsTargetKind
{
    /// <summary>
    /// One object of the actions class's type: a detail page, the object an action or save is
    /// submitted on, or one selected row. <see cref="DisableActionsContext.Entity"/> is its stored state.
    /// </summary>
    PersistentObject,

    /// <summary>
    /// A query of the actions class's type: a query page or sub-query grid, or the query a custom
    /// action was invoked from. <see cref="DisableActionsContext.Query"/> names it.
    /// </summary>
    Query,
}

/// <summary>
/// What <c>OnDisableActionsAsync</c> is told about the target it may withhold actions on (#460, D13).
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>The hook must depend only on the entity, the user and stored state.</b> It is evaluated
/// twice — when the page loads and again when an action is submitted — and the two answers must
/// agree, or a button is offered that then refuses (or hidden while the action still runs). Nothing
/// the client posted is in scope, deliberately: at submit, <see cref="Entity"/> is what is
/// <em>stored</em>, not the values being saved.
/// </para>
/// <para>
/// A decision about <c>New</c> sees no entity (there is none yet), and a create request names no
/// query, so a hook that withholds <c>New</c> only for a particular query hides the button but cannot
/// be enforced at submit. Decide <c>New</c> on the user and stored state alone.
/// </para>
/// </remarks>
public sealed class DisableActionsContext
{
    /// <summary>Load (the answer is returned) or Submit (the answer is enforced).</summary>
    public required DisableActionsPhase Phase { get; init; }

    /// <summary>Whether the target is one object or a query.</summary>
    public required DisableActionsTargetKind TargetKind { get; init; }

    /// <summary>
    /// At <see cref="DisableActionsPhase.Submit"/>, the action being submitted: a custom action's
    /// name, or <c>Edit</c> (an update), <c>New</c> (a create) or <c>Delete</c>. Null at load, where
    /// the hook should withhold everything that does not apply.
    /// <para>
    /// <c>Save</c> is honoured as an alias: an update is refused when <c>Edit</c> or <c>Save</c> is
    /// disabled, a create when <c>New</c> or <c>Save</c> is.
    /// </para>
    /// </summary>
    public string? ActionName { get; init; }

    /// <summary>The object's id for a <see cref="DisableActionsTargetKind.PersistentObject"/> target; null for a new object and for a query.</summary>
    public string? Id { get; init; }

    /// <summary>
    /// The object's stored entity for a <see cref="DisableActionsTargetKind.PersistentObject"/>
    /// target — typed as the actions class's entity, so <c>context.Entity is Car car</c> is the usual
    /// read. Null for a query target, for <c>New</c>, and for a type without documents (a composed type).
    /// </summary>
    public object? Entity { get; init; }

    /// <summary>The query, for a <see cref="DisableActionsTargetKind.Query"/> target.</summary>
    public SparkQueryInfo? Query { get; init; }

    /// <summary>
    /// For a <see cref="DisableActionsTargetKind.Query"/> target running as a sub-query: the object
    /// whose detail page it is rendered on (a different type). Null for a top-level query.
    /// </summary>
    public PersistentObject? Parent { get; init; }

    /// <summary>The entity type name of <see cref="Parent"/>.</summary>
    public string? ParentType { get; init; }
}

/// <summary>One target of a batched <c>OnDisableActionsAsync</c> call, with its own context.</summary>
/// <param name="Target">What to withhold actions on.</param>
/// <param name="Context">What the target is.</param>
public sealed record DisableActionsItem(IDisablable Target, DisableActionsContext Context);
