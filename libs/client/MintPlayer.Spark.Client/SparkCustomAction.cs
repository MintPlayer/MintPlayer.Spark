using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Client;

/// <summary>
/// One custom action as <c>POST /spark/actions/list</c> reports it for an entity type — already
/// narrowed to the actions the caller is allowed to invoke.
/// </summary>
/// <remarks>
/// ⚠️ <b>Not the server's <c>CustomActionDefinition</c>.</b> That type has no <see cref="Name"/>:
/// the name is the key the definition is filed under, and the endpoint composes it back in when it
/// projects the list. Reusing the definition here would drop the one field a caller needs in order
/// to invoke anything.
/// </remarks>
public sealed class SparkCustomAction
{
    /// <summary>The action name to pass to <see cref="SparkClient.ExecuteActionAsync"/>.</summary>
    public string Name { get; init; } = string.Empty;

    public TranslatedString? DisplayName { get; init; }
    public string? Icon { get; init; }
    public string? Description { get; init; }

    /// <summary>Where the action is shown: <c>detail</c>, <c>query</c>, or <c>both</c>.</summary>
    public string ShowedOn { get; init; } = "both";

    /// <summary>
    /// Selection rule for query-view actions: <c>=0</c>, <c>=1</c>, <c>&gt;0</c>. Null when the
    /// action takes no selection.
    /// </summary>
    public string? SelectionRule { get; init; }

    public bool RefreshOnCompleted { get; init; }
    public string? ConfirmationMessageKey { get; init; }

    /// <summary>Presentation only — <c>primary</c>, <c>secondary</c>, <c>danger</c>, <c>warning</c>.</summary>
    /// <remarks>
    /// Never authorization. An action is offered because a right grants it; a variant only asks the
    /// client to make it look like what it is.
    /// </remarks>
    public string? Variant { get; init; }

    /// <summary>Display order, lowest first. The endpoint already returns the list sorted by it.</summary>
    public int Offset { get; init; }
}
