using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.ClientOperations;

namespace MintPlayer.Spark.Client;

/// <summary>
/// One client operation off a <c>{ result, operations }</c> envelope — a side-effect the server
/// asks the caller to perform, rather than part of the result itself.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>This is deliberately a separate model from <see cref="ClientOperation"/> in Abstractions,
/// and it must stay that way.</b> The server-side DTO is <c>[JsonPolymorphic]</c> with the default
/// <c>UnknownDerivedTypeHandling.FailSerialization</c>, so deserializing an envelope into it throws
/// <see cref="JsonException"/> the moment the server emits a type this client has never heard of —
/// taking the <b>whole response</b> down, result included. The same is true of a known type with a
/// field missing, because every one of those DTOs declares its properties <c>required</c>.
/// </para>
/// <para>
/// Forward compatibility is the single most important property of the operations contract
/// (docs/prd/PRD-ClientOperations.md, "Extensibility contract"), so this hierarchy is parsed by hand
/// from <see cref="JsonElement"/> with tolerant defaults, and anything unrecognised lands on
/// <see cref="SparkUnknownOperation"/> with its payload intact.
/// </para>
/// </remarks>
public abstract class SparkClientOperation
{
    /// <summary>The wire discriminator — <c>notify</c>, <c>refreshAttribute</c>, and so on.</summary>
    public required string Type { get; init; }

    /// <summary>
    /// The operation's raw JSON, kept so a caller can read fields this client version does not model.
    /// </summary>
    public required JsonElement Raw { get; init; }
}

/// <summary>A message to show the user. Surfaced only — this SDK has no UI to show it in.</summary>
public sealed class SparkNotifyOperation : SparkClientOperation
{
    public required string Message { get; init; }

    /// <remarks>
    /// ⚠️ <b>On the wire this is a number, not a name.</b> No <c>JsonStringEnumConverter</c> is
    /// registered on the server, so <c>"kind": 1</c> is Success. A fixture written with
    /// <c>"kind": "Success"</c> does not resemble a real response.
    /// </remarks>
    public NotificationKind Kind { get; init; }

    public int? DurationMs { get; init; }
}

/// <summary>
/// A single attribute on one persistent object has a new value. Apply it with
/// <see cref="SparkClientOperations.Apply(PersistentObject, IEnumerable{SparkClientOperation})"/>.
/// </summary>
public sealed class SparkRefreshAttributeOperation : SparkClientOperation
{
    public string? ObjectTypeId { get; init; }
    public string? Id { get; init; }
    public string? AttributeName { get; init; }

    /// <summary>The scalar value, when <see cref="HasValue"/>.</summary>
    /// <remarks>
    /// A <see cref="JsonElement"/> (boxed), which is exactly what
    /// <see cref="PersistentObjectAttribute.Value"/> already holds on a PO that came off the wire —
    /// both are <c>object?</c> filled by System.Text.Json. Applying the patch is therefore an
    /// assignment, not a conversion.
    /// </remarks>
    public object? Value { get; init; }

    /// <summary>The single nested row, when <see cref="HasObject"/> — an AsDetail attribute.</summary>
    public PersistentObject? Object { get; init; }

    /// <summary>The nested rows, when <see cref="HasObjects"/> — an AsDetail array attribute.</summary>
    public IReadOnlyList<PersistentObject>? Objects { get; init; }

    /// <summary>
    /// Whether the wire carried a <c>value</c> field at all.
    /// </summary>
    /// <remarks>
    /// ⚠️ Absent and <c>null</c> are different instructions: absent means "this field is not part of
    /// the patch", <c>null</c> means "clear it". The three payload fields are applied independently
    /// for that reason, which is why a tri-state flag exists per field instead of one nullable value.
    /// </remarks>
    public bool HasValue { get; init; }

    /// <inheritdoc cref="HasValue"/>
    public bool HasObject { get; init; }

    /// <summary>
    /// Whether the wire carried an <c>objects</c> field.
    /// </summary>
    /// <remarks>
    /// ⚠️ An <b>empty</b> array is a real instruction — "the grid is now empty" — and must be applied.
    /// Only its absence means "leave the rows alone".
    /// </remarks>
    public bool HasObjects { get; init; }
}

/// <summary>A query's rows are stale and should be re-executed. Surfaced only.</summary>
/// <remarks>
/// This SDK deliberately does not reproduce the frontend's <c>RefreshCoordinator</c>: it keeps no
/// registry of open queries, so there is nothing here to refresh. The caller re-executes if it cares.
/// </remarks>
public sealed class SparkRefreshQueryOperation : SparkClientOperation
{
    public string? QueryId { get; init; }
}

/// <summary>The user should be taken somewhere. Surfaced only — a headless client has no router.</summary>
public sealed class SparkNavigateOperation : SparkClientOperation
{
    public string? ObjectTypeId { get; init; }
    public string? Id { get; init; }
    public string? RouteName { get; init; }
}

/// <summary>
/// An action should be greyed out. Surfaced only.
/// </summary>
/// <remarks>
/// ⚠️ This is a no-op <b>in the browser too</b> — ng-spark logs a warning and moves on. It is modelled
/// here so that reading the operations of a response does not silently lose one, not because this SDK
/// is expected to act on it. Note also that <c>DisableActionsOn(po, "A", "B")</c> emits <b>two</b>
/// operations, one per action name.
/// </remarks>
public sealed class SparkDisableActionOperation : SparkClientOperation
{
    public string? ActionName { get; init; }

    /// <summary>The target discriminator — <c>persistentObject</c>, <c>query</c>, <c>currentResponse</c> or <c>session</c>.</summary>
    public string? TargetKind { get; init; }
}

/// <summary>
/// The server is asking a question — the <c>449</c> that <see cref="SparkClient"/> answers through a
/// <see cref="SparkRetryHandler"/>.
/// </summary>
/// <remarks>
/// It is modelled here, rather than parsed separately, so that one pass over the envelope yields both
/// the question and the operations that accompany it. Parsing twice would deserialize the prompt's
/// nested <see cref="PersistentObject"/> once per attempt for nothing.
/// </remarks>
public sealed class SparkRetryOperation : SparkClientOperation
{
    public required RetryActionPayload Prompt { get; init; }
}

/// <summary>
/// An operation type this client version does not model. Never thrown on, by design (FR10): the
/// payload is kept in <see cref="SparkClientOperation.Raw"/> so a newer server can be read by an
/// older client without the response failing.
/// </summary>
public sealed class SparkUnknownOperation : SparkClientOperation;

/// <summary>
/// Observes client operations as a call produces them, in emission order.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Per call, for the same reason <see cref="SparkRetryHandler"/> is.</b> Two conversations
/// running through one <see cref="SparkClient"/> would otherwise deliver each other's notifications
/// to whichever caller happened to be listening.
/// </para>
/// <para>
/// Called <b>before</b> the retry prompt on a <c>449</c> (FR11), so a hook that says something and
/// then asks a question is observed in that order rather than in reverse.
/// </para>
/// </remarks>
public delegate void SparkOperationHandler(SparkClientOperation operation);
