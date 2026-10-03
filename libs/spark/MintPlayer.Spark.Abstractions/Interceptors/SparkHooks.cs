using System.Security.Claims;

namespace MintPlayer.Spark.Abstractions.Interceptors;

/// <summary>
/// What every persistence hook is (#482): a DI-registered service that governs the entity types it
/// claims. A hook implements one or more phase interfaces — <see cref="IBeforeSave"/>,
/// <see cref="IAfterSave"/>, <see cref="IBeforeDelete"/>, <see cref="IAfterDelete"/>,
/// <see cref="IDeleteReplacement"/>, <see cref="IAfterMaterialize"/>, <see cref="IAfterLoad"/>,
/// <see cref="INaturalIdCollision"/>, and the durable <see cref="IAfterSaveCommitted"/> /
/// <see cref="IAfterDeleteCommitted"/> — and is registered once with <c>spark.AddHook&lt;T&gt;()</c>
/// (scoped), or found by the hook registration generator. Before #482 these were called
/// <em>interceptors</em> (<c>IPersistentObjectInterceptor</c>); the namespace keeps that name.
/// </summary>
/// <remarks>
/// <para>
/// <b>An Actions class may implement hooks for its own type</b> (<c>CarActions : …,
/// IBeforeDelete&lt;Car&gt;</c>). It needs no registration: the framework resolves the type's Actions
/// class once per request and runs it as a hook, after the registered hooks of the same stage.
/// </para>
/// <para>
/// <b>The framework owns persistence.</b> It loads, maps (<c>MapAsync</c> on the Actions class), runs
/// the hooks, checks the row (WITH CHECK), writes with the expected change vector and commits once.
/// A hook cannot skip any of that, and neither can an Actions class.
/// </para>
/// <para>
/// <b>Order:</b> there is no numeric order. Within a phase hooks run in registration order, which
/// is not a contract. Two structural rules replace it: <see cref="IDeleteReplacement"/> decides
/// before any <see cref="IBeforeDelete"/> runs, and <see cref="HookStage.Finalize"/> before-hooks
/// run after every <see cref="HookStage.Default"/> one. A refusal is safe in any order: everything a
/// hook wrote to the request session is taken back.
/// </para>
/// <para>
/// <b>Refusing:</b> throw. <c>SparkValidationException</c> is a 400, <c>SparkRowLevelAccessDeniedException</c>
/// a 404, <see cref="SparkCancelException"/> a silent no-op, and a <c>Retry.Action</c> prompt a 449
/// (refused in a bulk delete).
/// </para>
/// </remarks>
public interface ISparkHook
{
    /// <summary>
    /// Whether this hook governs <paramref name="entityType"/>. Must depend on the type alone; the
    /// answer is cached process-wide per (hook type, entity type).
    /// </summary>
    bool AppliesTo(Type entityType);

    /// <summary>
    /// Whether this hook also runs for <see cref="PersistentObjectOperation.Sync"/>, a write replicated
    /// from the owner module. False by default: the owner already ran its own hooks.
    /// </summary>
    bool HandlesSync => false;
}

/// <summary>A hook for <typeparamref name="T"/> and every type assignable to it.</summary>
public interface ISparkHook<T> : ISparkHook where T : class
{
    bool ISparkHook.AppliesTo(Type entityType) => typeof(T).IsAssignableFrom(entityType);
}

/// <summary>When a before-hook runs among the others.</summary>
public enum HookStage
{
    /// <summary>With every other hook that states no stage.</summary>
    Default,

    /// <summary>After every <see cref="Default"/> hook, so it sees the entity as they left it (History's "did anything change?").</summary>
    Finalize,
}

/// <summary>
/// Before a save is checked and written: after mapping, before WITH CHECK. May validate, mutate
/// <see cref="SaveContext.Entity"/> (what it stamps is what WITH CHECK judges and what is written),
/// store side documents in <see cref="SparkHookContext.Session"/> (committed with the save, taken back
/// on refusal), prompt with <c>Retry.Action</c> (be idempotent up to the prompt), or throw.
/// </summary>
public interface IBeforeSave : ISparkHook
{
    /// <summary>When this hook runs among the before-save hooks.</summary>
    HookStage Stage => HookStage.Default;

    ValueTask OnBeforeSaveAsync(SaveContext context);
}

/// <summary>The typed form of <see cref="IBeforeSave"/>.</summary>
public interface IBeforeSave<T> : IBeforeSave, ISparkHook<T> where T : class
{
    ValueTask OnBeforeSaveAsync(T entity, SaveContext context);

    ValueTask IBeforeSave.OnBeforeSaveAsync(SaveContext context) => OnBeforeSaveAsync((T)context.Entity, context);
}

/// <summary>
/// After a save was committed. Each after-hook is isolated: a failure is logged and never turns the
/// committed write into an error, nor skips another hook. For in-request follow-ups (a toast, a
/// shown-once secret); work that must eventually happen belongs in a durable hook.
/// </summary>
public interface IAfterSave : ISparkHook
{
    ValueTask OnAfterSaveAsync(SaveContext context);
}

/// <summary>The typed form of <see cref="IAfterSave"/>.</summary>
public interface IAfterSave<T> : IAfterSave, ISparkHook<T> where T : class
{
    ValueTask OnAfterSaveAsync(T entity, SaveContext context);

    ValueTask IAfterSave.OnAfterSaveAsync(SaveContext context) => OnAfterSaveAsync((T)context.Entity, context);
}

/// <summary>
/// Decides whether a delete is replaced by a save of the entity (SoftDelete), before any
/// <see cref="IBeforeDelete"/> runs, so every before-delete hook sees the final
/// <see cref="DeleteContext.IsReplaced"/>. At most one may govern a type.
/// </summary>
public interface IDeleteReplacement : ISparkHook
{
    /// <summary>
    /// Returns true to replace the hard delete with a save of <see cref="DeleteContext.Entity"/> as the
    /// hooks leave it; mutate the entity here (mark it deleted). Never called for a purge.
    /// </summary>
    ValueTask<bool> ReplaceAsync(DeleteContext context);
}

/// <summary>Before a delete (or its replacement) is written. Same powers as <see cref="IBeforeSave"/>.</summary>
public interface IBeforeDelete : ISparkHook
{
    /// <summary>When this hook runs among the before-delete hooks.</summary>
    HookStage Stage => HookStage.Default;

    ValueTask OnBeforeDeleteAsync(DeleteContext context);
}

/// <summary>The typed form of <see cref="IBeforeDelete"/>.</summary>
public interface IBeforeDelete<T> : IBeforeDelete, ISparkHook<T> where T : class
{
    ValueTask OnBeforeDeleteAsync(T entity, DeleteContext context);

    ValueTask IBeforeDelete.OnBeforeDeleteAsync(DeleteContext context) => OnBeforeDeleteAsync((T)context.Entity, context);
}

/// <summary>After a delete (or its replacement) was committed. Isolated like <see cref="IAfterSave"/>.</summary>
public interface IAfterDelete : ISparkHook
{
    ValueTask OnAfterDeleteAsync(DeleteContext context);
}

/// <summary>The typed form of <see cref="IAfterDelete"/>.</summary>
public interface IAfterDelete<T> : IAfterDelete, ISparkHook<T> where T : class
{
    ValueTask OnAfterDeleteAsync(T entity, DeleteContext context);

    ValueTask IAfterDelete.OnAfterDeleteAsync(DeleteContext context) => OnAfterDeleteAsync((T)context.Entity, context);
}

/// <summary>
/// After an entity was loaded from RavenDB and before anything reads it (contributions F1): fills
/// <em>satellite</em> properties — modelled, <c>[JsonIgnore]</c>d, stored in side documents — so the
/// mapper, the row gates, the save merge and <see cref="SaveContext.Before"/> all see the hydrated
/// entity. Called for every entity-backed load: the base <c>LoadManyAsync</c>, the save's reload, and
/// the side-session load of <see cref="SaveContext.Before"/>.
/// </summary>
/// <remarks>
/// <b>Idempotent by contract:</b> called at most once per entity <em>instance</em> per request. The
/// Update endpoint pre-reads in the session the save reloads from, so the save gets the tracked,
/// already-hydrated instance back and is not called again for it.
/// </remarks>
public interface IAfterMaterialize : ISparkHook
{
    ValueTask OnAfterMaterializeAsync(MaterializeContext context);
}

/// <summary>After an entity-backed persistent object was loaded through the row-gated read path; may decorate it.</summary>
public interface IAfterLoad : ISparkHook
{
    ValueTask OnAfterLoadAsync(LoadContext context);
}

/// <summary>
/// A creation of an <c>IHasNaturalId</c> type derived an id that an existing document already holds,
/// and the row gate refused the caller that document. The framework answers 404 unless a hook throws
/// its own exception to say why (a <c>SparkValidationException</c>: "a deleted row holds this key —
/// restore it instead").
/// </summary>
/// <remarks>
/// ⚠️ Whatever is thrown here tells the caller something about a row it may not see. Explain only to a
/// caller entitled to know; otherwise return and let the 404 stand.
/// </remarks>
public interface INaturalIdCollision : ISparkHook
{
    ValueTask OnNaturalIdCollisionAsync(NaturalIdCollisionContext context);
}

/// <summary>A refused natural-id collision (see <see cref="INaturalIdCollision"/>).</summary>
public sealed class NaturalIdCollisionContext : SparkHookContext
{
    /// <summary>The derived id, held by <see cref="Existing"/>.</summary>
    public required string Id { get; init; }

    /// <summary>The object the caller tried to create.</summary>
    public required PersistentObject PersistentObject { get; init; }

    /// <summary>The stored entity that holds the id, loaded from a side session. Do not modify it.</summary>
    public required object Existing { get; init; }
}

/// <summary>What kind of write is happening.</summary>
public enum PersistentObjectOperation
{
    /// <summary>An edit of an existing entity.</summary>
    Save,

    /// <summary>A creation.</summary>
    New,

    /// <summary>A delete requested by a caller.</summary>
    Delete,

    /// <summary>A revert to an earlier revision (History).</summary>
    Revert,

    /// <summary>A restore of a soft-deleted entity (SoftDelete).</summary>
    Restore,

    /// <summary>A permanent delete that must not be replaced (SoftDelete purge, GDPR).</summary>
    Purge,

    /// <summary>A write replicated from the owner module.</summary>
    Sync,
}

/// <summary>What every hook context carries.</summary>
public abstract class SparkHookContext
{
    /// <summary>The CLR entity type.</summary>
    public required Type EntityType { get; init; }

    /// <summary>The caller, when there is an HTTP request.</summary>
    public ClaimsPrincipal? User { get; init; }

    /// <summary>Whether the caller is the system (module sync, background work) rather than a viewer.</summary>
    public bool IsSystemContext { get; init; }

    /// <summary>
    /// The RavenDB <c>IAsyncDocumentSession</c> the write commits through (for a materialize, the
    /// session the entity was loaded in). Documents a before-hook stores here commit with the write and
    /// are taken back when it is refused. Typed <see cref="object"/> because Abstractions carries no
    /// RavenDB dependency; <c>GetSession()</c> in <c>MintPlayer.Spark</c> returns it typed.
    /// </summary>
    public required object Session { get; init; }

    /// <summary>
    /// Small facts a before-hook records for the durable after-commit hooks (#482, D17): they get a
    /// payload, never the live entity, so what they need to know about the entity is copied here — a
    /// delete reason, the attributes an edit changed, the author of a deleted post. Captured after the
    /// last before-hook and stored with the write. Keep it small, and never put a secret in it: it
    /// lands in an outbox document.
    /// </summary>
    public IDictionary<string, string> Facts { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>A save through <c>IDatabaseAccess.SavePersistentObjectAsync</c>.</summary>
public sealed class SaveContext : SparkHookContext
{
    /// <summary><see cref="PersistentObjectOperation.Save"/>, <see cref="PersistentObjectOperation.New"/>, or the explicit kind the caller passed (Revert, Restore, Sync).</summary>
    public required PersistentObjectOperation Operation { get; init; }

    /// <summary>
    /// The object as the client submitted it, minus every attribute the caller may not write
    /// (contributions M2c-2b): those were dropped before any hook runs, so an absent attribute keeps
    /// its stored value (its default on a create).
    /// </summary>
    public required PersistentObject PersistentObject { get; init; }

    /// <summary>
    /// The attributes a static attribute right kept out of this save, as paths — <c>Title</c>, or
    /// <c>Lines.Text</c> for an AsDetail row attribute. Empty in system context. Per-row protection is
    /// deliberately not listed. History uses this to report a partial revert.
    /// </summary>
    public IReadOnlyList<string> UnwritableAttributes { get; init; } = [];

    /// <summary>The stored entity before this save, loaded from a separate session (so it is not the instance being saved). Null for a creation.</summary>
    public object? Before { get; init; }

    /// <summary>
    /// The entity being saved: the tracked instance in a before-hook (mutations are written), the
    /// saved instance in an after-hook. A hook mutates it; it cannot swap it for another instance,
    /// since the instance WITH CHECK judges is the one written.
    /// </summary>
    public required object Entity { get; init; }

    /// <summary>
    /// The document id. Null in a before-hook of a creation: the id (a natural id included) is
    /// assigned when the entity is stored, after the before-hooks. Always set in an after-hook.
    /// </summary>
    public string? Id { get; internal set; }

    /// <summary>Whether this save creates the entity.</summary>
    public bool IsNew => Operation == PersistentObjectOperation.New;
}

/// <summary>A delete through <c>IDatabaseAccess.DeletePersistentObjectAsync</c> or a bulk delete.</summary>
public sealed class DeleteContext : SparkHookContext
{
    /// <summary><see cref="PersistentObjectOperation.Delete"/>, <see cref="PersistentObjectOperation.Purge"/> or <see cref="PersistentObjectOperation.Sync"/>.</summary>
    public required PersistentObjectOperation Operation { get; init; }

    /// <summary>The document id.</summary>
    public required string Id { get; init; }

    /// <summary>The tracked entity about to be deleted. Mutations made for a replacement are saved.</summary>
    public required object Entity { get; init; }

    /// <summary>The reason the caller gave for this delete, if any — one for a whole bulk delete (#467, D20).</summary>
    public string? Reason { get; init; }

    /// <summary>Whether this delete must be permanent. Never replaced.</summary>
    public bool IsPurge => Operation == PersistentObjectOperation.Purge;

    /// <summary>
    /// Whether an <see cref="IDeleteReplacement"/> replaced the delete with a save of
    /// <see cref="Entity"/>. Decided before any <see cref="IBeforeDelete"/> runs, so it is final there.
    /// </summary>
    public bool IsReplaced { get; internal set; }
}

/// <summary>Why an entity was materialized (see <see cref="IAfterMaterialize"/>).</summary>
public enum MaterializeReason
{
    /// <summary>The row-gated read path (<c>LoadManyAsync</c>): a page, a parent, a batch.</summary>
    Load,

    /// <summary>The save's reload, before the posted values are merged.</summary>
    SaveReload,

    /// <summary>The side-session load that becomes <see cref="SaveContext.Before"/>.</summary>
    Before,
}

/// <summary>An entity just loaded from RavenDB (see <see cref="IAfterMaterialize"/>).</summary>
public sealed class MaterializeContext : SparkHookContext
{
    /// <summary>The loaded entity; fill its satellite properties here.</summary>
    public required object Entity { get; init; }

    /// <summary>Which load this is.</summary>
    public required MaterializeReason Reason { get; init; }
}

/// <summary>A load through <c>IDatabaseAccess.GetPersistentObjectAsync</c> / <c>GetPersistentObjectsByIdAsync</c>.</summary>
public sealed class LoadContext : SparkHookContext
{
    /// <summary>The object as it will be returned; hooks may decorate it.</summary>
    public required PersistentObject PersistentObject { get; init; }
}
