using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Client;

/// <summary>
/// One action of the composed catalogue as <c>POST /spark/actions/list</c> reports it for an entity
/// type — already narrowed to the actions the caller is allowed to invoke. Built-ins (New, Edit,
/// Delete) and custom actions alike (#467, D7).
/// </summary>
/// <remarks>
/// Text arrives resolved (#467, D26): <see cref="Label"/>, <see cref="Description"/> and
/// <see cref="Confirmation"/> carry every language, not translation keys.
/// </remarks>
public sealed class SparkCustomAction
{
    /// <summary>The action name to pass to <see cref="SparkClient.ExecuteActionAsync"/>.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The label: <c>actions.{Name}.label</c>, or the humanized name when untranslated.</summary>
    public TranslatedString? Label { get; init; }
    public string? Icon { get; init; }

    /// <summary><c>actions.{Name}.description</c>; null when untranslated.</summary>
    public TranslatedString? Description { get; init; }

    /// <summary>Where the action is shown: <c>detail</c>, <c>query</c>, or <c>both</c>.</summary>
    public string ShowedOn { get; init; } = "both";

    /// <summary>
    /// The selection a query invocation must have, as a cardinality expression over the selected row
    /// count: <c>=1</c>, <c>&gt;0</c>, <c>&lt;=5</c>, <c>1&lt;X&lt;5</c>. Null when the action takes no
    /// selection. The server enforces it; a violating request is a 400.
    /// </summary>
    public string? SelectionRule { get; init; }

    public bool RefreshOnCompleted { get; init; }

    /// <summary>The confirmation to ask before running; may contain <c>{count}</c>. Null asks nothing.</summary>
    public TranslatedString? Confirmation { get; init; }

    /// <summary>Presentation only — <c>primary</c>, <c>secondary</c>, <c>danger</c>, <c>warning</c>.</summary>
    /// <remarks>
    /// Never authorization. An action is offered because a right grants it; a variant only asks the
    /// client to make it look like what it is.
    /// </remarks>
    public string? Variant { get; init; }

    /// <summary>Display order, lowest first. The endpoint already returns the list sorted by it.</summary>
    public int Offset { get; init; }

    /// <summary>
    /// True for the framework's built-in <c>New</c>, <c>Edit</c> and <c>Delete</c> (#460 D18, #467 D7),
    /// which run through
    /// <see cref="SparkClient.NewPersistentObjectAsync(string,string?,string?,string?,IReadOnlyDictionary{string,string}?,CancellationToken,SparkRetryHandler?,SparkOperationHandler?)"/>,
    /// an update and <see cref="SparkClient.DeletePersistentObjectsAsync"/> rather than
    /// <see cref="SparkClient.ExecuteActionAsync"/>.
    /// </summary>
    public bool IsDefault { get; init; }

    /// <summary>
    /// The client method the action needs, e.g. <c>webauthn.create</c>: it raises a client-method step
    /// (<see cref="RetryActionPayload.ClientMethod"/>) a handler answers with <see cref="RetryAnswer.Return"/>.
    /// Null when the action needs none.
    /// </summary>
    public string? RequiresClient { get; init; }
}
