namespace MintPlayer.Spark.Abstractions.Interceptors;

/// <summary>
/// Diverts a save before anything is written (commerce X7): instead of the save, the replacement records
/// something of its own (a document proposal waiting for approval) and answers with a
/// <see cref="SaveReplacementReceipt"/>. The caller gets a first-class "submitted for approval" (HTTP 202), never an
/// error. At most one may govern a type, like <see cref="IDeleteReplacement"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>When it is asked:</b> after every gate the caller must pass — the type right, the row gate, the
/// collection guard, the etag check, the disabled-action gate, the attribute write shield and the validation —
/// and before anything is mapped, loaded into the request session or intercepted. So no
/// <see cref="IBeforeSave"/> or Actions hook runs for a diverted save; they run once, when the change is applied
/// (<see cref="ISparkReplacedSaves.ApplyAsync"/>), as the caller who applies it.
/// </para>
/// <para>
/// <b>Never when applying:</b> a save made by <see cref="ISparkReplacedSaves.ApplyAsync"/> does not ask the
/// replacement again, so an approved proposal cannot propose itself. Not asked for a
/// <see cref="PersistentObjectOperation.Sync"/> unless <see cref="ISparkInterceptor.HandlesSync"/>.
/// </para>
/// <para>
/// <b>Diverting is not refusing.</b> Return a receipt to divert, <see langword="null"/> to let the save happen.
/// Throw only to refuse, exactly as a before-interceptor would: a thrown exception takes back what the
/// replacement stored in <see cref="SparkInterceptorContext.Session"/>.
/// </para>
/// </remarks>
public interface ISaveReplacement : ISparkInterceptor
{
    /// <summary>
    /// Returns a receipt to divert the save, <see langword="null"/> to save normally. What the replacement
    /// stores in <see cref="SparkInterceptorContext.Session"/> commits with the request (or with the
    /// <c>ISparkWriteUnit</c> batch it runs in); the object itself is not written.
    /// </summary>
    ValueTask<SaveReplacementReceipt?> ReplaceAsync(SaveReplacementContext context);
}

/// <summary>A save an <see cref="ISaveReplacement"/> may divert (see there).</summary>
public sealed class SaveReplacementContext : SparkInterceptorContext
{
    /// <summary><see cref="PersistentObjectOperation.New"/>, <see cref="PersistentObjectOperation.Save"/>, or the explicit kind the caller passed.</summary>
    public required PersistentObjectOperation Operation { get; init; }

    /// <summary>
    /// The object as it would be saved: the caller's values minus every attribute they may not write (the
    /// write shield already ran). Read-only by contract; keep <see cref="PersistentObjectJson"/>, not this.
    /// </summary>
    public required PersistentObject PersistentObject { get; init; }

    /// <summary>
    /// <see cref="PersistentObject"/> as wire JSON text — the form <see cref="ISparkReplacedSaves.ApplyAsync"/>
    /// takes back. Store it as a string. ⚠️ Never store the <see cref="Abstractions.PersistentObject"/> itself in a
    /// RavenDB document: the store serializes with Newtonsoft, which ignores the System.Text.Json converter that
    /// tells an AsDetail attribute from a plain one, so nested rows would come back as plain attributes without
    /// their objects (S10).
    /// </summary>
    public required string PersistentObjectJson { get; init; }

    /// <summary>
    /// The version the change was made against: the stored row's change vector, which the etag check has just
    /// verified the caller's etag equals. The apply must find it unchanged (else a 409). Null for a creation.
    /// </summary>
    public string? BaseEtag { get; init; }

    /// <summary>The document id; null for a creation.</summary>
    public string? Id { get; init; }

    /// <summary>The stored entity, loaded from a separate session (do not modify it). Null for a creation.</summary>
    public object? Before { get; init; }

    /// <summary>The attributes a static right kept out of this save, as paths (see <see cref="SaveContext.UnwritableAttributes"/>).</summary>
    public IReadOnlyList<string> UnwritableAttributes { get; init; } = [];

    /// <summary>Whether this save creates the entity.</summary>
    public bool IsNew => Id is null;
}

/// <summary>
/// What an <see cref="ISaveReplacement"/> recorded instead of the save: answered to the caller as
/// "submitted" (HTTP 202 <c>{ submitted: { kind, referenceId } }</c>).
/// </summary>
/// <param name="Kind">What the save became, for the client to word it: <c>approval</c> for a document proposal.</param>
/// <param name="ReferenceId">The id of what was recorded (the proposal), so the client can link to it.</param>
public sealed record SaveReplacementReceipt(string Kind, string ReferenceId);

/// <summary>
/// Applies a save an <see cref="ISaveReplacement"/> diverted (commerce X7): the approver's half of a document
/// proposal. Implemented by the framework.
/// </summary>
public interface ISparkReplacedSaves
{
    /// <summary>
    /// Saves <see cref="ReplacedSave.PersistentObjectJson"/> as the current caller (the approver), through the
    /// same path an HTTP create or update takes — rights, row gates, write shield, validation, interceptors,
    /// WITH CHECK — with <c>Etag = </c><see cref="ReplacedSave.BaseEtag"/>, so a row changed since the proposal
    /// is a <see cref="ReplacedSaveOutcomeKind.Conflict"/>. <see cref="ReplacedSave.Alongside"/> runs in the same
    /// commit. The replacement is not asked again.
    /// </summary>
    /// <remarks>
    /// Refusals the caller may see as such stay exceptions, as on the save path:
    /// <c>SparkAccessDeniedException</c> (the approver may not edit the row: answer the uniform 404),
    /// <see cref="SparkValidationException"/>, <see cref="SparkThrottledException"/>, and an action the row's
    /// Actions class disabled.
    /// </remarks>
    Task<ReplacedSaveOutcome> ApplyAsync(ReplacedSave save, CancellationToken cancellationToken = default);
}

/// <summary>A diverted save to apply (see <see cref="ISparkReplacedSaves.ApplyAsync"/>).</summary>
public sealed record ReplacedSave
{
    /// <summary>The persistent-object type the save was made for.</summary>
    public required Guid ObjectTypeId { get; init; }

    /// <summary><see cref="SaveReplacementContext.PersistentObjectJson"/>, exactly as it was stored.</summary>
    public required string PersistentObjectJson { get; init; }

    /// <summary><see cref="SaveReplacementContext.BaseEtag"/>; null for a creation.</summary>
    public string? BaseEtag { get; init; }

    /// <summary>
    /// Who made the change (the proposer's user id): handed to the interceptors as
    /// <see cref="SaveContext.OnBehalfOf"/> and to the durable ones as the <see cref="SparkFacts.OnBehalfOf"/> fact.
    /// The write itself is the approver's.
    /// </summary>
    public string? OnBehalfOf { get; init; }

    /// <summary>
    /// Writes of the caller's own (the proposal's state change) that must commit with the apply or not at all. Gets
    /// the RavenDB <c>IAsyncDocumentSession</c> the apply commits through; store, do not save.
    /// </summary>
    public Func<object, Task>? Alongside { get; init; }
}

/// <summary>How an apply ended (see <see cref="ReplacedSaveOutcome"/>).</summary>
public enum ReplacedSaveOutcomeKind
{
    /// <summary>Saved, with <see cref="ReplacedSave.Alongside"/>, in one commit.</summary>
    Applied,

    /// <summary>An interceptor cancelled the save. Nothing was written, <see cref="ReplacedSave.Alongside"/> included.</summary>
    Cancelled,

    /// <summary>
    /// The row changed or went since the proposal (or, for a creation, its natural id is taken): a 409, and the
    /// proposal is stale. <see cref="ReplacedSaveOutcome.ConflictReason"/> says which. Nothing was written.
    /// </summary>
    Conflict,

    /// <summary>
    /// The caller may not write attributes the proposal sets (<see cref="ReplacedSaveOutcome.Attributes"/>). Saving
    /// would silently drop them, so the apply is refused instead (MCP's "same result as proposed"). Nothing was written.
    /// </summary>
    Unwritable,

    /// <summary>The save's validation refused it (<see cref="ReplacedSaveOutcome.Errors"/>). Nothing was written.</summary>
    Invalid,
}

/// <summary>The result of <see cref="ISparkReplacedSaves.ApplyAsync"/>.</summary>
public sealed record ReplacedSaveOutcome
{
    public required ReplacedSaveOutcomeKind Kind { get; init; }

    /// <summary>For <see cref="ReplacedSaveOutcomeKind.Applied"/>: the saved row, as a load presents it to the caller.</summary>
    public PersistentObject? Object { get; init; }

    /// <summary>For <see cref="ReplacedSaveOutcomeKind.Conflict"/>: <c>changed</c>, <c>deleted</c> or <c>exists</c>.</summary>
    public string? ConflictReason { get; init; }

    /// <summary>For <see cref="ReplacedSaveOutcomeKind.Unwritable"/>: the attribute paths (<c>Title</c>, <c>Lines.Text</c>) the caller may not write.</summary>
    public IReadOnlyList<string> Attributes { get; init; } = [];

    /// <summary>For <see cref="ReplacedSaveOutcomeKind.Invalid"/>: the validation errors.</summary>
    public IReadOnlyList<ValidationError> Errors { get; init; } = [];
}
